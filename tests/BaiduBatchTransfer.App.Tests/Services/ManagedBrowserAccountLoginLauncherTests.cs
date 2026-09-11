using BaiduBatchTransfer.App.Features.ManagedBrowser;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Application.Accounts;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.App.Tests.Services;

public sealed class ManagedBrowserAccountLoginLauncherTests
{
    [Fact]
    public async Task AddReservesOneIdPerWindowAndReloginPassesExactSelectedId()
    {
        var presenter = new Presenter();
        var launcher = new ManagedBrowserAccountLoginLauncher(presenter);
        await launcher.OpenAsync();
        await launcher.OpenAsync();
        var id = Guid.NewGuid();
        await launcher.ReloginAsync(id);
        Assert.Equal(3, presenter.Requests.Count);
        Assert.All(presenter.Requests.Take(2), request => Assert.Equal(ManagedBrowserLoginRequestKind.AddNew, request.Kind));
        Assert.NotEqual(presenter.Requests[0].AccountId, presenter.Requests[1].AccountId);
        Assert.Equal(ManagedBrowserLoginRequestKind.ReloginExisting, presenter.Requests[2].Kind);
        Assert.Equal(id, presenter.Requests[2].AccountId);
    }

    [Fact]
    public async Task AcceptanceRestartCanResumeTheExactFirstUnboundProfileOnlyOnce()
    {
        var presenter = new Presenter();
        var preserved = Guid.NewGuid();
        var launcher = new ManagedBrowserAccountLoginLauncher(presenter, preserved);

        await launcher.OpenAsync();
        await launcher.OpenAsync();

        Assert.Equal(preserved, presenter.Requests[0].AccountId);
        Assert.NotEqual(preserved, presenter.Requests[1].AccountId);
        Assert.NotEqual(Guid.Empty, presenter.Requests[1].AccountId);
    }

    [Theory]
    [InlineData(false, "--managed-browser-account-id=11111111111111111111111111111111", null)]
    [InlineData(true, "--managed-browser-account-id=not-a-guid", null)]
    [InlineData(true, "--managed-browser-account-id=11111111111111111111111111111111",
        "11111111-1111-1111-1111-111111111111")]
    public void ResumeProfileArgumentIsAcceptedOnlyInAcceptanceMode(
        bool acceptanceEnabled,
        string argument,
        string? expected)
    {
        var result = ManagedBrowserAccountLoginLauncher.ResumeProfileFromStartup(
            [argument], acceptanceEnabled);

        Assert.Equal(expected is null ? null : Guid.Parse(expected), result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModalConstructionAndShowAreDispatchedAndOnlyExistingAccountLoadsKnownFamily(bool relogin)
    {
        var dispatcher = new Dispatcher();
        var operations = new Operations();
        var id = Guid.NewGuid();
        var lookups = new List<Guid>();
        var presenter = new ManagedBrowserLoginWindowPresenter(operations, dispatcher,
            vm =>
            {
                Assert.True(dispatcher.InDispatch);
                Assert.Equal(relogin ? BrowserFamily.Edge : null, vm.SelectedFamily);
                return new Dialog(() =>
                {
                    Assert.True(dispatcher.InDispatch);
                    vm.CheckLoginAsync().GetAwaiter().GetResult();
                });
            },
            (accountId, _) => { lookups.Add(accountId); return Task.FromResult<BrowserFamily?>(BrowserFamily.Edge); });
        var request = relogin ? ManagedBrowserLoginRequest.ReloginExisting(id) : ManagedBrowserLoginRequest.AddNew(id);
        Assert.Equal(ManagedBrowserLoginStatus.Bound, (await presenter.ShowAsync(request, default)).Status);
        Assert.Equal(relogin ? new[] { id } : [], lookups);
        Assert.Equal(1, dispatcher.Calls);
    }

    [Fact]
    public async Task CancellationRequestsModalCloseAndClosedViewModelCannotRetry()
    {
        using var cancellation = new CancellationTokenSource();
        var operations = new Operations();
        ManagedBrowserLoginViewModel? captured = null;
        Dialog? dialog = null;
        var presenter = new ManagedBrowserLoginWindowPresenter(operations, new Dispatcher(),
            vm => { captured = vm; return dialog = new Dialog(cancellation.Cancel); },
            (_, _) => Task.FromResult<BrowserFamily?>(null));
        Assert.Equal(ManagedBrowserLoginStatus.Cancelled, (await presenter.ShowAsync(
            ManagedBrowserLoginRequest.AddNew(Guid.NewGuid()), cancellation.Token)).Status);
        Assert.Equal(1, dialog!.CloseRequests);
        await captured!.CheckLoginAsync();
        Assert.Equal(0, operations.Calls);
    }

    [Fact]
    public async Task AlreadyCancelledRequestNeverCreatesWindow()
    {
        var presenter = new ManagedBrowserLoginWindowPresenter(new Operations(), new Dispatcher(),
            _ => throw new Exception("Must not create window"), (_, _) => throw new Exception("Must not load family"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => presenter.ShowAsync(
            ManagedBrowserLoginRequest.AddNew(Guid.NewGuid()), new CancellationToken(true)));
    }

    private sealed class Presenter : IManagedBrowserLoginWindowPresenter
    {
        public List<ManagedBrowserLoginRequest> Requests { get; } = [];
        public Task<ManagedBrowserLoginResult> ShowAsync(ManagedBrowserLoginRequest request, CancellationToken cancellationToken)
        { Requests.Add(request); return Task.FromResult(new ManagedBrowserLoginResult(ManagedBrowserLoginStatus.Cancelled)); }
    }
    private sealed class Operations : IManagedBrowserLoginOperations
    {
        public int Calls;
        public Task<ManagedBrowserLoginResult> LaunchAsync(ManagedBrowserLoginRequest request, BrowserFamily? family = null,
            bool reselect = false, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(new ManagedBrowserLoginResult(ManagedBrowserLoginStatus.Bound)); }
    }
    private sealed class Dispatcher : IUiDispatcher
    {
        public bool InDispatch;
        public int Calls;
        public Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken = default)
        {
            Calls++;
            InDispatch = true;
            try { return Task.FromResult(action()); }
            finally { InDispatch = false; }
        }
    }
    private sealed class Dialog(Action show) : IManagedBrowserLoginDialog
    {
        public int CloseRequests;
        public void ShowDialog() => show();
        public void RequestClose() => CloseRequests++;
    }
}
