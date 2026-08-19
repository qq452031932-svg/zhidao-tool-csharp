using BaiduBatchTransfer.Infrastructure.Excel;

namespace BaiduBatchTransfer.Infrastructure.Tests.Excel;

public sealed class ExcelTextSanitizerTests
{
    [Theory]
    [InlineData("=1+1")]
    [InlineData(" +SUM(A1:A2)")]
    [InlineData("\t-2+3")]
    [InlineData("\r\n@cmd")]
    [InlineData("\u200B=HYPERLINK(\"https://example.test\")")]
    [InlineData("\u2060+1")]
    public void FormulaLikeUserTextIsPrefixed(string value)
    {
        var sanitized = ExcelTextSanitizer.Sanitize(value);

        Assert.StartsWith("'", sanitized, StringComparison.Ordinal);
        Assert.EndsWith(value, sanitized, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://pan.baidu.com/s/id")]
    [InlineData("ordinary note")]
    [InlineData("123")]
    [InlineData("")]
    public void SafeTextIsUnchanged(string value) =>
        Assert.Equal(value, ExcelTextSanitizer.Sanitize(value));

    [Fact]
    public void InvalidXmlControlsAreRemovedBeforeWriting() =>
        Assert.Equal("ab", ExcelTextSanitizer.Sanitize("a\u0001b"));
}
