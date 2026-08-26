using BaiduBatchTransfer.Application.Scheduling;
using Microsoft.Extensions.Hosting;

namespace BaiduBatchTransfer.App.Services;

public sealed class TransferCoordinatorHostedService : BackgroundService
{
    private readonly TransferCoordinator coordinator;
    private readonly IAdapterAvailability availability;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan idleScanInterval;

    public TransferCoordinatorHostedService(
        TransferCoordinator coordinator,
        IAdapterAvailability availability,
        TimeProvider? timeProvider = null,
        TimeSpan? idleScanInterval = null)
    {
        this.coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        this.availability = availability ?? throw new ArgumentNullException(nameof(availability));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.idleScanInterval = idleScanInterval ?? TimeSpan.FromSeconds(1);
        if (this.idleScanInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(idleScanInterval));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!availability.CanStartTransfers) return;
        while (!stoppingToken.IsCancellationRequested)
        {
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
