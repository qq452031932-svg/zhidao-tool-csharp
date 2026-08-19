namespace BaiduBatchTransfer.Application.Scheduling;

public sealed record CoordinatorState(
    IReadOnlyList<Guid> ActiveBatchIds,
    int ActiveSlotCount,
    IReadOnlyCollection<Guid> BusyAccountIds);

public sealed record ScheduledOperation(
    Guid OperationId,
    Guid BatchJobId,
    Guid AccountId,
    long AllocationOrdinal,
    string AllocationSource);

public interface IAllocationTransactionStore
{
    Task<CoordinatorState> GetStateAsync(CancellationToken cancellationToken = default);
    Task<Guid?> TryActivateNextBatchAsync(DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<ScheduledOperation?> TryReserveNextAsync(
        Guid batchId,
        DateTimeOffset reservedAt,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default);
}

public sealed class AllocationTransactionService
{
    private static readonly TimeSpan DefaultLease = TimeSpan.FromMinutes(5);
    private readonly IAllocationTransactionStore store;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim serialAllocation = new(1, 1);

    public AllocationTransactionService(IAllocationTransactionStore store, TimeProvider? timeProvider = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<CoordinatorState> GetStateAsync(CancellationToken cancellationToken = default) =>
        store.GetStateAsync(cancellationToken);

    public Task<Guid?> TryActivateNextBatchAsync(CancellationToken cancellationToken = default) =>
        store.TryActivateNextBatchAsync(timeProvider.GetUtcNow(), cancellationToken);

    public async Task<ScheduledOperation?> TryAllocateNextAsync(
        Guid batchId,
        CancellationToken cancellationToken = default)
    {
        if (batchId == Guid.Empty) throw new ArgumentException("A batch ID is required.", nameof(batchId));
        await serialAllocation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = timeProvider.GetUtcNow();
            return await store.TryReserveNextAsync(
                batchId, now, now.Add(DefaultLease), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            serialAllocation.Release();
        }
    }
}
