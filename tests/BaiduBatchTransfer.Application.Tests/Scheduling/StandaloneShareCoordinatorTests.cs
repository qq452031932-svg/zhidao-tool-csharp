using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Infrastructure.Database;

namespace BaiduBatchTransfer.Application.Tests.Scheduling;

public sealed class StandaloneShareCoordinatorTests
{
    [Fact]
    public async Task PeriodicScanContinuesAfterOneTransientStoreFailureAndStopRemainsClean()
    {
        var store = new TransientScanFailureStore();
        var coordinator = new StandaloneShareCoordinator(
            store, new RecordingRunner(), TimeProvider.System,
            TimeSpan.FromMinutes(30), TimeSpan.FromMilliseconds(10));

        await coordinator.StartAsync();
        await store.ThirdScanReached.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await coordinator.StopAsync().WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(store.GetBatchesCalls >= 3);
    }

    [Fact]
    public async Task StopHonorsShutdownDeadlineWhenRunnerIgnoresCancellation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bbt-standalone-coordinator-stop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var factory = new SqliteConnectionFactory(Path.Combine(root, "app.db"));
            await new DatabaseMigrator(factory, new DatabaseBackupService(factory, Path.Combine(root, "backups")), "test").MigrateAsync();
            var accountId = Guid.NewGuid();
            await using (var connection = await factory.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    INSERT INTO Accounts(Id,BaiduUserId,DisplayName,LoginMethod,Status,DailyLimit,DailyExecutedCount,
                      SortOrder,IsEnabled,DailyCountDateKey,IsDeleted)
                    VALUES($id,'user','account','WebView2','Active',0,0,0,1,'2026-08-25',0);
                    """;
                command.Parameters.AddWithValue("$id", accountId.ToString("D"));
                await command.ExecuteNonQueryAsync();
            }
            var now = new DateTimeOffset(2026, 8, 25, 10, 0, 0, TimeSpan.Zero);
            var batchId = Guid.NewGuid();
            var store = new SqliteStandaloneShareStore(factory);
            await store.CreateOrGetBatchAsync(StandaloneSharePendingBatchEnvelope.Create(
                StandaloneShareBatchRecord.Create(batchId, Path.Combine(root, "results"), 1, now),
                [StandaloneShareOperationRecord.Create(
                    Guid.NewGuid(), batchId,
                    new OwnedRemoteSelection(accountId,
                        new RemoteItemIdentitySet([new RemoteItemIdentity("remote", "item")]),
                        "item", RemoteResourceKind.File, new RemotePath("/")),
                    1, now)]));
            var runner = new NonCooperativeRunner();
            var coordinator = new StandaloneShareCoordinator(
                store, runner, new FixedTimeProvider(now.AddSeconds(1)), TimeSpan.FromMinutes(30));
            await coordinator.StartAsync();
            await runner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

            await coordinator.StopAsync(deadline.Token).WaitAsync(TimeSpan.FromSeconds(1));

            runner.Release.TrySetResult();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ReleasedPendingOperationIsReconciledByPeriodicScanWithoutRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bbt-standalone-coordinator-reconcile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var factory = new SqliteConnectionFactory(Path.Combine(root, "app.db"));
            await new DatabaseMigrator(factory, new DatabaseBackupService(factory, Path.Combine(root, "backups")), "test").MigrateAsync();
            var accountId = Guid.NewGuid();
            await using (var connection = await factory.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    INSERT INTO Accounts(Id,BaiduUserId,DisplayName,LoginMethod,Status,DailyLimit,DailyExecutedCount,
                      SortOrder,IsEnabled,DailyCountDateKey,IsDeleted)
                    VALUES($id,'user','account','WebView2','Active',0,0,0,1,'2026-08-25',0);
                    """;
                command.Parameters.AddWithValue("$id", accountId.ToString("D"));
                await command.ExecuteNonQueryAsync();
            }
            var now = new DateTimeOffset(2026, 8, 25, 10, 0, 0, TimeSpan.Zero);
            var batchId = Guid.NewGuid();
            var batch = StandaloneShareBatchRecord.Create(batchId, Path.Combine(root, "results"), 2, now);
            var stuck = new OwnedRemoteSelection(accountId,
                new RemoteItemIdentitySet([new RemoteItemIdentity("remote-stuck", "item 1")]),
                "item 1", RemoteResourceKind.File, new RemotePath("/"));
            var succeeds = new OwnedRemoteSelection(accountId,
                new RemoteItemIdentitySet([new RemoteItemIdentity("remote-success", "item 2")]),
                "item 2", RemoteResourceKind.File, new RemotePath("/"));
            var store = new SqliteStandaloneShareStore(factory);
            await store.CreateOrGetBatchAsync(StandaloneSharePendingBatchEnvelope.Create(batch,
                [
                    StandaloneShareOperationRecord.Create(Guid.NewGuid(), batchId, stuck, 1, now),
                    StandaloneShareOperationRecord.Create(Guid.NewGuid(), batchId, succeeds, 2, now),
                ]));
            var adapter = new CloseThenConfirmAdapter();
            var session = new AccountSession(accountId, "user",
                [new SessionCookie("BDUSS", "secret", ".baidu.com", "/", null, true, true)], now);
            var runner = new StandaloneShareOperationExecutor(
                adapter, new SessionStore(session), store, new ShareSubmissionEngine(), new FixedTimeProvider(now.AddSeconds(1)));
            await using var coordinator = new StandaloneShareCoordinator(
                store, runner, new FixedTimeProvider(now.AddSeconds(1)), TimeSpan.FromMinutes(30), TimeSpan.FromMilliseconds(20));

