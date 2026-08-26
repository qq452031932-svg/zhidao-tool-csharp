using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Core.Abstractions;

public interface IManagedBrowserStateStore
{
    Task SaveInstallationAsync(
        BrowserInstallationIdentity installation,
        CancellationToken cancellationToken);

    Task<BrowserInstallationIdentity?> LoadInstallationAsync(
        Guid installationId,
        CancellationToken cancellationToken);

    Task ActivateInstallationAsync(Guid installationId, CancellationToken cancellationToken);

    Task RetireInstallationAsync(Guid installationId, CancellationToken cancellationToken);

    Task SaveSessionAsync(
        ManagedBrowserSessionReference session,
        CancellationToken cancellationToken);

    Task<ManagedBrowserSessionReference?> LoadSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken);

    Task ActivateSessionAsync(Guid sessionId, CancellationToken cancellationToken);

    Task RetireSessionAsync(Guid sessionId, CancellationToken cancellationToken);

    Task RetainAsync(Guid installationId, Guid sessionId, CancellationToken cancellationToken);
}
