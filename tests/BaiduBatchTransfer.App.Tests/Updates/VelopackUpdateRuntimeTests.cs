using System.Net.Http;
using BaiduBatchTransfer.App.Updates;

namespace BaiduBatchTransfer.App.Tests.Updates;

public sealed class VelopackUpdateRuntimeTests
{
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

    private static ApplicationBuildInfo BuildInfo(string version, string? httpsFeedUrl = "https://updates.example.invalid/app/") =>
        ApplicationBuildInfo.CreateForTests(version, "adapter-test", httpsFeedUrl);

    private static VelopackClientAsset Asset(string version, string? notes) =>
        new(version, notes, new object());

    private sealed class FakeClient : IApplicationUpdateClient
    {
        public bool IsInstalled { get; init; } = true;

        public string? CurrentVersion { get; init; }

        public VelopackClientAsset? CheckResult { get; init; }

        public Exception? CheckException { get; init; }

        public Func<VelopackClientAsset?> PendingAccessor { get; set; } = () => null;

        public Func<VelopackClientAsset, Action<int>, CancellationToken, Task>? DownloadHandler { get; init; }

        public int CheckCalls { get; private set; }

        public int DownloadCalls { get; private set; }

        public int LaunchCalls { get; private set; }

        public VelopackClientAsset? UpdatePendingRestart => PendingAccessor();

        public Task<VelopackClientAsset?> CheckForUpdatesAsync(CancellationToken cancellationToken)
        {
            CheckCalls++;
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
        }

        public void Dispose()
        {
        }
    }
}
