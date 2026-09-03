using System.Net.Http;

namespace BaiduBatchTransfer.App.Updates;

public sealed class VelopackUpdateRuntime : IApplicationUpdateRuntime
{
    private readonly object sync = new();
    private readonly IApplicationUpdateClient client;
    private VelopackClientAsset? availableUpdate;
    private VelopackClientAsset? preparedUpdate;
    private ApplicationUpdateSnapshot snapshot;
    private int disposed;

    internal VelopackUpdateRuntime(
        IApplicationUpdateClient client,
        string fallbackCurrentVersion)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackCurrentVersion);

        var currentVersion = client.CurrentVersion ?? fallbackCurrentVersion;
        var pending = ReadPendingSafely();
        snapshot = pending is null
            ? NewSnapshot(ApplicationUpdateStatus.Idle, currentVersion, userMessage: "尚未检查更新。")
            : NewSnapshot(
                ApplicationUpdateStatus.ReadyToRestart,
                currentVersion,
                pending.Version,
                pending.ReleaseNotes,
                100,
                "更新已下载，重启后完成安装。");
    }

    public ApplicationUpdateSnapshot Snapshot
    {
        get
        {
            lock (sync) return snapshot;
        }
    }

    public event EventHandler<ApplicationUpdateSnapshot>? SnapshotChanged;

    public static IApplicationUpdateRuntime Create(ApplicationBuildInfo buildInfo)
        => Create(buildInfo, VelopackUpdateClient.Create);

    internal static IApplicationUpdateRuntime Create(
        ApplicationBuildInfo buildInfo,
        Func<ApplicationBuildInfo, IApplicationUpdateClient> clientFactory)
    {
        ArgumentNullException.ThrowIfNull(buildInfo);
        ArgumentNullException.ThrowIfNull(clientFactory);

        if (!buildInfo.IsFeedConfigured)
            return UnavailableApplicationUpdateRuntime.NotConfigured(buildInfo.ProductVersion);

        var client = clientFactory(buildInfo);
        var currentVersion = client.CurrentVersion ?? buildInfo.ProductVersion;
        if (client.IsInstalled)
            return new VelopackUpdateRuntime(client, buildInfo.ProductVersion);

        client.Dispose();
        return UnavailableApplicationUpdateRuntime.Unsupported(currentVersion);
    }

    public async Task<ApplicationUpdateSnapshot> CheckAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var current = Snapshot;
        if (current.Status == ApplicationUpdateStatus.ReadyToRestart)
            return current;

        Publish(Snapshot with
        {
            Status = ApplicationUpdateStatus.Checking,
            DownloadProgress = 0,
            UserMessage = "正在检查更新…"
        });

        try
        {
            var update = await client.CheckForUpdatesAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                availableUpdate = update;
            }

            return update is null
                ? Publish(Snapshot with
                {
                    Status = ApplicationUpdateStatus.UpToDate,
                    LatestVersion = null,
                    ReleaseNotes = null,
                    DownloadProgress = 0,
                    UserMessage = "已是最新版本。"
                })
                : Publish(Snapshot with
                {
                    Status = ApplicationUpdateStatus.Available,
                    LatestVersion = update.Version,
                    ReleaseNotes = update.ReleaseNotes,
                    DownloadProgress = 0,
                    UserMessage = "发现新版本。"
                });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Publish(MapFailure(exception, silentNetworkFailure: true));
        }
    }

    public async Task<ApplicationUpdateSnapshot> DownloadAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        VelopackClientAsset? update;
        lock (sync)
        {
            update = snapshot.Status == ApplicationUpdateStatus.Available
                ? availableUpdate
                : null;
        }

        if (update is null)
        {
            return Publish(Snapshot with
            {
                Status = ApplicationUpdateStatus.Failed,
                UserMessage = "当前没有可下载的更新。"
            });
        }

        Publish(Snapshot with
        {
            Status = ApplicationUpdateStatus.Downloading,
            DownloadProgress = 0,
            UserMessage = "正在下载更新…"
        });

        try
        {
            await client.DownloadUpdatesAsync(
                    update,
                    progress => PublishDownloadProgress(progress),
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var pending = client.UpdatePendingRestart;
            if (pending is null ||
                !string.Equals(pending.Version, update.Version, StringComparison.Ordinal))
            {
                return Publish(Snapshot with
                {
                    Status = ApplicationUpdateStatus.VerificationFailed,
                    DownloadProgress = 0,
                    UserMessage = "更新文件校验失败，请稍后重试。"
                });
            }

            return Publish(Snapshot with
            {
                Status = ApplicationUpdateStatus.ReadyToRestart,
                LatestVersion = pending.Version,
                ReleaseNotes = pending.ReleaseNotes ?? Snapshot.ReleaseNotes,
                DownloadProgress = 100,
                UserMessage = "更新已下载，重启后完成安装。"
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Publish(MapFailure(exception, silentNetworkFailure: false));
        }
    }

    public bool TryPrepareRestart()
    {
        ThrowIfDisposed();
        VelopackClientAsset? pending;
        try
        {
            pending = client.UpdatePendingRestart;
        }
        catch
        {
            return false;
        }

        if (pending is null)
            return false;

        lock (sync)
        {
            if (availableUpdate is not null &&
                !string.Equals(availableUpdate.Version, pending.Version, StringComparison.Ordinal))
            {
                return false;
            }

            preparedUpdate = pending;
        }

        Publish(Snapshot with
        {
            Status = ApplicationUpdateStatus.ReadyToRestart,
            LatestVersion = pending.Version,
            ReleaseNotes = pending.ReleaseNotes ?? Snapshot.ReleaseNotes,
            DownloadProgress = 100,
            UserMessage = "更新已下载，重启后完成安装。"
        });
        return true;
    }

    public bool LaunchPreparedUpdateAndRestart()
    {
        ThrowIfDisposed();
        VelopackClientAsset? prepared;
        lock (sync) prepared = preparedUpdate;
        if (prepared is null)
            return false;

        client.WaitExitThenApplyUpdates(prepared);
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        client.Dispose();
    }

    private ApplicationUpdateSnapshot PublishDownloadProgress(int progress)
    {
        var current = Snapshot;
        if (current.Status != ApplicationUpdateStatus.Downloading)
            return current;

        return Publish(current with { DownloadProgress = Math.Clamp(progress, 0, 100) });
    }

    private ApplicationUpdateSnapshot Publish(ApplicationUpdateSnapshot value)
    {
        lock (sync) snapshot = value;
        SnapshotChanged?.Invoke(this, value);
        return value;
    }

    private ApplicationUpdateSnapshot MapFailure(Exception exception, bool silentNetworkFailure)
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
                UserMessage = silentNetworkFailure
                    ? string.Empty
                    : "暂时无法连接更新服务，请稍后重试。"
            },
            _ => current with
            {
                Status = ApplicationUpdateStatus.Failed,
                UserMessage = "更新失败，请稍后重试。"
            }
        };
    }

    private VelopackClientAsset? ReadPendingSafely()
    {
        try
        {
            return client.UpdatePendingRestart;
        }
        catch
        {
            return null;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
    }

    private static ApplicationUpdateSnapshot NewSnapshot(
        ApplicationUpdateStatus status,
        string currentVersion,
        string? latestVersion = null,
        string? releaseNotes = null,
        int downloadProgress = 0,
        string userMessage = "") =>
        new(status, currentVersion, latestVersion, releaseNotes, downloadProgress, userMessage);
}
