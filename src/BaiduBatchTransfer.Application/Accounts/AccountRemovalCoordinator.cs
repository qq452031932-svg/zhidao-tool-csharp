using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Accounts;

public sealed class AccountRemovalCoordinator
{
    private const string FailedMessage = "账号退出准备失败。";
    private const string CleanupPendingMessage =
        "账号已禁用且凭据已清除；本机登录目录清理或墓碑终结仍待完成。";
    private const string RemovedMessage = "账号已退出。";

    private readonly IAccountRemovalStore store;
    private readonly RemoveAccountService removalService;
    private readonly IManagedBrowserLifecycle? managedLifecycle;
    private readonly IManagedBrowserProfileStore? managedProfiles;

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
        : this(store, removalService, null, null)
    {
    }

    public AccountRemovalCoordinator(
        IAccountRemovalStore store,
        RemoveAccountService removalService,
        IManagedBrowserLifecycle? managedLifecycle,
        IManagedBrowserProfileStore? managedProfiles)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.removalService = removalService ?? throw new ArgumentNullException(nameof(removalService));
        if ((managedLifecycle is null) != (managedProfiles is null))
            throw new ArgumentException("Managed browser removal dependencies must be supplied together.");
        this.managedLifecycle = managedLifecycle;
        this.managedProfiles = managedProfiles;
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
            var accountLifecycle = managedLifecycle as IManagedBrowserAccountLifecycle;
            using var accountLease = accountLifecycle is null ? null :
                await accountLifecycle.AcquireAccountLeaseAsync(request.AccountId, cancellationToken).ConfigureAwait(false);
            if (accountLifecycle is not null &&
                await accountLifecycle.PreparePendingRemovalAsync(request.AccountId, cancellationToken).ConfigureAwait(false) == ManagedBrowserStopStatus.Blocked)
                return Result(AccountExitStatus.ActiveWorkBlocked,
                    "专用浏览器或遗留登录目录尚未安全处理，暂不能退出账号。");
            ManagedBrowserProfileCleanupPreparation? managedCleanup = null;
            if (managedLifecycle is not null)
            {
                var active = await managedLifecycle
                    .LoadActiveSessionAsync(request.AccountId, cancellationToken)
                    .ConfigureAwait(false);
                if (active is not null)
                {
                    var stopped = await managedLifecycle.StopAsync(active, cancellationToken).ConfigureAwait(false);
                    if (stopped == ManagedBrowserStopStatus.Blocked)
                        return Result(AccountExitStatus.ActiveWorkBlocked,
                            "账号的专用浏览器仍在运行，请关闭后重试。");
                    managedCleanup = managedProfiles!.PrepareRemoval(active);
                }
            }
            preparation = await store
                .PrepareRemovalAsync(request, managedCleanup, cancellationToken)
                .ConfigureAwait(false);
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

        if (preparation.ManagedProfileCleanup is { } preparedCleanup)
        {
            _ = await removalService.RecoverManagedProfileAsync(
                preparedCleanup.ManifestId,
                cancellationToken).ConfigureAwait(false);
            return Result(AccountExitStatus.CleanupPending, CleanupPendingMessage);
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

        var managed = await removalService.PrepareManagedProfileDeletionAsync(
            accountId, cancellationToken).ConfigureAwait(false);
        if (managed.Status is not (ManagedBrowserProfileCleanupStatus.NotFound or ManagedBrowserProfileCleanupStatus.Deleted))
            return Result(AccountExitStatus.CleanupPending, CleanupPendingMessage);

        var cleanup = await removalService.FinalizePreparedAsync(
            accountId,
            removedAt,
            store,
            cancellationToken).ConfigureAwait(false);
        return CleanupResult(cleanup);
    }

    public Task<ManagedBrowserProfileDeletionPreparation> PrepareManagedProfileDeletionAsync(
        Guid accountId,
        CancellationToken cancellationToken) =>
        removalService.PrepareManagedProfileDeletionAsync(accountId, cancellationToken);

