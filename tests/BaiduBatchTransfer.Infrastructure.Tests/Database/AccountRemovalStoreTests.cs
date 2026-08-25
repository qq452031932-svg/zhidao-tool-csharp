using BaiduBatchTransfer.Application.Accounts;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Infrastructure.Database;
using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Text;

namespace BaiduBatchTransfer.Infrastructure.Tests.Database;

public sealed class AccountRemovalStoreTests : IDisposable
{
    private static readonly Guid A = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid B = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid C = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid Tombstone = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    private static readonly DateTimeOffset RemovedAt = new(2026, 8, 20, 12, 34, 56, TimeSpan.Zero);
    private readonly string root = Path.Combine(Path.GetTempPath(), $"bbt-account-removal-{Guid.NewGuid():N}");

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public async Task AccountPagesRejectUnsafeSizes(int pageSize)
    {
        var (factory, store, _) = await CreateAsync();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.GetAccountsPageAsync(1, pageSize, default));
        Assert.Equal(4, await ScalarAsync(factory, "SELECT COUNT(*) FROM Accounts;"));
    }

    [Fact]
    public async Task AccountPagesOmitTombstonesAndProjectSafeFields()
    {
        var (_, store, _) = await CreateAsync();
        var page = await store.GetAccountsPageAsync(1, 2, default);
        Assert.Equal(3, page.TotalCount);
        Assert.Equal(new[] { "Alpha", "Bravo" }, page.Items.Select(x => x.DisplayName));
        Assert.All(page.Items, x =>
        {
            Assert.DoesNotContain("baidu-", x.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("cookie", x.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(root, x.ToString(), StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task PreparationPersistsOpaqueMarkerAndOnlyThatAccountIsListedAsCleanupPending()
    {
        var (factory, store, _) = await CreateAsync();
        var preview = await store.PreviewAsync(B, AccountExitHistoryMode.KeepHistory, default);

        var prepared = await store.PrepareRemovalAsync(
            new(B, AccountExitHistoryMode.KeepHistory, preview.ConfirmationToken, RemovedAt),
            default);

        Assert.Equal(AccountRemovalPreparationStatus.Prepared, prepared.Status);
        var generation = await ScalarTextAsync(factory,
            "SELECT RemovalGeneration FROM Accounts WHERE Id=$id;", ("$id", Id(B)));
        Assert.Matches("^[0-9A-F]{64}$", generation);
        Assert.Equal("CleanupPending", await ScalarTextAsync(factory,
            "SELECT RemovalState FROM Accounts WHERE Id=$id;", ("$id", Id(B))));
        var page = await store.GetAccountsPageAsync(1, 100, default);
        Assert.True(Assert.Single(page.Items, item => item.AccountId == B).IsCleanupPending);
        Assert.All(page.Items.Where(item => item.AccountId != B), item => Assert.False(item.IsCleanupPending));
        Assert.DoesNotContain(generation, page.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenericDisabledCredentialFreeRowCannotEnterExternalCleanup()
    {
        var (factory, store, _) = await CreateAsync();
        await ExecuteAsync(factory,
            "UPDATE Accounts SET Status='Disabled',IsEnabled=0,EncryptedSession=NULL,LastValidatedAt=NULL WHERE Id=$id;",
            ("$id", Id(B)));
        var cleanupCount = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CompletePreparedRemovalAsync(
            B,
            RemovedAt,
            (_, _, _) =>
            {
                cleanupCount++;
                return Task.CompletedTask;
            },
            default));

        Assert.Equal(0, cleanupCount);
        Assert.Equal("None", await ScalarTextAsync(factory,
            "SELECT RemovalState FROM Accounts WHERE Id=$id;", ("$id", Id(B))));
        Assert.False((await ReadAccountAsync(factory, B)).Deleted);
    }

    [Fact]
    public async Task RepositoryUpsertCannotReenableAnAccountWithPendingRemovalMarker()
    {
        var (factory, store, _) = await CreateAsync();
        var preview = await store.PreviewAsync(B, AccountExitHistoryMode.KeepHistory, default);
        await store.PrepareRemovalAsync(
            new(B, AccountExitHistoryMode.KeepHistory, preview.ConfirmationToken, RemovedAt),
            default);
        await using var unit = await SqliteUnitOfWork.BeginAsync(factory);
        var pending = Assert.IsType<AccountRecord>(await unit.Accounts.GetByIdAsync(B));

        await Assert.ThrowsAsync<SqliteException>(() => unit.Accounts.UpsertAsync(pending with
        {
            Status = "Active",
            IsEnabled = true,
            EncryptedSession = [1, 2, 3],
            LastValidatedAt = RemovedAt,
        }));

        Assert.Equal("CleanupPending", await ScalarTextAsync(factory,
            "SELECT RemovalState FROM Accounts WHERE Id=$id;", ("$id", Id(B))));
    }

    [Fact]
    public async Task RepositorySeededDashedIdAndBlobSessionSupportsPagePreviewAndPrepare()
    {
        var (factory, store, _) = await CreateAsync();
        var accountId = Guid.NewGuid();
        var account = AccountRecord.Create(accountId, "repository-user", "Repository Account") with
        {
            EncryptedSession = [0, 1, 2, 127, 128, 255],
            WebView2ProfileDirectory = Path.Combine(root, "profiles", "repository"),
            LastValidatedAt = RemovedAt
        };
        await using (var unit = await SqliteUnitOfWork.BeginAsync(factory))
        {
            await unit.Accounts.UpsertAsync(account);
            await unit.CommitAsync();
        }

        var page = await store.GetAccountsPageAsync(1, 10, default);
        Assert.Contains(page.Items, item => item.AccountId == accountId);
        Assert.Equal(accountId.ToString("D"), await ScalarTextAsync(factory, "SELECT Id FROM Accounts WHERE BaiduUserId='repository-user';"));
        var preview = await store.PreviewAsync(accountId, AccountExitHistoryMode.KeepHistory, default);
        var result = await store.PrepareRemovalAsync(new(accountId, AccountExitHistoryMode.KeepHistory, preview.ConfirmationToken, RemovedAt), default);

        Assert.Equal(AccountExitStatus.Ready, preview.Status);
        Assert.Equal(AccountRemovalPreparationStatus.Prepared, result.Status);
        var state = await ReadAccountAsync(factory, accountId);
        Assert.Equal("Disabled", state.Status);
        Assert.Null(state.SessionHex);
    }

    [Fact]
    public async Task SessionBlobByteChangeMakesTokenStaleWithoutDisclosure()
    {
        var (factory, store, _) = await CreateAsync(seedGraph: true);
        var preview = await store.PreviewAsync(B, AccountExitHistoryMode.KeepHistory, default);
        await ExecuteAsync(factory, "UPDATE Accounts SET EncryptedSession=$session WHERE Id=$id;",
            ("$session", new byte[] { 0, 255, 1, 254, 2 }), ("$id", Id(B)));

        var result = await store.PrepareRemovalAsync(new(B, AccountExitHistoryMode.KeepHistory, preview.ConfirmationToken, RemovedAt), default);

        Assert.Equal(AccountRemovalPreparationStatus.StaleConfirmation, result.Status);
        Assert.DoesNotContain("00FF01FE02", result.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Waiting")]
    [InlineData("Reserved")]
    [InlineData("Preparing")]
    [InlineData("Submitting")]
    [InlineData("ResultPending")]
    public async Task NonterminalOperationsBlockPreviewAndPrepare(string status)
    {
        var (factory, store, graph) = await CreateAsync(seedGraph: true);
        var ready = await store.PreviewAsync(B, AccountExitHistoryMode.KeepHistory, default);
        var resource = Guid.NewGuid();
        await InsertResourceAsync(factory, resource, graph.BBatch, 9, B, "Reserved", "https://active.invalid");
        await InsertOperationAsync(factory, Guid.NewGuid(), resource, graph.BBatch, B, 3, status);
        var preview = await store.PreviewAsync(B, AccountExitHistoryMode.KeepHistory, default);
        var result = await store.PrepareRemovalAsync(new(B, AccountExitHistoryMode.KeepHistory, ready.ConfirmationToken, RemovedAt), default);
        Assert.Equal(AccountExitStatus.ActiveWorkBlocked, preview.Status);
        Assert.Equal(AccountRemovalPreparationStatus.ActiveWorkBlocked, result.Status);
        await AssertUnchangedAsync(factory);
    }

    [Theory]
    [InlineData("Queued")]
    [InlineData("Active")]
    [InlineData("Paused")]
    [InlineData("AwaitingCandidates")]
    [InlineData("PausedReady")]
    public async Task NonterminalAllocationsBlockPreviewAndPrepareWithoutOperations(string status)
    {
        var (factory, store, _) = await CreateAsync(seedGraph: true);
        var ready = await store.PreviewAsync(B, AccountExitHistoryMode.KeepHistory, default);
        var batch = Guid.NewGuid();
        await InsertBatchAsync(factory, batch, status, null, 0, 0);
        await InsertAllocationAsync(factory, Guid.NewGuid(), batch, B, "/new");
        var preview = await store.PreviewAsync(B, AccountExitHistoryMode.KeepHistory, default);
        var result = await store.PrepareRemovalAsync(new(B, AccountExitHistoryMode.KeepHistory, ready.ConfirmationToken, RemovedAt), default);
        Assert.Equal(AccountExitStatus.ActiveWorkBlocked, preview.Status);
        Assert.Equal(AccountRemovalPreparationStatus.ActiveWorkBlocked, result.Status);
        await AssertUnchangedAsync(factory);
    }

    [Theory]
    [InlineData(AccountExitHistoryMode.KeepHistory)]
    [InlineData(AccountExitHistoryMode.RemoveTerminalHistory)]
    public async Task TokenCannotApplyToAnotherLocalAccount(AccountExitHistoryMode mode)
    {
        var (factory, store, _) = await CreateAsync(seedGraph: true);
        var token = (await store.PreviewAsync(A, mode, default)).ConfirmationToken;
        var before = await ReadAllPersistentDataAsync(factory);
        var result = await store.PrepareRemovalAsync(new(B, mode, token, RemovedAt), default);
        Assert.Equal(AccountRemovalPreparationStatus.StaleConfirmation, result.Status);
        Assert.Equal(before, await ReadAllPersistentDataAsync(factory));
    }

    public static IEnumerable<object[]> ManifestMutations()
    {
        yield return ["account", "UPDATE Accounts SET Status='Invalid' WHERE Id=$account;"];
        yield return ["operation status", "UPDATE TransferOperations SET Status='Failed',OutcomeKind='Failed' WHERE Id=$operation;"];
        yield return ["operation field", "UPDATE TransferOperations SET AttemptCount=AttemptCount+1,UpdatedAt='2026-08-20T15:00:00Z' WHERE Id=$operation;"];
        yield return ["allocation", "UPDATE AccountAllocations SET FailedItemCount=FailedItemCount+1 WHERE Id=$allocation;"];
        yield return ["allocation path", "UPDATE AccountAllocations SET DefaultTargetPath='/changed',NormalizedDefaultTargetPath='/changed' WHERE Id=$allocation;"];
        yield return ["resource ownership", "UPDATE ResourceItems SET CandidateConstraintAccountId=$account WHERE Id=$public;"];
        yield return ["resource fields", "UPDATE ResourceItems SET GlobalValidityStatus='Changed',UpdatedAt='2026-08-20T15:00:00Z' WHERE Id=$public;"];
        yield return ["constrained no-operation resource", "UPDATE ResourceItems SET Note='changed' WHERE Id=$constrained;"];
        yield return ["B-only public resource", "UPDATE ResourceItems SET ShareUrl='https://changed.invalid' WHERE Id=$onlyPublic;"];
        yield return ["batch", "UPDATE BatchJobs SET Name='changed',NextAllocationOrdinal=NextAllocationOrdinal+1 WHERE Id=$batch;"];
        yield return ["import links", "UPDATE ImportSessions SET AppendToBatchJobId=NULL,UpdatedAt='2026-08-20T16:00:00Z' WHERE Id=$import;"];
    }

    [Theory]
    [MemberData(nameof(ManifestMutations))]
    public async Task AnyRelevantManifestChangeMakesTokenStaleAndWritesNothing(string _, string sql)
    {
        var (factory, store, g) = await CreateAsync(seedGraph: true);
        var token = (await store.PreviewAsync(B, AccountExitHistoryMode.KeepHistory, default)).ConfirmationToken;
        await ExecuteAsync(factory, sql, ("$account", Id(B)), ("$operation", Id(g.BOperation)),
            ("$allocation", Id(g.BAllocation)), ("$public", Id(g.SharedPublic)), ("$constrained", Id(g.Constrained)),
            ("$onlyPublic", Id(g.BOnlyPublic)), ("$batch", Id(g.SharedBatch)), ("$import", Id(g.Import)));
        var before = await ReadAccountAsync(factory, B);
        var result = await store.PrepareRemovalAsync(new(B, AccountExitHistoryMode.KeepHistory, token, RemovedAt), default);
        Assert.Equal(AccountRemovalPreparationStatus.StaleConfirmation, result.Status);
        Assert.Equal(before, await ReadAccountAsync(factory, B));
    }

    [Fact]
    public async Task CrossBatchResourceReferencedByTargetOperationIsInManifestClosure()
    {
        var (factory, store, g) = await CreateAsync(seedGraph: true);
        var ownerBatch = Guid.NewGuid();
        var crossBatchResource = Guid.NewGuid();
        await InsertBatchAsync(factory, ownerBatch, "Completed", null, 1, 0);
        await InsertResourceAsync(factory, crossBatchResource, ownerBatch, 1, null, "Expanded", "https://cross-batch.invalid");
        await ExecuteAsync(factory, "UPDATE TransferOperations SET ResourceItemId=$resource WHERE Id=$operation;",
            ("$resource", Id(crossBatchResource)), ("$operation", Id(g.BOperation)));
        var preview = await store.PreviewAsync(B, AccountExitHistoryMode.KeepHistory, default);
        await ExecuteAsync(factory, "UPDATE ResourceItems SET Note='changed after preview' WHERE Id=$resource;", ("$resource", Id(crossBatchResource)));

        var result = await store.PrepareRemovalAsync(new(B, AccountExitHistoryMode.KeepHistory, preview.ConfirmationToken, RemovedAt), default);

        Assert.Equal(AccountRemovalPreparationStatus.StaleConfirmation, result.Status);
        await AssertUnchangedAsync(factory);
    }

    [Theory]
    [InlineData("UPDATE BatchJobs SET Name='changed sibling' WHERE Id=$sibling;")]
    [InlineData("UPDATE BatchJobs SET ImportSessionId=NULL WHERE Id=$sibling;")]
    public async Task ImportReverseLinkClosureIncludesSiblingBatchesRegardlessOfPrecheckAccount(string mutation)
    {
        var (factory, store, g) = await CreateAsync(seedGraph: true);
        var bridgeImport = Guid.NewGuid();
        var bridgeBatch = Guid.NewGuid();
        var linkedImport = Guid.NewGuid();
        var sibling = Guid.NewGuid();
        await ExecuteAsync(factory, """
            INSERT INTO ImportSessions(Id,SourceType,SourceName,Status,TotalRows,ReadyRows,InvalidRows,DuplicateRows,ExcludedRows,
              PrecheckAccountId,CreatedBatchJobId,AppendToBatchJobId,CreatedAt,UpdatedAt)
            VALUES($id,'Text','sibling source','BatchCreated',0,0,0,0,0,$other,$relevant,NULL,$at,$at);
            """, ("$id", Id(bridgeImport)), ("$other", Id(A)), ("$relevant", Id(g.BBatch)), ("$at", "2026-08-20T08:00:00Z"));
        await InsertBatchAsync(factory, bridgeBatch, "Completed", bridgeImport, 0, 0);
        await ExecuteAsync(factory, """
            INSERT INTO ImportSessions(Id,SourceType,SourceName,Status,TotalRows,ReadyRows,InvalidRows,DuplicateRows,ExcludedRows,
              PrecheckAccountId,CreatedBatchJobId,AppendToBatchJobId,CreatedAt,UpdatedAt)
            VALUES($id,'Text','nested sibling source','BatchCreated',0,0,0,0,0,$other,$relevant,NULL,$at,$at);
            """, ("$id", Id(linkedImport)), ("$other", Id(A)), ("$relevant", Id(bridgeBatch)), ("$at", "2026-08-20T08:30:00Z"));
        await InsertBatchAsync(factory, sibling, "Completed", linkedImport, 0, 0);
        var preview = await store.PreviewAsync(B, AccountExitHistoryMode.KeepHistory, default);
        await ExecuteAsync(factory, mutation, ("$sibling", Id(sibling)));

        var result = await store.PrepareRemovalAsync(new(B, AccountExitHistoryMode.KeepHistory, preview.ConfirmationToken, RemovedAt), default);

        Assert.Equal(AccountRemovalPreparationStatus.StaleConfirmation, result.Status);
        await AssertUnchangedAsync(factory);
    }

    [Fact]
    public async Task OperationChangingToReferenceManifestedResourceMakesTokenStale()
    {
        var (factory, store, g) = await CreateAsync(seedGraph: true);
        var contextBatch = Guid.NewGuid();
        var originalResource = Guid.NewGuid();
        var contextOperation = Guid.NewGuid();
        await InsertBatchAsync(factory, contextBatch, "Completed", null, 1, 1);
        await InsertResourceAsync(factory, originalResource, contextBatch, 1, null, "Expanded", "https://context.invalid");
        await InsertOperationAsync(factory, contextOperation, originalResource, contextBatch, A, 1, "Succeeded");
        var preview = await store.PreviewAsync(B, AccountExitHistoryMode.KeepHistory, default);
        await ExecuteAsync(factory, "UPDATE TransferOperations SET ResourceItemId=$resource WHERE Id=$operation;",
            ("$resource", Id(g.BResource)), ("$operation", Id(contextOperation)));

        var result = await store.PrepareRemovalAsync(new(B, AccountExitHistoryMode.KeepHistory, preview.ConfirmationToken, RemovedAt), default);

        Assert.Equal(AccountRemovalPreparationStatus.StaleConfirmation, result.Status);
        await AssertUnchangedAsync(factory);
    }

    [Fact]
    public async Task SurvivingOperationReferencingManifestedResourceHasAllFieldsBound()
    {
        var (factory, store, g) = await CreateAsync(seedGraph: true);
        var contextBatch = Guid.NewGuid();
        var contextOperation = Guid.NewGuid();
        await InsertBatchAsync(factory, contextBatch, "Completed", null, 0, 1);
        await InsertOperationAsync(factory, contextOperation, g.BResource, contextBatch, A, 1, "Succeeded");
        var preview = await store.PreviewAsync(B, AccountExitHistoryMode.KeepHistory, default);
        Assert.Equal(3, preview.TerminalBatchCount);
        await ExecuteAsync(factory, "UPDATE TransferOperations SET Status='Failed',OutcomeKind='Failed',AttemptCount=AttemptCount+1 WHERE Id=$operation;",
            ("$operation", Id(contextOperation)));

        var result = await store.PrepareRemovalAsync(new(B, AccountExitHistoryMode.KeepHistory, preview.ConfirmationToken, RemovedAt), default);

        Assert.Equal(AccountRemovalPreparationStatus.StaleConfirmation, result.Status);
        await AssertUnchangedAsync(factory);
    }

    [Fact]
    public async Task PreviewCountsFullTerminalGraphAndRedactsManifestSecrets()
    {
        var (_, store, _) = await CreateAsync(seedGraph: true);
        var preview = await store.PreviewAsync(B, AccountExitHistoryMode.RemoveTerminalHistory, default);
        Assert.Equal(AccountExitStatus.Ready, preview.Status);
        Assert.Equal(2, preview.TerminalOperationCount);
        Assert.Equal(3, preview.TerminalBatchCount);
        Assert.Equal(2, preview.SharedBatchCount);
        Assert.Equal(64, preview.ConfirmationToken.Length);
        Assert.DoesNotContain(Id(B), preview.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(preview.ConfirmationToken, preview.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task KeepHistoryAtomicallyDisablesOnlyBAndClearsCredentials()
    {
        var (factory, store, _) = await CreateAsync(seedGraph: true);
        var beforeA = await ReadRowsAsync(factory, "SELECT * FROM Accounts WHERE Id=$id;", ("$id", Id(A)));
        var beforeC = await ReadRowsAsync(factory, "SELECT * FROM Accounts WHERE Id=$id;", ("$id", Id(C)));
        var beforeADaily = await ScalarTextAsync(factory,
            "SELECT DailyExecutedCount||','||DailyCountDateKey FROM Accounts WHERE Id=$id;", ("$id", Id(A)));
        var beforeCDaily = await ScalarTextAsync(factory,
            "SELECT DailyExecutedCount||','||DailyCountDateKey FROM Accounts WHERE Id=$id;", ("$id", Id(C)));
        var beforeOtherOperations = await ReadRowsAsync(factory,
            "SELECT * FROM TransferOperations WHERE AssignedAccountId IN ($a,$c) ORDER BY Id;", ("$a", Id(A)), ("$c", Id(C)));
        var beforeHistory = await ReadHistoryDataAsync(factory);
        var preview = await store.PreviewAsync(B, AccountExitHistoryMode.KeepHistory, default);
        var result = await store.PrepareRemovalAsync(new(B, AccountExitHistoryMode.KeepHistory, preview.ConfirmationToken, RemovedAt), default);
        Assert.Equal(AccountRemovalPreparationStatus.Prepared, result.Status);
        Assert.True(result.DatabasePrepared);
        Assert.Equal(beforeA, await ReadRowsAsync(factory, "SELECT * FROM Accounts WHERE Id=$id;", ("$id", Id(A))));
        Assert.Equal(beforeC, await ReadRowsAsync(factory, "SELECT * FROM Accounts WHERE Id=$id;", ("$id", Id(C))));
        Assert.Equal(beforeADaily, await ScalarTextAsync(factory,
            "SELECT DailyExecutedCount||','||DailyCountDateKey FROM Accounts WHERE Id=$id;", ("$id", Id(A))));
        Assert.Equal(beforeCDaily, await ScalarTextAsync(factory,
            "SELECT DailyExecutedCount||','||DailyCountDateKey FROM Accounts WHERE Id=$id;", ("$id", Id(C))));
        Assert.Equal(beforeOtherOperations, await ReadRowsAsync(factory,
            "SELECT * FROM TransferOperations WHERE AssignedAccountId IN ($a,$c) ORDER BY Id;", ("$a", Id(A)), ("$c", Id(C))));
        Assert.Equal(beforeHistory, await ReadHistoryDataAsync(factory));
        var b = await ReadAccountAsync(factory, B);
        Assert.Equal("Disabled", b.Status);
        Assert.False(b.Enabled);
        Assert.Null(b.SessionHex);
        Assert.Null(b.ValidatedAt);
        Assert.Equal(Path.Combine(root, "profiles", "bravo"), b.Profile);
        Assert.Equal(2, await ScalarAsync(factory, "SELECT COUNT(*) FROM TransferOperations WHERE AssignedAccountId=$id;", ("$id", Id(B))));
        Assert.Equal(0, await ScalarAsync(factory, "SELECT COUNT(*) FROM pragma_foreign_key_check;"));
    }

    [Fact]
    public async Task KeepHistoryDoesNotTreatOtherAccountSharedWorkAsTargetAccountWork()
    {
        var (factory, store, graph) = await CreateAsync(seedGraph: true);
        await ExecuteAsync(factory, """
            UPDATE TransferOperations
            SET Status='ResultPending',SubmitState='PendingConfirmation',TargetSlotState='Reserved'
            WHERE AssignedAccountId=$account AND BatchJobId=$batch;
            UPDATE ResourceItems
            SET CandidateStatus='Reserved',ReservedAccountId=$account,ReservationExpiresAt='2026-08-21T00:00:00Z'
            WHERE Id=$resource;
            """, ("$account", Id(A)), ("$batch", Id(graph.SharedBatch)), ("$resource", Id(graph.SharedPublic)));
        var beforeHistory = await ReadHistoryDataAsync(factory);

        var preview = await store.PreviewAsync(B, AccountExitHistoryMode.KeepHistory, default);
        var result = await store.PrepareRemovalAsync(
            new(B, AccountExitHistoryMode.KeepHistory, preview.ConfirmationToken, RemovedAt), default);

        Assert.Equal(AccountExitStatus.Ready, preview.Status);
        Assert.Equal(AccountRemovalPreparationStatus.Prepared, result.Status);
        Assert.Equal(beforeHistory, await ReadHistoryDataAsync(factory));
    }

    [Fact]
    public async Task RemoveTerminalHistoryDeletesOnlyBTerminalGraphAndRebuildsSharedCounters()
    {
        var (factory, store, graph) = await CreateAsync(seedGraph: true);
        var sharedDeletedResource = Guid.NewGuid();
        var sharedDeletedOperation = Guid.NewGuid();
        var sharedExclusiveNoOp = Guid.NewGuid();
        var cResource = Guid.NewGuid();
        var cOperation = Guid.NewGuid();
        var sharedAOperation = Guid.NewGuid();
        var shortfallImport = Guid.NewGuid();
        var shortfallBatch = Guid.NewGuid();
        var shortfallResource = Guid.NewGuid();
        var shortfallPublic = Guid.NewGuid();
        var shortfallOperation = Guid.NewGuid();
        var publicAudit = Guid.NewGuid();
        var aLog = Guid.NewGuid();
        var cLog = Guid.NewGuid();
        var bAccountLog = Guid.NewGuid();
        var bOperationLog = Guid.NewGuid();
        var bResourceLog = Guid.NewGuid();
        var bOnlyPublicLog = Guid.NewGuid();

        await InsertAllocationAsync(factory, Guid.NewGuid(), graph.SharedBatch, C, "/charlie");
        await InsertResourceAsync(factory, cResource, graph.SharedBatch, 4, C, "Expanded", "https://shared-c.invalid");
        await InsertResourceAsync(factory, sharedDeletedResource, graph.SharedBatch, 5, null, "Expanded", "https://shared-delete.invalid");
        await InsertResourceAsync(factory, sharedExclusiveNoOp, graph.SharedBatch, 6, B, "Unused", "https://shared-exclusive.invalid");
        await InsertOperationAsync(factory, cOperation, cResource, graph.SharedBatch, C, 3, "Succeeded");
        await InsertOperationAsync(factory, sharedDeletedOperation, sharedDeletedResource, graph.SharedBatch, B, 4, "Failed");
        await InsertOperationAsync(factory, sharedAOperation, graph.BResource, graph.SharedBatch, A, 5, "Succeeded");
        await ExecuteAsync(factory, "UPDATE ResourceItems SET CandidateConstraintAccountId=$account WHERE Id=$id;",
            ("$account", Id(B)), ("$id", Id(graph.BOnlyPublic)));
        await ExecuteAsync(factory, "UPDATE BatchJobs SET ResourceCount=6,OperationCount=5 WHERE Id=$id;", ("$id", Id(graph.SharedBatch)));
        await ExecuteAsync(factory, "UPDATE AccountAllocations SET AssignedItemCount=99,AttemptedItemCount=99,NewSuccessItemCount=99,AlreadySatisfiedItemCount=99,SuccessItemCount=99,FailedItemCount=99,LockedItemCount=99,ExclusiveCandidateCount=99,UnusedCandidateCount=99,ReservedSlotCount=99,FulfilledCount=99 WHERE BatchJobId=$batch AND AccountId<>$account;", ("$batch", Id(graph.SharedBatch)), ("$account", Id(B)));

        await InsertImportAsync(factory, shortfallImport, "BatchCreated", B);
        await InsertBatchAsync(factory, shortfallBatch, "CompletedWithShortfall", shortfallImport, 2, 1);
        await ExecuteAsync(factory, "UPDATE ImportSessions SET CreatedBatchJobId=$batch,AppendToBatchJobId=$batch WHERE Id=$id;", ("$batch", Id(shortfallBatch)), ("$id", Id(shortfallImport)));
        await InsertAllocationAsync(factory, Guid.NewGuid(), shortfallBatch, B, "/shortfall");
        await InsertResourceAsync(factory, shortfallResource, shortfallBatch, 1, B, "Expanded", "https://shortfall.invalid");
        await InsertResourceAsync(factory, shortfallPublic, shortfallBatch, 2, B, "Unused", "https://shortfall-public.invalid");
        await InsertOperationAsync(factory, shortfallOperation, shortfallResource, shortfallBatch, B, 1, "Cancelled");

        await InsertLogAsync(factory, publicAudit, graph.SharedBatch, null, null, null);
        await InsertLogAsync(factory, aLog, graph.SharedBatch, graph.AResource, null, A);
        await InsertLogAsync(factory, cLog, graph.SharedBatch, cResource, cOperation, C);
        await InsertLogAsync(factory, bAccountLog, graph.SharedBatch, null, null, B);
        await InsertLogAsync(factory, bOperationLog, graph.SharedBatch, graph.BResource, graph.BOperation, null);
        await InsertLogAsync(factory, bResourceLog, graph.SharedBatch, sharedDeletedResource, null, null);
        await InsertLogAsync(factory, bOnlyPublicLog, shortfallBatch, shortfallPublic, null, null);

        var beforeA = await ReadRowsAsync(factory, "SELECT * FROM Accounts WHERE Id=$id;", ("$id", Id(A)));
        var beforeC = await ReadRowsAsync(factory, "SELECT * FROM Accounts WHERE Id=$id;", ("$id", Id(C)));
        var beforeOtherOperations = await ReadRowsAsync(factory,
            "SELECT * FROM TransferOperations WHERE AssignedAccountId IN ($a,$c) ORDER BY Id;", ("$a", Id(A)), ("$c", Id(C)));
        var beforeADailyCounters = await ScalarTextAsync(factory,
            "SELECT DailyExecutedCount||','||DailyCountDateKey FROM Accounts WHERE Id=$id;", ("$id", Id(A)));
        var beforeCDailyCounters = await ScalarTextAsync(factory,
            "SELECT DailyExecutedCount||','||DailyCountDateKey FROM Accounts WHERE Id=$id;", ("$id", Id(C)));
        var preview = await store.PreviewAsync(B, AccountExitHistoryMode.RemoveTerminalHistory, default);
        Assert.Equal(4, preview.TerminalOperationCount);
        Assert.Equal(4, preview.TerminalBatchCount);
        Assert.Equal(1, preview.SharedBatchCount);
        Assert.Equal(64, preview.ConfirmationToken.Length);

        var result = await store.PrepareRemovalAsync(new(B, AccountExitHistoryMode.RemoveTerminalHistory, preview.ConfirmationToken, RemovedAt), default);

        Assert.Equal(AccountRemovalPreparationStatus.Prepared, result.Status);
        Assert.True(result.DatabasePrepared);
        Assert.Equal(beforeA, await ReadRowsAsync(factory, "SELECT * FROM Accounts WHERE Id=$id;", ("$id", Id(A))));
        Assert.Equal(beforeC, await ReadRowsAsync(factory, "SELECT * FROM Accounts WHERE Id=$id;", ("$id", Id(C))));
        Assert.Equal(beforeOtherOperations, await ReadRowsAsync(factory,
            "SELECT * FROM TransferOperations WHERE AssignedAccountId IN ($a,$c) ORDER BY Id;", ("$a", Id(A)), ("$c", Id(C))));
        Assert.Equal(beforeADailyCounters, await ScalarTextAsync(factory,
            "SELECT DailyExecutedCount||','||DailyCountDateKey FROM Accounts WHERE Id=$id;", ("$id", Id(A))));
        Assert.Equal(beforeCDailyCounters, await ScalarTextAsync(factory,
            "SELECT DailyExecutedCount||','||DailyCountDateKey FROM Accounts WHERE Id=$id;", ("$id", Id(C))));
        var b = await ReadAccountAsync(factory, B);
        Assert.Equal("Disabled", b.Status);
        Assert.False(b.Enabled);
        Assert.Null(b.SessionHex);
        Assert.Null(b.ValidatedAt);

        Assert.Equal(0, await ScalarAsync(factory, "SELECT COUNT(*) FROM TransferOperations WHERE AssignedAccountId=$account AND Status IN ('Succeeded','Failed','Cancelled');", ("$account", Id(B))));
        Assert.Equal(0, await ScalarAsync(factory, "SELECT COUNT(*) FROM AccountAllocations WHERE AccountId=$account AND BatchJobId IN ($shared,$only,$cancelled,$shortfall);", ("$account", Id(B)), ("$shared", Id(graph.SharedBatch)), ("$only", Id(graph.BBatch)), ("$cancelled", Id(graph.ConstraintBatch)), ("$shortfall", Id(shortfallBatch))));
        Assert.Equal(1, await ScalarAsync(factory, "SELECT COUNT(*) FROM BatchJobs WHERE Id=$id;", ("$id", Id(graph.SharedBatch))));
        Assert.Equal(0, await ScalarAsync(factory, "SELECT COUNT(*) FROM BatchJobs WHERE Id IN ($only,$cancelled,$shortfall);", ("$only", Id(graph.BBatch)), ("$cancelled", Id(graph.ConstraintBatch)), ("$shortfall", Id(shortfallBatch))));
        Assert.Equal(4, await ScalarAsync(factory, "SELECT ResourceCount FROM BatchJobs WHERE Id=$id;", ("$id", Id(graph.SharedBatch))));
        Assert.Equal(3, await ScalarAsync(factory, "SELECT OperationCount FROM BatchJobs WHERE Id=$id;", ("$id", Id(graph.SharedBatch))));

        Assert.Equal(1, await ScalarAsync(factory, "SELECT COUNT(*) FROM ResourceItems WHERE Id=$id;", ("$id", Id(graph.SharedPublic))));
        Assert.Equal(1, await ScalarAsync(factory, "SELECT COUNT(*) FROM ResourceItems WHERE Id=$id;", ("$id", Id(graph.BResource))));
        Assert.Equal(0, await ScalarAsync(factory, "SELECT COUNT(*) FROM ResourceItems WHERE Id IN ($deleted,$exclusive);", ("$deleted", Id(sharedDeletedResource)), ("$exclusive", Id(sharedExclusiveNoOp))));
        Assert.Equal("2,2,2,0,2,0,0,1,0,0,2", await ScalarTextAsync(factory, "SELECT AssignedItemCount||','||AttemptedItemCount||','||NewSuccessItemCount||','||AlreadySatisfiedItemCount||','||SuccessItemCount||','||FailedItemCount||','||LockedItemCount||','||ExclusiveCandidateCount||','||UnusedCandidateCount||','||ReservedSlotCount||','||FulfilledCount FROM AccountAllocations WHERE BatchJobId='" + Id(graph.SharedBatch) + "' AND AccountId='" + Id(A) + "';"));
        Assert.Equal("1,1,1,0,1,0,0,1,0,0,1", await ScalarTextAsync(factory, "SELECT AssignedItemCount||','||AttemptedItemCount||','||NewSuccessItemCount||','||AlreadySatisfiedItemCount||','||SuccessItemCount||','||FailedItemCount||','||LockedItemCount||','||ExclusiveCandidateCount||','||UnusedCandidateCount||','||ReservedSlotCount||','||FulfilledCount FROM AccountAllocations WHERE BatchJobId='" + Id(graph.SharedBatch) + "' AND AccountId='" + Id(C) + "';"));

        Assert.Equal(3, await ScalarAsync(factory, "SELECT COUNT(*) FROM ExecutionLogs WHERE Id IN ($public,$a,$c);", ("$public", Id(publicAudit)), ("$a", Id(aLog)), ("$c", Id(cLog))));
        Assert.Equal(0, await ScalarAsync(factory, "SELECT COUNT(*) FROM ExecutionLogs WHERE Id IN ($account,$operation,$resource,$only);", ("$account", Id(bAccountLog)), ("$operation", Id(bOperationLog)), ("$resource", Id(bResourceLog)), ("$only", Id(bOnlyPublicLog))));
        Assert.Equal(1, await ScalarAsync(factory, "SELECT COUNT(*) FROM ImportSessions WHERE Id=$id AND CreatedBatchJobId IS NULL AND AppendToBatchJobId=$shared;", ("$id", Id(graph.Import)), ("$shared", Id(graph.SharedBatch))));
        Assert.Equal(0, await ScalarAsync(factory, "SELECT COUNT(*) FROM ImportSessions WHERE Id=$id;", ("$id", Id(shortfallImport))));
        Assert.Equal(0, await ScalarAsync(factory, "SELECT COUNT(*) FROM pragma_foreign_key_check;"));
    }

    [Theory]
    [InlineData("Parsing")]
    [InlineData("Draft")]
    [InlineData("Prechecking")]
    [InlineData("ReadyToConfirm")]
    public async Task NonterminalImportSessionsBlockRemovalAndAreNeverDeleted(string status)
    {
        var (factory, store, _) = await CreateAsync(seedGraph: true);
        var import = Guid.NewGuid();
        await InsertImportAsync(factory, import, status, B);
        var before = await ReadAllPersistentDataAsync(factory);

        var preview = await store.PreviewAsync(B, AccountExitHistoryMode.RemoveTerminalHistory, default);
        var result = await store.PrepareRemovalAsync(new(B, AccountExitHistoryMode.RemoveTerminalHistory, preview.ConfirmationToken, RemovedAt), default);

        Assert.Equal(AccountExitStatus.ActiveWorkBlocked, preview.Status);
        Assert.Equal(AccountRemovalPreparationStatus.ActiveWorkBlocked, result.Status);
        Assert.Equal(before, await ReadAllPersistentDataAsync(factory));
    }

    [Fact]
    public async Task ActiveRemoteSelectionBlocksRemovalAndCancelledSelectionIsRetainedAsHistory()
    {
        var (factory, store, _) = await CreateAsync();
        var sessions = new SqliteRemoteSelectionSessionStore(factory);
        var beforeSession = await store.PreviewAsync(
            B, AccountExitHistoryMode.RemoveTerminalHistory, default);
        Assert.Equal(AccountExitStatus.Ready, beforeSession.Status);
        var session = await sessions.CreateAsync(B, RemovedAt.AddMinutes(-1));

        var blocked = await store.PreviewAsync(B, AccountExitHistoryMode.RemoveTerminalHistory, default);
        var blockedPreparation = await store.PrepareRemovalAsync(
            new(B, AccountExitHistoryMode.RemoveTerminalHistory,
                beforeSession.ConfirmationToken, RemovedAt), default);

        Assert.Equal(AccountExitStatus.ActiveWorkBlocked, blocked.Status);
        Assert.Equal(AccountRemovalPreparationStatus.ActiveWorkBlocked, blockedPreparation.Status);
        Assert.True(await sessions.TryCompleteAsync(
            session.SessionId, session.WorkspaceDraftId, B, session.Generation, session.Revision,
            RemoteSelectionSessionState.Cancelled, RemovedAt));

        var ready = await store.PreviewAsync(B, AccountExitHistoryMode.RemoveTerminalHistory, default);
        Assert.Equal(AccountExitStatus.Ready, ready.Status);
        var prepared = await store.PrepareRemovalAsync(
            new(B, AccountExitHistoryMode.RemoveTerminalHistory, ready.ConfirmationToken, RemovedAt), default);

        Assert.Equal(AccountRemovalPreparationStatus.Prepared, prepared.Status);
        Assert.Equal(RemoteSelectionSessionState.Cancelled,
            (await sessions.GetAsync(session.SessionId, session.WorkspaceDraftId,
                B, session.Generation))!.State);
    }

    [Fact]
    public async Task PendingConfirmationOperationBlocksRemovalAndIsNeverDeleted()
    {
        var (factory, store, graph) = await CreateAsync(seedGraph: true);
        var resource = Guid.NewGuid();
        var operation = Guid.NewGuid();
        await InsertResourceAsync(factory, resource, graph.BBatch, 9, B, "Reserved", "https://pending.invalid");
        await InsertOperationAsync(factory, operation, resource, graph.BBatch, B, 9, "ResultPending");
        await ExecuteAsync(factory, "UPDATE TransferOperations SET SubmitState='PendingConfirmation',TargetSlotState='Reserved',ConfirmationDescriptorJson='{}' WHERE Id=$id;", ("$id", Id(operation)));
        var before = await ReadAllPersistentDataAsync(factory);

        var preview = await store.PreviewAsync(B, AccountExitHistoryMode.RemoveTerminalHistory, default);
        var result = await store.PrepareRemovalAsync(new(B, AccountExitHistoryMode.RemoveTerminalHistory, preview.ConfirmationToken, RemovedAt), default);

        Assert.Equal(AccountExitStatus.ActiveWorkBlocked, preview.Status);
        Assert.Equal(AccountRemovalPreparationStatus.ActiveWorkBlocked, result.Status);
        Assert.Equal(before, await ReadAllPersistentDataAsync(factory));
    }

    [Fact]
    public async Task RemoveHistoryManifestMutationIsStaleAndWritesNothing()
    {
        var (factory, store, graph) = await CreateAsync(seedGraph: true);
        var preview = await store.PreviewAsync(B, AccountExitHistoryMode.RemoveTerminalHistory, default);
        await ExecuteAsync(factory, "UPDATE ResourceItems SET CandidateStatus='Unused' WHERE Id=$id;", ("$id", Id(graph.BResource)));
        var before = await ReadAllPersistentDataAsync(factory);

        var result = await store.PrepareRemovalAsync(new(B, AccountExitHistoryMode.RemoveTerminalHistory, preview.ConfirmationToken, RemovedAt), default);

        Assert.Equal(AccountRemovalPreparationStatus.StaleConfirmation, result.Status);
        Assert.Equal(before, await ReadAllPersistentDataAsync(factory));
    }

    [Fact]
    public async Task CleanupRelevantExecutionLogMutationMakesTokenStaleAndWritesNothing()
    {
        var (factory, store, graph) = await CreateAsync(seedGraph: true);
        var log = Guid.NewGuid();
        await InsertLogAsync(factory, log, graph.SharedBatch, graph.BResource, graph.BOperation, B);
        var preview = await store.PreviewAsync(B, AccountExitHistoryMode.RemoveTerminalHistory, default);
        await ExecuteAsync(factory, "UPDATE ExecutionLogs SET SanitizedMessage='changed audit' WHERE Id=$id;", ("$id", Id(log)));
        var before = await ReadAllPersistentDataAsync(factory);

        var result = await store.PrepareRemovalAsync(new(B, AccountExitHistoryMode.RemoveTerminalHistory, preview.ConfirmationToken, RemovedAt), default);

        Assert.Equal(AccountRemovalPreparationStatus.StaleConfirmation, result.Status);
        Assert.Equal(before, await ReadAllPersistentDataAsync(factory));
    }

    [Fact]
    public async Task OtherAccountConstrainedNoOpResourceMakesTerminalBatchSharedAndSurvives()
    {
        var (factory, store, _) = await CreateAsync();
        var batch = Guid.NewGuid();
        var bResource = Guid.NewGuid();
        var aResource = Guid.NewGuid();
        await InsertBatchAsync(factory, batch, "Completed", null, 2, 1);
        await InsertAllocationAsync(factory, Guid.NewGuid(), batch, B, "/bravo");
        await InsertResourceAsync(factory, bResource, batch, 1, B, "Expanded", "https://bravo.invalid");
        await InsertResourceAsync(factory, aResource, batch, 2, A, "Unused", "https://alpha.invalid");
        await InsertOperationAsync(factory, Guid.NewGuid(), bResource, batch, B, 1, "Succeeded");
        var preview = await store.PreviewAsync(B, AccountExitHistoryMode.RemoveTerminalHistory, default);

        var result = await store.PrepareRemovalAsync(new(B, AccountExitHistoryMode.RemoveTerminalHistory, preview.ConfirmationToken, RemovedAt), default);

        Assert.Equal(1, preview.SharedBatchCount);
        Assert.Equal(AccountRemovalPreparationStatus.Prepared, result.Status);
        Assert.Equal(1, await ScalarAsync(factory, "SELECT COUNT(*) FROM BatchJobs WHERE Id=$id;", ("$id", Id(batch))));
        Assert.Equal(1, await ScalarAsync(factory, "SELECT COUNT(*) FROM ResourceItems WHERE Id=$id;", ("$id", Id(aResource))));
        Assert.Equal(0, await ScalarAsync(factory, "SELECT COUNT(*) FROM ResourceItems WHERE Id=$id;", ("$id", Id(bResource))));
    }

    [Fact]
    public async Task PublicNoOpResourcePreservesMinimalTerminalBatchAndImportContext()
    {
        var (factory, store, _) = await CreateAsync();
        var import = Guid.NewGuid();
        var batch = Guid.NewGuid();
        var bResource = Guid.NewGuid();
        var bOperation = Guid.NewGuid();
        var publicResource = Guid.NewGuid();
        var publicAudit = Guid.NewGuid();
        await InsertImportAsync(factory, import, "BatchCreated", B);
        await InsertBatchAsync(factory, batch, "Completed", import, 2, 1);
        await ExecuteAsync(factory, "UPDATE ImportSessions SET CreatedBatchJobId=$batch,AppendToBatchJobId=$batch WHERE Id=$id;",
            ("$batch", Id(batch)), ("$id", Id(import)));
        await InsertAllocationAsync(factory, Guid.NewGuid(), batch, B, "/bravo");
        await InsertResourceAsync(factory, bResource, batch, 1, B, "Expanded", "https://bravo.invalid");
        await InsertResourceAsync(factory, publicResource, batch, 2, null, "Unallocated", "https://public.invalid");
        await InsertOperationAsync(factory, bOperation, bResource, batch, B, 1, "Succeeded");
        await InsertLogAsync(factory, publicAudit, batch, null, null, null);
        var preview = await store.PreviewAsync(B, AccountExitHistoryMode.RemoveTerminalHistory, default);

        var result = await store.PrepareRemovalAsync(new(B, AccountExitHistoryMode.RemoveTerminalHistory, preview.ConfirmationToken, RemovedAt), default);

        Assert.Equal(1, preview.TerminalOperationCount);
        Assert.Equal(1, preview.TerminalBatchCount);
        Assert.Equal(1, preview.SharedBatchCount);
        Assert.Equal(AccountRemovalPreparationStatus.Prepared, result.Status);
        Assert.Equal("1,0", await ScalarTextAsync(factory,
            "SELECT ResourceCount||','||OperationCount FROM BatchJobs WHERE Id='" + Id(batch) + "';"));
        Assert.Equal(1, await ScalarAsync(factory, "SELECT COUNT(*) FROM ResourceItems WHERE Id=$id;", ("$id", Id(publicResource))));
        Assert.Equal(0, await ScalarAsync(factory, "SELECT COUNT(*) FROM ResourceItems WHERE Id=$id;", ("$id", Id(bResource))));
        Assert.Equal(0, await ScalarAsync(factory, "SELECT COUNT(*) FROM TransferOperations WHERE Id=$id;", ("$id", Id(bOperation))));
        Assert.Equal(0, await ScalarAsync(factory, "SELECT COUNT(*) FROM AccountAllocations WHERE BatchJobId=$batch AND AccountId=$account;",
            ("$batch", Id(batch)), ("$account", Id(B))));
        Assert.Equal(1, await ScalarAsync(factory,
            "SELECT COUNT(*) FROM ImportSessions WHERE Id=$id AND CreatedBatchJobId=$batch AND AppendToBatchJobId=$batch;",
            ("$id", Id(import)), ("$batch", Id(batch))));
        Assert.Equal(1, await ScalarAsync(factory, "SELECT COUNT(*) FROM ExecutionLogs WHERE Id=$id;", ("$id", Id(publicAudit))));
        Assert.Equal(0, await ScalarAsync(factory, "SELECT COUNT(*) FROM pragma_foreign_key_check;"));
    }

    [Theory]
    [InlineData("Submitting")]
    [InlineData("PendingConfirmation")]
    public async Task TerminalStatusWithUnsafeSubmitStateBlocksPreviewAndPrepareWithoutWrites(string submitState)
    {
        var (factory, store, graph) = await CreateAsync(seedGraph: true);
        await ExecuteAsync(factory, "UPDATE TransferOperations SET SubmitState=$submit WHERE Id=$id;",
            ("$submit", submitState), ("$id", Id(graph.BOperation)));
        var before = await ReadAllPersistentDataAsync(factory);

        var preview = await store.PreviewAsync(B, AccountExitHistoryMode.RemoveTerminalHistory, default);
        var result = await store.PrepareRemovalAsync(
            new(B, AccountExitHistoryMode.RemoveTerminalHistory, preview.ConfirmationToken, RemovedAt), default);

        Assert.Equal(AccountExitStatus.ActiveWorkBlocked, preview.Status);
        Assert.Equal(AccountRemovalPreparationStatus.ActiveWorkBlocked, result.Status);
        Assert.Equal(before, await ReadAllPersistentDataAsync(factory));
    }

    [Theory]
    [InlineData("insert")]
    [InlineData("move")]
    [InlineData("field")]
    [InlineData("duplicate")]
    [InlineData("resource-reference")]
    public async Task AnyReachableStagedRowClosureMutationMakesCleanupTokenStale(string mutation)
    {
        var (factory, store, graph) = await CreateAsync(seedGraph: true);
        var row = Guid.NewGuid();
        var duplicate = Guid.NewGuid();
        var externalImport = Guid.NewGuid();
        await InsertImportAsync(factory, externalImport, "BatchCreated", A);
        await InsertStagedRowAsync(factory, row, graph.Import, 1, null, "ready note");
        await InsertStagedRowAsync(factory, duplicate, graph.Import, 2, row, "duplicate note");
        await ExecuteAsync(factory, "UPDATE ResourceItems SET StagedImportRowId=$row WHERE Id=$resource;",
            ("$row", Id(row)), ("$resource", Id(graph.BOnlyResource)));
        var preview = await store.PreviewAsync(B, AccountExitHistoryMode.RemoveTerminalHistory, default);

        switch (mutation)
        {
            case "insert":
                await InsertStagedRowAsync(factory, Guid.NewGuid(), graph.Import, 3, null, "inserted later");
                break;
            case "move":
                await ExecuteAsync(factory, "UPDATE StagedImportRows SET ImportSessionId=$import WHERE Id=$row;",
                    ("$import", Id(externalImport)), ("$row", Id(row)));
                break;
            case "field":
                await ExecuteAsync(factory, "UPDATE StagedImportRows SET ValidationMessage='changed decision',UpdatedAt='2026-08-20T09:00:00Z' WHERE Id=$row;", ("$row", Id(row)));
                break;
            case "duplicate":
                await ExecuteAsync(factory, "UPDATE StagedImportRows SET DuplicateOfStagedRowId=NULL WHERE Id=$row;", ("$row", Id(duplicate)));
                break;
            case "resource-reference":
                await ExecuteAsync(factory, "UPDATE ResourceItems SET StagedImportRowId=$row WHERE Id=$resource;",
                    ("$row", Id(duplicate)), ("$resource", Id(graph.SharedPublic)));
                break;
        }
        var before = await ReadAllPersistentDataAsync(factory);

        var result = await store.PrepareRemovalAsync(new(B, AccountExitHistoryMode.RemoveTerminalHistory, preview.ConfirmationToken, RemovedAt), default);

        Assert.Equal(AccountRemovalPreparationStatus.StaleConfirmation, result.Status);
        Assert.Equal(before, await ReadAllPersistentDataAsync(factory));
    }

    public static IEnumerable<object[]> UnsafeCancelledEvidence()
    {
        yield return ["daily count", "DailyCountedAt='2026-08-20T05:00:00Z'"];
        yield return ["descriptor", "ConfirmationDescriptorJson='{}'"];
        yield return ["submit started", "SubmitStartedAt='2026-08-20T05:00:00Z'"];
        yield return ["adapter request", "AdapterRequestId='request'"];
        yield return ["remote ids", "RemoteItemIdsJson='[]'"];
        yield return ["result received", "SubmitState='ResultReceived',ConfirmationDescriptorJson='{}'"];
        yield return ["reserved slot", "TargetSlotState='Reserved'"];
        yield return ["slot timestamp", "TargetSlotReservedAt='2026-08-20T05:00:00Z'"];
        yield return ["lease", "LeaseExpiresAt='2026-08-20T05:10:00Z'"];
    }

    [Theory]
    [MemberData(nameof(UnsafeCancelledEvidence))]
    public async Task CancelledOperationWithSubmissionOrReservationEvidenceBlocksWithoutWrites(string _, string mutation)
    {
        var (factory, store, graph) = await CreateAsync(seedGraph: true);
        await ExecuteAsync(factory, """
            UPDATE TransferOperations
            SET Status='Cancelled',OutcomeKind='Cancelled',SubmitState='NotPrepared',TargetSlotState='Released',
                DailyCountedAt=NULL,ConfirmationDescriptorJson=NULL,SubmitStartedAt=NULL,AdapterRequestId=NULL,
                RemoteItemIdsJson=NULL,TargetSlotReservedAt=NULL,LeaseExpiresAt=NULL
            WHERE Id=$id;
            """, ("$id", Id(graph.BOnlyOperation)));
        var ready = await store.PreviewAsync(B, AccountExitHistoryMode.RemoveTerminalHistory, default);
        Assert.Equal(AccountExitStatus.Ready, ready.Status);
        await ExecuteAsync(factory, $"UPDATE TransferOperations SET {mutation} WHERE Id=$id;", ("$id", Id(graph.BOnlyOperation)));
        var before = await ReadAllPersistentDataAsync(factory);

        var preview = await store.PreviewAsync(B, AccountExitHistoryMode.RemoveTerminalHistory, default);
        var result = await store.PrepareRemovalAsync(new(B, AccountExitHistoryMode.RemoveTerminalHistory, ready.ConfirmationToken, RemovedAt), default);

        Assert.Equal(AccountExitStatus.ActiveWorkBlocked, preview.Status);
        Assert.Equal(AccountRemovalPreparationStatus.ActiveWorkBlocked, result.Status);
        Assert.Equal(before, await ReadAllPersistentDataAsync(factory));
    }

    [Fact]
    public async Task UnsafeOtherAccountOperationInAffectedTerminalBatchBlocksWithoutWrites()
    {
        var (factory, store, graph) = await CreateAsync(seedGraph: true);
        await ExecuteAsync(factory, """
            UPDATE TransferOperations
            SET Status='ResultPending',OutcomeKind=NULL,SubmitState='PendingConfirmation',TargetSlotState='Reserved',
                ConfirmationDescriptorJson='{}'
            WHERE BatchJobId=$batch AND AssignedAccountId=$account;
            """, ("$batch", Id(graph.SharedBatch)), ("$account", Id(A)));
        var before = await ReadAllPersistentDataAsync(factory);

        var preview = await store.PreviewAsync(B, AccountExitHistoryMode.RemoveTerminalHistory, default);
        var result = await store.PrepareRemovalAsync(new(B, AccountExitHistoryMode.RemoveTerminalHistory, preview.ConfirmationToken, RemovedAt), default);

        Assert.Equal(AccountExitStatus.ActiveWorkBlocked, preview.Status);
        Assert.Equal(AccountRemovalPreparationStatus.ActiveWorkBlocked, result.Status);
        Assert.Equal(before, await ReadAllPersistentDataAsync(factory));
    }

    [Fact]
    public async Task ReservedAffectedResourceBlocksWithoutOrphaningReservation()
    {
        var (factory, store, graph) = await CreateAsync(seedGraph: true);
        await ExecuteAsync(factory, """
            UPDATE ResourceItems SET CandidateStatus='Reserved',ReservedAccountId=$account,
              ReservationExpiresAt='2026-08-20T10:00:00Z' WHERE Id=$resource;
            """, ("$account", Id(B)), ("$resource", Id(graph.SharedPublic)));
        var before = await ReadAllPersistentDataAsync(factory);

        var preview = await store.PreviewAsync(B, AccountExitHistoryMode.RemoveTerminalHistory, default);
        var result = await store.PrepareRemovalAsync(new(B, AccountExitHistoryMode.RemoveTerminalHistory, preview.ConfirmationToken, RemovedAt), default);

        Assert.Equal(AccountExitStatus.ActiveWorkBlocked, preview.Status);
        Assert.Equal(AccountRemovalPreparationStatus.ActiveWorkBlocked, result.Status);
        Assert.Equal(before, await ReadAllPersistentDataAsync(factory));
    }

    [Fact]
    public async Task NonterminalImportLinkedThroughAffectedGraphBlocksEvenWhenPrecheckedByA()
    {
        var (factory, store, graph) = await CreateAsync(seedGraph: true);
        await ExecuteAsync(factory, "UPDATE ImportSessions SET Status='Draft',PrecheckAccountId=$account WHERE Id=$id;",
            ("$account", Id(A)), ("$id", Id(graph.Import)));
        var before = await ReadAllPersistentDataAsync(factory);

        var preview = await store.PreviewAsync(B, AccountExitHistoryMode.RemoveTerminalHistory, default);
        var result = await store.PrepareRemovalAsync(new(B, AccountExitHistoryMode.RemoveTerminalHistory, preview.ConfirmationToken, RemovedAt), default);

        Assert.Equal(AccountExitStatus.ActiveWorkBlocked, preview.Status);
        Assert.Equal(AccountRemovalPreparationStatus.ActiveWorkBlocked, result.Status);
        Assert.Equal(before, await ReadAllPersistentDataAsync(factory));
    }

    [Fact]
    public async Task OtherAccountBoundLogThatTargetsRemovedOperationIsDeletedWithWholeBatch()
    {
        var (factory, store, _) = await CreateAsync();
        var import = Guid.NewGuid();
        var batch = Guid.NewGuid();
        var resource = Guid.NewGuid();
        var operation = Guid.NewGuid();
        var log = Guid.NewGuid();
        await InsertImportAsync(factory, import, "BatchCreated", B);
        await InsertBatchAsync(factory, batch, "Completed", import, 1, 1);
        await ExecuteAsync(factory, "UPDATE ImportSessions SET CreatedBatchJobId=$batch WHERE Id=$id;", ("$batch", Id(batch)), ("$id", Id(import)));
        await InsertAllocationAsync(factory, Guid.NewGuid(), batch, B, "/bravo");
        await InsertResourceAsync(factory, resource, batch, 1, B, "Expanded", "https://bravo.invalid");
        await InsertOperationAsync(factory, operation, resource, batch, B, 1, "Succeeded");
        await InsertLogAsync(factory, log, batch, resource, operation, A);
        var preview = await store.PreviewAsync(B, AccountExitHistoryMode.RemoveTerminalHistory, default);

        var result = await store.PrepareRemovalAsync(new(B, AccountExitHistoryMode.RemoveTerminalHistory, preview.ConfirmationToken, RemovedAt), default);

        Assert.Equal(0, preview.SharedBatchCount);
        Assert.Equal(AccountRemovalPreparationStatus.Prepared, result.Status);
        Assert.Equal(0, await ScalarAsync(factory, "SELECT COUNT(*) FROM BatchJobs WHERE Id=$id;", ("$id", Id(batch))));
        Assert.Equal(0, await ScalarAsync(factory, "SELECT COUNT(*) FROM ImportSessions WHERE Id=$id;", ("$id", Id(import))));
        Assert.Equal(0, await ScalarAsync(factory, "SELECT COUNT(*) FROM ExecutionLogs WHERE Id=$id;", ("$id", Id(log))));
    }

    [Fact]
    public async Task RepresentativeSharedCleanupRepairsGroupedCountersWithinStableBudget()
    {
        var (factory, store, graph) = await CreateAsync(seedGraph: true);
        const int itemsPerAccount = 300;
        await SeedRepresentativeSharedHistoryAsync(factory, graph.SharedBatch, itemsPerAccount);

        var stopwatch = Stopwatch.StartNew();
        var preview = await store.PreviewAsync(B, AccountExitHistoryMode.RemoveTerminalHistory, default);
        var result = await store.PrepareRemovalAsync(
            new(B, AccountExitHistoryMode.RemoveTerminalHistory, preview.ConfirmationToken, RemovedAt), default);
        stopwatch.Stop();

        Assert.Equal(AccountRemovalPreparationStatus.Prepared, result.Status);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20), $"Representative cleanup took {stopwatch.Elapsed}.");
        Assert.Equal(0, await ScalarAsync(factory,
            "SELECT COUNT(*) FROM TransferOperations WHERE BatchJobId=$batch AND AssignedAccountId=$account;",
            ("$batch", Id(graph.SharedBatch)), ("$account", Id(B))));
        Assert.Equal(await ScalarAsync(factory,
                "SELECT COUNT(*) FROM TransferOperations WHERE BatchJobId=$batch AND AssignedAccountId=$account;",
                ("$batch", Id(graph.SharedBatch)), ("$account", Id(A))),
            await ScalarAsync(factory,
                "SELECT AssignedItemCount FROM AccountAllocations WHERE BatchJobId=$batch AND AccountId=$account;",
                ("$batch", Id(graph.SharedBatch)), ("$account", Id(A))));
        Assert.Equal(0, await ScalarAsync(factory, "SELECT COUNT(*) FROM pragma_foreign_key_check;"));
    }

    [Fact]
    public async Task FailureAfterTerminalOperationDeletionRollsBackCleanupAndCredentialClear()
    {
        var (factory, normal, _) = await CreateAsync(seedGraph: true);
        var preview = await normal.PreviewAsync(B, AccountExitHistoryMode.RemoveTerminalHistory, default);
        var before = await ReadAllPersistentDataAsync(factory);
        var failing = new SqliteAccountRemovalStore(factory, async (connection, transaction, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT COUNT(*) FROM TransferOperations WHERE AssignedAccountId=$account AND Status IN ('Succeeded','Failed','Cancelled');";
            command.Parameters.AddWithValue("$account", Id(B));
            Assert.Equal(0L, Convert.ToInt64(await command.ExecuteScalarAsync(ct)));
            throw new InvalidOperationException("injected cleanup failure");
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.PrepareRemovalAsync(
            new(B, AccountExitHistoryMode.RemoveTerminalHistory, preview.ConfirmationToken, RemovedAt), default));

        Assert.Equal(before, await ReadAllPersistentDataAsync(factory));
    }

    [Fact]
    public async Task CancellationAfterTerminalOperationDeletionRollsBackCleanupAndCredentialClear()
    {
        var (factory, normal, _) = await CreateAsync(seedGraph: true);
        var preview = await normal.PreviewAsync(B, AccountExitHistoryMode.RemoveTerminalHistory, default);
        var before = await ReadAllPersistentDataAsync(factory);
        using var cancellation = new CancellationTokenSource();
        var cancelling = new SqliteAccountRemovalStore(factory, async (connection, transaction, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT COUNT(*) FROM TransferOperations WHERE AssignedAccountId=$account AND Status IN ('Succeeded','Failed','Cancelled');";
            command.Parameters.AddWithValue("$account", Id(B));
            Assert.Equal(0L, Convert.ToInt64(await command.ExecuteScalarAsync(ct)));
            cancellation.Cancel();
            await Task.FromCanceled(cancellation.Token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelling.PrepareRemovalAsync(
            new(B, AccountExitHistoryMode.RemoveTerminalHistory, preview.ConfirmationToken, RemovedAt), cancellation.Token));

        Assert.Equal(before, await ReadAllPersistentDataAsync(factory));
    }

    [Fact]
    public async Task ExceptionBeforeCommitRollsBackAllWrites()
    {
        var (factory, normal, _) = await CreateAsync(seedGraph: true);
        var token = (await normal.PreviewAsync(B, AccountExitHistoryMode.KeepHistory, default)).ConfirmationToken;
        var before = await ReadAccountAsync(factory, B);
        var failing = new SqliteAccountRemovalStore(factory, async (connection, transaction, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE Accounts SET Status='Invalid' WHERE Id=$id;";
            command.Parameters.AddWithValue("$id", Id(B));
            await command.ExecuteNonQueryAsync(ct);
            throw new InvalidOperationException("raw sqlite secret");
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.PrepareRemovalAsync(new(B, AccountExitHistoryMode.KeepHistory, token, RemovedAt), default));
        Assert.Equal(before, await ReadAccountAsync(factory, B));
    }

    [Fact]
    public async Task CancellationAfterCredentialClearRollsBackAccountBytesAndEveryTable()
    {
        var (factory, normal, _) = await CreateAsync(seedGraph: true);
        var preview = await normal.PreviewAsync(B, AccountExitHistoryMode.KeepHistory, default);
        var beforeAccount = await ReadAccountAsync(factory, B);
        var beforeDatabase = await ReadAllPersistentDataAsync(factory);
        using var cancellation = new CancellationTokenSource();
        var cancelling = new SqliteAccountRemovalStore(factory, null, null, async (connection, transaction, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT Status,EncryptedSession FROM Accounts WHERE Id=$id;";
            command.Parameters.AddWithValue("$id", Id(B));
            await using var reader = await command.ExecuteReaderAsync(ct);
            Assert.True(await reader.ReadAsync(ct));
            Assert.Equal("Disabled", reader.GetString(0));
            Assert.True(reader.IsDBNull(1));
            cancellation.Cancel();
            await Task.FromCanceled(cancellation.Token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelling.PrepareRemovalAsync(
            new(B, AccountExitHistoryMode.KeepHistory, preview.ConfirmationToken, RemovedAt), cancellation.Token));

        Assert.Equal(beforeAccount, await ReadAccountAsync(factory, B));
        Assert.Equal(beforeDatabase, await ReadAllPersistentDataAsync(factory));
    }

    [Fact]
    public async Task ImmediateTransactionPreventsRealSchedulerAssigningBInValidationGap()
    {
        var (factory, normal, g) = await CreateAsync(seedGraph: true, busyTimeout: 5000);
        var token = (await normal.PreviewAsync(B, AccountExitHistoryMode.KeepHistory, default)).ConfirmationToken;
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var guarded = new SqliteAccountRemovalStore(factory, async (connection, transaction, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE BatchJobs SET Status='Active' WHERE Id=$id;";
            command.Parameters.AddWithValue("$id", Id(g.BBatch));
            await command.ExecuteNonQueryAsync(ct);
            reached.SetResult();
            await release.Task.WaitAsync(ct);
        });
        var prepareTask = guarded.PrepareRemovalAsync(new(B, AccountExitHistoryMode.KeepHistory, token, RemovedAt), default);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var scheduler = new SqliteTransferSchedulingStore(factory);
        var writer = Task.Run(() => scheduler.TryReserveNextAsync(g.BBatch, RemovedAt, RemovedAt.AddMinutes(5), default));
        try
        {
            await Task.Delay(150);
            Assert.False(writer.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }
        Assert.Equal(AccountRemovalPreparationStatus.Prepared, (await prepareTask).Status);
        Assert.Null(await writer);
        Assert.Equal(0, await ScalarAsync(factory, "SELECT COUNT(*) FROM TransferOperations WHERE AssignedAccountId=$id AND Status='Reserved';", ("$id", Id(B))));
        Assert.Equal("Active", await ScalarTextAsync(factory, $"SELECT Status FROM BatchJobs WHERE Id='{Id(g.BBatch)}';"));
        Assert.Equal(1, await ScalarAsync(factory,
            "SELECT COUNT(*) FROM ResourceItems WHERE BatchJobId=$batch AND CandidateStatus='Unallocated';", ("$batch", Id(g.BBatch))));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(factory,
            "UPDATE Accounts SET Status='Active',IsEnabled=1,EncryptedSession=$session WHERE Id=$id;",
            ("$session", new byte[] { 4, 5, 6 }), ("$id", Id(B))));
        var eligible = await scheduler.TryReserveNextAsync(g.BBatch, RemovedAt.AddMinutes(1), RemovedAt.AddMinutes(6), default);
        Assert.Null(eligible);
    }

    [Fact]
    public async Task DeferredPreviewKeepsWalSnapshotWithoutBlockingWriter()
    {
        var (factory, normal, g) = await CreateAsync(seedGraph: true, busyTimeout: 2000);
        var snapshotReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePreview = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var previewStore = new SqliteAccountRemovalStore(factory, null, async (_, _, ct) =>
        {
            snapshotReached.SetResult();
            await releasePreview.Task.WaitAsync(ct);
        });
        var previewTask = previewStore.PreviewAsync(B, AccountExitHistoryMode.KeepHistory, default);
        await snapshotReached.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            var writer = Task.Run(async () =>
            {
                await using var connection = await factory.OpenConnectionAsync();
                await using var transaction = connection.BeginTransaction(deferred: false);
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE ResourceItems SET Note='writer committed' WHERE Id=$id;";
                command.Parameters.AddWithValue("$id", Id(g.SharedPublic));
                await command.ExecuteNonQueryAsync();
                await transaction.CommitAsync();
            });
            await writer.WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            releasePreview.TrySetResult();
        }

        var snapshotPreview = await previewTask;
        var currentPreview = await normal.PreviewAsync(B, AccountExitHistoryMode.KeepHistory, default);
        Assert.NotEqual(snapshotPreview.ConfirmationToken, currentPreview.ConfirmationToken);
        var stale = await normal.PrepareRemovalAsync(new(B, AccountExitHistoryMode.KeepHistory, snapshotPreview.ConfirmationToken, RemovedAt), default);
        Assert.Equal(AccountRemovalPreparationStatus.StaleConfirmation, stale.Status);
    }

    [Fact]
    public async Task FinalizeTombstoneIsConditionalAndIdempotent()
    {
        var (factory, store, _) = await CreateAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => CompleteAsync(store, B, RemovedAt));
        await AssertUnchangedAsync(factory);
        await PreparePendingAsync(store, B);
        await CompleteAsync(store, B, RemovedAt);
        await CompleteAsync(store, B, RemovedAt.AddHours(1));
        var state = await ReadAccountAsync(factory, B);
        Assert.Equal("Deleted", state.Status);
        Assert.True(state.Deleted);
        Assert.Equal(RemovedAt.ToString("O"), state.DeletedAt);
        Assert.Null(state.Profile);
    }

    [Fact]
    public async Task CoordinatorResumesDurableCleanupWithoutReplayingPreparationOrHistoryDeletion()
    {
        var (factory, store, _) = await CreateAsync(seedGraph: true);
        var preview = await store.PreviewAsync(B, AccountExitHistoryMode.RemoveTerminalHistory, default);
        var request = new AccountExitRequest(
            B,
            AccountExitHistoryMode.RemoveTerminalHistory,
            preview.ConfirmationToken,
            RemovedAt);
        var profiles = new RetryProfileStore { DeleteFailuresRemaining = 1 };
        var paths = new RecordingPathPolicy();
        var coordinator = new AccountRemovalCoordinator(store, profiles, paths);

        var first = await coordinator.ApplyAsync(request, default);
        var historyAfterPreparation = await ReadHistoryDataAsync(factory);
        var staleReplay = await coordinator.ApplyAsync(request, default);
        var resumed = await coordinator.ResumeCleanupAsync(B, RemovedAt.AddMinutes(1), default);

        Assert.Equal(AccountExitStatus.CleanupPending, first.Status);
        Assert.Equal(AccountExitStatus.StaleConfirmation, staleReplay.Status);
        Assert.Equal(AccountExitStatus.Removed, resumed.Status);
        Assert.Equal(2, profiles.DeleteCount);
        Assert.Equal(Path.Combine(root, "profiles", "bravo"), paths.LastPath);
        Assert.Equal(historyAfterPreparation, await ReadHistoryDataAsync(factory));
        var account = await ReadAccountAsync(factory, B);
        Assert.True(account.Deleted);
        Assert.Equal("Deleted", account.Status);
        Assert.Null(account.Profile);
    }

    [Fact]
    public async Task TombstoneFailureAfterExternalDeleteCanRetryWithAbsentProfile()
    {
        var (factory, store, _) = await CreateAsync();
        await PreparePendingAsync(store, B);
        await ExecuteAsync(factory, $"""
            CREATE TRIGGER fail_account_tombstone
            BEFORE UPDATE OF Status ON Accounts
            WHEN OLD.Id='{Id(B)}' AND NEW.Status='Deleted'
            BEGIN
              SELECT RAISE(ABORT, 'tombstone failed');
            END;
            """);
        var profilePresent = true;

        await Assert.ThrowsAsync<SqliteException>(() => store.CompletePreparedRemovalAsync(
            B,
            RemovedAt,
            (_, path, _) =>
            {
                Assert.Equal(Path.Combine(root, "profiles", "bravo"), path);
                profilePresent = false;
                return Task.CompletedTask;
            },
            default));

        var pending = await ReadAccountAsync(factory, B);
        Assert.Equal("Disabled", pending.Status);
        Assert.False(pending.Enabled);
        Assert.False(pending.Deleted);
        Assert.Null(pending.SessionHex);
        Assert.NotNull(pending.Profile);
        await ExecuteAsync(factory, "DROP TRIGGER fail_account_tombstone;");

        await store.CompletePreparedRemovalAsync(
            B,
            RemovedAt.AddMinutes(1),
            (_, path, _) =>
            {
                Assert.False(profilePresent);
                Assert.Equal(pending.Profile, path);
                return Task.CompletedTask;
            },
            default);

        Assert.True((await ReadAccountAsync(factory, B)).Deleted);
    }

    [Fact]
    public async Task ExternalCleanupCancellationRollsBackCompletionLease()
    {
        var (factory, store, _) = await CreateAsync();
        await PreparePendingAsync(store, B);
        var cancellation = new OperationCanceledException("profile-cancelled");

        var actual = await Assert.ThrowsAsync<OperationCanceledException>(() => store.CompletePreparedRemovalAsync(
            B,
            RemovedAt,
            (_, _, _) => Task.FromException(cancellation),
            default));

        Assert.Same(cancellation, actual);
        var pending = await ReadAccountAsync(factory, B);
        Assert.Equal("Disabled", pending.Status);
        Assert.False(pending.Enabled);
        Assert.False(pending.Deleted);
        Assert.Null(pending.SessionHex);
        Assert.NotNull(pending.Profile);
    }

    [Theory]
    [InlineData("UPDATE Accounts SET Status='Active',IsEnabled=1 WHERE Id=$id;")]
    [InlineData("UPDATE Accounts SET EncryptedSession=X'010203' WHERE Id=$id;")]
    [InlineData("UPDATE Accounts SET LastValidatedAt='2026-08-20T09:00:00Z' WHERE Id=$id;")]
    public async Task CompletionRefusesUnsafeCurrentStateBeforeExternalCleanup(string corruption)
    {
        var (factory, store, _) = await CreateAsync();
        await PreparePendingAsync(store, B);
        await ExecuteAsync(factory, "DROP TRIGGER TR_Accounts_RemovalState_Update;");
        await ExecuteAsync(factory, corruption, ("$id", Id(B)));
        var cleanupCount = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CompletePreparedRemovalAsync(
            B,
            RemovedAt,
            (_, _, _) =>
            {
                cleanupCount++;
                return Task.CompletedTask;
            },
            default));

        Assert.Equal(0, cleanupCount);
        Assert.False((await ReadAccountAsync(factory, B)).Deleted);
    }

    [Fact]
    public async Task CompletionLeasePreventsStaleRepositoryWriterFromReenablingAfterCleanup()
    {
        var (factory, store, _) = await CreateAsync(busyTimeout: 250);
        await PreparePendingAsync(store, B);
        var writerRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWriter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writerAttempting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = Task.Run(async () =>
        {
            await using var unit = await SqliteUnitOfWork.BeginAsync(factory);
            var stale = await unit.Accounts.GetByIdAsync(B) ?? throw new InvalidOperationException("Missing account.");
            writerRead.SetResult();
            await releaseWriter.Task;
            writerAttempting.SetResult();
            await unit.Accounts.UpsertAsync(stale with
            {
                Status = "Active",
                IsEnabled = true,
                EncryptedSession = [7, 8, 9],
                LastValidatedAt = RemovedAt
            });
            await unit.CommitAsync();
        });
        await writerRead.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await store.CompletePreparedRemovalAsync(
            B,
            RemovedAt,
            async (_, _, _) =>
            {
                releaseWriter.SetResult();
                await writerAttempting.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.NotSame(writer, await Task.WhenAny(writer, Task.Delay(100)));
            },
            default);

        await Assert.ThrowsAsync<SqliteException>(() => writer.WaitAsync(TimeSpan.FromSeconds(2)));
        var account = await ReadAccountAsync(factory, B);
        Assert.True(account.Deleted);
        Assert.False(account.Enabled);
        Assert.Null(account.SessionHex);
    }

    [Fact]
    public async Task FinalizeRefusesDisabledCredentialFreeRowWithStaleValidationTimestamp()
    {
        var (factory, store, _) = await CreateAsync();
        await ExecuteAsync(factory,
            "UPDATE Accounts SET Status='Disabled',IsEnabled=0,EncryptedSession=NULL WHERE Id=$id;", ("$id", Id(B)));
        var beforeAccount = await ReadAccountAsync(factory, B);
        var beforeDatabase = await ReadAllPersistentDataAsync(factory);
        Assert.NotNull(beforeAccount.ValidatedAt);

        await Assert.ThrowsAsync<InvalidOperationException>(() => CompleteAsync(store, B, RemovedAt));

        Assert.Equal(beforeAccount, await ReadAccountAsync(factory, B));
        Assert.Equal(beforeDatabase, await ReadAllPersistentDataAsync(factory));
    }

    [Fact]
    public async Task FinalizeRefusesReenabledOrCredentialBearingRows()
    {
        var (factory, store, _) = await CreateAsync();
        await ExecuteAsync(factory, "UPDATE Accounts SET Status='Disabled',IsEnabled=1 WHERE Id=$id;", ("$id", Id(B)));
        var reenabled = await ReadAccountAsync(factory, B);
        await Assert.ThrowsAsync<InvalidOperationException>(() => CompleteAsync(store, B, RemovedAt));
        Assert.Equal(reenabled, await ReadAccountAsync(factory, B));
        await ExecuteAsync(factory, "UPDATE Accounts SET IsEnabled=0,EncryptedSession=$session WHERE Id=$id;", ("$session", new byte[] { 9, 8, 7 }), ("$id", Id(B)));
        var credentialBearing = await ReadAccountAsync(factory, B);
        await Assert.ThrowsAsync<InvalidOperationException>(() => CompleteAsync(store, B, RemovedAt));
        Assert.Equal(credentialBearing, await ReadAccountAsync(factory, B));
        var accountCount = await ScalarAsync(factory, "SELECT COUNT(*) FROM Accounts;");
        await Assert.ThrowsAsync<InvalidOperationException>(() => CompleteAsync(store, Guid.NewGuid(), RemovedAt));
        Assert.Equal(accountCount, await ScalarAsync(factory, "SELECT COUNT(*) FROM Accounts;"));
    }

    public static IEnumerable<object[]> UnsafeDeletedCorruptions()
    {
        yield return ["UPDATE Accounts SET IsEnabled=1 WHERE Id=$id;"];
        yield return ["UPDATE Accounts SET EncryptedSession=X'010203' WHERE Id=$id;"];
        yield return ["UPDATE Accounts SET WebView2ProfileDirectory='retained-profile' WHERE Id=$id;"];
        yield return ["UPDATE Accounts SET LastValidatedAt='2026-08-20T09:00:00Z' WHERE Id=$id;"];
        yield return ["UPDATE Accounts SET DeletedAt=NULL WHERE Id=$id;"];
    }

    [Theory]
    [MemberData(nameof(UnsafeDeletedCorruptions))]
    public async Task FinalizeRefusesUnsafeRowsThatOnlyClaimToBeDeleted(string corruption)
    {
        var (factory, store, _) = await CreateAsync();
        await PreparePendingAsync(store, B);
        await CompleteAsync(store, B, RemovedAt);
        await ExecuteAsync(factory, corruption, ("$id", Id(B)));
        var before = await ReadAccountAsync(factory, B);

        await Assert.ThrowsAsync<InvalidOperationException>(() => CompleteAsync(store, B, RemovedAt.AddHours(1)));

        Assert.Equal(before, await ReadAccountAsync(factory, B));
    }

    [Fact]
    public async Task MalformedOrOversizedConfirmationTokensAreRejectedWithoutWrites()
    {
        var (factory, store, _) = await CreateAsync(seedGraph: true);
        var before = await ReadAccountAsync(factory, B);
        var tokens = new string?[] { null, string.Empty, "xyz", new string('A', 63), new string('A', 65), new string('A', 1_000_000) };
        foreach (var confirmationToken in tokens)
        {
            var request = new AccountExitRequest(B, AccountExitHistoryMode.KeepHistory, confirmationToken!, RemovedAt);
            var result = await store.PrepareRemovalAsync(request, default).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(AccountRemovalPreparationStatus.StaleConfirmation, result.Status);
            Assert.Equal(before, await ReadAccountAsync(factory, B));
        }
    }

    private async Task<(SqliteConnectionFactory Factory, SqliteAccountRemovalStore Store, Graph Graph)> CreateAsync(bool seedGraph = false, int busyTimeout = 1000)
    {
        Directory.CreateDirectory(root);
        var factory = new SqliteConnectionFactory(Path.Combine(root, $"app-{Guid.NewGuid():N}.db"), busyTimeout);
        await new DatabaseMigrator(factory, new DatabaseBackupService(factory, Path.Combine(root, "backups")), "test").MigrateAsync();
        await SeedAccountsAsync(factory);
        var graph = Graph.New();
        if (seedGraph) await SeedGraphAsync(factory, graph);
        return (factory, new SqliteAccountRemovalStore(factory), graph);
    }

    private async Task SeedAccountsAsync(SqliteConnectionFactory factory)
    {
        const string sql = """
            INSERT INTO Accounts(Id,BaiduUserId,DisplayName,EncryptedSession,WebView2ProfileDirectory,LoginMethod,Status,
              CapacityTotal,CapacityUsed,DailyLimit,DailyExecutedCount,SortOrder,IsEnabled,LastValidatedAt,DailyCountDateKey,IsDeleted,DeletedAt)
            VALUES($id,$baidu,$name,$session,$profile,'Cookie','Active',1000,100,10,1,$sort,1,$at,'2026-08-20',0,NULL);
            """;
        await ExecuteAsync(factory, sql, ("$id", Id(A)), ("$baidu", "baidu-alpha"), ("$name", "Alpha"), ("$session", Encoding.UTF8.GetBytes("cookie-alpha")), ("$profile", Path.Combine(root, "profiles", "alpha")), ("$sort", 1), ("$at", "2026-08-20T01:00:00Z"));
        await ExecuteAsync(factory, sql, ("$id", Id(B)), ("$baidu", "baidu-bravo"), ("$name", "Bravo"), ("$session", Encoding.UTF8.GetBytes("cookie-bravo")), ("$profile", Path.Combine(root, "profiles", "bravo")), ("$sort", 2), ("$at", "2026-08-20T02:00:00Z"));
        await ExecuteAsync(factory, sql, ("$id", Id(C)), ("$baidu", "baidu-charlie"), ("$name", "Charlie"), ("$session", Encoding.UTF8.GetBytes("cookie-charlie")), ("$profile", Path.Combine(root, "profiles", "charlie")), ("$sort", 3), ("$at", "2026-08-20T03:00:00Z"));
        await ExecuteAsync(factory, sql, ("$id", Id(Tombstone)), ("$baidu", "baidu-deleted"), ("$name", "Deleted"), ("$session", Encoding.UTF8.GetBytes("deleted-cookie")), ("$profile", Path.Combine(root, "profiles", "deleted")), ("$sort", 4), ("$at", "2026-08-19T01:00:00Z"));
        await ExecuteAsync(factory, "UPDATE Accounts SET Status='Deleted',IsEnabled=0,IsDeleted=1,EncryptedSession=NULL,WebView2ProfileDirectory=NULL,LastValidatedAt=NULL,DeletedAt='2026-08-19T00:00:00Z' WHERE Id=$id;", ("$id", Id(Tombstone)));
    }

    private static Task InsertImportAsync(SqliteConnectionFactory factory, Guid id, string status, Guid account) => ExecuteAsync(factory, """
        INSERT INTO ImportSessions(Id,SourceType,SourceName,Status,TotalRows,ReadyRows,InvalidRows,DuplicateRows,ExcludedRows,
          PrecheckAccountId,CreatedBatchJobId,AppendToBatchJobId,CreatedAt,UpdatedAt)
        VALUES($id,'Excel','safe.xlsx',$status,0,0,0,0,0,$account,NULL,NULL,$at,$at);
        """, ("$id", Id(id)), ("$status", status), ("$account", Id(account)), ("$at", "2026-08-20T04:00:00Z"));

    private static Task InsertStagedRowAsync(
        SqliteConnectionFactory factory,
        Guid id,
        Guid import,
        int rowNumber,
        Guid? duplicateOf,
        string note) => ExecuteAsync(factory, """
            INSERT INTO StagedImportRows(Id,ImportSessionId,SourceRowNumber,ShareUrl,NormalizedShareId,ExtractionCode,TargetPath,
              CreateFolderName,RequestedAccountName,MappedAccountId,Note,Disposition,DuplicateOfStagedRowId,ValidationMessage,
              PrecheckStatus,PrecheckErrorCode,PrecheckedAt,UpdatedAt)
            VALUES($id,$import,$row,'https://staged.invalid',$share,'code','/target','folder','Bravo',$account,$note,
              $disposition,$duplicate,'safe validation','Succeeded',NULL,$at,$at);
            """, ("$id", Id(id)), ("$import", Id(import)), ("$row", rowNumber), ("$share", $"staged-{id:N}"),
            ("$account", Id(B)), ("$note", note), ("$disposition", duplicateOf is null ? "Ready" : "Duplicate"),
            ("$duplicate", duplicateOf is null ? null : Id(duplicateOf.Value)), ("$at", "2026-08-20T04:15:00Z"));

    private static async Task SeedGraphAsync(SqliteConnectionFactory factory, Graph g)
    {
        await ExecuteAsync(factory, """
            INSERT INTO ImportSessions(Id,SourceType,SourceName,Status,TotalRows,ReadyRows,InvalidRows,DuplicateRows,ExcludedRows,PrecheckAccountId,CreatedAt,UpdatedAt)
            VALUES($id,'Excel','safe.xlsx','BatchCreated',5,5,0,0,0,$account,$at,$at);
            """, ("$id", Id(g.Import)), ("$account", Id(B)), ("$at", "2026-08-20T04:00:00Z"));
        await InsertBatchAsync(factory, g.SharedBatch, "Completed", g.Import, 3, 2);
        await InsertBatchAsync(factory, g.BBatch, "Completed", g.Import, 2, 1);
        await InsertBatchAsync(factory, g.ConstraintBatch, "Cancelled", g.Import, 1, 0);
        await ExecuteAsync(factory, "UPDATE ImportSessions SET CreatedBatchJobId=$only,AppendToBatchJobId=$shared WHERE Id=$id;", ("$only", Id(g.BBatch)), ("$shared", Id(g.SharedBatch)), ("$id", Id(g.Import)));
        await InsertAllocationAsync(factory, g.BAllocation, g.SharedBatch, B, "/bravo");
        await InsertAllocationAsync(factory, Guid.NewGuid(), g.SharedBatch, A, "/alpha");
        await InsertAllocationAsync(factory, Guid.NewGuid(), g.BBatch, B, "/bravo-only");
        await InsertResourceAsync(factory, g.BResource, g.SharedBatch, 1, B, "Expanded", "https://shared-b.invalid");
        await InsertResourceAsync(factory, g.AResource, g.SharedBatch, 2, A, "Expanded", "https://shared-a.invalid");
        await InsertResourceAsync(factory, g.SharedPublic, g.SharedBatch, 3, null, "Unallocated", "https://shared-public.invalid");
        await InsertResourceAsync(factory, g.BOnlyResource, g.BBatch, 1, B, "Expanded", "https://only-b.invalid");
        await InsertResourceAsync(factory, g.BOnlyPublic, g.BBatch, 2, null, "Unallocated", "https://only-public.invalid");
        await InsertResourceAsync(factory, g.Constrained, g.ConstraintBatch, 1, B, "Unused", "https://constraint.invalid");
        await InsertOperationAsync(factory, g.BOperation, g.BResource, g.SharedBatch, B, 1, "Succeeded");
        await InsertOperationAsync(factory, Guid.NewGuid(), g.AResource, g.SharedBatch, A, 2, "Succeeded");
        await InsertOperationAsync(factory, g.BOnlyOperation, g.BOnlyResource, g.BBatch, B, 1, "Failed");
    }

    private static Task InsertBatchAsync(SqliteConnectionFactory f, Guid id, string status, Guid? import, int resources, int operations) => ExecuteAsync(f, """
        INSERT INTO BatchJobs(Id,Name,SourceType,TransferMode,QuotaBasis,ShortagePolicy,ResourceCount,OperationCount,DefaultTargetPath,
          FolderCreationMode,DuplicatePolicy,ValidationMode,Status,CreatedAt,StartedAt,CompletedAt,ImportSessionId,QueueOrder,NextAllocationOrdinal)
        VALUES($id,'batch','Excel','Distribution','SuccessTarget','WaitForCandidates',$resources,$operations,'/default','PerItem','Skip','Strict',$status,
          $at,$at,$completed,$import,1,20);
        """, ("$id", Id(id)), ("$resources", resources), ("$operations", operations), ("$status", status), ("$at", "2026-08-20T04:00:00Z"),
        ("$completed", status is "Completed" or "CompletedWithShortfall" or "Cancelled" ? "2026-08-20T06:00:00Z" : null), ("$import", import is null ? null : Id(import.Value)));

    private static Task InsertAllocationAsync(SqliteConnectionFactory f, Guid id, Guid batch, Guid account, string path) => ExecuteAsync(f, """
        INSERT INTO AccountAllocations(Id,BatchJobId,AccountId,PriorityOrder,DefaultTargetPath,NormalizedDefaultTargetPath,TargetItemCount,
          TargetSuccessCount,EffectiveTargetSuccessCount,AssignedItemCount,AttemptedItemCount,NewSuccessItemCount,AlreadySatisfiedItemCount,
          SuccessItemCount,FailedItemCount,LockedItemCount,ExclusiveCandidateCount,UnusedCandidateCount,ReservedSlotCount,FulfilledCount)
        VALUES($id,$batch,$account,1,$path,$path,3,2,2,1,1,1,0,1,0,0,1,1,0,1);
        """, ("$id", Id(id)), ("$batch", Id(batch)), ("$account", Id(account)), ("$path", path));

    private static Task InsertResourceAsync(SqliteConnectionFactory f, Guid id, Guid batch, int sequence, Guid? constraint, string status, string url) => ExecuteAsync(f, """
        INSERT INTO ResourceItems(Id,BatchJobId,StagedImportRowId,CandidateSequence,ShareUrl,NormalizedShareId,ExtractionCode,Note,TargetPathOverride,
          NormalizedTargetPathOverride,CreateFolderName,FolderCreationModeOverride,GlobalValidityStatus,CandidateConstraintAccountId,CandidateStatus,
          ReservedAccountId,ReservationExpiresAt,CreatedAt,UpdatedAt)
        VALUES($id,$batch,NULL,$sequence,$url,$normalized,'code','note',NULL,NULL,NULL,'Inherit','Valid',$constraint,$status,NULL,NULL,$at,$at);
        """, ("$id", Id(id)), ("$batch", Id(batch)), ("$sequence", sequence), ("$url", url), ("$normalized", $"share-{id:N}"),
        ("$constraint", constraint is null ? null : Id(constraint.Value)), ("$status", status), ("$at", "2026-08-20T04:30:00Z"));

    private static Task InsertOperationAsync(SqliteConnectionFactory f, Guid id, Guid resource, Guid batch, Guid account, int sequence, string status)
    {
        var terminal = status is "Succeeded" or "Failed" or "Cancelled";
        var completedSubmission = status is "Succeeded" or "Failed";
        return ExecuteAsync(f, """
            INSERT INTO TransferOperations(Id,ResourceItemId,BatchJobId,SequenceNumber,AllocationOrdinal,TargetPath,NormalizedTargetPath,CreateFolderName,
              AssignedAccountId,AllocationSource,Status,OutcomeKind,AttemptCount,OperationFingerprint,ConfirmationDescriptorJson,AdapterRequestId,
              RemoteItemIdsJson,SubmitState,TargetSlotState,TargetSlotReservedAt,DailyCountedAt,SubmitStartedAt,LastErrorCode,LastErrorMessage,
              LeaseExpiresAt,CreatedAt,UpdatedAt)
            VALUES($id,$resource,$batch,$sequence,$sequence,'/target','/target','',$account,'Fixed',$status,$outcome,1,$fingerprint,$descriptor,
              NULL,NULL,$submit,$slot,NULL,$daily,NULL,NULL,NULL,NULL,$at,$at);
            """, ("$id", Id(id)), ("$resource", Id(resource)), ("$batch", Id(batch)), ("$sequence", sequence), ("$account", Id(account)),
            ("$status", status), ("$outcome", status == "Succeeded" ? "NewTransfer" : terminal ? status : null), ("$fingerprint", $"fp-{id:N}"),
            ("$descriptor", completedSubmission ? "{}" : null), ("$submit", completedSubmission ? "ResultReceived" : "NotPrepared"),
            ("$slot", status == "Succeeded" ? "Fulfilled" : terminal ? "Released" : "Reserved"),
            ("$daily", status is "Succeeded" or "Failed" ? "2026-08-20T05:15:00Z" : null), ("$at", "2026-08-20T05:30:00Z"));
    }

    private static Task InsertLogAsync(
        SqliteConnectionFactory factory,
        Guid id,
        Guid? batch,
        Guid? resource,
        Guid? operation,
        Guid? account) => ExecuteAsync(factory, """
            INSERT INTO ExecutionLogs(Id,BatchJobId,ResourceItemId,TransferOperationId,AccountId,Stage,Level,EventCode,SanitizedMessage,CreatedAt)
            VALUES($id,$batch,$resource,$operation,$account,'Transfer','Information','TEST','safe audit',$at);
            """, ("$id", Id(id)), ("$batch", batch is null ? null : Id(batch.Value)),
            ("$resource", resource is null ? null : Id(resource.Value)), ("$operation", operation is null ? null : Id(operation.Value)),
            ("$account", account is null ? null : Id(account.Value)), ("$at", "2026-08-20T05:45:00Z"));

    private static async Task SeedRepresentativeSharedHistoryAsync(
        SqliteConnectionFactory factory,
        Guid batch,
        int itemsPerAccount)
    {
        await using var connection = await factory.OpenConnectionAsync();
        await using var transaction = connection.BeginTransaction(deferred: false);
        for (var i = 0; i < itemsPerAccount; i++)
        {
            foreach (var account in new[] { A, B })
            {
                var resource = Guid.NewGuid();
                var operation = Guid.NewGuid();
                var log = Guid.NewGuid();
                var sequence = 10_000 + (i * 2) + (account == A ? 0 : 1);

                await using (var insertResource = connection.CreateCommand())
                {
                    insertResource.Transaction = transaction;
                    insertResource.CommandText = """
                        INSERT INTO ResourceItems(Id,BatchJobId,StagedImportRowId,CandidateSequence,ShareUrl,NormalizedShareId,
                          ExtractionCode,Note,TargetPathOverride,NormalizedTargetPathOverride,CreateFolderName,
                          FolderCreationModeOverride,GlobalValidityStatus,CandidateConstraintAccountId,CandidateStatus,
                          ReservedAccountId,ReservationExpiresAt,CreatedAt,UpdatedAt)
                        VALUES($id,$batch,NULL,$sequence,$url,$normalized,'code','benchmark',NULL,NULL,NULL,
                          'Inherit','Valid',$account,'Expanded',NULL,NULL,$at,$at);
                        """;
                    insertResource.Parameters.AddWithValue("$id", Id(resource));
                    insertResource.Parameters.AddWithValue("$batch", Id(batch));
                    insertResource.Parameters.AddWithValue("$sequence", sequence);
                    insertResource.Parameters.AddWithValue("$url", $"https://benchmark.invalid/{resource:N}");
                    insertResource.Parameters.AddWithValue("$normalized", $"benchmark-{resource:N}");
                    insertResource.Parameters.AddWithValue("$account", Id(account));
                    insertResource.Parameters.AddWithValue("$at", "2026-08-20T05:00:00Z");
                    await insertResource.ExecuteNonQueryAsync();
                }

                await using (var insertOperation = connection.CreateCommand())
                {
                    insertOperation.Transaction = transaction;
                    insertOperation.CommandText = """
                        INSERT INTO TransferOperations(Id,ResourceItemId,BatchJobId,SequenceNumber,AllocationOrdinal,
                          TargetPath,NormalizedTargetPath,CreateFolderName,AssignedAccountId,AllocationSource,Status,
                          OutcomeKind,AttemptCount,OperationFingerprint,ConfirmationDescriptorJson,AdapterRequestId,
                          RemoteItemIdsJson,SubmitState,TargetSlotState,TargetSlotReservedAt,DailyCountedAt,SubmitStartedAt,
                          LastErrorCode,LastErrorMessage,LeaseExpiresAt,CreatedAt,UpdatedAt)
                        VALUES($id,$resource,$batch,$sequence,$sequence,'/target','/target','',$account,'Fixed','Succeeded',
                          'NewTransfer',1,$fingerprint,'{}',NULL,NULL,'ResultReceived','Fulfilled',NULL,$at,NULL,
                          NULL,NULL,NULL,$at,$at);
                        """;
                    insertOperation.Parameters.AddWithValue("$id", Id(operation));
                    insertOperation.Parameters.AddWithValue("$resource", Id(resource));
                    insertOperation.Parameters.AddWithValue("$batch", Id(batch));
                    insertOperation.Parameters.AddWithValue("$sequence", sequence);
                    insertOperation.Parameters.AddWithValue("$account", Id(account));
                    insertOperation.Parameters.AddWithValue("$fingerprint", $"benchmark-{operation:N}");
                    insertOperation.Parameters.AddWithValue("$at", "2026-08-20T05:15:00Z");
                    await insertOperation.ExecuteNonQueryAsync();
                }

                await using var insertLog = connection.CreateCommand();
                insertLog.Transaction = transaction;
                insertLog.CommandText = """
                    INSERT INTO ExecutionLogs(Id,BatchJobId,ResourceItemId,TransferOperationId,AccountId,
                      Stage,Level,EventCode,SanitizedMessage,CreatedAt)
                    VALUES($id,$batch,$resource,$operation,$account,'Transfer','Information','BENCHMARK','safe audit',$at);
                    """;
                insertLog.Parameters.AddWithValue("$id", Id(log));
                insertLog.Parameters.AddWithValue("$batch", Id(batch));
                insertLog.Parameters.AddWithValue("$resource", Id(resource));
                insertLog.Parameters.AddWithValue("$operation", Id(operation));
                insertLog.Parameters.AddWithValue("$account", Id(account));
                insertLog.Parameters.AddWithValue("$at", "2026-08-20T05:30:00Z");
                await insertLog.ExecuteNonQueryAsync();
            }
        }
        await transaction.CommitAsync();
    }

    private static async Task ExecuteAsync(SqliteConnectionFactory f, string sql, params (string Name, object? Value)[] values)
    {
        await using var c = await f.OpenConnectionAsync();
        await using var command = c.CreateCommand();
        command.CommandText = sql;
        foreach (var x in values) command.Parameters.AddWithValue(x.Name, x.Value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(SqliteConnectionFactory f, string sql, params (string Name, object? Value)[] values)
    {
        await using var c = await f.OpenConnectionAsync();
        await using var command = c.CreateCommand();
        command.CommandText = sql;
        foreach (var x in values) command.Parameters.AddWithValue(x.Name, x.Value ?? DBNull.Value);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<AccountState> ReadAccountAsync(SqliteConnectionFactory f, Guid id)
    {
        await using var c = await f.OpenConnectionAsync();
        await using var command = c.CreateCommand();
        command.CommandText = "SELECT Status,IsEnabled,EncryptedSession,LastValidatedAt,IsDeleted,DeletedAt,WebView2ProfileDirectory FROM Accounts WHERE Id=$id;";
        command.Parameters.AddWithValue("$id", Id(id));
        await using var r = await command.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        return new(r.GetString(0), r.GetInt64(1) != 0, r.IsDBNull(2) ? null : Convert.ToHexString((byte[])r.GetValue(2)), r.IsDBNull(3) ? null : r.GetString(3),
            r.GetInt64(4) != 0, r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6));
    }

    private static async Task<string> ReadAllPersistentDataAsync(SqliteConnectionFactory factory)
    {
        await using var connection = await factory.OpenConnectionAsync();
        var tables = new List<string>();
        await using (var names = connection.CreateCommand())
        {
            names.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
            await using var reader = await names.ExecuteReaderAsync();
            while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
        }
        var snapshot = new StringBuilder();
        foreach (var table in tables)
        {
            snapshot.Append("T:").Append(table).AppendLine();
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM \"{table.Replace("\"", "\"\"")}\" ORDER BY rowid;";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
                {
                    if (reader.IsDBNull(ordinal)) snapshot.Append("N;");
                    else if (reader.GetValue(ordinal) is byte[] bytes) snapshot.Append("B:").Append(Convert.ToHexString(bytes)).Append(';');
                    else snapshot.Append("V:").Append(Convert.ToString(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture)).Append(';');
                }
                snapshot.AppendLine();
            }
        }
        return snapshot.ToString();
    }

    private static async Task<string> ReadHistoryDataAsync(SqliteConnectionFactory factory)
    {
        var snapshot = new StringBuilder();
        foreach (var table in new[] { "ImportSessions", "StagedImportRows", "BatchJobs", "ResourceItems", "TransferOperations", "AccountAllocations", "ExecutionLogs" })
        {
            snapshot.Append("T:").Append(table).AppendLine();
            snapshot.Append(await ReadRowsAsync(factory, $"SELECT * FROM {table} ORDER BY rowid;"));
        }
        return snapshot.ToString();
    }

    private static async Task<string> ReadRowsAsync(
        SqliteConnectionFactory factory,
        string sql,
        params (string Name, object? Value)[] values)
    {
        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var value in values) command.Parameters.AddWithValue(value.Name, value.Value ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync();
        var snapshot = new StringBuilder();
        while (await reader.ReadAsync())
        {
            for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
            {
                if (reader.IsDBNull(ordinal)) snapshot.Append("N;");
                else if (reader.GetValue(ordinal) is byte[] bytes) snapshot.Append("B:").Append(Convert.ToHexString(bytes)).Append(';');
                else snapshot.Append("V:").Append(Convert.ToString(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture)).Append(';');
            }
            snapshot.AppendLine();
        }
        return snapshot.ToString();
    }

    private static async Task AssertUnchangedAsync(SqliteConnectionFactory f)
    {
        var state = await ReadAccountAsync(f, B);
        Assert.Equal("Active", state.Status);
        Assert.True(state.Enabled);
        Assert.Equal(Convert.ToHexString(Encoding.UTF8.GetBytes("cookie-bravo")), state.SessionHex);
        Assert.NotNull(state.ValidatedAt);
        Assert.False(state.Deleted);
    }

    private static async Task<string> ScalarTextAsync(
        SqliteConnectionFactory f,
        string sql,
        params (string Name, object? Value)[] values)
    {
        await using var c = await f.OpenConnectionAsync();
        await using var command = c.CreateCommand();
        command.CommandText = sql;
        foreach (var value in values)
            command.Parameters.AddWithValue(value.Name, value.Value ?? DBNull.Value);
        return Convert.ToString(await command.ExecuteScalarAsync()) ?? string.Empty;
    }

    private static string Id(Guid id) => id.ToString("D");
    private static async Task PreparePendingAsync(
        SqliteAccountRemovalStore store,
        Guid accountId)
    {
        var preview = await store.PreviewAsync(accountId, AccountExitHistoryMode.KeepHistory, default);
        Assert.Equal(AccountExitStatus.Ready, preview.Status);
        var result = await store.PrepareRemovalAsync(
            new(accountId, AccountExitHistoryMode.KeepHistory, preview.ConfirmationToken, RemovedAt),
            default);
        Assert.Equal(AccountRemovalPreparationStatus.Prepared, result.Status);
    }

    private static Task CompleteAsync(
        SqliteAccountRemovalStore store,
        Guid accountId,
        DateTimeOffset removedAt) =>
        store.CompletePreparedRemovalAsync(
            accountId,
            removedAt,
            static (_, _, _) => Task.CompletedTask,
            default);

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    private sealed class RetryProfileStore : IWebViewProfileStore
    {
        public int DeleteFailuresRemaining { get; set; }
        public int DeleteCount { get; private set; }

        public Task CloseAsync(Guid accountId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteAsync(Guid accountId, string exactProfilePath, CancellationToken cancellationToken)
        {
            DeleteCount++;
            if (DeleteFailuresRemaining-- > 0) throw new IOException("profile-locked");
            return Task.CompletedTask;
        }

        public Task RegisterOwnershipAsync(Guid accountId, string exactProfilePath, Guid ownershipToken, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> CommitOwnedAsync(Guid accountId, string exactProfilePath, Guid ownershipToken, Func<CancellationToken, Task> commitAction, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<OwnedProfileDeletionStatus> DeleteOwnedAsync(Guid accountId, string exactProfilePath, Guid ownershipToken, Func<CancellationToken, Task<bool>> confirmNoActiveAccount, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingPathPolicy : IAccountProfilePathPolicy
    {
        public string? LastPath { get; private set; }
        public string GetAccountProfilePath(Guid accountId) => throw new NotSupportedException();
        public void EnsureSafeAccountPath(Guid accountId, string candidatePath) => LastPath = candidatePath;
    }

    private sealed record AccountState(string Status, bool Enabled, string? SessionHex, string? ValidatedAt, bool Deleted, string? DeletedAt, string? Profile);
    private sealed record Graph(Guid Import, Guid SharedBatch, Guid BBatch, Guid ConstraintBatch, Guid BAllocation, Guid BResource, Guid AResource,
        Guid SharedPublic, Guid BOnlyResource, Guid BOnlyPublic, Guid Constrained, Guid BOperation, Guid BOnlyOperation)
    {
        public static Graph New() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    }
}
