using BaiduBatchTransfer.App.Features.Acceptance;
using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class BaiduAcceptanceProbeLauncherTests
{
    [Fact]
    public async Task ShowsTheProbeWhenDebugSwitchAndEligibleAccountArePresent()
    {
        var account = Account();
        var presenter = new RecordingPresenter();
        var profiles = new RecordingProfilePathPolicy();
        var launcher = Launcher([account], presenter, profiles: profiles);

        var result = await launcher.OpenAsync(EnabledOptions());

        Assert.True(result.Started);
        Assert.Equal(account.Id, presenter.AccountId);
        Assert.Equal(profiles.IsolatedProfilePath, presenter.ProfilePath);
        Assert.NotEqual(account.WebView2ProfileDirectory, presenter.ProfilePath);
        Assert.True(profiles.LeaseDisposed);
        Assert.Equal(BaiduAcceptanceProbeCleanupOutcome.Completed, result.CleanupOutcome);
    }

    [Theory]
    [InlineData(true, "--not-the-probe")]
    [InlineData(false, "--baidu-field-probe")]
    public async Task DoesNotShowTheProbeWithoutTheDebugSwitchOrInRelease(bool isDebugBuild, string argument)
    {
        var presenter = new RecordingPresenter();
        var launcher = Launcher([Account()], presenter);

        var result = await launcher.OpenAsync(new BaiduAcceptanceProbeLaunchOptions(isDebugBuild, [argument]));

        Assert.False(result.Started);
        Assert.Equal("PROBE_DISABLED", result.SafeMessage);
        Assert.Null(presenter.AccountId);
    }

    [Fact]
    public async Task ReturnsOnlyAStableFailureCodeWhenNoEligibleAccountExists()
    {
        var presenter = new RecordingPresenter();
        var launcher = Launcher([], presenter);

        var result = await launcher.OpenAsync(EnabledOptions());

        Assert.False(result.Started);
        Assert.Equal("PROBE_NO_ELIGIBLE_ACCOUNT", result.SafeMessage);
        Assert.Null(presenter.AccountId);
    }

    [Fact]
    public async Task ReturnsOnlyAStableFailureCodeWhenThePresenterCannotShowTheWindow()
    {
        var account = Account();
        var presenter = new ThrowingPresenter("private profile C:\\secret\\profile could not be opened");
        var launcher = Launcher([account], presenter);

        var result = await launcher.OpenAsync(EnabledOptions());

        Assert.False(result.Started);
        Assert.Equal("PROBE_PRESENTATION_FAILED", result.SafeMessage);
        Assert.Equal(BaiduAcceptanceProbeCleanupOutcome.NotRequired, result.CleanupOutcome);
        Assert.DoesNotContain("secret", result.SafeMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReturnsPresentationFailureWhenTheActualPresenterReportsWindowInitializationFailure()
    {
        var window = new FailingProbeWindow();
        var presenter = new BaiduAcceptanceProbePresenter(
            new InlineProbeDispatcher(), new FailingProbeWindowFactory(window));
        var launcher = Launcher([Account()], presenter);

        var result = await launcher.OpenAsync(EnabledOptions());

        Assert.False(result.Started);
        Assert.Equal("PROBE_PRESENTATION_FAILED", result.SafeMessage);
        Assert.True(window.WasShown);
    }

    [Fact]
    public async Task WaitsForWindowClosureBeforeReportingInitializationFailureAndCleaningTheProfile()
    {
        var window = new PendingCloseFailingProbeWindow();
        var profiles = new RecordingProfilePathPolicy();
        var presenter = new BaiduAcceptanceProbePresenter(new InlineProbeDispatcher(), new PendingCloseFactory(window));
        var launch = Launcher([Account()], presenter, profiles: profiles).OpenAsync(EnabledOptions());

        await Task.Yield();
        Assert.False(launch.IsCompleted);
        Assert.False(profiles.LeaseDisposed);

        window.Close();
        var result = await launch;
        Assert.False(result.Started);
        Assert.True(profiles.LeaseDisposed);
    }

    [Fact]
    public async Task DoesNotPresentWhenTheEvidenceDirectoryIsUnavailable()
    {
        var presenter = new RecordingPresenter();
        var launcher = Launcher([Account()], presenter, new UnavailableOutputStore());

        var result = await launcher.OpenAsync(EnabledOptions());

        Assert.False(result.Started);
        Assert.Equal("PROBE_OUTPUT_UNAVAILABLE", result.SafeMessage);
        Assert.Null(presenter.AccountId);
    }

    [Fact]
    public async Task ReportsDeferredCleanupWithoutChangingASuccessfulPresentationToFailure()
    {
        var presenter = new RecordingPresenter();
        var launcher = Launcher([Account()], presenter, profiles: new DeferredCleanupProfilePathPolicy());

        var result = await launcher.OpenAsync(EnabledOptions());

        Assert.True(result.Started);
        Assert.Equal(BaiduAcceptanceProbeCleanupOutcome.Deferred, result.CleanupOutcome);
        Assert.NotNull(presenter.AccountId);
    }

    [Fact]
    public async Task CancellationWaitsForWindowTeardownBeforeCleaningTheIsolatedProfile()
    {
        using var cancellation = new CancellationTokenSource();
        var window = new CancellationPendingProbeWindow();
        var profiles = new RecordingProfilePathPolicy();
        var presenter = new BaiduAcceptanceProbePresenter(new InlineProbeDispatcher(), new CancellationPendingFactory(window));
        var launch = Launcher([Account()], presenter, profiles: profiles).OpenAsync(EnabledOptions(), cancellation.Token);

        await window.InitializationStarted.Task;
        cancellation.Cancel();
        await Task.Yield();
        Assert.False(launch.IsCompleted);
        Assert.False(profiles.LeaseDisposed);

        window.Close();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => launch);
        Assert.True(profiles.LeaseDisposed);
        Assert.Equal(BaiduAcceptanceProbeCleanupOutcome.Completed, profiles.Lease!.CleanupOutcome);
    }

    [Fact]
    public async Task CancellationWithUnclosedWindowDefersCleanupWithoutHangingOrReportingPresentationFailure()
    {
        using var cancellation = new CancellationTokenSource();
        var window = new CancellationPendingProbeWindow();
        var profiles = new RecordingProfilePathPolicy();
        var presenter = new BaiduAcceptanceProbePresenter(
            new InlineProbeDispatcher(), new CancellationPendingFactory(window), TimeSpan.FromMilliseconds(10));
        var launch = Launcher([Account()], presenter, profiles: profiles).OpenAsync(EnabledOptions(), cancellation.Token);

        await window.InitializationStarted.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => launch);
        Assert.False(profiles.LeaseDisposed);
        Assert.Equal(BaiduAcceptanceProbeCleanupOutcome.Deferred, profiles.Lease!.CleanupOutcome);
    }

    private static BaiduAcceptanceProbeLaunchOptions EnabledOptions() =>
        new(isDebugBuild: true, ["--baidu-field-probe"]);

    private static BaiduAcceptanceProbeLauncher Launcher(
        IReadOnlyList<AccountRecord> accounts,
        IBaiduAcceptanceProbePresenter presenter,
        IBaiduAcceptanceProbeOutputStore? outputStore = null,
        IBaiduAcceptanceProbeProfilePathPolicy? profiles = null) =>
        new(new FakeUnitOfWorkFactory(accounts), new BaiduAcceptanceProbeAccountSelector(),
            profiles ?? new RecordingProfilePathPolicy(), outputStore ?? new AvailableOutputStore(), presenter);

    private static AccountRecord Account() => new(
        Guid.NewGuid(), "private-user", "private-name", [1], "C:\\safe\\profile", "WebView2", "Active",
        null, null, 0, 0, 0, true, DateTimeOffset.UtcNow, "2026-08-24", false, null);

    private sealed class ThrowingPresenter(string message) : IBaiduAcceptanceProbePresenter
    {
        public Task ShowAsync(Guid accountId, string profilePath, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(message);
    }

    private sealed class RecordingPresenter : IBaiduAcceptanceProbePresenter
    {
        public Guid? AccountId { get; private set; }
        public string? ProfilePath { get; private set; }
        public Task ShowAsync(Guid accountId, string profilePath, CancellationToken cancellationToken = default)
        {
            AccountId = accountId;
            ProfilePath = profilePath;
            return Task.CompletedTask;
        }
    }

    private sealed class AvailableOutputStore : IBaiduAcceptanceProbeOutputStore
    {
        public Task EnsureWritableAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string> SaveAsync(IReadOnlyList<BaiduAcceptanceEvidence> evidence, CancellationToken cancellationToken = default) =>
            Task.FromResult("not-used");
    }

    private sealed class UnavailableOutputStore : IBaiduAcceptanceProbeOutputStore
    {
        public Task EnsureWritableAsync(CancellationToken cancellationToken = default) =>
            throw new IOException("private output directory");
        public Task<string> SaveAsync(IReadOnlyList<BaiduAcceptanceEvidence> evidence, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingProfilePathPolicy : IBaiduAcceptanceProbeProfilePathPolicy
    {
        public string IsolatedProfilePath { get; } = Path.Combine(Path.GetTempPath(), "isolated-probe-profile");
        public bool LeaseDisposed { get; private set; }
        public BaiduAcceptanceProbeProfileLease? Lease { get; private set; }
        public BaiduAcceptanceProbeProfileLease Create(Guid accountId) => Lease =
            new(IsolatedProfilePath, () => LeaseDisposed = true);
    }

    private sealed class DeferredCleanupProfilePathPolicy : IBaiduAcceptanceProbeProfilePathPolicy
    {
        public BaiduAcceptanceProbeProfileLease Create(Guid accountId) => new(
            Path.Combine(Path.GetTempPath(), "isolated-probe-profile"),
            () => Task.FromResult(BaiduAcceptanceProbeCleanupOutcome.Deferred));
    }

    private sealed class InlineProbeDispatcher : IBaiduAcceptanceProbeUiDispatcher
    {
        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            action();
            return Task.CompletedTask;
        }
    }

    private sealed class FailingProbeWindowFactory(FailingProbeWindow window) : IBaiduAcceptanceProbeWindowFactory
    {
        public IBaiduAcceptanceProbeWindow Create(Guid accountId, string profilePath) => window;
    }

    private sealed class FailingProbeWindow : IBaiduAcceptanceProbeWindow
    {
        public bool WasShown { get; private set; }
        public void Show() => WasShown = true;
        public void RequestClose() { }
        public Task WaitForInitializationAsync(CancellationToken cancellationToken = default) =>
            Task.FromException(new InvalidOperationException("private initialization failure"));
        public Task WaitForClosedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class PendingCloseFactory(PendingCloseFailingProbeWindow window) : IBaiduAcceptanceProbeWindowFactory
    {
        public IBaiduAcceptanceProbeWindow Create(Guid accountId, string profilePath) => window;
    }

    private sealed class PendingCloseFailingProbeWindow : IBaiduAcceptanceProbeWindow
    {
        private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Show() { }
        public void RequestClose() { }
        public Task WaitForInitializationAsync(CancellationToken cancellationToken = default) =>
            Task.FromException(new InvalidOperationException("private initialization failure"));
        public Task WaitForClosedAsync(CancellationToken cancellationToken = default) => closed.Task;
        public void Close() => closed.TrySetResult();
    }

    private sealed class CancellationPendingFactory(CancellationPendingProbeWindow window) : IBaiduAcceptanceProbeWindowFactory
    {
        public IBaiduAcceptanceProbeWindow Create(Guid accountId, string profilePath) => window;
    }

    private sealed class CancellationPendingProbeWindow : IBaiduAcceptanceProbeWindow
    {
        private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource InitializationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Show() { }
        public void RequestClose() { }
        public async Task WaitForInitializationAsync(CancellationToken cancellationToken = default)
        {
            InitializationStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        public Task WaitForClosedAsync(CancellationToken cancellationToken = default) => closed.Task.WaitAsync(cancellationToken);
        public void Close() => closed.TrySetResult();
    }

    private sealed class FakeUnitOfWorkFactory(IReadOnlyList<AccountRecord> accounts) : IUnitOfWorkFactory
    {
        public Task<IUnitOfWork> BeginAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IUnitOfWork>(new FakeUnitOfWork(accounts));
    }

    private sealed class FakeUnitOfWork(IReadOnlyList<AccountRecord> accounts) : IUnitOfWork
    {
        public IImportRepository Imports => throw new NotSupportedException();
        public IJobRepository Jobs => throw new NotSupportedException();
        public IAccountRepository Accounts { get; } = new FakeAccountRepository(accounts);
        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeAccountRepository(IReadOnlyList<AccountRecord> accounts) : IAccountRepository
    {
        public Task<Page<AccountRecord>> GetPageAsync(PageQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult(new Page<AccountRecord>(accounts, accounts.Count, query.PageNumber, query.PageSize));
        public Task UpsertAsync(AccountRecord account, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AccountRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AccountRecord?> GetByBaiduUserIdAsync(string baiduUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetEncryptedSessionAsync(Guid id, byte[]? encryptedSession, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpsertRuleAsync(AccountRuleRecord rule, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Page<AccountRuleRecord>> GetRulesPageAsync(Guid accountId, PageQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
