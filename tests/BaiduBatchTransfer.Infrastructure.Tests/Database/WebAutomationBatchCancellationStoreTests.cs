using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Infrastructure.Database;

namespace BaiduBatchTransfer.Infrastructure.Tests.Database;

public sealed class WebAutomationBatchCancellationStoreTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), $"bbt-web-batch-cancel-{Guid.NewGuid():N}");

    [Fact]
    public async Task TransferCancellationAtomicallyStopsSafeWorkAndAttempt()
    {
        var graph = await CreateTransferGraphAsync(attemptCount: 2);
        var attempts = new SqliteWebAutomationAttemptStore(graph.Factory);
        Assert.True(await attempts.BeginAsync(WebAutomationAttemptRecord.Create(
            graph.OperationId, graph.AccountId, 2, WebAutomationOwnerKind.Transfer, graph.Now)));
        var handoff = await EnterHandoffAsync(
            attempts, graph, 2, WebAutomationAttemptState.Preparing);
        var store = new SqliteWebAutomationBatchCancellationStore(graph.Factory);

        Assert.Equal(WebAutomationBatchCancellationDisposition.StaleHandoff, await store.CancelAsync(
            graph.OperationId, graph.BatchId, graph.AccountId, 2,
            WebAutomationOwnerKind.Transfer, handoff.Revision - 1, handoff.ResumeStage!.Value,
            graph.Now.AddSeconds(30)));
        Assert.Equal("Active", await ScalarTextAsync(
            graph.Factory, "SELECT Status FROM BatchJobs WHERE Id=$id;", graph.BatchId));

        var result = await store.CancelAsync(
            graph.OperationId, graph.BatchId, graph.AccountId, 2,
            WebAutomationOwnerKind.Transfer, handoff.Revision, handoff.ResumeStage!.Value,
            graph.Now.AddMinutes(1));

        Assert.Equal(WebAutomationBatchCancellationDisposition.Cancelled, result);
        Assert.True(await store.IsCancelledAsync(
            graph.BatchId, WebAutomationOwnerKind.Transfer));
        Assert.Equal("Cancelled", await ScalarTextAsync(
            graph.Factory, "SELECT Status FROM BatchJobs WHERE Id=$id;", graph.BatchId));
        Assert.Equal("Cancelled", await ScalarTextAsync(
            graph.Factory, "SELECT Status FROM TransferOperations WHERE Id=$id;", graph.OperationId));
        Assert.Equal(WebAutomationAttemptState.DefinitelyNotSubmitted,
            (await attempts.GetAsync(graph.OperationId, graph.AccountId, 2))!.State);
    }

    [Fact]
    public async Task DuplicateCancellationIsIdempotentAndSurvivesStoreRestart()
    {
        var graph = await CreateTransferGraphAsync(attemptCount: 2);
        var attempts = new SqliteWebAutomationAttemptStore(graph.Factory);
        Assert.True(await attempts.BeginAsync(WebAutomationAttemptRecord.Create(
            graph.OperationId, graph.AccountId, 2, WebAutomationOwnerKind.Transfer, graph.Now)));
        var handoff = await EnterHandoffAsync(
            attempts, graph, 2, WebAutomationAttemptState.Preparing);
        var store = new SqliteWebAutomationBatchCancellationStore(graph.Factory);

        Assert.Equal(WebAutomationBatchCancellationDisposition.Cancelled, await store.CancelAsync(
            graph.OperationId, graph.BatchId, graph.AccountId, 2,
            WebAutomationOwnerKind.Transfer, handoff.Revision, handoff.ResumeStage!.Value, graph.Now));
        Assert.Equal(WebAutomationBatchCancellationDisposition.AlreadyCancelled, await store.CancelAsync(
            graph.OperationId, graph.BatchId, graph.AccountId, 2,
            WebAutomationOwnerKind.Transfer, handoff.Revision, handoff.ResumeStage!.Value,
            graph.Now.AddMinutes(1)));

        var restarted = new SqliteWebAutomationBatchCancellationStore(graph.Factory);
        Assert.True(await restarted.IsCancelledAsync(graph.BatchId, WebAutomationOwnerKind.TransferShare));
    }

    [Fact]
    public async Task TerminalOrWrongBatchCannotBeCancelled()
    {
        var graph = await CreateTransferGraphAsync(attemptCount: 2);
        var store = new SqliteWebAutomationBatchCancellationStore(graph.Factory);

        Assert.Equal(WebAutomationBatchCancellationDisposition.OwnershipMismatch, await store.CancelAsync(
            graph.OperationId, Guid.NewGuid(), graph.AccountId, 2,
            WebAutomationOwnerKind.Transfer, 0, WebAutomationAttemptState.Preparing, graph.Now));
        await ExecuteAsync(graph.Factory,
            "UPDATE BatchJobs SET Status='Completed',CompletedAt=$now WHERE Id=$id;",
            graph.BatchId, graph.Now.AddMinutes(1));
        Assert.Equal(WebAutomationBatchCancellationDisposition.Terminal, await store.CancelAsync(
            graph.OperationId, graph.BatchId, graph.AccountId, 2,
            WebAutomationOwnerKind.Transfer, 0, WebAutomationAttemptState.Preparing,
            graph.Now.AddMinutes(2)));

        Assert.Equal("Completed", await ScalarTextAsync(
            graph.Factory, "SELECT Status FROM BatchJobs WHERE Id=$id;", graph.BatchId));
        Assert.Equal("Preparing", await ScalarTextAsync(
            graph.Factory, "SELECT Status FROM TransferOperations WHERE Id=$id;", graph.OperationId));
    }

    [Fact]
    public async Task DatabaseFailureRollsBackBatchAndOperationTogether()
    {
        var graph = await CreateTransferGraphAsync(attemptCount: 2);
        var attempts = new SqliteWebAutomationAttemptStore(graph.Factory);
        Assert.True(await attempts.BeginAsync(WebAutomationAttemptRecord.Create(
            graph.OperationId, graph.AccountId, 2, WebAutomationOwnerKind.Transfer, graph.Now)));
        var handoff = await EnterHandoffAsync(
            attempts, graph, 2, WebAutomationAttemptState.Preparing);
        await using (var connection = await graph.Factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER RejectSyntheticCancellation
                BEFORE UPDATE OF Status ON TransferOperations
                WHEN NEW.Status='Cancelled'
                BEGIN
                    SELECT RAISE(ABORT, 'synthetic cancellation failure');
                END;
                """;
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAnyAsync<Exception>(() =>
            new SqliteWebAutomationBatchCancellationStore(graph.Factory).CancelAsync(
                graph.OperationId, graph.BatchId, graph.AccountId, 2,
                WebAutomationOwnerKind.Transfer, handoff.Revision, handoff.ResumeStage!.Value,
                graph.Now.AddMinutes(1)));

        Assert.Equal("Active", await ScalarTextAsync(
            graph.Factory, "SELECT Status FROM BatchJobs WHERE Id=$id;", graph.BatchId));
        Assert.Equal("Preparing", await ScalarTextAsync(
            graph.Factory, "SELECT Status FROM TransferOperations WHERE Id=$id;", graph.OperationId));
    }

    [Fact]
    public async Task UncertainTransferKeepsSubmissionEvidenceAndAttemptNeedsDecision()
    {
        var graph = await CreateTransferGraphAsync(attemptCount: 2);
        await ExecuteAsync(graph.Factory, """
            UPDATE TransferOperations
            SET Status='Submitting',SubmitState='Submitting',ConfirmationDescriptorJson='{}',
                AdapterRequestId='request-evidence',SubmitStartedAt=$now
            WHERE Id=$id;
            """, graph.OperationId, graph.Now);
        var attempts = new SqliteWebAutomationAttemptStore(graph.Factory);
        Assert.True(await attempts.BeginAsync(WebAutomationAttemptRecord.Create(
            graph.OperationId, graph.AccountId, 2, WebAutomationOwnerKind.Transfer, graph.Now)));
        Assert.True(await attempts.TryPersistBaselineAsync(
            graph.OperationId, graph.AccountId, 2, 0,
            WebAutomationBaseline.Create(["before"]), new string('a', 64), graph.Now.AddSeconds(1)));
        Assert.True(await attempts.TryAdvanceAsync(
            graph.OperationId, graph.AccountId, 2,
            WebAutomationAttemptState.BaselinePersisted, 1,
            WebAutomationAttemptState.Submitting, new string('b', 64), false,
            graph.Now.AddSeconds(2)));
        var handoff = await EnterHandoffAsync(
            attempts, graph, 2, WebAutomationAttemptState.Submitting);

        var result = await new SqliteWebAutomationBatchCancellationStore(graph.Factory).CancelAsync(
            graph.OperationId, graph.BatchId, graph.AccountId, 2,
            WebAutomationOwnerKind.Transfer, handoff.Revision, handoff.ResumeStage!.Value,
            graph.Now.AddMinutes(1));

        Assert.Equal(WebAutomationBatchCancellationDisposition.Cancelled, result);
        Assert.Equal("Submitting", await ScalarTextAsync(
            graph.Factory, "SELECT Status FROM TransferOperations WHERE Id=$id;", graph.OperationId));
        Assert.Equal("request-evidence", await ScalarTextAsync(
            graph.Factory, "SELECT AdapterRequestId FROM TransferOperations WHERE Id=$id;", graph.OperationId));
        Assert.Equal(WebAutomationAttemptState.NeedsDecision,
            (await attempts.GetAsync(graph.OperationId, graph.AccountId, 2))!.State);
    }

    [Fact]
    public async Task StandaloneCancellationStopsOnlyDefinitelyUnsubmittedWork()
    {
        var graph = await CreateStandaloneGraphAsync();
        var attempts = new SqliteWebAutomationAttemptStore(graph.Factory);
        Assert.True(await attempts.BeginAsync(WebAutomationAttemptRecord.Create(
            graph.OperationId, graph.AccountId, 1, WebAutomationOwnerKind.StandaloneShare, graph.Now)));
        var handoff = await EnterHandoffAsync(
            attempts, graph, 1, WebAutomationAttemptState.Preparing);

        var result = await new SqliteWebAutomationBatchCancellationStore(graph.Factory).CancelAsync(
            graph.OperationId, graph.BatchId, graph.AccountId, 1,
            WebAutomationOwnerKind.StandaloneShare, handoff.Revision, handoff.ResumeStage!.Value,
            graph.Now.AddMinutes(1));

        Assert.Equal(WebAutomationBatchCancellationDisposition.Cancelled, result);
        Assert.Equal("Cancelled", await ScalarTextAsync(
            graph.Factory, "SELECT Status FROM StandaloneShareBatches WHERE Id=$id;", graph.BatchId));
        Assert.Equal("Cancelled", await ScalarTextAsync(
            graph.Factory, "SELECT Status FROM StandaloneShareOperations WHERE Id=$id;", graph.OperationId));
        Assert.Equal(WebAutomationAttemptState.DefinitelyNotSubmitted,
            (await attempts.GetAsync(graph.OperationId, graph.AccountId, 1))!.State);
    }

    [Fact]
    public async Task StandaloneSubmittingWorkIsPreservedForReconciliation()
    {
        var graph = await CreateStandaloneGraphAsync();
        await ExecuteAsync(graph.Factory, """
            UPDATE StandaloneShareOperations
            SET Status='Submitting',SubmissionState='ResultPending',SubmissionAcceptance='Unknown'
            WHERE Id=$id;
            """, graph.OperationId, graph.Now);
        var attempts = new SqliteWebAutomationAttemptStore(graph.Factory);
        Assert.True(await attempts.BeginAsync(WebAutomationAttemptRecord.Create(
            graph.OperationId, graph.AccountId, 1, WebAutomationOwnerKind.StandaloneShare, graph.Now)));
        Assert.True(await attempts.TryPersistBaselineAsync(
            graph.OperationId, graph.AccountId, 1, 0,
            WebAutomationBaseline.Create(["before"]), new string('c', 64), graph.Now.AddSeconds(1)));
        Assert.True(await attempts.TryAdvanceAsync(
            graph.OperationId, graph.AccountId, 1,
            WebAutomationAttemptState.BaselinePersisted, 1,
            WebAutomationAttemptState.Submitting, new string('d', 64), false,
            graph.Now.AddSeconds(2)));
        var handoff = await EnterHandoffAsync(
            attempts, graph, 1, WebAutomationAttemptState.Submitting);

        var result = await new SqliteWebAutomationBatchCancellationStore(graph.Factory).CancelAsync(
            graph.OperationId, graph.BatchId, graph.AccountId, 1,
            WebAutomationOwnerKind.StandaloneShare, handoff.Revision, handoff.ResumeStage!.Value,
            graph.Now.AddMinutes(1));

        Assert.Equal(WebAutomationBatchCancellationDisposition.Cancelled, result);
        Assert.Equal("Submitting", await ScalarTextAsync(
            graph.Factory, "SELECT Status FROM StandaloneShareOperations WHERE Id=$id;", graph.OperationId));
        Assert.Equal("ResultPending", await ScalarTextAsync(
            graph.Factory, "SELECT SubmissionState FROM StandaloneShareOperations WHERE Id=$id;", graph.OperationId));
        Assert.Equal(WebAutomationAttemptState.NeedsDecision,
            (await attempts.GetAsync(graph.OperationId, graph.AccountId, 1))!.State);
    }

    private async Task<Graph> CreateTransferGraphAsync(int attemptCount)
    {
        Directory.CreateDirectory(root);
        var factory = new SqliteConnectionFactory(Path.Combine(root, "app.db"));
        await new DatabaseMigrator(factory,
            new DatabaseBackupService(factory, Path.Combine(root, "backups")), "test").MigrateAsync();
        var accountId = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        var resourceId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Accounts(Id,BaiduUserId,DisplayName,LoginMethod,Status,DailyLimit,DailyExecutedCount,
              SortOrder,IsEnabled,DailyCountDateKey,IsDeleted)
            VALUES($account,'owner','owner','WebView2','Active',0,0,0,1,'2026-08-25',0);
            INSERT INTO BatchJobs(Id,Name,SourceType,TransferMode,QuotaBasis,ShortagePolicy,ResourceCount,
              OperationCount,DefaultTargetPath,FolderCreationMode,DuplicatePolicy,ValidationMode,Status,CreatedAt,QueueOrder)
            VALUES($batch,'job','Text','Distribution','TaskCount','ProportionalReduction',1,1,'/','None','Skip',
              'Immediate','Active',$now,1);
            INSERT INTO ResourceItems(Id,BatchJobId,CandidateSequence,ShareUrl,NormalizedShareId,
              FolderCreationModeOverride,GlobalValidityStatus,CandidateStatus,CreatedAt,UpdatedAt)
            VALUES($resource,$batch,1,'https://pan.baidu.com/s/test','test','None','Valid','Expanded',$now,$now);
            INSERT INTO TransferOperations(Id,ResourceItemId,BatchJobId,SequenceNumber,AllocationOrdinal,TargetPath,
              NormalizedTargetPath,CreateFolderName,AssignedAccountId,AllocationSource,Status,AttemptCount,
              OperationFingerprint,SubmitState,TargetSlotState,CreatedAt,UpdatedAt)
            VALUES($operation,$resource,$batch,1,1,'/','/','',$account,'Automatic','Preparing',$attempt,
              'transfer-fp','PreparingToSubmit','Reserved',$now,$now);
            """;
        command.Parameters.AddWithValue("$account", accountId.ToString("D"));
        command.Parameters.AddWithValue("$batch", batchId.ToString("D"));
        command.Parameters.AddWithValue("$resource", resourceId.ToString("D"));
        command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        command.Parameters.AddWithValue("$attempt", attemptCount);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        await command.ExecuteNonQueryAsync();
        return new(factory, operationId, batchId, accountId, now);
    }

    private async Task<Graph> CreateStandaloneGraphAsync()
    {
        Directory.CreateDirectory(root);
        var factory = new SqliteConnectionFactory(Path.Combine(root, "standalone.db"));
        await new DatabaseMigrator(factory,
            new DatabaseBackupService(factory, Path.Combine(root, "backups-standalone")), "test").MigrateAsync();
        var accountId = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Accounts(Id,BaiduUserId,DisplayName,LoginMethod,Status,DailyLimit,DailyExecutedCount,
              SortOrder,IsEnabled,DailyCountDateKey,IsDeleted)
            VALUES($account,'owner','owner','WebView2','Active',0,0,0,1,'2026-08-25',0);
            INSERT INTO StandaloneShareBatches(Id,DraftFingerprint,ResultDirectoryReference,Status,OperationCount,
              Revision,CreatedAt,UpdatedAt)
            VALUES($batch,'draft-fp',$directory,'Active',1,0,$now,$now);
            INSERT INTO StandaloneShareOperations(Id,BatchId,AccountId,SequenceNumber,DisplayName,ResourceKind,
              ParentPath,ExactItemSetFingerprint,Generation,Status,SubmissionState,SubmissionAcceptance,
              LeaseGeneration,Revision,CreatedAt,UpdatedAt)
            VALUES($operation,$batch,$account,1,'item','File','/','exact-fp',1,'Preparing',
              'Prepared','Unknown',1,0,$now,$now);
            INSERT INTO StandaloneShareRemoteItems(OperationId,ItemOrdinal,RemoteItemId,CanonicalSortKey,DisplayName)
            VALUES($operation,0,'remote-id',X'01','item');
            """;
        command.Parameters.AddWithValue("$account", accountId.ToString("D"));
        command.Parameters.AddWithValue("$batch", batchId.ToString("D"));
        command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        command.Parameters.AddWithValue("$directory", Path.Combine(root, "results"));
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        await command.ExecuteNonQueryAsync();
        return new(factory, operationId, batchId, accountId, now);
    }

    private static async Task ExecuteAsync(
        SqliteConnectionFactory factory, string sql, Guid id, DateTimeOffset now)
    {
        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<WebAutomationAttemptRecord> EnterHandoffAsync(
        SqliteWebAutomationAttemptStore attempts,
        Graph graph,
        int generation,
        WebAutomationAttemptState resumeStage)
    {
        var current = (await attempts.GetAsync(
            graph.OperationId, graph.AccountId, generation))!;
        Assert.Equal(resumeStage, current.State);
        Assert.True(await attempts.TryAdvanceAsync(
            graph.OperationId, graph.AccountId, generation,
            resumeStage, current.Revision, WebAutomationAttemptState.UserHandoff,
            new string('e', 64), true, graph.Now.AddSeconds(5)));
        return (await attempts.GetAsync(graph.OperationId, graph.AccountId, generation))!;
    }

    private static async Task<string> ScalarTextAsync(
        SqliteConnectionFactory factory, string sql, Guid id)
    {
        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        return Convert.ToString(await command.ExecuteScalarAsync())!;
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private sealed record Graph(
        SqliteConnectionFactory Factory,
        Guid OperationId,
        Guid BatchId,
        Guid AccountId,
        DateTimeOffset Now);
}
