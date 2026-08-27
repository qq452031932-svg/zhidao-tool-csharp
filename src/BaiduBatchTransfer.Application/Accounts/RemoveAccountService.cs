using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using System.Security.Cryptography;
using System.Text;

namespace BaiduBatchTransfer.Application.Accounts;

public enum RemoveAccountStatus
{
    Removed,
    AlreadyRemoved,
    NotFound,
    CleanupPending
}

public sealed record RemoveAccountResult(RemoveAccountStatus Status, string SafeMessage)
{
    public override string ToString() =>
        $"RemoveAccountResult {{ Status = {Status}, SensitiveData = [REDACTED] }}";
}

public sealed class RemoveAccountService
{
    private readonly IUnitOfWorkFactory? unitOfWorkFactory;
    private readonly IWebViewProfileStore profileStore;
    private readonly IAccountProfilePathPolicy profilePaths;
    private readonly IManagedBrowserStateStore? managedState;
    private readonly IManagedBrowserProfileStore? managedProfiles;
    private readonly byte[] confirmationKey = RandomNumberGenerator.GetBytes(32);

    public RemoveAccountService(
        IUnitOfWorkFactory unitOfWorkFactory,
        IWebViewProfileStore profileStore,
        IAccountProfilePathPolicy profilePaths)
    {
        this.unitOfWorkFactory = unitOfWorkFactory ?? throw new ArgumentNullException(nameof(unitOfWorkFactory));
        this.profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
        this.profilePaths = profilePaths ?? throw new ArgumentNullException(nameof(profilePaths));
    }

    internal RemoveAccountService(
        IWebViewProfileStore profileStore,
        IAccountProfilePathPolicy profilePaths)
    {
        this.profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
        this.profilePaths = profilePaths ?? throw new ArgumentNullException(nameof(profilePaths));
    }

    public RemoveAccountService(
        IUnitOfWorkFactory unitOfWorkFactory,
        IWebViewProfileStore profileStore,
        IAccountProfilePathPolicy profilePaths,
        IManagedBrowserStateStore managedState,
        IManagedBrowserProfileStore managedProfiles) :
        this(unitOfWorkFactory, profileStore, profilePaths)
    {
        this.managedState = managedState ?? throw new ArgumentNullException(nameof(managedState));
        this.managedProfiles = managedProfiles ?? throw new ArgumentNullException(nameof(managedProfiles));
    }

    internal RemoveAccountService(
        IWebViewProfileStore profileStore,
        IAccountProfilePathPolicy profilePaths,
        IManagedBrowserStateStore managedState,
        IManagedBrowserProfileStore managedProfiles) : this(profileStore, profilePaths)
    {
        this.managedState = managedState ?? throw new ArgumentNullException(nameof(managedState));
        this.managedProfiles = managedProfiles ?? throw new ArgumentNullException(nameof(managedProfiles));
    }

