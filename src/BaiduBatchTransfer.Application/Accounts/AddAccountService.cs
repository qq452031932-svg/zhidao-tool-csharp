using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Accounts;

public sealed record AccountProfileOwnership
{
    private AccountProfileOwnership(Guid token) => Token = token;
    public Guid Token { get; }

    public static AccountProfileOwnership NewLoginProfile(Guid token)
    {
        if (token == Guid.Empty) throw new ArgumentException("A profile ownership token is required.", nameof(token));
        return new(token);
    }
}

public enum ProfileCleanupStatus
{
    NotRequired,
    Completed,
    Pending,
    SkippedExistingAccountProtection
}

public sealed record AddAccountRequest(
    Guid AccountId,
    AccountSession Session,
    DateTimeOffset CreatedAt,
    AccountProfileOwnership ProfileOwnership,
    bool PersistBrowserProfile = true);

public enum AddAccountStatus
{
    Added,
    InvalidIdentity,
    DuplicateIdentity,
    AccountIdCollision,
    SessionRejected,
    InvalidProfilePath
}

public sealed record AddAccountResult(
    AddAccountStatus Status,
    Guid? AccountId,
    string SafeMessage,
    ProfileCleanupStatus ProfileCleanupStatus = ProfileCleanupStatus.NotRequired)
{
    public override string ToString() =>
        $"AddAccountResult {{ Status = {Status}, AccountId = {AccountId}, ProfileCleanupStatus = {ProfileCleanupStatus}, SensitiveData = [REDACTED] }}";
}

public sealed class AddAccountOperationException : Exception
{
    public AddAccountOperationException(Guid accountId, ProfileCleanupStatus profileCleanupStatus, Exception innerException) :
        base("Account addition failed. The profile cleanup status is available separately.", innerException)
    {
        AccountId = accountId;
        ProfileCleanupStatus = profileCleanupStatus;
    }

    public Guid AccountId { get; }
    public ProfileCleanupStatus ProfileCleanupStatus { get; }
}

public sealed class AddAccountCanceledException : OperationCanceledException
{
    public AddAccountCanceledException(
        Guid accountId,
        ProfileCleanupStatus profileCleanupStatus,
        OperationCanceledException innerException) :
        base("Account addition was canceled. The profile cleanup status is available separately.", innerException, innerException.CancellationToken)
    {
        AccountId = accountId;
        ProfileCleanupStatus = profileCleanupStatus;
    }

    public Guid AccountId { get; }
    public ProfileCleanupStatus ProfileCleanupStatus { get; }
}

public sealed class AddAccountService
{
    private const int MaximumStableUserIdLength = 256;
    private readonly IBaiduNetdiskAdapter adapter;
    private readonly IUnitOfWorkFactory unitOfWorkFactory;
    private readonly ISessionProtector sessionProtector;
    private readonly IWebViewProfileStore profileStore;
    private readonly IAccountProfilePathPolicy profilePaths;

    public AddAccountService(
        IBaiduNetdiskAdapter adapter,
        IUnitOfWorkFactory unitOfWorkFactory,
        ISessionProtector sessionProtector,
        IWebViewProfileStore profileStore,
        IAccountProfilePathPolicy profilePaths)
    {
        this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        this.unitOfWorkFactory = unitOfWorkFactory ?? throw new ArgumentNullException(nameof(unitOfWorkFactory));
        this.sessionProtector = sessionProtector ?? throw new ArgumentNullException(nameof(sessionProtector));
        this.profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
        this.profilePaths = profilePaths ?? throw new ArgumentNullException(nameof(profilePaths));
    }

