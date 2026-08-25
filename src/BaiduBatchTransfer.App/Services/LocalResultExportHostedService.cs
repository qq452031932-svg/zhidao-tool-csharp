using BaiduBatchTransfer.Application.Jobs;
using Microsoft.Extensions.Hosting;

namespace BaiduBatchTransfer.App.Services;

public sealed class LocalResultExportHostedService : BackgroundService
{
    private readonly ILocalResultExportCoordinator coordinator;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan scanInterval;

    public LocalResultExportHostedService(
        ILocalResultExportCoordinator coordinator,
        TimeProvider? timeProvider = null,
        TimeSpan? scanInterval = null)
    {
        this.coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.scanInterval = scanInterval ?? TimeSpan.FromMinutes(1);
        if (this.scanInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(scanInterval));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await coordinator.RecoverAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Export state is durable; the next bounded scan retries without affecting remote operations.
            }

            try
            {
                await Task.Delay(scanInterval, timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
