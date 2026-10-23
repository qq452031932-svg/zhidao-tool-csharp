using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.App.Features.Licensing;
using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.Core.Domain;
using Microsoft.Data.Sqlite;

namespace BaiduBatchTransfer.App.Tests.Services;

public sealed class TransferCoordinatorHostedServiceTests
{
    [Fact]
    public async Task RecoveredTransferWorkWaitsForStartupOrLaterAuthorization()
    {
        var store = new ControllableAllocationStore();
        var authorization = new LicenseRuntimeAuthorizationState();
        var service = Service(
            store,
            new RecordingExecutor(),
            TimeSpan.FromMilliseconds(250),
            authorizationState: authorization);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(75);
        Assert.Equal(0, store.RecoveryCount);

        authorization.CompleteStartupValidation();
        await Task.Delay(75);
        Assert.Equal(0, store.RecoveryCount);

        await authorization.AuthorizeAfterRecoveryAsync(_ => Task.CompletedTask);
        await store.FirstRecovery.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(1, store.RecoveryCount);
    }

    [Theory]
    [InlineData(RemoteWriteExecutionMode.Disabled)]
    [InlineData(RemoteWriteExecutionMode.AcceptanceOnly)]
    public async Task GlobalCoordinatorDoesNotScanUnlessRemoteWriteModeIsFull(
        RemoteWriteExecutionMode mode)
    {
        var store = new ControllableAllocationStore();
        var service = Service(store, new RecordingExecutor(), TimeSpan.FromMilliseconds(10), mode);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(75);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(0, store.RecoveryCount);
    }

    [Fact]
    public async Task DirectBackgroundModeRunsTheCoordinator()
    {
        var store = new ControllableAllocationStore();
        var service = Service(
            store, new RecordingExecutor(), TimeSpan.FromMilliseconds(250),
            RemoteWriteExecutionMode.DirectBackground);

        await service.StartAsync(CancellationToken.None);
        await store.FirstRecovery.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(1, store.RecoveryCount);
    }

    [Fact]
    public async Task DirectBackgroundModeReconcilesPendingTransferBeforeSchedulingMoreWork()
    {
        var store = new ControllableAllocationStore();
        var pending = new RecordingPendingTransferSource();
        var reconciler = new RecordingTransferReconciler();
        var service = Service(
            store, new RecordingExecutor(), TimeSpan.FromMilliseconds(250),
            RemoteWriteExecutionMode.DirectBackground, pending, reconciler);

        await service.StartAsync(CancellationToken.None);
        await reconciler.Reconciled.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(pending.OperationId, reconciler.OperationId);
        Assert.Equal(0, store.RecoveryCount);
    }

    [Fact]
    public async Task APendingTransferThatCannotBeSettledDoesNotStarveTheScheduler()
    {
        // 账号没有登录信息时核对得不出结论、也不改状态：原来会原地反复核对同一条，
        // 调度、分享核对和卡住检测都轮不到。
        var store = new ControllableAllocationStore();
        var pending = new StuckPendingTransferSource();
        var reconciler = new InconclusiveTransferReconciler();
        var service = Service(
            store, new RecordingExecutor(), TimeSpan.FromMilliseconds(100),
            RemoteWriteExecutionMode.DirectBackground, pending, reconciler);

        await service.StartAsync(CancellationToken.None);
        await store.FirstRecovery.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(300);
        await service.StopAsync(CancellationToken.None);

        Assert.InRange(reconciler.Count, 2, 20);
        Assert.True(store.RecoveryCount >= 1);
    }

    [Fact]
    public async Task TheStuckBatchWatchdogRunsWhenTheSchedulerIsIdle()
    {
        var store = new ControllableAllocationStore();
        var runControl = new WatchedRunControlStore();
        var watchdog = new StuckBatchWatchdog(runControl, TimeProvider.System,
            new StuckBatchWatchdogOptions(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(10), TimeSpan.FromMilliseconds(1)));
        var service = Service(store, new RecordingExecutor(), TimeSpan.FromMilliseconds(20), watchdog: watchdog);

        await service.StartAsync(CancellationToken.None);
        await runControl.Inspected.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await service.StopAsync(CancellationToken.None);
    }

    private sealed class StuckPendingTransferSource : IPendingTransferReconciliationSource
    {
        public Guid OperationId { get; } = Guid.NewGuid();
        public Task<Guid?> TryGetPendingDirectOperationAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<Guid?>(OperationId);
    }

