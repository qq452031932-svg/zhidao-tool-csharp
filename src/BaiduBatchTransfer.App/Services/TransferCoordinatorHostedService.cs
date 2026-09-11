using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.App.Features.Licensing;
using Microsoft.Extensions.Hosting;

namespace BaiduBatchTransfer.App.Services;

public sealed class TransferCoordinatorHostedService : BackgroundService
{
    private readonly TransferCoordinator coordinator;
    private readonly IAdapterAvailability availability;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan idleScanInterval;
    private readonly RemoteWriteExecutionMode remoteWriteMode;
    private readonly IPendingTransferReconciliationSource? pendingTransfers;
    private readonly ITransferResultReconciler? transferReconciler;
    private readonly LicenseRuntimeAuthorizationState? authorizationState;

    public TransferCoordinatorHostedService(
        TransferCoordinator coordinator,
        IAdapterAvailability availability,
        TimeProvider? timeProvider = null,
        TimeSpan? idleScanInterval = null,
        RemoteWriteExecutionMode remoteWriteMode = RemoteWriteExecutionMode.Full,
        IPendingTransferReconciliationSource? pendingTransfers = null,
        ITransferResultReconciler? transferReconciler = null,
        LicenseRuntimeAuthorizationState? authorizationState = null)
    {
        this.coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        this.availability = availability ?? throw new ArgumentNullException(nameof(availability));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.idleScanInterval = idleScanInterval ?? TimeSpan.FromSeconds(1);
        this.remoteWriteMode = remoteWriteMode;
        this.pendingTransfers = pendingTransfers;
        this.transferReconciler = transferReconciler;
        this.authorizationState = authorizationState;
        if ((pendingTransfers is null) != (transferReconciler is null))
            throw new ArgumentException("Pending transfer recovery requires both a source and reconciler.");
        if (!Enum.IsDefined(remoteWriteMode)) throw new ArgumentOutOfRangeException(nameof(remoteWriteMode));
        if (this.idleScanInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(idleScanInterval));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (remoteWriteMode is not (RemoteWriteExecutionMode.Full or RemoteWriteExecutionMode.DirectBackground) ||
            !availability.CanStartTransfers) return;
        if (authorizationState is not null)
        {
            try
            {
                await authorizationState.WaitUntilAuthorizedAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            if (remoteWriteMode == RemoteWriteExecutionMode.DirectBackground && pendingTransfers is not null)
            {
                var pendingOperationId = await pendingTransfers
                    .TryGetPendingDirectOperationAsync(stoppingToken).ConfigureAwait(false);
                if (pendingOperationId is not null)
                {
                    await transferReconciler!.ReconcileAsync(pendingOperationId.Value, stoppingToken)
                        .ConfigureAwait(false);
                    continue;
                }
            }

            int scheduled;
            try
            {
                scheduled = await coordinator.RunCycleAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            if (scheduled > 0) continue;
            try
            {
                await Task.Delay(idleScanInterval, timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
