using BaiduBatchTransfer.Application.Scheduling;
using Microsoft.Extensions.Hosting;

namespace BaiduBatchTransfer.App.Services;

public sealed class TransferCoordinatorHostedService : BackgroundService
{
    private readonly TransferCoordinator coordinator;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan idleScanInterval;

    public TransferCoordinatorHostedService(
        TransferCoordinator coordinator,
        TimeProvider? timeProvider = null,
        TimeSpan? idleScanInterval = null)
    {
        this.coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.idleScanInterval = idleScanInterval ?? TimeSpan.FromSeconds(1);
        if (this.idleScanInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(idleScanInterval));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var scheduled = 0;
            try
            {
                scheduled = await coordinator.RunCycleAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Scheduling state is durable. A bounded idle scan retries transient failures.
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
