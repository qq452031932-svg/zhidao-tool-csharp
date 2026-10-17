using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Infrastructure.Database.Repositories;
using Microsoft.Data.Sqlite;

namespace BaiduBatchTransfer.Infrastructure.Database;

public sealed partial class SqliteManagedBrowserStateStore
{
    /// <summary>No credential extraction/protection. Account creation and session activation share one writer transaction.</summary>
    public async Task<ManagedBrowserBindingStatus> BindLoginAsync(bool addNew, BrowserInstallationIdentity installation,
        ManagedBrowserSessionReference session, StoredManagedBrowserSession? expectedCurrent, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(session);
        if (installation.InstallationId != session.InstallationId || installation.BrowserFamily != session.BrowserFamily)
            return ManagedBrowserBindingStatus.Conflict;
        await using var connection = await factory.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            var accounts = new SqliteAccountRepository(connection, transaction);
            var account = await accounts.GetByIdAsync(session.AccountId, token).ConfigureAwait(false);
            if (addNew)
            {
                if (account is not null || expectedCurrent is not null) return ManagedBrowserBindingStatus.Conflict;
                var duplicate = await accounts.GetByBaiduUserIdAsync(session.StableBaiduUserId, token).ConfigureAwait(false);
                if (duplicate is not null && !duplicate.IsRemovedTombstone)
                    return ManagedBrowserBindingStatus.DuplicateIdentity;
                if (duplicate is not null)
                    await accounts.UpsertAsync(duplicate.WithReleasedIdentity(), token).ConfigureAwait(false);
            }
            else
            {
                if (account is null || account.IsDeleted || !account.IsEnabled || account.Status != "Active")
                    return ManagedBrowserBindingStatus.AccountUnavailable;
                if (!string.Equals(account.BaiduUserId, session.StableBaiduUserId, StringComparison.Ordinal))
                    return ManagedBrowserBindingStatus.AccountIdentityMismatch;
            }
            var current = await LoadActiveSessionAsync(connection, transaction, session.AccountId, session.BrowserFamily, token).ConfigureAwait(false);
            if ((expectedCurrent is null) != (current is null) ||
                (expectedCurrent is not null && !ExactSessionEquals(current, expectedCurrent)))
                return ManagedBrowserBindingStatus.Conflict;
            // Existing profile/session identities are immutable. Relogin validates them; it does not rewrite snapshots.
            if (current is not null)
            {
                var existingInstallation = await LoadInstallationAsync(connection, transaction, current.Identity.InstallationId, token).ConfigureAwait(false);
                return existingInstallation is { State: BrowserInstallationLifecycleState.Active } &&
                    SameLoginExecutable(existingInstallation.Identity, installation) &&
                    SameLoginSession(current.Identity, session)
                    ? ManagedBrowserBindingStatus.Bound : ManagedBrowserBindingStatus.Conflict;
            }
            var otherFamily = session.BrowserFamily == BrowserFamily.Chrome ? BrowserFamily.Edge : BrowserFamily.Chrome;
            if (await LoadActiveSessionAsync(connection, transaction, session.AccountId, otherFamily, token).ConfigureAwait(false) is not null)
                return ManagedBrowserBindingStatus.Conflict; // Family changes exclusively use Task-6 atomic retire/insert.
            var byFile = await LoadInstallationByFileIdentityAsync(connection, transaction, installation.FileIdentity, token).ConfigureAwait(false);
            if (byFile is not null && !SameLoginExecutable(byFile.Identity, installation))
                return ManagedBrowserBindingStatus.Conflict;
            var activeInstallation = await LoadActiveInstallationAsync(connection, transaction, token).ConfigureAwait(false);
            if (activeInstallation is not null && (byFile is null ||
                activeInstallation.Identity.InstallationId != byFile.Identity.InstallationId))
                return ManagedBrowserBindingStatus.Conflict;
            var now = DateTimeOffset.UtcNow.ToString("O");
            if (addNew)
                await accounts.UpsertAsync(AccountRecord.Create(session.AccountId, session.StableBaiduUserId, "百度网盘账号") with
                { LoginMethod = "ManagedChromium", LastValidatedAt = DateTimeOffset.UtcNow }, token).ConfigureAwait(false);
            if (byFile is null)
                await InsertFamilySwitchInstallationAsync(connection, transaction, installation, now, token).ConfigureAwait(false);
            var effective = byFile?.Identity ?? installation;
            var boundSession = RebindInstallation(session, effective.InstallationId);
            await InsertFamilySwitchSessionAsync(connection, transaction, boundSession, now, token).ConfigureAwait(false);
            if (activeInstallation is null &&
                await UpdateInstallationStateAsync(connection, transaction, effective.InstallationId, byFile?.Revision ?? 0,
                    BrowserInstallationLifecycleState.Retired, BrowserInstallationLifecycleState.Active, now, token).ConfigureAwait(false) != 1)
                return ManagedBrowserBindingStatus.Conflict;
            if (await UpdateSessionStateAsync(connection, transaction, boundSession.SessionId, 0,
                    ManagedBrowserSessionLifecycleState.Retired, ManagedBrowserSessionLifecycleState.Active, now, true, token).ConfigureAwait(false) != 1)
                return ManagedBrowserBindingStatus.Conflict;
            token.ThrowIfCancellationRequested();
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return ManagedBrowserBindingStatus.Bound;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            return ManagedBrowserBindingStatus.Conflict; // Dispose rolls back every write, including newly reserved accounts.
        }
    }

    private static bool SameLoginExecutable(BrowserInstallationIdentity left, BrowserInstallationIdentity right) =>
        left.BrowserFamily == right.BrowserFamily && left.FileIdentity == right.FileIdentity &&
        string.Equals(left.ExecutablePath, right.ExecutablePath, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.InstallationDirectory, right.InstallationDirectory, StringComparison.OrdinalIgnoreCase) &&
        left.Publisher == right.Publisher && left.ProductName == right.ProductName;

    private static bool SameLoginSession(ManagedBrowserSessionReference left, ManagedBrowserSessionReference right) =>
        left.SessionId == right.SessionId && left.AccountId == right.AccountId && left.InstallationId == right.InstallationId &&
        left.BrowserFamily == right.BrowserFamily && left.StableBaiduUserId == right.StableBaiduUserId &&
        left.ProfileId == right.ProfileId && left.ProfileOwnershipId == right.ProfileOwnershipId;
}
