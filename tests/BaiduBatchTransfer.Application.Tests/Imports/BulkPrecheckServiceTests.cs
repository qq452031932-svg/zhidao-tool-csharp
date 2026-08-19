using BaiduBatchTransfer.Application.Imports;
using BaiduBatchTransfer.Application.Tests.Jobs;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Tests.Imports;

public sealed class BulkPrecheckServiceTests
{
    [Fact]
    public async Task EditingLinkOrCodeInvalidatesPersistedPrecheck()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var importId = Guid.NewGuid();
        var row = BatchTestData.ReadyRow(importId, 1) with
        {
            PrecheckStatus = "Succeeded",
            PrecheckedAt = BatchTestData.Now,
        };
        await db.SeedAsync([], BatchTestData.ReadySession(importId, 1, 1), [row]);
        var service = new InvalidatePrecheckService(db.UnitOfWorkFactory);

        await service.UpdateLinkOrExtractionCodeAsync(
            row.Id,
            "https://pan.baidu.com/s/1changed",
            "z9y8");

        await using var unit = await db.UnitOfWorkFactory.BeginAsync();
        var persisted = await unit.Imports.GetRowByIdAsync(row.Id);
        Assert.Equal("https://pan.baidu.com/s/1changed", persisted!.ShareUrl);
        Assert.Equal("1changed", persisted.NormalizedShareId);
        Assert.Equal("z9y8", persisted.ExtractionCode);
        Assert.Null(persisted.PrecheckStatus);
        Assert.Null(persisted.PrecheckErrorCode);
        Assert.Null(persisted.PrecheckedAt);
        Assert.Equal("Draft", (await unit.Imports.GetSessionAsync(importId))!.Status);
    }

    [Fact]
    public async Task EditingIsRejectedWhileImportParsingIsStillWritingRows()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var importId = Guid.NewGuid();
        var row = BatchTestData.ReadyRow(importId, 1) with
        {
            PrecheckStatus = "Succeeded",
            PrecheckedAt = BatchTestData.Now,
        };
        await db.SeedAsync(
            [],
            BatchTestData.ReadySession(importId, 1, 1) with { Status = "Parsing" },
            [row]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new InvalidatePrecheckService(db.UnitOfWorkFactory).UpdateLinkOrExtractionCodeAsync(
                row.Id,
                "https://pan.baidu.com/s/changed",
                "z9y8"));

        await using var unit = await db.UnitOfWorkFactory.BeginAsync();
        Assert.Equal("Succeeded", (await unit.Imports.GetRowByIdAsync(row.Id))!.PrecheckStatus);
    }

    [Fact]
    public async Task PrecheckInvalidationCannotBypassDuplicateClassificationOrDraftCounters()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var importId = Guid.NewGuid();
        var first = BatchTestData.ReadyRow(importId, 1, normalizedId: "same");
        var edited = BatchTestData.ReadyRow(importId, 2, normalizedId: "other") with
        {
            PrecheckStatus = "Succeeded",
            PrecheckedAt = BatchTestData.Now,
        };
        await db.SeedAsync([], BatchTestData.ReadySession(importId, 2, 2), [first, edited]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new InvalidatePrecheckService(db.UnitOfWorkFactory).UpdateLinkOrExtractionCodeAsync(
                edited.Id,
                "https://pan.baidu.com/s/same",
                "z9y8"));

        await using var unit = await db.UnitOfWorkFactory.BeginAsync();
        var persisted = await unit.Imports.GetRowByIdAsync(edited.Id);
        var session = await unit.Imports.GetSessionAsync(importId);
        Assert.Equal("other", persisted!.NormalizedShareId);
        Assert.Equal("Succeeded", persisted.PrecheckStatus);
        Assert.Equal(2, session!.ReadyRows);
        Assert.Equal(0, session.InvalidRows);
    }

    [Fact]
    public async Task ConcurrentRunsAreSingleFlightAndDoNotRepeatBaiduRequests()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var importId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var account = BatchTestData.Account(accountId, "single-flight");
        await db.SeedAsync(
            [account],
            BatchTestData.ReadySession(importId, 4, 4) with { Status = "Draft" },
            Enumerable.Range(1, 4).Select(row => BatchTestData.ReadyRow(importId, row)));
        var adapter = new InspectingAdapter(
            share => BaiduAdapterResult<ShareMetadata>.Success(Metadata(int.Parse(share.StableShareId[6..]))),
            delayMilliseconds: 20);
        var store = new StubSessionStore(Session(accountId, account.BaiduUserId));
        var firstRun = new BulkPrecheckService(db.UnitOfWorkFactory, store, adapter)
            .RunAsync(new(importId, accountId));
        await adapter.FirstInspection;
        var secondRun = new BulkPrecheckService(db.UnitOfWorkFactory, store, adapter)
            .RunAsync(new(importId, accountId));

        var results = await Task.WhenAll(firstRun, secondRun);

        Assert.Equal(4, adapter.CallCount);
        Assert.Equal(1, adapter.MaximumConcurrency);
        Assert.Equal(4, results.Sum(result => result.ProcessedRows));
        Assert.All(results, result => Assert.Equal(BulkPrecheckRunStatus.Completed, result.Status));
        Assert.False(BulkPrecheckService.HasActiveRunGate(importId));
    }

    [Fact]
    public async Task SelectedAccountPersistsProgressWithoutChangingTransferDailyCount()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var importId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var account = BatchTestData.Account(accountId, "precheck") with { DailyExecutedCount = 17 };
        var session = BatchTestData.ReadySession(importId, 2, 2) with { Status = "Draft" };
        var rows = new[]
        {
            BatchTestData.ReadyRow(importId, 1),
            BatchTestData.ReadyRow(importId, 2),
        };
        await db.SeedAsync([account], session, rows);
        var store = new StubSessionStore(Session(accountId, account.BaiduUserId));
        var adapter = new InspectingAdapter(_ => BaiduAdapterResult<ShareMetadata>.Success(Metadata(1)));
        var service = new BulkPrecheckService(db.UnitOfWorkFactory, store, adapter);

        var result = await service.RunAsync(new BulkPrecheckRequest(importId, accountId));

        Assert.Equal(BulkPrecheckRunStatus.Completed, result.Status);
        Assert.Equal(2, result.ProcessedRows);
        Assert.Equal(1, adapter.MaximumConcurrency);
        Assert.All(adapter.AccountIds, id => Assert.Equal(accountId, id));
        await using var unit = await db.UnitOfWorkFactory.BeginAsync();
        var persistedSession = await unit.Imports.GetSessionAsync(importId);
        var persistedRows = await unit.Imports.GetRowsPageAsync(new(importId, null, 1, 10));
        var persistedAccount = await unit.Accounts.GetByIdAsync(accountId);
        Assert.Equal("ReadyToConfirm", persistedSession!.Status);
        Assert.Equal(accountId, persistedSession.PrecheckAccountId);
        Assert.All(persistedRows.Items, row => Assert.Equal("Succeeded", row.PrecheckStatus));
        Assert.Equal(17, persistedAccount!.DailyExecutedCount);
    }

    [Fact]
    public async Task LoginFailurePausesAndResumeCannotSilentlySwitchAccount()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var importId = Guid.NewGuid();
        var selected = Guid.NewGuid();
        var other = Guid.NewGuid();
        var selectedAccount = BatchTestData.Account(selected, "selected");
        await db.SeedAsync(
            [selectedAccount, BatchTestData.Account(other, "other")],
            BatchTestData.ReadySession(importId, 2, 2) with { Status = "Draft" },
            [BatchTestData.ReadyRow(importId, 1), BatchTestData.ReadyRow(importId, 2)]);
        var call = 0;
        var adapter = new InspectingAdapter(_ => ++call == 1
            ? BaiduAdapterResult<ShareMetadata>.Success(Metadata(1))
            : BaiduAdapterResult<ShareMetadata>.Failure(BaiduErrorKind.ExpiredLogin, "secret diagnostic"));
        var service = new BulkPrecheckService(
            db.UnitOfWorkFactory,
            new StubSessionStore(Session(selected, selectedAccount.BaiduUserId)),
            adapter);

        var paused = await service.RunAsync(new BulkPrecheckRequest(importId, selected));

        Assert.Equal(BulkPrecheckRunStatus.PausedForLogin, paused.Status);
        Assert.Equal(BaiduErrorKind.ExpiredLogin, paused.PauseReason);
        Assert.DoesNotContain("secret", paused.ToString(), StringComparison.OrdinalIgnoreCase);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RunAsync(new BulkPrecheckRequest(importId, other)));
        await using var unit = await db.UnitOfWorkFactory.BeginAsync();
        var page = await unit.Imports.GetRowsPageAsync(new(importId, null, 1, 10));
        Assert.Equal("Succeeded", page.Items[0].PrecheckStatus);
        Assert.Null(page.Items[1].PrecheckStatus);
        Assert.Equal("Prechecking", (await unit.Imports.GetSessionAsync(importId))!.Status);
    }

    [Fact]
    public async Task EditingACompletedPrecheckDoesNotPermitMixedAccountResults()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var importId = Guid.NewGuid();
        var selected = Guid.NewGuid();
        var other = Guid.NewGuid();
        var row = BatchTestData.ReadyRow(importId, 1) with
        {
            PrecheckStatus = "Succeeded",
            PrecheckedAt = BatchTestData.Now,
        };
        await db.SeedAsync(
            [BatchTestData.Account(selected, "selected"), BatchTestData.Account(other, "other")],
            BatchTestData.ReadySession(importId, 1, 1) with { PrecheckAccountId = selected },
            [row]);
        await new InvalidatePrecheckService(db.UnitOfWorkFactory).UpdateLinkOrExtractionCodeAsync(
            row.Id,
            "https://pan.baidu.com/s/changed",
            "z9y8");
        var otherAccount = BatchTestData.Account(other, "other");
        var service = new BulkPrecheckService(
            db.UnitOfWorkFactory,
            new StubSessionStore(Session(other, otherAccount.BaiduUserId)),
            new InspectingAdapter(_ => BaiduAdapterResult<ShareMetadata>.Success(Metadata(1))));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync(new(importId, other)));

        await using var unit = await db.UnitOfWorkFactory.BeginAsync();
        Assert.Equal(selected, (await unit.Imports.GetSessionAsync(importId))!.PrecheckAccountId);
        Assert.Null((await unit.Imports.GetRowByIdAsync(row.Id))!.PrecheckStatus);
    }

    [Fact]
    public async Task VerificationFailurePausesWithoutPersistingAnUnsafeDiagnostic()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var importId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var account = BatchTestData.Account(accountId, "verify");
        await db.SeedAsync(
            [account],
            BatchTestData.ReadySession(importId, 1, 1) with { Status = "Draft" },
            [BatchTestData.ReadyRow(importId, 1)]);
        var service = new BulkPrecheckService(
            db.UnitOfWorkFactory,
            new StubSessionStore(Session(accountId, account.BaiduUserId)),
            new InspectingAdapter(_ => BaiduAdapterResult<ShareMetadata>.Failure(
                BaiduErrorKind.VerificationRequired,
                "cookie=must-not-escape")));

        var result = await service.RunAsync(new(importId, accountId));

        Assert.Equal(BulkPrecheckRunStatus.PausedForVerification, result.Status);
        Assert.Equal(BaiduErrorKind.VerificationRequired, result.PauseReason);
        Assert.DoesNotContain("cookie", result.ToString(), StringComparison.OrdinalIgnoreCase);
        await using var unit = await db.UnitOfWorkFactory.BeginAsync();
        Assert.Null((await unit.Imports.GetRowByIdAsync((await unit.Imports.GetRowsPageAsync(new(importId, null, 1, 1))).Items[0].Id))!.PrecheckStatus);
    }

    [Fact]
    public async Task CancellationKeepsCommittedRowsAndResumeContinuesFromPersistedProgress()
    {
        await using var db = await BatchTestDatabase.CreateAsync();
        var importId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var account = BatchTestData.Account(accountId, "resume");
        await db.SeedAsync(
            [account],
            BatchTestData.ReadySession(importId, 2, 2) with { Status = "Draft" },
            [BatchTestData.ReadyRow(importId, 1), BatchTestData.ReadyRow(importId, 2)]);
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        var first = new BulkPrecheckService(
            db.UnitOfWorkFactory,
            new StubSessionStore(Session(accountId, account.BaiduUserId)),
            new InspectingAdapter(_ =>
            {
                calls++;
                if (calls == 2) cancellation.Cancel();
                return BaiduAdapterResult<ShareMetadata>.Success(Metadata(calls));
            }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => first.RunAsync(new(importId, accountId), cancellation.Token));
        await using (var unit = await db.UnitOfWorkFactory.BeginAsync())
        {
            var page = await unit.Imports.GetRowsPageAsync(new(importId, null, 1, 10));
            Assert.Equal("Succeeded", page.Items[0].PrecheckStatus);
            Assert.Null(page.Items[1].PrecheckStatus);
        }

        var resumed = await new BulkPrecheckService(
            db.UnitOfWorkFactory,
            new StubSessionStore(Session(accountId, account.BaiduUserId)),
            new InspectingAdapter(_ => BaiduAdapterResult<ShareMetadata>.Success(Metadata(2))))
            .RunAsync(new(importId, accountId));

        Assert.Equal(BulkPrecheckRunStatus.Completed, resumed.Status);
        Assert.Equal(1, resumed.ProcessedRows);
    }

    private static AccountSession Session(Guid accountId, string baiduUserId) =>
        new(accountId, baiduUserId, [new SessionCookie("BDUSS", "secret", ".baidu.com", "/", null, true, true)], DateTimeOffset.UtcNow);

    private static ShareMetadata Metadata(int sourceRow) =>
        new(new ShareReference($"share-{sourceRow}", $"https://pan.baidu.com/s/{sourceRow}"), "folder", 1, 1, "fingerprint");

    private sealed class StubSessionStore(AccountSession? session) : IBaiduSessionStore
    {
        public Task<AccountSession?> LoadAsync(string accountId, CancellationToken cancellationToken) => Task.FromResult(session);
        public Task RemoveAsync(string accountId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SaveAsync(AccountSession value, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class InspectingAdapter(
        Func<ShareReference, BaiduAdapterResult<ShareMetadata>> inspect,
        int delayMilliseconds = 0) : IBaiduNetdiskAdapter
    {
        private int concurrency;
        public int MaximumConcurrency { get; private set; }
        public int CallCount { get; private set; }
        public List<Guid?> AccountIds { get; } = [];
        public Task FirstInspection => firstInspection.Task;
        private readonly TaskCompletionSource firstInspection = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<BaiduAdapterResult<ShareMetadata>> InspectShareAsync(AccountSession session, ShareReference share, CancellationToken cancellationToken)
        {
            AccountIds.Add(session.LocalAccountId);
            CallCount++;
            MaximumConcurrency = Math.Max(MaximumConcurrency, Interlocked.Increment(ref concurrency));
            firstInspection.TrySetResult();
            try
            {
                if (delayMilliseconds > 0) await Task.Delay(delayMilliseconds, cancellationToken);
                return inspect(share);
            }
            finally { Interlocked.Decrement(ref concurrency); }
        }

        public Task<BaiduAdapterResult<BaiduAccountIdentity>> ValidateSessionAsync(AccountSession session, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<AccountCapacity>> GetCapacityAsync(AccountSession session, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<System.Collections.Immutable.ImmutableArray<RemoteDirectory>>> ListDirectoriesAsync(AccountSession session, RemotePath path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<RemoteDirectory>> EnsureDirectoryAsync(AccountSession session, RemotePath path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<TransferReceipt>> SaveWholeShareAsync(AccountSession session, ShareReference share, RemoteDirectory destination, DuplicatePolicy duplicatePolicy, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<TransferConfirmation>> ConfirmTransferAsync(AccountSession session, TransferConfirmationQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