    public async Task<ManagedBrowserProfileCleanupResult> RecoverManagedProfileAsync(
        Guid manifestId,
        CancellationToken cancellationToken)
    {
        if (manifestId == Guid.Empty || managedState is null || managedProfiles is null)
            return new(ManagedBrowserProfileCleanupStatus.NotFound, manifestId, null);

        var loaded = await LoadManagedPreparationAsync(manifestId, cancellationToken).ConfigureAwait(false);
        if (loaded is null)
            return new(ManagedBrowserProfileCleanupStatus.NotFound, manifestId, null);
        var (stored, preparation) = loaded.Value;
        if (stored.State == ManagedProfileCleanupState.Quarantined)
            return new(ManagedBrowserProfileCleanupStatus.Quarantined, manifestId, stored.Revision);
        if (stored.State == ManagedProfileCleanupState.Deleted)
            return new(ManagedBrowserProfileCleanupStatus.Deleted, manifestId, stored.Revision);
        if (stored.State == ManagedProfileCleanupState.CleanupPending)
            return new(ManagedBrowserProfileCleanupStatus.CleanupPending, manifestId, stored.Revision);
        if (stored.State != ManagedProfileCleanupState.QuarantineMoveIntent)
            return await MarkCleanupPendingAsync(stored, cancellationToken).ConfigureAwait(false);

        ManagedBrowserProfileLocationStatus location;
        try
        {
            location = await managedProfiles.ObserveAsync(preparation, cancellationToken).ConfigureAwait(false);
            if (location == ManagedBrowserProfileLocationStatus.SourceOnly)
            {
                var moved = await managedProfiles.MoveToQuarantineAsync(preparation, cancellationToken).ConfigureAwait(false);
                if (moved is not (ManagedBrowserProfileMoveStatus.Moved or ManagedBrowserProfileMoveStatus.AlreadyQuarantined))
                    return await MarkCleanupPendingAsync(stored, cancellationToken).ConfigureAwait(false);
            }
            else if (location != ManagedBrowserProfileLocationStatus.QuarantineOnly)
            {
                return await MarkCleanupPendingAsync(stored, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return await MarkCleanupPendingAsync(stored, cancellationToken).ConfigureAwait(false);
        }

        var completed = await managedState.TryTransitionCleanupManifestAsync(
            new ManagedProfileCleanupTransitionRequest(
                manifestId,
                stored.Revision,
                ManagedProfileCleanupState.QuarantineMoveIntent,
                ManagedProfileCleanupState.Quarantined),
            cancellationToken).ConfigureAwait(false);
        return completed.Status == ManagedBrowserTransitionStatus.Applied
            ? new(ManagedBrowserProfileCleanupStatus.Quarantined, manifestId, completed.CurrentRevision)
            : new(ManagedBrowserProfileCleanupStatus.Conflict, manifestId, completed.CurrentRevision);
    }

    public async Task<ManagedBrowserProfileDeletionConfirmation?> CreateManagedProfileDeletionConfirmationAsync(
        Guid manifestId,
        CancellationToken cancellationToken)
    {
        if (manifestId == Guid.Empty || managedState is null)
            return null;
        var stored = await managedState.LoadCleanupManifestAsync(manifestId, cancellationToken).ConfigureAwait(false);
        if (stored?.State != ManagedProfileCleanupState.Quarantined)
            return null;
        return new ManagedBrowserProfileDeletionConfirmation(
            manifestId,
            stored.Revision,
            CreateConfirmationToken(manifestId, stored.Revision));
    }

    public async Task<ManagedBrowserProfileDeletionPreparation> PrepareManagedProfileDeletionAsync(
        Guid accountId,
        CancellationToken cancellationToken)
    {
        if (accountId == Guid.Empty || managedState is null)
            return new(ManagedBrowserProfileCleanupStatus.NotFound, null);
        var stored = await managedState.LoadLatestCleanupManifestAsync(accountId, cancellationToken)
            .ConfigureAwait(false);
        if (stored is null)
            return new(ManagedBrowserProfileCleanupStatus.NotFound, null);
        var recovery = await RecoverManagedProfileAsync(stored.Manifest.ManifestId, cancellationToken)
            .ConfigureAwait(false);
        var confirmation = recovery.Status == ManagedBrowserProfileCleanupStatus.Quarantined
            ? await CreateManagedProfileDeletionConfirmationAsync(
                stored.Manifest.ManifestId, cancellationToken).ConfigureAwait(false)
            : null;
        return new(recovery.Status, confirmation);
    }

    public Task<ManagedBrowserProfileCleanupResult> DeleteConfirmedManagedProfileAsync(
        ManagedBrowserProfileDeletionConfirmation confirmation,
        CancellationToken cancellationToken) =>
        DeleteConfirmedManagedProfileCoreAsync(null, confirmation, cancellationToken);

    public Task<ManagedBrowserProfileCleanupResult> DeleteConfirmedManagedProfileAsync(
        Guid expectedAccountId,
        ManagedBrowserProfileDeletionConfirmation confirmation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        if (expectedAccountId == Guid.Empty)
            return Task.FromResult(new ManagedBrowserProfileCleanupResult(
                ManagedBrowserProfileCleanupStatus.Conflict,
                confirmation.ManifestId,
                null));
        return DeleteConfirmedManagedProfileCoreAsync(
            expectedAccountId, confirmation, cancellationToken);
    }

    private async Task<ManagedBrowserProfileCleanupResult> DeleteConfirmedManagedProfileCoreAsync(
        Guid? expectedAccountId,
        ManagedBrowserProfileDeletionConfirmation confirmation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        if (managedState is null || managedProfiles is null ||
            !TokenEquals(CreateConfirmationToken(confirmation.ManifestId, confirmation.ExpectedRevision),
                confirmation.ConfirmationToken))
            return new(ManagedBrowserProfileCleanupStatus.Conflict, confirmation.ManifestId, null);

        var loaded = await LoadManagedPreparationAsync(confirmation.ManifestId, cancellationToken).ConfigureAwait(false);
        if (loaded is null)
            return new(ManagedBrowserProfileCleanupStatus.NotFound, confirmation.ManifestId, null);
        var (stored, preparation) = loaded.Value;
        if ((expectedAccountId is { } accountId && stored.Manifest.AccountId != accountId) ||
            stored.State != ManagedProfileCleanupState.Quarantined ||
            stored.Revision != confirmation.ExpectedRevision)
            return new(ManagedBrowserProfileCleanupStatus.Conflict, confirmation.ManifestId, stored.Revision);

        var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(confirmation.ConfirmationToken)));
        var intent = await managedState.TryTransitionCleanupManifestAsync(
            new ManagedProfileCleanupTransitionRequest(
                confirmation.ManifestId,
                stored.Revision,
                ManagedProfileCleanupState.Quarantined,
                ManagedProfileCleanupState.DeleteIntent,
                tokenHash),
            cancellationToken).ConfigureAwait(false);
        if (intent.Status != ManagedBrowserTransitionStatus.Applied)
            return new(ManagedBrowserProfileCleanupStatus.Conflict, confirmation.ManifestId, intent.CurrentRevision);

        var deleteStatus = ManagedBrowserProfileDeleteStatus.IsolationRequired;
        try
        {
            deleteStatus = await managedProfiles.DeleteQuarantinedAsync(
                preparation,
                tokenHash,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The durable DeleteIntent below is converted to CleanupPending for manual recovery.
        }

        var target = deleteStatus is (ManagedBrowserProfileDeleteStatus.Deleted or ManagedBrowserProfileDeleteStatus.AlreadyDeleted)
            ? ManagedProfileCleanupState.Deleted
            : ManagedProfileCleanupState.CleanupPending;
        var completion = await managedState.TryTransitionCleanupManifestAsync(
            new ManagedProfileCleanupTransitionRequest(
                confirmation.ManifestId,
                intent.CurrentRevision!.Value,
                ManagedProfileCleanupState.DeleteIntent,
                target),
            cancellationToken).ConfigureAwait(false);
        if (completion.Status != ManagedBrowserTransitionStatus.Applied)
            return new(ManagedBrowserProfileCleanupStatus.Conflict, confirmation.ManifestId, completion.CurrentRevision);
        return new(
            target == ManagedProfileCleanupState.Deleted
                ? ManagedBrowserProfileCleanupStatus.Deleted
                : ManagedBrowserProfileCleanupStatus.CleanupPending,
            confirmation.ManifestId,
            completion.CurrentRevision);
    }

    private async Task<(StoredManagedProfileCleanupManifest Manifest, ManagedBrowserProfileCleanupPreparation Preparation)?>
        LoadManagedPreparationAsync(Guid manifestId, CancellationToken cancellationToken)
    {
        var manifest = await managedState!.LoadCleanupManifestAsync(manifestId, cancellationToken).ConfigureAwait(false);
        if (manifest is null)
            return null;
        var storedSession = await managedState.LoadSessionAsync(
            manifest.Manifest.SessionId, cancellationToken).ConfigureAwait(false);
        if (storedSession is null ||
            storedSession.Identity.AccountId != manifest.Manifest.AccountId ||
            !string.Equals(storedSession.Identity.ProfileOwnershipId,
                manifest.Manifest.ExpectedOwnershipId, StringComparison.Ordinal))
            return null;
        var installation = await managedState.LoadInstallationAsync(
            storedSession.Identity.InstallationId, cancellationToken).ConfigureAwait(false);
        if (installation is null ||
            installation.Identity.BrowserFamily != storedSession.Identity.BrowserFamily)
            return null;
        var exact = new ManagedBrowserLifecycleSession(
            storedSession.Identity,
            installation.Revision,
            storedSession.Revision,
            manifest.Manifest.SourceProfilePath);
        return (manifest, new ManagedBrowserProfileCleanupPreparation(
            exact,
            manifest.Manifest.ManifestId,
            manifest.Revision,
            manifest.Manifest.QuarantinePath));
    }

    private async Task<ManagedBrowserProfileCleanupResult> MarkCleanupPendingAsync(
        StoredManagedProfileCleanupManifest stored,
        CancellationToken cancellationToken)
    {
        if (stored.State is not (ManagedProfileCleanupState.QuarantineMoveIntent or ManagedProfileCleanupState.DeleteIntent))
            return new(ManagedBrowserProfileCleanupStatus.Conflict, stored.Manifest.ManifestId, stored.Revision);
        var transition = await managedState!.TryTransitionCleanupManifestAsync(
            new ManagedProfileCleanupTransitionRequest(
                stored.Manifest.ManifestId,
                stored.Revision,
                stored.State,
                ManagedProfileCleanupState.CleanupPending),
            cancellationToken).ConfigureAwait(false);
        return transition.Status == ManagedBrowserTransitionStatus.Applied
            ? new(ManagedBrowserProfileCleanupStatus.CleanupPending, stored.Manifest.ManifestId, transition.CurrentRevision)
            : new(ManagedBrowserProfileCleanupStatus.Conflict, stored.Manifest.ManifestId, transition.CurrentRevision);
    }

    private string CreateConfirmationToken(Guid manifestId, long revision)
    {
        using var hmac = new HMACSHA256(confirmationKey);
        return Convert.ToBase64String(hmac.ComputeHash(
            Encoding.UTF8.GetBytes($"{manifestId:D}:{revision}")));
    }

    private static bool TokenEquals(string expected, string supplied)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        return expectedBytes.Length == suppliedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }

