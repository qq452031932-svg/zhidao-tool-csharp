using System.IO;

namespace BaiduBatchTransfer.App.Updates;

public sealed class VelopackUpdateRuntime : IApplicationUpdateRuntime
{
    private readonly object sync = new();
    private readonly IApplicationUpdateClient client;
    private readonly SignedFeedPackageGate? packageGate;
    private readonly Func<bool>? startSignedUpdateGuard;
    private VelopackClientAsset? availableUpdate;
    private VelopackClientAsset? preparedUpdate;
    private ApplicationUpdateSnapshot snapshot;
    private long operationGeneration;
    private int disposed;

    internal VelopackUpdateRuntime(
        IApplicationUpdateClient client,
        string fallbackCurrentVersion,
        SignedFeedPackageGate? packageGate = null,
        Func<bool>? startSignedUpdateGuard = null)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.packageGate = packageGate;
        this.startSignedUpdateGuard = startSignedUpdateGuard;
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackCurrentVersion);

        var currentVersion = client.CurrentVersion ?? fallbackCurrentVersion;
        var pending = ReadPendingSafely();
        if (pending is not null && packageGate is not null && !packageGate.TryRevalidatePending(pending))
            pending = null;
        snapshot = pending is null
            ? NewSnapshot(
                ApplicationUpdateStatus.Idle,
                currentVersion,
                userMessage: ApplicationUpdateMessages.Idle)
            : NewSnapshot(
                ApplicationUpdateStatus.ReadyToRestart,
                currentVersion,
                pending.Version,
                pending.ReleaseNotes,
                100,
                ApplicationUpdateMessages.ReadyToRestart);
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
    {
        ArgumentNullException.ThrowIfNull(buildInfo);
        if (!buildInfo.IsFeedConfigured)
            return UnavailableApplicationUpdateRuntime.NotConfigured(buildInfo.ProductVersion);

        var client = VelopackUpdateClient.Create(buildInfo);
        var currentVersion = client.CurrentVersion ?? buildInfo.ProductVersion;
        if (!client.IsInstalled)
        {
            client.Dispose();
            return UnavailableApplicationUpdateRuntime.Unsupported(currentVersion);
        }

        SignedFeedPackageGate? gate = null;
        if (buildInfo.HttpsFeedUrl is { } feedIdentity)
        {
            var packagesRoot = Velopack.Locators.VelopackLocator.Current.PackagesDir;
            if (string.IsNullOrWhiteSpace(packagesRoot))
            {
                client.Dispose();
                return UnavailableApplicationUpdateRuntime.Unsupported(currentVersion);
            }
            gate = new SignedFeedPackageGate(
                buildInfo.SignedPendingReceiptPath,
                new SignedFeedStateStore(
                    buildInfo.SignedFeedStatePath,
                    feedIdentity,
                    currentVersion),
                packagesRoot);
        }

        return new VelopackUpdateRuntime(
            client,
            buildInfo.ProductVersion,
            gate,
            gate is null ? null : SignedUpdateGuard.Start);
    }

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
        cancellationToken.ThrowIfCancellationRequested();
        var stable = Snapshot;
        if (stable.Status == ApplicationUpdateStatus.ReadyToRestart)
            return stable;

        var generation = BeginOperation(stable with
        {
            Status = ApplicationUpdateStatus.Checking,
            DownloadProgress = 0,
            UserMessage = ApplicationUpdateMessages.Checking
        });
        using var cancellationRegistration = RegisterCancellation(
            cancellationToken,
            generation,
            ApplicationUpdateStatus.Checking,
            stable);

        try
        {
            var update = await client.CheckForUpdatesAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                if (operationGeneration == generation)
                    availableUpdate = update;
            }

            var result = update is null
                ? stable with
                {
                    Status = ApplicationUpdateStatus.UpToDate,
                    LatestVersion = null,
                    ReleaseNotes = null,
                    DownloadProgress = 0,
                    UserMessage = ApplicationUpdateMessages.UpToDate
                }
                : stable with
                {
                    Status = ApplicationUpdateStatus.Available,
                    LatestVersion = update.Version,
                    ReleaseNotes = update.ReleaseNotes,
                    DownloadProgress = 0,
                    UserMessage = ApplicationUpdateMessages.Available
                };
            return CompleteOperation(generation, result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            RestoreStable(generation, ApplicationUpdateStatus.Checking, stable);
            throw;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            RestoreStable(generation, ApplicationUpdateStatus.Checking, stable);
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception exception)
        {
            return CompleteOperation(
                generation,
                ApplicationUpdateFailureClassifier.Classify(exception, Snapshot));
        }
    }

    public async Task<ApplicationUpdateSnapshot> DownloadAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        VelopackClientAsset? update;
        ApplicationUpdateSnapshot stable;
        lock (sync)
        {
            stable = snapshot;
            update = snapshot.Status == ApplicationUpdateStatus.Available
                ? availableUpdate
                : null;
        }

        if (update is null)
        {
            return Publish(stable with
            {
                Status = ApplicationUpdateStatus.Failed,
                UserMessage = ApplicationUpdateMessages.NothingToDownload
            });
        }

        var generation = BeginOperation(stable with
        {
            Status = ApplicationUpdateStatus.Downloading,
            DownloadProgress = 0,
            UserMessage = ApplicationUpdateMessages.Downloading
        });
        using var cancellationRegistration = RegisterCancellation(
            cancellationToken,
            generation,
            ApplicationUpdateStatus.Downloading,
            stable);

        try
        {
            await client.DownloadUpdatesAsync(
                    update,
                    progress => PublishDownloadProgress(generation, progress),
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var pending = client.UpdatePendingRestart;
            if (pending is null || !FingerprintsMatch(pending.Fingerprint, update.Fingerprint))
            {
                return CompleteOperation(generation, stable with
                {
                    Status = ApplicationUpdateStatus.VerificationFailed,
                    DownloadProgress = 0,
                    UserMessage = ApplicationUpdateMessages.VerificationFailed
                });
            }

            if (packageGate is not null &&
                (update.SignedFeedSnapshot is null ||
                 !await packageGate.AuthorizeDownloadedAsync(
                         update.SignedFeedSnapshot,
                         pending,
                         cancellationToken)
                     .ConfigureAwait(false)))
            {
                return CompleteOperation(generation, stable with
                {
                    Status = ApplicationUpdateStatus.VerificationFailed,
                    DownloadProgress = 0,
                    UserMessage = ApplicationUpdateMessages.VerificationFailed
                });
            }
            cancellationToken.ThrowIfCancellationRequested();

            return CompleteOperation(generation, stable with
            {
                Status = ApplicationUpdateStatus.ReadyToRestart,
                LatestVersion = pending.Version,
                ReleaseNotes = pending.ReleaseNotes ?? stable.ReleaseNotes,
                DownloadProgress = 100,
                UserMessage = ApplicationUpdateMessages.ReadyToRestart
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            RestoreStable(generation, ApplicationUpdateStatus.Downloading, stable);
            throw;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            RestoreStable(generation, ApplicationUpdateStatus.Downloading, stable);
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception exception)
        {
            return CompleteOperation(
                generation,
                ApplicationUpdateFailureClassifier.Classify(exception, Snapshot));
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

        if (packageGate is not null && !packageGate.TryRevalidatePending(pending))
            return false;

        lock (sync)
        {
            if (availableUpdate is not null &&
                !FingerprintsMatch(availableUpdate.Fingerprint, pending.Fingerprint))
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
            UserMessage = ApplicationUpdateMessages.ReadyToRestart
        });
        return true;
    }

    public bool LaunchPreparedUpdateAndRestart()
    {
        ThrowIfDisposed();
        VelopackClientAsset? prepared;
        lock (sync)
        {
            prepared = preparedUpdate;
            preparedUpdate = null;
        }

        if (prepared is null)
            return false;

        using var authorization = packageGate?.TryAcquireLaunchAuthorization(prepared);
        if (packageGate is not null && authorization is null)
        {
            throw new ApplicationUpdateVerificationException(
                new InvalidDataException("The pending signed update failed final verification."));
        }

        if (packageGate is not null)
        {
            if (startSignedUpdateGuard is null || !startSignedUpdateGuard())
            {
                throw new ApplicationUpdateVerificationException(
                    new InvalidOperationException("The signed update guard could not be started."));
            }

            return true;
        }

        client.WaitExitThenApplyUpdates(prepared);
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lock (sync)
        {
            operationGeneration++;
            preparedUpdate = null;
        }

        client.Dispose();
    }

    private long BeginOperation(ApplicationUpdateSnapshot transient)
    {
        long generation;
        lock (sync)
        {
            generation = ++operationGeneration;
            snapshot = transient;
        }

        RaiseSnapshotChanged(transient);
        return generation;
    }

    private ApplicationUpdateSnapshot CompleteOperation(
        long generation,
        ApplicationUpdateSnapshot completed)
    {
        lock (sync)
        {
            if (disposed != 0 || operationGeneration != generation)
                return snapshot;
            operationGeneration++;
            snapshot = completed;
        }

        RaiseSnapshotChanged(completed);
        return completed;
    }

    private bool RestoreStable(
        long generation,
        ApplicationUpdateStatus transientStatus,
        ApplicationUpdateSnapshot stable)
    {
        lock (sync)
        {
            if (disposed != 0 ||
                operationGeneration != generation ||
                snapshot.Status != transientStatus)
            {
                return false;
            }

            operationGeneration++;
            snapshot = stable;
        }

        RaiseSnapshotChanged(stable);
        return true;
    }

    private CancellationTokenRegistration RegisterCancellation(
        CancellationToken cancellationToken,
        long generation,
        ApplicationUpdateStatus transientStatus,
        ApplicationUpdateSnapshot stable) => cancellationToken.Register(
            static state =>
            {
                var restore = (CancellationRestore)state!;
                var restored = restore.Runtime.RestoreStable(
                    restore.Generation,
                    restore.TransientStatus,
                    restore.Stable);
                if (restored && restore.TransientStatus == ApplicationUpdateStatus.Downloading)
                    restore.Runtime.packageGate?.InvalidateReceipt();
            },
            new CancellationRestore(this, generation, transientStatus, stable));

    private void PublishDownloadProgress(long generation, int progress)
    {
        ApplicationUpdateSnapshot updated;
        lock (sync)
        {
            if (disposed != 0 ||
                operationGeneration != generation ||
                snapshot.Status != ApplicationUpdateStatus.Downloading)
            {
                return;
            }

            updated = snapshot with { DownloadProgress = Math.Clamp(progress, 0, 100) };
            snapshot = updated;
        }

        RaiseSnapshotChanged(updated);
    }

    private ApplicationUpdateSnapshot Publish(ApplicationUpdateSnapshot value)
    {
        lock (sync) snapshot = value;
        RaiseSnapshotChanged(value);
        return value;
    }

    private void RaiseSnapshotChanged(ApplicationUpdateSnapshot value)
    {
        var handlers = SnapshotChanged;
        if (handlers is null)
            return;

        foreach (EventHandler<ApplicationUpdateSnapshot> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, value);
            }
            catch
            {
                // An observer cannot alter update state or fail the operation.
            }
        }
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

    private static bool FingerprintsMatch(
        VelopackAssetFingerprint left,
        VelopackAssetFingerprint right) =>
        string.Equals(left.PackageId, right.PackageId, StringComparison.Ordinal) &&
        string.Equals(left.Version, right.Version, StringComparison.Ordinal) &&
        string.Equals(left.AssetType, right.AssetType, StringComparison.Ordinal) &&
        string.Equals(left.FileName, right.FileName, StringComparison.Ordinal) &&
        left.Size == right.Size &&
        (string.IsNullOrEmpty(left.Sha256) ||
         string.IsNullOrEmpty(right.Sha256) ||
         string.Equals(left.Sha256, right.Sha256, StringComparison.OrdinalIgnoreCase));

    private static ApplicationUpdateSnapshot NewSnapshot(
        ApplicationUpdateStatus status,
        string currentVersion,
        string? latestVersion = null,
        string? releaseNotes = null,
        int downloadProgress = 0,
        string userMessage = "") =>
        new(status, currentVersion, latestVersion, releaseNotes, downloadProgress, userMessage);

    private sealed record CancellationRestore(
        VelopackUpdateRuntime Runtime,
        long Generation,
        ApplicationUpdateStatus TransientStatus,
        ApplicationUpdateSnapshot Stable);
}
