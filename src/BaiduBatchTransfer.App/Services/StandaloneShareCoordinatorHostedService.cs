using BaiduBatchTransfer.Application.Scheduling;
using Microsoft.Extensions.Hosting;

namespace BaiduBatchTransfer.App.Services;

public sealed class StandaloneShareCoordinatorHostedService(StandaloneShareCoordinator coordinator) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => coordinator.StartAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => coordinator.StopAsync(cancellationToken);
}
