using System.IO;
using Velopack;
using Velopack.Exceptions;
using Velopack.Sources;

namespace BaiduBatchTransfer.App.Updates;

internal interface IApplicationUpdateClient : IDisposable
{
    bool IsInstalled { get; }

    string? CurrentVersion { get; }

    VelopackClientAsset? UpdatePendingRestart { get; }

    Task<VelopackClientAsset?> CheckForUpdatesAsync(CancellationToken cancellationToken);

    Task DownloadUpdatesAsync(
        VelopackClientAsset update,
        Action<int> progress,
        CancellationToken cancellationToken);

    void WaitExitThenApplyUpdates(VelopackClientAsset update);
}

internal sealed record VelopackClientAsset(
    string Version,
    string? ReleaseNotes,
    object NativeToken);

internal sealed class VelopackUpdateClient : IApplicationUpdateClient
{
    private readonly UpdateManager manager;

    private VelopackUpdateClient(UpdateManager manager)
    {
        this.manager = manager;
    }

    public bool IsInstalled => manager.IsInstalled;

    public string? CurrentVersion => manager.CurrentVersion?.ToString();

    public VelopackClientAsset? UpdatePendingRestart =>
        manager.UpdatePendingRestart is { } pending ? FromAsset(pending) : null;

    public static IApplicationUpdateClient Create(ApplicationBuildInfo buildInfo)
    {
        ArgumentNullException.ThrowIfNull(buildInfo);

        IUpdateSource source = buildInfo.HttpsFeedUrl is { } httpsFeed
            ? new SimpleWebSource(new Uri(httpsFeed, UriKind.Absolute))
            : new SimpleFileSource(new DirectoryInfo(buildInfo.AcceptanceFeedPath!));
        var options = new UpdateOptions { ExplicitChannel = buildInfo.Channel };
        return new VelopackUpdateClient(new UpdateManager(source, options));
    }

    public async Task<VelopackClientAsset?> CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var update = await manager.CheckForUpdatesAsync()
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return update is null
                ? null
                : new VelopackClientAsset(
                    update.TargetFullRelease.Version.ToString(),
                    update.TargetFullRelease.NotesMarkdown,
                    update);
        }
        catch (AcquireLockFailedException exception)
        {
            throw new ApplicationUpdateBusyException(exception);
        }
        catch (ChecksumFailedException exception)
        {
            throw new ApplicationUpdateVerificationException(exception);
        }
        catch (NotInstalledException exception)
        {
            throw new ApplicationUpdateUnsupportedException(exception);
        }
    }

    public async Task DownloadUpdatesAsync(
        VelopackClientAsset update,
        Action<int> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(progress);

        if (update.NativeToken is not UpdateInfo updateInfo)
            throw new ArgumentException("The update token is invalid.", nameof(update));

        try
        {
            await manager.DownloadUpdatesAsync(updateInfo, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AcquireLockFailedException exception)
        {
            throw new ApplicationUpdateBusyException(exception);
        }
        catch (ChecksumFailedException exception)
        {
            throw new ApplicationUpdateVerificationException(exception);
        }
        catch (NotInstalledException exception)
        {
            throw new ApplicationUpdateUnsupportedException(exception);
        }
    }

    public void WaitExitThenApplyUpdates(VelopackClientAsset update)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (update.NativeToken is not VelopackAsset asset)
            throw new ArgumentException("The pending update token is invalid.", nameof(update));

        manager.WaitExitThenApplyUpdates(
            asset,
            silent: false,
            restart: true,
            restartArgs: Array.Empty<string>());
    }

    public void Dispose()
    {
    }

    private static VelopackClientAsset FromAsset(VelopackAsset asset) =>
        new(asset.Version.ToString(), asset.NotesMarkdown, asset);
}
