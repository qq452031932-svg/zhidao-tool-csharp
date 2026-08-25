using System.Threading.Channels;
using BaiduBatchTransfer.Application.Jobs;

namespace BaiduBatchTransfer.Application.Scheduling;

public interface ITransferOperationExecutor
{
    Task ExecuteAsync(Guid operationId, CancellationToken cancellationToken = default);
}

public enum CoordinatedWorkKind
{
    Transfer,
    Share,
    ShareReconciliation,
}

public sealed record CoordinatedWork(
    CoordinatedWorkKind Kind,
    Guid OperationId,
    Guid BatchJobId,
    Guid AccountId,
    int Generation,
    long LeaseGeneration)
{
    public override string ToString() =>
        $"CoordinatedWork {{ Kind = {Kind}, Identity = [REDACTED], Generation = {Generation}, LeaseGeneration = {LeaseGeneration} }}";
}

public interface IShareCoordinatorWorkSource
{
    Task<ShareLeaseRecoveryResult> RecoverExpiredLeasesAsync(DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<CoordinatedWork?> TryTakeDueReconciliationAsync(
        Guid batchId, DateTimeOffset now, IReadOnlyCollection<Guid> excludedAccounts,
        CancellationToken cancellationToken = default);
    Task<CoordinatedWork?> TryReserveShareAsync(
        Guid batchId, DateTimeOffset now, TimeSpan leaseDuration, IReadOnlyCollection<Guid> excludedAccounts,
        CancellationToken cancellationToken = default);
    Task ExecuteAsync(CoordinatedWork operation, CancellationToken cancellationToken = default);
}

public sealed class TransferCoordinator
{
    public const int MaximumGlobalConcurrency = 2;
    private readonly AllocationTransactionService allocation;
    private readonly ITransferOperationExecutor executor;
    private readonly NetworkExecutionGate networkGate;
    private readonly IShareCoordinatorWorkSource? shareWork;
    private readonly TimeProvider timeProvider;
    private readonly ILocalResultExportCoordinator? localResultExports;
    private readonly SemaphoreSlim singleCycle = new(1, 1);

    public TransferCoordinator(
        AllocationTransactionService allocation,
        ITransferOperationExecutor executor,
        NetworkExecutionGate networkGate,
        IShareCoordinatorWorkSource? shareWork = null,
        TimeProvider? timeProvider = null,
        ILocalResultExportCoordinator? localResultExports = null)
    {
        this.allocation = allocation ?? throw new ArgumentNullException(nameof(allocation));
        this.executor = executor ?? throw new ArgumentNullException(nameof(executor));
        this.networkGate = networkGate ?? throw new ArgumentNullException(nameof(networkGate));
        this.shareWork = shareWork;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.localResultExports = localResultExports;
    }

    public async Task<int> RunCycleAsync(CancellationToken cancellationToken = default)
    {
        await singleCycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await allocation.RecoverExpiredLeasesAsync(cancellationToken).ConfigureAwait(false);
            if (shareWork is not null)
                await shareWork.RecoverExpiredLeasesAsync(timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            if (!networkGate.CanAcquireNewWork) return 0;
            var state = await allocation.GetStateAsync(cancellationToken).ConfigureAwait(false);
            if (state.ActiveBatchIds.Count > 1)
                throw new InvalidOperationException("More than one active batch was detected; scheduling is blocked.");

            var batchId = state.ActiveBatchIds.Count == 1
                ? state.ActiveBatchIds[0]
                : await allocation.TryActivateNextBatchAsync(cancellationToken).ConfigureAwait(false);
            if (batchId is null) return 0;

            var capacity = Math.Max(0, MaximumGlobalConcurrency - state.ActiveNetworkOperationCount);
            if (capacity == 0) return 0;
            var channel = Channel.CreateBounded<CoordinatorDispatch>(new BoundedChannelOptions(MaximumGlobalConcurrency)
            {
                SingleWriter = true,
                SingleReader = false,
                FullMode = BoundedChannelFullMode.Wait,
            });
            var scheduled = 0;
            var scheduledAccounts = new HashSet<Guid>();
            for (; scheduled < capacity && networkGate.CanAcquireNewWork; scheduled++)
            {
                CoordinatedWork? share = null;
                if (shareWork is not null)
                    share = await shareWork.TryTakeDueReconciliationAsync(
                        batchId.Value, timeProvider.GetUtcNow(), scheduledAccounts, cancellationToken).ConfigureAwait(false);
                if (share is not null)
                {
                    scheduledAccounts.Add(share.AccountId);
                    await channel.Writer.WriteAsync(new(null, share), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var transfer = await allocation.TryAllocateNextAsync(batchId.Value, cancellationToken).ConfigureAwait(false);
                if (transfer is not null)
                {
                    scheduledAccounts.Add(transfer.AccountId);
                    await channel.Writer.WriteAsync(new(transfer, null), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (shareWork is not null)
                    share = await shareWork.TryReserveShareAsync(
                        batchId.Value, timeProvider.GetUtcNow(), TimeSpan.FromMinutes(5), scheduledAccounts,
                        cancellationToken).ConfigureAwait(false);
                if (share is null) break;
                scheduledAccounts.Add(share.AccountId);
                await channel.Writer.WriteAsync(new(null, share), cancellationToken).ConfigureAwait(false);
            }
            channel.Writer.TryComplete();

            async Task ConsumeAsync()
            {
                await foreach (var operation in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (operation.Transfer is not null)
                        await executor.ExecuteAsync(operation.Transfer.OperationId, cancellationToken).ConfigureAwait(false);
                    else
                        await shareWork!.ExecuteAsync(operation.Share!, cancellationToken).ConfigureAwait(false);
                }
            }

            await Task.WhenAll(ConsumeAsync(), ConsumeAsync()).ConfigureAwait(false);
            if (scheduled == 0 && state.ActiveNetworkOperationCount == 0 && state.BusyAccountIds.Count == 0 && networkGate.CanAcquireNewWork)
            {
                await allocation.TryFinalizeExhaustedBatchAsync(batchId.Value, cancellationToken).ConfigureAwait(false);
                if (localResultExports is not null)
                {
                    try
                    {
                        await localResultExports.ExportTransferAsync(batchId.Value, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch
                    {
                        // Export is independently durable and retried by startup recovery.
                    }
                }
            }
            return scheduled;
        }
        finally
        {
            singleCycle.Release();
        }
    }

    private sealed record CoordinatorDispatch(ScheduledOperation? Transfer, CoordinatedWork? Share);
}
