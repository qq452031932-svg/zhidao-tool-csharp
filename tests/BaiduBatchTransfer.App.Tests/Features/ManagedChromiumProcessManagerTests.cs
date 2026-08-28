using System.Net;
using System.Text;
using BaiduBatchTransfer.App.Features.ManagedBrowser;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Infrastructure.Security;
using Microsoft.Win32.SafeHandles;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class ManagedChromiumProcessManagerTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReturnedPinsMustMatchRequestedProfileAndExecutableIdentities(bool changedProfile)
    {
        var fixture = await Fixture.CreateAsync();
        using var manager = fixture.Manager;
        if (changedProfile)
        {
            var other = await Fixture.CreateAsync();
            using var unused = other.Manager;
            fixture.ProfileLease = other.ProfileLease;
        }
        else fixture.Executable = new FakeExecutable(new BrowserInstallationIdentity(Guid.NewGuid(), BrowserFamily.Chrome,
            @"C:\Browser", @"C:\Browser\chrome.exe", "Google LLC", "Google Chrome", "replaced-file-identity"));
        await Assert.ThrowsAsync<IOException>(() => manager.LaunchAsync(fixture.Installation, fixture.Profile));
        Assert.Equal(0, fixture.Processes.LaunchCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ActiveAccountRejectsDifferentInstallationOrProfileWithoutClosingExistingBrowser(bool changedProfile)
    {
        var fixture = await Fixture.CreateAsync();
        await using var manager = fixture.Manager;
        await manager.LaunchAsync(fixture.Installation, fixture.Profile);
        var profile = fixture.Profile;
        var installation = fixture.Installation;
        if (changedProfile)
        {
            var policy = new FileSystemManagedBrowserProfilePolicy(Path.Combine(Path.GetTempPath(), "task10-other-" + Guid.NewGuid().ToString("N")));
            profile = (await policy.GetOrCreateAsync(profile.AccountId, profile.Family)).Profile!;
        }
        else installation = new BrowserInstallationIdentity(Guid.NewGuid(), BrowserFamily.Chrome,
            @"C:\Browser", @"C:\Browser\chrome.exe", "Google LLC", "Google Chrome", "replacement");
        await Assert.ThrowsAsync<IOException>(() => manager.LaunchAsync(installation, profile));
        Assert.Equal(1, fixture.Processes.LaunchCount);
        Assert.Equal(0, fixture.Processes.Process.Terminations);
    }
    [Fact]
    public async Task ReconnectRejectsListenerSwitchEvenToAnotherVerifiedDescendantOfSameJob()
    {
        var fixture = await Fixture.CreateAsync();
        await using var manager = fixture.Manager;
        var session = (await manager.LaunchAsync(fixture.Installation, fixture.Profile)).Session!;
        fixture.Processes.Process.AdditionalOwnedPid = 456;
        fixture.Listeners.Owner = 456;
        await Assert.ThrowsAsync<IOException>(() => manager.ReconnectAsync(session));
    }

    [Fact]
    public async Task DeadRootWithUnconfirmedDescendantsCannotStartAnotherBrowserOnSameProfile()
    {
        var fixture = await Fixture.CreateAsync();
        using var manager = fixture.Manager;
        await manager.LaunchAsync(fixture.Installation, fixture.Profile);
        fixture.Processes.Process.Alive = false;
        fixture.Processes.Process.ExitConfirmed = false;
        await Assert.ThrowsAsync<IOException>(() => manager.LaunchAsync(fixture.Installation, fixture.Profile));
        Assert.Equal(1, fixture.Processes.LaunchCount);
        Assert.False(fixture.ProfileLease.Disposed);
        fixture.Processes.Process.ExitConfirmed = true;
    }

    [Fact]
    public async Task ConcurrentLaunchRequestsCreateOnlyOneOwnedBrowser()
    {
        var fixture = await Fixture.CreateAsync();
        await using var manager = fixture.Manager;
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => manager.LaunchAsync(fixture.Installation, fixture.Profile)));
        Assert.All(results, result => Assert.Same(results[0].Session, result.Session));
        Assert.Equal(1, fixture.Processes.LaunchCount);
    }

    [Fact]
    public async Task LateTargetSelectionAfterTimeoutCannotChangeNextAttachTarget()
    {
        var fixture = await Fixture.CreateAsync();
        await using var manager = fixture.Manager;
        var release = new TaskCompletionSource<ManagedBrowserTargetSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Targets.Pending = release.Task;
        Assert.Null((await manager.LaunchAsync(fixture.Installation, fixture.Profile)).Session);
        fixture.Targets.LatestSelection?.Invoke("late-unrelated-tab");
        release.SetResult(null);
        fixture.Targets.Pending = null;
        Assert.NotNull((await manager.LaunchAsync(fixture.Installation, fixture.Profile)).Session);
    }
    [Fact]
    public async Task DisposalDuringNativeCreationTracksLateJobAndRetainsPinsUntilConfirmedExit()
    {
        var fixture = await Fixture.CreateAsync();
        fixture.Processes.Process.ExitConfirmed = false;
        fixture.Processes.OnLaunch = fixture.Manager.Dispose;
        await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.Manager.LaunchAsync(fixture.Installation, fixture.Profile));
        Assert.False(fixture.ProfileLease.Disposed);
        Assert.False(fixture.Executable.Disposed);
        Assert.Equal(1, fixture.Processes.Process.Terminations);
        fixture.Processes.Process.ExitConfirmed = true;
        await fixture.Manager.DisposeAsync();
        Assert.True(fixture.ProfileLease.Disposed);
    }

    [Fact]
    public async Task FailedNativeLaunchWithUnconfirmedExitRemainsTrackedAndKeepsProfilePins()
    {
        var fixture = await Fixture.CreateAsync();
        fixture.Processes.Process.ExitConfirmed = false;
        fixture.Processes.FailWithOwnedProcess = true;
        await Assert.ThrowsAnyAsync<IOException>(() => fixture.Manager.LaunchAsync(fixture.Installation, fixture.Profile));
        Assert.False(fixture.ProfileLease.Disposed);
        Assert.False(fixture.Executable.Disposed);
        fixture.Processes.Process.ExitConfirmed = true;
        await fixture.Manager.DisposeAsync();
        Assert.True(fixture.ProfileLease.Disposed);
    }
    [Fact]
    public async Task InitialUniqueTargetRemainsPinnedWhenItsCurrentInfoIsNotReady()
    {
        var port = DevToolsActivePortReader.Parse(Encoding.ASCII.GetBytes($"49152\n/devtools/browser/{FakeTargets.FixedBrowserId:D}"),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(-1), IPAddress.Loopback);
        var probe = new ChromiumTargetProbe(new FakeTargetTransport(port, "changed-target"));
        string? selected = null;
        Assert.Null(await probe.ReadAsync(port, null, CancellationToken.None, id => selected = id));
        Assert.Equal("fixed-tab", selected);
    }

    [Fact]
    public void RealWindowsJobLayoutCreatesEmptyKillOnCloseJobWithoutLaunchingAnyProcess()
    {
        var native = new WindowsChromiumNativeApi();
        using var job = native.CreateJob();
        Assert.False(job.IsInvalid);
        Assert.True(native.IsJobEmpty(job));
    }

    [Fact]
    public async Task RegisteredManagerSupportsHostSynchronousDisposalAfterAsyncShutdown()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new FileSystemManagedBrowserProfilePolicy(Path.Combine(Path.GetTempPath(), "task10-di-" + Guid.NewGuid().ToString("N"))));
        services.AddSingleton<ManagedChromiumProcessManager>();
        using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<ManagedChromiumProcessManager>().DisposeAsync();
    }

    [Task10BrowserFact]
    public async Task OptInRealBrowserRemainsVisibleForFifteenSecondsAndOnlyOwnedJobIsClosed()
    {
        var directory = Environment.GetEnvironmentVariable("BAIDU_BATCH_TASK10_BROWSER_DIRECTORY");
        Assert.False(string.IsNullOrWhiteSpace(directory));
        var familyText = Environment.GetEnvironmentVariable("BAIDU_BATCH_TASK10_BROWSER_FAMILY") ?? "Chrome";
        Assert.True(Enum.TryParse<BrowserFamily>(familyText, out var family));
        var verification = await new BrowserExecutableVerifier().VerifyAsync(directory!, family, CancellationToken.None);
        Assert.True(verification.IsVerified);
        var policy = new FileSystemManagedBrowserProfilePolicy(Path.Combine(Path.GetTempPath(), "task10-browser-smoke-" + Guid.NewGuid().ToString("N")));
        var profile = (await policy.GetOrCreateAsync(Guid.NewGuid(), family)).Profile!;
        await using var manager = new ManagedChromiumProcessManager(policy);
        var launched = await manager.LaunchAsync(verification.Identity!, profile);
        Assert.Equal(ManagedChromiumLaunchStatus.Ready, launched.Status);
        var session = launched.Session!;
        using var process = Process.GetProcessById(session.RootProcessId);
        await Task.Delay(TimeSpan.FromSeconds(15));
        Assert.False(process.HasExited);
        var reconnected = await manager.ReconnectAsync(session);
        Assert.True(reconnected.Status is ManagedChromiumLaunchStatus.Ready or ManagedChromiumLaunchStatus.WaitingForPage);
        Assert.True(await manager.StopAsync(session));
        Assert.True(process.HasExited);
    }
    [Theory]
    [InlineData("ready")]
    [InlineData("browser-mismatch")]
    [InlineData("ambiguous-pages")]
    [InlineData("changed-target")]
    public async Task ConcreteProbeRequiresBrowserGuidUniquePageAndFreshExactTargetInfo(string scenario)
    {
        var port = DevToolsActivePortReader.Parse(Encoding.ASCII.GetBytes($"49152\n/devtools/browser/{FakeTargets.FixedBrowserId:D}"),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(-1), IPAddress.Loopback);
        var transport = new FakeTargetTransport(port, scenario);
        var probe = new ChromiumTargetProbe(transport);
        if (scenario == "browser-mismatch")
        {
            await Assert.ThrowsAsync<IOException>(() => probe.ReadAsync(port, null, CancellationToken.None));
            Assert.Empty(transport.Commands);
            return;
        }
        var target = await probe.ReadAsync(port, null, CancellationToken.None);
        if (scenario == "ready")
        {
            Assert.Equal("fixed-tab", target!.TargetId);
            Assert.Equal("https://pan.baidu.com/", target.PageUri.AbsoluteUri);
            Assert.Equal(["Target.getTargets", "Target.getTargetInfo"], transport.Commands);
        }
        else Assert.Null(target);
    }
    [Fact]
    public async Task NonCooperatingTargetProbeIsBoundedAndDoesNotBlockShutdownOrCloseVisibleBrowser()
    {
        var fixture = await Fixture.CreateAsync();
        await using var manager = fixture.Manager;
        var release = new TaskCompletionSource<ManagedBrowserTargetSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Targets.Pending = release.Task;
        var pending = manager.LaunchAsync(fixture.Installation, fixture.Profile);
        var completed = await Task.WhenAny(pending, Task.Delay(1000));
        release.SetResult(null);
        Assert.Same(pending, completed);
        Assert.Equal(ManagedChromiumLaunchStatus.WaitingForPage, (await pending).Status);
        Assert.Equal(0, fixture.Processes.Process.Terminations);
    }

    [Fact]
    public async Task StopDoesNotReleasePinnedProfileOrReportSuccessBeforeJobExitIsConfirmed()
    {
        var fixture = await Fixture.CreateAsync();
        using var manager = fixture.Manager;
        var session = (await manager.LaunchAsync(fixture.Installation, fixture.Profile)).Session!;
        fixture.Processes.Process.ExitConfirmed = false;
        Assert.False(await manager.StopAsync(session));
        Assert.False(fixture.ProfileLease.Disposed);
        Assert.False(fixture.Executable.Disposed);
        fixture.Processes.Process.ExitConfirmed = true;
        Assert.True(await manager.StopAsync(session));
        Assert.True(fixture.ProfileLease.Disposed);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NativeLaunchAssignsSuspendedRootBeforeResumeAndNeverResumesUnassignedProcess(bool assignSucceeds)
    {
        var fixture = await Fixture.CreateAsync();
        using var manager = fixture.Manager;
        var native = new FakeNative { AssignSucceeds = assignSucceeds };
        var factory = new WindowsJobObject(native);
        if (assignSucceeds)
        {
            using var process = factory.Launch(fixture.Executable, fixture.ProfileLease);
            Assert.Equal(["job", "suspended", "assign", "resume"], native.Events);
            Assert.Equal(["--user-data-dir=" + fixture.Profile.CanonicalSourcePath,
                "--remote-debugging-port=0", "--no-first-run", "--no-default-browser-check", "https://pan.baidu.com/"], native.Arguments);
        }
        else
        {
            Assert.Throws<IOException>(() => factory.Launch(fixture.Executable, fixture.ProfileLease));
            Assert.DoesNotContain("resume", native.Events);
            Assert.Contains("terminate-root", native.Events);
            Assert.Contains("terminate-job", native.Events);
            Assert.True(native.Root.IsClosed);
            Assert.True(native.Thread.IsClosed);
            Assert.True(native.Job.IsClosed);
        }
    }
    [Fact]
    public async Task LaunchAndReconnectReuseOnlyTheExactOwnedAccountBrowserProfileTuple()
    {
        var fixture = await Fixture.CreateAsync();
        await using var manager = fixture.Manager;
        var first = await manager.LaunchAsync(fixture.Installation, fixture.Profile);
        var second = await manager.LaunchAsync(fixture.Installation, fixture.Profile);
        var reconnect = await manager.ReconnectAsync(first.Session!);
        Assert.Equal(ManagedChromiumLaunchStatus.Ready, first.Status);
        Assert.Same(first.Session, second.Session);
        Assert.Same(first.Session, reconnect.Session);
        Assert.Equal(1, fixture.Processes.LaunchCount);
        Assert.Equal(fixture.Profile.OwnershipId, first.Session!.Profile.OwnershipId);
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("executable")]
    [InlineData("unowned")]
    [InlineData("wrong-owner")]
    [InlineData("owner-executable")]
    [InlineData("missing-listener")]
    [InlineData("ambiguous-listener")]
    [InlineData("exit-during-probe")]
    [InlineData("listener-change-during-probe")]
    [InlineData("browser-id")]
    [InlineData("port-replacement")]
    public async Task OwnershipFailureNeverReturnsSessionAndClosesOnlyItsOwnNewJob(string mutation)
    {
        var fixture = await Fixture.CreateAsync();
        using var manager = fixture.Manager;
        switch (mutation)
        {
            case "profile": fixture.ProfileLease.Valid = false; break;
            case "executable": fixture.Executable.Valid = false; break;
            case "unowned": fixture.Processes.Process.Owned = false; break;
            case "wrong-owner": fixture.Listeners.Owner = 987; break;
            case "owner-executable": fixture.Processes.Process.ExecutableMatches = false; break;
            case "missing-listener": fixture.Listeners.Count = 0; break;
            case "ambiguous-listener": fixture.Listeners.Count = 2; break;
            case "exit-during-probe": fixture.Targets.OnRead = () => fixture.Processes.Process.Alive = false; break;
            case "listener-change-during-probe": fixture.Targets.OnRead = () => fixture.Listeners.Owner = 987; break;
            case "browser-id": fixture.Targets.BrowserId = Guid.NewGuid(); break;
            case "port-replacement": fixture.Targets.OnRead = () => fixture.ProfileLease.PortValid = false; break;
        }
        await Assert.ThrowsAnyAsync<IOException>(() => manager.LaunchAsync(fixture.Installation, fixture.Profile));
        Assert.Equal(fixture.Processes.LaunchCount, fixture.Processes.Process.Terminations);
    }

    [Fact]
    public async Task PageNotReadyOrTargetTimeoutKeepsOwnedVisibleBrowserForUserAndRetry()
    {
        var fixture = await Fixture.CreateAsync();
        await using var manager = fixture.Manager;
        fixture.Targets.Page = new Uri("about:blank");
        var waiting = await manager.LaunchAsync(fixture.Installation, fixture.Profile);
        Assert.Equal(ManagedChromiumLaunchStatus.WaitingForPage, waiting.Status);
        Assert.Null(waiting.Session);
        Assert.Equal(0, fixture.Processes.Process.Terminations);
        fixture.Targets.ThrowTimeout = true;
        Assert.Null((await manager.LaunchAsync(fixture.Installation, fixture.Profile)).Session);
        Assert.Equal(0, fixture.Processes.Process.Terminations);
        fixture.Targets.ThrowTimeout = false;
        fixture.Targets.Page = new Uri("https://pan.baidu.com/");
        Assert.NotNull((await manager.LaunchAsync(fixture.Installation, fixture.Profile)).Session);
        Assert.Equal(1, fixture.Processes.LaunchCount);
    }

    [Fact]
    public async Task ReconnectRejectsDifferentTargetAndNeverAdoptsAnotherOpenPort()
    {
        var fixture = await Fixture.CreateAsync();
        await using var manager = fixture.Manager;
        var first = (await manager.LaunchAsync(fixture.Installation, fixture.Profile)).Session!;
        fixture.Targets.TargetId = "another-tab";
        Assert.Null((await manager.ReconnectAsync(first)).Session);
        Assert.Equal(0, fixture.Processes.Process.Terminations);
        Assert.DoesNotContain(typeof(ManagedChromiumProcessManager).GetMethods(), method =>
            method.GetParameters().Any(parameter => parameter.ParameterType == typeof(Uri) || parameter.Name == "port"));
    }

    [Fact]
    public async Task UserClosedBrowserIsNotReattachedAndSameProfileCanRestart()
    {
        var fixture = await Fixture.CreateAsync();
        await using var manager = fixture.Manager;
        var first = (await manager.LaunchAsync(fixture.Installation, fixture.Profile)).Session!;
        fixture.Processes.Process.Alive = false;
        await Assert.ThrowsAsync<IOException>(() => manager.ReconnectAsync(first));
        fixture.Processes.Process = new FakeProcess();
        fixture.Executable = new FakeExecutable(fixture.Installation);
        fixture.ProfileLease = new FakeProfile(fixture.Profile);
        var restarted = await manager.LaunchAsync(fixture.Installation, fixture.Profile);
        Assert.NotSame(first, restarted.Session);
        Assert.Equal(2, fixture.Processes.LaunchCount);
    }

    [Fact]
    public async Task AsyncThenSynchronousDisposalGracefullyClosesAndTerminatesOwnedJobOnlyOnce()
    {
        var fixture = await Fixture.CreateAsync();
        await fixture.Manager.LaunchAsync(fixture.Installation, fixture.Profile);
        await fixture.Manager.DisposeAsync();
        fixture.Manager.Dispose();
        await fixture.Manager.DisposeAsync();
        Assert.Equal(1, fixture.Processes.Process.GracefulCloses);
        Assert.Equal(1, fixture.Processes.Process.Terminations);
        Assert.True(fixture.ProfileLease.Disposed);
        Assert.True(fixture.Executable.Disposed);
    }

    [Theory]
    [InlineData("simple", "\"simple\"")]
    [InlineData("", "\"\"")]
    [InlineData("a b", "\"a b\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    [InlineData("C:\\folder with space\\", "\"C:\\folder with space\\\\\"")]
    public void NativeArgumentEncodingQuotesWithoutShellInterpretation(string value, string expected) =>
        Assert.Equal(expected, WindowsJobObject.EncodeArgumentList([value]));

    private sealed class Fixture
    {
        public required ManagedBrowserProfileDescriptor Profile { get; init; }
        public BrowserInstallationIdentity Installation { get; } = new(Guid.NewGuid(), BrowserFamily.Chrome,
            @"C:\Browser", @"C:\Browser\chrome.exe", "Google LLC", "Google Chrome", "volume:file:hash");
        public required FakeProfile ProfileLease { get; set; }
        public FakeExecutable Executable { get; set; } = null!;
        public FakeProcesses Processes { get; } = new();
        public FakeListeners Listeners { get; } = new();
        public FakeTargets Targets { get; } = new();
        public ManagedChromiumProcessManager Manager { get; private set; } = null!;
        public static async Task<Fixture> CreateAsync()
        {
            var policy = new FileSystemManagedBrowserProfilePolicy(Path.Combine(Path.GetTempPath(), "task10-" + Guid.NewGuid().ToString("N")));
            var profile = (await policy.GetOrCreateAsync(Guid.NewGuid(), BrowserFamily.Chrome)).Profile!;
            Assert.NotNull(profile);
            var fixture = new Fixture { Profile = profile, ProfileLease = new FakeProfile(profile) };
            fixture.Executable = new FakeExecutable(fixture.Installation);
            fixture.Manager = new((_, _) => Task.FromResult<IBrowserExecutableLaunchLease>(fixture.Executable),
                (_, _) => Task.FromResult<IManagedBrowserProfileLease>(fixture.ProfileLease), fixture.Processes,
                fixture.Listeners, fixture.Targets, TimeSpan.FromMilliseconds(50));
            return fixture;
        }
    }

    private sealed class FakeExecutable(BrowserInstallationIdentity identity) : IBrowserExecutableLaunchLease
    {
        public bool Valid = true, Disposed;
        public BrowserInstallationIdentity Identity => identity;
        public bool Validate() => Valid && !Disposed;
        public bool MatchesExecutablePath(string path) => Validate() && path == identity.ExecutablePath;
        public void Dispose() => Disposed = true;
    }
    private sealed class FakeProfile(ManagedBrowserProfileDescriptor profile) : IManagedBrowserProfileLease
    {
        public bool Valid = true, PortValid = true, Disposed;
        public ManagedBrowserProfileDescriptor Profile => profile;
        public bool Validate() => Valid && !Disposed;
        public IManagedBrowserPortFileLease? OpenDevToolsActivePort() => new PortFile(this);
        public void Dispose() => Disposed = true;
        private sealed class PortFile(FakeProfile profile) : IManagedBrowserPortFileLease
        {
            public DateTimeOffset LastWriteTimeUtc => DateTimeOffset.UtcNow;
            public byte[] ReadBounded(int maximumBytes) => Encoding.ASCII.GetBytes($"49152\n/devtools/browser/{FakeTargets.FixedBrowserId:D}");
            public bool Validate() => profile.PortValid && profile.Validate();
            public void Dispose() { }
        }
    }
    private sealed class FakeProcesses : IManagedChromiumProcessFactory
    {
        public FakeProcess Process = new();
        public int LaunchCount;
        public Action? OnLaunch;
        public bool FailWithOwnedProcess;
        public IManagedChromiumProcess Launch(IBrowserExecutableLaunchLease executable, IManagedBrowserProfileLease profile)
        {
            LaunchCount++; OnLaunch?.Invoke();
            if (FailWithOwnedProcess) throw new OwnedChromiumLaunchException(Process);
            return Process;
        }
    }
    private sealed class FakeProcess : IManagedChromiumProcess
    {
        public int RootProcessId => 123;
        public DateTimeOffset StartedAtUtc => DateTimeOffset.UtcNow.AddSeconds(-1);
        public bool Alive = true, Owned = true, ExecutableMatches = true, ExitConfirmed = true;
        public int AdditionalOwnedPid;
        public bool IsAlive => Alive;
        public int Terminations, GracefulCloses;
        public bool OwnsProcess(int processId) => Owned && (processId == 123 || processId == AdditionalOwnedPid);
        public bool VerifyExecutable(int processId, IBrowserExecutableLaunchLease executable) => ExecutableMatches && executable.Validate();
        public Task CloseGracefullyAsync(TimeSpan timeout, CancellationToken cancellationToken) { GracefulCloses++; return Task.CompletedTask; }
        public void Terminate() { Terminations++; Alive = false; }
        public bool WaitForExit(TimeSpan timeout) => ExitConfirmed;
        public void Dispose() { }
    }
    private sealed class FakeListeners : IWindowsTcpOwnerTable
    {
        public int Owner = 123, Count = 1;
        public IReadOnlyList<TcpListenerOwner> ReadListeners() => Enumerable.Range(0, Count).Select(_ => new TcpListenerOwner(IPAddress.Loopback, 49152, Owner)).ToArray();
    }
    private sealed class FakeTargets : IManagedBrowserTargetProbe
    {
        public static readonly Guid FixedBrowserId = Guid.Parse("76df3c18-1013-45fd-a989-2fbb60477286");
        public Guid BrowserId = FixedBrowserId;
        public string TargetId = "fixed-tab";
        public Uri Page = new("https://pan.baidu.com/");
        public Action? OnRead;
        public bool ThrowTimeout;
        public Task<ManagedBrowserTargetSnapshot?>? Pending;
        public Action<string>? LatestSelection;
        public Task<ManagedBrowserTargetSnapshot?> ReadAsync(DevToolsActivePort port, string? exactTargetId, CancellationToken cancellationToken,
            Action<string>? targetDiscovered = null)
        {
            OnRead?.Invoke();
            LatestSelection = targetDiscovered;
            if (Pending is not null) return Pending;
            if (ThrowTimeout) throw new TimeoutException();
            return Task.FromResult<ManagedBrowserTargetSnapshot?>(new(BrowserId, TargetId, Page));
        }
    }

    private sealed class FakeNative : IWindowsChromiumNativeApi
    {
        public List<string> Events = [];
        public IReadOnlyList<string> Arguments = [];
        public bool AssignSucceeds = true;
        private bool alive = true;
        public SafeFileHandle Job = new(new IntPtr(1), false);
        public SafeProcessHandle Root = new(new IntPtr(2), false);
        public SafeWaitHandle Thread = new(new IntPtr(3), false);
        public SafeFileHandle CreateJob() { Events.Add("job"); return Job; }
        public SuspendedChromiumProcess CreateSuspended(string executable, IReadOnlyList<string> arguments)
        { Events.Add("suspended"); Arguments = arguments; return new(Root, Thread, 123, DateTimeOffset.UtcNow); }
        public bool Assign(SafeFileHandle job, SafeProcessHandle process) { Events.Add("assign"); return AssignSucceeds; }
        public bool Resume(SafeWaitHandle thread) { Events.Add("resume"); return true; }
        public bool IsAlive(SafeProcessHandle process) => alive;
        public bool IsInJob(SafeFileHandle job, SafeProcessHandle process) => AssignSucceeds;
        public bool IsJobEmpty(SafeFileHandle job) => true;
        public string ReadImagePath(SafeProcessHandle process) => @"C:\Browser\chrome.exe";
        public SafeProcessHandle OpenProcess(int processId) => new(new IntPtr(4), false);
        public void CloseWindows(SafeFileHandle job) { }
        public void TerminateJob(SafeFileHandle job) { Events.Add("terminate-job"); alive = false; }
        public void TerminateProcess(SafeProcessHandle process) { Events.Add("terminate-root"); alive = false; }
    }

    private sealed class FakeTargetTransport(DevToolsActivePort port, string scenario) : IChromiumTargetTransport
    {
        public List<string> Commands = [];
        public Task<JsonDocument> VersionAsync(DevToolsActivePort _, CancellationToken token) =>
            Task.FromResult(JsonDocument.Parse(JsonSerializer.Serialize(new { webSocketDebuggerUrl = scenario == "browser-mismatch"
                ? $"ws://127.0.0.1:49152/devtools/browser/{Guid.NewGuid():D}" : port.WebSocketEndpoint.AbsoluteUri })));
        public Task<JsonDocument?> CommandAsync(DevToolsActivePort _, string method, string? targetId, CancellationToken token)
        {
            Commands.Add(method);
            var page = new { type = "page", targetId = "fixed-tab", url = "https://pan.baidu.com/" };
            var body = method == "Target.getTargets"
                ? JsonSerializer.Serialize(new { result = new { targetInfos = scenario == "ambiguous-pages" ? new[] { page, page } : new[] { page } } })
                : JsonSerializer.Serialize(new { result = new { targetInfo = new { type = "page", targetId = scenario == "changed-target" ? "other-tab" : "fixed-tab", url = "https://pan.baidu.com/" } } });
            return Task.FromResult<JsonDocument?>(JsonDocument.Parse(body));
        }
    }
}

public sealed class Task10BrowserFactAttribute : FactAttribute
{
    public Task10BrowserFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("BAIDU_BATCH_REQUIRE_TASK10_BROWSER") != "1")
            Skip = "Opt-in real browser smoke; run only with an explicitly approved browser directory.";
    }
}
