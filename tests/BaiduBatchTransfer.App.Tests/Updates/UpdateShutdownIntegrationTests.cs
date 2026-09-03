using BaiduBatchTransfer.App.Updates;

namespace BaiduBatchTransfer.App.Tests.Updates;

public sealed class UpdateShutdownIntegrationTests
{
    [Fact]
    public async Task PreparedUpdateLaunchesOnlyAfterTheCompleteSafeShutdownSequence()
    {
        var events = new List<string>();
        var orchestrator = CreateOrchestrator(
            events,
            SafeShutdown(events),
            launchPreparedUpdate: () =>
            {
                events.Add("velopack-wait-exit-then-apply");
                return true;
            });

        Assert.True(await orchestrator.ShutdownAsync());

        Assert.Equal(
            [
                "preference-flushed",
                "queue-disposed",
                "browser-stopped",
                "host-stopped",
                "host-disposed",
                "velopack-wait-exit-then-apply",
                "application-exit"
            ],
            events);
    }

    [Theory]
    [InlineData("preference-flushed")]
    [InlineData("queue-disposed")]
    [InlineData("browser-stopped")]
    public async Task SafeShutdownFailureNeverLaunchesUpdateAndRestoresWindow(string failurePoint)
    {
        var events = new List<string>();
        var applyCalls = 0;
        var orchestrator = CreateOrchestrator(
            events,
            FailedSafeShutdown(events, failurePoint),
            launchPreparedUpdate: () =>
            {
                applyCalls++;
                return true;
            });

        Assert.False(await orchestrator.ShutdownAsync());

        Assert.Equal(0, applyCalls);
        Assert.Equal("window-restored", events[^1]);
        Assert.DoesNotContain("application-exit", events);
    }

    [Fact]
    public async Task OrdinaryCloseDoesNotApplyAnUpdate()
    {
        var events = new List<string>();
        var orchestrator = CreateOrchestrator(
            events,
            SafeShutdown(events),
            launchPreparedUpdate: () => false);

        Assert.True(await orchestrator.ShutdownAsync());

        Assert.DoesNotContain("velopack-wait-exit-then-apply", events);
        Assert.Equal("application-exit", events[^1]);
    }

    [Fact]
    public async Task ConcurrentCloseRequestsLaunchAndExitAtMostOnce()
    {
        var events = new List<string>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var applyCalls = 0;
        var exitCalls = 0;
        var orchestrator = new UpdateAwareShutdownOrchestrator(
            async _ =>
            {
                entered.TrySetResult();
                await release.Task;
                return true;
            },
            () =>
            {
                Interlocked.Increment(ref applyCalls);
                return true;
            },
            () => events.Add("window-restored"),
            _ => events.Add("safe-error"),
            () => Interlocked.Increment(ref exitCalls));

        var first = orchestrator.ShutdownAsync();
        await entered.Task;
        var duplicate = orchestrator.ShutdownAsync();
        release.TrySetResult();

        Assert.True(await first);
        Assert.False(await duplicate);
        Assert.Equal(1, applyCalls);
        Assert.Equal(1, exitCalls);
    }

    [Fact]
    public async Task ApplyLaunchFailureShowsSafeErrorAndExitsWithoutRestoringDisposedUi()
    {
        var events = new List<string>();
        var applyCalls = 0;
        var orchestrator = CreateOrchestrator(
            events,
            SafeShutdown(events),
            launchPreparedUpdate: () =>
            {
                applyCalls++;
                events.Add("velopack-wait-exit-then-apply");
                throw new InvalidOperationException("synthetic updater launch failure");
            });

        Assert.True(await orchestrator.ShutdownAsync());
        Assert.False(await orchestrator.ShutdownAsync());

        Assert.Equal(1, applyCalls);
        Assert.DoesNotContain("window-restored", events);
        Assert.Contains("safe-error:更新程序未能启动，本机数据和当前版本未被删除。", events);
        Assert.Equal("application-exit", events[^1]);
    }

