using BaiduBatchTransfer.App.Features.ManagedBrowser;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed partial class ManagedBrowserHelperClientTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task PickerMutationDoesNotReturnEarlyAfterDispatch(bool cancel)
    {
        var process = new FakeProcess();
        await using var client = await Start(process);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OnRequest = async request =>
        {
            await gate.Task;
            process.Emit(ManagedBrowserResponse.For(request,
                new ManagedBrowserTransferPickerResult(ManagedBrowserTransferPickerResultKind.PickerOpened)));
        };
        using var cancellation = new CancellationTokenSource();
        var request = PickerRequest(cancel ? 2000 : 20);
        var pending = client.SendAsync(request, cancellation.Token);
        try
        {
            await Until(() => process.Requests.Count == 2);
            if (cancel) cancellation.Cancel();
            await Task.Delay(180);
            Assert.False(pending.IsCompleted);
            Assert.True(process.Killed);
            gate.TrySetResult();
            var response = await pending.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(ManagedBrowserTransferPickerResultKind.OutcomeUnknown,
                Assert.IsType<ManagedBrowserTransferPickerResult>(response.Result).Kind);
            Assert.Single(process.Requests, r => r.Action == ManagedBrowserAction.OpenTransferPicker);
        }
        finally { gate.TrySetResult(); process.OnRequest = null; }
    }

    [Fact]
    public async Task PickerPrecancellationDoesNotDispatch()
    {
        var process = new FakeProcess();
        await using var client = await Start(process);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var response = await client.SendAsync(PickerRequest(1000), cancellation.Token);
        Assert.Equal(ManagedBrowserTransferPickerResultKind.Cancelled,
            Assert.IsType<ManagedBrowserTransferPickerResult>(response.Result).Kind);
        Assert.DoesNotContain(process.Requests, r => r.Action == ManagedBrowserAction.OpenTransferPicker);
    }

    [Fact]
    public async Task PickerInterruptedTransportWaitsForPendingWriteAndReturnsUncertainty()
    {
        var process = new FakeProcess();
        await using var client = await Start(process);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OnRequest = _ => gate.Task;
        var pending = client.SendAsync(PickerRequest(1000), CancellationToken.None);
        try
        {
            await Until(() => process.Requests.Count == 2);
            process.Exit();
            await Task.Delay(30);
            Assert.False(pending.IsCompleted);
            gate.TrySetResult();
            var response = await pending.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(ManagedBrowserTransferPickerResultKind.OutcomeUnknown,
                Assert.IsType<ManagedBrowserTransferPickerResult>(response.Result).Kind);
        }
        finally { gate.TrySetResult(); process.OnRequest = null; }
    }

    private static ManagedBrowserRequest PickerRequest(int timeout)
    {
        var json = ManagedBrowserTransferPickerTests.Request();
        json["protocolVersion"] = 3;
        json["executionEngineVersion"] = "ManagedChromium-v3";
        json["payload"]!["timeoutMilliseconds"] = timeout;
        return ManagedBrowserProtocol.DecodeRequest(ManagedBrowserTransferPickerTests.Bytes(json));
    }

    [Fact]
    public async Task PickerConcurrentDisposeCannotReleasePendingMutationWrite()
    {
        var process = new FakeProcess();
        await using var client = await Start(process);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OnRequest = _ => gate.Task;
        var pending = client.SendAsync(PickerRequest(2000), CancellationToken.None);
        try
        {
            await Until(() => process.Requests.Count == 2);
            var closing = client.DisposeAsync().AsTask();
            await Task.Delay(30);
            Assert.False(pending.IsCompleted);
            gate.TrySetResult();
            await closing.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(ManagedBrowserTransferPickerResultKind.OutcomeUnknown,
                Assert.IsType<ManagedBrowserTransferPickerResult>((await pending).Result).Kind);
        }
        finally { gate.TrySetResult(); process.OnRequest = null; }
    }
}
