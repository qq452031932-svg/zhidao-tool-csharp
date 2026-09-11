namespace BaiduBatchTransfer.Application.Scheduling;

public sealed record ManagedTransferAcceptanceReservation(
    Guid BatchId,
    Guid TransferOperationId,
    Guid ShareOperationId,
    Guid AccountId,
    int TransferGeneration);

public interface IManagedTransferAcceptanceStore
{
    Task<ManagedTransferAcceptanceReservation?> TryReserveTransferAsync(
        Guid batchId,
        DateTimeOffset reservedAt,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default);

    Task<ShareExecutionLease?> TryReserveLinkedShareAsync(
        ManagedTransferAcceptanceReservation reservation,
        DateTimeOffset reservedAt,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default);

    Task<bool> TryFinalizeAsync(
        ManagedTransferAcceptanceReservation reservation,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);
}
