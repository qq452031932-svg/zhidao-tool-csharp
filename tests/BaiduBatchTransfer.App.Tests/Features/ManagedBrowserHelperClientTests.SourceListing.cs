using BaiduBatchTransfer.App.Features.ManagedBrowser;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed partial class ManagedBrowserHelperClientTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListingHardTimeoutIsTypedAndNeverReplayed(bool stallWrite)
    {
        var process = new FakeProcess();
        await using var client = await Start(process);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OnRequest = _ => stallWrite ? gate.Task : Task.CompletedTask;
        try
        {
            var request = ListingRequest(60);
            var response = await client.SendAsync(request, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(ManagedBrowserSourceListingResultKind.TimedOut, Assert.IsType<ManagedBrowserSourceListingResult>(response.Result).Kind);
            ManagedBrowserProtocol.ValidateResponseFor(request, response);
            await Until(() => process.Killed);
            Assert.Single(process.Requests, r => r.Action == ManagedBrowserAction.ReadTransferSource);
        }
        finally { gate.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListingCancellationIsTypedWithNoReplay(bool before)
    {
        var process = new FakeProcess();
        await using var client = await Start(process);
        using var cancel = new CancellationTokenSource();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OnRequest = _ => gate.Task;
        if (before) cancel.Cancel();
        var request = ListingRequest(2000);
        var pending = client.SendAsync(request, cancel.Token);
        if (!before) { await Until(() => process.Requests.Count == 2); cancel.Cancel(); }
        try
        {
            var response = await pending.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(ManagedBrowserSourceListingResultKind.Cancelled, Assert.IsType<ManagedBrowserSourceListingResult>(response.Result).Kind);
            Assert.Equal(before ? 0 : 1, process.Requests.Count(r => r.Action == ManagedBrowserAction.ReadTransferSource));
        }
        finally { gate.TrySetResult(); process.OnRequest = null; }
    }

    [Fact]
    public async Task ListingUsesItsRequestedBudget()
    {
        var process = new FakeProcess();
        await using var client = await Start(process);
        process.OnRequest = async request =>
        {
            await Task.Delay(250);
            process.Emit(ManagedBrowserResponse.For(request, new ManagedBrowserSourceListingResult(ManagedBrowserSourceListingResultKind.ExtractionRequired)));
        };
        var response = await client.SendAsync(ListingRequest(2000), CancellationToken.None);
        Assert.Equal(ManagedBrowserSourceListingResultKind.ExtractionRequired, Assert.IsType<ManagedBrowserSourceListingResult>(response.Result).Kind);
        Assert.False(process.Killed);
        process.OnRequest = null;
    }

    private static ManagedBrowserRequest ListingRequest(int timeout) => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 7,
        ManagedBrowserAction.ReadTransferSource, new ManagedBrowserReadTransferSourceRequest(Guid.NewGuid(), "synthetic-target",
            new Uri("http://127.0.0.1:9222/"), timeout, "https://pan.baidu.com/s/synthetic?pwd=a1B2", "synthetic-uid"));
}
