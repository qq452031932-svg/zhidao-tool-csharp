using System.Net.Http;

namespace BaiduBatchTransfer.App.Updates;

public sealed class ApplicationUpdateService : IApplicationUpdateService, IDisposable
{
    private readonly object sync = new();
    private readonly IApplicationUpdateRuntime runtime;
    private readonly CancellationTokenSource disposeCancellation = new();
    private ApplicationUpdateSnapshot snapshot;
    private Task<ApplicationUpdateSnapshot>? activeCheck;
    private int disposed;

    public ApplicationUpdateService(IApplicationUpdateRuntime runtime)
    {
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        snapshot = runtime.Snapshot;
        runtime.SnapshotChanged += OnRuntimeSnapshotChanged;
    }

    public ApplicationUpdateSnapshot Snapshot
    {
        get
        {
            lock (sync) return snapshot;
        }
    }

    public event EventHandler<ApplicationUpdateSnapshot>? SnapshotChanged;

    public async Task<ApplicationUpdateSnapshot> CheckAsync(
        bool manual,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        Task<ApplicationUpdateSnapshot> check;
        lock (sync)
        {
            if (activeCheck is null)
            {
                try
                {
                    activeCheck = runtime.CheckAsync(disposeCancellation.Token);
                }
                catch (Exception exception)
                {
                    activeCheck = Task.FromException<ApplicationUpdateSnapshot>(exception);
                }

                check = activeCheck;
                _ = activeCheck.ContinueWith(
                    (_, state) =>
                    {
                        var completion = (CheckCompletionState)state!;
                        lock (completion.Service.sync)
                        {
                            if (ReferenceEquals(completion.Service.activeCheck, completion.Check))
                                completion.Service.activeCheck = null;
                        }
                    },
                    new CheckCompletionState(this, activeCheck),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            else
            {
                check = activeCheck;
            }
        }

        ApplicationUpdateSnapshot result;
        try
        {
            result = await check.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (disposeCancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            result = MapFailure(exception);
        }

        if (result.Status == ApplicationUpdateStatus.NetworkUnavailable)
        {
            result = result with
            {
                UserMessage = manual
                    ? "暂时无法连接更新服务，请稍后重试。"
                    : string.Empty
            };
        }

        return Publish(result);
    }

    public async Task<ApplicationUpdateSnapshot> DownloadAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            disposeCancellation.Token);
        try
        {
            var result = await runtime.DownloadAsync(linkedCancellation.Token).ConfigureAwait(false);
            return Publish(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (disposeCancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Publish(MapFailure(exception));
        }
    }

    public bool TryPrepareRestart()
    {
        ThrowIfDisposed();
        return runtime.TryPrepareRestart();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        runtime.SnapshotChanged -= OnRuntimeSnapshotChanged;
        disposeCancellation.Cancel();
        disposeCancellation.Dispose();
    }

    private void OnRuntimeSnapshotChanged(object? sender, ApplicationUpdateSnapshot value) =>
        Publish(value);

    private ApplicationUpdateSnapshot Publish(ApplicationUpdateSnapshot value)
    {
        lock (sync) snapshot = value;
        SnapshotChanged?.Invoke(this, value);
        return value;
    }

    private ApplicationUpdateSnapshot MapFailure(Exception exception)
    {
        var current = Snapshot;
        return exception switch
        {
            ApplicationUpdateBusyException => current with
            {
                Status = ApplicationUpdateStatus.Busy,
                UserMessage = "更新操作正在进行。"
            },
            ApplicationUpdateVerificationException => current with
            {
                Status = ApplicationUpdateStatus.VerificationFailed,
                UserMessage = "更新文件校验失败，请稍后重试。"
            },
            ApplicationUpdateUnsupportedException => current with
            {
                Status = ApplicationUpdateStatus.Unsupported,
                UserMessage = "当前运行方式不支持在线更新。"
            },
            HttpRequestException or TimeoutException => current with
            {
                Status = ApplicationUpdateStatus.NetworkUnavailable,
                UserMessage = string.Empty
            },
            _ => current with
            {
                Status = ApplicationUpdateStatus.Failed,
                UserMessage = "更新失败，请稍后重试。"
            }
        };
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
    }

    private sealed record CheckCompletionState(
        ApplicationUpdateService Service,
        Task<ApplicationUpdateSnapshot> Check);
}
