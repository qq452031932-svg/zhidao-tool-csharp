using BaiduBatchTransfer.App.Features.BrowserAutomation;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Infrastructure.Database;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class WebAutomationBatchCancellationTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), $"bbt-web-cancel-result-{Guid.NewGuid():N}");

    [Fact]
    public async Task DurableSuccessBroadcastsOnceAndDuplicateCancelIsIdempotent()
    {
        var store = new RecordingStore
        {
            Results = new Queue<WebAutomationBatchCancellationDisposition>([
                WebAutomationBatchCancellationDisposition.Cancelled,
                WebAutomationBatchCancellationDisposition.AlreadyCancelled,
            ]),
        };
        var identity = Identity();
        var attempts = new RecordingAttemptStore(identity, WebAutomationAttemptState.NeedsDecision);
        using var service = new WebAutomationBatchCancellation(store, attempts, TimeProvider.System);
        var signals = 0;
        using var registration = service.Register(
            identity.BatchId, identity.OwnerKind, () => Interlocked.Increment(ref signals));

        var first = await service.CancelAsync(identity, 1, WebAutomationAttemptState.Preparing);
        var duplicate = await service.CancelAsync(identity, 1, WebAutomationAttemptState.Preparing);

        Assert.True(first.IsCancelled);
        Assert.Equal(WebAutomationAttemptState.NeedsDecision, first.PersistentState);
        Assert.Equal(first, duplicate);
        Assert.Equal(2, store.CancelCalls);
        Assert.Equal(2, attempts.GetCalls);
        Assert.Equal(1, signals);
    }

    [Fact]
    public async Task PersistenceFailureOrTerminalBatchNeverBroadcastsCancellation()
    {
        var identity = Identity();
        foreach (var behavior in new[] { StoreBehavior.Throw, StoreBehavior.Terminal })
        {
            var store = new RecordingStore { Behavior = behavior };
            using var service = new WebAutomationBatchCancellation(
                store,
                new RecordingAttemptStore(identity, WebAutomationAttemptState.DefinitelyNotSubmitted),
                TimeProvider.System);
            var signaled = false;
            using var registration = service.Register(
                identity.BatchId, identity.OwnerKind, () => signaled = true);

            await Assert.ThrowsAnyAsync<Exception>(() => service.CancelAsync(
                identity, 1, WebAutomationAttemptState.Preparing));

            Assert.False(signaled);
        }
    }

    [Fact]
    public async Task RestartedServiceLoadsDurableCancellationBeforeWorkStarts()
    {
        var identity = Identity();
        var store = new RecordingStore { PersistedCancelled = true };
        using var restarted = new WebAutomationBatchCancellation(
            store,
            new RecordingAttemptStore(identity, WebAutomationAttemptState.NeedsDecision),
            TimeProvider.System);
        var signaled = false;
        using var registration = restarted.Register(
            identity.BatchId, identity.OwnerKind, () => signaled = true);

        Assert.True(await restarted.IsCancelledAsync(identity.BatchId, identity.OwnerKind));
        Assert.True(signaled);
    }

    [Fact]
    public async Task RemoteSelectionCancellationHasItsOwnBucketFromTransferWithSameBatchId()
    {
        var identity = Identity(WebAutomationOwnerKind.RemoteSelection);
        using var service = new WebAutomationBatchCancellation(
            new RecordingStore(),
            new RecordingAttemptStore(identity, WebAutomationAttemptState.DefinitelyNotSubmitted),
            TimeProvider.System);
        var transferSignals = 0;
        var selectionSignals = 0;
        using var transfer = service.Register(
            identity.BatchId, WebAutomationOwnerKind.Transfer, () => transferSignals++);
        using var selection = service.Register(
            identity.BatchId, WebAutomationOwnerKind.RemoteSelection, () => selectionSignals++);

        await service.CancelAsync(identity, 1, WebAutomationAttemptState.Preparing);

        Assert.Equal(0, transferSignals);
        Assert.Equal(1, selectionSignals);
    }

    [Fact]
    public async Task PreparingWebStageWithBusinessSubmissionEvidenceReturnsCommittedNeedsDecision()
    {
        Directory.CreateDirectory(root);
        var factory = new SqliteConnectionFactory(Path.Combine(root, "app.db"));
        await new DatabaseMigrator(
            factory,
            new DatabaseBackupService(factory, Path.Combine(root, "backups")),
            "test").MigrateAsync();
        var identity = new WebAutomationRequestIdentity(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 2, WebAutomationOwnerKind.Transfer);
        var resourceId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using (var connection = await factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
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
                  OperationFingerprint,ConfirmationDescriptorJson,AdapterRequestId,SubmitState,TargetSlotState,
                  SubmitStartedAt,CreatedAt,UpdatedAt,ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId)
                VALUES($operation,$resource,$batch,1,1,'/','/','',$account,'Automatic','Submitting',2,
                  'transfer-fp','{}','request-evidence','Submitting','Reserved',$now,$now,$now,
                  'WebView2-v1','LegacyWebView2',$legacyProfile,NULL,NULL);
                """;
            command.Parameters.AddWithValue("$account", identity.AccountId.ToString("D"));
            command.Parameters.AddWithValue("$legacyProfile", ExecutionProfileSnapshot.LegacyForAccount(identity.AccountId).ProfileId);
            command.Parameters.AddWithValue("$batch", identity.BatchId.ToString("D"));
            command.Parameters.AddWithValue("$resource", resourceId.ToString("D"));
            command.Parameters.AddWithValue("$operation", identity.OperationId.ToString("D"));
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }
        var attempts = new SqliteWebAutomationAttemptStore(factory);
        Assert.True(await attempts.BeginAsync(WebAutomationAttemptRecord.Create(
            identity.OperationId, identity.AccountId, identity.Generation, identity.OwnerKind, now)));
        Assert.True(await attempts.TryAdvanceAsync(
            identity.OperationId, identity.AccountId, identity.Generation,
            WebAutomationAttemptState.Preparing, 0,
            WebAutomationAttemptState.UserHandoff, new string('f', 64), true, now.AddSeconds(1)));
        using var service = new WebAutomationBatchCancellation(
            new SqliteWebAutomationBatchCancellationStore(factory),
            attempts,
            TimeProvider.System);

        var result = await service.CancelAsync(
            identity, 1, WebAutomationAttemptState.Preparing);

        Assert.True(result.IsCancelled);
        Assert.Equal(WebAutomationAttemptState.NeedsDecision, result.PersistentState);
        Assert.Equal(WebAutomationAttemptState.NeedsDecision,
            (await attempts.GetAsync(identity.OperationId, identity.AccountId, identity.Generation))!.State);
    }

    private static WebAutomationRequestIdentity Identity(
        WebAutomationOwnerKind ownerKind = WebAutomationOwnerKind.Transfer) => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, ownerKind);

    private enum StoreBehavior
    {
        Success,
        Throw,
        Terminal,
    }

    private sealed class RecordingStore : IWebAutomationBatchCancellationStore
    {
        public Queue<WebAutomationBatchCancellationDisposition>? Results { get; init; }
        public StoreBehavior Behavior { get; init; }
        public bool PersistedCancelled { get; init; }
        public int CancelCalls { get; private set; }

        public Task<WebAutomationBatchCancellationDisposition> CancelAsync(
            Guid operationId,
            Guid batchId,
            Guid accountId,
            int generation,
            WebAutomationOwnerKind ownerKind,
            long expectedAttemptRevision,
            WebAutomationAttemptState expectedResumeStage,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            CancelCalls++;
            if (Behavior == StoreBehavior.Throw) throw new IOException("synthetic database failure");
            if (Behavior == StoreBehavior.Terminal)
                return Task.FromResult(WebAutomationBatchCancellationDisposition.Terminal);
            return Task.FromResult(Results?.Dequeue() ?? WebAutomationBatchCancellationDisposition.Cancelled);
        }

        public Task<bool> IsCancelledAsync(
            Guid batchId,
            WebAutomationOwnerKind ownerKind,
            CancellationToken cancellationToken = default) => Task.FromResult(PersistedCancelled);
    }

    private sealed class RecordingAttemptStore(
        WebAutomationRequestIdentity identity,
        WebAutomationAttemptState state) : IWebAutomationAttemptStore
    {
        public int GetCalls { get; private set; }

        public Task<WebAutomationAttemptRecord?> GetAsync(
            Guid operationId,
            Guid accountId,
            int generation,
            CancellationToken cancellationToken = default)
        {
            GetCalls++;
            var matches = operationId == identity.OperationId &&
                          accountId == identity.AccountId &&
                          generation == identity.Generation;
            return Task.FromResult<WebAutomationAttemptRecord?>(matches
                ? new WebAutomationAttemptRecord(
                    identity.OperationId,
                    identity.AccountId,
                    identity.Generation,
                    identity.OwnerKind,
                    state,
                    null,
                    null,
                    null,
                    false,
                    2,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow)
                : null);
        }

        public Task<bool> BeginAsync(WebAutomationAttemptRecord attempt, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<Page<WebAutomationAttemptRecord>> GetRecoveryPageAsync(PageQuery query, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<bool> TryPersistBaselineAsync(Guid operationId, Guid accountId, int generation, long expectedRevision, WebAutomationBaseline baseline, string observationFingerprint, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<bool> TryAdvanceAsync(Guid operationId, Guid accountId, int generation, WebAutomationAttemptState expectedState, long expectedRevision, WebAutomationAttemptState nextState, string? observationFingerprint, bool isWaitingForUser, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<bool> TryResumeFromUserHandoffAsync(Guid operationId, Guid accountId, int generation, long expectedRevision, WebAutomationAttemptState resumeStage, string? observationFingerprint, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<bool> TryResolveUserHandoffAsync(Guid operationId, Guid accountId, int generation, long expectedRevision, WebAutomationAttemptState resumeStage, WebAutomationAttemptState terminalState, string? observationFingerprint, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
