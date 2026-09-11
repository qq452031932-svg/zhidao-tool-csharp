using BaiduBatchTransfer.App.Features.ManagedBrowser;
using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.App.Services;

public sealed class DirectBackgroundAccountLoginLauncher(
    IBaiduLoginWindowPresenter presenter,
    IBaiduSessionStore sessions) : IAccountLoginLauncher, IAccountReloginLauncher
{
    private readonly object attemptGate = new();
    private readonly HashSet<Guid> activeAttempts = [];
    private readonly IBaiduLoginWindowPresenter presenter =
        presenter ?? throw new ArgumentNullException(nameof(presenter));
    private readonly IBaiduSessionStore sessions =
        sessions ?? throw new ArgumentNullException(nameof(sessions));

    public bool IsAvailable => true;
    public string StatusMessage => "使用应用内登录保存批量转存会话。";

    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        if (!TryBeginAttempt(Guid.Empty)) return;
        try
        {
            await presenter.ShowAsync(
                new BaiduLoginWindowRequest(Guid.NewGuid(), Guid.NewGuid(), BaiduLoginWindowPurpose.AddAccount),
                cancellationToken).ConfigureAwait(true);
        }
        finally { CompleteAttempt(Guid.Empty); }
    }

    public async Task<ManagedBrowserLoginResult> ReloginAsync(
        Guid accountId,
        CancellationToken cancellationToken = default)
    {
        if (accountId == Guid.Empty)
            throw new ArgumentException("An account is required.", nameof(accountId));
        if (!TryBeginAttempt(accountId))
            return new(ManagedBrowserLoginStatus.Cancelled);

        try
        {
            var key = accountId.ToString("D");
            var before = await sessions.LoadAsync(key, cancellationToken).ConfigureAwait(false);
            await presenter.ShowAsync(
                new BaiduLoginWindowRequest(accountId, Guid.NewGuid(), BaiduLoginWindowPurpose.RefreshAccountSession),
                cancellationToken).ConfigureAwait(true);
            var after = await sessions.LoadAsync(key, cancellationToken).ConfigureAwait(false);

            if (after?.LocalAccountId != accountId || string.IsNullOrWhiteSpace(after.BaiduUserId))
                return new(ManagedBrowserLoginStatus.AccountIdentityMismatch);
            if (before is not null && after.CapturedAtUtc <= before.CapturedAtUtc)
                return new(ManagedBrowserLoginStatus.Cancelled);
            return new(ManagedBrowserLoginStatus.Bound);
        }
        finally { CompleteAttempt(accountId); }
    }

    private bool TryBeginAttempt(Guid key)
    {
        lock (attemptGate) return activeAttempts.Add(key);
    }

    private void CompleteAttempt(Guid key)
    {
        lock (attemptGate) activeAttempts.Remove(key);
    }
}
