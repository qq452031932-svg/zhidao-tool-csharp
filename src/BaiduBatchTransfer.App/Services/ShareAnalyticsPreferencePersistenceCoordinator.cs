using System.Diagnostics;

namespace BaiduBatchTransfer.App.Services;

public sealed class ShareAnalyticsPreferencePersistenceCoordinator : IDisposable
{
    private readonly object gate = new();
    private readonly Func<Guid?, CancellationToken, Task> save;
    private readonly Action? reportFailure;
    private readonly SemaphoreSlim signal = new(0, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task worker;
    private TaskCompletionSource<long> progress = NewProgressSource();
    private Guid? pendingAccountId;
    private long requestedVersion;
    private long completedVersion;
    private int signalPending;
    private bool disposeRequested;

    public ShareAnalyticsPreferencePersistenceCoordinator(
        Func<Guid?, CancellationToken, Task> save,
        Action? reportFailure = null)
    {
        this.save = save ?? throw new ArgumentNullException(nameof(save));
        this.reportFailure = reportFailure;
        worker = Task.Run(RunAsync);
    }

    public void RequestSave(Guid? accountId)
    {
        lock (gate)
        {
            if (disposeRequested) return;
            pendingAccountId = accountId;
            requestedVersion++;
        }
        WakeWorker();
    }

    public async Task<bool> FlushAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        var stopwatch = Stopwatch.StartNew();
        long targetVersion;
        lock (gate) targetVersion = requestedVersion;
        while (true)
        {
            Task<long> progressTask;
            lock (gate)
            {
                if (completedVersion >= targetVersion) return true;
                progressTask = progress.Task;
            }

            var remaining = timeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero) return false;
            try
            {
                await progressTask.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return false;
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposeRequested) return;
            disposeRequested = true;
        }
        WakeWorker();
        _ = CompleteDisposalAsync();
    }

    private async Task RunAsync()
    {
        while (true)
        {
            await signal.WaitAsync().ConfigureAwait(false);
            Volatile.Write(ref signalPending, 0);
            while (true)
            {
                Guid? accountId;
                long version;
                lock (gate)
                {
                    if (completedVersion >= requestedVersion)
                    {
                        if (disposeRequested) return;
                        break;
                    }
                    accountId = pendingAccountId;
                    version = requestedVersion;
                }

                try
                {
                    await save(accountId, lifetime.Token).ConfigureAwait(false);
                }
                catch
                {
                    try { reportFailure?.Invoke(); }
                    catch { }
                }

                TaskCompletionSource<long> completedProgress;
                lock (gate)
                {
                    completedVersion = Math.Max(completedVersion, version);
                    completedProgress = progress;
                    progress = NewProgressSource();
                }
                completedProgress.TrySetResult(completedVersion);
            }
        }
    }

    private async Task CompleteDisposalAsync()
    {
        try { await worker.ConfigureAwait(false); }
        catch { }
        lifetime.Cancel();
        lifetime.Dispose();
        signal.Dispose();
    }

    private void WakeWorker()
    {
        if (Interlocked.Exchange(ref signalPending, 1) == 0)
            signal.Release();
    }

    private static TaskCompletionSource<long> NewProgressSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
