namespace BaiduBatchTransfer.App.Features.BrowserAutomation;

internal sealed class WebViewNavigationCompletionCoordinator
{
    private readonly object sync = new();
    private PendingNavigation? pending;

    internal Task NavigateDispatchedAsync(
        Uri target,
        Action navigate,
        Func<Func<Task>, Task> dispatchAsync,
        CancellationToken cancellationToken,
        CancellationToken userClosed)
    {
        ArgumentNullException.ThrowIfNull(dispatchAsync);
        return dispatchAsync(() => NavigateAsync(
            target,
            navigate,
            cancellationToken,
            userClosed));
    }

    internal async Task NavigateAsync(
        Uri target,
        Action navigate,
        CancellationToken cancellationToken,
        CancellationToken userClosed)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(navigate);
        using var fence = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            userClosed);
        fence.Token.ThrowIfCancellationRequested();
        var request = new PendingNavigation(target);
        lock (sync)
        {
            if (pending is not null)
                throw new InvalidOperationException("另一个网页导航仍在进行中。");
            pending = request;
        }

        try
        {
            navigate();
            await request.Completion.Task.WaitAsync(fence.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (sync)
            {
                if (ReferenceEquals(pending, request)) pending = null;
            }
        }
    }

    internal void OnNavigationStarting(ulong navigationId, Uri? destination, bool isAllowed)
    {
        TaskCompletionSource? rejected = null;
        lock (sync)
        {
            if (pending is not { } request) return;
            if (request.NavigationId is { } expectedId)
            {
                if (expectedId == navigationId && !isAllowed) rejected = request.Completion;
            }
            else if (destination is { IsAbsoluteUri: true } && string.Equals(
                destination.AbsoluteUri,
                request.TargetAbsoluteUri,
                StringComparison.Ordinal))
            {
                request.NavigationId = navigationId;
                if (!isAllowed) rejected = request.Completion;
            }
        }

        rejected?.TrySetException(NavigationFailure());
    }

    internal void OnNavigationCompleted(ulong navigationId, bool isSuccess)
    {
        TaskCompletionSource? completion = null;
        lock (sync)
        {
            if (pending is { NavigationId: { } expectedId } request &&
                expectedId == navigationId)
                completion = request.Completion;
        }

        if (completion is null) return;
        if (isSuccess) completion.TrySetResult();
        else completion.TrySetException(NavigationFailure());
    }

    private static InvalidOperationException NavigationFailure() =>
        new("百度官方页面导航失败，未执行网页操作。");

    private sealed class PendingNavigation(Uri target)
    {
        public string TargetAbsoluteUri { get; } = target.AbsoluteUri;
        public ulong? NavigationId { get; set; }
        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
