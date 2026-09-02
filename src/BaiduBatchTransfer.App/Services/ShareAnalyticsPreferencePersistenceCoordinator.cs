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
    private long processedVersion;
    private long persistedVersion;
    private int signalPending;
    private bool disposeRequested;
    private bool sealedForShutdown;

    public ShareAnalyticsPreferencePersistenceCoordinator(
        Func<Guid?, CancellationToken, Task> save,
        Action? reportFailure = null)
    {
        this.save = save ?? throw new ArgumentNullException(nameof(save));
        this.reportFailure = reportFailure;
        worker = Task.Run(RunAsync);
    }

    public bool RequestSave(Guid? accountId)
    {
        lock (gate)
        {
            if (disposeRequested || sealedForShutdown) return false;
            pendingAccountId = accountId;
            requestedVersion++;
        }
        WakeWorker();
        return true;
    }

    public async Task<bool> FlushAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        long targetVersion;
        lock (gate) targetVersion = requestedVersion;
        return await WaitForTargetAsync(targetVersion, timeout, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> SealAndFlushAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        long targetVersion;
        lock (gate)
        {
            if (disposeRequested) return Task.FromResult(false);
            sealedForShutdown = true;
            targetVersion = requestedVersion;
        }
        return WaitForTargetAsync(targetVersion, timeout, cancellationToken);
    }

    public void ResumeAfterFailedShutdown()
    {
        var retryFailedVersion = false;
        lock (gate)
        {
            if (disposeRequested) return;
            sealedForShutdown = false;
            if (processedVersion >= requestedVersion && persistedVersion < requestedVersion)
            {
                requestedVersion++;
                retryFailedVersion = true;
            }
        }
        if (retryFailedVersion) WakeWorker();
    }

    private async Task<bool> WaitForTargetAsync(
        long targetVersion,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            Task<long> progressTask;
            lock (gate)
            {
                if (persistedVersion >= targetVersion) return true;
                if (processedVersion >= targetVersion) return false;
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
            sealedForShutdown = true;
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
                    if (processedVersion >= requestedVersion)
                    {
                        if (disposeRequested) return;
                        break;
                    }
                    accountId = pendingAccountId;
                    version = requestedVersion;
                }

                var persisted = false;
                try
                {
                    await save(accountId, lifetime.Token).ConfigureAwait(false);
                    persisted = true;
                }
                catch
                {
                    try { reportFailure?.Invoke(); }
                    catch { }
                }

                TaskCompletionSource<long> completedProgress;
                lock (gate)
                {
                    processedVersion = Math.Max(processedVersion, version);
                    if (persisted)
                        persistedVersion = Math.Max(persistedVersion, version);
                    completedProgress = progress;
                    progress = NewProgressSource();
                }
                completedProgress.TrySetResult(processedVersion);
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
