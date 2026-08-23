using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.Application.Scheduling;

public sealed class ShareCoordinatorWorkSource : IShareCoordinatorWorkSource
{
    private readonly IShareSchedulingStore store;
    private readonly IInterruptedShareOperationRecoveryStore recovery;
    private readonly ShareOperationExecutor executor;
    private readonly ShareOperationReconciliationService reconciliation;

    public ShareCoordinatorWorkSource(
        IShareSchedulingStore store,
        IInterruptedShareOperationRecoveryStore recovery,
        ShareOperationExecutor executor,
        ShareOperationReconciliationService reconciliation)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.recovery = recovery ?? throw new ArgumentNullException(nameof(recovery));
        this.executor = executor ?? throw new ArgumentNullException(nameof(executor));
        this.reconciliation = reconciliation ?? throw new ArgumentNullException(nameof(reconciliation));
    }

    public Task<ShareLeaseRecoveryResult> RecoverExpiredLeasesAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        recovery.RecoverExpiredShareOperationsAsync(now, cancellationToken);

    public async Task<CoordinatedWork?> TryTakeDueReconciliationAsync(
        Guid batchId,
        DateTimeOffset now,
        IReadOnlyCollection<Guid> excludedAccounts,
        CancellationToken cancellationToken = default)
    {
        var operation = await store.TryGetDueResultPendingAsync(
            batchId, now, excludedAccounts, cancellationToken).ConfigureAwait(false);
        if (operation is null) return null;
        return Work(CoordinatedWorkKind.ShareReconciliation, operation);
    }

    public async Task<CoordinatedWork?> TryReserveShareAsync(
        Guid batchId,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        IReadOnlyCollection<Guid> excludedAccounts,
        CancellationToken cancellationToken = default)
    {
        var operation = await store.TryReserveNextAsync(now, leaseDuration, cancellationToken).ConfigureAwait(false);
        if (operation is null) return null;
        if (operation.BatchJobId != batchId || excludedAccounts.Contains(operation.AccountId))
            throw new InvalidOperationException("The share scheduling store violated the unified coordinator lease boundary.");
        return Work(CoordinatedWorkKind.Share, operation);
    }

    public async Task ExecuteAsync(CoordinatedWork operation, CancellationToken cancellationToken = default)
    {
        switch (operation.Kind)
        {
            case CoordinatedWorkKind.Share:
                await executor.ExecuteAsync(
                    new(operation.OperationId, operation.Generation, operation.LeaseGeneration), cancellationToken)
                    .ConfigureAwait(false);
                break;
            case CoordinatedWorkKind.ShareReconciliation:
                await reconciliation.ReconcileAsync(operation.OperationId, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation), "Only share work is accepted by this source.");
        }
    }

    private static CoordinatedWork Work(CoordinatedWorkKind kind, ShareOperationRecord operation) =>
        new(kind, operation.Id, operation.BatchJobId, operation.AccountId, operation.Generation, operation.LeaseGeneration);
}
