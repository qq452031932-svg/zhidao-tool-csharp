using BaiduBatchTransfer.Application.Jobs;

namespace BaiduBatchTransfer.Application.Scheduling;

public enum ManagedTransferAcceptanceRunDisposition
{
    NotReserved,
    AwaitingTransferResolution,
    AwaitingFinalization,
    AwaitingExport,
    Completed,
}

public sealed record ManagedTransferAcceptanceRunOutcome(
    ManagedTransferAcceptanceRunDisposition Disposition,
    Guid? TransferOperationId = null,
    Guid? ShareOperationId = null);

public interface IShareOperationExecutor
{
    Task<ShareExecutionResult> ExecuteAsync(
        ShareExecutionLease lease,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Executes only the identifiers returned by the AcceptanceOnly store. It never
/// consults either global transfer or share work source.
/// </summary>
public sealed class ManagedTransferAcceptanceRunner(
    IManagedTransferAcceptanceStore store,
    ITransferOperationExecutor transferExecutor,
    IShareOperationExecutor shareExecutor,
    ILocalResultExportCoordinator exports,
    TimeProvider? timeProvider = null,
    TimeSpan? leaseDuration = null)
{
    private readonly IManagedTransferAcceptanceStore store =
        store ?? throw new ArgumentNullException(nameof(store));
    private readonly ITransferOperationExecutor transferExecutor =
        transferExecutor ?? throw new ArgumentNullException(nameof(transferExecutor));
    private readonly IShareOperationExecutor shareExecutor =
        shareExecutor ?? throw new ArgumentNullException(nameof(shareExecutor));
    private readonly ILocalResultExportCoordinator exports =
        exports ?? throw new ArgumentNullException(nameof(exports));
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;
    private readonly TimeSpan leaseDuration = ValidateLeaseDuration(leaseDuration ?? TimeSpan.FromMinutes(30));

    public async Task<ManagedTransferAcceptanceRunOutcome> ExecuteAsync(
        Guid batchId,
        CancellationToken cancellationToken = default)
    {
        if (batchId == Guid.Empty) throw new ArgumentException("A batch is required.", nameof(batchId));
        var now = timeProvider.GetUtcNow();
        var reservation = await store.TryReserveTransferAsync(
            batchId, now, now + leaseDuration, cancellationToken).ConfigureAwait(false);
        if (reservation is null)
            return new(ManagedTransferAcceptanceRunDisposition.NotReserved);

        await transferExecutor.ExecuteAsync(reservation.TransferOperationId, cancellationToken)
            .ConfigureAwait(false);

        now = timeProvider.GetUtcNow();
        var shareLease = await store.TryReserveLinkedShareAsync(
            reservation, now, now + leaseDuration, cancellationToken).ConfigureAwait(false);
        if (shareLease is null)
            return new(
                ManagedTransferAcceptanceRunDisposition.AwaitingTransferResolution,
                reservation.TransferOperationId,
                reservation.ShareOperationId);

        await shareExecutor.ExecuteAsync(shareLease.Value, cancellationToken).ConfigureAwait(false);
        var finalized = await store.TryFinalizeAsync(
            reservation, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        if (!finalized)
            return new(
                ManagedTransferAcceptanceRunDisposition.AwaitingFinalization,
                reservation.TransferOperationId,
                reservation.ShareOperationId);

        var exported = await exports.ExportTransferAsync(batchId, cancellationToken).ConfigureAwait(false);
        if (exported.Disposition is not (LocalResultExportDisposition.Exported or
            LocalResultExportDisposition.AlreadySucceeded))
            return new(
                ManagedTransferAcceptanceRunDisposition.AwaitingExport,
                reservation.TransferOperationId,
                reservation.ShareOperationId);

        return new(
            ManagedTransferAcceptanceRunDisposition.Completed,
            reservation.TransferOperationId,
            reservation.ShareOperationId);
    }

    private static TimeSpan ValidateLeaseDuration(TimeSpan value) =>
        value > TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(nameof(value));
}
