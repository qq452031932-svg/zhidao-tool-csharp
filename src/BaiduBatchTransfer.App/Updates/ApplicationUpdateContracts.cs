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
