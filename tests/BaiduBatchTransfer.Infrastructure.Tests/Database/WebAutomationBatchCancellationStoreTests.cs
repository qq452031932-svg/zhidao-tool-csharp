using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
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
        var capability = new AdapterCapabilityVersion(
            "BaiduTransfer", "protocol-1", "selectors-1", "build-1");
        var source = new TransferSourceEvidence(
            new RemoteItemIdentitySet([new("source", null)]), 1, true, true,
            "source-generation", "baidu-user", capability, new string('a', 64));
        Assert.True(await attempts.TryPersistTransferSourceEvidenceAsync(
            graph.OperationId, graph.AccountId, 2, 0, source, graph.Now.AddSeconds(1)));
        var baseline = WebAutomationBaseline.Create(["before"]);
        var targetFingerprint = TransferTargetPreparationEvidence.ComputeObservationFingerprint(
            "target", new RemotePath("/target"), 1, true, true, "target-generation", "baidu-user",
            TransferTargetInventoryScope.AllItems, baseline);
        var target = new TransferTargetPreparationEvidence(
            "target", new RemotePath("/target"), 1, true, true, "target-generation", "baidu-user",
            TransferTargetInventoryScope.AllItems, baseline, targetFingerprint);
        Assert.True(await attempts.TryPersistTransferTargetPreparationAsync(
            graph.OperationId, graph.AccountId, 2, 1, target, graph.Now.AddSeconds(2)));
        var submission = TransferSubmissionEvidence.CreateBound(
            Guid.NewGuid(), new string('b', 64), source.Items.Fingerprint, target.TargetExactId,
            target.Baseline.Fingerprint, source.BaiduUserId, capability, graph.Now.AddSeconds(3));
        Assert.True(await attempts.TryPersistTransferSubmissionAsync(
            graph.OperationId, graph.AccountId, 2, 2, submission, graph.Now.AddSeconds(3)));
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
        var restored = await new SqliteWebAutomationAttemptStore(graph.Factory)
            .GetAsync(graph.OperationId, graph.AccountId, 2);
        Assert.Equal(WebAutomationAttemptState.NeedsDecision, restored!.State);
        Assert.Equal(submission, restored.SubmissionEvidence);
        Assert.True(restored.SubmissionEvidence!.IsLocallyBound);
    }

    [Fact]
    public async Task WebPreparingCannotOverrideTransferSubmissionEvidence()
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
        var handoff = await EnterHandoffAsync(
            attempts, graph, 2, WebAutomationAttemptState.Preparing);

        Assert.Equal(WebAutomationBatchCancellationDisposition.Cancelled,
            await new SqliteWebAutomationBatchCancellationStore(graph.Factory).CancelAsync(
                graph.OperationId, graph.BatchId, graph.AccountId, 2,
                WebAutomationOwnerKind.Transfer, handoff.Revision, handoff.ResumeStage!.Value,
                graph.Now.AddMinutes(1)));

        Assert.Equal(WebAutomationAttemptState.NeedsDecision,
            (await attempts.GetAsync(graph.OperationId, graph.AccountId, 2))!.State);
        Assert.Equal("request-evidence", await ScalarTextAsync(
            graph.Factory, "SELECT AdapterRequestId FROM TransferOperations WHERE Id=$id;", graph.OperationId));
    }

    [Fact]
    public async Task WebPreparingCannotOverrideTransferShareSubmissionEvidence()
    {
        var graph = await CreateTransferGraphAsync(attemptCount: 2);
        var shareOperationId = Guid.NewGuid();
        await using (var connection = await graph.Factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO BatchShareSettings(BatchJobId,IsPermanent,IsAccessUnlimited,ExtractionCodeMode,
                  FixedExtractionCode,ExistingSharePolicy,SettingsFingerprint,CreatedAt)
                VALUES($batch,1,1,'Random',NULL,'Ask','settings-fp',$now);
                INSERT INTO BatchOutputAccounts(BatchJobId,AccountId,OutputOrdinal,OutputLabel,
                  PlannedTransferCount,PlannedShareCount,ShareEnabled,CreatedShareOperationCount)
                VALUES($batch,$account,1,'output',1,1,1,0);
                INSERT INTO ShareOperations(Id,TransferOperationId,ResourceItemId,BatchJobId,AccountId,OutputOrdinal,
                  Status,ExactItemSetFingerprint,SettingsFingerprint,Generation,RequestDescriptorJson,
                  AdapterRequestId,SubmissionAcceptance,AttemptCount,LeaseGeneration,SubmitStartedAt,CreatedAt,UpdatedAt,
                  ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId)
                SELECT $share,operation.Id,operation.ResourceItemId,operation.BatchJobId,operation.AssignedAccountId,1,
                  'Submitting','exact-fp','settings-fp',1,'{}','share-request','Unknown',1,1,$now,$now,$now,
                  operation.ExecutionEngineVersion,operation.BrowserFamily,operation.ProfileId,
                  operation.InstallationId,operation.SessionId
                FROM TransferOperations operation WHERE operation.Id=$transfer;
                """;
            command.Parameters.AddWithValue("$batch", graph.BatchId.ToString("D"));
            command.Parameters.AddWithValue("$account", graph.AccountId.ToString("D"));
            command.Parameters.AddWithValue("$share", shareOperationId.ToString("D"));
            command.Parameters.AddWithValue("$transfer", graph.OperationId.ToString("D"));
            command.Parameters.AddWithValue("$now", graph.Now.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }
        var attempts = new SqliteWebAutomationAttemptStore(graph.Factory);
        Assert.True(await attempts.BeginAsync(WebAutomationAttemptRecord.Create(
            shareOperationId, graph.AccountId, 1, WebAutomationOwnerKind.TransferShare, graph.Now)));
        var shareGraph = graph with { OperationId = shareOperationId };
        var handoff = await EnterHandoffAsync(
            attempts, shareGraph, 1, WebAutomationAttemptState.Preparing);

        Assert.Equal(WebAutomationBatchCancellationDisposition.Cancelled,
            await new SqliteWebAutomationBatchCancellationStore(graph.Factory).CancelAsync(
                shareOperationId, graph.BatchId, graph.AccountId, 1,
                WebAutomationOwnerKind.TransferShare, handoff.Revision, handoff.ResumeStage!.Value,
                graph.Now.AddMinutes(1)));

        Assert.Equal(WebAutomationAttemptState.NeedsDecision,
            (await attempts.GetAsync(shareOperationId, graph.AccountId, 1))!.State);
        Assert.Equal("share-request", await ScalarTextAsync(
            graph.Factory, "SELECT AdapterRequestId FROM ShareOperations WHERE Id=$id;", shareOperationId));
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

    [Fact]
    public async Task WebPreparingCannotOverrideStandaloneResultEvidence()
    {
        var graph = await CreateStandaloneGraphAsync();
        await ExecuteAsync(graph.Factory, """
            UPDATE StandaloneShareOperations
            SET Status='ResultPending',SubmissionState='ResultPending',
                BaselineFingerprint='baseline-evidence',BaselineShareCount=1,BaselineCompletedAt=$now
            WHERE Id=$id;
            """, graph.OperationId, graph.Now);
        var attempts = new SqliteWebAutomationAttemptStore(graph.Factory);
        Assert.True(await attempts.BeginAsync(WebAutomationAttemptRecord.Create(
            graph.OperationId, graph.AccountId, 1, WebAutomationOwnerKind.StandaloneShare, graph.Now)));
        var handoff = await EnterHandoffAsync(
            attempts, graph, 1, WebAutomationAttemptState.Preparing);

        Assert.Equal(WebAutomationBatchCancellationDisposition.Cancelled,
            await new SqliteWebAutomationBatchCancellationStore(graph.Factory).CancelAsync(
                graph.OperationId, graph.BatchId, graph.AccountId, 1,
                WebAutomationOwnerKind.StandaloneShare, handoff.Revision, handoff.ResumeStage!.Value,
                graph.Now.AddMinutes(1)));

        Assert.Equal(WebAutomationAttemptState.NeedsDecision,
            (await attempts.GetAsync(graph.OperationId, graph.AccountId, 1))!.State);
        Assert.Equal("ResultPending", await ScalarTextAsync(
            graph.Factory, "SELECT Status FROM StandaloneShareOperations WHERE Id=$id;", graph.OperationId));
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
              OperationFingerprint,SubmitState,TargetSlotState,CreatedAt,UpdatedAt,
              ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId)
            VALUES($operation,$resource,$batch,1,1,'/','/','',$account,'Automatic','Preparing',$attempt,
              'transfer-fp','NotPrepared','Reserved',$now,$now,
              'WebView2-v1','LegacyWebView2',$legacyProfile,NULL,NULL);
            """;
        command.Parameters.AddWithValue("$account", accountId.ToString("D"));
        command.Parameters.AddWithValue("$batch", batchId.ToString("D"));
        command.Parameters.AddWithValue("$resource", resourceId.ToString("D"));
        command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        command.Parameters.AddWithValue("$attempt", attemptCount);
        command.Parameters.AddWithValue("$legacyProfile", ExecutionProfileSnapshot.LegacyForAccount(accountId).ProfileId);
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
              LeaseGeneration,Revision,CreatedAt,UpdatedAt,
              ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId)
            VALUES($operation,$batch,$account,1,'item','File','/','exact-fp',1,'Preparing',
              'NotStarted','Unknown',1,0,$now,$now,
              'WebView2-v1','LegacyWebView2',$legacyProfile,NULL,NULL);
            INSERT INTO StandaloneShareRemoteItems(OperationId,ItemOrdinal,RemoteItemId,CanonicalSortKey,DisplayName)
            VALUES($operation,0,'remote-id',X'01','item');
            """;
        command.Parameters.AddWithValue("$account", accountId.ToString("D"));
        command.Parameters.AddWithValue("$batch", batchId.ToString("D"));
        command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        command.Parameters.AddWithValue("$directory", Path.Combine(root, "results"));
        command.Parameters.AddWithValue("$legacyProfile", ExecutionProfileSnapshot.LegacyForAccount(accountId).ProfileId);
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
