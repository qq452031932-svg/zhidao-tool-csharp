using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Core.Tests;

public sealed class BaiduShareLinkTests
{
    [Theory]
    [InlineData("https://pan.baidu.com/s/share", "https://pan.baidu.com/s/share?pwd=a1B2")]
    [InlineData("https://pan.baidu.com/s/share?from=test", "https://pan.baidu.com/s/share?from=test&pwd=a1B2")]
    [InlineData("https://pan.baidu.com/s/share#section", "https://pan.baidu.com/s/share?pwd=a1B2#section")]
    [InlineData("https://pan.baidu.com/s/share?from=test#section", "https://pan.baidu.com/s/share?from=test&pwd=a1B2#section")]
    [InlineData("https://pan.baidu.com/s/share?pwd=a1B2", "https://pan.baidu.com/s/share?pwd=a1B2")]
    [InlineData("https://pan.baidu.com/s/share?pwd=a1B2&from=test#section", "https://pan.baidu.com/s/share?pwd=a1B2&from=test#section")]
    [InlineData("https://pan.baidu.com/s/share?%70wd=a1B2", "https://pan.baidu.com/s/share?%70wd=a1B2")]
    [InlineData("https://pan.baidu.com/s/share??pwd=a1B2", "https://pan.baidu.com/s/share??pwd=a1B2&pwd=a1B2")]
    [InlineData("https://pan.baidu.com/s/share??pwd=x9Y8", "https://pan.baidu.com/s/share??pwd=x9Y8&pwd=a1B2")]
    [InlineData("https://pan.baidu.com/s/share?from=test?", "https://pan.baidu.com/s/share?from=test?&pwd=a1B2")]
    [InlineData("https://pan.baidu.com/s/share??", "https://pan.baidu.com/s/share??&pwd=a1B2")]
    [InlineData("https://pan.baidu.com/s/share?from=test?#section", "https://pan.baidu.com/s/share?from=test?&pwd=a1B2#section")]
    [InlineData("https://pan.baidu.com/s/share??#section", "https://pan.baidu.com/s/share??&pwd=a1B2#section")]
    [InlineData("https://pan.baidu.com/s/share?#section", "https://pan.baidu.com/s/share?pwd=a1B2#section")]
    [InlineData("https://pan.baidu.com/s/share?from=test&#section", "https://pan.baidu.com/s/share?from=test&pwd=a1B2#section")]
    public void NormalizeProducesDistributableLink(string url, string expected)
    {
        Assert.Equal(expected, BaiduShareLink.Normalize(url, "a1B2"));
    }

    [Theory]
    [InlineData("https://pan.baidu.com/s/share?pwd=x9Y8")]
    [InlineData("https://pan.baidu.com/s/share?pwd=a1B2&pwd=x9Y8")]
    [InlineData("https://pan.baidu.com/s/share?pwd=")]
    [InlineData("https://pan.baidu.com/s/share?pwd")]
    [InlineData("https://pan.baidu.com/s/share?%70wd=x9Y8")]
    public void ConflictingCodeIsRejectedWithoutDisclosingEitherCode(string url)
    {
        var error = Assert.Throws<ArgumentException>(() => BaiduShareLink.Normalize(url, "a1B2"));
        Assert.DoesNotContain("a1B2", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("x9Y8", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(url, error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/s/share")]
    [InlineData("http://pan.baidu.com/s/share")]
    [InlineData("ftp://pan.baidu.com/s/share")]
    [InlineData("https://")]
    [InlineData("https://bad host/s/share")]
    public void InvalidHttpsUrlIsRejectedWithoutDisclosingCode(string? url)
    {
        var error = Assert.Throws<ArgumentException>(() => BaiduShareLink.Normalize(url!, "a1B2"));
        Assert.DoesNotContain("a1B2", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("abcde")]
    [InlineData("a-b1")]
    [InlineData("中文ab")]
    [InlineData(" ab1")]
    public void InvalidExtractionCodeIsRejectedWithoutDisclosingInput(string? code)
    {
        var error = Assert.Throws<ArgumentException>(() => BaiduShareLink.Normalize("https://pan.baidu.com/s/share", code!));
        if (!string.IsNullOrEmpty(code)) Assert.DoesNotContain(code, error.ToString(), StringComparison.Ordinal);
    }
}
