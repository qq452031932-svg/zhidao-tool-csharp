namespace BaiduBatchTransfer.Application.Scheduling;

public sealed record AllocationCounterRebuildResult(
    int AllocationCount,
    int ReservedSlotCount,
    int FulfilledCount);

public interface IAllocationCounterStore
{
    Task<AllocationCounterRebuildResult> RebuildAllocationCountersAsync(
        CancellationToken cancellationToken = default);
}

public sealed class AllocationCounterRebuilder
{
    private readonly IAllocationCounterStore store;

    public AllocationCounterRebuilder(IAllocationCounterStore store) =>
        this.store = store ?? throw new ArgumentNullException(nameof(store));

    public Task<AllocationCounterRebuildResult> RebuildAsync(
        CancellationToken cancellationToken = default) =>
        store.RebuildAllocationCountersAsync(cancellationToken);
}