    public async Task<AddAccountResult> AddAsync(AddAccountRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var result = await AddCoreAsync(request, cancellationToken).ConfigureAwait(false);
            if (result.Status == AddAccountStatus.Added) return result;

            if (!request.PersistBrowserProfile) return result;

            var cleanup = await CleanupOwnedProfileAsync(
                request.AccountId, request.ProfileOwnership, CancellationToken.None).ConfigureAwait(false);
            return result with { ProfileCleanupStatus = cleanup };
        }
        catch (OperationCanceledException exception)
        {
            if (!request.PersistBrowserProfile)
                throw new AddAccountCanceledException(request.AccountId, ProfileCleanupStatus.NotRequired, exception);
            var cleanup = await CleanupOwnedProfileAsync(
                request.AccountId, request.ProfileOwnership, CancellationToken.None).ConfigureAwait(false);
            throw new AddAccountCanceledException(request.AccountId, cleanup, exception);
        }
        catch (Exception exception) when (exception is not AddAccountOperationException)
        {
            if (!request.PersistBrowserProfile)
                throw new AddAccountOperationException(request.AccountId, ProfileCleanupStatus.NotRequired, exception);
            var cleanup = await CleanupOwnedProfileAsync(
                request.AccountId, request.ProfileOwnership, CancellationToken.None).ConfigureAwait(false);
            throw new AddAccountOperationException(request.AccountId, cleanup, exception);
        }
    }

    public Task<ProfileCleanupStatus> RetryProfileCleanupAsync(
        Guid accountId,
        AccountProfileOwnership profileOwnership,
        CancellationToken cancellationToken) =>
        CleanupOwnedProfileAsync(accountId, profileOwnership, cancellationToken);

    private async Task<AddAccountResult> AddCoreAsync(AddAccountRequest request, CancellationToken cancellationToken)
    {
        if (request.AccountId == Guid.Empty)
            return new(AddAccountStatus.InvalidProfilePath, null, "A valid local account ID is required.");

        var validation = await adapter.ValidateSessionAsync(request.Session, cancellationToken).ConfigureAwait(false);
        if (!validation.IsSuccess || validation.Value is null)
            return new(AddAccountStatus.SessionRejected, null, "The account session could not be validated.");

        var identity = validation.Value;
        if (!IsStableIdentity(identity))
            return new(AddAccountStatus.InvalidIdentity, null, "The service did not report a stable account identity.");

        string? profilePath = null;
        if (request.PersistBrowserProfile)
        {
            try { profilePath = profilePaths.GetAccountProfilePath(request.AccountId); }
            catch (Exception exception) when (IsPathFailure(exception))
            {
                return new(AddAccountStatus.InvalidProfilePath, null, "The account profile path is not safe.");
            }
        }

        await using var unit = await unitOfWorkFactory.BeginAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existingId = await unit.Accounts.GetByIdAsync(request.AccountId, cancellationToken).ConfigureAwait(false);
            if (existingId is not null)
            {
                await unit.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new(AddAccountStatus.AccountIdCollision, null, "The local account ID is already in use.");
            }

            var duplicate = await unit.Accounts.GetByBaiduUserIdAsync(identity.UserId, cancellationToken).ConfigureAwait(false);
            if (duplicate is not null && !duplicate.IsRemovedTombstone)
            {
                await unit.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new(AddAccountStatus.DuplicateIdentity, duplicate.Id, "This Baidu identity is already an account.");
            }
            // 退出过的账号重新登录：和新账号一起提交，失败时一起回滚。
            if (duplicate is not null)
                await unit.Accounts.UpsertAsync(duplicate.WithReleasedIdentity(), cancellationToken).ConfigureAwait(false);

            var boundSession = request.Session.Bind(request.AccountId, identity.UserId);
            var protection = sessionProtector.Protect(boundSession);
            var encryptedSession = protection.ProtectedBytes;
            if (!protection.IsSuccess || encryptedSession is null)
            {
                await unit.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new(AddAccountStatus.SessionRejected, null, "The account session could not be saved safely.");
            }

            var account = AccountRecord.Create(request.AccountId, identity.UserId, identity.DisplayName) with
            {
                EncryptedSession = encryptedSession,
                WebView2ProfileDirectory = profilePath,
                LastValidatedAt = request.CreatedAt.ToUniversalTime(),
                DailyCountDateKey = request.CreatedAt.ToString("yyyy-MM-dd")
            };
            var committed = request.PersistBrowserProfile
                ? await profileStore.CommitOwnedAsync(
                    request.AccountId,
                    profilePath!,
                    request.ProfileOwnership.Token,
                    async token =>
                    {
                        await unit.Accounts.UpsertAsync(account, token).ConfigureAwait(false);
                        await unit.CommitAsync(token).ConfigureAwait(false);
                    },
                    cancellationToken).ConfigureAwait(false)
                : await CommitCookieOnlyAsync(unit, account, cancellationToken).ConfigureAwait(false);
            if (!committed)
            {
                await unit.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new(AddAccountStatus.AccountIdCollision, null, "The login profile is already being finalized.");
            }
            return new(AddAccountStatus.Added, request.AccountId, "The account was added.");
        }
        catch
        {
            try { await unit.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception) { /* Preserve the original operation failure. */ }
            throw;
        }
    }

    private static async Task<bool> CommitCookieOnlyAsync(
        IUnitOfWork unit,
        AccountRecord account,
        CancellationToken cancellationToken)
    {
        await unit.Accounts.UpsertAsync(account, cancellationToken).ConfigureAwait(false);
        await unit.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<ProfileCleanupStatus> CleanupOwnedProfileAsync(
        Guid accountId,
        AccountProfileOwnership profileOwnership,
        CancellationToken cancellationToken)
    {
        if (profileOwnership is null) return ProfileCleanupStatus.Pending;
        if (accountId == Guid.Empty)
            return ProfileCleanupStatus.Pending;

        try
        {
            await using (var unit = await unitOfWorkFactory.BeginAsync(CancellationToken.None).ConfigureAwait(false))
            {
                var existing = await unit.Accounts.GetByIdAsync(accountId, CancellationToken.None).ConfigureAwait(false);
                await unit.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                if (existing is not null) return ProfileCleanupStatus.SkippedExistingAccountProtection;
            }

            var cleanupFailed = false;
            try { await profileStore.CloseAsync(accountId, cancellationToken).ConfigureAwait(false); }
            catch (Exception) { cleanupFailed = true; }

            try
            {
                var profilePath = profilePaths.GetAccountProfilePath(accountId);
                var deletion = await profileStore.DeleteOwnedAsync(
                    accountId,
                    profilePath,
                    profileOwnership.Token,
                    token => ConfirmNoAccountAsync(accountId, token),
                    cancellationToken).ConfigureAwait(false);
                if (deletion == OwnedProfileDeletionStatus.OwnershipMismatch)
                    return ProfileCleanupStatus.SkippedExistingAccountProtection;
            }
            catch (Exception) { cleanupFailed = true; }

            return cleanupFailed ? ProfileCleanupStatus.Pending : ProfileCleanupStatus.Completed;
        }
        catch (Exception)
        {
            return ProfileCleanupStatus.Pending;
        }
    }

    private async Task<bool> ConfirmNoAccountAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await using var unit = await unitOfWorkFactory.BeginAsync(cancellationToken).ConfigureAwait(false);
        var existing = await unit.Accounts.GetByIdAsync(accountId, cancellationToken).ConfigureAwait(false);
        await unit.RollbackAsync(cancellationToken).ConfigureAwait(false);
        return existing is null;
    }

    private static bool IsPathFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;

    private static bool IsStableIdentity(BaiduAccountIdentity identity) =>
        !string.IsNullOrWhiteSpace(identity.UserId) &&
        identity.UserId.Length <= MaximumStableUserIdLength &&
        !identity.UserId.Any(char.IsControl) &&
        !string.IsNullOrWhiteSpace(identity.DisplayName) &&
        identity.DisplayName.Length <= 256 &&
        !identity.DisplayName.Any(char.IsControl);
}
