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
    public async Task ReentrantDownloadFromCheckingNotificationReturnsBusyWithoutCallingRuntime()
    {
        var completion = new TaskCompletionSource<ApplicationUpdateSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new FakeRuntime();
        runtime.CheckHandler = _ =>
        {
            runtime.Raise(Snapshot(ApplicationUpdateStatus.Checking));
            return completion.Task;
        };
        using var service = new ApplicationUpdateService(runtime);
        ApplicationUpdateSnapshot? reentrantResult = null;
        service.SnapshotChanged += (_, snapshot) =>
        {
            if (snapshot.Status == ApplicationUpdateStatus.Checking)
                reentrantResult = service.DownloadAsync(CancellationToken.None).GetAwaiter().GetResult();
        };

        var check = service.CheckAsync(manual: true, CancellationToken.None);
        completion.SetResult(Snapshot(ApplicationUpdateStatus.UpToDate));
        await check;

        Assert.Equal(ApplicationUpdateStatus.Busy, reentrantResult?.Status);
        Assert.Equal(0, runtime.DownloadCalls);
    }

    [Fact]
    public async Task SubscriberFailureDoesNotChangeOperationResult()
    {
        var runtime = new FakeRuntime();
        runtime.CheckHandler = _ =>
        {
            var result = Snapshot(ApplicationUpdateStatus.UpToDate);
            runtime.Raise(result);
            return Task.FromResult(result);
        };
        using var service = new ApplicationUpdateService(runtime);
        service.SnapshotChanged += (_, _) => throw new InvalidOperationException("subscriber secret");

        var result = await service.CheckAsync(manual: true, CancellationToken.None);

        Assert.Equal(ApplicationUpdateStatus.UpToDate, result.Status);
        Assert.Equal(ApplicationUpdateStatus.UpToDate, service.Snapshot.Status);
    }

    [Fact]
    public async Task ReturnedStaleSnapshotDoesNotOverwriteLatestRuntimeEvent()
    {
        var runtime = new FakeRuntime { Snapshot = Snapshot(ApplicationUpdateStatus.Available) };
        runtime.DownloadHandler = _ =>
        {
            runtime.Raise(Snapshot(ApplicationUpdateStatus.Downloading));
            runtime.Raise(Snapshot(ApplicationUpdateStatus.ReadyToRestart));
            return Task.FromResult(Snapshot(ApplicationUpdateStatus.Available));
        };
        using var service = new ApplicationUpdateService(runtime);

        await service.DownloadAsync(CancellationToken.None);

        Assert.Equal(ApplicationUpdateStatus.ReadyToRestart, service.Snapshot.Status);
    }

    [Fact]
    public async Task DownloadDuringCheckReturnsBusyAndDoesNotEnterRuntime()
    {
        var release = new TaskCompletionSource<ApplicationUpdateSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new FakeRuntime { CheckHandler = _ => release.Task };
        using var service = new ApplicationUpdateService(runtime);

        var check = service.CheckAsync(manual: true, CancellationToken.None);
        var download = await service.DownloadAsync(CancellationToken.None);
        release.SetResult(Snapshot(ApplicationUpdateStatus.UpToDate));
        await check;

        Assert.Equal(ApplicationUpdateStatus.Busy, download.Status);
        Assert.Equal(0, runtime.DownloadCalls);
    }

    [Fact]
    public async Task ConcurrentDownloadsShareOneRuntimeOperation()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ApplicationUpdateSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new FakeRuntime { Snapshot = Snapshot(ApplicationUpdateStatus.Available) };
        runtime.DownloadHandler = _ =>
        {
            started.TrySetResult();
            return release.Task;
        };
        using var service = new ApplicationUpdateService(runtime);

        var first = service.DownloadAsync(CancellationToken.None);
        await started.Task;
        var second = service.DownloadAsync(CancellationToken.None);
        release.SetResult(Snapshot(ApplicationUpdateStatus.ReadyToRestart));

        var results = await Task.WhenAll(first, second);
        Assert.Equal(1, runtime.DownloadCalls);
        Assert.All(results, result => Assert.Equal(ApplicationUpdateStatus.ReadyToRestart, result.Status));
    }

    [Fact]
    public async Task MixedAutomaticAndManualWaitersShareCheckAndKeepManualNetworkMessage()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ApplicationUpdateSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new FakeRuntime();
        runtime.CheckHandler = _ =>
        {
            started.TrySetResult();
            return release.Task;
        };
        using var service = new ApplicationUpdateService(runtime);

        var automatic = service.CheckAsync(manual: false, CancellationToken.None);
        await started.Task;
        var manual = service.CheckAsync(manual: true, CancellationToken.None);
        release.SetResult(Snapshot(ApplicationUpdateStatus.NetworkUnavailable));

        Assert.Equal(string.Empty, (await automatic).UserMessage);
        Assert.Equal("暂时无法连接更新服务，请稍后重试。", (await manual).UserMessage);
        Assert.Equal("暂时无法连接更新服务，请稍后重试。", service.Snapshot.UserMessage);
        Assert.Equal(1, runtime.CheckCalls);
    }

    [Fact]
    public async Task CancelledCheckRestoresStableSnapshotAndRetainsGateUntilRuntimeFinishes()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ApplicationUpdateSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new FakeRuntime();
        runtime.CheckHandler = async cancellationToken =>
        {
            runtime.Raise(Snapshot(ApplicationUpdateStatus.Checking));
            started.TrySetResult();
            try
            {
                return await release.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                runtime.Raise(Snapshot(ApplicationUpdateStatus.Idle));
                await release.Task;
                throw;
            }
        };
        using var service = new ApplicationUpdateService(runtime);
        using var cancellation = new CancellationTokenSource();

        var cancelled = service.CheckAsync(manual: true, cancellation.Token);
        await started.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        Assert.Equal(ApplicationUpdateStatus.Idle, service.Snapshot.Status);
        Assert.Equal(ApplicationUpdateStatus.Busy,
            (await service.CheckAsync(manual: true, CancellationToken.None)).Status);
        Assert.Equal(1, runtime.CheckCalls);

        release.SetResult(Snapshot(ApplicationUpdateStatus.UpToDate));
        await runtime.OperationFinished.Task;
        Assert.Equal(ApplicationUpdateStatus.UpToDate,
            (await service.CheckAsync(manual: true, CancellationToken.None)).Status);
        Assert.Equal(2, runtime.CheckCalls);
    }

    [Fact]
    public async Task DisposeDuringCheckRestoresStableSnapshotWithoutDisposingRuntime()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new FakeRuntime();
        runtime.CheckHandler = async cancellationToken =>
        {
            runtime.Raise(Snapshot(ApplicationUpdateStatus.Checking));
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return Snapshot(ApplicationUpdateStatus.UpToDate);
            }
            catch (OperationCanceledException)
            {
                runtime.Raise(Snapshot(ApplicationUpdateStatus.Idle));
                throw;
            }
        };
        var service = new ApplicationUpdateService(runtime);

        var check = service.CheckAsync(manual: false, CancellationToken.None);
        await started.Task;
        service.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
        Assert.Equal(ApplicationUpdateStatus.Idle, service.Snapshot.Status);
        Assert.False(runtime.WasDisposed);
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
        public ApplicationUpdateSnapshot Snapshot { get; set; } =
            ApplicationUpdateServiceTests.Snapshot(ApplicationUpdateStatus.Idle);

        public event EventHandler<ApplicationUpdateSnapshot>? SnapshotChanged;

        public Func<CancellationToken, Task<ApplicationUpdateSnapshot>>? CheckHandler { get; set; }

        public Func<CancellationToken, Task<ApplicationUpdateSnapshot>>? DownloadHandler { get; set; }

        public int CheckCalls { get; private set; }

        public int DownloadCalls { get; private set; }

        public bool WasDisposed { get; private set; }

        public Task<ApplicationUpdateSnapshot> CheckAsync(CancellationToken cancellationToken)
        {
            CheckCalls++;
            Task<ApplicationUpdateSnapshot> operation;
            try
            {
                operation = CheckHandler is null
                    ? Task.FromResult(Snapshot)
                    : CheckHandler(cancellationToken);
            }
            catch (Exception exception)
            {
                operation = Task.FromException<ApplicationUpdateSnapshot>(exception);
            }

            _ = operation.ContinueWith(
                _ => OperationFinished.TrySetResult(),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
            return operation;
        }

        public async Task<ApplicationUpdateSnapshot> DownloadAsync(CancellationToken cancellationToken)
        {
            DownloadCalls++;
            return DownloadHandler is null
                ? Snapshot
                : await DownloadHandler(cancellationToken);
        }

        public TaskCompletionSource OperationFinished { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Raise(ApplicationUpdateSnapshot value)
        {
            Snapshot = value;
            SnapshotChanged?.Invoke(this, value);
        }

        public bool TryPrepareRestart() => false;

        public bool LaunchPreparedUpdateAndRestart() => false;

        public void Dispose() => WasDisposed = true;
    }
}
