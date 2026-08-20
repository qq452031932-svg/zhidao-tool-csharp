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

        var preparation = new AccountRemovalPreparation(
            AccountRemovalPreparationStatus.Prepared,
            account.Id,
            account.WebView2ProfileDirectory,
            true,
            "The account is disabled and credential-free.");
        return await FinalizePreparedCoreAsync(
            preparation,
            removedAt,
            TombstoneAsync,
            cancellationToken).ConfigureAwait(false);
    }

    public Task<RemoveAccountResult> FinalizePreparedAsync(
        AccountRemovalPreparation preparation,
        DateTimeOffset removedAt,
        IAccountRemovalStore removalStore,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(removalStore);

        return FinalizePreparedCoreAsync(
            preparation,
            removedAt,
            removalStore.FinalizeTombstoneAsync,
            cancellationToken);
    }

    private async Task<RemoveAccountResult> FinalizePreparedCoreAsync(
        AccountRemovalPreparation preparation,
        DateTimeOffset removedAt,
        Func<Guid, DateTimeOffset, CancellationToken, Task> finalizeTombstone,
        CancellationToken cancellationToken)
    {
        if (preparation.Status != AccountRemovalPreparationStatus.Prepared ||
            !preparation.DatabasePrepared ||
            preparation.AccountId == Guid.Empty)
        {
            return new(
                RemoveAccountStatus.CleanupPending,
                "The prepared account snapshot could not be finalized safely.");
        }

        try
        {
            await profileStore.CloseAsync(preparation.AccountId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return new(RemoveAccountStatus.CleanupPending, "The account is disabled and credential-free; profile cleanup is pending.");
        }

        if (preparation.ProfilePath is not null)
        {
            try
            {
                profilePaths.EnsureSafeAccountPath(preparation.AccountId, preparation.ProfilePath);
                await profileStore.DeleteAsync(
                    preparation.AccountId,
                    preparation.ProfilePath,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return new(RemoveAccountStatus.CleanupPending, "The account is disabled and credential-free; profile cleanup is pending.");
            }
        }

        try
        {
            await finalizeTombstone(
                preparation.AccountId,
                removedAt,
                cancellationToken).ConfigureAwait(false);
            return new(RemoveAccountStatus.Removed, "The account was removed.");
        }
        catch (Exception)
        {
            return new(RemoveAccountStatus.CleanupPending, "Profile cleanup completed; account finalization is pending.");
        }
    }

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