    private sealed class InconclusiveTransferReconciler : ITransferResultReconciler
    {
        private int count;
        public int Count => Volatile.Read(ref count);
        public Task<TransferConfirmation> ReconcileAsync(Guid operationId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref count);
            return Task.FromResult(TransferConfirmation.Ambiguous);
        }
    }

    private sealed class WatchedRunControlStore : IBatchRunControlStore
    {
        public TaskCompletionSource Inspected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<StuckBatchSnapshot?> GetActiveStuckSnapshotAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            Inspected.TrySetResult();
            return Task.FromResult<StuckBatchSnapshot?>(null);
        }
        public Task<PauseBatchDisposition> TryPauseBatchAsync(Guid batchJobId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<ResumeBatchDisposition> TryResumeBatchAsync(Guid batchJobId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<RetryFailedTransferDisposition> TryRetryFailedTransferAsync(
            Guid operationId, DateTimeOffset retriedAt, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task StartRunsAnInitialCycleWithoutBusySpinningWhileIdle()
    {
        var store = new ControllableAllocationStore();
        var service = Service(store, new RecordingExecutor(), TimeSpan.FromMilliseconds(250));

        await service.StartAsync(CancellationToken.None);
        await store.FirstRecovery.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Task.Delay(75);

        Assert.Equal(1, store.RecoveryCount);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task BoundedRescanFindsWorkQueuedAfterTheHostStarted()
    {
        var store = new ControllableAllocationStore();
        var executor = new RecordingExecutor();
        var service = Service(store, executor, TimeSpan.FromMilliseconds(25));

        await service.StartAsync(CancellationToken.None);
        try
        {
            await store.FirstEmptyScan.Task.WaitAsync(TimeSpan.FromSeconds(1));
            store.QueueOne();

            await executor.Executed.Task.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.Equal(store.OperationId, executor.OperationId);
            Assert.InRange(store.RecoveryCount, 2, 20);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task StopCancelsAndJoinsARunningCycle()
    {
        var store = new ControllableAllocationStore { BlockRecovery = true };
        var service = Service(store, new RecordingExecutor(), TimeSpan.FromMinutes(1));

        await service.StartAsync(CancellationToken.None);
        await store.FirstRecovery.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(store.RecoveryCancellationObserved);
    }

    [Fact]
    public async Task UnexpectedCoordinatorFailureFaultsOnceInsteadOfBeingRetriedForever()
    {
        var failure = new InvalidOperationException("corrupt scheduling invariant");
        var store = new ControllableAllocationStore { RecoveryFailure = failure };
        var runLog = new RecordingRunLog();
        var service = Service(store, new RecordingExecutor(), TimeSpan.FromMilliseconds(10), runLog: runLog);

        await service.StartAsync(CancellationToken.None);
        var observed = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(1)));
        await Task.Delay(50);

        Assert.Same(failure, observed);
        Assert.Equal(1, store.RecoveryCount);
        // 软件随后会关闭：原因必须留在运行日志里（用户 10-23 闪退时日志里什么都没有）。
        var line = Assert.Single(runLog.Lines);
        Assert.Contains("转存调度出错", line);
        Assert.Contains("corrupt scheduling invariant", line);
    }

    [Fact]
    public async Task ABusyDatabaseIsRetriedInsteadOfStoppingTheApplication()
    {
        // 用户 10-23：建几千条的批次或监控反馈同步时数据库被占住，调度器拿不到锁抛 SQLite 5，软件随即退出。
        var store = new ControllableAllocationStore
        {
            TransientRecoveryFailure = new SqliteException("SQLite Error 5: 'database is locked'.", 5),
            TransientRecoveryFailureCount = 3,
        };
        var runLog = new RecordingRunLog();
        var service = Service(store, new RecordingExecutor(), TimeSpan.FromMilliseconds(10),
            runLog: runLog, busyRetryDelay: TimeSpan.FromMilliseconds(10));

        await service.StartAsync(CancellationToken.None);
        await store.RecoveredAfterTransientFailures.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(50);
        await service.StopAsync(CancellationToken.None);

        Assert.False(service.ExecuteTask!.IsFaulted);
        Assert.True(store.RecoveryCount > 4);
        Assert.Equal(2, runLog.Lines.Count);
        Assert.Contains("数据库正被其他操作占用", runLog.Lines[0]);
        Assert.Contains("期间等了 3 次", runLog.Lines[1]);
    }

    private static TransferCoordinatorHostedService Service(
        ControllableAllocationStore store,
        ITransferOperationExecutor executor,
        TimeSpan idleScanInterval,
        RemoteWriteExecutionMode mode = RemoteWriteExecutionMode.Full,
        IPendingTransferReconciliationSource? pendingTransfers = null,
        ITransferResultReconciler? reconciler = null,
        LicenseRuntimeAuthorizationState? authorizationState = null,
        StuckBatchWatchdog? watchdog = null,
        IRunLog? runLog = null,
        TimeSpan? busyRetryDelay = null) => new(
            new TransferCoordinator(
                new AllocationTransactionService(store),
                executor,
                new NetworkExecutionGate(initiallyOnline: true)),
            new AvailableAdapter(),
            TimeProvider.System,
            idleScanInterval,
            mode,
            pendingTransfers,
            reconciler,
            authorizationState,
            watchdog,
            runLog,
            busyRetryDelay);

    private sealed class RecordingRunLog : IRunLog
    {
        private readonly object sync = new();
        private readonly List<string> lines = [];
        public IReadOnlyList<string> Lines { get { lock (sync) return lines.ToArray(); } }
        public void Write(string source, string? message) { lock (sync) lines.Add($"[{source}] {message}"); }
    }

    private sealed class RecordingPendingTransferSource : IPendingTransferReconciliationSource
    {
        private int returned;
        public Guid OperationId { get; } = Guid.NewGuid();

        public Task<Guid?> TryGetPendingDirectOperationAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Interlocked.Exchange(ref returned, 1) == 0 ? (Guid?)OperationId : null);
    }

    private sealed class RecordingTransferReconciler : ITransferResultReconciler
    {
        public TaskCompletionSource Reconciled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Guid? OperationId { get; private set; }

        public async Task<TransferConfirmation> ReconcileAsync(
            Guid operationId,
            CancellationToken cancellationToken = default)
        {
            OperationId = operationId;
            Reconciled.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return TransferConfirmation.ConfirmedPresent;
        }
    }

    private sealed class AvailableAdapter : IAdapterAvailability
    {
        public bool CanStartTransfers => true;
        public string StatusMessage => string.Empty;
    }

    private sealed class RecordingExecutor : ITransferOperationExecutor
    {
        public TaskCompletionSource Executed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Guid? OperationId { get; private set; }

        public Task ExecuteAsync(Guid operationId, CancellationToken cancellationToken = default)
        {
            OperationId = operationId;
            Executed.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private sealed class ControllableAllocationStore : IAllocationTransactionStore
    {
        private int queued;
        private int reserved;

        public Guid BatchId { get; } = Guid.NewGuid();
        public Guid OperationId { get; } = Guid.NewGuid();
        public Guid AccountId { get; } = Guid.NewGuid();
        public TaskCompletionSource FirstRecovery { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstEmptyScan { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int RecoveryCount { get; private set; }
        public bool BlockRecovery { get; init; }
        public Exception? RecoveryFailure { get; init; }
        public Exception? TransientRecoveryFailure { get; init; }
        public int TransientRecoveryFailureCount { get; init; }
        public TaskCompletionSource RecoveredAfterTransientFailures { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool RecoveryCancellationObserved { get; private set; }

        public void QueueOne() => Volatile.Write(ref queued, 1);

        public async Task<LeaseRecoveryResult> RecoverExpiredLeasesAsync(
            DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            RecoveryCount++;
            FirstRecovery.TrySetResult();
            if (RecoveryFailure is not null) throw RecoveryFailure;
            if (TransientRecoveryFailure is not null)
            {
                if (RecoveryCount <= TransientRecoveryFailureCount) throw TransientRecoveryFailure;
                RecoveredAfterTransientFailures.TrySetResult();
            }
            if (BlockRecovery)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    RecoveryCancellationObserved = true;
                    throw;
                }
            }
            return new(0, 0);
        }

        public Task<CoordinatorState> GetStateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new CoordinatorState([], 0, []));

        public Task<Guid?> TryActivateNextBatchAsync(
            DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            Guid? nextBatch = Volatile.Read(ref queued) == 1 ? BatchId : null;
            if (nextBatch is null) FirstEmptyScan.TrySetResult();
            return Task.FromResult(nextBatch);
        }

        public Task<ScheduledOperation?> TryReserveNextAsync(
            Guid batchId,
            DateTimeOffset reservedAt,
            DateTimeOffset leaseExpiresAt,
            CancellationToken cancellationToken = default)
        {
            if (batchId != BatchId || Volatile.Read(ref queued) == 0 || Interlocked.Exchange(ref reserved, 1) != 0)
                return Task.FromResult<ScheduledOperation?>(null);
            return Task.FromResult<ScheduledOperation?>(new(
                OperationId, BatchId, AccountId, 1, "Automatic"));
        }

        public Task<BatchExhaustionOutcome> TryFinalizeExhaustedBatchAsync(
            Guid batchId,
            DateTimeOffset now,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(BatchExhaustionOutcome.Completed);
    }
}
