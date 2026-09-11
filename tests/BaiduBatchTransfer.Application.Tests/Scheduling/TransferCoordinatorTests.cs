using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Tests.Scheduling;

public sealed class TransferCoordinatorTests
{
    [Fact]
    public async Task SharedAdapterGateLimitsAllCallersToTwoGloballyAndOnePerAccount()
    {
        var gate = new BaiduAdapterConcurrencyGate();
        var accountA = Session(Guid.NewGuid());
        var accountB = Session(Guid.NewGuid());
        var accountC = Session(Guid.NewGuid());
        var sync = new object();
        var activeGlobal = 0;
        var maximumGlobal = 0;
        var activeByAccount = new Dictionary<string, int>(StringComparer.Ordinal);
        var maximumByAccount = new Dictionary<string, int>(StringComparer.Ordinal);

        async Task RunAsync(AccountSession session)
        {
            await using var lease = await gate.AcquireAsync(session);
            lock (sync)
            {
                activeGlobal++;
                maximumGlobal = Math.Max(maximumGlobal, activeGlobal);
                activeByAccount[session.AccountId] = activeByAccount.GetValueOrDefault(session.AccountId) + 1;
                maximumByAccount[session.AccountId] = Math.Max(
                    maximumByAccount.GetValueOrDefault(session.AccountId), activeByAccount[session.AccountId]);
            }
            await Task.Delay(50);
            lock (sync)
            {
                activeGlobal--;
                activeByAccount[session.AccountId]--;
            }
        }

        await Task.WhenAll(RunAsync(accountA), RunAsync(accountA), RunAsync(accountB), RunAsync(accountC));

        Assert.Equal(2, maximumGlobal);
        Assert.All(maximumByAccount.Values, value => Assert.Equal(1, value));
    }

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

    private static AccountSession Session(Guid id) => new(
        id, $"baidu-{id:N}",
        [new SessionCookie("BDUSS", "secret", ".baidu.com", "/", null, true, true)],
        DateTimeOffset.UtcNow);

    [Fact]
    public async Task TransferAndShareUseOneGlobalTwoWorkerPoolAndOneWorkerPerAccount()
    {
        var batch = Guid.NewGuid();
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();
        var transfers = new RecordingAllocationStore(batch, [Candidate(accountA, 1, "Public")]);
        var shares = new RecordingShareWorkSource([
            new(CoordinatedWorkKind.Share, Guid.NewGuid(), batch, accountB, 1, 1),
            new(CoordinatedWorkKind.Share, Guid.NewGuid(), batch, accountA, 1, 1),
        ], expectedConcurrent: 2);

        var scheduled = await new TransferCoordinator(
            new AllocationTransactionService(transfers), new ImmediateExecutor(),
            new NetworkExecutionGate(true), shares).RunCycleAsync();

        Assert.Equal(2, scheduled);
        Assert.Single(shares.Executed);
        Assert.Single(transfers.Reserved);
        Assert.Equal(2, shares.MaximumCombinedConcurrency);
        Assert.Equal(2, shares.Executed.Concat(transfers.Reserved.Select(x =>
            new CoordinatedWork(CoordinatedWorkKind.Transfer, x.OperationId, x.BatchJobId, x.AccountId, 0, 0)))
            .Select(x => x.AccountId).Distinct().Count());
    }

    [Fact]
    public async Task DirectPolicyPrioritizesReadyShareAndHonorsConfiguredConcurrency()
    {
        var batch = Guid.NewGuid();
        var transfers = new RecordingAllocationStore(batch,
            [Candidate(Guid.NewGuid(), 1, "Public"), Candidate(Guid.NewGuid(), 2, "Public")]);
        var readyShare = new CoordinatedWork(
            CoordinatedWorkKind.Share, Guid.NewGuid(), batch, Guid.NewGuid(), 1, 1);
        var shares = new RecordingShareWorkSource([readyShare], expectedConcurrent: 1);
        var policy = new FixedPolicySource(new BatchSchedulingPolicy(1, PreferReadyShares: true));

        var scheduled = await new TransferCoordinator(
            new AllocationTransactionService(transfers), new ImmediateExecutor(),
            new NetworkExecutionGate(true), shares, schedulingPolicies: policy).RunCycleAsync();

        Assert.Equal(1, scheduled);
        Assert.Equal([readyShare], shares.Executed);
        Assert.Empty(transfers.Reserved);
    }

    [Fact]
    public async Task DirectPolicyCanUseThreeWorkersButNeverAFourth()
    {
        var batch = Guid.NewGuid();
        var store = new RecordingAllocationStore(batch,
        [
            Candidate(Guid.NewGuid(), 1, "Public"), Candidate(Guid.NewGuid(), 2, "Public"),
            Candidate(Guid.NewGuid(), 3, "Public"), Candidate(Guid.NewGuid(), 4, "Public"),
        ]);
        var executor = new BlockingExecutor(expectedConcurrent: 3);

        var scheduled = await new TransferCoordinator(
            new AllocationTransactionService(store), executor, new NetworkExecutionGate(true),
            schedulingPolicies: new FixedPolicySource(
                new BatchSchedulingPolicy(3, PreferReadyShares: true))).RunCycleAsync();

        Assert.Equal(3, scheduled);
        Assert.Equal(3, executor.MaximumConcurrency);
        Assert.Equal(3, store.Reserved.Count);
    }

