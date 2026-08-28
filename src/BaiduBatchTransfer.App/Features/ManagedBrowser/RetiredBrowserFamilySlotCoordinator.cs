using BaiduBatchTransfer.Application.Accounts;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Infrastructure.Security;

namespace BaiduBatchTransfer.App.Features.ManagedBrowser;

internal enum ManagedProfileOwnership { None, Matching, DifferentFamily, Conflict }

/// <summary>Reclaims only a persisted retired family slot by retaining it in Task 6 quarantine.</summary>
public sealed class RetiredBrowserFamilySlotCoordinator(IManagedBrowserStateStore store,
    ManagedChromiumProcessManager manager, IManagedBrowserProfilePolicy profiles,
    IManagedBrowserProfileStore cleanupProfiles, RemoveAccountService cleanup, string appDataRoot)
{
    // The caller holds the account lifecycle lease throughout this operation and the subsequent switch.
    public async Task<bool> PrepareAsync(ManagedBrowserLifecycleSession current, BrowserFamily targetFamily,
        CancellationToken token)
    {
        if (current.BrowserFamily == targetFamily) return false;
        var retained = await store.LoadRetainedFamilySlotAsync(current.AccountId, targetFamily, token).ConfigureAwait(false);
        var presence = await profiles.ProbeExistingAsync(current.AccountId, targetFamily, token).ConfigureAwait(false);
        if (presence == ManagedBrowserProfilePresence.Uncertain) return false;
        if (retained is null) return presence == ManagedBrowserProfilePresence.Absent;
        if (retained.State == ManagedBrowserSessionLifecycleState.CleanupPending ||
            retained.Identity.StableBaiduUserId != current.Session.StableBaiduUserId) return false;
        var installation = await store.LoadInstallationAsync(retained.Identity.InstallationId, token).ConfigureAwait(false);
        if (installation is null || installation.Identity.BrowserFamily != targetFamily) return false;
        var descriptor = ManagedBrowserProfileDescriptor.FromStoredSession(retained.Identity, appDataRoot);
        var ownership = await manager.ClassifyOwnedProfileAsync(descriptor, token).ConfigureAwait(false);
        if (ownership is ManagedProfileOwnership.Matching or ManagedProfileOwnership.Conflict) return false;
        // None/DifferentFamily proves only absence from this manager. Existing pinned rename/marker
        // validation must still reject sharing, replacement, or unknown external use without killing it.
        var exact = new ManagedBrowserLifecycleSession(retained.Identity, installation.Revision, retained.Revision,
            descriptor.CanonicalSourcePath);
        var preparation = cleanupProfiles.PrepareRemoval(exact);
        var manifest = await store.LoadCleanupManifestAsync(preparation.ManifestId, token).ConfigureAwait(false);
        if (manifest is null)
        {
            if (retained.State != ManagedBrowserSessionLifecycleState.Retired ||
                (await cleanupProfiles.ObserveAsync(preparation, token).ConfigureAwait(false)) != ManagedBrowserProfileLocationStatus.SourceOnly)
                return false;
            manifest = await store.CreateCleanupManifestAsync(new(preparation.ManifestId, current.AccountId,
                retained.Identity.SessionId, preparation.SourceProfilePath, preparation.QuarantinePath,
                retained.Identity.ProfileOwnershipId, null), token).ConfigureAwait(false);
        }
        if (manifest.State == ManagedProfileCleanupState.Prepared)
        {
            if (retained.State != ManagedBrowserSessionLifecycleState.Retired ||
                (await cleanupProfiles.ObserveAsync(preparation, token).ConfigureAwait(false)) != ManagedBrowserProfileLocationStatus.SourceOnly)
                return false;
            var claim = await store.TryClaimRetiredIdentityForCleanupAsync(new(installation.Identity.InstallationId,
                retained.Identity.SessionId, current.AccountId, installation.Revision, retained.Revision,
                manifest.Manifest.ManifestId, manifest.Revision), token).ConfigureAwait(false);
            if (claim.Status != ManagedBrowserTransitionStatus.Applied) return false;
        }
        else if (manifest.State != ManagedProfileCleanupState.QuarantineMoveIntent) return false;
        var recovered = await cleanup.RecoverManagedProfileAsync(preparation.ManifestId, token).ConfigureAwait(false);
        return recovered.Status == ManagedBrowserProfileCleanupStatus.Quarantined &&
            await profiles.ProbeExistingAsync(current.AccountId, targetFamily, token).ConfigureAwait(false) == ManagedBrowserProfilePresence.Absent;
    }
}
