using BaiduBatchTransfer.App.Features.ManagedBrowser;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class ManagedBrowserProtocolTests
{
    private static readonly Guid CorrelationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OperationId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid AccountId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly string Nonce = new('a', 64);

    [Fact]
    public async Task FrameUsesFourByteBigEndianLengthAndUtf8Json()
    {
        var request = HandshakeRequest();
        await using var stream = new MemoryStream();

        await ManagedBrowserProtocol.WriteRequestAsync(stream, request, CancellationToken.None);

        var bytes = stream.ToArray();
        Assert.Equal(bytes.Length - 4, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(0, 4)));
        var json = new UTF8Encoding(false, true).GetString(bytes.AsSpan(4));
        Assert.Contains("\"protocolVersion\":3", json, StringComparison.Ordinal);
        Assert.Contains("\"executionEngineVersion\":\"ManagedChromium-v3\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\uFEFF", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FragmentedAndCoalescedFramesAreReadExactlyOnce()
    {
        var first = ManagedBrowserProtocol.EncodeRequest(HandshakeRequest());
        var secondRequest = Request(ManagedBrowserAction.Health, ManagedBrowserRequestPayload.Empty);
        var second = ManagedBrowserProtocol.EncodeRequest(secondRequest);
        await using var stream = new FragmentedReadStream(first.Concat(second).ToArray(), 1, 2, 3, 5, 8);

        var decodedFirst = await ManagedBrowserProtocol.ReadRequestAsync(stream, CancellationToken.None);
        var decodedSecond = await ManagedBrowserProtocol.ReadRequestAsync(stream, CancellationToken.None);
        var eof = await ManagedBrowserProtocol.ReadRequestAsync(stream, CancellationToken.None);

        Assert.Equal(ManagedBrowserAction.Handshake, decodedFirst!.Action);
        Assert.Equal(ManagedBrowserAction.Health, decodedSecond!.Action);
        Assert.Null(eof);
    }

    [Fact]
    public async Task CleanEofIsDistinctFromTruncatedHeaderAndPayload()
    {
        Assert.Null(await ManagedBrowserProtocol.ReadRequestAsync(
            new MemoryStream(), CancellationToken.None));

        var headerError = await Assert.ThrowsAsync<ManagedBrowserProtocolException>(() =>
            ManagedBrowserProtocol.ReadRequestAsync(
                new MemoryStream([0, 0]), CancellationToken.None));
        Assert.Equal(ManagedBrowserProtocolError.UnexpectedEof, headerError.Error);

        var payloadError = await Assert.ThrowsAsync<ManagedBrowserProtocolException>(() =>
            ManagedBrowserProtocol.ReadRequestAsync(
                new MemoryStream([0, 0, 0, 4, (byte)'{']), CancellationToken.None));
        Assert.Equal(ManagedBrowserProtocolError.UnexpectedEof, payloadError.Error);
    }

    [Fact]
    public async Task PendingReadHonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        await using var stream = new NeverCompletingReadStream();

        var pending = ManagedBrowserProtocol.ReadRequestAsync(stream, cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1048577)]
    public async Task InvalidFrameLengthIsRejectedBeforeReadingPayload(int length)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, length);
        var error = await Assert.ThrowsAsync<ManagedBrowserProtocolException>(() =>
            ManagedBrowserProtocol.ReadRequestAsync(new MemoryStream(header), CancellationToken.None));
        Assert.Equal(ManagedBrowserProtocolError.FrameSizeInvalid, error.Error);
    }

    [Fact]
    public async Task MalformedUtf8AndJsonHaveDistinctTypedErrors()
    {
        var utf8 = await Assert.ThrowsAsync<ManagedBrowserProtocolException>(() =>
            ManagedBrowserProtocol.ReadRequestAsync(Frame([0xff]), CancellationToken.None));
        Assert.Equal(ManagedBrowserProtocolError.MalformedUtf8, utf8.Error);

        var json = await Assert.ThrowsAsync<ManagedBrowserProtocolException>(() =>
            ManagedBrowserProtocol.ReadRequestAsync(Frame(Encoding.UTF8.GetBytes("{")), CancellationToken.None));
        Assert.Equal(ManagedBrowserProtocolError.MalformedJson, json.Error);
    }

    [Theory]
    [InlineData("protocolVersion", "1", ManagedBrowserProtocolError.ProtocolVersionMismatch)]
    [InlineData("protocolVersion", "2", ManagedBrowserProtocolError.ProtocolVersionMismatch)]
    [InlineData("executionEngineVersion", "\"ManagedChromium-v1\"", ManagedBrowserProtocolError.ExecutionEngineVersionMismatch)]
    [InlineData("executionEngineVersion", "\"ManagedChromium-v2\"", ManagedBrowserProtocolError.ExecutionEngineVersionMismatch)]
    [InlineData("action", "\"RunScript\"", ManagedBrowserProtocolError.UnknownAction)]
    public void VersionEngineAndActionMismatchesAreRejected(
        string property,
        string replacement,
        ManagedBrowserProtocolError expected)
    {
        var json = HandshakeJson().Replace(
            property switch
            {
                "protocolVersion" => "\"protocolVersion\":3",
                "executionEngineVersion" => "\"executionEngineVersion\":\"ManagedChromium-v3\"",
                _ => "\"action\":\"Handshake\"",
            }, $"\"{property}\":{replacement}", StringComparison.Ordinal);

        var error = Assert.Throws<ManagedBrowserProtocolException>(() =>
            ManagedBrowserProtocol.DecodeRequest(Encoding.UTF8.GetBytes(json)));
        Assert.Equal(expected, error.Error);
    }

    [Theory]
    [InlineData("\"protocolVersion\":3", "\"protocolVersion\":\"3\"", ManagedBrowserProtocolError.ProtocolVersionMismatch)]
    [InlineData("\"protocolVersion\":3", "\"protocolVersion\":3.0", ManagedBrowserProtocolError.ProtocolVersionMismatch)]
    [InlineData("\"executionEngineVersion\":\"ManagedChromium-v3\"", "\"executionEngineVersion\":1", ManagedBrowserProtocolError.ExecutionEngineVersionMismatch)]
    [InlineData("\"action\":\"Handshake\"", "\"action\":1", ManagedBrowserProtocolError.UnknownAction)]
    public void MismatchedDiscriminatorTypesUseTheSameTypedErrorsAsPython(
        string original,
        string replacement,
        ManagedBrowserProtocolError expected)
    {
        var json = HandshakeJson().Replace(original, replacement, StringComparison.Ordinal);

        var error = Assert.Throws<ManagedBrowserProtocolException>(() =>
            ManagedBrowserProtocol.DecodeRequest(Encoding.UTF8.GetBytes(json)));

        Assert.Equal(expected, error.Error);
    }

    [Fact]
    public void InvalidPayloadShapesUseTheCrossLanguageErrorCode()
    {
        var valid = HandshakeJson();
        var payloadStart = valid.IndexOf("\"payload\":", StringComparison.Ordinal);
        var invalidPayloads = new[]
        {
            valid[..payloadStart] + "\"payload\":[]}",
            valid.Replace("\"parentProcessId\":1234,", "", StringComparison.Ordinal),
        };

        foreach (var json in invalidPayloads)
        {
            var error = Assert.Throws<ManagedBrowserProtocolException>(() =>
                ManagedBrowserProtocol.DecodeRequest(Encoding.UTF8.GetBytes(json)));

            Assert.Equal(ManagedBrowserProtocolError.InvalidPayload, error.Error);
        }
    }

    [Fact]
    public void EveryBodyFieldTypeAndFormatFailureUsesCrossLanguageErrorParity()
    {
        var requestHandshake = HandshakeJson();
        var requestObserve = ObserveJson();
        var responseHandshake = HandshakeResponseJson();
        var responseHealth = HealthResponseJson();
        var responseShutdown = ShutdownResponseJson();
        var responseObserved = ObservedResponseJson();
        var cases = new (string Name, bool Response, string Json, ManagedBrowserProtocolError Error)[]
        {
            ("request nonce type", false, WithBodyValue(requestHandshake, "payload", "nonce", "1"), ManagedBrowserProtocolError.InvalidPayload),
            ("request nonce format", false, WithBodyValue(requestHandshake, "payload", "nonce", "\"short\""), ManagedBrowserProtocolError.InvalidPayload),
            ("request parent pid type", false, WithBodyValue(requestHandshake, "payload", "parentProcessId", "\"1234\""), ManagedBrowserProtocolError.InvalidPayload),
            ("request parent pid format", false, WithBodyValue(requestHandshake, "payload", "parentProcessId", "0"), ManagedBrowserProtocolError.InvalidPayload),
            ("request capabilities type", false, WithBodyValue(requestHandshake, "payload", "requiredCapabilities", "\"Health\""), ManagedBrowserProtocolError.InvalidPayload),
            ("request capabilities item type", false, WithBodyValue(requestHandshake, "payload", "requiredCapabilities", "[\"Health\",1,\"ObserveAccountIdentity\"]"), ManagedBrowserProtocolError.InvalidPayload),
            ("request capabilities set", false, WithBodyValue(requestHandshake, "payload", "requiredCapabilities", "[\"Health\",\"Shutdown\"]"), ManagedBrowserProtocolError.CapabilityMismatch),
            ("request capabilities order", false, WithBodyValue(requestHandshake, "payload", "requiredCapabilities", "[\"Shutdown\",\"Health\",\"ObserveAccountIdentity\"]"), ManagedBrowserProtocolError.CapabilityMismatch),
            ("request capabilities duplicate", false, WithBodyValue(requestHandshake, "payload", "requiredCapabilities", "[\"Health\",\"Health\"]"), ManagedBrowserProtocolError.CapabilityMismatch),
            ("request invalid schema precedes capability mismatch", false, WithBodyValue(WithBodyValue(requestHandshake, "payload", "nonce", "\"short\""), "payload", "requiredCapabilities", "[\"Health\",\"Shutdown\"]"), ManagedBrowserProtocolError.InvalidPayload),
            ("request browser id type", false, WithBodyValue(requestObserve, "payload", "browserId", "1"), ManagedBrowserProtocolError.InvalidPayload),
            ("request browser id format", false, WithBodyValue(requestObserve, "payload", "browserId", "\"not-a-guid\""), ManagedBrowserProtocolError.InvalidPayload),
            ("request target id type", false, WithBodyValue(requestObserve, "payload", "targetId", "1"), ManagedBrowserProtocolError.InvalidPayload),
            ("request target id format", false, WithBodyValue(requestObserve, "payload", "targetId", "\"\""), ManagedBrowserProtocolError.InvalidPayload),
            ("request endpoint type", false, WithBodyValue(requestObserve, "payload", "devToolsEndpoint", "1"), ManagedBrowserProtocolError.InvalidPayload),
            ("request endpoint format", false, WithBodyValue(requestObserve, "payload", "devToolsEndpoint", "\"http://localhost:43127/\""), ManagedBrowserProtocolError.InvalidPayload),
            ("request timeout type", false, WithBodyValue(requestObserve, "payload", "timeoutMilliseconds", "\"1\""), ManagedBrowserProtocolError.InvalidPayload),
            ("request timeout numeric type", false, WithBodyValue(requestObserve, "payload", "timeoutMilliseconds", "1.0"), ManagedBrowserProtocolError.InvalidPayload),
            ("request timeout format", false, WithBodyValue(requestObserve, "payload", "timeoutMilliseconds", "0"), ManagedBrowserProtocolError.InvalidPayload),
            ("response handshake kind type", true, WithBodyValue(responseHandshake, "result", "kind", "1"), ManagedBrowserProtocolError.InvalidPayload),
            ("response handshake kind format", true, WithBodyValue(responseHandshake, "result", "kind", "\"Healthy\""), ManagedBrowserProtocolError.InvalidPayload),
            ("response nonce type", true, WithBodyValue(responseHandshake, "result", "nonce", "1"), ManagedBrowserProtocolError.InvalidPayload),
            ("response nonce format", true, WithBodyValue(responseHandshake, "result", "nonce", "\"short\""), ManagedBrowserProtocolError.InvalidPayload),
            ("response parent pid type", true, WithBodyValue(responseHandshake, "result", "parentProcessId", "\"1234\""), ManagedBrowserProtocolError.InvalidPayload),
            ("response parent pid format", true, WithBodyValue(responseHandshake, "result", "parentProcessId", "0"), ManagedBrowserProtocolError.InvalidPayload),
            ("response helper pid type", true, WithBodyValue(responseHandshake, "result", "helperProcessId", "\"9876\""), ManagedBrowserProtocolError.InvalidPayload),
            ("response helper pid format", true, WithBodyValue(responseHandshake, "result", "helperProcessId", "0"), ManagedBrowserProtocolError.InvalidPayload),
            ("response capabilities type", true, WithBodyValue(responseHandshake, "result", "capabilities", "\"Health\""), ManagedBrowserProtocolError.InvalidPayload),
            ("response capabilities item type", true, WithBodyValue(responseHandshake, "result", "capabilities", "[\"Health\",1,\"ObserveAccountIdentity\"]"), ManagedBrowserProtocolError.InvalidPayload),
            ("response capabilities set", true, WithBodyValue(responseHandshake, "result", "capabilities", "[\"Health\",\"Shutdown\"]"), ManagedBrowserProtocolError.CapabilityMismatch),
            ("response capabilities order", true, WithBodyValue(responseHandshake, "result", "capabilities", "[\"Shutdown\",\"Health\",\"ObserveAccountIdentity\"]"), ManagedBrowserProtocolError.CapabilityMismatch),
            ("response capabilities duplicate", true, WithBodyValue(responseHandshake, "result", "capabilities", "[\"Health\",\"Health\"]"), ManagedBrowserProtocolError.CapabilityMismatch),
            ("response invalid schema precedes capability mismatch", true, WithBodyValue(WithBodyValue(responseHandshake, "result", "nonce", "\"short\""), "result", "capabilities", "[\"Health\",\"Shutdown\"]"), ManagedBrowserProtocolError.InvalidPayload),
            ("response health kind type", true, WithBodyValue(responseHealth, "result", "kind", "1"), ManagedBrowserProtocolError.InvalidPayload),
            ("response health kind format", true, WithBodyValue(responseHealth, "result", "kind", "\"ShuttingDown\""), ManagedBrowserProtocolError.InvalidPayload),
            ("response shutdown kind type", true, WithBodyValue(responseShutdown, "result", "kind", "1"), ManagedBrowserProtocolError.InvalidPayload),
            ("response shutdown kind format", true, WithBodyValue(responseShutdown, "result", "kind", "\"Healthy\""), ManagedBrowserProtocolError.InvalidPayload),
            ("response observed kind type", true, WithBodyValue(responseObserved, "result", "kind", "1"), ManagedBrowserProtocolError.InvalidPayload),
            ("response observed kind format", true, WithBodyValue(responseObserved, "result", "kind", "\"Unknown\""), ManagedBrowserProtocolError.InvalidPayload),
            ("response uid type", true, WithBodyValue(responseObserved, "result", "stableBaiduUserId", "1"), ManagedBrowserProtocolError.InvalidPayload),
            ("response uid format", true, WithBodyValue(responseObserved, "result", "stableBaiduUserId", "\"\""), ManagedBrowserProtocolError.InvalidPayload),
        };

        foreach (var testCase in cases)
        {
            var error = Assert.Throws<ManagedBrowserProtocolException>(() =>
                testCase.Response
                    ? ManagedBrowserProtocol.DecodeResponse(Encoding.UTF8.GetBytes(testCase.Json))
                    : ManagedBrowserProtocol.DecodeRequest(Encoding.UTF8.GetBytes(testCase.Json)));
            Assert.True(testCase.Error == error.Error,
                $"{testCase.Name}: expected {testCase.Error}, got {error.Error}");
        }
    }

    [Theory]
    [InlineData("unexpected")]
    [InlineData("cookie")]
    [InlineData("Authorization")]
    [InlineData("extractionCode")]
    public void UnknownAndSecretLikeFieldsAreRejectedRecursively(string field)
    {
        var json = HandshakeJson().Replace(
            "\"payload\":{",
            $"\"payload\":{{\"{field}\":\"do-not-accept\",", StringComparison.Ordinal);

        var error = Assert.Throws<ManagedBrowserProtocolException>(() =>
            ManagedBrowserProtocol.DecodeRequest(Encoding.UTF8.GetBytes(json)));
        Assert.Equal(
            field.Equals("unexpected", StringComparison.Ordinal)
                ? ManagedBrowserProtocolError.UnknownField
                : ManagedBrowserProtocolError.ForbiddenField,
            error.Error);
        Assert.DoesNotContain("do-not-accept", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateJsonFieldsAreRejected()
    {
        var json = HandshakeJson().Replace(
            "\"generation\":1",
            "\"generation\":1,\"generation\":1", StringComparison.Ordinal);

        var error = Assert.Throws<ManagedBrowserProtocolException>(() =>
            ManagedBrowserProtocol.DecodeRequest(Encoding.UTF8.GetBytes(json)));
        Assert.Equal(ManagedBrowserProtocolError.DuplicateField, error.Error);
    }

    [Fact]
    public void HandshakeCapabilitiesAreFixedAndNonceIsRedacted()
    {
        var request = HandshakeRequest();
        var payload = Assert.IsType<ManagedBrowserHandshakeRequest>(request.Payload);

        Assert.Equal(ManagedBrowserProtocol.RequiredCapabilities, payload.RequiredCapabilities);
        Assert.Equal(new[]
        {
            "Health", "Shutdown", "ObserveAccountIdentity", "OpenTransferSource",
            "ReadTransferSource", "ReadTransferDirectory", "OpenTransferPicker",
            "OpenTransferDirectoryRoot", "EnterTransferDirectory",
            "BeginCreateTransferDirectory", "ConfirmCreateTransferDirectory",
            "CancelTransferPicker", "ObserveTransferPickerClosed",
            "OpenTransferTargetRoot", "EnterTransferTargetDirectory",
            "ReadTransferTargetDirectory", "OpenTransferSourceAfterTargetRead",
            "ObserveTransferSubmissionReady", "SubmitTransferOnce",
            "ObserveTransferSubmissionOutcome", "ReadShareOfficialListing",
            "SubmitShareOnce", "ObserveShareSubmissionOutcome", "SubmitExtractionCode",
        }, payload.RequiredCapabilities);
        Assert.Equal(24, payload.RequiredCapabilities.Count);
        Assert.DoesNotContain(Nonce, request.ToString(), StringComparison.Ordinal);

        var altered = HandshakeJson().Replace(
            "\"ObserveAccountIdentity\"",
            "\"ExecuteJavaScript\"", StringComparison.Ordinal);
        var error = Assert.Throws<ManagedBrowserProtocolException>(() =>
            ManagedBrowserProtocol.DecodeRequest(Encoding.UTF8.GetBytes(altered)));
        Assert.Equal(ManagedBrowserProtocolError.CapabilityMismatch, error.Error);
    }

    [Fact]
    public async Task MaximumLegalFrameIsAcceptedAndOneByteOverIsRejectedBeforePayloadRead()
    {
        Assert.Equal(1_048_576, ManagedBrowserProtocol.MaximumFrameBytes);
        var legal = new byte[ManagedBrowserProtocol.MaximumFrameBytes];
        legal[0] = (byte)'{';
        var malformed = await Assert.ThrowsAsync<ManagedBrowserProtocolException>(() =>
            ManagedBrowserProtocol.ReadRequestAsync(Frame(legal), CancellationToken.None));
        Assert.Equal(ManagedBrowserProtocolError.MalformedJson, malformed.Error);

        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, ManagedBrowserProtocol.MaximumFrameBytes + 1);
        await using var unreadable = new HeaderOnlyStream(header);
        var tooLarge = await Assert.ThrowsAsync<ManagedBrowserProtocolException>(() =>
            ManagedBrowserProtocol.ReadRequestAsync(unreadable, CancellationToken.None));
        Assert.Equal(ManagedBrowserProtocolError.FrameSizeInvalid, tooLarge.Error);
        Assert.Equal(4, unreadable.BytesRead);
    }

    [Fact]
    public void ObserveIdentityBindingAndTypedResponsesAreStrictAndRedacted()
    {
        var browserId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var request = Request(
            ManagedBrowserAction.ObserveAccountIdentity,
            new ManagedBrowserObserveAccountIdentityRequest(
                browserId, "target-1", new Uri("http://127.0.0.1:43127/"), 30_000));
        var decoded = ManagedBrowserProtocol.DecodeRequest(
            ManagedBrowserProtocol.EncodeRequestPayload(request));
        var payload = Assert.IsType<ManagedBrowserObserveAccountIdentityRequest>(decoded.Payload);
        Assert.Equal(browserId, payload.BrowserId);
        Assert.Equal("target-1", payload.TargetId);

        const string uid = "stable-secret-uid";
        var response = ManagedBrowserResponse.For(
            request, new ManagedBrowserIdentityResult(ManagedBrowserIdentityResultKind.Observed, uid));
        var roundTrip = ManagedBrowserProtocol.DecodeResponse(
            ManagedBrowserProtocol.EncodeResponsePayload(response));
        var result = Assert.IsType<ManagedBrowserIdentityResult>(roundTrip.Result);
        Assert.Equal(uid, result.StableBaiduUserId);
        Assert.DoesNotContain(uid, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(uid, response.ToString(), StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() => new ManagedBrowserObserveAccountIdentityRequest(
            browserId, "", new Uri("http://127.0.0.1:43127/"), 30_000));

        var leaked = Encoding.UTF8.GetString(ManagedBrowserProtocol.EncodeResponsePayload(response))
            .Replace("\"stableBaiduUserId\":\"stable-secret-uid\"",
                "\"stableBaiduUserId\":\"stable-secret-uid\",\"pageUrl\":\"https://pan.baidu.com/private\"",
                StringComparison.Ordinal);
        var leakError = Assert.Throws<ManagedBrowserProtocolException>(() =>
            ManagedBrowserProtocol.DecodeResponse(Encoding.UTF8.GetBytes(leaked)));
        Assert.Equal(ManagedBrowserProtocolError.ForbiddenField, leakError.Error);
    }

    [Fact]
    public void BoundedTextUsesUnicodeScalarsUtf8BytesAndRejectsC0AndC1Controls()
    {
        var endpoint = new Uri("http://127.0.0.1:43127/");
        var browserId = Guid.NewGuid();
        _ = new ManagedBrowserObserveAccountIdentityRequest(browserId, string.Concat(Enumerable.Repeat("😀", 64)), endpoint, 1);

        Assert.Throws<ArgumentException>(() => new ManagedBrowserObserveAccountIdentityRequest(
            browserId, string.Concat(Enumerable.Repeat("😀", 65)), endpoint, 1));
        Assert.Throws<ArgumentException>(() => new ManagedBrowserObserveAccountIdentityRequest(
            browserId, "target\u0085id", endpoint, 1));
        Assert.Throws<ArgumentException>(() => new ManagedBrowserObserveAccountIdentityRequest(
            browserId, "target\0id", endpoint, 1));
    }

    [Theory]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://127.0.0.1:80/")]
    [InlineData("http://[::1]:43127/")]
    [InlineData("http://localhost:43127/")]
    [InlineData("HTTP://127.0.0.1:43127/")]
    [InlineData("http://127.000.000.001:43127/")]
    public void DevToolsEndpointRequiresExplicitNonDefaultIpv4LoopbackPort(string endpoint)
    {
        Assert.Throws<ArgumentException>(() => new ManagedBrowserObserveAccountIdentityRequest(
            Guid.NewGuid(), "target", new Uri(endpoint), 1));
    }

    [Fact]
    public void RepeatedRejectedSecretDocumentsLeaveParserUsable()
    {
        var invalid = HandshakeJson().Replace(
            "\"payload\":{", "\"payload\":{\"cookie\":\"secret\",", StringComparison.Ordinal);

        for (var index = 0; index < 2_000; index++)
        {
            var error = Assert.Throws<ManagedBrowserProtocolException>(() =>
                ManagedBrowserProtocol.DecodeRequest(Encoding.UTF8.GetBytes(invalid)));
            Assert.Equal(ManagedBrowserProtocolError.ForbiddenField, error.Error);
        }

        Assert.Equal(ManagedBrowserAction.Handshake,
            ManagedBrowserProtocol.DecodeRequest(
                ManagedBrowserProtocol.EncodeRequestPayload(HandshakeRequest())).Action);
    }

    [Fact]
    public void HelperSessionRequestLimitIsFixedForBothImplementations()
    {
        Assert.Equal(1_024, ManagedBrowserProtocol.MaximumRequestsPerSession);
    }

    [Theory]
    [InlineData("https://pan.baidu.com/", ManagedBrowserRoute.PanRoot)]
    [InlineData("https://passport.baidu.com/", ManagedBrowserRoute.PassportRoot)]
    public void OnlyExactHttpsTopLevelRoutesAreAllowed(string value, ManagedBrowserRoute expected)
    {
        Assert.Equal(expected, ManagedBrowserRoutePolicy.Classify(new Uri(value)));
    }

    [Theory]
    [InlineData("http://pan.baidu.com/")]
    [InlineData("https://user@pan.baidu.com/")]
    [InlineData("https://pan.baidu.com:444/")]
    [InlineData("https://sub.pan.baidu.com/")]
    [InlineData("https://pan.baidu.com.evil.test/")]
    [InlineData("https://pan.baidu.com/disk/home")]
    [InlineData("https://pan.baidu.com/?query=1")]
    public void LookalikeOrNonRootRoutesAreRejected(string value)
    {
        Assert.Equal(ManagedBrowserRoute.Rejected, ManagedBrowserRoutePolicy.Classify(new Uri(value)));
    }

    [Fact]
    public void ScreenshotConfirmedPanMainCanBeObservedWithoutBeingLoginProof()
    {
        var route = ManagedBrowserRoutePolicy.Classify(new Uri("https://pan.baidu.com/disk/main#/index?category=all"));

        Assert.NotEqual(ManagedBrowserRoute.Rejected, route);
        Assert.NotEqual(ManagedBrowserRoute.PanRoot, route);
        Assert.True(ManagedBrowserRoutePolicy.CanObserveIdentity(route));
        Assert.True(ManagedBrowserRoutePolicy.CanTransition(ManagedBrowserRoute.PanRoot, route));
        Assert.True(ManagedBrowserRoutePolicy.CanTransition(ManagedBrowserRoute.PassportRoot, route));
        Assert.True(ManagedBrowserRoutePolicy.CanTransition(route, route));
        Assert.True(ManagedBrowserRoutePolicy.CanTransition(route, ManagedBrowserRoute.PanRoot));
        Assert.True(ManagedBrowserRoutePolicy.CanTransition(route, ManagedBrowserRoute.PassportRoot));
        Assert.False(ManagedBrowserRoutePolicy.CanTransition(route, ManagedBrowserRoute.Rejected));
        Assert.False(ManagedBrowserRoutePolicy.CanTransition(ManagedBrowserRoute.Rejected, route));
        Assert.False(ManagedBrowserRoutePolicy.CanObserveIdentity(ManagedBrowserRoute.PassportRoot));
    }

    [Fact]
    public void BaiduHomeFlowRedirectStillClassifiesAsExactPanMain()
    {
        var route = ManagedBrowserRoutePolicy.Classify(
            new Uri("https://pan.baidu.com/disk/main?from=homeFlow#/index?category=all"));

        Assert.Equal(ManagedBrowserRoute.PanMain, route);
        Assert.True(ManagedBrowserRoutePolicy.CanObserveIdentity(route));
    }

    [Theory]
    [InlineData("http://pan.baidu.com/disk/main#/index?category=all")]
    [InlineData("https://user@pan.baidu.com/disk/main#/index?category=all")]
    [InlineData("https://pan.baidu.com:444/disk/main#/index?category=all")]
    [InlineData("https://pan.baidu.com.evil.test/disk/main#/index?category=all")]
    [InlineData("https://passport.baidu.com/disk/main#/index?category=all")]
    [InlineData("https://pan.baidu.com/disk/main/extra#/index?category=all")]
    [InlineData("https://pan.baidu.com/disk/main?redirect=1#/index?category=all")]
    [InlineData("https://pan.baidu.com/disk/main#/index?category=all&redirect=1")]
    [InlineData("https://pan.baidu.com/disk/main#/index?category=all&category=other")]
    [InlineData("https://pan.baidu.com/disk/main#/share?category=all")]
    public void PanMainLookalikesRemainRejected(string value)
    {
        Assert.Equal(ManagedBrowserRoute.Rejected, ManagedBrowserRoutePolicy.Classify(new Uri(value)));
    }

    [Fact]
    public void ResponseMustMatchEveryRequestIdentityField()
    {
        var request = Request(ManagedBrowserAction.Health, ManagedBrowserRequestPayload.Empty);
        var response = ManagedBrowserResponse.For(request, ManagedBrowserHealthResult.Healthy);
        ManagedBrowserProtocol.ValidateResponseFor(request, response);

        var wrong = new ManagedBrowserResponse(
            response.CorrelationId,
            Guid.NewGuid(),
            response.AccountId,
            response.Generation,
            response.Action,
            response.Result);
        var error = Assert.Throws<ManagedBrowserProtocolException>(() =>
            ManagedBrowserProtocol.ValidateResponseFor(request, wrong));
        Assert.Equal(ManagedBrowserProtocolError.ResponseIdentityMismatch, error.Error);
    }

    private static ManagedBrowserRequest HandshakeRequest() =>
        Request(ManagedBrowserAction.Handshake,
            new ManagedBrowserHandshakeRequest(Nonce, 1234, ManagedBrowserProtocol.RequiredCapabilities));

    private static ManagedBrowserRequest Request(
        ManagedBrowserAction action,
        ManagedBrowserRequestPayload payload) =>
        new(CorrelationId, OperationId, AccountId, 1, action, payload);

    private static string HandshakeJson() =>
        Encoding.UTF8.GetString(ManagedBrowserProtocol.EncodeRequestPayload(HandshakeRequest()));

    private static string ObserveJson() => Encoding.UTF8.GetString(
        ManagedBrowserProtocol.EncodeRequestPayload(Request(
            ManagedBrowserAction.ObserveAccountIdentity,
            new ManagedBrowserObserveAccountIdentityRequest(
                Guid.Parse("44444444-4444-4444-4444-444444444444"),
                "target-1", new Uri("http://127.0.0.1:43127/"), 30_000))));

    private static string HandshakeResponseJson() => Encoding.UTF8.GetString(
        ManagedBrowserProtocol.EncodeResponsePayload(ManagedBrowserResponse.For(
            HandshakeRequest(), new ManagedBrowserHandshakeResult(
                Nonce, 1234, 9876, ManagedBrowserProtocol.RequiredCapabilities))));

    private static string HealthResponseJson()
    {
        var request = Request(ManagedBrowserAction.Health, ManagedBrowserRequestPayload.Empty);
        return Encoding.UTF8.GetString(ManagedBrowserProtocol.EncodeResponsePayload(
            ManagedBrowserResponse.For(request, ManagedBrowserHealthResult.Healthy)));
    }

    private static string ShutdownResponseJson()
    {
        var request = Request(ManagedBrowserAction.Shutdown, ManagedBrowserRequestPayload.Empty);
        return Encoding.UTF8.GetString(ManagedBrowserProtocol.EncodeResponsePayload(
            ManagedBrowserResponse.For(request, ManagedBrowserShutdownResult.ShuttingDown)));
    }

    private static string ObservedResponseJson()
    {
        var request = Request(
            ManagedBrowserAction.ObserveAccountIdentity,
            new ManagedBrowserObserveAccountIdentityRequest(
                Guid.Parse("44444444-4444-4444-4444-444444444444"),
                "target-1", new Uri("http://127.0.0.1:43127/"), 30_000));
        return Encoding.UTF8.GetString(ManagedBrowserProtocol.EncodeResponsePayload(
            ManagedBrowserResponse.For(request, new ManagedBrowserIdentityResult(
                ManagedBrowserIdentityResultKind.Observed, "stable-uid"))));
    }

    private static string WithBodyValue(
        string json, string bodyName, string fieldName, string rawJsonValue)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        root[bodyName]!.AsObject()[fieldName] = JsonNode.Parse(rawJsonValue);
        return root.ToJsonString();
    }

    private static MemoryStream Frame(byte[] payload)
    {
        var bytes = new byte[payload.Length + 4];
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(0, 4), payload.Length);
        payload.CopyTo(bytes, 4);
        return new MemoryStream(bytes);
    }

    private sealed class FragmentedReadStream(byte[] bytes, params int[] chunks) : Stream
    {
        private int offset;
        private int chunkIndex;

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (offset >= bytes.Length) return 0;
            var chunk = chunks[chunkIndex++ % chunks.Length];
            var count = Math.Min(Math.Min(chunk, buffer.Length), bytes.Length - offset);
            bytes.AsMemory(offset, count).CopyTo(buffer);
            offset += count;
            return count;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position { get => offset; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class HeaderOnlyStream(byte[] header) : Stream
    {
        private int offset;
        public int BytesRead { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (offset >= header.Length) throw new InvalidOperationException("Payload read must not be attempted.");
            var count = Math.Min(buffer.Length, header.Length - offset);
            header.AsMemory(offset, count).CopyTo(buffer);
            offset += count;
            BytesRead += count;
            return ValueTask.FromResult(count);
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class NeverCompletingReadStream : Stream
    {
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
