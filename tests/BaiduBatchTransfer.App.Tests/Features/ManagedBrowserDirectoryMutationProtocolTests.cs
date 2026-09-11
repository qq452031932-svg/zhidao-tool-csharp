using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using BaiduBatchTransfer.App.Features.ManagedBrowser;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class ManagedBrowserDirectoryMutationProtocolTests
{
    [Fact]
    public void NestedExpectedEvidenceMustBeAnObjectWithTypedInvalidPayloadFailures()
    {
        foreach (var request in FixedRequests())
        {
            var field = request.Action == ManagedBrowserAction.ObserveTransferPickerClosed
                ? "expectedSource" : "expectedDirectory";
            foreach (var malformed in new JsonNode?[] { null, JsonValue.Create(1), new JsonArray() })
            {
                var json = JsonNode.Parse(ManagedBrowserProtocol.EncodeRequestPayload(request))!.AsObject();
                json["payload"]![field] = malformed?.DeepClone();

                var error = Assert.Throws<ManagedBrowserProtocolException>(() =>
                    ManagedBrowserProtocol.DecodeRequest(Encoding.UTF8.GetBytes(json.ToJsonString())));

                Assert.Equal(ManagedBrowserProtocolError.InvalidPayload, error.Error);
            }
        }
    }

    [Fact]
    public void ActionSpecificExtrasAndGenericScriptFieldsAreRejected()
    {
        var extraFields = new Dictionary<ManagedBrowserAction, (string Name, JsonNode Value)>
        {
            [ManagedBrowserAction.OpenTransferDirectoryRoot] = ("childExactId", "child-1"),
            [ManagedBrowserAction.EnterTransferDirectory] = ("rootBreadcrumbExactId", "root-1"),
            [ManagedBrowserAction.BeginCreateTransferDirectory] = ("childCanonicalPath", "/parent/child"),
            [ManagedBrowserAction.ConfirmCreateTransferDirectory] = ("rootBreadcrumbExactId", "root-1"),
            [ManagedBrowserAction.CancelTransferPicker] = ("newSegment", "created"),
            [ManagedBrowserAction.ObserveTransferPickerClosed] = ("expectedDirectory", new JsonObject()),
        };
        foreach (var request in FixedRequests())
        {
            var json = JsonNode.Parse(ManagedBrowserProtocol.EncodeRequestPayload(request))!.AsObject();
            json["payload"]![extraFields[request.Action].Name] = extraFields[request.Action].Value.DeepClone();
            AssertProtocolError(json, ManagedBrowserProtocolError.UnknownField);

            json = JsonNode.Parse(ManagedBrowserProtocol.EncodeRequestPayload(request))!.AsObject();
            json["payload"]!["script"] = "never accepted";
            AssertProtocolError(json, ManagedBrowserProtocolError.ForbiddenField);

            json = JsonNode.Parse(ManagedBrowserProtocol.EncodeRequestPayload(request))!.AsObject();
            json["payload"]!["cookie"] = "never accepted";
            AssertProtocolError(json, ManagedBrowserProtocolError.ForbiddenField);
        }
    }

    [Fact]
    public void ImmutableUidFingerprintAncestryAndPathMismatchesAreInvalidPayload()
    {
        void Check(Action<JsonObject> mutate)
        {
            var request = FixedRequests().Single(value => value.Action == ManagedBrowserAction.EnterTransferDirectory);
            var json = JsonNode.Parse(ManagedBrowserProtocol.EncodeRequestPayload(request))!.AsObject();
            mutate(json["payload"]!.AsObject());
            AssertProtocolError(json, ManagedBrowserProtocolError.InvalidPayload);
        }

        Check(payload => payload["expectedDirectory"]!["baiduUserId"] = "other-uid");
        Check(payload => payload["expectedDirectory"]!["observationFingerprint"] = new string('0', 64));
        Check(payload => payload["expectedDirectory"]!["ancestorExactIds"] = new JsonArray("root-1", "wrong-parent"));
        Check(payload => payload["childCanonicalPath"] = "/parent/wrong-child");

        var closed = FixedRequests().Single(value => value.Action == ManagedBrowserAction.ObserveTransferPickerClosed);
        var closedJson = JsonNode.Parse(ManagedBrowserProtocol.EncodeRequestPayload(closed))!.AsObject();
        closedJson["payload"]!["expectedSource"]!["observationFingerprint"] = new string('0', 64);
        AssertProtocolError(closedJson, ManagedBrowserProtocolError.InvalidPayload);
    }

    [Fact]
    public void EnterDirectoryRejectsFolderNamesThatCanDeriveMultiplePathSegments()
    {
        var binding = Binding();
        var unsigned = new ManagedBrowserDirectorySnapshot("synthetic-uid", "directory-generation-1", "parent-1", "/parent",
            ["root-1", "parent-1"], 1, true, true,
            [new ManagedBrowserSourceItem("child-1", ManagedBrowserSourceItemKind.Folder, "bad/name")], new string('0', 64));
        var signed = new ManagedBrowserDirectorySnapshot(unsigned.BaiduUserId, unsigned.GenerationToken,
            unsigned.ParentExactId, unsigned.CanonicalPath, unsigned.AncestorExactIds, unsigned.TotalCount, true, true,
            unsigned.Items, unsigned.ComputeFingerprint(binding.SourceUri));

        Assert.Throws<ArgumentException>(() => ManagedBrowserDirectoryMutationRequest.ForEnterDirectory(
            binding, signed, "child-1", "/parent/bad/name"));
    }

    [Fact]
    public void ClientCancellationAndDeadlineAfterPossibleMutationDispatchAreOutcomeUnknown()
    {
        var terminal = typeof(ManagedBrowserHelperClient).GetMethod(
            "TerminalResponse", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(terminal);
        foreach (var request in FixedRequests().Where(value => value.Action != ManagedBrowserAction.ObserveTransferPickerClosed))
        foreach (var error in new[] { ManagedBrowserHelperClientError.Cancelled, ManagedBrowserHelperClientError.TimedOut })
        {
            var response = Assert.IsType<ManagedBrowserResponse>(terminal.Invoke(null, [request, error]));
            var result = Assert.IsType<ManagedBrowserDirectoryMutationResult>(response.Result);
            Assert.Equal(ManagedBrowserDirectoryMutationResultKind.OutcomeUnknown, result.Kind);
        }
    }

    [Fact]
    public void OpenRootBindsTheImmutableCurrentDirectoryAndProvenRootBreadcrumb()
    {
        var binding = new ManagedBrowserOpenTransferSourceRequest(Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "synthetic-target", new Uri("http://127.0.0.1:9222/"), 30_000,
            "https://pan.baidu.com/s/synthetic_-123?pwd=a1B2", "synthetic-uid");
        var directory = SignedDirectory(binding.SourceUri);
        var request = new ManagedBrowserRequest(Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Guid.Parse("33333333-3333-3333-3333-333333333333"), Guid.Parse("44444444-4444-4444-4444-444444444444"),
            7, ManagedBrowserAction.OpenTransferDirectoryRoot,
            ManagedBrowserDirectoryMutationRequest.ForOpenRoot(binding, directory, "root-1"));

        var json = JsonNode.Parse(ManagedBrowserProtocol.EncodeRequestPayload(request))!.AsObject();
        var payload = json["payload"]!.AsObject();
        Assert.Equal("root-1", payload["rootBreadcrumbExactId"]!.GetValue<string>());
        Assert.Equal("parent-1", payload["expectedDirectory"]!["parentExactId"]!.GetValue<string>());
        Assert.Equal("/parent", payload["expectedDirectory"]!["canonicalPath"]!.GetValue<string>());
        Assert.Equal("synthetic-uid", payload["expectedBaiduUserId"]!.GetValue<string>());
        Assert.Equal(request.Action, ManagedBrowserProtocol.DecodeRequest(ManagedBrowserProtocol.EncodeRequestPayload(request)).Action);
    }

    [Fact]
    public void EnterDirectoryBindsOneExistingFolderIdAndItsExactCanonicalChildPath()
    {
        var binding = new ManagedBrowserOpenTransferSourceRequest(Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "synthetic-target", new Uri("http://127.0.0.1:9222/"), 30_000,
            "https://pan.baidu.com/s/synthetic_-123?pwd=a1B2", "synthetic-uid");
        var request = new ManagedBrowserRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 7,
            ManagedBrowserAction.EnterTransferDirectory,
            ManagedBrowserDirectoryMutationRequest.ForEnterDirectory(binding, SignedDirectory(binding.SourceUri), "child-1", "/parent/child"));

        var payload = JsonNode.Parse(ManagedBrowserProtocol.EncodeRequestPayload(request))!["payload"]!.AsObject();
        Assert.Equal("child-1", payload["childExactId"]!.GetValue<string>());
        Assert.Equal("/parent/child", payload["childCanonicalPath"]!.GetValue<string>());
    }

    [Fact]
    public void BeginCreateBindsOnlyOneExactNewSegment()
    {
        var binding = new ManagedBrowserOpenTransferSourceRequest(Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "synthetic-target", new Uri("http://127.0.0.1:9222/"), 30_000,
            "https://pan.baidu.com/s/synthetic_-123?pwd=a1B2", "synthetic-uid");
        var request = new ManagedBrowserRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 7,
            ManagedBrowserAction.BeginCreateTransferDirectory,
            ManagedBrowserDirectoryMutationRequest.ForBeginCreate(binding, SignedDirectory(binding.SourceUri), "created"));
        var payload = JsonNode.Parse(ManagedBrowserProtocol.EncodeRequestPayload(request))!["payload"]!.AsObject();
        Assert.Equal("created", payload["newSegment"]!.GetValue<string>());
        Assert.False(payload.ContainsKey("selector"));
    }

    [Fact]
    public void ConfirmCreateBindsTheExistingCreateDialogEvidenceAndExactSegment()
    {
        var binding = new ManagedBrowserOpenTransferSourceRequest(Guid.Parse("11111111-1111-1111-1111-111111111111"), "synthetic-target",
            new Uri("http://127.0.0.1:9222/"), 30_000, "https://pan.baidu.com/s/synthetic_-123?pwd=a1B2", "synthetic-uid");
        var request = new ManagedBrowserRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 7,
            ManagedBrowserAction.ConfirmCreateTransferDirectory,
            ManagedBrowserDirectoryMutationRequest.ForConfirmCreate(binding, SignedDirectory(binding.SourceUri), "created"));
        Assert.Equal("created", JsonNode.Parse(ManagedBrowserProtocol.EncodeRequestPayload(request))!["payload"]!["newSegment"]!.GetValue<string>());
    }

    [Fact]
    public void CancelPickerBindsOnlyTheImmutableCurrentDirectory()
    {
        var binding = new ManagedBrowserOpenTransferSourceRequest(Guid.Parse("11111111-1111-1111-1111-111111111111"), "synthetic-target",
            new Uri("http://127.0.0.1:9222/"), 30_000, "https://pan.baidu.com/s/synthetic_-123?pwd=a1B2", "synthetic-uid");
        var request = new ManagedBrowserRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 7,
            ManagedBrowserAction.CancelTransferPicker,
            ManagedBrowserDirectoryMutationRequest.ForCancelPicker(binding, SignedDirectory(binding.SourceUri)));
        var payload = JsonNode.Parse(ManagedBrowserProtocol.EncodeRequestPayload(request))!["payload"]!.AsObject();
        Assert.False(payload.ContainsKey("newSegment"));
        Assert.Equal("parent-1", payload["expectedDirectory"]!["parentExactId"]!.GetValue<string>());
    }

    [Fact]
    public void ClosedObserverCanOnlyCarryTheImmutableOriginalSourceSnapshot()
    {
        var binding = new ManagedBrowserOpenTransferSourceRequest(Guid.Parse("11111111-1111-1111-1111-111111111111"), "synthetic-target",
            new Uri("http://127.0.0.1:9222/"), 30_000, "https://pan.baidu.com/s/synthetic_-123?pwd=a1B2", "synthetic-uid");
        var source = SignedSource(binding.SourceUri);
        var request = new ManagedBrowserRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 7,
            ManagedBrowserAction.ObserveTransferPickerClosed,
            new ManagedBrowserObserveTransferPickerClosedRequest(binding, source));
        var payload = JsonNode.Parse(ManagedBrowserProtocol.EncodeRequestPayload(request))!["payload"]!.AsObject();
        Assert.True(payload.ContainsKey("expectedSource"));
        Assert.False(payload.ContainsKey("expectedDirectory"));
    }

    [Fact]
    public void MutationSuccessKindsAreBoundToTheirExactAction()
    {
        var binding = new ManagedBrowserOpenTransferSourceRequest(Guid.Parse("11111111-1111-1111-1111-111111111111"), "synthetic-target",
            new Uri("http://127.0.0.1:9222/"), 30_000, "https://pan.baidu.com/s/synthetic_-123?pwd=a1B2", "synthetic-uid");
        var request = new ManagedBrowserRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 7,
            ManagedBrowserAction.OpenTransferDirectoryRoot,
            ManagedBrowserDirectoryMutationRequest.ForOpenRoot(binding, SignedDirectory(binding.SourceUri), "root-1"));
        Assert.Throws<ArgumentException>(() => ManagedBrowserResponse.For(request,
            new ManagedBrowserDirectoryMutationResult(ManagedBrowserDirectoryMutationResultKind.DirectoryEntered)));
    }

    private static ManagedBrowserDirectorySnapshot SignedDirectory(string sourceUri)
    {
        var unsigned = new ManagedBrowserDirectorySnapshot("synthetic-uid", "directory-generation-1", "parent-1", "/parent",
            ["root-1", "parent-1"], 1, true, true,
            [new ManagedBrowserSourceItem("child-1", ManagedBrowserSourceItemKind.Folder, "child")], new string('0', 64));
        return new ManagedBrowserDirectorySnapshot(unsigned.BaiduUserId, unsigned.GenerationToken, unsigned.ParentExactId,
            unsigned.CanonicalPath, unsigned.AncestorExactIds, unsigned.TotalCount, true, true, unsigned.Items,
            unsigned.ComputeFingerprint(sourceUri));
    }

    private static ManagedBrowserSourceSnapshot SignedSource(string sourceUri)
    {
        var unsigned = new ManagedBrowserSourceSnapshot("synthetic-uid", "source-generation-1", 1, true, true,
            [new ManagedBrowserSourceItem("source-1", ManagedBrowserSourceItemKind.File, "file")], new string('0', 64));
        return new ManagedBrowserSourceSnapshot(unsigned.BaiduUserId, unsigned.GenerationToken, unsigned.TotalCount, true, true,
            unsigned.Items, unsigned.ComputeFingerprint(sourceUri));
    }

    private static IEnumerable<ManagedBrowserRequest> FixedRequests()
    {
        var binding = Binding();
        var directory = SignedDirectory(binding.SourceUri);
        var payloads = new (ManagedBrowserAction Action, ManagedBrowserRequestPayload Payload)[]
        {
            (ManagedBrowserAction.OpenTransferDirectoryRoot,
                ManagedBrowserDirectoryMutationRequest.ForOpenRoot(binding, directory, "root-1")),
            (ManagedBrowserAction.EnterTransferDirectory,
                ManagedBrowserDirectoryMutationRequest.ForEnterDirectory(binding, directory, "child-1", "/parent/child")),
            (ManagedBrowserAction.BeginCreateTransferDirectory,
                ManagedBrowserDirectoryMutationRequest.ForBeginCreate(binding, directory, "created")),
            (ManagedBrowserAction.ConfirmCreateTransferDirectory,
                ManagedBrowserDirectoryMutationRequest.ForConfirmCreate(binding, directory, "created")),
            (ManagedBrowserAction.CancelTransferPicker,
                ManagedBrowserDirectoryMutationRequest.ForCancelPicker(binding, directory)),
            (ManagedBrowserAction.ObserveTransferPickerClosed,
                new ManagedBrowserObserveTransferPickerClosedRequest(binding, SignedSource(binding.SourceUri))),
        };
        return payloads.Select(pair => new ManagedBrowserRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 7,
            pair.Action, pair.Payload));
    }

    private static ManagedBrowserOpenTransferSourceRequest Binding() => new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"), "synthetic-target",
        new Uri("http://127.0.0.1:9222/"), 30_000,
        "https://pan.baidu.com/s/synthetic_-123?pwd=a1B2", "synthetic-uid");

    private static void AssertProtocolError(JsonObject json, ManagedBrowserProtocolError expected)
    {
        var error = Assert.Throws<ManagedBrowserProtocolException>(() =>
            ManagedBrowserProtocol.DecodeRequest(Encoding.UTF8.GetBytes(json.ToJsonString())));
        Assert.Equal(expected, error.Error);
    }
}
