using BaiduBatchTransfer.Application.Jobs;
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
    private readonly StuckBatchWatchdog? stuckBatchWatchdog;

    public TransferCoordinatorHostedService(
        TransferCoordinator coordinator,
        IAdapterAvailability availability,
        TimeProvider? timeProvider = null,
        TimeSpan? idleScanInterval = null,
        RemoteWriteExecutionMode remoteWriteMode = RemoteWriteExecutionMode.Full,
        IPendingTransferReconciliationSource? pendingTransfers = null,
        ITransferResultReconciler? transferReconciler = null,
        LicenseRuntimeAuthorizationState? authorizationState = null,
        StuckBatchWatchdog? stuckBatchWatchdog = null)
    {
        this.stuckBatchWatchdog = stuckBatchWatchdog;
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
        Guid? lastPendingOperationId = null;
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
                    // 同一条核对完还是它（例如账号没有登录信息，核对不出结论也没改状态）：
                    // 不能原地连转，否则分享核对、卡住检测和后面的调度永远轮不到。往下走，空闲时歇一下。
                    var repeated = pendingOperationId == lastPendingOperationId;
                    lastPendingOperationId = pendingOperationId;
                    if (!repeated) continue;
                }
                else lastPendingOperationId = null;
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
            if (stuckBatchWatchdog is not null)
            {
                try
                {
                    await stuckBatchWatchdog.CheckAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception)
                {
                    // 卡住检测只是兜底，查不了这一轮就跳过，不能让调度循环跟着停掉。
                }
            }
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
