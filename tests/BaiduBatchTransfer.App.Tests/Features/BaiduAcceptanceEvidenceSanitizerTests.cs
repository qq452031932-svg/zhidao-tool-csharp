using System.Text.Json;
using BaiduBatchTransfer.App.Features.Acceptance;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class BaiduAcceptanceEvidenceSanitizerTests
{
    [Fact]
    public void KeepsOnlyOfficialRequestShapeAndJsonPropertyNames()
    {
        const string secret = "secret-user-file-id-998877";
        var result = BaiduAcceptanceEvidenceSanitizer.TryCreate(
            new Uri($"https://pan.baidu.com/api/create?a={secret}"),
            "post",
            200,
            "{\"errno\":0,\"data\":{\"fs_id\":\"" + secret +
            "\",\"share_url\":\"https://secret.example\"}}");

        Assert.NotNull(result);
        Assert.Equal("pan.baidu.com", result.Host);
        Assert.Equal("/segments/2", result.Path);
        Assert.Equal("POST", result.Method);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal(["data", "errno", "fs_id", "share_url"], result.JsonPropertyNames);

        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain(secret, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("secret.example", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("?", serialized, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://pan.baidu.com/api/list")]
    [InlineData("https://pan.baidu.com.evil.example/api/list")]
    [InlineData("https://user@pan.baidu.com/api/list")]
    [InlineData("https://pan.baidu.com:444/api/list")]
    public void RejectsNonOfficialOrUnsafeDestinations(string value)
    {
        Assert.Null(BaiduAcceptanceEvidenceSanitizer.TryCreate(
            new Uri(value), "GET", 200, "{\"errno\":0}"));
    }

    [Fact]
    public void RedactsDynamicPathSegmentsAndUnsafePropertyNames()
    {
        var result = BaiduAcceptanceEvidenceSanitizer.TryCreate(
            new Uri("https://d.pcs.baidu.com/rest/2.0/pcs/file/998877665544332211/token-AbCdEf0123456789"),
            "GET",
            206,
            "{\"safe_name\":1,\"private value\":2,\"user@example.com\":3}");

        Assert.NotNull(result);
        Assert.Equal("/segments/6", result.Path);
        Assert.Equal(["safe_name"], result.JsonPropertyNames);
    }

    [Fact]
    public void BoundsResponseInspectionAndNeverEchoesInvalidInputs()
    {
        var oversized = "{\"secret\":\"" + new string('x', 300_000) + "\"}";
        var result = BaiduAcceptanceEvidenceSanitizer.TryCreate(
            new Uri("https://pan.baidu.com/api/list"),
            "TRACE secret-header",
            999,
            oversized);

        Assert.NotNull(result);
        Assert.Equal("OTHER", result.Method);
        Assert.Null(result.StatusCode);
        Assert.Empty(result.JsonPropertyNames);
        Assert.DoesNotContain("secret", result.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RedactsEveryPathQueryAndFragmentValue()
    {
        var result = BaiduAcceptanceEvidenceSanitizer.TryCreate(
            new Uri("https://pan.baidu.com/alice/secret-file/abc123/report-final.xlsx?account=account-123#private-fragment"),
            "GET",
            200,
            "{\"ok\":true}");

        Assert.NotNull(result);
        var serialized = JsonSerializer.Serialize(result);
        Assert.Equal("/segments/4", result.Path);
        foreach (var secret in new[] { "alice", "secret-file", "abc123", "report-final.xlsx", "account-123", "private-fragment" })
            Assert.DoesNotContain(secret, serialized, StringComparison.OrdinalIgnoreCase);
    }
}
