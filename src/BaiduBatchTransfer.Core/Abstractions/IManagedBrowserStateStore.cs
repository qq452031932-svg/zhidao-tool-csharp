using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Core.Abstractions;

public interface IManagedBrowserStateStore
{
    Task<StoredBrowserInstallation> CreateInstallationAsync(
        BrowserInstallationIdentity installation,
        CancellationToken cancellationToken);

    Task<StoredBrowserInstallation?> LoadInstallationAsync(
        Guid installationId,
        CancellationToken cancellationToken);

    Task<StoredBrowserInstallation?> LoadActiveInstallationAsync(CancellationToken cancellationToken);

    Task<ManagedBrowserActiveSlotTransitionResult<StoredBrowserInstallation>> TryReplaceActiveInstallationAsync(
        ManagedBrowserInstallationActivationRequest request,
        CancellationToken cancellationToken);

    Task<ManagedBrowserTransitionResult> TryRetireInstallationAsync(
        ManagedBrowserTransitionRequest request,
        CancellationToken cancellationToken);

    Task<StoredManagedBrowserSession> CreateSessionAsync(
        ManagedBrowserSessionReference session,
        CancellationToken cancellationToken);

    Task<StoredManagedBrowserSession?> LoadSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken);

    Task<StoredManagedBrowserSession?> LoadActiveSessionAsync(
        Guid accountId,
        BrowserFamily browserFamily,
        CancellationToken cancellationToken);

    Task<ManagedBrowserActiveSlotTransitionResult<StoredManagedBrowserSession>> TryReplaceActiveSessionAsync(
        ManagedBrowserSessionActivationRequest request,
        CancellationToken cancellationToken);

    Task<ManagedBrowserTransitionResult> TryRetireSessionAsync(
        ManagedBrowserTransitionRequest request,
        CancellationToken cancellationToken);

    Task<ManagedBrowserTransitionResult> TryClaimRetiredIdentityForCleanupAsync(
        ManagedBrowserCleanupClaimRequest request,
        CancellationToken cancellationToken);

    Task<StoredManagedProfileCleanupManifest> CreateCleanupManifestAsync(
        ManagedProfileCleanupManifest manifest,
        CancellationToken cancellationToken);

    Task<StoredManagedProfileCleanupManifest?> LoadCleanupManifestAsync(
        Guid manifestId,
        CancellationToken cancellationToken);

    Task<ManagedBrowserTransitionResult> TryTransitionCleanupManifestAsync(
        ManagedProfileCleanupTransitionRequest request,
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
