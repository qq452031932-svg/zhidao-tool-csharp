using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Services;

namespace BaiduBatchTransfer.Core.Tests;

public sealed class SpreadsheetTextSanitizerTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("ordinary note", "ordinary note")]
    [InlineData("a\u0001b", "ab")]
    [InlineData("=1+1", "'=1+1")]
    [InlineData(" \u200B+SUM(A1:A2)", "' \u200B+SUM(A1:A2)")]
    public void SanitizesSpreadsheetTextExactlyOnce(string? value, string expected) =>
        Assert.Equal(expected, SpreadsheetTextSanitizer.Sanitize(value));

    [Fact]
    public void ProjectionColumnStringRepresentationRedactsUserHeader()
    {
        var text = new BatchResultOriginalColumn(3, "用户私密表头").ToString();

        Assert.DoesNotContain("用户私密表头", text, StringComparison.Ordinal);
        Assert.Contains("Header = [REDACTED]", text, StringComparison.Ordinal);
    }
}
