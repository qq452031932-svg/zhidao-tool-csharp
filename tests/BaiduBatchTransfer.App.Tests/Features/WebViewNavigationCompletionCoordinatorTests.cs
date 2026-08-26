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
