using BaiduBatchTransfer.App.Features.Acceptance;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class BaiduAcceptanceProbeWebPolicyTests
{
    [Theory]
    [InlineData("https://pan.baidu.com/", "GET", true)]
    [InlineData("https://pan.baidu.com/", "HEAD", true)]
    [InlineData("https://pan.baidu.com/api/create", "POST", false)]
    [InlineData("https://pan.baidu.com/api/share", "PUT", false)]
    [InlineData("https://example.com/", "GET", false)]
    [InlineData("http://pan.baidu.com/", "GET", false)]
    public void AllowsOnlyReadOnlyOfficialRequests(string destination, string method, bool expected)
    {
        Assert.Equal(expected, BaiduAcceptanceProbeWebPolicy.IsAllowedRequest(new Uri(destination), method));
    }

    [Fact]
    public void NeverAllowsDownloads()
    {
        Assert.False(BaiduAcceptanceProbeWebPolicy.IsDownloadAllowed());
    }
}
