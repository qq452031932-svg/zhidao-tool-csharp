using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Infrastructure.Security;

public sealed class EncryptedBaiduSessionStore(
    IUnitOfWorkFactory unitOfWorkFactory,
    ISessionProtector sessionProtector) : IBaiduSessionStore
{
    private readonly IUnitOfWorkFactory unitOfWorkFactory =
        unitOfWorkFactory ?? throw new ArgumentNullException(nameof(unitOfWorkFactory));
    private readonly ISessionProtector sessionProtector =
        sessionProtector ?? throw new ArgumentNullException(nameof(sessionProtector));

    public async Task SaveAsync(AccountSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.LocalAccountId is not { } localAccountId || string.IsNullOrWhiteSpace(session.BaiduUserId))
            throw new ArgumentException("The session must be bound before it can be persisted.", nameof(session));

        await using var unit = await unitOfWorkFactory.BeginAsync(cancellationToken).ConfigureAwait(false);
        var account = await unit.Accounts.GetByIdAsync(localAccountId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The account does not exist.");
        if (!string.Equals(account.BaiduUserId, session.BaiduUserId, StringComparison.Ordinal) ||
            account.IsDeleted || !account.IsEnabled || !string.Equals(account.Status, "Active", StringComparison.Ordinal))
            throw new InvalidOperationException("The session binding does not match an active account.");

        var protectedResult = sessionProtector.Protect(session);
        var encryptedBytes = protectedResult.ProtectedBytes;
        if (!protectedResult.IsSuccess || encryptedBytes is null)
            throw new InvalidOperationException("The session could not be protected safely.");

        await unit.Accounts.SetEncryptedSessionAsync(localAccountId, encryptedBytes, cancellationToken).ConfigureAwait(false);
        await unit.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AccountSession?> LoadAsync(string accountId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(accountId, out var localAccountId)) return null;
        await using var unit = await unitOfWorkFactory.BeginAsync(cancellationToken).ConfigureAwait(false);
        var account = await unit.Accounts.GetByIdAsync(localAccountId, cancellationToken).ConfigureAwait(false);
        await unit.RollbackAsync(cancellationToken).ConfigureAwait(false);
        if (account?.EncryptedSession is not { Length: > 0 } encryptedBytes || account.IsDeleted) return null;

        var result = sessionProtector.Unprotect(encryptedBytes, localAccountId, account.BaiduUserId);
        return result.IsSuccess ? result.Session : null;
    }

    public async Task RemoveAsync(string accountId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(accountId, out var localAccountId)) return;
        await using var unit = await unitOfWorkFactory.BeginAsync(cancellationToken).ConfigureAwait(false);
        await unit.Accounts.SetEncryptedSessionAsync(localAccountId, null, cancellationToken).ConfigureAwait(false);
        await unit.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
