namespace BaiduBatchTransfer.App;

public sealed class ApplicationShutdownCoordinator
{
    private readonly object gate = new();
    private readonly IAsyncDisposable queue;
    private readonly Func<CancellationToken, Task> stopHostAsync;
    private readonly IDisposable host;
    private readonly IAsyncDisposable? managedBrowser;
    private Task? queueDisposal;
    private Task<bool>? finalization;
    private Task? browserDisposal;

    public ApplicationShutdownCoordinator(
        IAsyncDisposable queue,
        Func<CancellationToken, Task> stopHostAsync,
        IDisposable host,
        IAsyncDisposable? managedBrowser = null)
    {
        this.queue = queue ?? throw new ArgumentNullException(nameof(queue));
        this.stopHostAsync = stopHostAsync ?? throw new ArgumentNullException(nameof(stopHostAsync));
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.managedBrowser = managedBrowser;
    }

    public async Task<bool> ShutdownAsync(
        TimeSpan queueTimeout,
        CancellationToken cancellationToken = default)
    {
        if (queueTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(queueTimeout));

        Task disposeQueue;
        lock (gate)
            disposeQueue = queueDisposal ??= DisposeQueueAsync();

        try
        {
            await disposeQueue.WaitAsync(queueTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Keep the host and its persistence dependencies alive. A later close request
            // can finish once the in-flight queue shutdown has durably fenced its work.
            return false;
        }

        Task<bool> finalize;
        lock (gate)
            finalize = finalization ??= FinalizeHostAsync();
        var completed = await finalize.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!completed)
            lock (gate)
                if (ReferenceEquals(finalization, finalize)) finalization = null;
        return completed;
    }

    private async Task DisposeQueueAsync() => await queue.DisposeAsync().ConfigureAwait(false);

    private async Task<bool> FinalizeHostAsync()
    {
        if (managedBrowser is not null)
        {
            try
            {
                browserDisposal ??= managedBrowser.DisposeAsync().AsTask();
                await browserDisposal.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
            catch (Exception)
            {
                if (browserDisposal?.IsFaulted == true || browserDisposal?.IsCanceled == true) browserDisposal = null;
                return false; // Do not dispose persistence/profile dependencies while owned exit is unconfirmed.
            }
        }
        try { await stopHostAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception)
        {
            // Stopping is best-effort during shutdown; disposal still releases the host.
        }
        finally { host.Dispose(); }
        return true;
    }
}

public sealed class ApplicationStoppingShutdownCoordinator : IDisposable
{
    private readonly Func<bool> tryBeginShutdown;
    private readonly Func<Func<Task>, Task> dispatchAsync;
    private readonly Func<Task<bool>> shutdownAsync;
    private readonly Action completeExit;
    private readonly Action failClosed;
    private readonly CancellationTokenRegistration registration;

    public ApplicationStoppingShutdownCoordinator(
        CancellationToken applicationStopping,
        Func<bool> tryBeginShutdown,
        Func<Func<Task>, Task> dispatchAsync,
        Func<Task<bool>> shutdownAsync,
        Action completeExit,
        Action failClosed)
    {
        this.tryBeginShutdown = tryBeginShutdown ?? throw new ArgumentNullException(nameof(tryBeginShutdown));
        this.dispatchAsync = dispatchAsync ?? throw new ArgumentNullException(nameof(dispatchAsync));
        this.shutdownAsync = shutdownAsync ?? throw new ArgumentNullException(nameof(shutdownAsync));
        this.completeExit = completeExit ?? throw new ArgumentNullException(nameof(completeExit));
        this.failClosed = failClosed ?? throw new ArgumentNullException(nameof(failClosed));
        registration = applicationStopping.Register(OnApplicationStopping);
    }

    public void Dispose() => registration.Dispose();

    private void OnApplicationStopping()
    {
        if (!tryBeginShutdown()) return;
        _ = ObserveShutdownAsync();
    }

    private async Task ObserveShutdownAsync()
    {
        try
        {
            await dispatchAsync(ShutdownOnDispatcherAsync).ConfigureAwait(false);
        }
        catch
        {
            TryFailClosed();
        }
    }

    private async Task ShutdownOnDispatcherAsync()
    {
        try
        {
            if (await shutdownAsync().ConfigureAwait(true))
                completeExit();
            else
                TryFailClosed();
        }
        catch
        {
            TryFailClosed();
        }
    }

    private void TryFailClosed()
    {
        try { failClosed(); }
        catch { }
    }
}
