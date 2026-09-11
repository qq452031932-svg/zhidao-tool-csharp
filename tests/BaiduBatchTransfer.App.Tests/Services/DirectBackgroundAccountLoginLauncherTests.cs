using BaiduBatchTransfer.App.Features.ManagedBrowser;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.App.Tests.Services;

public sealed class DirectBackgroundAccountLoginLauncherTests
{
    [Fact]
    public async Task AddUsesCookieCapturingDirectLoginRequest()
    {
        var presenter = new RecordingPresenter();
        var launcher = new DirectBackgroundAccountLoginLauncher(presenter, new SessionStore());

        await launcher.OpenAsync();

        var request = Assert.Single(presenter.Requests);
        Assert.Equal(BaiduLoginWindowPurpose.AddAccount, request.Purpose);
        Assert.NotEqual(Guid.Empty, request.AccountId);
        Assert.NotEqual(Guid.Empty, request.OwnershipToken);
    }

    [Fact]
    public async Task ReloginPassesExactAccountAndRequiresANewerEncryptedSession()
    {
        var accountId = Guid.NewGuid();
        var capturedAt = DateTimeOffset.Parse("2026-09-01T01:00:00Z");
        var store = new SessionStore(Session(accountId, capturedAt));
        var presenter = new RecordingPresenter(async request =>
        {
            Assert.Equal(BaiduLoginWindowPurpose.RefreshAccountSession, request.Purpose);
            Assert.Equal(accountId, request.AccountId);
            await store.SaveAsync(Session(accountId, capturedAt.AddMinutes(1)), default);
        });
        var launcher = new DirectBackgroundAccountLoginLauncher(presenter, store);

        var result = await launcher.ReloginAsync(accountId);

        Assert.Equal(ManagedBrowserLoginStatus.Bound, result.Status);
        Assert.Equal(accountId, (await store.LoadAsync(accountId.ToString("D"), default))!.LocalAccountId);
    }

    [Fact]
    public async Task ReloginWithoutANewerBoundSessionIsNotReportedAsSuccessful()
    {
        var accountId = Guid.NewGuid();
        var capturedAt = DateTimeOffset.Parse("2026-09-01T01:00:00Z");
        var store = new SessionStore(Session(accountId, capturedAt));
        var launcher = new DirectBackgroundAccountLoginLauncher(new RecordingPresenter(), store);

        var result = await launcher.ReloginAsync(accountId);

        Assert.Equal(ManagedBrowserLoginStatus.Cancelled, result.Status);
    }

    [Fact]
    public async Task ConcurrentReloginForSameAccountDoesNotOpenASecondWindow()
    {
        var accountId = Guid.NewGuid();
        var capturedAt = DateTimeOffset.Parse("2026-09-01T01:00:00Z");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var presenter = new RecordingPresenter(async _ =>
        {
            entered.TrySetResult();
            await release.Task;
        });
        var launcher = new DirectBackgroundAccountLoginLauncher(
            presenter,
            new SessionStore(Session(accountId, capturedAt)));

        var first = launcher.ReloginAsync(accountId);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = launcher.ReloginAsync(accountId);
        var completed = await Task.WhenAny(second, Task.Delay(TimeSpan.FromMilliseconds(200)));
        release.TrySetResult();
        await first;

        Assert.Same(second, completed);
        Assert.Equal(ManagedBrowserLoginStatus.Cancelled, (await second).Status);
        Assert.Single(presenter.Requests);
    }

    private static AccountSession Session(Guid accountId, DateTimeOffset capturedAt) => new(
        accountId,
        "123456",
        [new SessionCookie("BDUSS", "synthetic", ".baidu.com", "/", null, true, true)],
        capturedAt);

    private sealed class RecordingPresenter(
        Func<BaiduLoginWindowRequest, Task>? onShow = null) : IBaiduLoginWindowPresenter
    {
        public List<BaiduLoginWindowRequest> Requests { get; } = [];

        public async Task ShowAsync(BaiduLoginWindowRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (onShow is not null) await onShow(request);
        }
    }

    private sealed class SessionStore(AccountSession? initial = null) : IBaiduSessionStore
    {
        private AccountSession? session = initial;

        public Task SaveAsync(AccountSession value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            session = value;
            return Task.CompletedTask;
        }

        public Task<AccountSession?> LoadAsync(string accountId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(session?.AccountId == accountId ? session : null);
        }

        public Task RemoveAsync(string accountId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (session?.AccountId == accountId) session = null;
            return Task.CompletedTask;
        }
    }
}