            await coordinator.StartAsync();
            var deadline = DateTime.UtcNow.AddSeconds(5);
            IReadOnlyList<StandaloneShareOperationRecord> operations;
            do
            {
                operations = (await store.GetBatchSnapshotAsync(batchId))!.Operations;
                if (operations.Any(operation => operation.Status == StandaloneShareOperationStatus.Succeeded)) break;
                await Task.Delay(20);
            } while (DateTime.UtcNow < deadline);

            Assert.Contains(operations, operation => operation.Status == StandaloneShareOperationStatus.Succeeded);
            Assert.Contains(operations, operation => operation.Status == StandaloneShareOperationStatus.ResultPending);
            Assert.Equal(2, adapter.SubmitCalls);
            Assert.True(adapter.ConfirmCalls >= 2);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task StartupRecoversPersistedQueuedBatchAndRunsOneOperationAtATime()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bbt-standalone-coordinator-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var factory = new SqliteConnectionFactory(Path.Combine(root, "app.db"));
            await new DatabaseMigrator(factory, new DatabaseBackupService(factory, Path.Combine(root, "backups")), "test").MigrateAsync();
            var accountId = Guid.NewGuid();
            await using (var connection = await factory.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    INSERT INTO Accounts(Id,BaiduUserId,DisplayName,LoginMethod,Status,DailyLimit,DailyExecutedCount,
                      SortOrder,IsEnabled,DailyCountDateKey,IsDeleted)
                    VALUES($id,'user','account','WebView2','Active',0,0,0,1,'2026-08-25',0);
                    """;
                command.Parameters.AddWithValue("$id", accountId.ToString("D"));
                await command.ExecuteNonQueryAsync();
            }
            var now = new DateTimeOffset(2026, 8, 25, 10, 0, 0, TimeSpan.Zero);
            var batchId = Guid.NewGuid();
            var batch = StandaloneShareBatchRecord.Create(batchId, Path.Combine(root, "results"), 1, now);
            var selection = new OwnedRemoteSelection(accountId,
                new RemoteItemIdentitySet([new RemoteItemIdentity("remote", "item")]),
                "item", RemoteResourceKind.File, new RemotePath("/"));
            var store = new SqliteStandaloneShareStore(factory);
            await store.CreateOrGetBatchAsync(StandaloneSharePendingBatchEnvelope.Create(batch,
                [StandaloneShareOperationRecord.Create(Guid.NewGuid(), batchId, selection, 1, now)]));
            var runner = new RecordingRunner();
            await using var coordinator = new StandaloneShareCoordinator(
                store, runner, new FixedTimeProvider(now.AddSeconds(1)), TimeSpan.FromMinutes(30));

            await coordinator.StartAsync();
            var lease = await runner.Executed.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(batchId, lease.BatchId);
            Assert.Equal(1, runner.MaximumConcurrency);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class RecordingRunner : IStandaloneShareOperationRunner
    {
        private int concurrent;
        public int MaximumConcurrency { get; private set; }
        public TaskCompletionSource<StandaloneShareOperationLease> Executed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<StandaloneShareExecutionResult> ExecuteAsync(StandaloneShareOperationLease lease, CancellationToken cancellationToken = default)
        {
            MaximumConcurrency = Math.Max(MaximumConcurrency, Interlocked.Increment(ref concurrent));
            Executed.TrySetResult(lease);
            Interlocked.Decrement(ref concurrent);
            return Task.FromResult(new StandaloneShareExecutionResult(StandaloneShareExecutionDisposition.Stale, "stale"));
        }
        public Task<StandaloneShareExecutionResult> ReconcileAsync(StandaloneShareOperationLease lease, CancellationToken cancellationToken = default) =>
            Task.FromResult(new StandaloneShareExecutionResult(StandaloneShareExecutionDisposition.ResultPending, "pending"));
    }

    private sealed class NonCooperativeRunner : IStandaloneShareOperationRunner
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<StandaloneShareExecutionResult> ExecuteAsync(
            StandaloneShareOperationLease lease,
            CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task;
            return new(StandaloneShareExecutionDisposition.Stale, "stale");
        }

        public Task<StandaloneShareExecutionResult> ReconcileAsync(
            StandaloneShareOperationLease lease,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new StandaloneShareExecutionResult(StandaloneShareExecutionDisposition.ResultPending, "pending"));
    }

    private sealed class CloseThenConfirmAdapter : IOperationBoundBaiduShareAdapter
    {
        public int SubmitCalls { get; private set; }
        public int ConfirmCalls { get; private set; }
        public Task<BaiduAdapterResult<BaiduSharePage>> ListSharesAsync(AccountSession session, string? cursor, CancellationToken cancellationToken) =>
            Task.FromResult(BaiduAdapterResult<BaiduSharePage>.Success(new([], null)));
        public Task<BaiduAdapterResult<ShareCreationReceipt>> CreatePermanentShareAsync(AccountSession session, PermanentShareRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<BaiduAdapterResult<SharePreparationReceipt>> PreparePermanentShareForOperationAsync(ShareOperationAutomationContext context, AccountSession session, PermanentShareRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(BaiduAdapterResult<SharePreparationReceipt>.Success(SharePreparationReceipt.Ready, SubmissionAcceptance.DefinitelyNotAccepted));
        public Task<BaiduAdapterResult<ShareCreationReceipt>> SubmitPermanentShareForOperationAsync(ShareOperationAutomationContext context, AccountSession session, PermanentShareRequest request, CancellationToken cancellationToken)
        {
            SubmitCalls++;
            throw new InvalidOperationException("window closed");
        }
        public Task<BaiduAdapterResult<ShareCreationReceipt>> ConfirmExactShareForOperationAsync(ShareOperationAutomationContext context, AccountSession session, PermanentShareRequest request, CancellationToken cancellationToken)
        {
            ConfirmCalls++;
            if (context.ExactRemoteItems.Items.Single().ExactId == "remote-stuck")
                return Task.FromResult(BaiduAdapterResult<ShareCreationReceipt>.Failure(
                    BaiduErrorKind.UnknownResponse, adapterDiagnostic: null,
                    submissionAcceptance: SubmissionAcceptance.Unknown));
            return Task.FromResult(BaiduAdapterResult<ShareCreationReceipt>.Success(new(
                DateTimeOffset.UtcNow, "adapter", request, "share-new", "https://pan.baidu.com/s/share-new",
                new EffectiveShareSettings(true, true, ShareExtractionCodeBehavior.Random, "A1b2"))));
        }
    }

    private sealed class SessionStore(AccountSession session) : IBaiduSessionStore
    {
        public Task<AccountSession?> LoadAsync(string accountId, CancellationToken cancellationToken) =>
            Task.FromResult<AccountSession?>(session);
        public Task SaveAsync(AccountSession value, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RemoveAsync(string accountId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TransientScanFailureStore : IStandaloneShareExecutionStore
    {
        public int GetBatchesCalls { get; private set; }
        public TaskCompletionSource ThirdScanReached { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<Page<StandaloneShareBatchRecord>> GetBatchesAsync(PageQuery query, CancellationToken cancellationToken = default)
        {
            var call = ++GetBatchesCalls;
            if (call == 2) throw new IOException("transient database read failure");
            if (call >= 3) ThirdScanReached.TrySetResult();
            return Task.FromResult(new Page<StandaloneShareBatchRecord>([], 0, query.PageNumber, query.PageSize));
        }

        public Task<int> RecoverExpiredLeasesAsync(DateTimeOffset now, int maximumCount, CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<StandaloneShareOperationLease?> TryClaimReconciliationAsync(Guid batchId, Guid leaseOwnerId, DateTimeOffset now, DateTimeOffset leaseExpiresAt, CancellationToken cancellationToken = default) => Task.FromResult<StandaloneShareOperationLease?>(null);
        public Task<StandaloneShareOperationLease?> TryLeaseNextAsync(Guid batchId, Guid leaseOwnerId, DateTimeOffset now, DateTimeOffset leaseExpiresAt, CancellationToken cancellationToken = default) => Task.FromResult<StandaloneShareOperationLease?>(null);
        public Task<StandaloneShareBatchCreationReceipt> CreateOrGetBatchAsync(StandaloneSharePendingBatchEnvelope envelope, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<StandaloneShareBatchCreationReceipt?> GetBatchCreationAsync(Guid batchId, string draftFingerprint, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<StandaloneShareOperationRecord>> GetOperationsAsync(Guid batchId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<StandaloneShareBatchSnapshot?> GetBatchSnapshotAsync(Guid batchId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<StandaloneShareMutationResult<StandaloneShareBatchRecord>> TryPauseBatchAsync(Guid batchId, long expectedRevision, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<StandaloneShareMutationResult<StandaloneShareBatchRecord>> TryResumeBatchAsync(Guid batchId, long expectedRevision, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<StandaloneShareMutationResult<StandaloneShareOperationRecord>> TryRetryFailedOperationAsync(Guid operationId, int expectedGeneration, long expectedRevision, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> TryCompleteBaselineAsync(Guid operationId, Guid batchId, Guid accountId, int generation, long expectedLeaseGeneration, Guid expectedLeaseOwnerId, long expectedRevision, IReadOnlyCollection<string> existingShareIds, string baselineFingerprint, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<StringKeysetPage<string>> GetBaselinePageAsync(Guid operationId, int generation, string? afterShareId, int pageSize, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> TryMarkSubmittingAsync(Guid operationId, Guid batchId, Guid accountId, int generation, long expectedLeaseGeneration, Guid expectedLeaseOwnerId, long expectedRevision, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> TryRecordDefinitelyNotAcceptedAttemptAsync(Guid operationId, Guid batchId, Guid accountId, int generation, long expectedLeaseGeneration, Guid expectedLeaseOwnerId, long expectedRevision, int completedAttemptCount, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> TryBeginSubmissionRetryAsync(Guid operationId, Guid batchId, Guid accountId, int generation, long expectedLeaseGeneration, Guid expectedLeaseOwnerId, long expectedRevision, int completedAttemptCount, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> TryMarkResultPendingAsync(Guid operationId, Guid batchId, Guid accountId, int generation, long expectedLeaseGeneration, Guid expectedLeaseOwnerId, long expectedRevision, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> TryFailAsync(Guid operationId, Guid batchId, Guid accountId, int generation, long expectedLeaseGeneration, Guid expectedLeaseOwnerId, long expectedRevision, SubmissionAcceptance submissionAcceptance, string sanitizedErrorCode, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> TryAcceptResultAsync(StandaloneShareResultRecord result, long expectedLeaseGeneration, Guid expectedLeaseOwnerId, long expectedRevision, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> TryReuseExistingResultAsync(StandaloneShareResultRecord result, long expectedLeaseGeneration, Guid expectedLeaseOwnerId, long expectedRevision, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<StandaloneShareResultRecord?> GetResultAsync(Guid operationId, int generation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> TryReleaseReconciliationAsync(Guid operationId, Guid batchId, Guid accountId, int generation, long expectedLeaseGeneration, Guid expectedLeaseOwnerId, long expectedRevision, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
