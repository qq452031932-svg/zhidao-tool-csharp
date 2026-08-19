using System.Threading.Channels;

namespace BaiduBatchTransfer.Application.Scheduling;

public interface ITransferOperationExecutor
{
    Task ExecuteAsync(Guid operationId, CancellationToken cancellationToken = default);
}

public sealed class TransferCoordinator
{
    public const int MaximumGlobalConcurrency = 2;
    private readonly AllocationTransactionService allocation;
    private readonly ITransferOperationExecutor executor;
    private readonly NetworkExecutionGate networkGate;
    private readonly SemaphoreSlim singleCycle = new(1, 1);

    public TransferCoordinator(
        AllocationTransactionService allocation,
        ITransferOperationExecutor executor,
        NetworkExecutionGate networkGate)
    {
        this.allocation = allocation ?? throw new ArgumentNullException(nameof(allocation));
        this.executor = executor ?? throw new ArgumentNullException(nameof(executor));
        this.networkGate = networkGate ?? throw new ArgumentNullException(nameof(networkGate));
    }

    public async Task<int> RunCycleAsync(CancellationToken cancellationToken = default)
    {
        if (!networkGate.CanAcquireNewWork) return 0;
        await singleCycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!networkGate.CanAcquireNewWork) return 0;
            var state = await allocation.GetStateAsync(cancellationToken).ConfigureAwait(false);
            if (state.ActiveBatchIds.Count > 1)
                throw new InvalidOperationException("More than one active batch was detected; scheduling is blocked.");

            var batchId = state.ActiveBatchIds.Count == 1
                ? state.ActiveBatchIds[0]
                : await allocation.TryActivateNextBatchAsync(cancellationToken).ConfigureAwait(false);
            if (batchId is null) return 0;

            var capacity = Math.Max(0, MaximumGlobalConcurrency - state.ActiveSlotCount);
            if (capacity == 0) return 0;
            var channel = Channel.CreateBounded<ScheduledOperation>(new BoundedChannelOptions(MaximumGlobalConcurrency)
            {
                SingleWriter = true,
                SingleReader = false,
                FullMode = BoundedChannelFullMode.Wait,
            });
            var scheduled = 0;
            for (; scheduled < capacity && networkGate.CanAcquireNewWork; scheduled++)
            {
                var operation = await allocation.TryAllocateNextAsync(batchId.Value, cancellationToken)
                    .ConfigureAwait(false);
                if (operation is null) break;
                await channel.Writer.WriteAsync(operation, cancellationToken).ConfigureAwait(false);
            }
            channel.Writer.TryComplete();

            async Task ConsumeAsync()
            {
                await foreach (var operation in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                    await executor.ExecuteAsync(operation.OperationId, cancellationToken).ConfigureAwait(false);
            }

            await Task.WhenAll(ConsumeAsync(), ConsumeAsync()).ConfigureAwait(false);
            return scheduled;
        }
        finally
        {
            singleCycle.Release();
        }
    }
}
