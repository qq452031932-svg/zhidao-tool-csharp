using BaiduBatchTransfer.Application.Scheduling;

namespace BaiduBatchTransfer.Application.Tests.Scheduling;

public sealed class TransferCoordinatorTests
{
    [Fact]
    public async Task CoordinatorAllocatesSeriallyAndExecutesAtMostTwoWithOnePerAccount()
    {
        var batch = Guid.NewGuid();
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();
        var store = new RecordingAllocationStore(batch,
        [
            Candidate(accountA, 1, "Exclusive"),
            Candidate(accountB, 2, "Public"),
            Candidate(accountA, 3, "Public"),
        ]);
        var executor = new BlockingExecutor(expectedConcurrent: 2);
        var coordinator = new TransferCoordinator(
            new AllocationTransactionService(store), executor, new NetworkExecutionGate(true));

        var scheduled = await coordinator.RunCycleAsync();

        Assert.Equal(2, scheduled);
        Assert.Equal([1L, 2L], store.Reserved.Select(item => item.AllocationOrdinal));
        Assert.Equal(2, store.Reserved.Select(item => item.AccountId).Distinct().Count());
        Assert.Equal(2, executor.MaximumConcurrency);
    }

    [Fact]
    public async Task ResultPendingKeepsAccountBusyButDoesNotConsumeGlobalNetworkWorker()
    {
        var batch = Guid.NewGuid();
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();
        var accountC = Guid.NewGuid();
        var store = new RecordingAllocationStore(batch,
            [Candidate(accountA, 2, "Public"), Candidate(accountB, 3, "Public"), Candidate(accountC, 4, "Public")]);
        store.BusyAccounts.Add(accountA);
        var executor = new ImmediateExecutor();

        var scheduled = await new TransferCoordinator(
            new AllocationTransactionService(store), executor, new NetworkExecutionGate(true)).RunCycleAsync();

        Assert.Equal(2, scheduled);
        Assert.Equal([accountB, accountC], store.Reserved.Select(item => item.AccountId));
    }

    [Fact]
    public async Task OfflineGatePreventsCandidateAcquisition()
    {
        var store = new RecordingAllocationStore(Guid.NewGuid(), [Candidate(Guid.NewGuid(), 1, "Public")]);
        var gate = new NetworkExecutionGate(true);
        gate.ReportOffline();

        var scheduled = await new TransferCoordinator(
            new AllocationTransactionService(store), new ImmediateExecutor(), gate).RunCycleAsync();

        Assert.Equal(0, scheduled);
        Assert.Equal(0, store.ReservationAttempts);
        Assert.Equal(0, store.ExhaustionChecks);
        Assert.Equal(1, store.RecoveryChecks);
    }

    [Fact]
    public async Task FullNetworkWorkerCapacityDoesNotTriggerFalseExhaustion()
    {
        var store = new RecordingAllocationStore(Guid.NewGuid(), [] )
        {
            ActiveNetworkOperationCount = TransferCoordinator.MaximumGlobalConcurrency,
        };

        var scheduled = await new TransferCoordinator(
            new AllocationTransactionService(store), new ImmediateExecutor(), new NetworkExecutionGate(true)).RunCycleAsync();

        Assert.Equal(0, scheduled);
        Assert.Equal(0, store.ReservationAttempts);
        Assert.Equal(0, store.ExhaustionChecks);
    }

    [Fact]
    public async Task MultipleActiveBatchesAreRejectedInsteadOfSchedulingEither()
    {
        var store = new RecordingAllocationStore(Guid.NewGuid(), []);
        store.ActiveBatchIds.Add(Guid.NewGuid());
        var coordinator = new TransferCoordinator(
            new AllocationTransactionService(store), new ImmediateExecutor(), new NetworkExecutionGate(true));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.RunCycleAsync());

        Assert.Equal("More than one active batch was detected; scheduling is blocked.", error.Message);
    }

    [Fact]
    public async Task EmptyOnlineCycleChecksPersistentExhaustionExactlyOnce()
    {
        var store = new RecordingAllocationStore(Guid.NewGuid(), []);

        var scheduled = await new TransferCoordinator(
            new AllocationTransactionService(store), new ImmediateExecutor(), new NetworkExecutionGate(true)).RunCycleAsync();

        Assert.Equal(0, scheduled);
        Assert.Equal(1, store.ExhaustionChecks);
    }

    private static ScheduledOperation Candidate(Guid account, long ordinal, string source) =>
        new(Guid.NewGuid(), Guid.NewGuid(), account, ordinal, source);

    private sealed class RecordingAllocationStore(Guid activeBatchId, IEnumerable<ScheduledOperation> candidates)
        : IAllocationTransactionStore
    {
        private readonly Queue<ScheduledOperation> candidates = new(candidates);
        public List<Guid> ActiveBatchIds { get; } = [activeBatchId];
        public HashSet<Guid> BusyAccounts { get; } = [];
        public List<ScheduledOperation> Reserved { get; } = [];
        public int ActiveNetworkOperationCount { get; set; }
        public int ReservationAttempts { get; private set; }
        public int ExhaustionChecks { get; private set; }
        public int RecoveryChecks { get; private set; }

        public Task<CoordinatorState> GetStateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new CoordinatorState(ActiveBatchIds.ToArray(), ActiveNetworkOperationCount, BusyAccounts.ToArray()));

        public Task<Guid?> TryActivateNextBatchAsync(DateTimeOffset now, CancellationToken cancellationToken = default) =>
            Task.FromResult<Guid?>(null);

        public Task<ScheduledOperation?> TryReserveNextAsync(
            Guid batchId,
            DateTimeOffset reservedAt,
            DateTimeOffset leaseExpiresAt,
            CancellationToken cancellationToken = default)
        {
            ReservationAttempts++;
            while (candidates.TryDequeue(out var candidate))
            {
                if (BusyAccounts.Contains(candidate.AccountId)) continue;
                BusyAccounts.Add(candidate.AccountId);
                ActiveNetworkOperationCount++;
                Reserved.Add(candidate);
                return Task.FromResult<ScheduledOperation?>(candidate);
            }
            return Task.FromResult<ScheduledOperation?>(null);
        }

        public Task<BatchExhaustionOutcome> TryFinalizeExhaustedBatchAsync(
            Guid batchId,
            DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            ExhaustionChecks++;
            return Task.FromResult(BatchExhaustionOutcome.NotExhausted);
        }

        public Task<LeaseRecoveryResult> RecoverExpiredLeasesAsync(
            DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            RecoveryChecks++;
            return Task.FromResult(new LeaseRecoveryResult(0, 0));
        }
    }

    private sealed class ImmediateExecutor : ITransferOperationExecutor
    {
        public Task ExecuteAsync(Guid operationId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class BlockingExecutor(int expectedConcurrent) : ITransferOperationExecutor
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int current;
        public int MaximumConcurrency { get; private set; }

        public async Task ExecuteAsync(Guid operationId, CancellationToken cancellationToken = default)
        {
            var value = Interlocked.Increment(ref current);
            MaximumConcurrency = Math.Max(MaximumConcurrency, value);
            if (value == expectedConcurrent) release.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            Interlocked.Decrement(ref current);
        }
    }
}
