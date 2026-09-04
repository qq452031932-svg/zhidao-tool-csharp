using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using BaiduBatchTransfer.App.Updates;
using Velopack;

namespace BaiduBatchTransfer.App.Tests.Updates;

public sealed class VelopackUpdateRuntimeTests
{
    [Fact]
    public void PendingAssetAdapterDoesNotCacheVerifiedPackageFingerprint()
    {
        var adapter = typeof(VelopackUpdateClient).GetNestedType(
            "VelopackUpdateManagerAdapter",
            BindingFlags.NonPublic);

        Assert.NotNull(adapter);
        Assert.DoesNotContain(
            adapter.GetFields(BindingFlags.NonPublic | BindingFlags.Instance),
            field => field.FieldType == typeof(VelopackClientAsset));
    }

    [Fact]
    public void PendingAssetFingerprintIsCompletedFromDownloadedFullPackage()
    {
        var directory = Directory.CreateTempSubdirectory("velopack-pending-");
        try
        {
            const string fileName = "SuperPan.BaiduBatchTransfer-2.0.0-full.nupkg";
            var content = new byte[] { 1, 2, 3, 4, 5 };
            File.WriteAllBytes(Path.Combine(directory.FullName, fileName), content);
            var incomplete = new VelopackAssetFingerprint(
                "SuperPan.BaiduBatchTransfer",
                "2.0.0",
                "Full",
                fileName,
                content.Length,
                string.Empty);

            var completed = VelopackUpdateClient.CompletePendingFingerprint(
                incomplete,
                directory.FullName);

            Assert.Equal(content.Length, completed.Size);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(content)), completed.Sha256);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void ClientFactoryDisablesDeltaReconstructionForExactFingerprintVerification()
    {
        var configuration = new VelopackUpdateSourceConfiguration(
            VelopackUpdateSourceKind.LocalDirectory,
            Path.GetFullPath(Path.Combine("test-data", "ota-feed")),
            "win-x64-ota-acceptance");
        var factory = typeof(VelopackUpdateClient).GetMethod(
            "CreateUpdateOptions",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(factory);
        var options = Assert.IsType<UpdateOptions>(factory.Invoke(null, [configuration]));
        Assert.Equal(-1, options.MaximumDeltasBeforeFallback);
    }

    [Fact]
    public void ClientFactorySelectsHttpsSourceAndConfiguredChannel()
    {
        VelopackUpdateSourceConfiguration? captured = null;
        var manager = new FakeManagerAdapter();

        using var client = VelopackUpdateClient.Create(
            BuildInfo("1.0.0"),
            configuration =>
            {
                captured = configuration;
                return manager;
            });

        Assert.NotNull(captured);
        Assert.Equal(VelopackUpdateSourceKind.Https, captured.Kind);
        Assert.Equal("https://updates.example.invalid/app/", captured.Location);
        Assert.Equal("win-x64-stable", captured.Channel);
    }

    [Fact]
    public void ClientFactorySelectsLocalDirectoryForAcceptanceFeed()
    {
        var feedPath = Path.GetFullPath(Path.Combine("test-data", "ota-feed"));
        var buildInfo = ApplicationBuildInfo.CreateForTests(
            "1.0.0",
            "adapter-test",
            httpsFeedUrl: null,
            channel: "win-x64-ota-acceptance",
            acceptanceFeedPath: feedPath,
            isOtaAcceptanceBuild: true);
        VelopackUpdateSourceConfiguration? captured = null;

        using var client = VelopackUpdateClient.Create(
            buildInfo,
            configuration =>
            {
                captured = configuration;
                return new FakeManagerAdapter();
            });

        Assert.NotNull(captured);
        Assert.Equal(VelopackUpdateSourceKind.LocalDirectory, captured.Kind);
        Assert.Equal(feedPath, captured.Location);
        Assert.Equal("win-x64-ota-acceptance", captured.Channel);
    }

    [Fact]
    public async Task ClientCancellationTracksNonCancellableManagerCheckToCompletion()
    {
        var managerResult = new TaskCompletionSource<VelopackClientAsset?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = new FakeManagerAdapter { CheckHandler = () => managerResult.Task };
        using var client = new VelopackUpdateClient(manager);
        using var cancellation = new CancellationTokenSource();

        var check = client.CheckForUpdatesAsync(cancellation.Token);
        cancellation.Cancel();

        Assert.False(check.IsCompleted);
        managerResult.SetResult(null);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
    }

    [Fact]
    public void ClientUsesOnlyGracefulWaitExitApplyArguments()
    {
        var manager = new FakeManagerAdapter();
        using var client = new VelopackUpdateClient(manager);
        var pending = Asset("2.0.0", null);

        client.WaitExitThenApplyUpdates(pending);

        Assert.Same(pending, manager.AppliedAsset);
        Assert.False(manager.ApplySilent);
        Assert.True(manager.ApplyRestart);
        Assert.Empty(manager.RestartArguments!);
    }

    [Fact]
    public void MissingFeedCreatesNotConfiguredRuntimeWithoutCreatingClient()
    {
        var clientsCreated = 0;
        var buildInfo = BuildInfo("1.0.0", httpsFeedUrl: null);

        using var runtime = VelopackUpdateRuntime.Create(
            buildInfo,
            _ =>
            {
                clientsCreated++;
                return new FakeClient();
            });

        Assert.Equal(ApplicationUpdateStatus.NotConfigured, runtime.Snapshot.Status);
        Assert.Equal(0, clientsCreated);
    }

    [Fact]
    public void NonInstalledClientCreatesUnsupportedRuntime()
    {
        var client = new FakeClient { IsInstalled = false };

        using var runtime = VelopackUpdateRuntime.Create(BuildInfo("1.0.0"), _ => client);

        Assert.Equal(ApplicationUpdateStatus.Unsupported, runtime.Snapshot.Status);
        Assert.Equal(0, client.CheckCalls);
    }

    [Fact]
    public void InstalledVersionTakesPriorityOverBuildVersion()
    {
        var client = new FakeClient { CurrentVersion = "2.3.4" };

        using var runtime = new VelopackUpdateRuntime(client, fallbackCurrentVersion: "1.0.0");

        Assert.Equal("2.3.4", runtime.Snapshot.CurrentVersion);
    }

    [Fact]
    public void DevelopmentRuntimeFallsBackToBuildVersion()
    {
        var client = new FakeClient { CurrentVersion = null };

        using var runtime = new VelopackUpdateRuntime(client, fallbackCurrentVersion: "1.4.0");

        Assert.Equal("1.4.0", runtime.Snapshot.CurrentVersion);
    }

    [Fact]
    public async Task CheckProjectsVersionAndReleaseNotes()
    {
        var client = new FakeClient
        {
            CheckResult = Asset("2.0.0", "修复与改进")
        };
        using var runtime = new VelopackUpdateRuntime(client, "1.0.0");

        var snapshot = await runtime.CheckAsync(CancellationToken.None);

        Assert.Equal(ApplicationUpdateStatus.Available, snapshot.Status);
        Assert.Equal("2.0.0", snapshot.LatestVersion);
        Assert.Equal("修复与改进", snapshot.ReleaseNotes);
    }

    [Fact]
    public async Task DownloadIsRejectedUnlessAnUpdateIsAvailable()
    {
        var client = new FakeClient();
        using var runtime = new VelopackUpdateRuntime(client, "1.0.0");

        var snapshot = await runtime.DownloadAsync(CancellationToken.None);

        Assert.Equal(ApplicationUpdateStatus.Failed, snapshot.Status);
        Assert.Equal(0, client.DownloadCalls);
    }

    [Fact]
    public async Task DownloadProgressIsClampedToZeroThroughOneHundred()
    {
        var target = Asset("2.0.0", null);
        var pending = Asset("2.0.0", null);
        var observed = new List<int>();
        VelopackClientAsset? clientPending = null;
        var client = new FakeClient
        {
            CheckResult = target,
            DownloadHandler = (_, progress, _) =>
            {
                progress(-12);
                progress(45);
                progress(140);
                clientPending = pending;
                return Task.CompletedTask;
            }
        };
        client.PendingAccessor = () => clientPending;
        using var runtime = new VelopackUpdateRuntime(client, "1.0.0");
        runtime.SnapshotChanged += (_, snapshot) =>
        {
            if (snapshot.Status == ApplicationUpdateStatus.Downloading)
                observed.Add(snapshot.DownloadProgress);
        };
        await runtime.CheckAsync(CancellationToken.None);

        var snapshot = await runtime.DownloadAsync(CancellationToken.None);

        Assert.Equal(new[] { 0, 0, 45, 100 }, observed);
        Assert.Equal(ApplicationUpdateStatus.ReadyToRestart, snapshot.Status);
        Assert.Equal(100, snapshot.DownloadProgress);
    }

    [Fact]
    public async Task DownloadMustProduceMatchingPendingAssetBeforeRestartCanBePrepared()
    {
        var client = new FakeClient { CheckResult = Asset("2.0.0", null) };
        using var runtime = new VelopackUpdateRuntime(client, "1.0.0");
        await runtime.CheckAsync(CancellationToken.None);

        var snapshot = await runtime.DownloadAsync(CancellationToken.None);

        Assert.Equal(ApplicationUpdateStatus.VerificationFailed, snapshot.Status);
        Assert.False(runtime.TryPrepareRestart());
    }

    [Fact]
    public async Task DifferentPendingVersionIsRejected()
    {
        VelopackClientAsset? pending = null;
        var client = new FakeClient
        {
            CheckResult = Asset("2.0.0", null),
            PendingAccessor = () => pending,
            DownloadHandler = (_, _, _) =>
            {
                pending = Asset("3.0.0", null);
                return Task.CompletedTask;
            }
        };
        using var runtime = new VelopackUpdateRuntime(client, "1.0.0");
        await runtime.CheckAsync(CancellationToken.None);

        var snapshot = await runtime.DownloadAsync(CancellationToken.None);

        Assert.Equal(ApplicationUpdateStatus.VerificationFailed, snapshot.Status);
        Assert.False(runtime.TryPrepareRestart());
    }

    [Fact]
    public async Task SameVersionWithDifferentAssetFingerprintIsRejected()
    {
        VelopackClientAsset? pending = null;
        var client = new FakeClient
        {
            CheckResult = Asset("2.0.0", null, sha256: "target-sha"),
            PendingAccessor = () => pending,
            DownloadHandler = (_, _, _) =>
            {
                pending = Asset("2.0.0", null, sha256: "different-sha");
                return Task.CompletedTask;
            }
        };
        using var runtime = new VelopackUpdateRuntime(client, "1.0.0");
        await runtime.CheckAsync(CancellationToken.None);

        var snapshot = await runtime.DownloadAsync(CancellationToken.None);

        Assert.Equal(ApplicationUpdateStatus.VerificationFailed, snapshot.Status);
        Assert.False(runtime.TryPrepareRestart());
    }

    [Fact]
    public void NewRuntimeRebuildsReadyStateFromCurrentPendingAsset()
    {
        var client = new FakeClient
        {
            PendingAccessor = () => Asset("2.0.0", "已下载")
        };

        using var runtime = new VelopackUpdateRuntime(client, "1.0.0");

        Assert.Equal(ApplicationUpdateStatus.ReadyToRestart, runtime.Snapshot.Status);
        Assert.Equal("2.0.0", runtime.Snapshot.LatestVersion);
        Assert.True(runtime.TryPrepareRestart());
        Assert.True(runtime.LaunchPreparedUpdateAndRestart());
        Assert.Equal(1, client.LaunchCalls);
    }

    [Fact]
    public async Task CheckDoesNotOverwriteReadyStateRebuiltFromPendingAsset()
    {
        var client = new FakeClient
        {
            PendingAccessor = () => Asset("2.0.0", "已下载")
        };
        using var runtime = new VelopackUpdateRuntime(client, "1.0.0");

        var snapshot = await runtime.CheckAsync(CancellationToken.None);

        Assert.Equal(ApplicationUpdateStatus.ReadyToRestart, snapshot.Status);
        Assert.Equal("2.0.0", snapshot.LatestVersion);
        Assert.Equal(0, client.CheckCalls);
    }

    [Fact]
    public void LaunchWithoutPreparedAssetIsRejected()
    {
        var client = new FakeClient();
        using var runtime = new VelopackUpdateRuntime(client, "1.0.0");

        Assert.False(runtime.LaunchPreparedUpdateAndRestart());
        Assert.Equal(0, client.LaunchCalls);
    }

    [Fact]
    public void PreparedUpdateCanOnlyBeLaunchedOnce()
    {
        var client = new FakeClient
        {
            PendingAccessor = () => Asset("2.0.0", null)
        };
        using var runtime = new VelopackUpdateRuntime(client, "1.0.0");
        Assert.True(runtime.TryPrepareRestart());

        var first = runtime.LaunchPreparedUpdateAndRestart();
        var second = runtime.LaunchPreparedUpdateAndRestart();

        Assert.True(first);
        Assert.False(second);
        Assert.Equal(1, client.LaunchCalls);
    }

    [Fact]
    public async Task ConcurrentLaunchesConsumePreparedUpdateAtomically()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeClient
        {
            PendingAccessor = () => Asset("2.0.0", null),
            LaunchHandler = () =>
            {
                entered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
            }
        };
        using var runtime = new VelopackUpdateRuntime(client, "1.0.0");
        Assert.True(runtime.TryPrepareRestart());

        var first = Task.Run(runtime.LaunchPreparedUpdateAndRestart);
        await entered.Task;
        var second = await Task.Run(runtime.LaunchPreparedUpdateAndRestart);
        release.SetResult();

        Assert.True(await first);
        Assert.False(second);
        Assert.Equal(1, client.LaunchCalls);
    }

