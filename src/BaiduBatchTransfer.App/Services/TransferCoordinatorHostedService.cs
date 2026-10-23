using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.App.Features.Licensing;
using Microsoft.Data.Sqlite;
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
    private readonly IRunLog? runLog;
    private readonly TimeSpan busyRetryDelay;

    public TransferCoordinatorHostedService(
        TransferCoordinator coordinator,
        IAdapterAvailability availability,
        TimeProvider? timeProvider = null,
        TimeSpan? idleScanInterval = null,
        RemoteWriteExecutionMode remoteWriteMode = RemoteWriteExecutionMode.Full,
        IPendingTransferReconciliationSource? pendingTransfers = null,
        ITransferResultReconciler? transferReconciler = null,
        LicenseRuntimeAuthorizationState? authorizationState = null,
        StuckBatchWatchdog? stuckBatchWatchdog = null,
        IRunLog? runLog = null,
        TimeSpan? busyRetryDelay = null)
    {
        this.stuckBatchWatchdog = stuckBatchWatchdog;
        this.runLog = runLog;
        this.busyRetryDelay = busyRetryDelay ?? TimeSpan.FromSeconds(3);
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
        if (this.busyRetryDelay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(busyRetryDelay));
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
        var busyStreak = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
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

                if (busyStreak > 0)
                {
                    runLog?.Write(RunLogSource, $"数据库恢复空闲，转存调度已继续（期间等了 {busyStreak} 次）。");
                    busyStreak = 0;
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
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested && IsDatabaseBusy(exception))
            {
                // 用户 10-23：建几千条的批次、监控反馈首次同步时一次写很多，数据库被占住超过 5 秒，
                // 这里等不到锁就抛「database is locked」，没人接住，整个软件跟着退出（退出码 0、日志里没有原因）。
                // 没拿到锁的那次事务整体回滚、什么都没写进去，歇一下再来就行（与重开软件后的恢复一样）；连着忙只在第一次写日志。
                if (busyStreak++ == 0)
                    runLog?.Write(RunLogSource, $"数据库正被其他操作占用（建批次或监控反馈同步时一次写入较多），转存调度稍后自动重试：{exception.Message}");
                try
                {
                    await Task.Delay(busyRetryDelay, timeProvider, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                // 其他错误仍按原设计停下（调度数据不一致时不能带病重试），但原因要写进运行日志，
                // 否则软件关掉了用户和我们都不知道为什么。
                runLog?.Write(RunLogSource, $"【错误】转存调度出错，软件将关闭，请把运行日志发给我们：{exception}");
                throw;
            }
        }
    }

    private const string RunLogSource = "转存";

    private static bool IsDatabaseBusy(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            // 5 = SQLITE_BUSY（database is locked），6 = SQLITE_LOCKED。
            if (current is SqliteException sqlite && sqlite.SqliteErrorCode is 5 or 6) return true;
        }
        return false;
    }
}
