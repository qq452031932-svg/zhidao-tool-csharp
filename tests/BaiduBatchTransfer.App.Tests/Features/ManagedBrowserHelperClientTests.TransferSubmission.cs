using BaiduBatchTransfer.App.Features.ManagedBrowser;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed partial class ManagedBrowserHelperClientTests
{
    [Fact]
    public async Task SubmitPrecancellationIsKnownNotDispatchedAndWritesNoFrame()
    {
        var process = new FakeProcess();
        await using var client = await Start(process);
        var before = process.Requests.Count;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var response = await client.SendAsync(SubmitRequest(1_000), cancelled.Token);

        var result = Assert.IsType<ManagedBrowserSubmitTransferOnceResult>(response.Result);
        Assert.Equal(ManagedBrowserTransferSubmitResultKind.Cancelled, result.Kind);
        Assert.Equal(ManagedBrowserTransferSubmitDispatchState.KnownNotDispatched, result.DispatchState);
        Assert.Equal(before, process.Requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SubmitInterruptionAfterWriteSettlesAsDispatchOutcomeUnknownWithoutRetry(bool cancel)
    {
        var process = new FakeProcess();
        await using var client = await Start(process);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OnRequest = _ => gate.Task;
        using var cancellation = new CancellationTokenSource();
        var pending = client.SendAsync(SubmitRequest(cancel ? 2_000 : 20), cancellation.Token);
        try
        {
            await Until(() => process.Requests.Count == 2);
            if (cancel) cancellation.Cancel();
            await Task.Delay(150);
            Assert.False(pending.IsCompleted);
            Assert.True(process.Killed);
            gate.TrySetResult();
            var result = Assert.IsType<ManagedBrowserSubmitTransferOnceResult>(
                (await pending.WaitAsync(TimeSpan.FromSeconds(2))).Result);
            Assert.Equal(cancel ? ManagedBrowserTransferSubmitResultKind.Cancelled : ManagedBrowserTransferSubmitResultKind.TimedOut,
                result.Kind);
            Assert.Equal(ManagedBrowserTransferSubmitDispatchState.DispatchOutcomeUnknown, result.DispatchState);
            Assert.Single(process.Requests, request => request.Action == ManagedBrowserAction.SubmitTransferOnce);
        }
        finally { gate.TrySetResult(); process.OnRequest = null; }
    }

    [Fact]
    public async Task ReadinessAndOutcomeUseTheirBoundedTimeoutsAndReturnTypedTerminals()
    {
        var process = new FakeProcess();
        await using var client = await Start(process);
        process.OnRequest = _ => Task.CompletedTask;

        var ready = await client.SendAsync(ReadinessRequest(20), CancellationToken.None);
        Assert.Equal(ManagedBrowserTransferSubmissionReadinessResultKind.TimedOut,
            Assert.IsType<ManagedBrowserTransferSubmissionReadinessResult>(ready.Result).Kind);
    }

    private static ManagedBrowserRequest ReadinessRequest(int timeout)
    {
        var context = SubmissionContext(timeout);
        return new ManagedBrowserRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1,
            ManagedBrowserAction.ObserveTransferSubmissionReady,
            new ManagedBrowserObserveTransferSubmissionReadyRequest(context));
    }

    private static ManagedBrowserRequest SubmitRequest(int timeout)
    {
        var context = SubmissionContext(timeout);
        var readiness = new ManagedBrowserTransferSubmissionReadiness(context, 1);
        return new ManagedBrowserRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1,
            ManagedBrowserAction.SubmitTransferOnce,
            new ManagedBrowserSubmitTransferOnceRequest(context, readiness.ReadinessFingerprint));
    }

    private static ManagedBrowserTransferSubmissionContext SubmissionContext(int timeout) => new(
        Guid.Parse("44444444-4444-4444-4444-444444444444"), "target-1",
        new Uri("http://127.0.0.1:43127/"), timeout,
        "https://pan.baidu.com/s/source_1?pwd=A1b2", "uid-1", "source-generation-1",
        new string('a', 64), new string('b', 64), "source-1", ManagedBrowserSourceItemKind.File,
        "selected-target-1", "/prepared", new string('c', 64), DuplicatePolicy.Skip);
}
