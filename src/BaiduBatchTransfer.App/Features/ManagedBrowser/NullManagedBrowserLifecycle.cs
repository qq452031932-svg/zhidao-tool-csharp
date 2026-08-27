using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.App.Features.ManagedBrowser;

/// <summary>
/// Task-6 boundary implementation. The real process owner is wired in Task 11.
/// </summary>
public sealed class NullManagedBrowserLifecycle : IManagedBrowserLifecycle
{
    public Task<ManagedBrowserLifecycleSession?> LoadActiveSessionAsync(
        Guid accountId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<ManagedBrowserLifecycleSession?>(null);
    }

    public Task<ManagedBrowserStopStatus> StopAsync(
        ManagedBrowserLifecycleSession exactSession,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exactSession);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ManagedBrowserStopStatus.NotRunning);
    }
}
