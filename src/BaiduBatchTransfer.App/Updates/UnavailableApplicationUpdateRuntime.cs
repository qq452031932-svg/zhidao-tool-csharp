namespace BaiduBatchTransfer.App.Updates;

public sealed class UnavailableApplicationUpdateRuntime : IApplicationUpdateRuntime
{
    private readonly ApplicationUpdateSnapshot snapshot;
    private int disposed;

    public UnavailableApplicationUpdateRuntime()
        : this(ApplicationUpdateStatus.NotConfigured, ApplicationBuildInfo.Current.ProductVersion)
    {
    }

    private UnavailableApplicationUpdateRuntime(
        ApplicationUpdateStatus status,
        string currentVersion)
    {
        snapshot = new ApplicationUpdateSnapshot(
            status,
            currentVersion,
            null,
            null,
            0,
            status == ApplicationUpdateStatus.NotConfigured
                ? ApplicationUpdateMessages.NotConfigured
                : ApplicationUpdateMessages.Unsupported);
    }

    public ApplicationUpdateSnapshot Snapshot => snapshot;

    public event EventHandler<ApplicationUpdateSnapshot>? SnapshotChanged
    {
        add { }
        remove { }
    }

    public static UnavailableApplicationUpdateRuntime NotConfigured(string currentVersion) =>
        new(ApplicationUpdateStatus.NotConfigured, currentVersion);

    public static UnavailableApplicationUpdateRuntime Unsupported(string currentVersion) =>
        new(ApplicationUpdateStatus.Unsupported, currentVersion);

    public Task<ApplicationUpdateSnapshot> CheckAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(snapshot);
    }

    public Task<ApplicationUpdateSnapshot> DownloadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(snapshot);
    }

    public bool TryPrepareRestart() => false;

    public bool LaunchPreparedUpdateAndRestart() => false;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
    }
}
