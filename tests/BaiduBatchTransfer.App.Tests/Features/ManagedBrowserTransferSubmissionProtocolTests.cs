using BaiduBatchTransfer.App.Features.ManagedBrowser;
using BaiduBatchTransfer.Core.Domain;
using System.Text;
using System.Text.Json.Nodes;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class ManagedBrowserTransferSubmissionProtocolTests
{
    private static readonly Guid CorrelationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OperationId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid AccountId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid BrowserId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    [Fact]
    public void FinalSubmissionActionsAndEnumsAreStableAndOrdered()
    {
        Assert.Equal(19, (int)ManagedBrowserAction.ObserveTransferSubmissionReady);
        Assert.Equal(20, (int)ManagedBrowserAction.SubmitTransferOnce);
        Assert.Equal(21, (int)ManagedBrowserAction.ObserveTransferSubmissionOutcome);
        Assert.Equal(new[] { 1, 2, 3 }, Enum.GetValues<ManagedBrowserTransferSubmitDispatchState>().Select(value => (int)value));
        Assert.Equal(Enumerable.Range(1, 11), Enum.GetValues<ManagedBrowserTransferOutcomeKind>().Select(value => (int)value));
    }

    [Fact]
    public void ThreeFixedActionsRoundTripWithStrictActionPayloadAndResultPairs()
    {
        var context = Context();
        var readiness = new ManagedBrowserTransferSubmissionReadiness(context, 1);
        var cases = new (ManagedBrowserAction Action, ManagedBrowserRequestPayload Payload, ManagedBrowserResult Result)[]
        {
            (ManagedBrowserAction.ObserveTransferSubmissionReady,
                new ManagedBrowserObserveTransferSubmissionReadyRequest(context),
                new ManagedBrowserTransferSubmissionReadinessResult(
                    ManagedBrowserTransferSubmissionReadinessResultKind.Ready, readiness)),
            (ManagedBrowserAction.SubmitTransferOnce,
                new ManagedBrowserSubmitTransferOnceRequest(context, readiness.ReadinessFingerprint),
                new ManagedBrowserSubmitTransferOnceResult(
                    ManagedBrowserTransferSubmitResultKind.ClickAttempted,
                    ManagedBrowserTransferSubmitDispatchState.ClickDispatched)),
            (ManagedBrowserAction.ObserveTransferSubmissionOutcome,
                new ManagedBrowserObserveTransferSubmissionOutcomeRequest(context, readiness.ReadinessFingerprint),
                ManagedBrowserTransferSubmissionOutcomeResult.CreateAcceptedBound(
                    "official-request-1", "selected-target-1", "source-1", "destination-1")),
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
            Assert.Throws<ArgumentException>(() => ManagedBrowserResponse.For(
                Request(ManagedBrowserAction.Health, ManagedBrowserRequestPayload.Empty), item.Result));
        }
    }

    [Fact]
    public void ReadinessFingerprintIsVersionedLengthPrefixedStrictUtf8AndBindsEveryImmutableField()
    {
        var context = Context();
        var baseline = new ManagedBrowserTransferSubmissionReadiness(context, 1);
        Assert.Equal(64, baseline.ReadinessFingerprint.Length);
        Assert.Equal(baseline.ReadinessFingerprint,
            new ManagedBrowserTransferSubmissionReadiness(Context(), 1).ReadinessFingerprint);
        Assert.False(context.EqualsEvidence(Context(timeout: 29_999)));

        var variants = new[]
        {
            Context(browserId: Guid.Parse("55555555-5555-5555-5555-555555555555")),
            Context(targetId: "target-2"), Context(endpoint: "http://127.0.0.1:43128/"), Context(timeout: 29_999),
            Context(sourceUri: "https://pan.baidu.com/s/other_source?pwd=C3d4"),
            Context(uid: "uid-2"), Context(sourceGeneration: "source-generation-2"),
            Context(sourceObservation: Fingerprint('2')), Context(sourceFingerprint: Fingerprint('3')),
            Context(sourceExactId: "source-2"), Context(sourceKind: ManagedBrowserSourceItemKind.Folder),
            Context(selectedTargetExactId: "selected-target-2"), Context(selectedPath: "/other"),
            Context(targetBaseline: Fingerprint('4')), Context(duplicatePolicy: DuplicatePolicy.Rename),
        };
        Assert.All(variants, variant => Assert.NotEqual(baseline.ReadinessFingerprint,
            new ManagedBrowserTransferSubmissionReadiness(variant, 1).ReadinessFingerprint));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ManagedBrowserTransferSubmissionReadiness(context, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ManagedBrowserTransferSubmissionReadiness(context, 2));
        Assert.Throws<ArgumentException>(() => Context(uid: "bad\ud800"));
    }

    [Fact]
    public void ReadinessFingerprintMatchesIndependentUtf8KnownAnswerAndSeparatesAmbiguousFieldBoundaries()
    {
        var multilingual = Context(targetId: "目标-一", uid: "用户-甲", sourceGeneration: "代次-乙",
            selectedPath: "/准备/应用", duplicatePolicy: DuplicatePolicy.Rename);
        var readiness = new ManagedBrowserTransferSubmissionReadiness(multilingual, 1);
        Assert.Equal("cb5757dad5aa8a2d2a5fca0dbd616a4ae9fd696c88a2147291c4c2367cd7c4b0",
            readiness.ReadinessFingerprint);

        var left = Context(uid: "甲", sourceGeneration: "乙丙");
        var right = Context(uid: "甲乙", sourceGeneration: "丙");
        Assert.Equal(Encoding.UTF8.GetBytes(left.BaiduUserId + left.SourceGenerationToken),
            Encoding.UTF8.GetBytes(right.BaiduUserId + right.SourceGenerationToken));
        Assert.NotEqual(new ManagedBrowserTransferSubmissionReadiness(left, 1).ReadinessFingerprint,
            new ManagedBrowserTransferSubmissionReadiness(right, 1).ReadinessFingerprint);
    }

    [Fact]
    public void SubmissionRequestsSerializeOnlyFixedImmutableEvidence()
    {
        var context = Context();
        var readiness = new ManagedBrowserTransferSubmissionReadiness(context, 1);
        var requests = new[]
        {
            Request(ManagedBrowserAction.ObserveTransferSubmissionReady,
                new ManagedBrowserObserveTransferSubmissionReadyRequest(context)),
            Request(ManagedBrowserAction.SubmitTransferOnce,
                new ManagedBrowserSubmitTransferOnceRequest(context, readiness.ReadinessFingerprint)),
            Request(ManagedBrowserAction.ObserveTransferSubmissionOutcome,
                new ManagedBrowserObserveTransferSubmissionOutcomeRequest(context, readiness.ReadinessFingerprint)),
        };

        foreach (var request in requests)
        {
            var json = Encoding.UTF8.GetString(ManagedBrowserProtocol.EncodeRequestPayload(request));
            foreach (var forbidden in new[]
                     { "selector", "script", "url", "buttonText", "name", "toast", "pageText", "cookie" })
                Assert.DoesNotContain($"\"{forbidden}\"", json, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"sourceUri\":\"https://pan.baidu.com/s/source_1?pwd=A1b2\"", json,
                StringComparison.Ordinal);
            Assert.DoesNotContain("serverName", json, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"selectedTargetExactId\":\"selected-target-1\"", json, StringComparison.Ordinal);
            Assert.Contains("\"targetBaselineFingerprint\":", json, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("selector")]
    [InlineData("script")]
    [InlineData("url")]
    [InlineData("buttonText")]
    [InlineData("name")]
    [InlineData("toast")]
    [InlineData("pageText")]
    [InlineData("cookie")]
    public void DynamicOrSecretSubmissionFieldsAreForbidden(string field)
    {
        var context = Context();
        var request = Request(ManagedBrowserAction.ObserveTransferSubmissionReady,
            new ManagedBrowserObserveTransferSubmissionReadyRequest(context));
        var root = JsonNode.Parse(ManagedBrowserProtocol.EncodeRequestPayload(request))!.AsObject();
        root["payload"]!.AsObject()[field] = "private";
        var error = Assert.Throws<ManagedBrowserProtocolException>(() => ManagedBrowserProtocol.DecodeRequest(
            Encoding.UTF8.GetBytes(root.ToJsonString())));
        Assert.Equal(ManagedBrowserProtocolError.ForbiddenField, error.Error);
        Assert.DoesNotContain("private", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void FrozenSourceUriIsRequiredAndUnrelatedSubmissionFieldsRemainUnknown()
    {
        var request = Request(ManagedBrowserAction.ObserveTransferSubmissionReady,
            new ManagedBrowserObserveTransferSubmissionReadyRequest(Context()));
        var missing = JsonNode.Parse(ManagedBrowserProtocol.EncodeRequestPayload(request))!.AsObject();
        missing["payload"]!.AsObject().Remove("sourceUri");
        Assert.Equal(ManagedBrowserProtocolError.InvalidPayload,
            Assert.Throws<ManagedBrowserProtocolException>(() => ManagedBrowserProtocol.DecodeRequest(
                Encoding.UTF8.GetBytes(missing.ToJsonString()))).Error);

        var unknown = JsonNode.Parse(ManagedBrowserProtocol.EncodeRequestPayload(request))!.AsObject();
        unknown["payload"]!.AsObject()["retry"] = true;
        Assert.Equal(ManagedBrowserProtocolError.UnknownField,
            Assert.Throws<ManagedBrowserProtocolException>(() => ManagedBrowserProtocol.DecodeRequest(
                Encoding.UTF8.GetBytes(unknown.ToJsonString()))).Error);
    }

    [Fact]
    public void AcceptedBoundIsTheOnlyOutcomeThatCanCarryOfficialBindingFields()
    {
        var accepted = ManagedBrowserTransferSubmissionOutcomeResult.CreateAcceptedBound(
            "official-request-1", "selected-target-1", "source-1", "destination-1");
        Assert.Equal(ManagedBrowserTransferOutcomeKind.AcceptedBound, accepted.Kind);
        Assert.Equal(64, accepted.OfficialObservationFingerprint!.Length);
        Assert.Throws<ArgumentException>(() => new ManagedBrowserTransferSubmissionOutcomeResult(
            ManagedBrowserTransferOutcomeKind.UnboundAcknowledgement,
            "official-request-1", "selected-target-1", "source-1", "destination-1", Fingerprint('9')));

        foreach (var kind in Enum.GetValues<ManagedBrowserTransferOutcomeKind>().Where(
                     kind => kind != ManagedBrowserTransferOutcomeKind.AcceptedBound))
        {
            var result = new ManagedBrowserTransferSubmissionOutcomeResult(kind);
            Assert.Null(result.OfficialRequestId);
            Assert.Null(result.ExactTargetId);
            Assert.Null(result.SourceExactId);
            Assert.Null(result.DestinationExactId);
            Assert.Null(result.OfficialObservationFingerprint);
            var response = ManagedBrowserResponse.For(
                Request(ManagedBrowserAction.ObserveTransferSubmissionOutcome,
                    new ManagedBrowserObserveTransferSubmissionOutcomeRequest(
                        Context(), new ManagedBrowserTransferSubmissionReadiness(Context(), 1).ReadinessFingerprint)), result);
            Assert.IsType<ManagedBrowserTransferSubmissionOutcomeResult>(ManagedBrowserProtocol.DecodeResponse(
                ManagedBrowserProtocol.EncodeResponsePayload(response)).Result);
        }
    }

    [Fact]
    public void AcceptedBoundFingerprintMatchesIndependentUtf8KnownAnswerAndMutationIsRejectedOnDecode()
    {
        var accepted = ManagedBrowserTransferSubmissionOutcomeResult.CreateAcceptedBound(
            "请求-甲", "selected-target-1", "source-1", "destination-1");
        Assert.Equal("782c48017a8b9f8424b48788a746a59546076e20b7480096ab1ebf03164b0d73",
            accepted.OfficialObservationFingerprint);

        var context = Context();
        var readiness = new ManagedBrowserTransferSubmissionReadiness(context, 1);
        var request = Request(ManagedBrowserAction.ObserveTransferSubmissionOutcome,
            new ManagedBrowserObserveTransferSubmissionOutcomeRequest(context, readiness.ReadinessFingerprint));
        var json = JsonNode.Parse(ManagedBrowserProtocol.EncodeResponsePayload(
            ManagedBrowserResponse.For(request, accepted)))!.AsObject();
        json["result"]!["officialObservationFingerprint"] = Fingerprint('9');
        var error = Assert.Throws<ManagedBrowserProtocolException>(() =>
            ManagedBrowserProtocol.DecodeResponse(Encoding.UTF8.GetBytes(json.ToJsonString())));
        Assert.Equal(ManagedBrowserProtocolError.InvalidPayload, error.Error);
    }

    [Fact]
    public void ResponseValidationRequiresReadinessAndOfficialOutcomeParityWithRequest()
    {
        var context = Context();
        var readinessRequest = Request(ManagedBrowserAction.ObserveTransferSubmissionReady,
            new ManagedBrowserObserveTransferSubmissionReadyRequest(context));
        Assert.Throws<ManagedBrowserProtocolException>(() => ManagedBrowserProtocol.ValidateResponseFor(
            readinessRequest, ManagedBrowserResponse.For(readinessRequest,
                new ManagedBrowserTransferSubmissionReadinessResult(
                    ManagedBrowserTransferSubmissionReadinessResultKind.Ready,
                    new ManagedBrowserTransferSubmissionReadiness(Context(uid: "uid-2"), 1)))));
        Assert.Throws<ManagedBrowserProtocolException>(() => ManagedBrowserProtocol.ValidateResponseFor(
            readinessRequest, ManagedBrowserResponse.For(readinessRequest,
                new ManagedBrowserTransferSubmissionReadinessResult(
                    ManagedBrowserTransferSubmissionReadinessResultKind.Ready,
                    new ManagedBrowserTransferSubmissionReadiness(
                        Context(sourceUri: "https://pan.baidu.com/s/other_source?pwd=C3d4"), 1)))));

        var readiness = new ManagedBrowserTransferSubmissionReadiness(context, 1);
        var outcomeRequest = Request(ManagedBrowserAction.ObserveTransferSubmissionOutcome,
            new ManagedBrowserObserveTransferSubmissionOutcomeRequest(context, readiness.ReadinessFingerprint));
        foreach (var result in new[]
                 {
                     ManagedBrowserTransferSubmissionOutcomeResult.CreateAcceptedBound(
                         "official-request-1", "other-target", "source-1", "destination-1"),
                     ManagedBrowserTransferSubmissionOutcomeResult.CreateAcceptedBound(
                         "official-request-1", "selected-target-1", "other-source", "destination-1"),
                 })
            Assert.Throws<ManagedBrowserProtocolException>(() => ManagedBrowserProtocol.ValidateResponseFor(
                outcomeRequest, ManagedBrowserResponse.For(outcomeRequest, result)));
    }

    [Fact]
    public void ResultConstructorsRejectUnknownEnumsAndInconsistentDispatchStates()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ManagedBrowserSubmitTransferOnceResult(
            (ManagedBrowserTransferSubmitResultKind)999, ManagedBrowserTransferSubmitDispatchState.KnownNotDispatched));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ManagedBrowserTransferSubmissionOutcomeResult(
            (ManagedBrowserTransferOutcomeKind)999));
        Assert.Throws<ArgumentException>(() => new ManagedBrowserSubmitTransferOnceResult(
            ManagedBrowserTransferSubmitResultKind.ClickAttempted,
            ManagedBrowserTransferSubmitDispatchState.KnownNotDispatched));
        Assert.Throws<ArgumentException>(() => new ManagedBrowserSubmitTransferOnceResult(
            ManagedBrowserTransferSubmitResultKind.ReadinessMismatch,
            ManagedBrowserTransferSubmitDispatchState.ClickDispatched));
        _ = new ManagedBrowserSubmitTransferOnceResult(
            ManagedBrowserTransferSubmitResultKind.ClickAttempted,
            ManagedBrowserTransferSubmitDispatchState.DispatchOutcomeUnknown);
    }

    [Fact]
    public void SubmitAndOutcomeObservationRequestsRequireReadinessFingerprintParity()
    {
        var context = Context();
        Assert.Throws<ArgumentException>(() => new ManagedBrowserSubmitTransferOnceRequest(
            context, Fingerprint('9')));
        Assert.Throws<ArgumentException>(() => new ManagedBrowserObserveTransferSubmissionOutcomeRequest(
            context, Fingerprint('9')));
    }

    [Fact]
    public void SubmissionContextRequiresOneCanonicalFrozenSourceUri()
    {
        Assert.Equal("https://pan.baidu.com/s/source_1?pwd=A1b2", Context().SourceUri);
        foreach (var invalid in new string?[]
                 {
                     null, "", " https://pan.baidu.com/s/source_1?pwd=A1b2",
                     "http://pan.baidu.com/s/source_1", "https://pan.baidu.com/s/",
                     "https://pan.baidu.com/s/source_1/", "https://pan.baidu.com/s/source_1?pwd=abc",
                     "https://pan.baidu.com/s/source_1?pwd=abcd&x=1", "https://foreign.test/s/source_1",
                 })
            Assert.Throws<ArgumentException>(() => Context(sourceUri: invalid!));
    }

    [Fact]
    public void SubmitResultConstructorAndDecoderRejectEveryInvalidKindDispatchPair()
    {
        var valid = ValidSubmitPairs();
        var request = Request(ManagedBrowserAction.SubmitTransferOnce,
            new ManagedBrowserSubmitTransferOnceRequest(
                Context(), new ManagedBrowserTransferSubmissionReadiness(Context(), 1).ReadinessFingerprint));
        var template = JsonNode.Parse(ManagedBrowserProtocol.EncodeResponsePayload(
            ManagedBrowserResponse.For(request, new ManagedBrowserSubmitTransferOnceResult(
                ManagedBrowserTransferSubmitResultKind.ClickAttempted,
                ManagedBrowserTransferSubmitDispatchState.ClickDispatched))))!.AsObject();

        foreach (var kind in Enum.GetValues<ManagedBrowserTransferSubmitResultKind>())
        foreach (var state in Enum.GetValues<ManagedBrowserTransferSubmitDispatchState>())
        {
            if (valid.Contains((kind, state))) continue;
            Assert.Throws<ArgumentException>(() => new ManagedBrowserSubmitTransferOnceResult(kind, state));
            var json = template.DeepClone().AsObject();
            json["result"]!["kind"] = kind.ToString();
            json["result"]!["dispatchState"] = state.ToString();
            var error = Assert.Throws<ManagedBrowserProtocolException>(() =>
                ManagedBrowserProtocol.DecodeResponse(Encoding.UTF8.GetBytes(json.ToJsonString())));
            Assert.Equal(ManagedBrowserProtocolError.InvalidPayload, error.Error);
        }
    }

    [Fact]
    public void SubmitResultConstructorAndDecoderAcceptOnlyTheExplicitValidMatrix()
    {
        var request = Request(ManagedBrowserAction.SubmitTransferOnce,
            new ManagedBrowserSubmitTransferOnceRequest(
                Context(), new ManagedBrowserTransferSubmissionReadiness(Context(), 1).ReadinessFingerprint));
        foreach (var pair in ValidSubmitPairs())
        {
            var result = new ManagedBrowserSubmitTransferOnceResult(pair.Kind, pair.State);
            var decoded = ManagedBrowserProtocol.DecodeResponse(ManagedBrowserProtocol.EncodeResponsePayload(
                ManagedBrowserResponse.For(request, result)));
            var roundTrip = Assert.IsType<ManagedBrowserSubmitTransferOnceResult>(decoded.Result);
            Assert.Equal(pair.Kind, roundTrip.Kind);
            Assert.Equal(pair.State, roundTrip.DispatchState);
        }
    }

    private static HashSet<(ManagedBrowserTransferSubmitResultKind Kind,
        ManagedBrowserTransferSubmitDispatchState State)> ValidSubmitPairs() =>
    [
        (ManagedBrowserTransferSubmitResultKind.ClickAttempted, ManagedBrowserTransferSubmitDispatchState.ClickDispatched),
        (ManagedBrowserTransferSubmitResultKind.ClickAttempted, ManagedBrowserTransferSubmitDispatchState.DispatchOutcomeUnknown),
        (ManagedBrowserTransferSubmitResultKind.ReadinessMismatch, ManagedBrowserTransferSubmitDispatchState.KnownNotDispatched),
        (ManagedBrowserTransferSubmitResultKind.PageNotValidated, ManagedBrowserTransferSubmitDispatchState.KnownNotDispatched),
        (ManagedBrowserTransferSubmitResultKind.AccountIdentityMismatch, ManagedBrowserTransferSubmitDispatchState.KnownNotDispatched),
        (ManagedBrowserTransferSubmitResultKind.LoginRequired, ManagedBrowserTransferSubmitDispatchState.KnownNotDispatched),
        (ManagedBrowserTransferSubmitResultKind.VerificationRequired, ManagedBrowserTransferSubmitDispatchState.KnownNotDispatched),
        (ManagedBrowserTransferSubmitResultKind.BrowserUnavailable, ManagedBrowserTransferSubmitDispatchState.KnownNotDispatched),
        (ManagedBrowserTransferSubmitResultKind.BrowserUnavailable, ManagedBrowserTransferSubmitDispatchState.DispatchOutcomeUnknown),
        (ManagedBrowserTransferSubmitResultKind.TimedOut, ManagedBrowserTransferSubmitDispatchState.KnownNotDispatched),
        (ManagedBrowserTransferSubmitResultKind.TimedOut, ManagedBrowserTransferSubmitDispatchState.DispatchOutcomeUnknown),
        (ManagedBrowserTransferSubmitResultKind.Cancelled, ManagedBrowserTransferSubmitDispatchState.KnownNotDispatched),
        (ManagedBrowserTransferSubmitResultKind.Cancelled, ManagedBrowserTransferSubmitDispatchState.DispatchOutcomeUnknown),
    ];

    private static ManagedBrowserTransferSubmissionContext Context(
        Guid? browserId = null, string targetId = "target-1", string endpoint = "http://127.0.0.1:43127/",
        int timeout = 30_000,
        string sourceUri = "https://pan.baidu.com/s/source_1?pwd=A1b2",
        string uid = "uid-1", string sourceGeneration = "source-generation-1",
        string? sourceObservation = null, string? sourceFingerprint = null,
        string sourceExactId = "source-1", ManagedBrowserSourceItemKind sourceKind = ManagedBrowserSourceItemKind.File,
        string selectedTargetExactId = "selected-target-1", string selectedPath = "/prepared",
        string? targetBaseline = null, DuplicatePolicy duplicatePolicy = DuplicatePolicy.Skip) =>
        new(browserId ?? BrowserId, targetId, new Uri(endpoint), timeout, sourceUri, uid, sourceGeneration,
            sourceObservation ?? Fingerprint('a'), sourceFingerprint ?? Fingerprint('b'),
            sourceExactId, sourceKind, selectedTargetExactId, selectedPath,
            targetBaseline ?? Fingerprint('c'), duplicatePolicy);

    private static ManagedBrowserRequest Request(ManagedBrowserAction action, ManagedBrowserRequestPayload payload) =>
        new(CorrelationId, OperationId, AccountId, 7, action, payload);

    private static string Fingerprint(char value) => new(value, 64);
}
