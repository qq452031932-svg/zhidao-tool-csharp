using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Application.Scheduling;

namespace BaiduBatchTransfer.App.Tests.Services;

public sealed class TransferCoordinatorHostedServiceTests
{
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
        await store.FirstRecovery.Task.WaitAsync(TimeSpan.FromSeconds(1));
        store.QueueOne();

        await executor.Executed.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(store.OperationId, executor.OperationId);
        Assert.InRange(store.RecoveryCount, 2, 20);
        await service.StopAsync(CancellationToken.None);
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

    private static TransferCoordinatorHostedService Service(
        ControllableAllocationStore store,
        ITransferOperationExecutor executor,
        TimeSpan idleScanInterval) => new(
            new TransferCoordinator(
                new AllocationTransactionService(store),
                executor,
                new NetworkExecutionGate(initiallyOnline: true)),
            TimeProvider.System,
            idleScanInterval);

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
        public int RecoveryCount { get; private set; }
        public bool BlockRecovery { get; init; }
        public bool RecoveryCancellationObserved { get; private set; }

        public void QueueOne() => Volatile.Write(ref queued, 1);

        public async Task<LeaseRecoveryResult> RecoverExpiredLeasesAsync(
            DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            RecoveryCount++;
            FirstRecovery.TrySetResult();
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
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Guid?>(Volatile.Read(ref queued) == 1 ? BatchId : null);

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
