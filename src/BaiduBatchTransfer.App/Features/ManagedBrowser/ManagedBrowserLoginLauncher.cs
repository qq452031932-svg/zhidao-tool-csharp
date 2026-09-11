using BaiduBatchTransfer.Application.Accounts;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Infrastructure.Security;
using System.IO;

namespace BaiduBatchTransfer.App.Features.ManagedBrowser;

public enum ManagedBrowserLoginStatus
{
    Bound, Pending, LoginRequired, BrowserUnavailable, TimedOut, Cancelled,
    AccountIdentityMismatch, AccountUnavailable, DuplicateIdentity, Conflict, ActiveWorkBlocked,
    BrowserFamilyChoiceRequired, SetupUnavailable
}

public enum ManagedBrowserLoginStage
{
    None, AccountRead, SetupReady, LifecyclePrepared, ManagerEntered,
    ManagerProfilePinned, ManagerExecutablePinned, ManagerProcessLaunched,
    ManagerRootVerified, ManagerPortRead, ManagerTargetBound, BrowserLaunched,
    HelperStarted, HelperHealthy, IdentityReceived, IdentityObserved,
    BrowserReconnected, AccountBound
}

public sealed record ManagedBrowserLoginResult(ManagedBrowserLoginStatus Status,
    ManagedBrowserLoginStage Stage = ManagedBrowserLoginStage.None,
    string? DiagnosticCode = null)
{
    public string SafeMessage => Status switch
    {
        ManagedBrowserLoginStatus.Bound => "账号身份已核验并绑定。",
        ManagedBrowserLoginStatus.Pending => "专用浏览器已打开，请等待登录页面就绪。",
        ManagedBrowserLoginStatus.LoginRequired => "请在专用浏览器中完成登录后重试。",
        ManagedBrowserLoginStatus.TimedOut => "身份核验等待超时，浏览器仍保留供您操作。",
        ManagedBrowserLoginStatus.Cancelled => "已取消等待，未更改账号绑定。",
        ManagedBrowserLoginStatus.AccountIdentityMismatch => "浏览器账号与所选账号不一致，未更改绑定。",
        ManagedBrowserLoginStatus.AccountUnavailable => "所选账号当前不可用。",
        ManagedBrowserLoginStatus.DuplicateIdentity => "该百度账号已存在。",
        ManagedBrowserLoginStatus.ActiveWorkBlocked => "账号仍有活动操作或专用浏览器，请稍后重试。",
        ManagedBrowserLoginStatus.BrowserFamilyChoiceRequired => "请选择 Chrome 或 Edge。",
        ManagedBrowserLoginStatus.SetupUnavailable => "浏览器目录尚未就绪，请重新选择。",
        ManagedBrowserLoginStatus.Conflict => "账号或浏览器状态已变化，请重试。",
        ManagedBrowserLoginStatus.BrowserUnavailable => $"专用浏览器暂不可用（诊断：{Stage}/{DiagnosticCode ?? "None"}），请检查后重试。",
        _ => "专用浏览器暂不可用，请检查后重试。"
    };
}

internal interface IManagedBrowserLoginHelper : IAsyncDisposable
{
    Task<ManagedBrowserResponse> SendAsync(ManagedBrowserRequest request, CancellationToken token);
}

public sealed class ManagedBrowserLoginLauncher : IManagedBrowserLoginOperations
{
    private readonly BrowserSetupService setup;
    private readonly IManagedBrowserProfilePolicy profiles;
    private readonly ManagedChromiumProcessManager manager;
    private readonly ManagedBrowserLifecycleCoordinator lifecycle;
    private readonly IManagedBrowserStateStore store;
    private readonly IUnitOfWorkFactory units;
    private readonly ManagedBrowserAccountService accounts;
    private readonly ManagedBrowserFamilySwitchCoordinator familySwitch;
    private readonly RetiredBrowserFamilySlotCoordinator retiredSlots;
    private readonly Func<CancellationToken, Task<IManagedBrowserLoginHelper>> startHelper;

