using System.Net.Http;

namespace BaiduBatchTransfer.App.Updates;

public enum ApplicationUpdateStatus
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    ReadyToRestart,
    NotConfigured,
    Unsupported,
    Busy,
    NetworkUnavailable,
    VerificationFailed,
    Failed
}

public sealed record ApplicationUpdateSnapshot(
    ApplicationUpdateStatus Status,
    string CurrentVersion,
    string? LatestVersion,
    string? ReleaseNotes,
    int DownloadProgress,
    string UserMessage);

public interface IApplicationUpdateService
{
    ApplicationUpdateSnapshot Snapshot { get; }

    event EventHandler<ApplicationUpdateSnapshot>? SnapshotChanged;

    Task<ApplicationUpdateSnapshot> CheckAsync(bool manual, CancellationToken cancellationToken);

    Task<ApplicationUpdateSnapshot> DownloadAsync(CancellationToken cancellationToken);

    bool TryPrepareRestart();
}

public interface IApplicationUpdateRuntime : IDisposable
{
    ApplicationUpdateSnapshot Snapshot { get; }

    event EventHandler<ApplicationUpdateSnapshot>? SnapshotChanged;

    Task<ApplicationUpdateSnapshot> CheckAsync(CancellationToken cancellationToken);

    Task<ApplicationUpdateSnapshot> DownloadAsync(CancellationToken cancellationToken);

    bool TryPrepareRestart();

    bool LaunchPreparedUpdateAndRestart();
}

internal sealed class ApplicationUpdateBusyException : Exception
{
    public ApplicationUpdateBusyException(Exception innerException)
        : base("The update operation is already running.", innerException)
    {
    }
}

internal sealed class ApplicationUpdateVerificationException : Exception
{
    public ApplicationUpdateVerificationException(Exception innerException)
        : base("The downloaded update did not pass verification.", innerException)
    {
    }
}

internal sealed class ApplicationUpdateUnsupportedException : Exception
{
    public ApplicationUpdateUnsupportedException(Exception innerException)
        : base("The current process is not an installed application.", innerException)
    {
    }
}

internal static class ApplicationUpdateMessages
{
    public const string Idle = "尚未检查更新。";
    public const string Checking = "正在检查更新…";
    public const string UpToDate = "已是最新版本。";
    public const string Available = "发现新版本。";
    public const string Downloading = "正在下载更新…";
    public const string ReadyToRestart = "更新已下载，重启后完成安装。";
    public const string NotConfigured = "更新服务尚未配置。";
    public const string Unsupported = "当前运行方式不支持在线更新。";
    public const string Busy = "更新操作正在进行。";
    public const string NetworkUnavailable = "暂时无法连接更新服务，请稍后重试。";
    public const string VerificationFailed = "更新文件校验失败，请稍后重试。";
    public const string Failed = "更新失败，请稍后重试。";
    public const string NothingToDownload = "当前没有可下载的更新。";
}

internal static class ApplicationUpdateFailureClassifier
{
    public static ApplicationUpdateSnapshot Classify(
        Exception exception,
        ApplicationUpdateSnapshot current) => exception switch
        {
            ApplicationUpdateBusyException => current with
            {
                Status = ApplicationUpdateStatus.Busy,
                UserMessage = ApplicationUpdateMessages.Busy
            },
            ApplicationUpdateVerificationException => current with
            {
                Status = ApplicationUpdateStatus.VerificationFailed,
                UserMessage = ApplicationUpdateMessages.VerificationFailed
            },
            ApplicationUpdateUnsupportedException => current with
            {
                Status = ApplicationUpdateStatus.Unsupported,
                UserMessage = ApplicationUpdateMessages.Unsupported
            },
            HttpRequestException or TimeoutException => current with
            {
                Status = ApplicationUpdateStatus.NetworkUnavailable,
                UserMessage = string.Empty
            },
            _ => current with
            {
                Status = ApplicationUpdateStatus.Failed,
                UserMessage = ApplicationUpdateMessages.Failed
            }
        };
}
