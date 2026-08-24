namespace BaiduBatchTransfer.App;

public sealed class ApplicationShutdownCoordinator
{
    private readonly object gate = new();
    private readonly IAsyncDisposable queue;
    private readonly Func<CancellationToken, Task> stopHostAsync;
    private readonly IDisposable host;
    private Task? queueDisposal;
    private Task? finalization;

    public ApplicationShutdownCoordinator(
        IAsyncDisposable queue,
        Func<CancellationToken, Task> stopHostAsync,
        IDisposable host)
    {
        this.queue = queue ?? throw new ArgumentNullException(nameof(queue));
        this.stopHostAsync = stopHostAsync ?? throw new ArgumentNullException(nameof(stopHostAsync));
        this.host = host ?? throw new ArgumentNullException(nameof(host));
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

        Task finalize;
        lock (gate)
            finalize = finalization ??= FinalizeHostAsync();
        await finalize.WaitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task DisposeQueueAsync() => await queue.DisposeAsync().ConfigureAwait(false);

    private async Task FinalizeHostAsync()
    {
        try { await stopHostAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception)
        {
            // Stopping is best-effort during shutdown; disposal still releases the host.
        }
        finally { host.Dispose(); }
    }
}
