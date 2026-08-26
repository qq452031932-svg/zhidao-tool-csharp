using BaiduBatchTransfer.App.Features.BrowserAutomation;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class WebViewNavigationCompletionCoordinatorTests
{
    [Fact]
    public async Task NavigationWaitsForMatchingSuccessAndIgnoresStaleCompletion()
    {
        var coordinator = new WebViewNavigationCompletionCoordinator();
        var target = new Uri("https://pan.baidu.com/s/expected");

        using var firstCancellation = new CancellationTokenSource();
        var first = InvokeNavigation(coordinator, target, firstCancellation.Token);
        coordinator.OnNavigationStarting(41UL, target, isAllowed: true);
        Assert.False(first.IsCompleted);
        firstCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        var second = InvokeNavigation(coordinator, target, CancellationToken.None);
        coordinator.OnNavigationCompleted(41UL, isSuccess: true);
        Assert.False(second.IsCompleted);
        coordinator.OnNavigationStarting(42UL, target, isAllowed: true);
        coordinator.OnNavigationCompleted(42UL, isSuccess: true);

        await second;
    }

    [Fact]
    public async Task NavigationStartingWithDifferentFragmentCannotReleaseRequestedNavigation()
    {
        var coordinator = new WebViewNavigationCompletionCoordinator();
        var target = new Uri("https://pan.baidu.com/disk/main#/all?path=%2Fexpected");
        var unrelated = new Uri("https://pan.baidu.com/disk/main#/all?path=%2Funrelated");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var navigation = InvokeNavigation(coordinator, target, timeout.Token);

        coordinator.OnNavigationStarting(51UL, unrelated, isAllowed: true);
        coordinator.OnNavigationCompleted(51UL, isSuccess: true);
        await Task.Yield();

        Assert.False(navigation.IsCompleted);
        coordinator.OnNavigationStarting(
            52UL,
            new Uri(target.AbsoluteUri),
            isAllowed: true);
        coordinator.OnNavigationCompleted(52UL, isSuccess: true);
        await navigation;
    }

    [Fact]
    public async Task DispatchedNavigationInvokesNavigateOnlyInsideDispatcher()
    {
        var coordinator = new WebViewNavigationCompletionCoordinator();
        var target = new Uri("https://pan.baidu.com/s/dispatched");
        var insideDispatcher = false;
        var navigateInsideDispatcher = false;
        Action navigate = () => navigateInsideDispatcher = insideDispatcher;
        Task DispatchAsync(Func<Task> callback)
        {
            insideDispatcher = true;
            var dispatched = callback();
            insideDispatcher = false;
            return dispatched;
        }

        var navigation = coordinator.NavigateDispatchedAsync(
            target,
            navigate,
            DispatchAsync,
            CancellationToken.None,
            CancellationToken.None);
        Assert.True(navigateInsideDispatcher);
        Assert.False(navigation.IsCompleted);
        coordinator.OnNavigationStarting(61UL, target, isAllowed: true);
        coordinator.OnNavigationCompleted(61UL, isSuccess: true);
        await navigation;
    }

    [Fact]
    public async Task FailedCompletionStopsCallerBeforeFollowingAction()
    {
        var coordinator = new WebViewNavigationCompletionCoordinator();
        var target = new Uri("https://pan.baidu.com/s/failed");
        var followingActionCalled = false;

        async Task NavigateThenActAsync()
        {
            await coordinator.NavigateAsync(
                target, () => { }, CancellationToken.None, CancellationToken.None);
            followingActionCalled = true;
        }

        var flow = NavigateThenActAsync();
        coordinator.OnNavigationStarting(71UL, target, isAllowed: true);
        coordinator.OnNavigationCompleted(71UL, isSuccess: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => flow);
        Assert.False(followingActionCalled);
    }

    [Fact]
    public async Task UserCloseClearsPendingNavigationForNextRequest()
    {
        var coordinator = new WebViewNavigationCompletionCoordinator();
        var target = new Uri("https://pan.baidu.com/s/closed");
        using var userClosed = new CancellationTokenSource();
        var interrupted = coordinator.NavigateAsync(
            target, () => { }, CancellationToken.None, userClosed.Token);

        userClosed.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => interrupted);

        var retry = InvokeNavigation(coordinator, target, CancellationToken.None);
        coordinator.OnNavigationStarting(81UL, target, isAllowed: true);
        coordinator.OnNavigationCompleted(81UL, isSuccess: true);
        await retry;
    }

    [Fact]
    public async Task NavigateThrowClearsPendingNavigationForNextRequest()
    {
        var coordinator = new WebViewNavigationCompletionCoordinator();
        var target = new Uri("https://pan.baidu.com/s/throw");

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.NavigateAsync(
            target,
            () => throw new InvalidOperationException("navigate failed"),
            CancellationToken.None,
            CancellationToken.None));

        var retry = InvokeNavigation(coordinator, target, CancellationToken.None);
        coordinator.OnNavigationStarting(91UL, target, isAllowed: true);
        coordinator.OnNavigationCompleted(91UL, isSuccess: true);
        await retry;
    }

    private static Task InvokeNavigation(
        WebViewNavigationCompletionCoordinator coordinator,
        Uri target,
        CancellationToken cancellationToken)
    {
        var navigateCalled = false;
        Action navigate = () => navigateCalled = true;
        var task = coordinator.NavigateAsync(
            target, navigate, cancellationToken, CancellationToken.None);
        Assert.True(navigateCalled);
        Assert.False(task.IsCompleted);
        return task;
    }
}
