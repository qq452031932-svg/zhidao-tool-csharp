using BaiduBatchTransfer.App;

namespace BaiduBatchTransfer.App.Tests;

public sealed class ApplicationShutdownCoordinatorTests
{
    [Fact]
    public void ApplicationDefersMainWindowCloseUntilAsyncCoordinatorCompletes()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "BaiduBatchTransfer.App", "App.xaml.cs"));

        Assert.Contains("MainWindow.Closing += OnMainWindowClosing", source, StringComparison.Ordinal);
        Assert.Contains("e.Cancel = true", source, StringComparison.Ordinal);
        Assert.Contains("await shutdownCoordinator", source, StringComparison.Ordinal);
        Assert.Contains(".ShutdownAsync(TimeSpan.FromSeconds(15))", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetAwaiter().GetResult()", source, StringComparison.Ordinal);
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
