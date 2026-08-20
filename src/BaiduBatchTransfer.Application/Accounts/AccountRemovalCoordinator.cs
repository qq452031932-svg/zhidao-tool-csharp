using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.Application.Accounts;

public sealed class AccountRemovalCoordinator
{
    private const string FailedMessage = "账号退出准备失败。";
    private const string CleanupPendingMessage =
        "账号已禁用且凭据已清除；本机登录目录清理或墓碑终结仍待完成。";
    private const string RemovedMessage = "账号已退出。";

    private readonly IAccountRemovalStore store;
    private readonly RemoveAccountService removalService;

    public AccountRemovalCoordinator(
        IAccountRemovalStore store,
        IWebViewProfileStore profileStore,
        IAccountProfilePathPolicy profilePaths) :
        this(store, new RemoveAccountService(profileStore, profilePaths))
    {
    }

    public AccountRemovalCoordinator(
        IAccountRemovalStore store,
        RemoveAccountService removalService)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.removalService = removalService ?? throw new ArgumentNullException(nameof(removalService));
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

    /// <summary>
    /// Applies a confirmed removal once. A request replayed after preparation remains stale and is not a cleanup retry.
    /// </summary>
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

        var cleanup = await removalService.FinalizePreparedAsync(
            request.AccountId,
            request.RemovedAt,
            store,
            cancellationToken).ConfigureAwait(false);
        return CleanupResult(cleanup);
    }

    /// <summary>
    /// Resumes only the leased cleanup/tombstone phase for an already disabled, credential-free account.
    /// </summary>
    /// <remarks>This path does not validate a confirmation token or repeat history deletion.</remarks>
    public async Task<AccountExitPreview> ResumeCleanupAsync(
        Guid accountId,
        DateTimeOffset removedAt,
        CancellationToken cancellationToken)
    {
        if (accountId == Guid.Empty)
            return Result(AccountExitStatus.Failed, FailedMessage);

        var cleanup = await removalService.FinalizePreparedAsync(
            accountId,
            removedAt,
            store,
            cancellationToken).ConfigureAwait(false);
        return CleanupResult(cleanup);
    }

    private static AccountExitPreview CleanupResult(RemoveAccountResult cleanup) =>
        cleanup.Status == RemoveAccountStatus.Removed
            ? Result(AccountExitStatus.Removed, RemovedMessage)
            : Result(AccountExitStatus.CleanupPending, CleanupPendingMessage);

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
