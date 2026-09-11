using BaiduBatchTransfer.App.Features.ManagedBrowser;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed partial class ManagedChromiumProcessManagerTests
{
    [Theory]
    [InlineData("https://pan.baidu.com/s/1synthetic?pwd=aB12", true)]
    [InlineData("https://pan.baidu.com/s/1synthetic", true)]
    [InlineData("https://pan.baidu.com/s/other", false)]
    [InlineData("https://pan.baidu.com/s/1synthetic?pwd=abcd", false)]
    [InlineData("https://pan.baidu.com/s/1synthetic?extra=1", false)]
    [InlineData("https://pan.baidu.com/s/1synthetic#fragment", false)]
    [InlineData("https://evil.example/s/1synthetic", false)]
    [InlineData("https://PAN.baidu.com/s/1synthetic", false)]
    [InlineData("https://pan.baidu.com:443/s/1synthetic", false)]
    [InlineData("https://pan.baidu.com/s/1Synthetic", false)]
    [InlineData("https://pan.baidu.com/s/%31synthetic", false)]
    [InlineData("https://pan.baidu.com/", false)]
    [InlineData("https://pan.baidu.com/disk/main#/index?category=all", false)]
    public async Task TransferSourceProofRequiresExactSourceWithoutBroadeningLoginPolicy(string page, bool expected)
    {
        var f = await Fixture.CreateAsync();
        using var manager = f.Manager;
        var handle = (await manager.LaunchAsync(f.Installation, f.Profile)).Session!;
        f.Targets.Page = new Uri(page);

        Assert.Equal(expected, await VerifySource(manager, handle, SourceBinding(handle)));

        Assert.Null(f.Targets.LatestSelection); // The source proof must never discover/select a target.
        if (expected) Assert.Null((await manager.ReconnectAsync(handle)).Session);
        Assert.Equal(1, f.Processes.LaunchCount);
        Assert.Equal(0, f.Processes.Process.Terminations);
    }

    [Theory]
    [InlineData("browser")]
    [InlineData("target")]
    [InlineData("endpoint")]
    [InlineData("copy")]
    [InlineData("stopped")]
    public async Task TransferSourceProofRejectsStaleOrMismatchedBindingBeforeProbe(string mismatch)
    {
        var f = await Fixture.CreateAsync();
        using var manager = f.Manager;
        var handle = (await manager.LaunchAsync(f.Installation, f.Profile)).Session!;
        var binding = new ManagedBrowserOpenTransferSourceRequest(mismatch == "browser" ? Guid.NewGuid() : handle.BrowserId,
            mismatch == "target" ? "wrong-tab" : handle.TargetId,
            mismatch == "endpoint" ? new Uri("http://127.0.0.1:49153/") : handle.Endpoint, 500, SourceBinding(handle).SourceUri, "123");
        if (mismatch == "copy") handle = new(handle.RootProcessId, handle.Installation, handle.Profile,
            new DevToolsActivePort(49152, handle.BrowserId, handle.Endpoint,
                new Uri($"ws://127.0.0.1:49152/devtools/browser/{handle.BrowserId:D}")), handle.TargetId);
        if (mismatch == "stopped") Assert.True(await manager.StopAsync(handle));
        var probes = 0;
        f.Targets.OnRead = () => probes++;

        Assert.False(await VerifySource(manager, handle, binding));
        Assert.Equal(0, probes);
        Assert.Equal(1, f.Processes.LaunchCount);
    }

    [Theory]
    [InlineData("browser")]
    [InlineData("target")]
    public async Task TransferSourceProofRejectsChangedProbeIdentity(string mismatch)
    {
        var f = await Fixture.CreateAsync();
        using var manager = f.Manager;
        var handle = (await manager.LaunchAsync(f.Installation, f.Profile)).Session!;
        f.Targets.Page = new Uri(SourceBinding(handle).SourceUri);
        if (mismatch == "browser") f.Targets.BrowserId = Guid.NewGuid();
        else f.Targets.TargetId = "wrong-tab";
        Assert.False(await VerifySource(manager, handle, SourceBinding(handle)));
    }

    [Theory]
    [InlineData("root", false)] [InlineData("root", true)]
    [InlineData("job", false)] [InlineData("job", true)]
    [InlineData("exe", false)] [InlineData("exe", true)]
    [InlineData("profile", false)] [InlineData("profile", true)]
    [InlineData("port", false)] [InlineData("port", true)]
    [InlineData("listener", false)] [InlineData("listener", true)]
    public async Task TransferSourceProofChecksOwnershipBeforeAndAfterProbe(string loss, bool during)
    {
        var f = await Fixture.CreateAsync();
        using var manager = f.Manager;
        var handle = (await manager.LaunchAsync(f.Installation, f.Profile)).Session!;
        f.Targets.Page = new Uri(SourceBinding(handle).SourceUri);
        void Lose()
        {
            switch (loss)
            {
                case "root": f.Processes.Process.Alive = false; break;
                case "job": f.Processes.Process.Owned = false; break;
                case "exe": f.Executable.Valid = false; break;
                case "profile": f.ProfileLease.Valid = false; break;
                case "port": f.ProfileLease.PortValid = false; break;
                case "listener": f.Listeners.Owner = 999; break;
            }
        }
        if (during) f.Targets.OnRead = Lose;
        else Lose();
        await Assert.ThrowsAsync<IOException>(() => VerifySource(manager, handle, SourceBinding(handle)));
        Assert.Equal(1, f.Processes.LaunchCount);
        Assert.Equal(0, f.Processes.Process.Terminations);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task TransferSourceProofCancellationCannotSucceedEvenWhenProbeIgnoresToken(bool before)
    {
        var f = await Fixture.CreateAsync();
        using var manager = f.Manager;
        var handle = (await manager.LaunchAsync(f.Installation, f.Profile)).Session!;
        f.Targets.Page = new Uri(SourceBinding(handle).SourceUri);
        using var cancellation = new CancellationTokenSource();
        if (before) cancellation.Cancel(); else f.Targets.OnRead = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => VerifySource(manager, handle, SourceBinding(handle), cancellation.Token));
    }

    [Fact]
    public async Task TransferSourceProofBoundsUncooperativeProbeAndRejectsLateSuccess()
    {
        var f = await Fixture.CreateAsync();
        using var manager = f.Manager;
        var handle = (await manager.LaunchAsync(f.Installation, f.Profile)).Session!;
        var late = new TaskCompletionSource<ManagedBrowserTargetSnapshot?>();
        f.Targets.Pending = late.Task;
        Assert.False(await VerifySource(manager, handle, SourceBinding(handle)).WaitAsync(TimeSpan.FromSeconds(2)));
        late.SetResult(new(handle.BrowserId, handle.TargetId, new Uri(SourceBinding(handle).SourceUri)));
        Assert.Null(f.Targets.LatestSelection);
        Assert.Equal(1, f.Processes.LaunchCount);
    }

    [Fact]
    public async Task TransferSourceProofObservesLateProbeFaultAfterBoundedReturn()
    {
        const string marker = "synthetic-late-transfer-source-probe";
        var unobserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Observe(object? _, UnobservedTaskExceptionEventArgs args)
        {
            if (args.Exception.ToString().Contains(marker, StringComparison.Ordinal)) unobserved.TrySetResult();
            args.SetObserved();
        }
        TaskScheduler.UnobservedTaskException += Observe;
        try
        {
            var weak = await CreateLateFaultAsync();
            for (var attempt = 0; attempt < 8 && weak.IsAlive; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                await Task.Delay(25);
            }
            Assert.False(unobserved.Task.IsCompleted,
                "The bounded verifier detached a late-faulting target probe without observing its exception.");
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Observe;
        }

        static async Task<WeakReference> CreateLateFaultAsync()
        {
            var f = await Fixture.CreateAsync();
            using var manager = f.Manager;
            var handle = (await manager.LaunchAsync(f.Installation, f.Profile)).Session!;
            var late = new TaskCompletionSource<ManagedBrowserTargetSnapshot?>();
            var task = late.Task;
            f.Targets.Pending = task;
            Assert.False(await VerifySource(manager, handle, SourceBinding(handle)).WaitAsync(TimeSpan.FromSeconds(2)));
            f.Targets.Pending = null;
            late.SetException(new IOException(marker));
            return new WeakReference(task);
        }
    }

    private static ManagedBrowserOpenTransferSourceRequest SourceBinding(ManagedChromiumSessionHandle handle) =>
        new(handle.BrowserId, handle.TargetId, handle.Endpoint, 500, "https://pan.baidu.com/s/1synthetic?pwd=aB12", "123");

    [Fact]
    public async Task TransferSourceProofRejectsSynchronousLateProbeDespiteCompletedTask()
    {
        var f = await Fixture.CreateAsync();
        using var manager = f.Manager;
        var handle = (await manager.LaunchAsync(f.Installation, f.Profile)).Session!;
        f.Targets.Page = new(SourceBinding(handle).SourceUri);
        f.Targets.OnRead = () => Thread.Sleep(100);
        Assert.False(await VerifySource(manager, handle, SourceBinding(handle)));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task TransferSourceProofRejectsAddedPwdAndRelativeUri(bool relative)
    {
        var f = await Fixture.CreateAsync();
        using var manager = f.Manager;
        var handle = (await manager.LaunchAsync(f.Installation, f.Profile)).Session!;
        var binding = new ManagedBrowserOpenTransferSourceRequest(handle.BrowserId, handle.TargetId, handle.Endpoint,
            500, "https://pan.baidu.com/s/1synthetic", "123");
        f.Targets.Page = relative ? new Uri("/s/1synthetic", UriKind.Relative) : new Uri(binding.SourceUri + "?pwd=abcd");
        Assert.False(await VerifySource(manager, handle, binding));
    }

    private static Task<bool> VerifySource(ManagedChromiumProcessManager manager, ManagedChromiumSessionHandle handle,
        ManagedBrowserOpenTransferSourceRequest binding, CancellationToken token = default) =>
        manager.VerifyTransferSourceAsync(handle, binding, token);
}
