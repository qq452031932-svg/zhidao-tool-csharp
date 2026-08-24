namespace BaiduBatchTransfer.App.Features.Acceptance;

public interface IBaiduAcceptanceProbeBrowserProcess
{
    Task ObserveExitAsync(CancellationToken cancellationToken = default);
    void DisposeBrowser();
    Task WaitForObservationAsync(Task observation, CancellationToken cancellationToken = default);
}

public sealed class BaiduAcceptanceProbeBrowserLifecycle(
    IBaiduAcceptanceProbeBrowserProcess browser,
    TimeSpan exitTimeout)
{
    public async Task<bool> DisposeAfterExitAsync(CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(browser);
        var observation = browser.ObserveExitAsync(cancellationToken);
        browser.DisposeBrowser();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(exitTimeout);
        try
        {
            await browser.WaitForObservationAsync(observation, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
