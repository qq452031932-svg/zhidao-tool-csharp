using BaiduBatchTransfer.Application.Imports;
using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.Application.Tests.Imports;

public sealed class TextImportParserTests
{
    [Fact]
    public async Task MixedChineseProseProducesOneRowPerLinkAndAssociatesOnlyAdjacentCode()
    {
        const string text = "课程资料 https://pan.baidu.com/s/course-1 提取码：a1B2；公开目录 https://yun.baidu.com/s/public-2 欢迎收藏";

        var rows = await ReadAllAsync(new TextImportParser().ParseAsync(new StringReader(text)));

        Assert.Collection(
            rows,
            first =>
            {
                Assert.Equal("https://pan.baidu.com/s/course-1", first.ShareUrl);
                Assert.Equal("a1B2", first.ExtractionCode);
                Assert.Contains("课程资料", first.Note);
                Assert.DoesNotContain("a1B2", first.Note);
                Assert.Null(first.ValidationError);
            },
            second =>
            {
                Assert.Equal("https://yun.baidu.com/s/public-2", second.ShareUrl);
                Assert.Null(second.ExtractionCode);
                Assert.Contains("公开目录", second.Note);
                Assert.Null(second.ValidationError);
            });
    }

    [Fact]
    public async Task NonBaiduAndMalformedLinesAreReturnedAsSafeInvalidRows()
    {
        const string text = "说明 https://example.com/file secret=do-not-echo\n坏链接 https://pan.baidu.com/%ZZ";

        var rows = await ReadAllAsync(new TextImportParser().ParseAsync(new StringReader(text)));

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal("No supported Baidu share link was found.", row.ValidationError));
        Assert.All(rows, row => Assert.DoesNotContain("do-not-echo", row.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task ParserStreamsLinesAndHonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await ReadAllAsync(
                new TextImportParser().ParseAsync(
                    new StringReader("https://pan.baidu.com/s/id"),
                    cancellation.Token)));
    }

    private static async Task<List<ImportSourceRow>> ReadAllAsync(IAsyncEnumerable<ImportSourceRow> source)
    {
        var rows = new List<ImportSourceRow>();
        await foreach (var row in source) rows.Add(row);
        return rows;
    }
}
