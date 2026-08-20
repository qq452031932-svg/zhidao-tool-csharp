using BaiduBatchTransfer.Application.Accounts;
using BaiduBatchTransfer.Infrastructure.Database;

namespace BaiduBatchTransfer.Infrastructure.Tests.Database;

public sealed class AccountRemovalStoreTests : IDisposable
{
    private static readonly Guid A = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid B = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid C = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly DateTimeOffset RemovedAt = new(2026, 8, 20, 12, 34, 56, TimeSpan.Zero);
    private readonly string root = Path.Combine(Path.GetTempPath(), $"bbt-account-removal-{Guid.NewGuid():N}");

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public async Task AccountPagesRejectUnsafeSizes(int pageSize)
    {
        var (factory, store, _) = await CreateAsync();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.GetAccountsPageAsync(1, pageSize, default));
        Assert.Equal(3, await ScalarAsync(factory, "SELECT COUNT(*) FROM Accounts;"));
    }

    [Fact]
    public async Task AccountPagesOmitTombstonesAndProjectSafeFields()
    {
        var (_, store, _) = await CreateAsync();
        var page = await store.GetAccountsPageAsync(1, 2, default);
        Assert.Equal(2, page.TotalCount);
        Assert.Equal(new[] { "Alpha", "Bravo" }, page.Items.Select(x => x.DisplayName));
        Assert.All(page.Items, x =>
        {
            Assert.DoesNotContain("baidu-", x.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("cookie", x.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(root, x.ToString(), StringComparison.OrdinalIgnoreCase);
        });
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

    [Fact]
    public async Task TokenCannotApplyToAnotherLocalAccount()
    {
        var (factory, store, _) = await CreateAsync(seedGraph: true);
        var token = (await store.PreviewAsync(A, AccountExitHistoryMode.KeepHistory, default)).ConfirmationToken;
        var result = await store.PrepareRemovalAsync(new(B, AccountExitHistoryMode.KeepHistory, token, RemovedAt), default);
        Assert.Equal(AccountRemovalPreparationStatus.StaleConfirmation, result.Status);
        await AssertUnchangedAsync(factory);
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
        Assert.Equal(1, preview.SharedBatchCount);
        Assert.Equal(64, preview.ConfirmationToken.Length);
        Assert.DoesNotContain(Id(B), preview.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(preview.ConfirmationToken, preview.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task KeepHistoryAtomicallyDisablesOnlyBAndClearsCredentials()
    {
        var (factory, store, _) = await CreateAsync(seedGraph: true);
        var beforeA = await ReadAccountAsync(factory, A);
        var beforeC = await ReadAccountAsync(factory, C);
        var preview = await store.PreviewAsync(B, AccountExitHistoryMode.KeepHistory, default);
        var result = await store.PrepareRemovalAsync(new(B, AccountExitHistoryMode.KeepHistory, preview.ConfirmationToken, RemovedAt), default);
        Assert.Equal(AccountRemovalPreparationStatus.Prepared, result.Status);
        Assert.True(result.DatabasePrepared);
        Assert.Equal(Path.Combine(root, "profiles", "bravo"), result.ProfilePath);
        Assert.Equal(beforeA, await ReadAccountAsync(factory, A));
        Assert.Equal(beforeC, await ReadAccountAsync(factory, C));
        var b = await ReadAccountAsync(factory, B);
        Assert.Equal("Disabled", b.Status);
        Assert.False(b.Enabled);
        Assert.Null(b.Session);
        Assert.Null(b.ValidatedAt);
        Assert.Equal(2, await ScalarAsync(factory, "SELECT COUNT(*) FROM TransferOperations WHERE AssignedAccountId=$id;", ("$id", Id(B))));
        Assert.Equal(0, await ScalarAsync(factory, "SELECT COUNT(*) FROM pragma_foreign_key_check;"));
    }

    [Fact]
    public async Task RemoveTerminalHistoryFailsClosedWithoutDisabling()
    {
        var (factory, store, _) = await CreateAsync(seedGraph: true);
        var preview = await store.PreviewAsync(B, AccountExitHistoryMode.RemoveTerminalHistory, default);
        var before = await ReadAccountAsync(factory, B);
        var result = await store.PrepareRemovalAsync(new(B, AccountExitHistoryMode.RemoveTerminalHistory, preview.ConfirmationToken, RemovedAt), default);
        Assert.Equal(AccountRemovalPreparationStatus.Failed, result.Status);
        Assert.False(result.DatabasePrepared);
        Assert.Equal(before, await ReadAccountAsync(factory, B));
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
    }

    [Fact]
    public async Task FinalizeTombstoneIsConditionalAndIdempotent()
    {
        var (factory, store, _) = await CreateAsync();
        await store.FinalizeTombstoneAsync(B, RemovedAt, default);
        await AssertUnchangedAsync(factory);
        await ExecuteAsync(factory, "UPDATE Accounts SET Status='Disabled',IsEnabled=0,EncryptedSession=NULL,LastValidatedAt=NULL WHERE Id=$id;", ("$id", Id(B)));
        await store.FinalizeTombstoneAsync(B, RemovedAt, default);
        await store.FinalizeTombstoneAsync(B, RemovedAt.AddHours(1), default);
        var state = await ReadAccountAsync(factory, B);
        Assert.Equal("Deleted", state.Status);
        Assert.True(state.Deleted);
        Assert.Equal(RemovedAt.ToString("O"), state.DeletedAt);
        Assert.Null(state.Profile);
    }

    [Fact]
    public async Task FinalizeRefusesReenabledOrCredentialBearingRows()
    {
        var (factory, store, _) = await CreateAsync();
        await ExecuteAsync(factory, "UPDATE Accounts SET Status='Disabled',IsEnabled=1 WHERE Id=$id;", ("$id", Id(B)));
        await store.FinalizeTombstoneAsync(B, RemovedAt, default);
        Assert.False((await ReadAccountAsync(factory, B)).Deleted);
        await ExecuteAsync(factory, "UPDATE Accounts SET IsEnabled=0,EncryptedSession='credential' WHERE Id=$id;", ("$id", Id(B)));
        await store.FinalizeTombstoneAsync(B, RemovedAt, default);
        Assert.False((await ReadAccountAsync(factory, B)).Deleted);
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
        await ExecuteAsync(factory, sql, ("$id", Id(A)), ("$baidu", "baidu-alpha"), ("$name", "Alpha"), ("$session", "cookie-alpha"), ("$profile", Path.Combine(root, "profiles", "alpha")), ("$sort", 1), ("$at", "2026-08-20T01:00:00Z"));
        await ExecuteAsync(factory, sql, ("$id", Id(B)), ("$baidu", "baidu-bravo"), ("$name", "Bravo"), ("$session", "cookie-bravo"), ("$profile", Path.Combine(root, "profiles", "bravo")), ("$sort", 2), ("$at", "2026-08-20T02:00:00Z"));
        await ExecuteAsync(factory, sql, ("$id", Id(C)), ("$baidu", "baidu-charlie"), ("$name", "Charlie"), ("$session", "cookie-charlie"), ("$profile", Path.Combine(root, "profiles", "charlie")), ("$sort", 3), ("$at", "2026-08-20T03:00:00Z"));
        await ExecuteAsync(factory, "UPDATE Accounts SET Status='Deleted',IsEnabled=0,IsDeleted=1,EncryptedSession=NULL,DeletedAt='2026-08-19T00:00:00Z' WHERE Id=$id;", ("$id", Id(C)));
    }

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
        return ExecuteAsync(f, """
            INSERT INTO TransferOperations(Id,ResourceItemId,BatchJobId,SequenceNumber,AllocationOrdinal,TargetPath,NormalizedTargetPath,CreateFolderName,
              AssignedAccountId,AllocationSource,Status,OutcomeKind,AttemptCount,OperationFingerprint,ConfirmationDescriptorJson,AdapterRequestId,
              RemoteItemIdsJson,SubmitState,TargetSlotState,TargetSlotReservedAt,DailyCountedAt,SubmitStartedAt,LastErrorCode,LastErrorMessage,
              LeaseExpiresAt,CreatedAt,UpdatedAt)
            VALUES($id,$resource,$batch,$sequence,$sequence,'/target','/target','',$account,'Fixed',$status,$outcome,1,$fingerprint,$descriptor,
              NULL,NULL,$submit,$slot,NULL,NULL,NULL,NULL,NULL,NULL,$at,$at);
            """, ("$id", Id(id)), ("$resource", Id(resource)), ("$batch", Id(batch)), ("$sequence", sequence), ("$account", Id(account)),
            ("$status", status), ("$outcome", status == "Succeeded" ? "NewTransfer" : terminal ? status : null), ("$fingerprint", $"fp-{id:N}"),
            ("$descriptor", terminal ? "{}" : null), ("$submit", terminal ? "ResultReceived" : "NotPrepared"),
            ("$slot", status == "Succeeded" ? "Fulfilled" : terminal ? "Released" : "Reserved"), ("$at", "2026-08-20T05:30:00Z"));
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
        return new(r.GetString(0), r.GetInt64(1) != 0, r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
            r.GetInt64(4) != 0, r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6));
    }

    private static async Task AssertUnchangedAsync(SqliteConnectionFactory f)
    {
        var state = await ReadAccountAsync(f, B);
        Assert.Equal("Active", state.Status);
        Assert.True(state.Enabled);
        Assert.Equal("cookie-bravo", state.Session);
        Assert.NotNull(state.ValidatedAt);
        Assert.False(state.Deleted);
    }

    private static string Id(Guid id) => id.ToString("N");
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private sealed record AccountState(string Status, bool Enabled, string? Session, string? ValidatedAt, bool Deleted, string? DeletedAt, string? Profile);
    private sealed record Graph(Guid Import, Guid SharedBatch, Guid BBatch, Guid ConstraintBatch, Guid BAllocation, Guid BResource, Guid AResource,
        Guid SharedPublic, Guid BOnlyResource, Guid BOnlyPublic, Guid Constrained, Guid BOperation, Guid BOnlyOperation)
    {
        public static Graph New() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    }
}
