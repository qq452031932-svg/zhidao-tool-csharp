using BaiduBatchTransfer.App.Updates;

namespace BaiduBatchTransfer.App.Tests.Updates;

public sealed class ApplicationUpdateServiceTests
{
    [Fact]
    public async Task NotConfiguredRuntimeStaysNotConfiguredWithoutStartingAnOperation()
    {
        using var runtime = UnavailableApplicationUpdateRuntime.NotConfigured("1.2.3");
        using var service = new ApplicationUpdateService(runtime);

        var snapshot = await service.CheckAsync(manual: true, CancellationToken.None);

        Assert.Equal(ApplicationUpdateStatus.NotConfigured, snapshot.Status);
        Assert.Equal("1.2.3", snapshot.CurrentVersion);
    }

    [Fact]
    public async Task UnsupportedRuntimeStaysUnsupported()
    {
        using var runtime = UnavailableApplicationUpdateRuntime.Unsupported("2.0.0");
        using var service = new ApplicationUpdateService(runtime);

        var snapshot = await service.CheckAsync(manual: true, CancellationToken.None);

        Assert.Equal(ApplicationUpdateStatus.Unsupported, snapshot.Status);
        Assert.Equal("当前运行方式不支持在线更新。", snapshot.UserMessage);
    }

    [Fact]
    public async Task AutomaticNetworkFailureIsSilentButManualFailureUsesFixedChineseMessage()
    {
        var runtime = new FakeRuntime
        {
            CheckHandler = _ => Task.FromResult(Snapshot(ApplicationUpdateStatus.NetworkUnavailable))
        };
        using var service = new ApplicationUpdateService(runtime);

        var automatic = await service.CheckAsync(manual: false, CancellationToken.None);
        var manual = await service.CheckAsync(manual: true, CancellationToken.None);

        Assert.Equal(ApplicationUpdateStatus.NetworkUnavailable, automatic.Status);
        Assert.Equal(string.Empty, automatic.UserMessage);
        Assert.Equal(ApplicationUpdateStatus.NetworkUnavailable, manual.Status);
        Assert.Equal("暂时无法连接更新服务，请稍后重试。", manual.UserMessage);
    }

    [Fact]
    public async Task ConcurrentChecksShareOneRuntimeOperation()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ApplicationUpdateSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new FakeRuntime
        {
            CheckHandler = _ =>
            {
                started.TrySetResult();
                return release.Task;
            }
        };
        using var service = new ApplicationUpdateService(runtime);

        var first = service.CheckAsync(manual: true, CancellationToken.None);
        await started.Task;
        var second = service.CheckAsync(manual: true, CancellationToken.None);
        release.SetResult(Snapshot(ApplicationUpdateStatus.UpToDate));

        var results = await Task.WhenAll(first, second);
        Assert.Equal(1, runtime.CheckCalls);
        Assert.All(results, result => Assert.Equal(ApplicationUpdateStatus.UpToDate, result.Status));
    }

    [Fact]
    public async Task CancellingOneWaiterDoesNotCancelTheSharedCheckForOtherWaiters()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ApplicationUpdateSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new FakeRuntime
        {
            CheckHandler = async cancellationToken =>
            {
                started.TrySetResult();
                return await release.Task.WaitAsync(cancellationToken);
            }
        };
        using var service = new ApplicationUpdateService(runtime);
        using var cancellation = new CancellationTokenSource();

        var cancelledWaiter = service.CheckAsync(manual: false, cancellation.Token);
        await started.Task;
        var remainingWaiter = service.CheckAsync(manual: false, CancellationToken.None);
        cancellation.Cancel();
        release.TrySetResult(Snapshot(ApplicationUpdateStatus.UpToDate));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledWaiter);
        Assert.Equal(ApplicationUpdateStatus.UpToDate, (await remainingWaiter).Status);
        Assert.Equal(1, runtime.CheckCalls);
    }

    [Fact]
    public void DisposingServiceDoesNotDisposeRuntime()
    {
        var runtime = new FakeRuntime();
        var service = new ApplicationUpdateService(runtime);

        service.Dispose();

        Assert.False(runtime.WasDisposed);
    }

    private static ApplicationUpdateSnapshot Snapshot(ApplicationUpdateStatus status) =>
        new(status, "1.0.0", null, null, 0, string.Empty);

    private sealed class FakeRuntime : IApplicationUpdateRuntime
    {
        public ApplicationUpdateSnapshot Snapshot { get; private set; } =
            ApplicationUpdateServiceTests.Snapshot(ApplicationUpdateStatus.Idle);

        public event EventHandler<ApplicationUpdateSnapshot>? SnapshotChanged;

        public Func<CancellationToken, Task<ApplicationUpdateSnapshot>>? CheckHandler { get; init; }

        public int CheckCalls { get; private set; }

        public bool WasDisposed { get; private set; }

        public async Task<ApplicationUpdateSnapshot> CheckAsync(CancellationToken cancellationToken)
        {
            CheckCalls++;
            Snapshot = CheckHandler is null
                ? Snapshot
                : await CheckHandler(cancellationToken);
            SnapshotChanged?.Invoke(this, Snapshot);
            return Snapshot;
        }

        public Task<ApplicationUpdateSnapshot> DownloadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Snapshot);

        public bool TryPrepareRestart() => false;

        public bool LaunchPreparedUpdateAndRestart() => false;

        public void Dispose() => WasDisposed = true;
    }
}
