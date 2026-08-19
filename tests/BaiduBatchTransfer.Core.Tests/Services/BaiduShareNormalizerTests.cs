using BaiduBatchTransfer.Core.Services;

namespace BaiduBatchTransfer.Core.Tests.Services;

public sealed class BaiduShareNormalizerTests
{
    private readonly BaiduShareNormalizer _normalizer = new();

    [Theory]
    [InlineData("https://pan.baidu.com/s/abc?pwd=1a2b。", "abc", "1a2b")]
    [InlineData("https://pan.baidu.com/s/abc).", "abc", null)]
    [InlineData("HTTPS://PAN.BAIDU.COM/s/abc?from=share", "abc", null)]
    [InlineData(" https://yun.baidu.com/s/abc?utm_source=sheet， ", "abc", null)]
    [InlineData("https://pan.baidu.com/share/init?surl=abc&pwd=9z8y", "abc", "9z8y")]
    public void EquivalentCanonicalLinks_HaveTheSameStableIdentity(
        string input,
        string expectedShareId,
        string? expectedExtractionCode)
    {
        var result = _normalizer.Normalize(input);

        Assert.Equal(expectedShareId, result.ShareId);
        Assert.Equal("https://pan.baidu.com/s/abc", result.CanonicalUrl);
        Assert.Equal(expectedExtractionCode, result.ExtractionCode);
    }

    [Fact]
    public void TrackingParametersAndFragments_AreRemovedFromCanonicalUrl()
    {
        var result = _normalizer.Normalize(
            "https://pan.baidu.com/s/share-id?pwd=abcd&from=copy#section");

        Assert.Equal("https://pan.baidu.com/s/share-id", result.CanonicalUrl);
        Assert.Equal("abcd", result.ExtractionCode);
    }

    [Fact]
    public void UnicodeAndEscapedForms_NormalizeDeterministically()
    {
        var literal = _normalizer.Normalize("https://pan.baidu.com/s/\u8D44\u6E90");
        var escaped = _normalizer.Normalize(
            "https://pan.baidu.com/s/%E8%B5%84%E6%BA%90");

        Assert.Equal(literal.ShareId, escaped.ShareId);
        Assert.Equal(literal.CanonicalUrl, escaped.CanonicalUrl);
    }

    [Fact]
    public void PathAndQueryForms_DecodeEscapedDataExactlyOnce()
    {
        var pathForm = _normalizer.Normalize("https://pan.baidu.com/s/abc%252E");
        var queryForm = _normalizer.Normalize(
            "https://pan.baidu.com/share/init?surl=abc%252E");

        Assert.Equal("abc%2E", pathForm.ShareId);
        Assert.Equal(pathForm.ShareId, queryForm.ShareId);
        Assert.Equal(pathForm.CanonicalUrl, queryForm.CanonicalUrl);
    }

    [Fact]
    public void EscapedTrailingPunctuation_IsTreatedAsUrlData()
    {
        var result = _normalizer.Normalize("https://pan.baidu.com/s/abc%2E");

        Assert.Equal("abc.", result.ShareId);
        Assert.Equal("https://pan.baidu.com/s/abc.", result.CanonicalUrl);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("http://pan.baidu.com/s/abc")]
    [InlineData("https://example.com/s/abc")]
    [InlineData("https://pan.baidu.com.example.com/s/abc")]
    [InlineData("https://user@pan.baidu.com/s/abc")]
    [InlineData("https://pan.baidu.com:444/s/abc")]
    [InlineData("https://pan.baidu.com/s/")]
    [InlineData("https://pan.baidu.com/s/abc%ZZ")]
    [InlineData("https://pan.baidu.com/s/abc%C0%AF")]
    [InlineData("https://pan.baidu.com/s/abc?pwd=%ZZ")]
    [InlineData("https://pan.baidu.com/s/abc?pwd=%C0%AF")]
    public void MalformedOrUntrustedLinks_AreRejected(string input)
    {
        Assert.Throws<ArgumentException>(() => _normalizer.Normalize(input));
    }

    [Theory]
    [InlineData("https://pan.baidu.com/s//abc")]
    [InlineData("https://pan.baidu.com/s/abc/")]
    [InlineData("https://pan.baidu.com//s/abc")]
    [InlineData("https://pan.baidu.com/share//init?surl=abc")]
    [InlineData("https://pan.baidu.com/other/../s/abc")]
    public void NonCanonicalPathShapes_AreRejected(string input)
    {
        Assert.Throws<ArgumentException>(() => _normalizer.Normalize(input));
    }

    [Theory]
    [InlineData("https://pan.baidu.com/surl?target=abc")]
    [InlineData("https://dwz.cn/abc")]
    public void UnresolvedShortLinks_AreRejectedRatherThanAssignedAGuessedId(string input)
    {
        var exception = Assert.Throws<ArgumentException>(() => _normalizer.Normalize(input));

        Assert.DoesNotContain("abc", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConflictingExtractionCodes_AreRejectedWithoutLeakingThem()
    {
        const string input = "https://pan.baidu.com/s/abc?pwd=first&pwd=second";

        var exception = Assert.Throws<ArgumentException>(() => _normalizer.Normalize(input));

        Assert.DoesNotContain("first", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("second", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResultToString_DoesNotExposeUrlShareIdOrExtractionCode()
    {
        var result = _normalizer.Normalize("https://pan.baidu.com/s/secret-id?pwd=secret-code");

        var display = result.ToString();

        Assert.DoesNotContain("secret-id", display, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-code", display, StringComparison.Ordinal);
        Assert.DoesNotContain("pan.baidu.com", display, StringComparison.Ordinal);
    }
}
