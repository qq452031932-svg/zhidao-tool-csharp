using BaiduBatchTransfer.App.Features.ManagedBrowser;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed partial class ManagedBrowserHelperClientTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectoryListingHardDeadlineIsTypedAndNeverReplayed(bool stallWrite)
    {
        var process = new FakeProcess();
        var lease = new FakeLease();
        await using var client = await Start(process, lease);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OnRequest = _ => stallWrite ? gate.Task : Task.CompletedTask;
        var request = DirectoryListingRequest(60);
        try
        {
            var response = await client.SendAsync(request, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(ManagedBrowserDirectoryListingResultKind.TimedOut,
                Assert.IsType<ManagedBrowserDirectoryListingResult>(response.Result).Kind);
            ManagedBrowserProtocol.ValidateResponseFor(request, response);
            await Until(() => process.Killed);
            Assert.Single(process.Requests, value => value.Action == ManagedBrowserAction.ReadTransferDirectory);
            Assert.DoesNotContain(process.Requests, value => value.Action == ManagedBrowserAction.OpenTransferSource);
            if (stallWrite) Assert.False(lease.Released);
        }
        finally { gate.TrySetResult(); }
        await Until(() => lease.Released);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectoryListingCancellationIsTypedAndDoesNotSendAgain(bool beforeSend)
    {
        var process = new FakeProcess();
        await using var client = await Start(process);
        using var cancellation = new CancellationTokenSource();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OnRequest = _ => gate.Task;
        if (beforeSend) cancellation.Cancel();
        var request = DirectoryListingRequest(2000);
        var pending = client.SendAsync(request, cancellation.Token);
        try
        {
            if (!beforeSend)
            {
                await Until(() => process.Requests.Count == 2);
                cancellation.Cancel();
            }
            var response = await pending.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(ManagedBrowserDirectoryListingResultKind.Cancelled,
                Assert.IsType<ManagedBrowserDirectoryListingResult>(response.Result).Kind);
            ManagedBrowserProtocol.ValidateResponseFor(request, response);
            Assert.Equal(beforeSend ? 0 : 1,
                process.Requests.Count(value => value.Action == ManagedBrowserAction.ReadTransferDirectory));
            if (beforeSend) Assert.False(process.Killed);
            else await Until(() => process.Killed);
        }
        finally { gate.TrySetResult(); process.OnRequest = null; }
    }

    [Fact]
    public async Task DirectoryListingUsesRequestBudgetInsteadOfDefaultControlTimeout()
    {
        var process = new FakeProcess();
        await using var client = await Start(process);
        process.OnRequest = async request =>
        {
            // The fixture's default control timeout is 120 ms; this read has its own 2 s budget.
            await Task.Delay(250);
            process.Emit(ManagedBrowserResponse.For(request,
                new ManagedBrowserDirectoryListingResult(ManagedBrowserDirectoryListingResultKind.PageNotValidated)));
        };
        try
        {
            var request = DirectoryListingRequest(2000);
            var response = await client.SendAsync(request, CancellationToken.None);
            Assert.Equal(ManagedBrowserDirectoryListingResultKind.PageNotValidated,
                Assert.IsType<ManagedBrowserDirectoryListingResult>(response.Result).Kind);
            ManagedBrowserProtocol.ValidateResponseFor(request, response);
            Assert.False(process.Killed);
            Assert.Single(process.Requests, value => value.Action == ManagedBrowserAction.ReadTransferDirectory);
        }
        finally { process.OnRequest = null; }
    }

    private static ManagedBrowserRequest DirectoryListingRequest(int timeout) => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 7, ManagedBrowserAction.ReadTransferDirectory,
        new ManagedBrowserReadTransferDirectoryRequest(Guid.NewGuid(), "synthetic-target",
            new Uri("http://127.0.0.1:9222/"), timeout,
            "https://pan.baidu.com/s/synthetic?pwd=a1B2", "synthetic-uid", "parent-id", "/测试目录"));
}
