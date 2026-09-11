using System.Text;
using System.Text.Json.Nodes;
using BaiduBatchTransfer.App.Features.ManagedBrowser;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class ManagedBrowserShareSubmissionProtocolTests
{
    private static readonly Guid CorrelationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OperationId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid AccountId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid BrowserId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    [Fact]
    public void ThreeShareActionsAreFixedAndRoundTripWithStrictPairs()
    {
        Assert.Equal(22, (int)ManagedBrowserAction.ReadShareOfficialListing);
        Assert.Equal(23, (int)ManagedBrowserAction.SubmitShareOnce);
        Assert.Equal(24, (int)ManagedBrowserAction.ObserveShareSubmissionOutcome);

        var context = Context();
        var listing = Listing(context, Entry("old-share", "https://pan.baidu.com/s/old_share"));
        Assert.Equal("b90828c325b3af76097628fa68c165c0c16574cd3b4b4971e0f4c4d407ccb824",
            listing.ShareIdsFingerprint);
        Assert.Equal("375f3c893fb7d2143779b9d747b382d4cf84df58a3edc37b3a5284d1e0f9835c",
            listing.ObservationFingerprint);
        Assert.Equal("04eac615c92d026ec5e5f082a5f887bd77f8b9d169422e40c90464e97e63b6ba",
            ManagedBrowserShareSubmissionOutcomeResult.CreateAcceptedBound(
                "request-1", "new-share", "https://pan.baidu.com/s/new_share", "item-1",
                ShareExtractionCodeBehavior.Fixed, "A1b2").OfficialObservationFingerprint);
        var cases = new (ManagedBrowserAction Action, ManagedBrowserRequestPayload Payload, ManagedBrowserResult Result)[]
        {
            (ManagedBrowserAction.ReadShareOfficialListing,
                new ManagedBrowserReadShareOfficialListingRequest(context),
                new ManagedBrowserReadShareOfficialListingResult(
                    ManagedBrowserShareListingResultKind.Observed, listing)),
            (ManagedBrowserAction.SubmitShareOnce,
                new ManagedBrowserSubmitShareOnceRequest(
                    context, listing.ShareIdsFingerprint, listing.ObservationFingerprint),
                new ManagedBrowserSubmitShareOnceResult(
                    ManagedBrowserShareSubmitResultKind.ClickAttempted,
                    ManagedBrowserShareSubmitDispatchState.ClickDispatched)),
            (ManagedBrowserAction.ObserveShareSubmissionOutcome,
                new ManagedBrowserObserveShareSubmissionOutcomeRequest(
                    context, listing.ShareIdsFingerprint, listing.ObservationFingerprint),
                ManagedBrowserShareSubmissionOutcomeResult.CreateAcceptedBound(
                    "request-1", "new-share", "https://pan.baidu.com/s/new_share", "item-1",
                    ShareExtractionCodeBehavior.Fixed, "A1b2")),
        };

        foreach (var item in cases)
        {
            var request = Request(item.Action, item.Payload);
            var decodedRequest = ManagedBrowserProtocol.DecodeRequest(
                ManagedBrowserProtocol.EncodeRequestPayload(request));
            Assert.Equal(item.Action, decodedRequest.Action);

            var response = ManagedBrowserResponse.For(request, item.Result);
            var decodedResponse = ManagedBrowserProtocol.DecodeResponse(
                ManagedBrowserProtocol.EncodeResponsePayload(response));
            ManagedBrowserProtocol.ValidateResponseFor(request, decodedResponse);
            Assert.Equal(item.Action, decodedResponse.Action);
        }
    }

    [Fact]
    public void ContextAllowsOneOrMoreExactItemsAndPermanentUnlimitedRandomOrFixedCode()
    {
        var random = Context(mode: ShareExtractionCodeBehavior.Random, fixedCode: null);
        Assert.Equal(["item-1"], random.ExactItems.Items.Select(item => item.ExactId));
        Assert.True(random.IsPermanent);
        Assert.True(random.IsAccessUnlimited);

        var multiple = Context(
            items: new RemoteItemIdentitySet([
                new RemoteItemIdentity("item-1", "one"),
                new RemoteItemIdentity("item-2", "two"),
            ]));
        Assert.Equal(["item-1", "item-2"], multiple.ExactItems.Items.Select(item => item.ExactId));
        Assert.Equal(2, multiple.ExactItems.Count);
        Assert.Throws<ArgumentException>(() => Context(
            mode: ShareExtractionCodeBehavior.Random, fixedCode: "A1b2"));
        Assert.Throws<ArgumentException>(() => Context(
            mode: ShareExtractionCodeBehavior.Fixed, fixedCode: null));
        Assert.Throws<ArgumentException>(() => Context(
            mode: ShareExtractionCodeBehavior.Fixed, fixedCode: "abc"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Context(
            mode: ShareExtractionCodeBehavior.Unknown, fixedCode: null));
    }

    [Fact]
    public void MultiObjectContextAndAcceptedOutcomeRoundTripAsOneExactShare()
    {
        var items = new RemoteItemIdentitySet([
            new RemoteItemIdentity("item-1", "one"),
            new RemoteItemIdentity("item-2", "two"),
        ]);
        var context = Context(items: items);
        var baseline = Listing(context);
        var request = Request(ManagedBrowserAction.ObserveShareSubmissionOutcome,
            new ManagedBrowserObserveShareSubmissionOutcomeRequest(
                context, baseline.ShareIdsFingerprint, baseline.ObservationFingerprint));
        var result = ManagedBrowserShareSubmissionOutcomeResult.CreateAcceptedBound(
            "request-1", "new-share", "https://pan.baidu.com/s/new_share", items,
            ShareExtractionCodeBehavior.Fixed, "A1b2");

        var decodedRequest = ManagedBrowserProtocol.DecodeRequest(
            ManagedBrowserProtocol.EncodeRequestPayload(request));
        var decodedResponse = ManagedBrowserProtocol.DecodeResponse(
            ManagedBrowserProtocol.EncodeResponsePayload(ManagedBrowserResponse.For(request, result)));

        ManagedBrowserProtocol.ValidateResponseFor(decodedRequest, decodedResponse);
        var accepted = Assert.IsType<ManagedBrowserShareSubmissionOutcomeResult>(decodedResponse.Result);
        Assert.Equal(2, accepted.ExactItems!.Count);
        Assert.Equal(items.Fingerprint, accepted.ExactItems.Fingerprint);
    }

    [Theory]
    [InlineData("selector")]
    [InlineData("script")]
    [InlineData("url")]
    [InlineData("targetUrl")]
    [InlineData("items")]
    [InlineData("batch")]
    [InlineData("retry")]
    public void CallerSuppliedNavigationSelectorsScriptsAndBulkFieldsAreRejected(string field)
    {
        var request = Request(ManagedBrowserAction.ReadShareOfficialListing,
            new ManagedBrowserReadShareOfficialListingRequest(Context()));
        var root = JsonNode.Parse(ManagedBrowserProtocol.EncodeRequestPayload(request))!.AsObject();
        root["payload"]!.AsObject()[field] = field == "items" ? new JsonArray("item-1", "item-2") : "private";

        var error = Assert.Throws<ManagedBrowserProtocolException>(() => ManagedBrowserProtocol.DecodeRequest(
            Encoding.UTF8.GetBytes(root.ToJsonString())));
        Assert.Contains(error.Error, new[]
        {
            ManagedBrowserProtocolError.ForbiddenField,
            ManagedBrowserProtocolError.UnknownField,
        });
        Assert.DoesNotContain("private", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void OfficialListingIsStableCompleteAndEveryEntryIsAOneItemPermanentUnlimitedShare()
    {
        var context = Context();
        var first = Listing(context,
            Entry("share-2", "https://pan.baidu.com/s/share_2"),
            Entry("share-1", "https://pan.baidu.com/s/share_1"));
        var second = Listing(context,
            Entry("share-1", "https://pan.baidu.com/s/share_1"),
            Entry("share-2", "https://pan.baidu.com/s/share_2"));

        Assert.Equal(first.ShareIdsFingerprint, second.ShareIdsFingerprint);
        Assert.Equal(first.ObservationFingerprint, second.ObservationFingerprint);
        Assert.Throws<ArgumentException>(() => new ManagedBrowserShareOfficialEntry(
            "share-3", "https://evil.example/s/share_3", "item-1", true, true,
            ShareExtractionCodeBehavior.Fixed, "A1b2"));
        Assert.Throws<ArgumentException>(() => new ManagedBrowserShareOfficialEntry(
            "share-3", "https://pan.baidu.com/s/share_3", "item-1", false, true,
            ShareExtractionCodeBehavior.Fixed, "A1b2"));
    }

    [Fact]
    public void ResponseParityRejectsAResultForAnotherExactItemOrSettings()
    {
        var context = Context();
        var baseline = Listing(context, Entry("old-share", "https://pan.baidu.com/s/old_share"));
        var request = Request(ManagedBrowserAction.ObserveShareSubmissionOutcome,
            new ManagedBrowserObserveShareSubmissionOutcomeRequest(
                context, baseline.ShareIdsFingerprint, baseline.ObservationFingerprint));

        foreach (var result in new[]
                 {
                     ManagedBrowserShareSubmissionOutcomeResult.CreateAcceptedBound(
                         "request-1", "new-share", "https://pan.baidu.com/s/new_share", "item-2",
                         ShareExtractionCodeBehavior.Fixed, "A1b2"),
                     ManagedBrowserShareSubmissionOutcomeResult.CreateAcceptedBound(
                         "request-1", "new-share", "https://pan.baidu.com/s/new_share", "item-1",
                         ShareExtractionCodeBehavior.Fixed, "Z9y8"),
                 })
        {
            var response = ManagedBrowserResponse.For(request, result);
            var error = Assert.Throws<ManagedBrowserProtocolException>(() =>
                ManagedBrowserProtocol.ValidateResponseFor(request, response));
            Assert.Equal(ManagedBrowserProtocolError.ResponseIdentityMismatch, error.Error);
        }
    }

    private static ManagedBrowserShareSubmissionContext Context(
        RemoteItemIdentitySet? items = null,
        ShareExtractionCodeBehavior mode = ShareExtractionCodeBehavior.Fixed,
        string? fixedCode = "A1b2") => new(
        BrowserId,
        "target-1",
        new Uri("http://127.0.0.1:43127/"),
        30_000,
        "uid-1",
        "directory-1",
        "/prepared",
        items ?? new RemoteItemIdentitySet([new RemoteItemIdentity("item-1", "app.apk")]),
        mode,
        fixedCode);

    private static ManagedBrowserShareOfficialEntry Entry(string shareId, string shareUrl) => new(
        shareId, shareUrl, "item-1", true, true,
        ShareExtractionCodeBehavior.Fixed, "A1b2");

    private static ManagedBrowserShareOfficialListing Listing(
        ManagedBrowserShareSubmissionContext context,
        params ManagedBrowserShareOfficialEntry[] entries) => new(context, true, true, entries);

    private static ManagedBrowserRequest Request(ManagedBrowserAction action, ManagedBrowserRequestPayload payload) =>
        new(CorrelationId, OperationId, AccountId, 1, action, payload);
}
