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

internal enum VelopackUpdateSourceKind
{
    Https,
    LocalDirectory
}

internal sealed record VelopackUpdateSourceConfiguration(
    VelopackUpdateSourceKind Kind,
    string Location,
    string Channel);

internal sealed record VelopackAssetFingerprint(
    string PackageId,
    string Version,
    string AssetType,
    string FileName,
    long Size,
    string Sha256);

internal sealed record VelopackClientAsset(
    VelopackAssetFingerprint Fingerprint,
    string? ReleaseNotes,
    object NativeToken)
{
    public string Version => Fingerprint.Version;
}

internal interface IVelopackUpdateManagerAdapter
{
    bool IsInstalled { get; }

    string? CurrentVersion { get; }

    VelopackClientAsset? UpdatePendingRestart { get; }

    Task<VelopackClientAsset?> CheckForUpdatesAsync();

    Task DownloadUpdatesAsync(
        VelopackClientAsset update,
        Action<int> progress,
        CancellationToken cancellationToken);

    void WaitExitThenApplyUpdates(
        VelopackClientAsset update,
        bool silent,
        bool restart,
        string[] restartArguments);
}

internal sealed class VelopackUpdateClient : IApplicationUpdateClient
{
    private readonly IVelopackUpdateManagerAdapter manager;

    internal VelopackUpdateClient(IVelopackUpdateManagerAdapter manager)
    {
        this.manager = manager ?? throw new ArgumentNullException(nameof(manager));
    }

    public bool IsInstalled => manager.IsInstalled;

    public string? CurrentVersion => manager.CurrentVersion;

    public VelopackClientAsset? UpdatePendingRestart => manager.UpdatePendingRestart;

    public static IApplicationUpdateClient Create(ApplicationBuildInfo buildInfo) =>
        Create(buildInfo, CreateManager);

    internal static IApplicationUpdateClient Create(
        ApplicationBuildInfo buildInfo,
        Func<VelopackUpdateSourceConfiguration, IVelopackUpdateManagerAdapter> managerFactory)
    {
        ArgumentNullException.ThrowIfNull(buildInfo);
        ArgumentNullException.ThrowIfNull(managerFactory);

        var configuration = buildInfo.HttpsFeedUrl is { } httpsFeed
            ? new VelopackUpdateSourceConfiguration(
                VelopackUpdateSourceKind.Https,
                httpsFeed,
                buildInfo.Channel)
            : new VelopackUpdateSourceConfiguration(
                VelopackUpdateSourceKind.LocalDirectory,
                buildInfo.AcceptanceFeedPath!,
                buildInfo.Channel);
        return new VelopackUpdateClient(managerFactory(configuration));
    }

    public async Task<VelopackClientAsset?> CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var managerCheck = manager.CheckForUpdatesAsync();
            try
            {
                return await managerCheck.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await managerCheck.ConfigureAwait(false);
                }
                catch
                {
                    // Velopack 1.2 has no native check cancellation; observe true completion.
                }

                throw;
            }
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

        try
        {
            await manager.DownloadUpdatesAsync(update, progress, cancellationToken)
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
        manager.WaitExitThenApplyUpdates(
            update,
            silent: false,
            restart: true,
            restartArguments: Array.Empty<string>());
    }

    public void Dispose()
    {
    }

    private static IVelopackUpdateManagerAdapter CreateManager(
        VelopackUpdateSourceConfiguration configuration)
    {
        IUpdateSource source = configuration.Kind == VelopackUpdateSourceKind.Https
            ? new SimpleWebSource(new Uri(configuration.Location, UriKind.Absolute))
            : new SimpleFileSource(new DirectoryInfo(configuration.Location));
        var options = CreateUpdateOptions(configuration);
        return new VelopackUpdateManagerAdapter(new UpdateManager(source, options));
    }

    private static UpdateOptions CreateUpdateOptions(
        VelopackUpdateSourceConfiguration configuration) => new()
        {
            ExplicitChannel = configuration.Channel,
            // Velopack 1.2 delta reconstruction can produce a byte-different full nupkg.
            // Download the signed/checksummed full asset so the pending fingerprint remains exact.
            MaximumDeltasBeforeFallback = -1
        };

    private sealed class VelopackUpdateManagerAdapter : IVelopackUpdateManagerAdapter
    {
        private readonly UpdateManager manager;

        public VelopackUpdateManagerAdapter(UpdateManager manager)
        {
            this.manager = manager;
        }

        public bool IsInstalled => manager.IsInstalled;

        public string? CurrentVersion => manager.CurrentVersion?.ToString();

        public VelopackClientAsset? UpdatePendingRestart =>
            manager.UpdatePendingRestart is { } pending ? FromAsset(pending) : null;

        public async Task<VelopackClientAsset?> CheckForUpdatesAsync()
        {
            var update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            return update is null
                ? null
                : new VelopackClientAsset(
                    Fingerprint(update.TargetFullRelease),
                    update.TargetFullRelease.NotesMarkdown,
                    update);
        }

        public Task DownloadUpdatesAsync(
            VelopackClientAsset update,
            Action<int> progress,
            CancellationToken cancellationToken)
        {
            if (update.NativeToken is not UpdateInfo updateInfo)
                throw new ArgumentException("The update token is invalid.", nameof(update));

            return manager.DownloadUpdatesAsync(updateInfo, progress, cancellationToken);
        }

        public void WaitExitThenApplyUpdates(
            VelopackClientAsset update,
            bool silent,
            bool restart,
            string[] restartArguments)
        {
            if (update.NativeToken is not VelopackAsset asset)
                throw new ArgumentException("The pending update token is invalid.", nameof(update));

            manager.WaitExitThenApplyUpdates(asset, silent, restart, restartArguments);
        }

        private static VelopackClientAsset FromAsset(VelopackAsset asset) =>
            new(Fingerprint(asset), asset.NotesMarkdown, asset);

        private static VelopackAssetFingerprint Fingerprint(VelopackAsset asset) =>
            new(
                asset.PackageId ?? string.Empty,
                asset.Version.ToString(),
                asset.Type.ToString(),
                asset.FileName ?? string.Empty,
                asset.Size,
                asset.SHA256 ?? string.Empty);
    }
}
