using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Accounts;

public enum RefreshAccountSessionStatus
{
    Refreshed,
    AccountUnavailable,
    SessionRejected,
    IdentityMismatch,
}

public sealed record RefreshAccountSessionResult(RefreshAccountSessionStatus Status);

public sealed class RefreshAccountSessionService(
    IBaiduNetdiskAdapter adapter,
    IBaiduSessionStore sessions)
{
    private readonly IBaiduNetdiskAdapter adapter =
        adapter ?? throw new ArgumentNullException(nameof(adapter));
    private readonly IBaiduSessionStore sessions =
        sessions ?? throw new ArgumentNullException(nameof(sessions));

    public async Task<RefreshAccountSessionResult> RefreshAsync(
        Guid accountId,
        AccountSession capturedSession,
        CancellationToken cancellationToken)
    {
        if (accountId == Guid.Empty)
            throw new ArgumentException("An account is required.", nameof(accountId));
        ArgumentNullException.ThrowIfNull(capturedSession);

        var key = accountId.ToString("D");
        var existing = await sessions.LoadAsync(key, cancellationToken).ConfigureAwait(false);
        if (existing?.LocalAccountId != accountId || string.IsNullOrWhiteSpace(existing.BaiduUserId))
            return new(RefreshAccountSessionStatus.AccountUnavailable);

        var validation = await adapter.ValidateSessionAsync(capturedSession, cancellationToken).ConfigureAwait(false);
        if (!validation.IsSuccess || validation.Value is null)
            return new(RefreshAccountSessionStatus.SessionRejected);
        if (!string.Equals(validation.Value.UserId, existing.BaiduUserId, StringComparison.Ordinal))
            return new(RefreshAccountSessionStatus.IdentityMismatch);

        await sessions.SaveAsync(
            capturedSession.Bind(accountId, existing.BaiduUserId), cancellationToken).ConfigureAwait(false);
        return new(RefreshAccountSessionStatus.Refreshed);
    }
}
