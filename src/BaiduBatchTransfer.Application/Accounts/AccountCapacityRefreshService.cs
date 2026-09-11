using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Accounts;

public enum AccountCapacityRefreshDisposition
{
    Updated,
    SessionUnavailable,
    LoginRequired,
    VerificationRequired,
    TemporarilyUnavailable,
    Unavailable,
    AccountChanged,
}

public sealed record AccountCapacityRefreshResult(
    Guid AccountId,
    AccountCapacityRefreshDisposition Disposition,
    AccountCapacity? Capacity,
    string SafeMessage,
    Uri? AvatarUri = null)
{
    public override string ToString() =>
        $"AccountCapacityRefreshResult {{ Disposition = {Disposition}, Capacity = [REDACTED], AvatarUri = [REDACTED], Identity = [REDACTED] }}";
}

public sealed class AccountCapacityRefreshService(
    IBaiduSessionStore sessionStore,
    IBaiduNetdiskAdapter adapter,
    IAccountCapacityStore capacityStore,
    TimeProvider timeProvider)
{
    private readonly IBaiduSessionStore sessionStore =
        sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
    private readonly IBaiduNetdiskAdapter adapter =
        adapter ?? throw new ArgumentNullException(nameof(adapter));
    private readonly IAccountCapacityStore capacityStore =
        capacityStore ?? throw new ArgumentNullException(nameof(capacityStore));
    private readonly TimeProvider timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<AccountCapacityRefreshResult> RefreshAsync(
        Guid accountId,
        CancellationToken cancellationToken = default)
    {
        if (accountId == Guid.Empty)
            throw new ArgumentException("An account id is required.", nameof(accountId));
        try
        {
            var session = await sessionStore
                .LoadAsync(accountId.ToString("D"), cancellationToken)
                .ConfigureAwait(false);
            if (session?.LocalAccountId != accountId || string.IsNullOrWhiteSpace(session.BaiduUserId))
                return Result(accountId, AccountCapacityRefreshDisposition.SessionUnavailable);

            var observed = await adapter
                .GetCapacityAsync(session, cancellationToken)
                .ConfigureAwait(false);
            if (!observed.IsSuccess)
                return Result(accountId, Map(observed.ErrorKind));

            var capacity = observed.Value!;
            Uri? avatarUri = null;
            try
            {
                var identity = await adapter
                    .ValidateSessionAsync(session, cancellationToken)
                    .ConfigureAwait(false);
                if (identity.IsSuccess)
                {
                    if (!string.Equals(identity.Value!.UserId, session.BaiduUserId, StringComparison.Ordinal))
                        return Result(accountId, AccountCapacityRefreshDisposition.AccountChanged);
                    avatarUri = identity.Value.AvatarUri;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch
            {
                // Avatar is presentation-only. A profile read failure must not discard a valid quota refresh.
            }
            var persisted = await capacityStore.TryUpdateAsync(
                accountId,
                session.BaiduUserId,
                capacity,
                timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
            return persisted
                ? new(accountId, AccountCapacityRefreshDisposition.Updated, capacity, "账号容量已更新。", avatarUri)
                : Result(accountId, AccountCapacityRefreshDisposition.AccountChanged);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return Result(accountId, AccountCapacityRefreshDisposition.TemporarilyUnavailable);
        }
    }

    private static AccountCapacityRefreshDisposition Map(BaiduErrorKind? errorKind) => errorKind switch
    {
        BaiduErrorKind.ExpiredLogin => AccountCapacityRefreshDisposition.LoginRequired,
        BaiduErrorKind.VerificationRequired => AccountCapacityRefreshDisposition.VerificationRequired,
        BaiduErrorKind.TransientService => AccountCapacityRefreshDisposition.TemporarilyUnavailable,
        _ => AccountCapacityRefreshDisposition.Unavailable,
    };

    private static AccountCapacityRefreshResult Result(
        Guid accountId,
        AccountCapacityRefreshDisposition disposition) =>
        new(accountId, disposition, null, disposition switch
        {
            AccountCapacityRefreshDisposition.SessionUnavailable or
            AccountCapacityRefreshDisposition.LoginRequired => "账号登录状态不可用，请重新登录。",
            AccountCapacityRefreshDisposition.VerificationRequired => "请先完成百度安全验证。",
            AccountCapacityRefreshDisposition.TemporarilyUnavailable => "容量获取失败，请稍后重试。",
            AccountCapacityRefreshDisposition.AccountChanged => "账号状态已变化，未更新容量。",
            _ => "暂时无法读取账号容量。",
        });
}
