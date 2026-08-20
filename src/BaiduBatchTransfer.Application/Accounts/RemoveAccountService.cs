using BaiduBatchTransfer.Core.Abstractions;

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
