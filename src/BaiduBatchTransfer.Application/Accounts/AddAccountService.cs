using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Accounts;

public sealed record AddAccountRequest(Guid AccountId, AccountSession Session, DateTimeOffset CreatedAt);

public enum AddAccountStatus
{
    Added,
    InvalidIdentity,
    DuplicateIdentity,
    AccountIdCollision,
    SessionRejected,
    InvalidProfilePath
}

public sealed record AddAccountResult(AddAccountStatus Status, Guid? AccountId, string SafeMessage)
{
    public override string ToString() =>
        $"AddAccountResult {{ Status = {Status}, AccountId = {AccountId}, SensitiveData = [REDACTED] }}";
}

public sealed class AddAccountService
{
    private const int MaximumStableUserIdLength = 256;
    private readonly IBaiduNetdiskAdapter adapter;
    private readonly IUnitOfWorkFactory unitOfWorkFactory;
    private readonly ISessionProtector sessionProtector;
    private readonly IAccountProfilePathPolicy profilePaths;

    public AddAccountService(
        IBaiduNetdiskAdapter adapter,
        IUnitOfWorkFactory unitOfWorkFactory,
        ISessionProtector sessionProtector,
        IAccountProfilePathPolicy profilePaths)
    {
        this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        this.unitOfWorkFactory = unitOfWorkFactory ?? throw new ArgumentNullException(nameof(unitOfWorkFactory));
        this.sessionProtector = sessionProtector ?? throw new ArgumentNullException(nameof(sessionProtector));
        this.profilePaths = profilePaths ?? throw new ArgumentNullException(nameof(profilePaths));
    }

    public async Task<AddAccountResult> AddAsync(AddAccountRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.AccountId == Guid.Empty)
            return new(AddAccountStatus.InvalidProfilePath, null, "A valid local account ID is required.");

        var validation = await adapter.ValidateSessionAsync(request.Session, cancellationToken).ConfigureAwait(false);
        if (!validation.IsSuccess || validation.Value is null)
            return new(AddAccountStatus.SessionRejected, null, "The account session could not be validated.");

        var identity = validation.Value;
        if (!IsStableIdentity(identity))
            return new(AddAccountStatus.InvalidIdentity, null, "The service did not report a stable account identity.");

        string profilePath;
        try
        {
            profilePath = profilePaths.GetAccountProfilePath(request.AccountId);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new(AddAccountStatus.InvalidProfilePath, null, "The account profile path is not safe.");
        }

        await using var unit = await unitOfWorkFactory.BeginAsync(cancellationToken).ConfigureAwait(false);
        var commitAttempted = false;
        try
        {
            var existingId = await unit.Accounts.GetByIdAsync(request.AccountId, cancellationToken).ConfigureAwait(false);
            if (existingId is not null)
            {
                await unit.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new(AddAccountStatus.AccountIdCollision, null, "The local account ID is already in use.");
            }

            var duplicate = await unit.Accounts.GetByBaiduUserIdAsync(identity.UserId, cancellationToken).ConfigureAwait(false);
            if (duplicate is not null)
            {
                await unit.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new(AddAccountStatus.DuplicateIdentity, duplicate.Id, "This Baidu identity already exists in account history.");
            }

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
            await unit.Accounts.UpsertAsync(account, cancellationToken).ConfigureAwait(false);
            commitAttempted = true;
            await unit.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(AddAccountStatus.Added, request.AccountId, "The account was added.");
        }
        catch
        {
            if (commitAttempted)
            {
                try { await unit.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception) { /* Preserve the original transaction failure. */ }
            }
            else
            {
                try { await unit.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception) { /* Preserve the original operation failure. */ }
            }
            throw;
        }
    }

    private static bool IsStableIdentity(BaiduAccountIdentity identity) =>
        !string.IsNullOrWhiteSpace(identity.UserId) &&
        identity.UserId.Length <= MaximumStableUserIdLength &&
        !identity.UserId.Any(char.IsControl) &&
        !string.IsNullOrWhiteSpace(identity.DisplayName) &&
        identity.DisplayName.Length <= 256 &&
        !identity.DisplayName.Any(char.IsControl);
}
