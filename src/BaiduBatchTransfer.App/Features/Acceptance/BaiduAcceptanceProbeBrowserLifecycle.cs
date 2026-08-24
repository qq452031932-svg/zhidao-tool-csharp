namespace BaiduBatchTransfer.App.Features.Acceptance;

public sealed class BaiduAcceptanceProbeBrowserLifecycle(
    uint activeBrowserProcessId,
    Action disposeBrowser,
    TimeSpan exitTimeout)
{
    private readonly TaskCompletionSource exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void NotifyBrowserProcessExited(uint browserProcessId)
    {
        if (browserProcessId == activeBrowserProcessId) exited.TrySetResult();
    }

    public async Task<bool> DisposeAfterExitAsync(CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(disposeBrowser);
        disposeBrowser();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(exitTimeout);
        try
        {
            await exited.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