    public async Task<AccountExitPreview> ConfirmManagedProfileDeletionAsync(
        Guid accountId,
        DateTimeOffset removedAt,
        ManagedBrowserProfileDeletionConfirmation confirmation,
        CancellationToken cancellationToken)
    {
        if (accountId == Guid.Empty)
            return Result(AccountExitStatus.Failed, FailedMessage);
        var deleted = await removalService.DeleteConfirmedManagedProfileAsync(
            accountId, confirmation, cancellationToken).ConfigureAwait(false);
        if (deleted.Status != ManagedBrowserProfileCleanupStatus.Deleted)
            return Result(AccountExitStatus.CleanupPending, CleanupPendingMessage);
        var finalized = await removalService.FinalizePreparedAsync(
            accountId, removedAt, store, cancellationToken).ConfigureAwait(false);
        return CleanupResult(finalized);
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

/// <summary>
/// Task-6 state-only family switch. Task 11 supplies the real identity observation and process fence.
/// </summary>
public sealed class ManagedBrowserFamilySwitchCoordinator(
    IManagedBrowserStateStore stateStore,
    IManagedBrowserFamilySwitchFence fence)
{
    private readonly IManagedBrowserStateStore stateStore =
        stateStore ?? throw new ArgumentNullException(nameof(stateStore));
    private readonly IManagedBrowserFamilySwitchFence fence =
        fence ?? throw new ArgumentNullException(nameof(fence));

    public Task<bool> CanSwitchAsync(Guid accountId, CancellationToken cancellationToken) =>
        fence.CanSwitchAsync(accountId, cancellationToken);

    public async Task<ManagedBrowserFamilySwitchResult> SwitchAsync(
        ManagedBrowserFamilySwitchObservation observation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var current = observation.ExpectedCurrent;
        var targetInstallation = observation.TargetInstallation;
        var targetSession = observation.TargetSession;
        if (targetInstallation.BrowserFamily == current.BrowserFamily ||
            targetSession.BrowserFamily != targetInstallation.BrowserFamily ||
            targetSession.AccountId != current.AccountId ||
            targetSession.InstallationId != targetInstallation.InstallationId ||
            targetSession.SessionId == current.SessionId ||
            string.Equals(targetSession.ProfileId, current.ProfileId, StringComparison.Ordinal) ||
            string.Equals(targetSession.ProfileOwnershipId, current.ProfileOwnershipId, StringComparison.Ordinal) ||
            !string.Equals(observation.ObservedStableBaiduUserId,
                current.Session.StableBaiduUserId, StringComparison.Ordinal) ||
            !string.Equals(targetSession.StableBaiduUserId,
                observation.ObservedStableBaiduUserId, StringComparison.Ordinal))
            return new(ManagedBrowserFamilySwitchStatus.IdentityMismatch);

        if (!await fence.CanSwitchAsync(current.AccountId, cancellationToken).ConfigureAwait(false))
            return new(ManagedBrowserFamilySwitchStatus.ActiveWorkBlocked);

        var storedInstallation = await stateStore.LoadInstallationAsync(
            current.InstallationId, cancellationToken).ConfigureAwait(false);
        var storedSession = await stateStore.LoadSessionAsync(
            current.SessionId, cancellationToken).ConfigureAwait(false);
        if (storedInstallation is null || storedSession is null ||
            storedInstallation.Revision != current.InstallationRevision ||
            storedInstallation.State != BrowserInstallationLifecycleState.Active ||
            storedInstallation.Identity.InstallationId != current.InstallationId ||
            storedInstallation.Identity.BrowserFamily != current.BrowserFamily ||
            storedSession.Revision != current.SessionRevision ||
            storedSession.State != ManagedBrowserSessionLifecycleState.Active ||
            storedSession.Identity.SessionId != current.SessionId ||
            storedSession.Identity.InstallationId != current.InstallationId ||
            storedSession.Identity.AccountId != current.AccountId ||
            storedSession.Identity.BrowserFamily != current.BrowserFamily ||
            !string.Equals(storedSession.Identity.StableBaiduUserId,
                current.Session.StableBaiduUserId, StringComparison.Ordinal) ||
            !string.Equals(storedSession.Identity.ProfileId,
                current.ProfileId, StringComparison.Ordinal) ||
            !string.Equals(storedSession.Identity.ProfileOwnershipId,
                current.ProfileOwnershipId, StringComparison.Ordinal))
            return new(ManagedBrowserFamilySwitchStatus.Conflict);

        var reusableTarget = await stateStore.LoadInstallationByFileIdentityAsync(
            targetInstallation.FileIdentity, cancellationToken).ConfigureAwait(false);

        ManagedBrowserFamilySwitchTransitionResult transition;
        try
        {
            transition = await stateStore.TrySwitchFamilyAsync(
                new ManagedBrowserFamilySwitchTransitionRequest(
                    storedInstallation,
                    storedSession,
                    targetInstallation,
                    targetSession,
                    observation.ObservedStableBaiduUserId,
                    reusableTarget),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new(ManagedBrowserFamilySwitchStatus.Conflict);
        }

        return new(transition.Status switch
        {
            ManagedBrowserFamilySwitchTransitionStatus.Applied => ManagedBrowserFamilySwitchStatus.Switched,
            ManagedBrowserFamilySwitchTransitionStatus.ActiveWorkBlocked =>
                ManagedBrowserFamilySwitchStatus.ActiveWorkBlocked,
            _ => ManagedBrowserFamilySwitchStatus.Conflict,
        });
    }
}

public sealed class ManagedBrowserFamilySwitchReadinessFence(IManagedBrowserStateStore store) : IManagedBrowserFamilySwitchFence
{
    public Task<bool> CanSwitchAsync(Guid accountId, CancellationToken cancellationToken) =>
        store.CanSwitchFamilyAsync(accountId, cancellationToken);
}
