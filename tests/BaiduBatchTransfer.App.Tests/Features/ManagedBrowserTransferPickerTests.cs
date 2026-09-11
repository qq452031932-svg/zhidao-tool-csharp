using System.Text;
using System.Text.Json.Nodes;
using BaiduBatchTransfer.App.Features.ManagedBrowser;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class ManagedBrowserTransferPickerTests
{
    [Fact]
    public void ExactSourcePickerRequestRoundTripsWithoutFreeFormFields()
    {
        var json = Request();
        var request = ManagedBrowserProtocol.DecodeRequest(Bytes(json));
        Assert.Contains("OpenTransferPicker", ManagedBrowserProtocol.RequiredCapabilities);
        Assert.True(JsonNode.DeepEquals(json, JsonNode.Parse(ManagedBrowserProtocol.EncodeRequest(request)[4..])));
        Assert.DoesNotContain("fixture_1", request.Payload.ToString());
        Assert.DoesNotContain("synthetic", request.Payload.ToString());
    }

    [Theory]
    [InlineData("PickerOpened")] [InlineData("OutcomeUnknown")] [InlineData("PageNotValidated")]
    [InlineData("AccountIdentityMismatch")] [InlineData("LoginRequired")] [InlineData("VerificationRequired")]
    [InlineData("BrowserUnavailable")] [InlineData("TimedOut")] [InlineData("Cancelled")]
    public void FixedResultsRoundTripAndRejectExtraEvidence(string kind)
    {
        var json = Request().AsObject();
        json.Remove("payload");
        json["result"] = new JsonObject { ["kind"] = kind };
        var response = ManagedBrowserProtocol.DecodeResponse(Bytes(json));
        Assert.True(JsonNode.DeepEquals(json, JsonNode.Parse(ManagedBrowserProtocol.EncodeResponse(response)[4..])));
        json["result"]!["items"] = new JsonArray();
        Assert.Throws<ManagedBrowserProtocolException>(() => ManagedBrowserProtocol.DecodeResponse(Bytes(json)));
    }

    [Theory]
    [InlineData("missing")] [InlineData("fingerprint")] [InlineData("uid")]
    [InlineData("name")] [InlineData("generation")] [InlineData("partial")] [InlineData("script")]
    public void UnboundOrTamperedSourceCannotBeDispatched(string change)
    {
        var json = Request();
        var source = json["payload"]!["expectedSource"]!;
        switch (change)
        {
            case "missing": json["payload"]!.AsObject().Remove("expectedSource"); break;
            case "fingerprint": source["observationFingerprint"] = new string('0', 64); break;
            case "uid": source["baiduUserId"] = "other-uid"; break;
            case "name": source["items"]![0]!["serverName"] = "changed"; break;
            case "generation": source["generationToken"] = "changed"; break;
            case "partial": source["reachedEnd"] = false; break;
            case "script": json["payload"]!["script"] = "anything"; break;
        }
        Assert.Throws<ManagedBrowserProtocolException>(() => ManagedBrowserProtocol.DecodeRequest(Bytes(json)));
    }

    internal static JsonNode Request()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "tools", "managed_browser_helper"))) root = root.Parent;
        Assert.NotNull(root);
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(root.FullName, "tools", "managed_browser_helper", "tests", "fixtures", "read_transfer_source.json")))!;
        var request = fixture["request"]!.DeepClone();
        request["action"] = "OpenTransferPicker";
        request["payload"]!["expectedSource"] = fixture["result"]!.DeepClone();
        return request;
    }
    internal static byte[] Bytes(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString());
}
