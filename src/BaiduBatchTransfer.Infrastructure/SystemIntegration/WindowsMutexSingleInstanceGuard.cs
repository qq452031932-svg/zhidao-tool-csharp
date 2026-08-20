using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.Infrastructure.SystemIntegration;

public sealed class WindowsMutexSingleInstanceGuard : ISingleInstanceGuard, IDisposable
{
    private readonly string mutexName;
    private readonly object sync = new();
    private readonly ManualResetEventSlim release = new(false);
    private TaskCompletionSource<bool>? acquisition;
    private Thread? ownerThread;
    private bool disposed;

    public WindowsMutexSingleInstanceGuard(string mutexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutexName);
        this.mutexName = mutexName;
    }

    public Task<bool> TryAcquireAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (acquisition is not null) return acquisition.Task;

            acquisition = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ownerThread = new Thread(OwnMutex)
            {
                IsBackground = true,
                Name = "百度批量转存单实例保护",
            };
            ownerThread.Start();
            return acquisition.Task.WaitAsync(cancellationToken);
        }
    }

    private void OwnMutex()
    {
        using var mutex = new Mutex(false, mutexName);
        var ownsMutex = false;
        try
        {
            try { ownsMutex = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { ownsMutex = true; }
            acquisition!.TrySetResult(ownsMutex);
            if (ownsMutex) release.Wait();
        }
        catch (Exception error)
        {
            acquisition!.TrySetException(error);
        }
        finally
        {
            if (ownsMutex) mutex.ReleaseMutex();
        }
    }

    public void Dispose()
    {
        Thread? thread;
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            thread = ownerThread;
            release.Set();
        }
        if (thread is not null && thread != Thread.CurrentThread) thread.Join(TimeSpan.FromSeconds(5));
        release.Dispose();
    }
}
