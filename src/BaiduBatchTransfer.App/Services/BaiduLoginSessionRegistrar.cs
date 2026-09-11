using BaiduBatchTransfer.Application.Accounts;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.App.Services;

public sealed record BaiduLoginSessionRegistrationResult(
    bool Succeeded,
    AddAccountResult? AddResult,
    string? FailureMessage);

public sealed class BaiduLoginSessionRegistrar
{
    private readonly Func<AddAccountRequest, CancellationToken, Task<AddAccountResult>> addAccount;
    private readonly Func<Guid, AccountSession, CancellationToken, Task<RefreshAccountSessionResult>> refreshAccount;

    public BaiduLoginSessionRegistrar(
        AddAccountService addAccountService,
        RefreshAccountSessionService refreshAccountService)
        : this(addAccountService.AddAsync, refreshAccountService.RefreshAsync)
    {
    }

    internal BaiduLoginSessionRegistrar(
        Func<AddAccountRequest, CancellationToken, Task<AddAccountResult>> addAccount,
        Func<Guid, AccountSession, CancellationToken, Task<RefreshAccountSessionResult>> refreshAccount)
    {
        this.addAccount = addAccount ?? throw new ArgumentNullException(nameof(addAccount));
        this.refreshAccount = refreshAccount ?? throw new ArgumentNullException(nameof(refreshAccount));
    }

    public async Task<BaiduLoginSessionRegistrationResult> RegisterAsync(
        BaiduLoginWindowPurpose purpose,
        Guid accountId,
        AccountProfileOwnership ownership,
        AccountSession capturedSession,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken)
    {
        if (purpose == BaiduLoginWindowPurpose.AddAccount)
        {
            var result = await addAccount(
                new AddAccountRequest(
                    accountId,
                    capturedSession,
                    capturedAt,
                    ownership,
                    PersistBrowserProfile: false),
                cancellationToken).ConfigureAwait(false);
            return new(result.Status == AddAccountStatus.Added, result,
                result.Status == AddAccountStatus.Added ? null : result.SafeMessage);
        }

        var refresh = await refreshAccount(accountId, capturedSession, cancellationToken).ConfigureAwait(false);
        return refresh.Status switch
        {
            RefreshAccountSessionStatus.Refreshed => new(true, null, null),
            RefreshAccountSessionStatus.IdentityMismatch => new(false, null, "登录账号与所选账号不一致。"),
            RefreshAccountSessionStatus.AccountUnavailable => new(false, null, "所选账号当前不可用。"),
            _ => new(false, null, "账号会话未能保存，请重新登录。"),
        };
    }
}