    [Fact]
    public async Task DueResultPendingReconciliationUsesCapacityButIsNeverReservedAgain()
    {
        var batch = Guid.NewGuid();
        var pendingAccount = Guid.NewGuid();
        var transferAccount = Guid.NewGuid();
        var transfers = new RecordingAllocationStore(batch, [Candidate(transferAccount, 1, "Public")]);
        transfers.BusyAccounts.Add(pendingAccount);
        var shares = new RecordingShareWorkSource([
            new(CoordinatedWorkKind.ShareReconciliation, Guid.NewGuid(), batch, pendingAccount, 1, 7),
        ], expectedConcurrent: 2);

        var scheduled = await new TransferCoordinator(
            new AllocationTransactionService(transfers), new ImmediateExecutor(),
            new NetworkExecutionGate(true), shares).RunCycleAsync();

        Assert.Equal(2, scheduled);
        Assert.Single(shares.Executed);
        Assert.Equal(CoordinatedWorkKind.ShareReconciliation, shares.Executed[0].Kind);
        Assert.Equal(0, shares.ShareReservationCount);
    }

    [Fact]
    public async Task OfflineCyclePerformsLocalShareRecoveryButNoReconciliationOrExecution()
    {
        var batch = Guid.NewGuid();
        var transfers = new RecordingAllocationStore(batch, []);
        var shares = new RecordingShareWorkSource([
            new(CoordinatedWorkKind.ShareReconciliation, Guid.NewGuid(), batch, Guid.NewGuid(), 1, 1),
        ], expectedConcurrent: 1);
        var gate = new NetworkExecutionGate(true);
        gate.ReportOffline();

        var scheduled = await new TransferCoordinator(
            new AllocationTransactionService(transfers), new ImmediateExecutor(), gate, shares).RunCycleAsync();

        Assert.Equal(0, scheduled);
        Assert.Equal(1, shares.RecoveryCount);
        Assert.Empty(shares.Executed);
        Assert.Equal(0, shares.ReconciliationTakeCount);
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

    private sealed class FixedPolicySource(BatchSchedulingPolicy policy) : IBatchSchedulingPolicySource
    {
        public Task<BatchSchedulingPolicy> GetPolicyAsync(
            Guid batchId, CancellationToken cancellationToken = default) => Task.FromResult(policy);
    }

    private sealed class RecordingShareWorkSource(IEnumerable<CoordinatedWork> work, int expectedConcurrent)
        : IShareCoordinatorWorkSource
    {
        private readonly Queue<CoordinatedWork> work = new(work);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int concurrent;
        public List<CoordinatedWork> Executed { get; } = [];
        public int MaximumCombinedConcurrency { get; private set; }
        public int RecoveryCount { get; private set; }
        public int ReconciliationTakeCount { get; private set; }
        public int ShareReservationCount { get; private set; }

        public Task<ShareLeaseRecoveryResult> RecoverExpiredLeasesAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            RecoveryCount++;
            return Task.FromResult(new ShareLeaseRecoveryResult(0, 0));
        }

        public Task<CoordinatedWork?> TryTakeDueReconciliationAsync(Guid batchId, DateTimeOffset now, IReadOnlyCollection<Guid> excludedAccounts, CancellationToken cancellationToken = default)
        {
            ReconciliationTakeCount++;
            return Task.FromResult(Take(CoordinatedWorkKind.ShareReconciliation, excludedAccounts));
        }

        public Task<CoordinatedWork?> TryReserveShareAsync(Guid batchId, DateTimeOffset now, TimeSpan leaseDuration, IReadOnlyCollection<Guid> excludedAccounts, CancellationToken cancellationToken = default)
        {
            ShareReservationCount++;
            return Task.FromResult(Take(CoordinatedWorkKind.Share, excludedAccounts));
        }

        private CoordinatedWork? Take(CoordinatedWorkKind kind, IReadOnlyCollection<Guid> excluded)
        {
            foreach (var item in work.ToArray())
            {
                if (item.Kind != kind || excluded.Contains(item.AccountId)) continue;
                var retained = work.Where(x => x != item).ToArray();
                work.Clear();
                foreach (var value in retained) work.Enqueue(value);
                return item;
            }
            return null;
        }

        public async Task ExecuteAsync(CoordinatedWork operation, CancellationToken cancellationToken = default)
        {
            Executed.Add(operation);
            var value = Interlocked.Increment(ref concurrent);
            MaximumCombinedConcurrency = Math.Max(MaximumCombinedConcurrency, value + 1);
            if (value + 1 >= expectedConcurrent) release.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            Interlocked.Decrement(ref concurrent);
        }
    }
}
