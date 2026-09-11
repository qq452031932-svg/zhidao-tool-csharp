using BaiduBatchTransfer.App.Features.ManagedBrowser;
using System.Text;
using System.Text.Json.Nodes;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class ManagedBrowserTransferSourceProtocolTests
{
    private const string Source = "https://pan.baidu.com/s/synthetic_-123?pwd=a1B2";

    [Theory]
    [InlineData("correlationId", "\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\"")]
    [InlineData("operationId", "\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\"")]
    [InlineData("accountId", "\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\"")]
    [InlineData("generation", "8")]
    public void SourceHintCannotCompleteAnotherEnvelope(string field, string value)
    {
        var request = ManagedBrowserProtocol.DecodeRequest(Encoding.UTF8.GetBytes(RequestJson()));
        var json = JsonNode.Parse(ResponseJson("SourceOpened"))!;
        json[field] = JsonNode.Parse(value);
        var response = ManagedBrowserProtocol.DecodeResponse(Encoding.UTF8.GetBytes(json.ToJsonString()));
        var error = Assert.Throws<ManagedBrowserProtocolException>(() => ManagedBrowserProtocol.ValidateResponseFor(request, response));
        Assert.Equal(ManagedBrowserProtocolError.ResponseIdentityMismatch, error.Error);
    }

    [Fact]
    public void IdentityActionResponseCannotCompleteSourceRequest()
    {
        var request = ManagedBrowserProtocol.DecodeRequest(Encoding.UTF8.GetBytes(RequestJson()));
        var response = new ManagedBrowserResponse(request.CorrelationId, request.OperationId, request.AccountId,
            request.Generation, ManagedBrowserAction.ObserveAccountIdentity,
            new ManagedBrowserIdentityResult(ManagedBrowserIdentityResultKind.LoginRequired));
        var error = Assert.Throws<ManagedBrowserProtocolException>(() => ManagedBrowserProtocol.ValidateResponseFor(request, response));
        Assert.Equal(ManagedBrowserProtocolError.ResponseIdentityMismatch, error.Error);
    }

    [Theory]
    [InlineData("script", "\"click()\"", ManagedBrowserProtocolError.ForbiddenField)]
    [InlineData("extra", "{\"cookie\":\"private\"}", ManagedBrowserProtocolError.ForbiddenField)]
    [InlineData("extra", "{\"payload\":[{\"extractionCode\":\"1234\"}]}", ManagedBrowserProtocolError.ForbiddenField)]
    public void SourceRequestCannotSmuggleNestedFields(string field, string value, ManagedBrowserProtocolError expected)
    {
        var json = JsonNode.Parse(RequestJson())!;
        json["payload"]![field] = JsonNode.Parse(value);
        Reject(json.ToJsonString(), expected);
    }

    [Fact]
    public void SourceRequestRejectsDuplicateAndMissingFields()
    {
        var json = RequestJson().Replace("\"timeoutMilliseconds\":1000", "\"timeoutMilliseconds\":1000,\"timeoutMilliseconds\":1000", StringComparison.Ordinal);
        Reject(json, ManagedBrowserProtocolError.DuplicateField);
        var missing = JsonNode.Parse(RequestJson())!;
        missing["payload"]!.AsObject().Remove("expectedBaiduUserId");
        Reject(missing.ToJsonString(), ManagedBrowserProtocolError.InvalidPayload);
    }

    [Fact]
    public void OlderHelperCapabilitySetCannotNegotiateTheNewCommand()
    {
        var request = new ManagedBrowserRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1,
            ManagedBrowserAction.Handshake, new ManagedBrowserHandshakeRequest(new string('a', 64), 1234,
                ManagedBrowserProtocol.RequiredCapabilities));
        var json = JsonNode.Parse(ManagedBrowserProtocol.EncodeRequestPayload(request))!;
        json["payload"]!["requiredCapabilities"] = new JsonArray("Health", "Shutdown", "ObserveAccountIdentity");
        Reject(json.ToJsonString(), ManagedBrowserProtocolError.CapabilityMismatch);
    }

    [Fact]
    public void BothRuntimesUseTheSameExactUtf8WireFixture()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "tools", "managed_browser_helper")))
            root = root.Parent;
        Assert.NotNull(root);
        var fixture = File.ReadAllText(Path.Combine(root.FullName, "tools", "managed_browser_helper", "tests",
            "fixtures", "open_transfer_source.json"), Encoding.UTF8).TrimEnd('\r', '\n');
        var payload = Encoding.UTF8.GetBytes(fixture);
        var request = ManagedBrowserProtocol.DecodeRequest(payload);
        Assert.Equal(payload, ManagedBrowserProtocol.EncodeRequest(request)[4..]);
        foreach (var kind in Enum.GetValues<ManagedBrowserTransferSourceResultKind>())
        {
            var response = ManagedBrowserResponse.For(request, new ManagedBrowserTransferSourceResult(kind));
            var expected = JsonNode.Parse(fixture)!.AsObject();
            expected.Remove("payload");
            expected["result"] = new JsonObject { ["kind"] = kind.ToString() };
            Assert.Equal(Encoding.UTF8.GetBytes(expected.ToJsonString(new() { WriteIndented = false })),
                ManagedBrowserProtocol.EncodeResponse(response)[4..]);
        }
    }

    [Fact]
    public void MaximumLengthSourceAndUtf8IdentityAreAccepted()
    {
        var source = "https://pan.baidu.com/s/" + new string('a', 128) + "?pwd=a1B2";
        var json = JsonNode.Parse(RequestJson(source))!;
        json["payload"]!["expectedBaiduUserId"] = string.Concat(Enumerable.Repeat("😀", 64));
        _ = ManagedBrowserProtocol.DecodeRequest(Encoding.UTF8.GetBytes(json.ToJsonString()));
    }

    [Fact]
    public void SourceActionCannotUseIdentityPayloadOrClaimIdentityAsResult()
    {
        var request = ManagedBrowserProtocol.DecodeRequest(Encoding.UTF8.GetBytes(RequestJson()));
        Assert.Throws<ArgumentException>(() => new ManagedBrowserRequest(request.CorrelationId,
            request.OperationId, request.AccountId, request.Generation, request.Action,
            new ManagedBrowserObserveAccountIdentityRequest(Guid.NewGuid(), "target", new Uri("http://127.0.0.1:9222/"), 1000)));
        Assert.Throws<ArgumentException>(() => ManagedBrowserResponse.For(request,
            new ManagedBrowserIdentityResult(ManagedBrowserIdentityResultKind.LoginRequired)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ManagedBrowserTransferSourceResult((ManagedBrowserTransferSourceResultKind)0));
    }

    [Theory]
    [InlineData("https://pan.baidu.com/s/a")]
    [InlineData(Source)]
    public void CanonicalSourceRequestRoundtripsWithoutLosingBinding(string source)
    {
        var original = RequestJson(source);
        var request = ManagedBrowserProtocol.DecodeRequest(Encoding.UTF8.GetBytes(original));
        var actual = JsonNode.Parse(ManagedBrowserProtocol.EncodeRequestPayload(request))!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(original), actual));
        Assert.Contains("OpenTransferSource", ManagedBrowserProtocol.RequiredCapabilities);
        foreach (var secret in new[] { source, "synthetic-private-uid", "synthetic-target", "127.0.0.1" })
        {
            Assert.DoesNotContain(secret, request.ToString());
            Assert.DoesNotContain(secret, request.Payload.ToString());
        }
    }

    [Theory]
    [InlineData("http://pan.baidu.com/s/a")]
    [InlineData("https://pan.baidu.com.evil/s/a")]
    [InlineData("https://pan.baidu.com:443/s/a")]
    [InlineData("https://x@pan.baidu.com/s/a")]
    [InlineData("https://pan.baidu.com/s/a#x")]
    [InlineData("https://pan.baidu.com/s/a?")]
    [InlineData("https://pan.baidu.com/s/a?pwd=123")]
    [InlineData("https://pan.baidu.com/s/a?pwd=12345")]
    [InlineData("https://pan.baidu.com/s/a?pwd=1234&x=1")]
    [InlineData("https://pan.baidu.com/s/a?pwd=1234&pwd=5678")]
    [InlineData("https://pan.baidu.com/s/%61")]
    [InlineData("https://pan.baidu.com/s/../s/a")]
    [InlineData("https://pan.baidu.com/s/a/")]
    [InlineData("https://pan.baidu.com/s/a\n")]
    [InlineData("https://pan.baidu.com/s/a ")]
    [InlineData("https://pan.baidu.com/s/中文")]
    [InlineData("HTTPS://PAN.BAIDU.COM/s/a")]
    [InlineData("https://pan.baidu.com\\s\\a")]
    public void NonCanonicalOrUnscopedSourceIsRejected(string source) =>
        Reject(RequestJson(source), ManagedBrowserProtocolError.InvalidPayload);

    [Theory]
    [InlineData("browserId", "\"00000000-0000-0000-0000-000000000000\"")]
    [InlineData("targetId", "\"\"")]
    [InlineData("devToolsEndpoint", "\"http://localhost:9222/\"")]
    [InlineData("timeoutMilliseconds", "0")]
    [InlineData("timeoutMilliseconds", "120001")]
    [InlineData("timeoutMilliseconds", "1.0")]
    [InlineData("sourceUri", "null")]
    [InlineData("expectedBaiduUserId", "null")]
    [InlineData("expectedBaiduUserId", "\" \"")]
    public void MalformedPayloadIsRejected(string field, string value)
    {
        var json = JsonNode.Parse(RequestJson())!;
        json["payload"]![field] = JsonNode.Parse(value);
        Reject(json.ToJsonString(), ManagedBrowserProtocolError.InvalidPayload);
    }

    [Fact]
    public void OverlongAndInvalidUtf8ScalarValuesAreRejected()
    {
        Reject(RequestJson("https://pan.baidu.com/s/" + new string('a', 129)), ManagedBrowserProtocolError.InvalidPayload);
        foreach (var uid in new[] { new string('a', 257), new string('中', 86), "uid\u0080" })
        {
            var json = JsonNode.Parse(RequestJson())!;
            json["payload"]!["expectedBaiduUserId"] = uid;
            Reject(json.ToJsonString(), ManagedBrowserProtocolError.InvalidPayload);
        }
    }

    [Theory]
    [InlineData("SourceOpened")]
    [InlineData("ExtractionRequired")]
    [InlineData("LoginRequired")]
    [InlineData("VerificationRequired")]
    [InlineData("PageNotValidated")]
    [InlineData("BrowserUnavailable")]
    [InlineData("AccountIdentityMismatch")]
    [InlineData("TimedOut")]
    [InlineData("Cancelled")]
    public void ResultIsOnlyTypedHintAndPreservesEnvelope(string kind)
    {
        var request = ManagedBrowserProtocol.DecodeRequest(Encoding.UTF8.GetBytes(RequestJson()));
        var json = ResponseJson(kind);
        var response = ManagedBrowserProtocol.DecodeResponse(Encoding.UTF8.GetBytes(json));
        ManagedBrowserProtocol.ValidateResponseFor(request, response);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(ManagedBrowserProtocol.EncodeResponsePayload(response))));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("sourceopened")]
    [InlineData("Observed")]
    [InlineData("Transferred")]
    public void UnknownHintIsNotAcceptedAsSuccess(string kind)
    {
        var error = Assert.Throws<ManagedBrowserProtocolException>(() =>
            ManagedBrowserProtocol.DecodeResponse(Encoding.UTF8.GetBytes(ResponseJson(kind))));
        Assert.Equal(ManagedBrowserProtocolError.InvalidPayload, error.Error);
    }

    [Theory]
    [InlineData("pageText", ManagedBrowserProtocolError.ForbiddenField)]
    [InlineData("extractionCode", ManagedBrowserProtocolError.ForbiddenField)]
    [InlineData("script", ManagedBrowserProtocolError.ForbiddenField)]
    [InlineData("sourceUri", ManagedBrowserProtocolError.UnknownField)]
    public void ResultCannotCarryPageDataOrNewInstructions(string field, ManagedBrowserProtocolError expected)
    {
        var json = JsonNode.Parse(ResponseJson("SourceOpened"))!;
        json["result"]![field] = "private-value";
        var error = Assert.Throws<ManagedBrowserProtocolException>(() =>
            ManagedBrowserProtocol.DecodeResponse(Encoding.UTF8.GetBytes(json.ToJsonString())));
        Assert.Equal(expected, error.Error);
        Assert.DoesNotContain("private-value", error.ToString());
    }

    private static void Reject(string json, ManagedBrowserProtocolError expected)
    {
        var error = Assert.Throws<ManagedBrowserProtocolException>(() =>
            ManagedBrowserProtocol.DecodeRequest(Encoding.UTF8.GetBytes(json)));
        Assert.Equal(expected, error.Error);
        Assert.DoesNotContain("synthetic-private-uid", error.ToString());
    }

    internal static string RequestJson(string source = Source) => new JsonObject
    {
        ["protocolVersion"] = ManagedBrowserProtocol.ProtocolVersion,
        ["executionEngineVersion"] = ManagedBrowserProtocol.ExecutionEngineVersion,
        ["correlationId"] = "11111111-1111-1111-1111-111111111111",
        ["operationId"] = "22222222-2222-2222-2222-222222222222",
        ["accountId"] = "33333333-3333-3333-3333-333333333333",
        ["generation"] = 7, ["action"] = "OpenTransferSource",
        ["payload"] = new JsonObject
        {
            ["browserId"] = "44444444-4444-4444-4444-444444444444", ["targetId"] = "synthetic-target",
            ["devToolsEndpoint"] = "http://127.0.0.1:9222/", ["timeoutMilliseconds"] = 1000,
            ["sourceUri"] = source, ["expectedBaiduUserId"] = "synthetic-private-uid",
        },
    }.ToJsonString();

    private static string ResponseJson(string kind)
    {
        var json = JsonNode.Parse(RequestJson())!.AsObject();
        json.Remove("payload");
        json["result"] = new JsonObject { ["kind"] = kind };
        return json.ToJsonString();
    }
}
