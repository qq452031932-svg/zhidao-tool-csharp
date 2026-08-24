using BaiduBatchTransfer.App.Features.Acceptance;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class BaiduAcceptanceProbeObservationCoordinatorTests
{
    [Fact]
    public async Task CancelsAndAwaitsAnObservationDuringClose()
    {
        var coordinator = new BaiduAcceptanceProbeObservationCoordinator();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.Track(async cancellationToken =>
        {
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); }
        });

        await coordinator.StopAsync(TimeSpan.FromSeconds(1));

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ContainsFaultedObservationWithoutSurfacingItsPrivateException()
    {
        var coordinator = new BaiduAcceptanceProbeObservationCoordinator();
        coordinator.Track(_ => Task.FromException(new InvalidOperationException("private observation failure")));

        await coordinator.StopAsync(TimeSpan.FromSeconds(1));
    }
}
