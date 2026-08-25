using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Tests.Scheduling;

public sealed class ShareReconciliationTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExactlyOneNewCompatibleShareIsAccepted()
    {
        var harness = Harness([Page(Old()), Page(New())], maximumPolls: 2);

        var result = await harness.Service.ReconcileAsync(harness.Operation.Id);

        Assert.Equal(ShareReconciliationDisposition.Accepted, result.Disposition);
        Assert.NotNull(harness.Store.Accepted);
        Assert.Equal("分享成功", result.StableStatus);
    }

    [Fact]
    public async Task OperationBoundRecoveryUsesExactConfirmAndNeverFallsBackToLegacyListing()
    {
        var exact = new RemoteItemIdentitySet([new("remote-1", "display")]);
        var operation = new ShareOperationRecord(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1,
            "ResultPending", exact.Fingerprint,
            ShareSettingsFingerprint.Compute(true, true, ShareExtractionCodeBehavior.Random, null),
            1, "{}", "request", "Unknown", 1, null, Now.AddMinutes(-1), Now,
            null, null, Now.AddMinutes(-2), Now, 4);
        var settings = new BatchShareSettingsRecord(
            operation.BatchJobId, true, true, "Random", null, "Regenerate", Now,
            operation.SettingsFingerprint);
        var store = new RecordingStore(new(operation, settings, true, true, exact, ExistingShareDecision.Regenerate));
        var adapter = new BoundReconciliationAdapter();
        var service = new ShareOperationReconciliationService(
            adapter, new MemorySessionStore(operation.AccountId), store, new RecordingDelay(),
            new ShareReconciliationOptions(1, 10, TimeSpan.Zero, TimeSpan.FromMinutes(1)),
            new FrozenTimeProvider(Now));

        var result = await service.ReconcileAsync(operation.Id);

        Assert.Equal(ShareReconciliationDisposition.Accepted, result.Disposition);
        Assert.Equal(1, adapter.ConfirmCalls);
        Assert.Equal(0, adapter.ListCalls);
    }

    [Fact]
    public async Task OperationBoundExplicitRejectionFailsSafelyInsteadOfRemainingPending()
    {
        var exact = new RemoteItemIdentitySet([new("remote-1", "display")]);
        var operation = new ShareOperationRecord(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1,
            "ResultPending", exact.Fingerprint,
            ShareSettingsFingerprint.Compute(true, true, ShareExtractionCodeBehavior.Random, null),
            3, "{}", "request", "Unknown", 2, null, Now.AddMinutes(-1), Now,
            null, null, Now.AddMinutes(-2), Now, 7);
        var settings = new BatchShareSettingsRecord(
            operation.BatchJobId, true, true, "Random", null, "Regenerate", Now,
            operation.SettingsFingerprint);
        var store = new RecordingStore(new(operation, settings, true, true, exact, ExistingShareDecision.Regenerate));
        var adapter = new BoundReconciliationAdapter(SubmissionAcceptance.DefinitelyNotAccepted);
        var service = new ShareOperationReconciliationService(
            adapter, new MemorySessionStore(operation.AccountId), store, new RecordingDelay(),
            new ShareReconciliationOptions(1, 10, TimeSpan.Zero, TimeSpan.FromMinutes(1)),
            new FrozenTimeProvider(Now));

        var result = await service.ReconcileAsync(operation.Id);

        Assert.Equal(ShareReconciliationDisposition.DefinitelyNotAccepted, result.Disposition);
        Assert.True(store.FailedDefinitelyNotAccepted);
        Assert.Null(store.PendingIssue);
        Assert.Equal(1, adapter.ConfirmCalls);
        Assert.Equal(0, adapter.ListCalls);
    }

    [Fact]
    public async Task ZeroMatchesAtBoundedTimeoutRemainsPendingWithoutCreate()
    {
        var harness = Harness([Page(Old()), Page(Old())], maximumPolls: 2);

        var result = await harness.Service.ReconcileAsync(harness.Operation.Id);

        Assert.Equal(ShareReconciliationDisposition.ResultPending, result.Disposition);
        Assert.Null(harness.Store.Accepted);
        Assert.Equal(ShareReconciliationIssue.ReconcileTimeout, harness.Store.PendingIssue);
        Assert.Equal(0, harness.Adapter.CreateCalls);
    }

    [Fact]
    public async Task MultipleNewCompatibleSharesRemainAmbiguous()
    {
        var harness = Harness([Page(New("new-1"), New("new-2"))]);

        var result = await harness.Service.ReconcileAsync(harness.Operation.Id);

        Assert.Equal(ShareReconciliationDisposition.Ambiguous, result.Disposition);
        Assert.Null(harness.Store.Accepted);
        Assert.Equal(ShareReconciliationIssue.MultipleCompatibleShares, harness.Store.PendingIssue);
    }

    [Fact]
    public async Task LaterPageAndPollCanRevealTheUniqueMatch()
    {
        var harness = Harness([
            Page(Old()),
            Page(Old(), next: "page-2"), Page(),
            Page(Old(), next: "page-2"), Page(New())
        ], maximumPolls: 3, maximumPages: 2);

        var result = await harness.Service.ReconcileAsync(harness.Operation.Id);

        Assert.Equal(ShareReconciliationDisposition.Accepted, result.Disposition);
        Assert.Equal(5, harness.Adapter.ListCalls);
        Assert.Equal(2, harness.Delay.Delays.Count);
    }

    [Fact]
    public async Task ReconciliationNeverLeaksShareMaterialInTypedResult()
    {
        var harness = Harness([Page(New())]);

        var result = await harness.Service.ReconcileAsync(harness.Operation.Id);

        var text = result.ToString();
        Assert.DoesNotContain("https://", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("new-share", text, StringComparison.Ordinal);
        Assert.DoesNotContain("r4nd", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(BaiduErrorKind.ExpiredLogin, ShareReconciliationIssue.AccountSessionUnavailable, "等待账号登录")]
    [InlineData(BaiduErrorKind.VerificationRequired, ShareReconciliationIssue.AccountVerificationRequired, "账号需要安全验证")]
    public async Task ListingAccountFailureRequiresUserAndKeepsUnknownResultPending(
        BaiduErrorKind errorKind,
        ShareReconciliationIssue expectedIssue,
        string expectedStatus)
    {
        var harness = Harness([], listFailure: errorKind);

        var result = await harness.Service.ReconcileAsync(harness.Operation.Id);

        Assert.Equal(ShareReconciliationDisposition.RequiresUser, result.Disposition);
        Assert.Equal(expectedStatus, result.StableStatus);
        Assert.Equal(expectedIssue, harness.Store.PendingIssue);
        Assert.Null(harness.Store.Accepted);
        Assert.Equal(0, harness.Adapter.CreateCalls);
    }

    [Fact]
    public async Task DifferentAccountsCanEnterShareListingConcurrently()
    {
        var first = Context(Guid.NewGuid(), Guid.NewGuid());
        var second = Context(Guid.NewGuid(), Guid.NewGuid());
        var store = new ConcurrentReconciliationStore([first, second]);
        var adapter = new ConcurrentEntryAdapter(New());
        var service = new ShareOperationReconciliationService(
            adapter,
            new AnyAccountSessionStore(),
            store,
            new RecordingDelay(),
            new ShareReconciliationOptions(1, 10, TimeSpan.Zero, TimeSpan.FromMinutes(1)),
            new FrozenTimeProvider(Now));

        var results = await Task.WhenAll(
            service.ReconcileAsync(first.Operation.Id),
            service.ReconcileAsync(second.Operation.Id));

        Assert.All(results, result => Assert.Equal(ShareReconciliationDisposition.Accepted, result.Disposition));
        Assert.Equal(2, adapter.MaximumConcurrentListings);
        Assert.Equal(2, store.AcceptedOperationCount);
    }

    private static HarnessState Harness(
        IEnumerable<BaiduSharePage> pages,
        int maximumPolls = 1,
        int maximumPages = 10,
        BaiduErrorKind? listFailure = null)
    {
        var exact = new RemoteItemIdentitySet([new("remote-1", "display")]);
        var operation = new ShareOperationRecord(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1,
            "ResultPending", exact.Fingerprint,
            ShareSettingsFingerprint.Compute(true, true, ShareExtractionCodeBehavior.Random, null),
            1, "{}", "request", "Unknown", 1, null, Now.AddMinutes(-1), Now,
            null, null, Now.AddMinutes(-2), Now, 4);
        var settings = new BatchShareSettingsRecord(
            operation.BatchJobId, true, true, "Random", null, "Regenerate", Now,
            operation.SettingsFingerprint);
        var store = new RecordingStore(new(
            operation, settings, true, true, exact, ExistingShareDecision.Regenerate));
        store.Baseline.Add(new(operation.Id, "old-share", 1));
        var adapter = new RecordingAdapter(pages, listFailure);
        var delay = new RecordingDelay();
        var service = new ShareOperationReconciliationService(
            adapter, new MemorySessionStore(operation.AccountId), store, delay,
            new ShareReconciliationOptions(maximumPolls, maximumPages, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1)),
            new FrozenTimeProvider(Now));
        return new(service, operation, store, adapter, delay);
    }

    private static BaiduSharePage Page(params BaiduShareRecord[] items) => new(items, null);
    private static BaiduSharePage Page(BaiduShareRecord item, string next) => new([item], next);
    private static BaiduShareRecord Old() => Share("old-share");
    private static BaiduShareRecord New(string id = "new-share") => Share(id);
    private static BaiduShareRecord Share(string id) => new(
        id, new([new RemoteItemIdentity("remote-1", "display")]),
        $"https://pan.baidu.com/s/{id}", "r4nd", true, true);

    private static ShareExecutionContext Context(Guid operationId, Guid accountId)
    {
        var exact = new RemoteItemIdentitySet([new("remote-1", "display")]);
        var operation = new ShareOperationRecord(
            operationId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), accountId, 1,
            "ResultPending", exact.Fingerprint,
            ShareSettingsFingerprint.Compute(true, true, ShareExtractionCodeBehavior.Random, null),
            1, "{}", "request", "Unknown", 1, null, Now.AddMinutes(-1), Now,
            null, null, Now.AddMinutes(-2), Now, 4);
        var settings = new BatchShareSettingsRecord(
            operation.BatchJobId, true, true, "Random", null, "Regenerate", Now,
            operation.SettingsFingerprint);
        return new(operation, settings, true, true, exact, ExistingShareDecision.Regenerate);
    }

    private sealed record HarnessState(
        ShareOperationReconciliationService Service,
        ShareOperationRecord Operation,
        RecordingStore Store,
        RecordingAdapter Adapter,
        RecordingDelay Delay);

    private sealed class RecordingStore(ShareExecutionContext context) : IShareReconciliationStore
    {
        public List<ShareDiscoveryBaselineRecord> Baseline { get; } = [];
        public BaiduShareResultRecord? Accepted { get; private set; }
        public ShareReconciliationIssue? PendingIssue { get; private set; }
        public bool FailedDefinitelyNotAccepted { get; private set; }

        public Task<ShareExecutionContext?> LoadPendingAsync(Guid operationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ShareExecutionContext?>(context);

        public Task<StringKeysetPage<ShareDiscoveryBaselineRecord>> GetBaselinePageAsync(Guid operationId, string? afterShareId, int pageSize, CancellationToken cancellationToken = default)
        {
            var values = Baseline.OrderBy(x => x.ShareId, StringComparer.Ordinal)
                .Where(x => afterShareId is null || string.CompareOrdinal(x.ShareId, afterShareId) > 0)
                .Take(pageSize + 1).ToList();
            var more = values.Count > pageSize;
            if (more) values.RemoveAt(values.Count - 1);
            return Task.FromResult(new StringKeysetPage<ShareDiscoveryBaselineRecord>(values, more ? values[^1].ShareId : null));
        }

        public Task<bool> TryAcceptAsync(Guid operationId, int generation, long leaseGeneration, ShareReconciliationWriteSnapshot expected, BaiduShareResultRecord result, DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
        {
            Accepted = result;
            return Task.FromResult(true);
        }

        public Task<bool> TryKeepPendingAsync(Guid operationId, int generation, long leaseGeneration, ShareReconciliationWriteSnapshot expected, ShareReconciliationIssue issue, DateTimeOffset? observedSessionCapturedAtUtc, DateTimeOffset nextReconcileAt, DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
        {
            PendingIssue = issue;
            return Task.FromResult(true);
        }

        public Task<bool> TryFailDefinitelyNotAcceptedAsync(Guid operationId, int generation, long leaseGeneration, ShareReconciliationFailureSnapshot expected, DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
        {
            FailedDefinitelyNotAccepted = true;
            return Task.FromResult(true);
        }
    }

    private sealed class RecordingAdapter(
        IEnumerable<BaiduSharePage> pages,
        BaiduErrorKind? listFailure = null) : IBaiduShareAdapter
    {
        private readonly Queue<BaiduSharePage> pages = new(pages);
        public int ListCalls { get; private set; }
        public int CreateCalls { get; private set; }

        public Task<BaiduAdapterResult<BaiduSharePage>> ListSharesAsync(AccountSession session, string? cursor, CancellationToken cancellationToken)
        {
            ListCalls++;
            return Task.FromResult(listFailure is { } error
                ? BaiduAdapterResult<BaiduSharePage>.Failure(error, "private diagnostic")
                : BaiduAdapterResult<BaiduSharePage>.Success(pages.Dequeue()));
        }

        public Task<BaiduAdapterResult<ShareCreationReceipt>> CreatePermanentShareAsync(AccountSession session, PermanentShareRequest request, CancellationToken cancellationToken)
        {
            CreateCalls++;
            throw new InvalidOperationException("Reconciliation must not create a share.");
        }
    }

    private sealed class BoundReconciliationAdapter(
        SubmissionAcceptance acceptance = SubmissionAcceptance.Unknown) : IOperationBoundBaiduShareAdapter
    {
        public int ConfirmCalls { get; private set; }
        public int ListCalls { get; private set; }
        public Task<BaiduAdapterResult<BaiduSharePage>> ListSharesAsync(AccountSession session, string? cursor, CancellationToken cancellationToken)
        { ListCalls++; throw new InvalidOperationException("Bound recovery must not use legacy listing."); }
        public Task<BaiduAdapterResult<ShareCreationReceipt>> CreatePermanentShareAsync(AccountSession session, PermanentShareRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<SharePreparationReceipt>> PreparePermanentShareForOperationAsync(ShareOperationAutomationContext context, AccountSession session, PermanentShareRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<ShareCreationReceipt>> SubmitPermanentShareForOperationAsync(ShareOperationAutomationContext context, AccountSession session, PermanentShareRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<ShareCreationReceipt>> ConfirmExactShareForOperationAsync(ShareOperationAutomationContext context, AccountSession session, PermanentShareRequest request, CancellationToken cancellationToken)
        {
            ConfirmCalls++;
            if (acceptance == SubmissionAcceptance.DefinitelyNotAccepted)
                return Task.FromResult(BaiduAdapterResult<ShareCreationReceipt>.Failure(
                    BaiduErrorKind.UnknownResponse, adapterDiagnostic: null,
                    submissionAcceptance: acceptance));
            return Task.FromResult(BaiduAdapterResult<ShareCreationReceipt>.Success(new(
                Now, "adapter", request, "new-share", "https://pan.baidu.com/s/new-share",
                new EffectiveShareSettings(true, true, ShareExtractionCodeBehavior.Random, "r4nd"))));
        }
    }

    private sealed class ConcurrentEntryAdapter(BaiduShareRecord share) : IBaiduShareAdapter
    {
        private readonly TaskCompletionSource bothEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int concurrentListings;
        public int MaximumConcurrentListings { get; private set; }

        public async Task<BaiduAdapterResult<BaiduSharePage>> ListSharesAsync(
            AccountSession session,
            string? cursor,
            CancellationToken cancellationToken)
        {
            var concurrent = Interlocked.Increment(ref concurrentListings);
            MaximumConcurrentListings = Math.Max(MaximumConcurrentListings, concurrent);
            if (concurrent == 2) bothEntered.TrySetResult();
            try
            {
                await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken);
                return BaiduAdapterResult<BaiduSharePage>.Success(new([share], null));
            }
            finally
            {
                Interlocked.Decrement(ref concurrentListings);
            }
        }

        public Task<BaiduAdapterResult<ShareCreationReceipt>> CreatePermanentShareAsync(
            AccountSession session,
            PermanentShareRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Reconciliation must not create a share.");
    }

    private sealed class ConcurrentReconciliationStore(IEnumerable<ShareExecutionContext> contexts)
        : IShareReconciliationStore
    {
        private readonly IReadOnlyDictionary<Guid, ShareExecutionContext> contexts =
            contexts.ToDictionary(context => context.Operation.Id);
        private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> accepted = new();
        public int AcceptedOperationCount => accepted.Count;

        public Task<ShareExecutionContext?> LoadPendingAsync(
            Guid operationId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(contexts.GetValueOrDefault(operationId));

        public Task<StringKeysetPage<ShareDiscoveryBaselineRecord>> GetBaselinePageAsync(
            Guid operationId,
            string? afterShareId,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new StringKeysetPage<ShareDiscoveryBaselineRecord>([], null));

        public Task<bool> TryAcceptAsync(
            Guid operationId,
            int generation,
            long leaseGeneration,
            ShareReconciliationWriteSnapshot expected,
            BaiduShareResultRecord result,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(accepted.TryAdd(operationId, 0));

        public Task<bool> TryKeepPendingAsync(
            Guid operationId,
            int generation,
            long leaseGeneration,
            ShareReconciliationWriteSnapshot expected,
            ShareReconciliationIssue issue,
            DateTimeOffset? observedSessionCapturedAtUtc,
            DateTimeOffset nextReconcileAt,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> TryFailDefinitelyNotAcceptedAsync(
            Guid operationId,
            int generation,
            long leaseGeneration,
            ShareReconciliationFailureSnapshot expected,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class MemorySessionStore(Guid accountId) : IBaiduSessionStore
    {
        private readonly AccountSession session = new(accountId, "baidu-user", [
            new SessionCookie("BDUSS", "secret", ".baidu.com", "/", null, true, true)], Now);
        public Task<AccountSession?> LoadAsync(string accountId, CancellationToken cancellationToken) => Task.FromResult<AccountSession?>(session);
        public Task SaveAsync(AccountSession session, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RemoveAsync(string accountId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class AnyAccountSessionStore : IBaiduSessionStore
    {
        public Task<AccountSession?> LoadAsync(string accountId, CancellationToken cancellationToken) =>
            Task.FromResult<AccountSession?>(new(
                Guid.Parse(accountId),
                "baidu-user",
                [new SessionCookie("BDUSS", "secret", ".baidu.com", "/", null, true, true)],
                Now));

        public Task SaveAsync(AccountSession session, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RemoveAsync(string accountId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingDelay : IAsyncDelay
    {
        public List<TimeSpan> Delays { get; } = [];
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Delays.Add(delay);
            return Task.CompletedTask;
        }
    }

    private sealed class FrozenTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
