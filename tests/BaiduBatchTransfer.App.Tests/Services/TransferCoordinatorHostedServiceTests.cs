using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.App.Features.Licensing;
using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.Core.Domain;

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
        var service = Service(store, new RecordingExecutor(), TimeSpan.FromMilliseconds(10));

        await service.StartAsync(CancellationToken.None);
        var observed = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(1)));
        await Task.Delay(50);

        Assert.Same(failure, observed);
        Assert.Equal(1, store.RecoveryCount);
    }

    private static TransferCoordinatorHostedService Service(
        ControllableAllocationStore store,
        ITransferOperationExecutor executor,
        TimeSpan idleScanInterval,
        RemoteWriteExecutionMode mode = RemoteWriteExecutionMode.Full,
        IPendingTransferReconciliationSource? pendingTransfers = null,
        ITransferResultReconciler? reconciler = null,
        LicenseRuntimeAuthorizationState? authorizationState = null) => new(
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
            authorizationState);

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

        public Task<TransferConfirmation> ReconcileAsync(
            Guid operationId,
            CancellationToken cancellationToken = default)
        {
            OperationId = operationId;
            Reconciled.TrySetResult();
            return Task.FromResult(TransferConfirmation.ConfirmedPresent);
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
        public bool RecoveryCancellationObserved { get; private set; }

        public void QueueOne() => Volatile.Write(ref queued, 1);

        public async Task<LeaseRecoveryResult> RecoverExpiredLeasesAsync(
            DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            RecoveryCount++;
            FirstRecovery.TrySetResult();
            if (RecoveryFailure is not null) throw RecoveryFailure;
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
