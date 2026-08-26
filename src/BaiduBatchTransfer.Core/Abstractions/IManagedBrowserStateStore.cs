using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Core.Abstractions;

public interface IManagedBrowserStateStore
{
    Task<BrowserInstallationIdentity> CreateInstallationAsync(
        BrowserInstallationIdentity installation,
        CancellationToken cancellationToken);

    Task<BrowserInstallationIdentity?> LoadInstallationAsync(
        Guid installationId,
        CancellationToken cancellationToken);

    Task<BrowserInstallationIdentity?> LoadActiveInstallationAsync(CancellationToken cancellationToken);

    Task<ManagedBrowserTransitionResult> TryActivateInstallationAsync(
        ManagedBrowserTransitionRequest request,
        CancellationToken cancellationToken);

    Task<ManagedBrowserTransitionResult> TryRetireInstallationAsync(
        ManagedBrowserTransitionRequest request,
        CancellationToken cancellationToken);

    Task<ManagedBrowserSessionReference> CreateSessionAsync(
        ManagedBrowserSessionReference session,
        CancellationToken cancellationToken);

    Task<ManagedBrowserSessionReference?> LoadSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken);

    Task<ManagedBrowserSessionReference?> LoadActiveSessionAsync(
        Guid accountId,
        BrowserFamily browserFamily,
        CancellationToken cancellationToken);

    Task<ManagedBrowserTransitionResult> TryActivateSessionAsync(
        ManagedBrowserTransitionRequest request,
        CancellationToken cancellationToken);

    Task<ManagedBrowserTransitionResult> TryRetireSessionAsync(
        ManagedBrowserTransitionRequest request,
        CancellationToken cancellationToken);

    Task AddRetentionAsync(
        ManagedBrowserRetentionReference reference,
        CancellationToken cancellationToken);

    Task ReleaseRetentionAsync(
        ManagedBrowserRetentionReference reference,
        CancellationToken cancellationToken);

    Task<bool> IsRetirementEligibleAsync(
        Guid installationId,
        Guid sessionId,
        CancellationToken cancellationToken);
}