    public async Task<RemoveAccountResult> RemoveAsync(
        Guid accountId,
        DateTimeOffset removedAt,
        CancellationToken cancellationToken)
    {
        if (accountId == Guid.Empty) return new(RemoveAccountStatus.NotFound, "The account was not found.");

        var account = await DisableAndClearCredentialsAsync(accountId, cancellationToken).ConfigureAwait(false);
        if (account is null) return new(RemoveAccountStatus.NotFound, "The account was not found.");
        if (account.IsDeleted) return new(RemoveAccountStatus.AlreadyRemoved, "The account was already removed.");

        var profilePath = account.WebView2ProfileDirectory;
        if (profilePath is not null)
        {
            try
            {
                profilePaths.EnsureSafeAccountPath(account.Id, profilePath);
            }
            catch (Exception exception) when (IsUnsafePathException(exception))
            {
                return new(RemoveAccountStatus.CleanupPending, "Profile cleanup is pending because its path is not safe.");
            }
        }

        try
        {
            await CloseAndDeleteProfileAsync(
                account.Id,
                profilePath,
                validateAfterClose: false,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new(RemoveAccountStatus.CleanupPending, "The account is disabled and credential-free; profile cleanup is pending.");
        }

        try
        {
            await TombstoneAsync(account.Id, removedAt, cancellationToken).ConfigureAwait(false);
            return new(RemoveAccountStatus.Removed, "The account was removed.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new(RemoveAccountStatus.CleanupPending, "Profile cleanup completed; account finalization is pending.");
        }
    }

    internal async Task<RemoveAccountResult> FinalizePreparedAsync(
        Guid accountId,
        DateTimeOffset removedAt,
        IAccountRemovalStore removalStore,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(removalStore);
        if (accountId == Guid.Empty)
            return new(
                RemoveAccountStatus.CleanupPending,
                "The prepared account snapshot could not be finalized safely.");

        try
        {
            await removalStore.CompletePreparedRemovalAsync(
                accountId,
                removedAt,
                (leasedAccountId, leasedProfilePath, token) => CloseAndDeleteProfileAsync(
                    leasedAccountId,
                    leasedProfilePath,
                    validateAfterClose: true,
                    token),
                cancellationToken).ConfigureAwait(false);
            return new(RemoveAccountStatus.Removed, "The account was removed.");
        }
        catch (Exception)
        {
            return new(RemoveAccountStatus.CleanupPending, "The account is disabled and credential-free; profile cleanup is pending.");
        }
    }

    private async Task CloseAndDeleteProfileAsync(
        Guid accountId,
        string? profilePath,
        bool validateAfterClose,
        CancellationToken cancellationToken)
    {
        await profileStore.CloseAsync(accountId, cancellationToken).ConfigureAwait(false);
        if (profilePath is null) return;

        if (validateAfterClose)
            profilePaths.EnsureSafeAccountPath(accountId, profilePath);
        await profileStore.DeleteAsync(accountId, profilePath, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsUnsafePathException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;

    private async Task<AccountRecord?> DisableAndClearCredentialsAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await using var unit = await unitOfWorkFactory!.BeginAsync(cancellationToken).ConfigureAwait(false);
        var account = await unit.Accounts.GetByIdAsync(accountId, cancellationToken).ConfigureAwait(false);
        if (account is null || account.IsDeleted)
        {
            await unit.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return account;
        }

        var disabled = account with
        {
            EncryptedSession = null,
            Status = "Disabled",
            IsEnabled = false,
            LastValidatedAt = null
        };
        await unit.Accounts.UpsertAsync(disabled, cancellationToken).ConfigureAwait(false);
        await unit.CommitAsync(cancellationToken).ConfigureAwait(false);
        return disabled;
    }

    private async Task TombstoneAsync(Guid accountId, DateTimeOffset removedAt, CancellationToken cancellationToken)
    {
        await using var unit = await unitOfWorkFactory!.BeginAsync(cancellationToken).ConfigureAwait(false);
        var account = await unit.Accounts.GetByIdAsync(accountId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The disabled account disappeared during cleanup.");
        if (account.IsDeleted)
        {
            await unit.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var tombstone = account with
        {
            DisplayName = "Removed account",
            EncryptedSession = null,
            WebView2ProfileDirectory = null,
            Status = "Deleted",
            IsEnabled = false,
            IsDeleted = true,
            DeletedAt = removedAt.ToUniversalTime(),
            LastValidatedAt = null
        };
        await unit.Accounts.UpsertAsync(tombstone, cancellationToken).ConfigureAwait(false);
        await unit.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