    public ManagedBrowserLoginLauncher(BrowserSetupService setup, IManagedBrowserProfilePolicy profiles,
        ManagedChromiumProcessManager manager, ManagedBrowserLifecycleCoordinator lifecycle,
        IManagedBrowserStateStore store, IUnitOfWorkFactory units, ManagedBrowserAccountService accounts,
        ManagedBrowserFamilySwitchCoordinator familySwitch, RetiredBrowserFamilySlotCoordinator retiredSlots, string helperRoot)
        : this(setup, profiles, manager, lifecycle, store, units, accounts, familySwitch, retiredSlots,
            async token => await ManagedBrowserHelperClient.StartAsync(helperRoot, token).ConfigureAwait(false)) { }

    internal ManagedBrowserLoginLauncher(BrowserSetupService setup, IManagedBrowserProfilePolicy profiles,
        ManagedChromiumProcessManager manager, ManagedBrowserLifecycleCoordinator lifecycle,
        IManagedBrowserStateStore store, IUnitOfWorkFactory units, ManagedBrowserAccountService accounts,
        ManagedBrowserFamilySwitchCoordinator familySwitch, RetiredBrowserFamilySlotCoordinator retiredSlots,
        Func<CancellationToken, Task<IManagedBrowserLoginHelper>> startHelper)
    {
        this.setup = setup ?? throw new ArgumentNullException(nameof(setup));
        this.profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        this.manager = manager ?? throw new ArgumentNullException(nameof(manager));
        this.lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.units = units ?? throw new ArgumentNullException(nameof(units));
        this.accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        this.familySwitch = familySwitch ?? throw new ArgumentNullException(nameof(familySwitch));
        this.retiredSlots = retiredSlots ?? throw new ArgumentNullException(nameof(retiredSlots));
        this.startHelper = startHelper ?? throw new ArgumentNullException(nameof(startHelper));
    }

