using System.IO;
using System.Security.Cryptography;
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
    string Channel,
    string CurrentVersion = "0.0.0",
    string? SignedFeedStatePath = null);

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

    internal string? LocalPackagePath { get; init; }

    internal SignedFeedVerificationSnapshot? SignedFeedSnapshot { get; init; }
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
                buildInfo.Channel,
                buildInfo.ProductVersion,
                buildInfo.SignedFeedStatePath)
            : new VelopackUpdateSourceConfiguration(
                VelopackUpdateSourceKind.LocalDirectory,
                buildInfo.AcceptanceFeedPath!,
                buildInfo.Channel,
                buildInfo.ProductVersion);
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
        catch (InvalidDataException exception)
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
        catch (InvalidDataException exception)
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
        if (manager is IDisposable disposable)
            disposable.Dispose();
    }

    private static IVelopackUpdateManagerAdapter CreateManager(
        VelopackUpdateSourceConfiguration configuration)
    {
        var source = CreateUpdateSource(configuration);
        var options = CreateUpdateOptions(configuration);
        return new VelopackUpdateManagerAdapter(new UpdateManager(source, options), source);
    }

    internal static IUpdateSource CreateUpdateSource(
        VelopackUpdateSourceConfiguration configuration)
    {
        if (configuration.Kind == VelopackUpdateSourceKind.LocalDirectory)
            return new SimpleFileSource(new DirectoryInfo(configuration.Location));
        if (string.IsNullOrWhiteSpace(configuration.SignedFeedStatePath))
            throw new InvalidOperationException("The signed feed replay state path is not configured.");
        var feedUri = new Uri(configuration.Location, UriKind.Absolute);
        var store = new SignedFeedStateStore(
            configuration.SignedFeedStatePath,
            feedUri.AbsoluteUri,
            configuration.CurrentVersion);
        return new SignedFeedUpdateSource(feedUri, store);
    }

    private static UpdateOptions CreateUpdateOptions(
        VelopackUpdateSourceConfiguration configuration) => new()
        {
            ExplicitChannel = configuration.Channel,
            // Velopack 1.2 delta reconstruction can produce a byte-different full nupkg.
            // Download the signed/checksummed full asset so the pending fingerprint remains exact.
            MaximumDeltasBeforeFallback = -1
        };

    private sealed class VelopackUpdateManagerAdapter : IVelopackUpdateManagerAdapter, IDisposable
    {
        private readonly UpdateManager manager;
        private readonly IUpdateSource source;
        private readonly SignedFeedUpdateSource? signedFeedSource;

        public VelopackUpdateManagerAdapter(UpdateManager manager, IUpdateSource source)
        {
            this.manager = manager;
            this.source = source;
            signedFeedSource = source as SignedFeedUpdateSource;
        }

        public bool IsInstalled => manager.IsInstalled;

        public string? CurrentVersion => manager.CurrentVersion?.ToString();

        public VelopackClientAsset? UpdatePendingRestart
        {
            get
            {
                if (manager.UpdatePendingRestart is not { } pending)
                    return null;

                var fingerprint = Fingerprint(pending);
                fingerprint = PreparePendingFingerprint(
                    fingerprint,
                    Velopack.Locators.VelopackLocator.Current.PackagesDir!,
                    signedFeedSource is not null);
                return new VelopackClientAsset(
                    fingerprint,
                    pending.NotesMarkdown,
                    pending)
                {
                    LocalPackagePath = ResolvePackagePath(fingerprint)
                };
            }
        }

        public async Task<VelopackClientAsset?> CheckForUpdatesAsync()
        {
            var update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            return update is null
                ? null
                : new VelopackClientAsset(
                    Fingerprint(update.TargetFullRelease),
                    update.TargetFullRelease.NotesMarkdown,
                    update)
                {
                    SignedFeedSnapshot = signedFeedSource?.LatestSnapshot
                };
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

        public void Dispose()
        {
            if (source is IDisposable disposable)
                disposable.Dispose();
        }

        private static string? ResolvePackagePath(VelopackAssetFingerprint fingerprint)
        {
            if (!string.Equals(Path.GetFileName(fingerprint.FileName), fingerprint.FileName, StringComparison.Ordinal))
                return null;

            var packagesDirectory = Velopack.Locators.VelopackLocator.Current.PackagesDir;
            if (string.IsNullOrWhiteSpace(packagesDirectory))
                return null;
            var root = Path.GetFullPath(packagesDirectory);
            var packagePath = Path.GetFullPath(Path.Combine(root, fingerprint.FileName));
            var requiredPrefix = root.EndsWith(Path.DirectorySeparatorChar)
                ? root
                : root + Path.DirectorySeparatorChar;
            return packagePath.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase)
                ? packagePath
                : null;
        }

        private static VelopackAssetFingerprint Fingerprint(VelopackAsset asset) =>
            new(
                asset.PackageId ?? string.Empty,
                asset.Version.ToString(),
                asset.Type.ToString(),
                asset.FileName ?? string.Empty,
                asset.Size,
                asset.SHA256 ?? string.Empty);
    }

    internal static VelopackAssetFingerprint CompletePendingFingerprint(
        VelopackAssetFingerprint fingerprint,
        string packagesDirectory)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(packagesDirectory);

        if (!string.Equals(
                Path.GetFileName(fingerprint.FileName),
                fingerprint.FileName,
                StringComparison.Ordinal))
        {
            return fingerprint;
        }

        try
        {
            var root = Path.GetFullPath(packagesDirectory);
            var packagePath = Path.GetFullPath(Path.Combine(root, fingerprint.FileName));
            var requiredPrefix = root.EndsWith(Path.DirectorySeparatorChar)
                ? root
                : root + Path.DirectorySeparatorChar;
            if (!packagePath.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase))
                return fingerprint;

            using var package = File.OpenRead(packagePath);
            return fingerprint with
            {
                Size = package.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(package))
            };
        }
        catch (IOException)
        {
            return fingerprint;
        }
        catch (UnauthorizedAccessException)
        {
            return fingerprint;
        }
    }

    internal static VelopackAssetFingerprint PreparePendingFingerprint(
        VelopackAssetFingerprint fingerprint,
        string packagesDirectory,
        bool signedFeed) => signedFeed
        ? fingerprint
        : CompletePendingFingerprint(fingerprint, packagesDirectory);
}
