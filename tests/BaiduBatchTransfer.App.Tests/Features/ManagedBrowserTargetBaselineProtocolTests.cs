using BaiduBatchTransfer.App.Features.ManagedBrowser;
using System.Text;
using System.Text.Json.Nodes;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class ManagedBrowserTargetBaselineProtocolTests
{
    private static readonly Guid CorrelationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OperationId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid AccountId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid BrowserId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    [Fact]
    public void FourFixedActionsRoundTripWithStrictActionResultPairs()
    {
        var binding = Binding();
        var inventory = Inventory();
        var cases = new (ManagedBrowserAction Action, ManagedBrowserRequestPayload Payload, ManagedBrowserResult Result)[]
        {
            (ManagedBrowserAction.OpenTransferTargetRoot,
                new ManagedBrowserOpenTransferTargetRootRequest(binding),
                new ManagedBrowserOpenTransferTargetRootResult(ManagedBrowserOpenTransferTargetRootResultKind.RootOpened,
                    ManagedBrowserTargetNavigationState.RouteOpened)),
            (ManagedBrowserAction.EnterTransferTargetDirectory,
                new ManagedBrowserEnterTransferTargetDirectoryRequest(binding, inventory.ToHeader(), "child-1", "child", "/parent/child"),
                new ManagedBrowserEnterTransferTargetDirectoryResult(ManagedBrowserEnterTransferTargetDirectoryResultKind.DirectoryEntered,
                    ManagedBrowserTargetNavigationState.RouteOpened)),
            (ManagedBrowserAction.ReadTransferTargetDirectory,
                new ManagedBrowserReadTransferTargetDirectoryRequest(binding, "parent-1", "/parent", ["root-1", "parent-1"]),
                new ManagedBrowserReadTransferTargetDirectoryResult(ManagedBrowserReadTransferTargetDirectoryResultKind.Observed, inventory)),
            (ManagedBrowserAction.OpenTransferSourceAfterTargetRead,
                new ManagedBrowserOpenTransferSourceAfterTargetReadRequest(binding, inventory.ToHeader()),
                new ManagedBrowserOpenTransferSourceAfterTargetReadResult(ManagedBrowserOpenTransferSourceAfterTargetReadResultKind.SourceRestored,
                    ManagedBrowserTargetNavigationState.RouteOpened)),
        };

        foreach (var item in cases)
        {
            var request = Request(item.Action, item.Payload);
            Assert.Contains("\"protocolVersion\":3", Encoding.UTF8.GetString(
                ManagedBrowserProtocol.EncodeRequestPayload(request)), StringComparison.Ordinal);
            var decodedRequest = ManagedBrowserProtocol.DecodeRequest(ManagedBrowserProtocol.EncodeRequestPayload(request));
            Assert.Equal(item.Action, decodedRequest.Action);
            var response = ManagedBrowserResponse.For(request, item.Result);
            var decodedResponse = ManagedBrowserProtocol.DecodeResponse(ManagedBrowserProtocol.EncodeResponsePayload(response));
            Assert.Equal(item.Action, decodedResponse.Action);
            Assert.Throws<ArgumentException>(() => ManagedBrowserResponse.For(
                Request(ManagedBrowserAction.Health, ManagedBrowserRequestPayload.Empty), item.Result));
        }
    }

    [Fact]
    public void ObservedTargetInventoryResponseMustMatchRequestBindingAndExpectedDirectoryExactly()
    {
        var request = Request(ManagedBrowserAction.ReadTransferTargetDirectory,
            new ManagedBrowserReadTransferTargetDirectoryRequest(
                Binding(), "parent-1", "/parent", ["root-1", "parent-1"]));
        var variants = new[]
        {
            Snapshot(Guid.Parse("55555555-5555-5555-5555-555555555555"), "target-1", "http://127.0.0.1:43127/", "uid-1", "parent-1", "/parent", ["root-1", "parent-1"]),
            Snapshot(BrowserId, "target-2", "http://127.0.0.1:43127/", "uid-1", "parent-1", "/parent", ["root-1", "parent-1"]),
            Snapshot(BrowserId, "target-1", "http://127.0.0.1:43128/", "uid-1", "parent-1", "/parent", ["root-1", "parent-1"]),
            Snapshot(BrowserId, "target-1", "http://127.0.0.1:43127/", "uid-2", "parent-1", "/parent", ["root-1", "parent-1"]),
            Snapshot(BrowserId, "target-1", "http://127.0.0.1:43127/", "uid-1", "parent-2", "/parent", ["root-1", "parent-2"]),
            Snapshot(BrowserId, "target-1", "http://127.0.0.1:43127/", "uid-1", "parent-1", "/other", ["root-1", "parent-1"]),
            Snapshot(BrowserId, "target-1", "http://127.0.0.1:43127/", "uid-1", "parent-1", "/parent", ["other-root", "parent-1"]),
        };

        ManagedBrowserProtocol.ValidateResponseFor(request, ManagedBrowserResponse.For(request,
            new ManagedBrowserReadTransferTargetDirectoryResult(
                ManagedBrowserReadTransferTargetDirectoryResultKind.Observed, Inventory())));
        foreach (var snapshot in variants)
            Assert.Throws<ManagedBrowserProtocolException>(() => ManagedBrowserProtocol.ValidateResponseFor(
                request, ManagedBrowserResponse.For(request,
                    new ManagedBrowserReadTransferTargetDirectoryResult(
                        ManagedBrowserReadTransferTargetDirectoryResultKind.Observed, snapshot))));
    }

    [Fact]
    public void OmittedExpectedDirectoryMeansRootAndRejectsNestedObservedInventory()
    {
        var request = Request(ManagedBrowserAction.ReadTransferTargetDirectory,
            new ManagedBrowserReadTransferTargetDirectoryRequest(Binding(), null, null, null));
        var root = Snapshot(BrowserId, "target-1", "http://127.0.0.1:43127/", "uid-1",
            "root-1", "/", ["root-1"]);
        ManagedBrowserProtocol.ValidateResponseFor(request, ManagedBrowserResponse.For(request,
            new ManagedBrowserReadTransferTargetDirectoryResult(
                ManagedBrowserReadTransferTargetDirectoryResultKind.Observed, root)));
        Assert.Throws<ManagedBrowserProtocolException>(() => ManagedBrowserProtocol.ValidateResponseFor(
            request, ManagedBrowserResponse.For(request,
                new ManagedBrowserReadTransferTargetDirectoryResult(
                    ManagedBrowserReadTransferTargetDirectoryResultKind.Observed, Inventory()))));
    }

    [Fact]
    public void NavigationStateDistinguishesPreDispatchDenialRouteOpenedAndPostDispatchUnknown()
    {
        _ = new ManagedBrowserOpenTransferTargetRootResult(
            ManagedBrowserOpenTransferTargetRootResultKind.TimedOut,
            ManagedBrowserTargetNavigationState.KnownNotDispatched);
        _ = new ManagedBrowserOpenTransferTargetRootResult(
            ManagedBrowserOpenTransferTargetRootResultKind.LoginRequired,
            ManagedBrowserTargetNavigationState.RouteOutcomeUnknown);
        Assert.Throws<ArgumentException>(() => new ManagedBrowserOpenTransferTargetRootResult(
            ManagedBrowserOpenTransferTargetRootResultKind.RootOpened,
            ManagedBrowserTargetNavigationState.KnownNotDispatched));
        Assert.Throws<ArgumentException>(() => new ManagedBrowserOpenTransferTargetRootResult(
            ManagedBrowserOpenTransferTargetRootResultKind.TimedOut,
            ManagedBrowserTargetNavigationState.RouteOpened));
    }

    [Fact]
    public void EnterCarriesOnlyParentHeaderAndExactChildTupleAndStaysBelowOldFrameLimit()
    {
        var inventory = Inventory();
        var request = Request(ManagedBrowserAction.EnterTransferTargetDirectory,
            new ManagedBrowserEnterTransferTargetDirectoryRequest(
                Binding(), inventory.ToHeader(), "child-1", "child", "/parent/child"));

        var bytes = ManagedBrowserProtocol.EncodeRequest(request);
        var json = Encoding.UTF8.GetString(bytes.AsSpan(4));
        Assert.True(bytes.Length < 65_536);
        var payload = JsonNode.Parse(json)!["payload"]!.AsObject();
        Assert.Null(payload["expectedParent"]!["items"]);
        Assert.Contains("\"parentObservationFingerprint\"", json, StringComparison.Ordinal);
        Assert.Contains("\"childExactId\":\"child-1\"", json, StringComparison.Ordinal);
        Assert.Contains("\"childServerName\":\"child\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("script", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("selector", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnterMaximumSmallFieldsFitBelow64KiBAndPathOrAggregateOverageIsRejected()
    {
        var segments = Enumerable.Range(0, 63)
            .Select(index => new string('"', index == 62 ? 56 : 60)).ToArray();
        var parentPath = "/" + string.Join('/', segments);
        Assert.Equal(3_839, Encoding.UTF8.GetByteCount(parentPath));
        var ancestors = Enumerable.Range(0, 64)
            .Select(index => index.ToString("D3") + new string('a', 125)).ToArray();
        var childId = new string('c', 128);
        var childName = new string('"', 256);
        var parent = new ManagedBrowserTargetInventorySnapshot(
            BrowserId, new string('t', 256), new Uri("http://127.0.0.1:43127/"),
            "u" + new string('"', 255), new string('"', 256), ancestors[^1], parentPath,
            ancestors, 1, true, true, 64,
            [new ManagedBrowserTargetInventoryItem(childId,
                ManagedBrowserTargetInventoryItemKind.Folder, childName)]);
        var binding = MaximalBinding(parent);
        var request = Request(ManagedBrowserAction.EnterTransferTargetDirectory,
            new ManagedBrowserEnterTransferTargetDirectoryRequest(binding, parent.ToHeader(), childId,
                childName, parentPath + "/" + childName));

        var frame = ManagedBrowserProtocol.EncodeRequest(request);
        Assert.True(frame.Length < 65_536, $"Maximum legal Enter frame was {frame.Length} bytes.");
        var overParent = new ManagedBrowserTargetInventorySnapshot(
            BrowserId, parent.TargetId, parent.DevToolsEndpoint, parent.BaiduUserId,
            parent.GenerationToken, ancestors[^1], parentPath + "x", ancestors, 1, true, true, 64,
            [new ManagedBrowserTargetInventoryItem(childId,
                ManagedBrowserTargetInventoryItemKind.Folder, childName)]);
        var overBinding = MaximalBinding(overParent);
        Assert.Throws<ArgumentException>(() => new ManagedBrowserEnterTransferTargetDirectoryRequest(
            overBinding, overParent.ToHeader(), childId, childName,
            overParent.CanonicalPath + "/" + childName));

        var payload = ManagedBrowserProtocol.EncodeRequestPayload(request);
        var oversized = new byte[65_532];
        payload.AsSpan(0, payload.Length - 1).CopyTo(oversized.AsSpan());
        oversized.AsSpan(payload.Length - 1, oversized.Length - payload.Length).Fill((byte)' ');
        oversized[^1] = (byte)'}';
        Assert.Throws<ManagedBrowserProtocolException>(() => ManagedBrowserProtocol.DecodeRequest(oversized));
    }

    [Fact]
    public void InventoryFingerprintIsVersionedLengthPrefixedDeterministicAndRedacted()
    {
        var first = Inventory();
        var reversed = new ManagedBrowserTargetInventorySnapshot(
            BrowserId, "target-1", new Uri("http://127.0.0.1:43127/"), "uid-1", "generation-1",
            "parent-1", "/parent", ["root-1", "parent-1"], 2, true, true, 3,
            [new ManagedBrowserTargetInventoryItem("file-2", ManagedBrowserTargetInventoryItemKind.File, "two.txt"),
             new ManagedBrowserTargetInventoryItem("child-1", ManagedBrowserTargetInventoryItemKind.Folder, "child")]);

        Assert.Equal(first.ObservationFingerprint, reversed.ObservationFingerprint);
        Assert.Equal("3a0645f30f6e3386f0c26111bb512957df3eb36de54fd4663b735e64283a17c8",
            first.ObservationFingerprint);
        foreach (var secret in new[] { "uid-1", "target-1", "parent-1", "/parent", "two.txt" })
        {
            Assert.DoesNotContain(secret, first.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(secret, first.ToHeader().ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void InventoryRequiresAllItemsCompleteStableRootInclusiveUniqueEvidence()
    {
        Assert.Equal(ManagedBrowserTargetInventoryScope.AllItems, Inventory().Scope);
        Assert.Throws<ArgumentException>(() => NewInventory(items:
            [Item("same", ManagedBrowserTargetInventoryItemKind.File, "a"),
             Item("same", ManagedBrowserTargetInventoryItemKind.Folder, "b")]));
        Assert.Throws<ArgumentException>(() => NewInventory(totalCount: 1));
        Assert.Throws<ArgumentException>(() => NewInventory(reachedEnd: false));
        Assert.Throws<ArgumentException>(() => NewInventory(isStable: false));
        Assert.Throws<ArgumentOutOfRangeException>(() => NewInventory(enumerationStepCount: 65));
        Assert.Throws<ArgumentException>(() => NewInventory(canonicalPath: "/parent", ancestors: ["parent-1"]));
        Assert.Throws<ArgumentException>(() => NewInventory(items: Enumerable.Range(0, 1025)
            .Select(index => Item($"item-{index}", ManagedBrowserTargetInventoryItemKind.File, "x")).ToArray()));
    }

    [Fact]
    public void CalculatedWorstCaseInventoryFrameFitsOneMiBAndRetainsSmallFieldBounds()
    {
        var items = Enumerable.Range(0, 1024).Select(index =>
            Item(index.ToString("D4") + new string('x', 124),
                index % 2 == 0 ? ManagedBrowserTargetInventoryItemKind.File : ManagedBrowserTargetInventoryItemKind.Folder,
                new string('"', 256))).ToArray();
        var snapshot = NewInventory(totalCount: items.Length, enumerationStepCount: 64, items: items);
        var request = Request(ManagedBrowserAction.ReadTransferTargetDirectory,
            new ManagedBrowserReadTransferTargetDirectoryRequest(Binding(), "parent-1", "/parent", ["root-1", "parent-1"]));
        var frame = ManagedBrowserProtocol.EncodeResponse(ManagedBrowserResponse.For(request,
            new ManagedBrowserReadTransferTargetDirectoryResult(ManagedBrowserReadTransferTargetDirectoryResultKind.Observed, snapshot)));

        Assert.True(frame.Length <= ManagedBrowserProtocol.MaximumFrameBytes,
            $"Worst legal inventory frame was {frame.Length} bytes.");
        Assert.Throws<ArgumentException>(() => new ManagedBrowserTargetInventoryItem("id", ManagedBrowserTargetInventoryItemKind.File,
            string.Concat(Enumerable.Repeat("😀", 65))));
    }

    [Theory]
    [InlineData("script")]
    [InlineData("selector")]
    [InlineData("url")]
    [InlineData("items")]
    public void UnknownDynamicAndParentItemFieldsAreRejected(string field)
    {
        var request = Request(ManagedBrowserAction.EnterTransferTargetDirectory,
            new ManagedBrowserEnterTransferTargetDirectoryRequest(Binding(), Inventory().ToHeader(), "child-1", "child", "/parent/child"));
        var root = JsonNode.Parse(ManagedBrowserProtocol.EncodeRequestPayload(request))!.AsObject();
        root["payload"]!.AsObject()[field] = field == "items" ? new JsonArray() : "private";
        var error = Assert.Throws<ManagedBrowserProtocolException>(() =>
            ManagedBrowserProtocol.DecodeRequest(Encoding.UTF8.GetBytes(root.ToJsonString())));
        Assert.Contains(error.Error, new[] { ManagedBrowserProtocolError.UnknownField, ManagedBrowserProtocolError.ForbiddenField });
        Assert.DoesNotContain("private", error.ToString(), StringComparison.Ordinal);
    }

    private static ManagedBrowserTargetActionBinding Binding()
    {
        var source = new ManagedBrowserSourceSnapshot("uid-1", "source-generation", 1, true, true,
            [new ManagedBrowserSourceItem("source-1", ManagedBrowserSourceItemKind.File, "source.txt")],
            "4cdb5ac7a450d6125ebaef9287cb8431f3976b5374ebc5de96c2570786995956");
        return new ManagedBrowserTargetActionBinding(BrowserId, "target-1", new Uri("http://127.0.0.1:43127/"),
            30_000, "https://pan.baidu.com/s/source_1?pwd=A1b2", "uid-1", source);
    }

    private static ManagedBrowserTargetActionBinding MaximalBinding(
        ManagedBrowserTargetInventorySnapshot target)
    {
        const string uri = "https://pan.baidu.com/s/source_1?pwd=A1b2";
        var items = Enumerable.Range(0, 32).Select(index => new ManagedBrowserSourceItem(
            index.ToString("D3") + new string('s', 125), ManagedBrowserSourceItemKind.File,
            new string('"', 256))).ToArray();
        var draft = new ManagedBrowserSourceSnapshot(target.BaiduUserId, new string('"', 256),
            items.Length, true, true, items, new string('0', 64));
        var source = new ManagedBrowserSourceSnapshot(target.BaiduUserId, draft.GenerationToken,
            items.Length, true, true, items, draft.ComputeFingerprint(uri));
        return new ManagedBrowserTargetActionBinding(target.BrowserId, target.TargetId,
            target.DevToolsEndpoint, 120_000, uri, target.BaiduUserId, source);
    }

    private static ManagedBrowserTargetInventorySnapshot Inventory() => NewInventory();

    private static ManagedBrowserTargetInventorySnapshot Snapshot(Guid browserId, string targetId,
        string endpoint, string uid, string directoryId, string path, IReadOnlyList<string> ancestors) =>
        new(browserId, targetId, new Uri(endpoint), uid, "generation-1", directoryId, path, ancestors,
            1, true, true, 1, [Item("file-2", ManagedBrowserTargetInventoryItemKind.File, "two.txt")]);

    private static ManagedBrowserTargetInventorySnapshot NewInventory(
        string canonicalPath = "/parent", IReadOnlyList<string>? ancestors = null, int totalCount = 2,
        bool reachedEnd = true, bool isStable = true, int enumerationStepCount = 3,
        IReadOnlyList<ManagedBrowserTargetInventoryItem>? items = null) =>
        new(BrowserId, "target-1", new Uri("http://127.0.0.1:43127/"), "uid-1", "generation-1",
            "parent-1", canonicalPath, ancestors ?? ["root-1", "parent-1"], totalCount,
            reachedEnd, isStable, enumerationStepCount,
            items ?? [Item("child-1", ManagedBrowserTargetInventoryItemKind.Folder, "child"),
                      Item("file-2", ManagedBrowserTargetInventoryItemKind.File, "two.txt")]);

    private static ManagedBrowserTargetInventoryItem Item(
        string id, ManagedBrowserTargetInventoryItemKind kind, string name) => new(id, kind, name);

    private static ManagedBrowserRequest Request(ManagedBrowserAction action, ManagedBrowserRequestPayload payload) =>
        new(CorrelationId, OperationId, AccountId, 7, action, payload);
}