    [Fact]
    public async Task ExceptionBeforeSafeShutdownCompletesRestoresWindowAndDoesNotExit()
    {
        var events = new List<string>();
        var orchestrator = CreateOrchestrator(
            events,
            _ => throw new InvalidOperationException("synthetic safe shutdown failure"),
            launchPreparedUpdate: () => throw new InvalidOperationException("must not run"));

        Assert.False(await orchestrator.ShutdownAsync());

        Assert.Equal(["window-restored"], events);
    }

    [Fact]
    public async Task SafeShutdownContinuationReturnsToCapturedContextBeforeRestoringWindow()
    {
        var previous = SynchronizationContext.Current;
        var context = new CallbackTrackingSynchronizationContext();
        var restoredOnCapturedContext = false;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            var orchestrator = new UpdateAwareShutdownOrchestrator(
                async _ =>
                {
                    await Task.Delay(1).ConfigureAwait(false);
                    return false;
                },
                () => false,
                () => restoredOnCapturedContext = context.IsExecutingCallback,
                _ => { },
                () => { });

            Assert.False(await orchestrator.ShutdownAsync());
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        Assert.True(restoredOnCapturedContext);
    }

    [Fact]
    public async Task ThrowingWindowRestoreRunsOnceDoesNotEscapeAndLeavesShutdownRetryable()
    {
        var safeShutdownCalls = 0;
        var restoreCalls = 0;
        var applyCalls = 0;
        var exitCalls = 0;
        var orchestrator = new UpdateAwareShutdownOrchestrator(
            _ => Task.FromResult(Interlocked.Increment(ref safeShutdownCalls) > 1),
            () =>
            {
                applyCalls++;
                return true;
            },
            () =>
            {
                restoreCalls++;
                throw new InvalidOperationException("synthetic restore failure");
            },
            _ => { },
            () => exitCalls++);

        Assert.False(await orchestrator.ShutdownAsync());
        Assert.Equal(1, restoreCalls);
        Assert.Equal(0, applyCalls);
        Assert.Equal(0, exitCalls);

        Assert.True(await orchestrator.ShutdownAsync());
        Assert.Equal(2, safeShutdownCalls);
        Assert.Equal(1, restoreCalls);
        Assert.Equal(1, applyCalls);
        Assert.Equal(1, exitCalls);
    }

    private static UpdateAwareShutdownOrchestrator CreateOrchestrator(
        List<string> events,
        Func<CancellationToken, Task<bool>> safeShutdown,
        Func<bool> launchPreparedUpdate) => new(
            safeShutdown,
            launchPreparedUpdate,
            () => events.Add("window-restored"),
            message => events.Add($"safe-error:{message}"),
            () => events.Add("application-exit"));

    private static Func<CancellationToken, Task<bool>> SafeShutdown(List<string> events) => _ =>
    {
        events.Add("preference-flushed");
        events.Add("queue-disposed");
        events.Add("browser-stopped");
        events.Add("host-stopped");
        events.Add("host-disposed");
        return Task.FromResult(true);
    };

    private static Func<CancellationToken, Task<bool>> FailedSafeShutdown(
        List<string> events,
        string failurePoint) => _ =>
    {
        events.Add("preference-flushed");
        if (failurePoint == "preference-flushed")
            return Task.FromResult(false);

        events.Add("queue-disposed");
        if (failurePoint == "queue-disposed")
            return Task.FromResult(false);

        events.Add("browser-stopped");
        return Task.FromResult(failurePoint != "browser-stopped");
    };

    private sealed class CallbackTrackingSynchronizationContext : SynchronizationContext
    {
        public bool IsExecutingCallback { get; private set; }

        public override void Post(SendOrPostCallback d, object? state)
        {
            IsExecutingCallback = true;
            try
            {
                d(state);
            }
            finally
            {
                IsExecutingCallback = false;
            }
        }
    }
}
