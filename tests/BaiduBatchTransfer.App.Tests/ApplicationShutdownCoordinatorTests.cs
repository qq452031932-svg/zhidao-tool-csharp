using BaiduBatchTransfer.App;

namespace BaiduBatchTransfer.App.Tests;

public sealed class ApplicationShutdownCoordinatorTests
{
    [Fact]
    public void ApplicationOwnsApplicationStoppingSubscription()
    {
        var field = Assert.Single(typeof(App).GetFields(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic),
            candidate => candidate.FieldType == typeof(ApplicationStoppingShutdownCoordinator));

        Assert.False(field.IsStatic);
    }

    [Fact]
    public async Task ApplicationStoppingDispatchesOneAsynchronousShutdownAndCompletesExit()
    {
        using var stopping = new CancellationTokenSource();
        var shutdownRequested = 0;
        var dispatchCalls = 0;
        var shutdownCalls = 0;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = new ApplicationStoppingShutdownCoordinator(
            stopping.Token,
            () => Interlocked.Exchange(ref shutdownRequested, 1) == 0,
            async operation =>
            {
                dispatchCalls++;
                await operation();
            },
            () =>
            {
                shutdownCalls++;
                return Task.FromResult(true);
            },
            () => completed.TrySetResult(),
            () => throw new Xunit.Sdk.XunitException("Successful shutdown must not fail closed."));

        stopping.Cancel();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, dispatchCalls);
        Assert.Equal(1, shutdownCalls);
    }

    [Fact]
    public void ApplicationStoppingDoesNotRepeatAnAlreadyRequestedShutdown()
    {
        using var stopping = new CancellationTokenSource();
        var dispatchCalls = 0;
        using var coordinator = new ApplicationStoppingShutdownCoordinator(
            stopping.Token,
            () => false,
            operation =>
            {
                dispatchCalls++;
                return operation();
            },
            () => throw new Xunit.Sdk.XunitException("Shutdown must not be repeated."),
            () => throw new Xunit.Sdk.XunitException("Exit must not be repeated."),
            () => throw new Xunit.Sdk.XunitException("Fail-closed must not be repeated."));

        stopping.Cancel();

        Assert.Equal(0, dispatchCalls);
    }

    [Fact]
    public async Task ApplicationStoppingFailsClosedWhenSafeShutdownCannotComplete()
    {
        using var stopping = new CancellationTokenSource();
        var failedClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = new ApplicationStoppingShutdownCoordinator(
            stopping.Token,
            () => true,
            operation => operation(),
            () => Task.FromResult(false),
            () => throw new Xunit.Sdk.XunitException("Incomplete shutdown must not complete normally."),
            () => failedClosed.TrySetResult());

        stopping.Cancel();

        await failedClosed.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ManagedBrowserStopsAfterQueueFencesAndBeforeHostDisposal()
    {
        var events = new List<string>();
        var coordinator = new ApplicationShutdownCoordinator(new RecordingAsyncDisposable(events),
            _ => { events.Add("host-stopped"); return Task.CompletedTask; }, new RecordingDisposable(events),
            new BrowserShutdown(events));
        Assert.True(await coordinator.ShutdownAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(["queue-disposed", "browser-stopped", "host-stopped", "host-disposed"], events);
    }

    [Fact]
    public async Task UnconfirmedBrowserExitKeepsHostAliveAndAllowsRetry()
    {
        var events = new List<string>();
        var browser = new BrowserShutdown(events) { Fail = true };
        var coordinator = new ApplicationShutdownCoordinator(new RecordingAsyncDisposable(events),
            _ => { events.Add("host-stopped"); return Task.CompletedTask; }, new RecordingDisposable(events), browser);
        Assert.False(await coordinator.ShutdownAsync(TimeSpan.FromSeconds(1)));
        Assert.DoesNotContain("host-disposed", events);
        browser.Fail = false;
        Assert.True(await coordinator.ShutdownAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(1, events.Count(value => value == "queue-disposed"));
    }

    [Fact]
    public void ApplicationRegistersManagedProcessManagerAndPassesItToRealShutdownCoordinator()
    {
        var root = FindRepositoryRoot();
        var bootstrap = File.ReadAllText(Path.Combine(root, "src", "BaiduBatchTransfer.App", "AppBootstrap.cs"));
        var app = File.ReadAllText(Path.Combine(root, "src", "BaiduBatchTransfer.App", "App.xaml.cs"));
        Assert.Contains("AddSingleton<ManagedChromiumProcessManager>", bootstrap, StringComparison.Ordinal);
        Assert.Contains("GetRequiredService<ManagedChromiumProcessManager>()", app, StringComparison.Ordinal);
    }

    private sealed class BrowserShutdown(List<string> events) : IAsyncDisposable
    {
        public bool Fail;
        public ValueTask DisposeAsync()
        {
            if (Fail) throw new IOException("Unconfirmed owned job exit.");
            events.Add("browser-stopped");
            return ValueTask.CompletedTask;
        }
    }
    [Fact]
    public void ApplicationDefersMainWindowCloseUntilAsyncCoordinatorCompletes()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "BaiduBatchTransfer.App", "App.xaml.cs"));

        Assert.Contains("MainWindow.Closing += OnMainWindowClosing", source, StringComparison.Ordinal);
        Assert.Contains("e.Cancel = true", source, StringComparison.Ordinal);
        Assert.Contains("await updateShutdownOrchestrator", source, StringComparison.Ordinal);
        Assert.Contains(".ShutdownAsync(CancellationToken.None)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetAwaiter().GetResult()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplicationFlushesShareAnalyticsPreferenceBeforeHostShutdown()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "BaiduBatchTransfer.App", "App.xaml.cs"));

        var frozen = source.IndexOf("MainWindow.IsEnabled = false", StringComparison.Ordinal);
        var flush = source.IndexOf("SealAndFlushShareAnalyticsPreferenceAsync", StringComparison.Ordinal);
        var shutdown = source.IndexOf("await coordinator", StringComparison.Ordinal);
        Assert.True(frozen >= 0 && frozen < flush, "The window must be frozen before the final preference is sealed.");
        Assert.True(flush >= 0, "The normal close flow must flush the last account preference.");
        Assert.True(shutdown > flush, "The preference flush must finish before host shutdown/disposal starts.");
        Assert.Contains("ResumeShareAnalyticsPreferenceAfterFailedShutdown", source, StringComparison.Ordinal);
        Assert.Contains("MainWindow.IsEnabled = true", source, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromSeconds(2)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplicationDelegatesCloseToUpdateAwareOrchestratorAndOwnsRuntimeUntilExit()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "BaiduBatchTransfer.App", "App.xaml.cs"));

        Assert.Contains("UpdateAwareShutdownOrchestrator", source, StringComparison.Ordinal);
        Assert.Contains("updateRuntime.LaunchPreparedUpdateAndRestart", source, StringComparison.Ordinal);
        Assert.Contains("await updateShutdownOrchestrator", source, StringComparison.Ordinal);
        Assert.Contains(".ShutdownAsync(CancellationToken.None)", source, StringComparison.Ordinal);
        Assert.Contains("updateRuntime.Dispose()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("UnsafeApplyUpdates", source, StringComparison.Ordinal);

        var safeShutdown = source.IndexOf("ShutdownSafelyAsync", StringComparison.Ordinal);
        var launchPrepared = source.IndexOf("updateRuntime.LaunchPreparedUpdateAndRestart", StringComparison.Ordinal);
        Assert.True(safeShutdown >= 0 && safeShutdown < launchPrepared,
            "The complete safe-shutdown delegate must be wired before the updater launch delegate.");
    }

    [Fact]
    public async Task QueueIsDisposedBeforeHostStopsOrDependenciesAreDisposed()
    {
        var events = new List<string>();
        var queue = new RecordingAsyncDisposable(events);
        var host = new RecordingDisposable(events);
        var coordinator = new ApplicationShutdownCoordinator(
            queue,
            _ =>
            {
                events.Add("host-stopped");
                return Task.CompletedTask;
            },
            host);

        Assert.True(await coordinator.ShutdownAsync(TimeSpan.FromSeconds(1)));

        Assert.Equal(["queue-disposed", "host-stopped", "host-disposed"], events);
    }

    [Fact]
    public async Task ConcurrentShutdownRequestsRunTheSequenceOnlyOnce()
    {
        var events = new List<string>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new RecordingAsyncDisposable(events, release.Task);
        var host = new RecordingDisposable(events);
        var coordinator = new ApplicationShutdownCoordinator(
            queue,
            _ =>
            {
                events.Add("host-stopped");
                return Task.CompletedTask;
            },
            host);

        var first = coordinator.ShutdownAsync(TimeSpan.FromSeconds(2));
        var duplicate = coordinator.ShutdownAsync(TimeSpan.FromSeconds(2));
        release.TrySetResult();

        Assert.True(await first);
        Assert.True(await duplicate);
        Assert.Equal(1, queue.DisposeCalls);
        Assert.Equal(1, host.DisposeCalls);
    }

    [Fact]
    public async Task TimeoutKeepsHostAliveUntilQueueHasPersistedAndRetryCompletes()
    {
        var events = new List<string>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new RecordingAsyncDisposable(events, release.Task);
        var host = new RecordingDisposable(events);
        var coordinator = new ApplicationShutdownCoordinator(
            queue,
            _ =>
            {
                events.Add("host-stopped");
                return Task.CompletedTask;
            },
            host);

        Assert.False(await coordinator.ShutdownAsync(TimeSpan.FromMilliseconds(20)));
        Assert.Equal(0, host.DisposeCalls);
        Assert.DoesNotContain("host-stopped", events);

        events.Add("safe-state-persisted");
        release.TrySetResult();
        Assert.True(await coordinator.ShutdownAsync(TimeSpan.FromSeconds(1)));
        Assert.True(events.IndexOf("safe-state-persisted") < events.IndexOf("host-disposed"));
    }

    [Fact]
    public async Task HostStopFailureStillDisposesHostAfterQueueShutdown()
    {
        var events = new List<string>();
        var coordinator = new ApplicationShutdownCoordinator(
            new RecordingAsyncDisposable(events),
            _ =>
            {
                events.Add("host-stop-failed");
                throw new InvalidOperationException("synthetic stop failure");
            },
            new RecordingDisposable(events));

        Assert.True(await coordinator.ShutdownAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(["queue-disposed", "host-stop-failed", "host-disposed"], events);
    }

    private sealed class RecordingAsyncDisposable(List<string> events, Task? release = null) : IAsyncDisposable
    {
        public int DisposeCalls { get; private set; }

        public async ValueTask DisposeAsync()
        {
            DisposeCalls++;
            if (release is not null) await release;
            events.Add("queue-disposed");
        }
    }

    private sealed class RecordingDisposable(List<string> events) : IDisposable
    {
        public int DisposeCalls { get; private set; }

        public void Dispose()
        {
            DisposeCalls++;
            events.Add("host-disposed");
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "BaiduBatchTransfer.App")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
