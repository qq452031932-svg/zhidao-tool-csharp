using BaiduBatchTransfer.App.Features.ManagedBrowser;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed partial class ManagedBrowserHelperClientTests
{
    [Fact]
    public async Task ShareSubmitPrecancellationIsKnownNotDispatchedAndWritesNoFrame()
    {
        var process = new FakeProcess();
        await using var client = await Start(process);
        var before = process.Requests.Count;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var response = await client.SendAsync(ShareSubmitRequest(1_000), cancelled.Token);

        var result = Assert.IsType<ManagedBrowserSubmitShareOnceResult>(response.Result);
        Assert.Equal(ManagedBrowserShareSubmitResultKind.Cancelled, result.Kind);
        Assert.Equal(ManagedBrowserShareSubmitDispatchState.KnownNotDispatched, result.DispatchState);
        Assert.Equal(before, process.Requests.Count);
    }

    [Fact]
    public async Task ShareListingUsesItsBoundedTimeoutAndReturnsATypedTerminal()
    {
        var process = new FakeProcess();
        await using var client = await Start(process);
        process.OnRequest = _ => Task.CompletedTask;

        var response = await client.SendAsync(ShareListingRequest(20), CancellationToken.None);

        Assert.Equal(ManagedBrowserShareListingResultKind.TimedOut,
            Assert.IsType<ManagedBrowserReadShareOfficialListingResult>(response.Result).Kind);
    }

    private static ManagedBrowserRequest ShareListingRequest(int timeout)
    {
        var context = ShareContext(timeout);
        return new ManagedBrowserRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1,
            ManagedBrowserAction.ReadShareOfficialListing,
            new ManagedBrowserReadShareOfficialListingRequest(context));
    }

    private static ManagedBrowserRequest ShareSubmitRequest(int timeout)
    {
        var context = ShareContext(timeout);
        var baseline = WebAutomationBaseline.Create(["old-share"]);
        return new ManagedBrowserRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1,
            ManagedBrowserAction.SubmitShareOnce,
            new ManagedBrowserSubmitShareOnceRequest(context, baseline.Fingerprint, new string('b', 64)));
    }

    private static ManagedBrowserShareSubmissionContext ShareContext(int timeout)
    {
        var items = new RemoteItemIdentitySet([new RemoteItemIdentity("item-1", "app.apk")]);
        return new ManagedBrowserShareSubmissionContext(
            Guid.Parse("44444444-4444-4444-4444-444444444444"),
            "target-1",
            new Uri("http://127.0.0.1:43127/"),
            timeout,
            "uid-1",
            "directory-1",
            "/prepared",
            items,
            ShareExtractionCodeBehavior.Fixed,
            "A1b2");
    }
}
