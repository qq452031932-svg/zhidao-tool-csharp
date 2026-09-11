using BaiduBatchTransfer.App.Features.ManagedBrowser;
using System.Text;
using System.Text.Json.Nodes;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class ManagedBrowserSourceListingProtocolTests
{
    [Fact]
    public void ReadRequestAndMixedListingRoundtripTheSharedFixture()
    {
        var fixture = Fixture();
        var requestBytes = Encoding.UTF8.GetBytes(fixture["request"]!.ToJsonString());
        var request = ManagedBrowserProtocol.DecodeRequest(requestBytes);
        Assert.Contains("ReadTransferSource", ManagedBrowserProtocol.RequiredCapabilities);
        Assert.True(JsonNode.DeepEquals(fixture["request"], JsonNode.Parse(ManagedBrowserProtocol.EncodeRequest(request)[4..])));
        var response = ManagedBrowserProtocol.DecodeResponse(Encoding.UTF8.GetBytes(ResponseJson()));
        ManagedBrowserProtocol.ValidateResponseFor(request, response);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(ResponseJson()), JsonNode.Parse(ManagedBrowserProtocol.EncodeResponse(response)[4..])));
        Assert.DoesNotContain("synthetic-private-uid", response.Result.ToString());
        Assert.DoesNotContain("测试文件夹", response.Result.ToString());
    }

    [Theory]
    [InlineData("ExtractionRequired")]
    [InlineData("LoginRequired")]
    [InlineData("VerificationRequired")]
    [InlineData("PageNotValidated")]
    [InlineData("BrowserUnavailable")]
    [InlineData("AccountIdentityMismatch")]
    [InlineData("TimedOut")]
    [InlineData("Cancelled")]
    public void DenialsNeverCarryPartialItems(string kind)
    {
        var json = JsonNode.Parse(ResponseJson())!;
        json["result"] = new JsonObject { ["kind"] = kind };
        _ = ManagedBrowserProtocol.DecodeResponse(Encoding.UTF8.GetBytes(json.ToJsonString()));
        json["result"]!["items"] = new JsonArray();
        Reject(json, ManagedBrowserProtocolError.UnknownField);
    }

    [Theory]
    [InlineData("totalCount", "1")]
    [InlineData("totalCount", "2.0")]
    [InlineData("reachedEnd", "false")]
    [InlineData("reachedEnd", "1")]
    [InlineData("isStable", "false")]
    [InlineData("generationToken", "\"\"")]
    [InlineData("baiduUserId", "\" \"")]
    [InlineData("observationFingerprint", "\"not-a-hash\"")]
    [InlineData("items", "[]")]
    [InlineData("items", "null")]
    public void IncompleteOrMalformedSnapshotCannotBecomeObserved(string field, string value)
    {
        var json = JsonNode.Parse(ResponseJson())!;
        json["result"]![field] = JsonNode.Parse(value);
        Reject(json, ManagedBrowserProtocolError.InvalidPayload);
    }

    [Theory]
    [InlineData("exactId", "\"\"")]
    [InlineData("exactId", "\"id/child\"")]
    [InlineData("exactId", "123")]
    [InlineData("kind", "\"Unknown\"")]
    [InlineData("kind", "\"file\"")]
    [InlineData("kind", "0")]
    [InlineData("serverName", "\"\"")]
    [InlineData("serverName", "\"line\\nfeed\"")]
    public void InvalidItemFieldsAreRejected(string field, string value)
    {
        var json = JsonNode.Parse(ResponseJson())!;
        json["result"]!["items"]![0]![field] = JsonNode.Parse(value);
        Reject(json, ManagedBrowserProtocolError.InvalidPayload);
    }

    [Fact]
    public void DuplicateIdsAndOversizedFieldsAreNotSilentlyTruncated()
    {
        var json = JsonNode.Parse(ResponseJson())!;
        json["result"]!["items"]![1]!["exactId"] = "101";
        Reject(json, ManagedBrowserProtocolError.InvalidPayload);
        foreach (var field in new[] { "exactId", "serverName" })
        {
            json = JsonNode.Parse(ResponseJson())!;
            json["result"]!["items"]![0]![field] = new string('a', field == "exactId" ? 129 : 257);
            Reject(json, ManagedBrowserProtocolError.InvalidPayload);
        }
        json = JsonNode.Parse(ResponseJson())!;
        var rows = new JsonArray();
        for (var i = 0; i < 33; i++) rows.Add(new JsonObject { ["exactId"] = $"id{i}", ["kind"] = "File", ["serverName"] = "file" });
        json["result"]!["items"] = rows;
        json["result"]!["totalCount"] = 33;
        Reject(json, ManagedBrowserProtocolError.InvalidPayload);
    }

    [Fact]
    public void UnicodeByteLimitAndNestedForbiddenFieldsAreEnforced()
    {
        var json = JsonNode.Parse(ResponseJson())!;
        json["result"]!["items"]![0]!["serverName"] = new string('中', 86);
        Reject(json, ManagedBrowserProtocolError.InvalidPayload);
        json = JsonNode.Parse(ResponseJson())!;
        json["result"]!["items"]![0]!["extra"] = new JsonObject { ["cookie"] = "private-value" };
        Reject(json, ManagedBrowserProtocolError.ForbiddenField);
        json = JsonNode.Parse(ResponseJson())!;
        json["result"]!["pageText"] = "private-value";
        Reject(json, ManagedBrowserProtocolError.ForbiddenField);
    }

    [Theory]
    [InlineData("generationToken")]
    [InlineData("baiduUserId")]
    [InlineData("serverName")]
    public void MalformedEscapedUnicodeIsRejectedWithoutRawDecoderErrors(string field)
    {
        var json = JsonNode.Parse(ResponseJson())!;
        var target = field == "serverName" ? json["result"]!["items"]![0]! : json["result"]!;
        target[field] = "UNICODE_PLACEHOLDER";
        var raw = json.ToJsonString().Replace("UNICODE_PLACEHOLDER", "\\ud800", StringComparison.Ordinal);
        var error = Assert.Throws<ManagedBrowserProtocolException>(() => ManagedBrowserProtocol.DecodeResponse(Encoding.UTF8.GetBytes(raw)));
        Assert.Equal(ManagedBrowserProtocolError.InvalidPayload, error.Error);
    }

    [Fact]
    public void PreviousFourCapabilityHelperCannotNegotiateReadListing()
    {
        var json = Fixture()["request"]!.DeepClone();
        json["action"] = "Handshake";
        json["payload"] = new JsonObject { ["nonce"] = new string('a', 64), ["parentProcessId"] = 123,
            ["requiredCapabilities"] = new JsonArray("Health", "Shutdown", "ObserveAccountIdentity", "OpenTransferSource") };
        var error = Assert.Throws<ManagedBrowserProtocolException>(() => ManagedBrowserProtocol.DecodeRequest(Encoding.UTF8.GetBytes(json.ToJsonString())));
        Assert.Equal(ManagedBrowserProtocolError.CapabilityMismatch, error.Error);
    }

    internal static JsonNode Fixture()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "tools", "managed_browser_helper"))) root = root.Parent;
        Assert.NotNull(root);
        return JsonNode.Parse(File.ReadAllText(Path.Combine(root.FullName, "tools", "managed_browser_helper", "tests", "fixtures", "read_transfer_source.json")))!;
    }

    internal static string ResponseJson()
    {
        var fixture = Fixture();
        var response = fixture["request"]!.DeepClone().AsObject();
        response.Remove("payload");
        response["result"] = fixture["result"]!.DeepClone();
        return response.ToJsonString();
    }

    private static void Reject(JsonNode json, ManagedBrowserProtocolError expected)
    {
        var error = Assert.Throws<ManagedBrowserProtocolException>(() => ManagedBrowserProtocol.DecodeResponse(Encoding.UTF8.GetBytes(json.ToJsonString())));
        Assert.Equal(expected, error.Error);
        Assert.DoesNotContain("private-value", error.ToString());
    }
}
