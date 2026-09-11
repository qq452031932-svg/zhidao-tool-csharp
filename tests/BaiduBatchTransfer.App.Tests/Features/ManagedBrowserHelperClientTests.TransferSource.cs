using BaiduBatchTransfer.App.Features.ManagedBrowser;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed partial class ManagedBrowserHelperClientTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceHardTimeoutReturnsTypedResultWithoutReplayingNavigation(bool stallWrite)
    {
        var process = new FakeProcess();
        var lease = new FakeLease();
        await using var client = await Start(process, lease);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OnRequest = _ => stallWrite ? gate.Task : Task.CompletedTask;
        var request = SourceRequest(60);
        try
        {
            var response = await client.SendAsync(request, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(ManagedBrowserTransferSourceResultKind.TimedOut,
                Assert.IsType<ManagedBrowserTransferSourceResult>(response.Result).Kind);
            ManagedBrowserProtocol.ValidateResponseFor(request, response);
            await Until(() => process.Killed);
            Assert.Single(process.Requests, r => r.Action == ManagedBrowserAction.OpenTransferSource);
            if (stallWrite) Assert.False(lease.Released);
        }
        finally { gate.TrySetResult(); }
        await Until(() => lease.Released);
    }

    [Fact]
    public async Task SourceUsesItsOwnLongerBudgetAndReturnsHint()
    {
        var process = new FakeProcess();
        await using var client = await Start(process);
        process.OnRequest = async request =>
        {
            await Task.Delay(250);
            process.Emit(ManagedBrowserResponse.For(request,
                new ManagedBrowserTransferSourceResult(ManagedBrowserTransferSourceResultKind.ExtractionRequired)));
        };
        var request = SourceRequest(2000);
        var response = await client.SendAsync(request, CancellationToken.None);
        Assert.Equal(ManagedBrowserTransferSourceResultKind.ExtractionRequired,
            Assert.IsType<ManagedBrowserTransferSourceResult>(response.Result).Kind);
        ManagedBrowserProtocol.ValidateResponseFor(request, response);
        Assert.False(process.Killed);
        process.OnRequest = null;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceCancellationIsTypedAndNeverReplayed(bool preCancelled)
    {
        var process = new FakeProcess();
        await using var client = await Start(process);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OnRequest = _ => gate.Task;
        using var cancellation = new CancellationTokenSource();
        if (preCancelled) cancellation.Cancel();
        var request = SourceRequest(2000);
        var pending = client.SendAsync(request, cancellation.Token);
        if (!preCancelled)
        {
            await Until(() => process.Requests.Count == 2);
            cancellation.Cancel();
        }
        try
        {
            var response = await pending.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(ManagedBrowserTransferSourceResultKind.Cancelled,
                Assert.IsType<ManagedBrowserTransferSourceResult>(response.Result).Kind);
            ManagedBrowserProtocol.ValidateResponseFor(request, response);
            Assert.Equal(preCancelled ? 0 : 1,
                process.Requests.Count(r => r.Action == ManagedBrowserAction.OpenTransferSource));
            if (!preCancelled) await Until(() => process.Killed);
            else Assert.False(process.Killed);
        }
        finally { gate.TrySetResult(); process.OnRequest = null; }
    }

    private static ManagedBrowserRequest SourceRequest(int timeout) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 7, ManagedBrowserAction.OpenTransferSource,
            new ManagedBrowserOpenTransferSourceRequest(Guid.NewGuid(), "synthetic-target",
                new Uri("http://127.0.0.1:9222/"), timeout,
                "https://pan.baidu.com/s/synthetic?pwd=a1B2", "synthetic-uid"));
}