    [Fact]
    public void FailedLaunchStillConsumesPreparedUpdate()
    {
        var client = new FakeClient
        {
            PendingAccessor = () => Asset("2.0.0", null),
            LaunchException = new InvalidOperationException("launch failed")
        };
        using var runtime = new VelopackUpdateRuntime(client, "1.0.0");
        Assert.True(runtime.TryPrepareRestart());

        Assert.Throws<InvalidOperationException>(() => runtime.LaunchPreparedUpdateAndRestart());

        Assert.False(runtime.LaunchPreparedUpdateAndRestart());
        Assert.Equal(1, client.LaunchCalls);
    }

    [Fact]
    public async Task ClientFailuresMapToFixedDomainStatusesWithoutLeakingDetails()
    {
        var client = new FakeClient
        {
            CheckException = new HttpRequestException("https://secret.example/path")
        };
        using var runtime = new VelopackUpdateRuntime(client, "1.0.0");

        var snapshot = await runtime.CheckAsync(CancellationToken.None);

        Assert.Equal(ApplicationUpdateStatus.NetworkUnavailable, snapshot.Status);
        Assert.Equal(string.Empty, snapshot.UserMessage);
        Assert.DoesNotContain("secret", snapshot.UserMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancelledCheckRestoresStableStateWhileUnderlyingOperationRemainsTracked()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeClient
        {
            CheckHandler = async cancellationToken =>
            {
                started.TrySetResult();
                try
                {
                    await release.Task.WaitAsync(cancellationToken);
                    return null;
                }
                catch (OperationCanceledException)
                {
                    cancellationObserved.TrySetResult();
                    await release.Task;
                    throw;
                }
            }
        };
        using var runtime = new VelopackUpdateRuntime(client, "1.0.0");
        using var cancellation = new CancellationTokenSource();

        var check = runtime.CheckAsync(cancellation.Token);
        await started.Task;
        cancellation.Cancel();
        await cancellationObserved.Task;

        Assert.Equal(ApplicationUpdateStatus.Idle, runtime.Snapshot.Status);
        Assert.False(check.IsCompleted);
        release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
        Assert.Equal(ApplicationUpdateStatus.UpToDate,
            (await runtime.CheckAsync(CancellationToken.None)).Status);
    }

    [Fact]
    public async Task CancelledDownloadRestoresAvailableState()
    {
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var target = Asset("2.0.0", null);
        var client = new FakeClient
        {
            CheckResult = target,
            DownloadHandler = async (_, _, cancellationToken) =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    cancellationObserved.TrySetResult();
                    throw;
                }
            }
        };
        using var runtime = new VelopackUpdateRuntime(client, "1.0.0");
        await runtime.CheckAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();

