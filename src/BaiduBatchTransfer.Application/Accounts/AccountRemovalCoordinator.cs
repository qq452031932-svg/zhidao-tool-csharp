using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.Application.Accounts;

public sealed class AccountRemovalCoordinator
{
    private const string FailedMessage = "账号退出准备失败。";
    private const string CleanupPendingMessage =
        "账号已禁用且凭据已清除；本机登录目录清理或墓碑终结仍待完成。";
    private const string RemovedMessage = "账号已退出。";

    private readonly IAccountRemovalStore store;
    private readonly IWebViewProfileStore profileStore;
    private readonly IAccountProfilePathPolicy profilePaths;

    public AccountRemovalCoordinator(
        IAccountRemovalStore store,
        IWebViewProfileStore profileStore,
        IAccountProfilePathPolicy profilePaths)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
        this.profilePaths = profilePaths ?? throw new ArgumentNullException(nameof(profilePaths));
    }

    public Task<AccountPage> GetAccountsPageAsync(
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken) =>
        store.GetAccountsPageAsync(pageNumber, pageSize, cancellationToken);

    public Task<AccountExitPreview> PreviewAsync(
        Guid accountId,
        AccountExitHistoryMode mode,
        CancellationToken cancellationToken) =>
        store.PreviewAsync(accountId, mode, cancellationToken);

    public async Task<AccountExitPreview> ApplyAsync(
        AccountExitRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        AccountRemovalPreparation preparation;
        try
        {
            preparation = await store.PrepareRemovalAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Result(AccountExitStatus.Failed, FailedMessage);
        }

        if (preparation.Status != AccountRemovalPreparationStatus.Prepared)
            return Result(Map(preparation.Status), preparation.SafeMessage);

        if (!preparation.DatabasePrepared ||
            request.AccountId == Guid.Empty ||
            preparation.AccountId != request.AccountId)
        {
            return Result(AccountExitStatus.Failed, FailedMessage);
        }

        try
        {
            await profileStore.CloseAsync(request.AccountId, cancellationToken).ConfigureAwait(false);
            if (preparation.ProfilePath is not null)
            {
                profilePaths.EnsureSafeAccountPath(request.AccountId, preparation.ProfilePath);
                await profileStore.DeleteAsync(
                    request.AccountId,
                    preparation.ProfilePath,
                    cancellationToken).ConfigureAwait(false);
            }

            await store.FinalizeTombstoneAsync(
                request.AccountId,
                request.RemovedAt,
                cancellationToken).ConfigureAwait(false);
            return Result(AccountExitStatus.Removed, RemovedMessage);
        }
        catch (Exception)
        {
            return Result(AccountExitStatus.CleanupPending, CleanupPendingMessage);
        }
    }

    private static AccountExitStatus Map(AccountRemovalPreparationStatus status) => status switch
    {
        AccountRemovalPreparationStatus.AlreadyRemoved => AccountExitStatus.AlreadyRemoved,
        AccountRemovalPreparationStatus.NotFound => AccountExitStatus.NotFound,
        AccountRemovalPreparationStatus.ActiveWorkBlocked => AccountExitStatus.ActiveWorkBlocked,
        AccountRemovalPreparationStatus.StaleConfirmation => AccountExitStatus.StaleConfirmation,
        AccountRemovalPreparationStatus.Failed => AccountExitStatus.Failed,
        _ => AccountExitStatus.Failed
    };

    private static AccountExitPreview Result(AccountExitStatus status, string safeMessage) =>
        new(status, string.Empty, 0, 0, 0, string.Empty, safeMessage);
}
