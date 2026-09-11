using System.Text;
using System.Text.Json.Nodes;
using BaiduBatchTransfer.App.Features.ManagedBrowser;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class ManagedBrowserDirectoryListingProtocolTests
{
    [Fact]
    public void DirectoryRequestAndFolderListingRoundtripSharedFixture()
    {
        var fixture = Fixture();
        var request = ManagedBrowserProtocol.DecodeRequest(Bytes(fixture["request"]!));
        Assert.Contains("ReadTransferDirectory", ManagedBrowserProtocol.RequiredCapabilities);
        Assert.True(JsonNode.DeepEquals(fixture["request"], JsonNode.Parse(ManagedBrowserProtocol.EncodeRequest(request)[4..])));
        var response = ManagedBrowserProtocol.DecodeResponse(Bytes(Response()));
        ManagedBrowserProtocol.ValidateResponseFor(request, response);
        Assert.True(JsonNode.DeepEquals(Response(), JsonNode.Parse(ManagedBrowserProtocol.EncodeResponse(response)[4..])));
        Assert.DoesNotContain("synthetic-private-uid", response.Result.ToString());
    }

    [Fact]
    public void ExplicitEmptyRootFolderListIsValid()
    {
        var json = Response();
        json["result"]!["items"] = new JsonArray();
        json["result"]!["totalCount"] = 0;
        json["result"]!["canonicalPath"] = "/";
        json["result"]!["ancestorExactIds"] = new JsonArray("parent-1");
        _ = ManagedBrowserProtocol.DecodeResponse(Bytes(json));
    }

    [Theory]
    [InlineData("parentExactId", "\"bad/id\"")]
    [InlineData("parentExactId", "\"wrong-parent\"")]
    [InlineData("canonicalPath", "\"target\"")]
    [InlineData("canonicalPath", "\"/target/\"")]
    [InlineData("canonicalPath", "\"//target\"")]
    [InlineData("canonicalPath", "\"/a/../target\"")]
    [InlineData("canonicalPath", "\" /target\"")]
    [InlineData("canonicalPath", "\"/target \"")]
    [InlineData("canonicalPath", "\"/a\\\\b\"")]
    [InlineData("ancestorExactIds", "[]")]
    [InlineData("ancestorExactIds", "[\"parent-1\",\"parent-1\"]")]
    [InlineData("ancestorExactIds", "[\"root-1\"]")]
    [InlineData("ancestorExactIds", "[\"bad/id\",\"parent-1\"]")]
    [InlineData("totalCount", "0")]
    [InlineData("totalCount", "2.0")]
    [InlineData("reachedEnd", "false")]
    [InlineData("isStable", "false")]
    [InlineData("items", "null")]
    [InlineData("generationToken", "\"\"")]
    [InlineData("observationFingerprint", "\"not-a-hash\"")]
    public void MalformedOrIncompleteDirectoryIsRejected(string field, string value)
    {
        var json = Response();
        json["result"]![field] = JsonNode.Parse(value);
        Assert.Throws<ManagedBrowserProtocolException>(() => ManagedBrowserProtocol.DecodeResponse(Bytes(json)));
    }

    [Theory]
    [InlineData("kind", "File")]
    [InlineData("exactId", "root-1")]
    [InlineData("exactId", "parent-1")]
    [InlineData("exactId", "202")]
    [InlineData("serverName", "")]
    public void FilesAncestorAliasesDuplicatesAndEmptyNamesAreRejected(string field, string value)
    {
        var json = Response();
        json["result"]!["items"]![0]![field] = value;
        Assert.Throws<ManagedBrowserProtocolException>(() => ManagedBrowserProtocol.DecodeResponse(Bytes(json)));
    }

    [Theory]
    [InlineData("expectedParentExactId")]
    [InlineData("expectedCanonicalPath")]
    public void ExpectedDirectoryFieldsMustBothBePresentAndPaired(string field)
    {
        var json = Fixture()["request"]!;
        json["payload"]![field] = field == "expectedParentExactId" ? "parent-1" : "/目标目录";
        Assert.Throws<ManagedBrowserProtocolException>(() => ManagedBrowserProtocol.DecodeRequest(Bytes(json)));
        json["payload"]!.AsObject().Remove(field);
        Assert.Throws<ManagedBrowserProtocolException>(() => ManagedBrowserProtocol.DecodeRequest(Bytes(json)));
    }

    [Theory]
    [InlineData("LoginRequired")]
    [InlineData("VerificationRequired")]
    [InlineData("PageNotValidated")]
    [InlineData("BrowserUnavailable")]
    [InlineData("AccountIdentityMismatch")]
    [InlineData("TimedOut")]
    [InlineData("Cancelled")]
    public void DenialResultsContainNoPartialDirectory(string kind)
    {
        var json = Response();
        json["result"] = new JsonObject { ["kind"] = kind };
        _ = ManagedBrowserProtocol.DecodeResponse(Bytes(json));
        json["result"]!["items"] = new JsonArray();
        Assert.Throws<ManagedBrowserProtocolException>(() => ManagedBrowserProtocol.DecodeResponse(Bytes(json)));
    }

    [Fact]
    public void OversizedOrInvalidUnicodePathCannotBeNormalized()
    {
        foreach (var path in new[] { "/" + new string('中', 86), "/" + string.Join('/', Enumerable.Repeat("a", 33)), "/a\n" })
        {
            var json = Response();
            json["result"]!["canonicalPath"] = path;
            Assert.Throws<ManagedBrowserProtocolException>(() => ManagedBrowserProtocol.DecodeResponse(Bytes(json)));
        }
        // Use an ASCII sentinel because JsonNode's serializer may escape the original Chinese text.
        var invalid = Response(); invalid["result"]!["canonicalPath"] = "PATH_SENTINEL";
        var raw = invalid.ToJsonString().Replace("PATH_SENTINEL", "/\\ud800", StringComparison.Ordinal);
        Assert.Throws<ManagedBrowserProtocolException>(() => ManagedBrowserProtocol.DecodeResponse(Encoding.UTF8.GetBytes(raw)));
    }

    internal static byte[] Bytes(JsonNode json) => Encoding.UTF8.GetBytes(json.ToJsonString());
    internal static JsonNode Fixture()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "tools", "managed_browser_helper"))) root = root.Parent;
        Assert.NotNull(root);
        return JsonNode.Parse(File.ReadAllText(Path.Combine(root.FullName, "tools", "managed_browser_helper", "tests", "fixtures", "read_transfer_directory.json")))!;
    }
    internal static JsonNode Response()
    {
        var fixture = Fixture(); var response = fixture["request"]!.DeepClone().AsObject();
        response.Remove("payload"); response["result"] = fixture["result"]!.DeepClone(); return response;
    }
}
