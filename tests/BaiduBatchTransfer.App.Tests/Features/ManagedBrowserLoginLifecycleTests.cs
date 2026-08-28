using BaiduBatchTransfer.App.Features.ManagedBrowser;
using BaiduBatchTransfer.Application.Accounts;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Infrastructure.Database;
using BaiduBatchTransfer.Infrastructure.Security;
using BaiduBatchTransfer.App.Services;
using static BaiduBatchTransfer.App.Tests.Features.ManagedChromiumProcessManagerTests;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class ManagedBrowserLoginLifecycleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FirstUseAndSavedSetupBindReservedIdWithoutCookie(bool saved)
    {
        await using var f = await Fixture.CreateAsync(saved);
        var id = Guid.NewGuid();
        var result = await f.Launcher.LaunchAsync(ManagedBrowserLoginRequest.AddNew(id), BrowserFamily.Chrome);
        Assert.Equal(ManagedBrowserLoginStatus.Bound, result.Status);
        Assert.Equal(saved ? 0 : 1, f.Picker.Calls);
        var account = await f.ReadAccountAsync(id);
        Assert.Equal("ManagedChromium", account!.LoginMethod);
        Assert.Null(account.EncryptedSession);
        Assert.Null(account.WebView2ProfileDirectory);
        Assert.Equal("123", account.BaiduUserId);
        Assert.Equal(id, (await f.Store.LoadActiveSessionAsync(id, BrowserFamily.Chrome, default))!.Identity.AccountId);
        Assert.Equal([ManagedBrowserAction.Health, ManagedBrowserAction.ObserveAccountIdentity], f.Helper.Requests.Select(x => x.Action));
        var request = f.Helper.Requests[1];
        var payload = Assert.IsType<ManagedBrowserObserveAccountIdentityRequest>(request.Payload);
        Assert.Equal(FakeTargets.FixedBrowserId, payload.BrowserId);
        Assert.Equal("fixed-tab", payload.TargetId);
        Assert.Equal(new Uri("http://127.0.0.1:49152/"), payload.DevToolsEndpoint);
        Assert.Equal(id, request.AccountId);
    }

    [Theory]
    [InlineData("123", ManagedBrowserLoginStatus.Bound)]
    [InlineData("wrong-private-uid", ManagedBrowserLoginStatus.AccountIdentityMismatch)]
    public async Task ReloginUsesSelectedIdAndPreservesLegacyCredentials(string uid, ManagedBrowserLoginStatus expected)
    {
        await using var f = await Fixture.CreateAsync();
        var id = Guid.NewGuid();
        await f.SaveAccountAsync(AccountRecord.Create(id, "123", "旧账号") with { EncryptedSession = [1, 2], WebView2ProfileDirectory = @"C:\legacy" });
        f.Helper.Identity = new(ManagedBrowserIdentityResultKind.Observed, uid);
        var result = await f.Launcher.LaunchAsync(ManagedBrowserLoginRequest.ReloginExisting(id), BrowserFamily.Chrome);
        Assert.Equal(expected, result.Status);
        var account = (await f.ReadAccountAsync(id))!;
        Assert.Equal("WebView2", account.LoginMethod);
        Assert.Equal(new byte[] { 1, 2 }, account.EncryptedSession);
        Assert.Equal(@"C:\legacy", account.WebView2ProfileDirectory);
        Assert.Equal(expected == ManagedBrowserLoginStatus.Bound, await f.Store.LoadActiveSessionAsync(id, BrowserFamily.Chrome, default) is not null);
        Assert.DoesNotContain(uid, result.ToString());
    }

    [Theory]
    [InlineData(ManagedBrowserIdentityResultKind.LoginRequired, ManagedBrowserLoginStatus.LoginRequired)]
    [InlineData(ManagedBrowserIdentityResultKind.TimedOut, ManagedBrowserLoginStatus.TimedOut)]
    [InlineData(ManagedBrowserIdentityResultKind.Cancelled, ManagedBrowserLoginStatus.Cancelled)]
    [InlineData(ManagedBrowserIdentityResultKind.BrowserUnavailable, ManagedBrowserLoginStatus.BrowserUnavailable)]
    public async Task NonIdentityOutcomesDoNotPersistOrCloseVisibleBrowser(ManagedBrowserIdentityResultKind kind, ManagedBrowserLoginStatus expected)
    {
        await using var f = await Fixture.CreateAsync();
        var id = Guid.NewGuid();
        f.Helper.Identity = new(kind);
        Assert.Equal(expected, (await f.Launcher.LaunchAsync(ManagedBrowserLoginRequest.AddNew(id), BrowserFamily.Chrome)).Status);
        Assert.Null(await f.ReadAccountAsync(id));
        Assert.Null(await f.Store.LoadActiveSessionAsync(id, BrowserFamily.Chrome, default));
        Assert.Equal(0, f.Processes.Process.Terminations);
        Assert.True(f.Processes.Process.IsAlive);
        Assert.Equal(ManagedBrowserStopStatus.Stopped, await f.Lifecycle.StopPendingAsync(id, default));
    }

    [Fact]
    public async Task PendingTargetNeverStartsHelperAndRetryUsesSameOwnedProfile()
    {
        await using var f = await Fixture.CreateAsync();
        var request = ManagedBrowserLoginRequest.AddNew(Guid.NewGuid());
        f.Targets.Page = new Uri("about:blank");
        Assert.Equal(ManagedBrowserLoginStatus.Pending, (await f.Launcher.LaunchAsync(request, BrowserFamily.Chrome)).Status);
        Assert.Equal(0, f.HelperStarts);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Lifecycle.LoadActiveSessionAsync(request.AccountId, default));
        f.Targets.Page = new Uri("https://pan.baidu.com/");
        Assert.Equal(ManagedBrowserLoginStatus.Bound, (await f.Launcher.LaunchAsync(request, BrowserFamily.Chrome)).Status);
        Assert.Equal(1, f.Processes.LaunchCount);
    }

    [Fact]
    public async Task FailedOwnershipProofCannotStartHelperOrPersistIdentity()
    {
        await using var f = await Fixture.CreateAsync();
        var id = Guid.NewGuid();
        f.Listeners.Owner = 999;
        Assert.Equal(ManagedBrowserLoginStatus.BrowserUnavailable, (await f.Launcher.LaunchAsync(ManagedBrowserLoginRequest.AddNew(id), BrowserFamily.Chrome)).Status);
        Assert.Equal(0, f.HelperStarts);
        Assert.Null(await f.ReadAccountAsync(id));
    }

    [Fact]
    public async Task RepeatedReloginKeepsImmutableSessionAndCanonicalInstallation()
    {
        await using var f = await Fixture.CreateAsync();
        var id = Guid.NewGuid();
        Assert.Equal(ManagedBrowserLoginStatus.Bound, (await f.Launcher.LaunchAsync(ManagedBrowserLoginRequest.AddNew(id), BrowserFamily.Chrome)).Status);
        var first = (await f.Store.LoadActiveSessionAsync(id, BrowserFamily.Chrome, default))!;
        Assert.Equal(ManagedBrowserLoginStatus.Bound, (await f.Launcher.LaunchAsync(ManagedBrowserLoginRequest.ReloginExisting(id), BrowserFamily.Chrome)).Status);
        var second = (await f.Store.LoadActiveSessionAsync(id, BrowserFamily.Chrome, default))!;
        Assert.Equal(first.Identity.SessionId, second.Identity.SessionId);
        Assert.Equal(first.Identity.InstallationId, second.Identity.InstallationId);
        Assert.Equal(first.Revision, second.Revision);
        Assert.Equal(1, f.Processes.LaunchCount);
    }

    [Fact]
    public async Task ExplicitReselectNeedsExplicitFamilyWhenBothExist()
    {
        await using var f = await Fixture.CreateAsync();
        f.Files.Both = true;
        var request = ManagedBrowserLoginRequest.AddNew(Guid.NewGuid());
        Assert.Equal(ManagedBrowserLoginStatus.BrowserFamilyChoiceRequired, (await f.Launcher.LaunchAsync(request, reselect: true)).Status);
        Assert.Equal(0, f.Processes.LaunchCount);
        Assert.Equal(ManagedBrowserLoginStatus.Bound, (await f.Launcher.LaunchAsync(request, BrowserFamily.Edge)).Status);
        Assert.NotNull(await f.Store.LoadActiveSessionAsync(request.AccountId, BrowserFamily.Edge, default));
    }

    [Fact]
    public async Task FailedPendingStopKeepsOwnershipAndBlocksAccountLeaseRemoval()
    {
        await using var f = await Fixture.CreateAsync();
        var id = Guid.NewGuid();
        f.Targets.Page = new Uri("about:blank");
        await f.Launcher.LaunchAsync(ManagedBrowserLoginRequest.AddNew(id), BrowserFamily.Chrome);
        f.Processes.Process.ExitConfirmed = false;
        Assert.Equal(ManagedBrowserStopStatus.Blocked, await f.Lifecycle.StopPendingAsync(id, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Lifecycle.LoadActiveSessionAsync(id, default));
        f.Processes.Process.ExitConfirmed = true;
        Assert.Equal(ManagedBrowserStopStatus.Stopped, await f.Lifecycle.StopPendingAsync(id, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Lifecycle.LoadActiveSessionAsync(id, default));
    }

    [Fact]
    public async Task SharedAccountLeaseFencesLoginUntilRemovalDisablesAccount()
    {
        await using var f = await Fixture.CreateAsync();
        var id = Guid.NewGuid();
        await f.SaveAccountAsync(AccountRecord.Create(id, "123", "旧账号"));
        var lease = await f.Lifecycle.AcquireAccountLeaseAsync(id, default);
        var login = f.Launcher.LaunchAsync(ManagedBrowserLoginRequest.ReloginExisting(id), BrowserFamily.Chrome);
        Assert.False(login.IsCompleted);
        await f.SaveAccountAsync((await f.ReadAccountAsync(id))! with { IsEnabled = false, Status = "Disabled" });
        lease.Dispose();
        Assert.Equal(ManagedBrowserLoginStatus.AccountUnavailable, (await login).Status);
        Assert.Equal(0, f.Processes.LaunchCount);
    }

    [Theory]
    [InlineData("123", ManagedBrowserLoginStatus.Bound)]
    [InlineData("different", ManagedBrowserLoginStatus.AccountIdentityMismatch)]
    public async Task FamilySwitchStopsOldOwnerBeforeLaunchAndCommitsOnlyMatchingIdentity(string uid, ManagedBrowserLoginStatus expected)
    {
        await using var f = await Fixture.CreateAsync();
        f.Files.Both = true;
        var id = Guid.NewGuid();
        await f.Launcher.LaunchAsync(ManagedBrowserLoginRequest.AddNew(id), BrowserFamily.Chrome);
        var old = (await f.Store.LoadActiveSessionAsync(id, BrowserFamily.Chrome, default))!;
        var oldProcess = f.Processes.Process;
        f.Helper.Identity = new(ManagedBrowserIdentityResultKind.Observed, uid);
        Assert.Equal(expected, (await f.Launcher.LaunchAsync(ManagedBrowserLoginRequest.ReloginExisting(id), BrowserFamily.Edge)).Status);
        Assert.Equal(1, oldProcess.Terminations);
        Assert.Equal(2, f.Processes.LaunchCount);
        var currentOld = (await f.Store.LoadSessionAsync(old.Identity.SessionId, default))!;
        Assert.Equal(expected == ManagedBrowserLoginStatus.Bound ? ManagedBrowserSessionLifecycleState.Retired : ManagedBrowserSessionLifecycleState.Active, currentOld.State);
        Assert.Equal(old.Identity.ProfileId, currentOld.Identity.ProfileId);
        var target = await f.Store.LoadActiveSessionAsync(id, BrowserFamily.Edge, default);
        Assert.Equal(expected == ManagedBrowserLoginStatus.Bound, target is not null);
        if (target is not null) Assert.NotEqual(old.Identity.SessionId, target.Identity.SessionId);
    }

    [Fact]
    public async Task FamilySwitchDoesNotLaunchWhenOldOwnedJobCannotStop()
    {
        await using var f = await Fixture.CreateAsync();
        f.Files.Both = true;
        var id = Guid.NewGuid();
        await f.Launcher.LaunchAsync(ManagedBrowserLoginRequest.AddNew(id), BrowserFamily.Chrome);
        f.Processes.Process.ExitConfirmed = false;
        var result = await f.Launcher.LaunchAsync(ManagedBrowserLoginRequest.ReloginExisting(id), BrowserFamily.Edge);
        f.Processes.Process.ExitConfirmed = true;
        Assert.Equal(ManagedBrowserLoginStatus.ActiveWorkBlocked, result.Status);
        Assert.Equal(1, f.Processes.LaunchCount);
        Assert.NotNull(await f.Store.LoadActiveSessionAsync(id, BrowserFamily.Chrome, default));
    }

    [Fact]
    public async Task HelperCleanupFailureCannotReportUnchangedAfterCommittingAnAccount()
    {
        await using var f = await Fixture.CreateAsync();
        var id = Guid.NewGuid();
        f.Helper.FailDispose = true;
        Assert.Equal(ManagedBrowserLoginStatus.BrowserUnavailable, (await f.Launcher.LaunchAsync(ManagedBrowserLoginRequest.AddNew(id), BrowserFamily.Chrome)).Status);
        Assert.Null(await f.ReadAccountAsync(id));
        Assert.Null(await f.Store.LoadActiveSessionAsync(id, BrowserFamily.Chrome, default));
        Assert.True(f.Processes.Process.IsAlive);
    }

    [Fact]
    public async Task SessionRevisionChangedDuringObservationCannotBeSilentlyAccepted()
    {
        await using var f = await Fixture.CreateAsync();
        var id = Guid.NewGuid();
        await f.Launcher.LaunchAsync(ManagedBrowserLoginRequest.AddNew(id), BrowserFamily.Chrome);
        f.Helper.OnObserve = async () =>
        {
            await using var c = await f.Factory.OpenConnectionAsync();
            await using var command = c.CreateCommand();
            command.CommandText = "UPDATE ManagedBrowserSessions SET Revision=Revision+1;";
            await command.ExecuteNonQueryAsync();
        };
        Assert.Equal(ManagedBrowserLoginStatus.Conflict, (await f.Launcher.LaunchAsync(ManagedBrowserLoginRequest.ReloginExisting(id), BrowserFamily.Chrome)).Status);
    }

    [Fact]
    public async Task ManagedOnlyAccountsAreNotEligibleForLegacyOperations()
    {
        await using var f = await Fixture.CreateAsync();
        var id = Guid.NewGuid();
        await f.Launcher.LaunchAsync(ManagedBrowserLoginRequest.AddNew(id), BrowserFamily.Chrome);
        var eligible = await new BaiduBatchTransfer.App.WizardAccountProvider(new SqliteUnitOfWorkFactory(f.Factory)).LoadEligibleAsync();
        Assert.Empty(eligible);
    }

    [Fact]
    public async Task UnboundLoginProfileBlocksRemovalEvenAfterItsOwnedBrowserHasStopped()
    {
        await using var f = await Fixture.CreateAsync();
        var id = Guid.NewGuid();
        await f.SaveAccountAsync(AccountRecord.Create(id, "123", "旧账号"));
        f.Helper.Identity = new(ManagedBrowserIdentityResultKind.LoginRequired);
        await f.Launcher.LaunchAsync(ManagedBrowserLoginRequest.ReloginExisting(id), BrowserFamily.Chrome);
        using var lease = await f.Lifecycle.AcquireAccountLeaseAsync(id, default);
        Assert.Equal(ManagedBrowserStopStatus.Blocked, await f.Lifecycle.PreparePendingRemovalAsync(id, default));
        Assert.Equal(1, f.Processes.Process.Terminations);
        Assert.Equal(ManagedBrowserStopStatus.Blocked, await f.Lifecycle.PreparePendingRemovalAsync(id, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Lifecycle.LoadActiveSessionAsync(id, default));
        Assert.Equal("Active", (await f.ReadAccountAsync(id))!.Status);
    }

    [Fact]
    public async Task UnboundProfileRemainsVisibleToRemovalAfterSwitchingBackToOriginalFamily()
    {
        await using var f = await Fixture.CreateAsync();
        f.Files.Both = true;
        var id = Guid.NewGuid();
        await f.Launcher.LaunchAsync(ManagedBrowserLoginRequest.AddNew(id), BrowserFamily.Chrome);
        f.Helper.Identity = new(ManagedBrowserIdentityResultKind.LoginRequired);
        await f.Launcher.LaunchAsync(ManagedBrowserLoginRequest.ReloginExisting(id), BrowserFamily.Edge);
        f.Helper.Identity = new(ManagedBrowserIdentityResultKind.Observed, "123");
        Assert.Equal(ManagedBrowserLoginStatus.Bound, (await f.Launcher.LaunchAsync(ManagedBrowserLoginRequest.ReloginExisting(id), BrowserFamily.Chrome)).Status);
        using var lease = await f.Lifecycle.AcquireAccountLeaseAsync(id, default);
        Assert.Equal(ManagedBrowserStopStatus.Blocked, await f.Lifecycle.PreparePendingRemovalAsync(id, default));
    }

    [Fact]
    public async Task CancellationDuringHelperCleanupDoesNotCommitOrCloseBrowser()
    {
        await using var f = await Fixture.CreateAsync();
        var id = Guid.NewGuid();
        using var cancelled = new CancellationTokenSource();
        f.Helper.OnDispose = cancelled.Cancel;
        Assert.Equal(ManagedBrowserLoginStatus.Cancelled, (await f.Launcher.LaunchAsync(ManagedBrowserLoginRequest.AddNew(id), BrowserFamily.Chrome, cancellationToken: cancelled.Token)).Status);
        Assert.Null(await f.ReadAccountAsync(id));
        Assert.Equal(0, f.Processes.Process.Terminations);
    }

    [Fact]
    public async Task AccountRemovedDuringObservationCannotBeBound()
    {
        await using var f = await Fixture.CreateAsync();
        var id = Guid.NewGuid();
        await f.SaveAccountAsync(AccountRecord.Create(id, "123", "旧账号"));
        f.Helper.OnObserve = async () => await f.SaveAccountAsync((await f.ReadAccountAsync(id))! with { Status = "Disabled", IsEnabled = false });
        Assert.Equal(ManagedBrowserLoginStatus.AccountUnavailable, (await f.Launcher.LaunchAsync(ManagedBrowserLoginRequest.ReloginExisting(id), BrowserFamily.Chrome)).Status);
        Assert.Null(await f.Store.LoadActiveSessionAsync(id, BrowserFamily.Chrome, default));
    }

    [Fact]
    public async Task UserClosedBrowserDuringObservationCannotPersistSuccessfulIdentity()
    {
        await using var f = await Fixture.CreateAsync();
        var id = Guid.NewGuid();
        f.Helper.OnObserve = () => { f.Processes.Process.Alive = false; return Task.CompletedTask; };
        Assert.Equal(ManagedBrowserLoginStatus.BrowserUnavailable, (await f.Launcher.LaunchAsync(ManagedBrowserLoginRequest.AddNew(id), BrowserFamily.Chrome)).Status);
        Assert.Null(await f.ReadAccountAsync(id));
    }

    [Fact]
    public async Task FamilySwitchReadinessBlocksBeforeAnyOwnedBrowserStopOrNewLaunch()
    {
        await using var f = await Fixture.CreateAsync();
        f.Files.Both = true;
        var id = Guid.NewGuid();
        await f.Launcher.LaunchAsync(ManagedBrowserLoginRequest.AddNew(id), BrowserFamily.Chrome);
        f.Fence.Allowed = false;
        var oldProcess = f.Processes.Process;
        Assert.Equal(ManagedBrowserLoginStatus.ActiveWorkBlocked, (await f.Launcher.LaunchAsync(ManagedBrowserLoginRequest.ReloginExisting(id), BrowserFamily.Edge)).Status);
        Assert.Equal(0, oldProcess.Terminations);
        Assert.Equal(1, f.Processes.LaunchCount);
    }

    [Fact]
    public async Task FreshLifecycleBlocksOrphanProfileAfterRestartWithoutInventingSession()
    {
        await using var f = await Fixture.CreateAsync();
        var id = Guid.NewGuid();
        f.Helper.Identity = new(ManagedBrowserIdentityResultKind.LoginRequired);
        await f.Launcher.LaunchAsync(ManagedBrowserLoginRequest.AddNew(id), BrowserFamily.Chrome);
        await f.Lifecycle.StopPendingAsync(id, default);
        var restarted = new ManagedBrowserLifecycleCoordinator(f.Store, f.Manager, f.Root);
        Assert.Equal(ManagedBrowserStopStatus.Blocked, await restarted.PreparePendingRemovalAsync(id, default));
        Assert.Null(await f.Store.LoadActiveSessionAsync(id, BrowserFamily.Chrome, default));
    }

    [Fact]
    public async Task FreshLifecycleAllowsGenuinelyAbsentProfiles()
    {
        await using var f = await Fixture.CreateAsync();
        Assert.Equal(ManagedBrowserStopStatus.NotRunning, await f.Lifecycle.PreparePendingRemovalAsync(Guid.NewGuid(), default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovalWaitsForLoginOrFamilySwitchLeaseAndCancellationPreservesOldBinding(bool switching)
    {
        await using var f = await Fixture.CreateAsync();
        f.Files.Both = true;
        var id = Guid.NewGuid();
        await f.Launcher.LaunchAsync(ManagedBrowserLoginRequest.AddNew(id), BrowserFamily.Chrome);
        var old = (await f.Store.LoadActiveSessionAsync(id, BrowserFamily.Chrome, default))!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        f.Helper.OnObserve = () => { entered.SetResult(); return release.Task; };
        var login = f.Launcher.LaunchAsync(ManagedBrowserLoginRequest.ReloginExisting(id),
            switching ? BrowserFamily.Edge : BrowserFamily.Chrome, cancellationToken: cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var removal = PrepareRemovalAsync();
        Assert.False(removal.IsCompleted);
        Assert.True(f.Processes.Process.IsAlive);
        cancellation.Cancel();
        Assert.Equal(ManagedBrowserLoginStatus.Cancelled, (await login).Status);
        Assert.Equal(switching ? ManagedBrowserStopStatus.Blocked : ManagedBrowserStopStatus.Stopped, await removal);
        var persisted = (await f.Store.LoadActiveSessionAsync(id, BrowserFamily.Chrome, default))!;
        Assert.Equal(old.Identity.SessionId, persisted.Identity.SessionId);
        Assert.Equal(old.Revision, persisted.Revision);
        Assert.False(f.Processes.Process.IsAlive);
        async Task<ManagedBrowserStopStatus> PrepareRemovalAsync()
        {
            using var lease = await f.Lifecycle.AcquireAccountLeaseAsync(id, default);
            return await f.Lifecycle.PreparePendingRemovalAsync(id, default);
        }
    }

    [Fact]
    public async Task RemovalBlocksUncertainProfilePresenceRatherThanTreatingItAsAbsent()
    {
        await using var f = await Fixture.CreateAsync();
        var lifecycle = new ManagedBrowserLifecycleCoordinator(f.Store, f.Manager, f.Root, new UncertainProfiles());
        Assert.Equal(ManagedBrowserStopStatus.Blocked, await lifecycle.PreparePendingRemovalAsync(Guid.NewGuid(), default));
    }

    [Fact]
    public async Task RealRemovalOfLegacyAccountWithNoManagedProfileStillCompletes()
    {
        await using var f = await Fixture.CreateAsync();
        var id = Guid.NewGuid();
        await f.SaveAccountAsync(AccountRecord.Create(id, "123", "旧账号") with { EncryptedSession = [1, 2] });
        var removal = f.CreateRemoval();
        var preview = await removal.PreviewAsync(id, AccountExitHistoryMode.KeepHistory, default);
        Assert.Equal(AccountExitStatus.Ready, preview.Status);
        var result = await removal.ApplyAsync(new(id, AccountExitHistoryMode.KeepHistory, preview.ConfirmationToken, DateTimeOffset.UtcNow), default);
        Assert.Equal(AccountExitStatus.Removed, result.Status);
        Assert.True((await f.ReadAccountAsync(id))!.IsDeleted);
        Assert.Equal(0, f.Processes.LaunchCount);
    }

    [Fact]
    public async Task RealRemovalStopsBoundBrowserBeforeDurableQuarantineOfItsExactProfile()
    {
        await using var f = await Fixture.CreateAsync();
        var id = Guid.NewGuid();
        Assert.Equal(ManagedBrowserLoginStatus.Bound, (await f.Launcher.LaunchAsync(ManagedBrowserLoginRequest.AddNew(id), BrowserFamily.Chrome)).Status);
        var removal = f.CreateRemoval();
        var preview = await removal.PreviewAsync(id, AccountExitHistoryMode.KeepHistory, default);
        Assert.Equal(AccountExitStatus.Ready, preview.Status);
        var result = await removal.ApplyAsync(new(id, AccountExitHistoryMode.KeepHistory, preview.ConfirmationToken, DateTimeOffset.UtcNow), default);
        Assert.Equal(AccountExitStatus.CleanupPending, result.Status);
        Assert.Equal(1, f.Processes.Process.Terminations);
        var manifest = await f.Store.LoadLatestCleanupManifestAsync(id, default);
        Assert.Equal(ManagedProfileCleanupState.Quarantined, manifest!.State);
        Assert.False((await f.ReadAccountAsync(id))!.IsEnabled);
        Assert.Null((await f.ReadAccountAsync(id))!.EncryptedSession);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required SqliteConnectionFactory Factory;
        public required string Root;
        public required SqliteManagedBrowserStateStore Store;
        public required ManagedChromiumProcessManager Manager;
        public required ManagedBrowserLifecycleCoordinator Lifecycle;
        public ManagedBrowserLoginLauncher Launcher = null!;
        public FakeProcesses Processes = new();
        public FakeListeners Listeners = new();
        public FakeTargets Targets = new();
        public Picker Picker = new();
        public Files Files = new();
        public Helper Helper = new();
        public SwitchFence Fence = null!;
        public int HelperStarts;
        public static async Task<Fixture> CreateAsync(bool saved = true)
        {
            var root = Path.Combine(Path.GetTempPath(), "task11b-" + Guid.NewGuid().ToString("N"));
            var factory = new SqliteConnectionFactory(Path.Combine(root, "state.db"));
            await new DatabaseMigrator(factory, new DatabaseBackupService(factory, Path.Combine(root, "backups")), "test").MigrateAsync();
            var store = new SqliteManagedBrowserStateStore(factory);
            var profiles = new FileSystemManagedBrowserProfilePolicy(root);
            Fixture f = null!;
            var manager = new ManagedChromiumProcessManager((i, _) => Task.FromResult<IBrowserExecutableLaunchLease>(new FakeExecutable(i)),
                (p, _) => Task.FromResult<IManagedBrowserProfileLease>(new FakeProfile(p)),
                new DelegatingProcesses(() => f.Processes), new DelegatingListeners(() => f.Listeners), new DelegatingTargets(() => f.Targets), TimeSpan.FromMilliseconds(50));
            var lifecycle = new ManagedBrowserLifecycleCoordinator(store, manager, root);
            f = new Fixture { Factory = factory, Root = root, Store = store, Manager = manager, Lifecycle = lifecycle };
            f.Fence = new SwitchFence(store);
            var directory = new SqliteBrowserDirectoryState(factory);
            if (saved)
            {
                var intent = await directory.TryBeginEnsurePromptAsync(0, new(BrowserDirectorySelectionIntentKind.EnsurePrompt, Guid.NewGuid(), Guid.NewGuid()), default);
                await directory.TrySaveAsync(@"C:\Browser", intent.Intent!.Revision, intent.Intent, default);
            }
            var setup = new BrowserSetupService(f.Picker, f.Files, directory, new BrowserSelectionProcessContext(), new ProcessBrowserSelectionActivityRegistry(), new Verifier());
            f.Launcher = new ManagedBrowserLoginLauncher(setup, profiles, manager, lifecycle, store,
                new SqliteUnitOfWorkFactory(factory), new ManagedBrowserAccountService(store),
                new ManagedBrowserFamilySwitchCoordinator(store, f.Fence),
                _ => { f.HelperStarts++; return Task.FromResult<IManagedBrowserLoginHelper>(f.Helper); });
            return f;
        }
        public async Task<AccountRecord?> ReadAccountAsync(Guid id)
        {
            await using var unit = await new SqliteUnitOfWorkFactory(Factory).BeginAsync();
            return await unit.Accounts.GetByIdAsync(id);
        }
        public async Task SaveAccountAsync(AccountRecord account)
        {
            await using var unit = await new SqliteUnitOfWorkFactory(Factory).BeginAsync();
            await unit.Accounts.UpsertAsync(account);
            await unit.CommitAsync();
        }
        public ValueTask DisposeAsync() => Manager.DisposeAsync();
        public AccountRemovalCoordinator CreateRemoval()
        {
            var paths = new FileSystemAccountProfilePathPolicy(Path.Combine(Root, "legacy"));
            var managed = new FileSystemManagedBrowserProfileStore(Root, new FileSystemManagedBrowserProfilePolicy(Root));
            return new(new SqliteAccountRemovalStore(Factory),
                new RemoveAccountService(new SqliteUnitOfWorkFactory(Factory),
                    new FileSystemWebViewProfileStore(paths, new NoBrowserCloser()), paths, Store, managed), Lifecycle, managed);
        }
    }
    private sealed class NoBrowserCloser : IWebViewProfileCloser
    { public Task CloseAsync(Guid id, CancellationToken token) => Task.CompletedTask; }
    private sealed class Picker : IBrowserDirectoryPicker
    {
        public int Calls;
        public Task<string?> PickBrowserDirectoryAsync(CancellationToken token) { Calls++; return Task.FromResult<string?>(@"C:\Browser"); }
    }
    private sealed class Files : IBrowserDirectoryFileSystem
    {
        public bool Both;
        public Task<bool> FileExistsAsync(string path, CancellationToken token) => Task.FromResult(Both || path.EndsWith("chrome.exe", StringComparison.Ordinal));
    }
    private sealed class Verifier : IBrowserExecutableVerifier
    {
        public Task<BrowserExecutableVerificationResult> ReverifyForLaunchAsync(BrowserInstallationIdentity identity, CancellationToken token) =>
            Task.FromResult(BrowserExecutableVerificationResult.Verified(identity));
        public Task<BrowserExecutableVerificationResult> VerifyAsync(string directory, BrowserFamily family, CancellationToken token) =>
            Task.FromResult(BrowserExecutableVerificationResult.Verified(new BrowserInstallationIdentity(Guid.NewGuid(), family, directory,
                Path.Combine(directory, family == BrowserFamily.Chrome ? "chrome.exe" : "msedge.exe"),
                family == BrowserFamily.Chrome ? "Google LLC" : "Microsoft Corporation", family == BrowserFamily.Chrome ? "Google Chrome" : "Microsoft Edge", "fixed-file-" + family)));
    }
    private sealed class Helper : IManagedBrowserLoginHelper
    {
        public bool FailDispose;
        public Func<Task>? OnObserve;
        public Action? OnDispose;
        public List<ManagedBrowserRequest> Requests = [];
        public ManagedBrowserIdentityResult Identity = new(ManagedBrowserIdentityResultKind.Observed, "123");
        public async Task<ManagedBrowserResponse> SendAsync(ManagedBrowserRequest request, CancellationToken token)
        {
            Requests.Add(request);
            if (request.Action == ManagedBrowserAction.ObserveAccountIdentity && OnObserve is not null) await OnObserve().WaitAsync(token);
            return ManagedBrowserResponse.For(request,
                request.Action == ManagedBrowserAction.Health ? ManagedBrowserHealthResult.Healthy : Identity);
        }
        public ValueTask DisposeAsync()
        {
            OnDispose?.Invoke();
            return FailDispose ? ValueTask.FromException(new IOException("private data")) : ValueTask.CompletedTask;
        }
    }
    private sealed class SwitchFence(IManagedBrowserStateStore store) : IManagedBrowserFamilySwitchFence
    {
        public bool Allowed = true;
        public Task<bool> CanSwitchAsync(Guid id, CancellationToken token) => Allowed
            ? new ManagedBrowserFamilySwitchReadinessFence(store).CanSwitchAsync(id, token) : Task.FromResult(false);
    }
    private sealed class UncertainProfiles : IManagedBrowserProfilePolicy
    {
        public Task<ManagedBrowserProfilePresence> ProbeExistingAsync(Guid id, BrowserFamily family, CancellationToken token = default) => Task.FromResult(ManagedBrowserProfilePresence.Uncertain);
        public Task<ManagedBrowserProfileResult> GetOrCreateAsync(Guid id, BrowserFamily family, CancellationToken token = default) => throw new InvalidOperationException("Read-only removal must not create a profile.");
        public Task<ManagedBrowserProfileResult> OpenExistingAsync(ManagedBrowserProfileDescriptor profile, CancellationToken token = default) => throw new InvalidOperationException("Uncertain presence must fail closed.");
    }
    private sealed class DelegatingProcesses(Func<FakeProcesses> get) : IManagedChromiumProcessFactory
    {
        public IManagedChromiumProcess Launch(IBrowserExecutableLaunchLease e, IManagedBrowserProfileLease p)
        {
            if (!get().Process.IsAlive) get().Process = new FakeProcess();
            return get().Launch(e, p);
        }
    }
    private sealed class DelegatingListeners(Func<FakeListeners> get) : IWindowsTcpOwnerTable
    { public IReadOnlyList<TcpListenerOwner> ReadListeners() => get().ReadListeners(); }
    private sealed class DelegatingTargets(Func<FakeTargets> get) : IManagedBrowserTargetProbe
    {
        public Task<ManagedBrowserTargetSnapshot?> ReadAsync(DevToolsActivePort p, string? id, CancellationToken t, Action<string>? discovered = null) => get().ReadAsync(p, id, t, discovered);
    }
}
