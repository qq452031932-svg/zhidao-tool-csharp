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
        var launcher = Launcher([account], presenter);

        var result = await launcher.OpenAsync(EnabledOptions());

        Assert.True(result.Started);
        Assert.Equal(account.Id, presenter.AccountId);
        Assert.Equal(account.WebView2ProfileDirectory, presenter.ProfilePath);
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
        Assert.DoesNotContain("secret", result.SafeMessage, StringComparison.OrdinalIgnoreCase);
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

    private static BaiduAcceptanceProbeLaunchOptions EnabledOptions() =>
        new(isDebugBuild: true, ["--baidu-field-probe"]);

    private static BaiduAcceptanceProbeLauncher Launcher(
        IReadOnlyList<AccountRecord> accounts,
        IBaiduAcceptanceProbePresenter presenter,
        IBaiduAcceptanceProbeOutputStore? outputStore = null) =>
        new(new FakeUnitOfWorkFactory(accounts), new BaiduAcceptanceProbeAccountSelector(), outputStore ?? new AvailableOutputStore(), presenter);

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