        var download = runtime.DownloadAsync(cancellation.Token);
        cancellation.Cancel();
        await cancellationObserved.Task;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        Assert.Equal(ApplicationUpdateStatus.Available, runtime.Snapshot.Status);
    }

    [Fact]
    public async Task CancellationWinsIfDownloadReportsAnotherFailureDuringCancellation()
    {
        var target = Asset("2.0.0", null);
        var client = new FakeClient
        {
            CheckResult = target,
            DownloadHandler = async (_, _, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                    .ContinueWith(_ => { }, CancellationToken.None);
                throw new HttpRequestException("late transport failure");
            }
        };
        using var runtime = new VelopackUpdateRuntime(client, "1.0.0");
        await runtime.CheckAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();

        var download = runtime.DownloadAsync(cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        Assert.Equal(ApplicationUpdateStatus.Available, runtime.Snapshot.Status);
    }

    private static ApplicationBuildInfo BuildInfo(string version, string? httpsFeedUrl = "https://updates.example.invalid/app/") =>
        ApplicationBuildInfo.CreateForTests(version, "adapter-test", httpsFeedUrl);

    private static VelopackClientAsset Asset(
        string version,
        string? notes,
        string sha256 = "sha-256") =>
        new(
            new VelopackAssetFingerprint(
                "SuperPan.BaiduBatchTransfer",
                version,
                "Full",
                $"SuperPan.BaiduBatchTransfer-{version}-full.nupkg",
                1024,
                sha256),
            notes,
            new object());

    private sealed class FakeClient : IApplicationUpdateClient
    {
        public bool IsInstalled { get; init; } = true;

        public string? CurrentVersion { get; init; }

        public VelopackClientAsset? CheckResult { get; init; }

        public Exception? CheckException { get; init; }

        public Func<CancellationToken, Task<VelopackClientAsset?>>? CheckHandler { get; init; }

        public Func<VelopackClientAsset?> PendingAccessor { get; set; } = () => null;

        public Func<VelopackClientAsset, Action<int>, CancellationToken, Task>? DownloadHandler { get; init; }

        public int CheckCalls { get; private set; }

        public int DownloadCalls { get; private set; }

        public int LaunchCalls { get; private set; }

        public Exception? LaunchException { get; init; }

        public Action? LaunchHandler { get; init; }

        public VelopackClientAsset? UpdatePendingRestart => PendingAccessor();

        public Task<VelopackClientAsset?> CheckForUpdatesAsync(CancellationToken cancellationToken)
        {
            CheckCalls++;
            if (CheckHandler is not null)
                return CheckHandler(cancellationToken);
            return CheckException is null
                ? Task.FromResult(CheckResult)
                : Task.FromException<VelopackClientAsset?>(CheckException);
        }

        public Task DownloadUpdatesAsync(
            VelopackClientAsset update,
            Action<int> progress,
            CancellationToken cancellationToken)
        {
            DownloadCalls++;
            return DownloadHandler?.Invoke(update, progress, cancellationToken) ?? Task.CompletedTask;
        }

        public void WaitExitThenApplyUpdates(VelopackClientAsset update)
        {
            LaunchCalls++;
            LaunchHandler?.Invoke();
            if (LaunchException is not null)
                throw LaunchException;
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeManagerAdapter : IVelopackUpdateManagerAdapter
    {
        public bool IsInstalled { get; init; } = true;

        public string? CurrentVersion { get; init; } = "1.0.0";

        public VelopackClientAsset? UpdatePendingRestart { get; init; }

        public Func<Task<VelopackClientAsset?>>? CheckHandler { get; init; }

        public VelopackClientAsset? AppliedAsset { get; private set; }

        public bool ApplySilent { get; private set; }

        public bool ApplyRestart { get; private set; }

        public string[]? RestartArguments { get; private set; }

        public Task<VelopackClientAsset?> CheckForUpdatesAsync() =>
            CheckHandler?.Invoke() ?? Task.FromResult<VelopackClientAsset?>(null);

        public Task DownloadUpdatesAsync(
            VelopackClientAsset update,
            Action<int> progress,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public void WaitExitThenApplyUpdates(
            VelopackClientAsset update,
            bool silent,
            bool restart,
            string[] restartArguments)
        {
            AppliedAsset = update;
            ApplySilent = silent;
            ApplyRestart = restart;
            RestartArguments = restartArguments;
        }
    }
}