    public async Task<ManagedBrowserLoginResult> LaunchAsync(ManagedBrowserLoginRequest request,
        BrowserFamily? family = null, bool reselect = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (family is not (null or BrowserFamily.Chrome or BrowserFamily.Edge))
            throw new ArgumentOutOfRangeException(nameof(family));
        var progress = new LoginProgress();
        try
        {
            using var lease = await lifecycle.AcquireAccountLeaseAsync(request.AccountId, cancellationToken).ConfigureAwait(false);
            return await LaunchCoreAsync(request, family, reselect, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return Result(ManagedBrowserLoginStatus.Cancelled, progress.Stage); }
        catch (TimeoutException) { return Result(ManagedBrowserLoginStatus.TimedOut, progress.Stage); }
        catch (ManagedBrowserHelperClientException error)
        {
            return Result(error.Error switch
            {
                ManagedBrowserHelperClientError.Cancelled => ManagedBrowserLoginStatus.Cancelled,
                ManagedBrowserHelperClientError.TimedOut => ManagedBrowserLoginStatus.TimedOut,
                _ => ManagedBrowserLoginStatus.BrowserUnavailable
            }, progress.Stage, error.Error.ToString());
        }
        catch (ManagedBrowserHelperIntegrityException error)
        { return Result(ManagedBrowserLoginStatus.BrowserUnavailable, progress.Stage,
            error.Phase is null ? error.Error.ToString() : $"{error.Error}@{error.Phase}"); }
        catch (Exception error)
        {
            // Never surface arbitrary browser/helper/SQLite details (UIDs, paths, URLs or page data).
            var code = error is IOException ? $"IOException-{error.HResult & 0xffff}" : error.GetType().Name;
            return Result(ManagedBrowserLoginStatus.BrowserUnavailable, progress.Stage, code);
        }
    }

    private async Task<ManagedBrowserLoginResult> LaunchCoreAsync(ManagedBrowserLoginRequest request,
        BrowserFamily? family, bool reselect, LoginProgress progress, CancellationToken token)
    {
        var account = await ReadAccountAsync(request.AccountId, token).ConfigureAwait(false);
        progress.Stage = ManagedBrowserLoginStage.AccountRead;
        if (request.Kind == ManagedBrowserLoginRequestKind.AddNew ? account is not null : !IsActive(account))
            return Result(request.Kind == ManagedBrowserLoginRequestKind.AddNew ? ManagedBrowserLoginStatus.Conflict : ManagedBrowserLoginStatus.AccountUnavailable);
        var selected = reselect ? await setup.ReselectAsync(token).ConfigureAwait(false) : await setup.EnsureSelectedAsync(token).ConfigureAwait(false);
        if (selected.Status == BrowserSetupStatus.Cancelled) return Result(ManagedBrowserLoginStatus.Cancelled);
        if (selected.Status is not (BrowserSetupStatus.Selected or BrowserSetupStatus.BrowserFamilyChoiceRequired))
            return Result(ManagedBrowserLoginStatus.SetupUnavailable);
        if (family is null && selected.Candidates.Count != 1) return Result(ManagedBrowserLoginStatus.BrowserFamilyChoiceRequired);
        var candidate = family is null ? selected.Candidates.Single() : selected.Candidates.SingleOrDefault(x => x.BrowserFamily == family);
        if (candidate is null) return Result(ManagedBrowserLoginStatus.SetupUnavailable);
        progress.Stage = ManagedBrowserLoginStage.SetupReady;

        var installation = candidate.Identity;
        var known = await store.LoadInstallationByFileIdentityAsync(installation.FileIdentity, token).ConfigureAwait(false);
        if (known is not null)
        {
            if (!SameExecutable(known.Identity, installation)) return Result(ManagedBrowserLoginStatus.Conflict);
            installation = known.Identity;
        }
        var current = await lifecycle.LoadPersistedAsync(request.AccountId, token).ConfigureAwait(false);
        var switching = current is not null && current.BrowserFamily != installation.BrowserFamily;
        if (switching && !await familySwitch.CanSwitchAsync(request.AccountId, token).ConfigureAwait(false))
            return Result(ManagedBrowserLoginStatus.ActiveWorkBlocked);
        var pending = lifecycle.Pending(request.AccountId);
        if (switching && (pending is null || !SameExecutable(pending.Installation, installation)) &&
            !await retiredSlots.PrepareAsync(current!, installation.BrowserFamily, token).ConfigureAwait(false))
            return Result(ManagedBrowserLoginStatus.ActiveWorkBlocked);
        if (pending is not null && !SameExecutable(pending.Installation, installation))
        {
            if (await lifecycle.StopPendingAsync(request.AccountId, token).ConfigureAwait(false) == ManagedBrowserStopStatus.Blocked)
                return Result(ManagedBrowserLoginStatus.ActiveWorkBlocked);
            pending = null;
        }
        if (pending is not null) installation = pending.Installation;
        if (switching && pending is null &&
            await lifecycle.StopAsync(current!, token).ConfigureAwait(false) == ManagedBrowserStopStatus.Blocked)
            return Result(ManagedBrowserLoginStatus.ActiveWorkBlocked);
        if (!switching && current is not null && current.InstallationId != installation.InstallationId)
            return Result(ManagedBrowserLoginStatus.Conflict);

        ManagedBrowserProfileDescriptor profile;
        if (pending is not null) profile = pending.Profile;
        else
        {
            var prepared = current is null || switching
                ? await profiles.GetOrCreateAsync(request.AccountId, installation.BrowserFamily, token).ConfigureAwait(false)
                : await profiles.OpenExistingAsync(lifecycle.Describe(current.Session), token).ConfigureAwait(false);
            if (prepared.Status != ManagedBrowserProfileStatus.Ready || prepared.Profile is null)
                return Result(ManagedBrowserLoginStatus.BrowserUnavailable);
            profile = prepared.Profile;
            pending = new(installation, profile, !switching && current is not null ? current.SessionId : Guid.NewGuid());
            lifecycle.Track(pending, alreadyBound: current is not null && !switching); // Before launch: never lose pending ownership.
        }
        progress.Stage = ManagedBrowserLoginStage.LifecyclePrepared;
        var launched = await manager.LaunchAsync(installation, profile, token,
            stage => progress.Stage = stage switch
            {
                ManagedChromiumLaunchStage.Entered => ManagedBrowserLoginStage.ManagerEntered,
                ManagedChromiumLaunchStage.ProfilePinned => ManagedBrowserLoginStage.ManagerProfilePinned,
                ManagedChromiumLaunchStage.ExecutablePinned => ManagedBrowserLoginStage.ManagerExecutablePinned,
                ManagedChromiumLaunchStage.ProcessLaunched => ManagedBrowserLoginStage.ManagerProcessLaunched,
                ManagedChromiumLaunchStage.RootVerified => ManagedBrowserLoginStage.ManagerRootVerified,
                ManagedChromiumLaunchStage.PortRead => ManagedBrowserLoginStage.ManagerPortRead,
                _ => ManagedBrowserLoginStage.ManagerTargetBound,
            }).ConfigureAwait(false);
        if (launched.Status != ManagedChromiumLaunchStatus.Ready || launched.Session is null)
            return Result(ManagedBrowserLoginStatus.Pending);
        var handle = launched.Session;
        progress.Stage = ManagedBrowserLoginStage.BrowserLaunched;
        var identity = await ObserveAsync(handle, progress, token).ConfigureAwait(false);
        if (identity.Kind != ManagedBrowserIdentityResultKind.Observed)
            return Result(identity.Kind switch
            {
                ManagedBrowserIdentityResultKind.LoginRequired => ManagedBrowserLoginStatus.LoginRequired,
                ManagedBrowserIdentityResultKind.TimedOut => ManagedBrowserLoginStatus.TimedOut,
                ManagedBrowserIdentityResultKind.Cancelled => ManagedBrowserLoginStatus.Cancelled,
                _ => ManagedBrowserLoginStatus.BrowserUnavailable
            });
        progress.Stage = ManagedBrowserLoginStage.IdentityObserved;
        token.ThrowIfCancellationRequested();
        if (request.Kind == ManagedBrowserLoginRequestKind.ReloginExisting)
        {
            account = await ReadAccountAsync(request.AccountId, token).ConfigureAwait(false);
            if (!IsActive(account)) return Result(ManagedBrowserLoginStatus.AccountUnavailable);
            if (account!.BaiduUserId != identity.StableBaiduUserId) return Result(ManagedBrowserLoginStatus.AccountIdentityMismatch);
        }
        // Re-prove the still-owned browser after observation before binding any durable identity.
        if ((await manager.ReconnectAsync(handle, token).ConfigureAwait(false)).Session is null)
            return Result(ManagedBrowserLoginStatus.Pending);
        progress.Stage = ManagedBrowserLoginStage.BrowserReconnected;
        var session = !switching && current is not null ? current.Session : new ManagedBrowserSessionReference(pending.SessionId,
            installation.InstallationId, request.AccountId, identity.StableBaiduUserId!, installation.BrowserFamily,
            profile.ProfileId.ToString("N"), profile.OwnershipId.ToString("N"));
        if (switching)
        {
            var switched = await familySwitch.SwitchAsync(new(current!, installation, session, identity.StableBaiduUserId!), token).ConfigureAwait(false);
            if (switched.Status == ManagedBrowserFamilySwitchStatus.Switched) lifecycle.Bound(profile);
            return Result(switched.Status switch
            {
                ManagedBrowserFamilySwitchStatus.Switched => ManagedBrowserLoginStatus.Bound,
                ManagedBrowserFamilySwitchStatus.IdentityMismatch => ManagedBrowserLoginStatus.AccountIdentityMismatch,
                ManagedBrowserFamilySwitchStatus.ActiveWorkBlocked => ManagedBrowserLoginStatus.ActiveWorkBlocked,
                _ => ManagedBrowserLoginStatus.Conflict
            });
        }
        var expected = current is null ? null : new StoredManagedBrowserSession(current.Session, ManagedBrowserSessionLifecycleState.Active, current.SessionRevision);
        var bound = await accounts.BindAsync(request, installation, session, expected, token).ConfigureAwait(false);
        progress.Stage = ManagedBrowserLoginStage.AccountBound;
        if (bound == ManagedBrowserBindingStatus.Bound) lifecycle.Bound(profile);
        return Result(bound switch
        {
            ManagedBrowserBindingStatus.Bound => ManagedBrowserLoginStatus.Bound,
            ManagedBrowserBindingStatus.AccountIdentityMismatch => ManagedBrowserLoginStatus.AccountIdentityMismatch,
            ManagedBrowserBindingStatus.AccountUnavailable => ManagedBrowserLoginStatus.AccountUnavailable,
            ManagedBrowserBindingStatus.DuplicateIdentity => ManagedBrowserLoginStatus.DuplicateIdentity,
            ManagedBrowserBindingStatus.ActiveWorkBlocked => ManagedBrowserLoginStatus.ActiveWorkBlocked,
            _ => ManagedBrowserLoginStatus.Conflict
        });
    }

    private async Task<ManagedBrowserIdentityResult> ObserveAsync(ManagedChromiumSessionHandle handle,
        LoginProgress progress, CancellationToken token)
    {
        // Complete helper cleanup before the durable commit. A cleanup failure cannot disguise a saved binding.
        await using var helper = await startHelper(token).ConfigureAwait(false);
        progress.Stage = ManagedBrowserLoginStage.HelperStarted;
        var operationId = Guid.NewGuid();
        ManagedBrowserRequest BoundRequest(ManagedBrowserAction action, ManagedBrowserRequestPayload payload) =>
            new(Guid.NewGuid(), operationId, handle.Profile.AccountId, 1, action, payload);
        var health = BoundRequest(ManagedBrowserAction.Health, ManagedBrowserRequestPayload.Empty);
        ManagedBrowserProtocol.ValidateResponseFor(health, await helper.SendAsync(health, token).ConfigureAwait(false));
        progress.Stage = ManagedBrowserLoginStage.HelperHealthy;
        var observe = BoundRequest(ManagedBrowserAction.ObserveAccountIdentity,
            new ManagedBrowserObserveAccountIdentityRequest(handle.BrowserId, handle.TargetId, handle.Endpoint, 5000));
        var response = await helper.SendAsync(observe, token).ConfigureAwait(false);
        ManagedBrowserProtocol.ValidateResponseFor(observe, response);
        progress.Stage = ManagedBrowserLoginStage.IdentityReceived;
        return (ManagedBrowserIdentityResult)response.Result;
    }

    private async Task<AccountRecord?> ReadAccountAsync(Guid accountId, CancellationToken token)
    {
        await using var unit = await units.BeginAsync(token).ConfigureAwait(false);
        return await unit.Accounts.GetByIdAsync(accountId, token).ConfigureAwait(false);
    }
    private static bool IsActive(AccountRecord? account) => account is { IsDeleted: false, IsEnabled: true, Status: "Active" };
    private static bool SameExecutable(BrowserInstallationIdentity x, BrowserInstallationIdentity y) =>
        x.BrowserFamily == y.BrowserFamily && x.FileIdentity == y.FileIdentity && x.Publisher == y.Publisher && x.ProductName == y.ProductName &&
        string.Equals(x.ExecutablePath, y.ExecutablePath, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(x.InstallationDirectory, y.InstallationDirectory, StringComparison.OrdinalIgnoreCase);
    private sealed class LoginProgress
    {
        public ManagedBrowserLoginStage Stage { get; set; }
    }
    private static ManagedBrowserLoginResult Result(ManagedBrowserLoginStatus status,
        ManagedBrowserLoginStage stage = ManagedBrowserLoginStage.None,
        string? diagnosticCode = null) => new(status, stage, diagnosticCode);
}
