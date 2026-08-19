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
        Assert.Equal("No supported Baidu share link was found.", rows[0].ValidationError);
        Assert.Equal("The Baidu link is malformed or unsupported.", rows[1].ValidationError);
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

    [Fact]
    public async Task MixedValidAndMalformedBaiduLinksProduceSeparateRowsWithoutReportingOtherHosts()
    {
        const string text = "有效 https://pan.baidu.com/s/ok，畸形 https://pan.baidu.com/%ZZ，旧协议 http://yun.baidu.com/s/legacy，参考 https://example.com/page";

        var rows = await ReadAllAsync(new TextImportParser().ParseAsync(new StringReader(text)));

        Assert.Equal(3, rows.Count);
        Assert.Equal("https://pan.baidu.com/s/ok", rows[0].ShareUrl);
        Assert.Null(rows[0].ValidationError);
        Assert.Equal("https://pan.baidu.com/%ZZ", rows[1].ShareUrl);
        Assert.Equal("The Baidu link is malformed or unsupported.", rows[1].ValidationError);
        Assert.Equal("http://yun.baidu.com/s/legacy", rows[2].ShareUrl);
        Assert.Equal("The Baidu link is malformed or unsupported.", rows[2].ValidationError);
        Assert.DoesNotContain(rows, row => row.ShareUrl.Contains("example.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UserInfoBaiduAuthorityIsReportedInvalidWithoutMistakingExternalHostForBaidu()
    {
        const string text = "有效 https://pan.baidu.com/s/ok，带用户信息 https://user@pan.baidu.com/s/bad，外站 https://pan.baidu.com@evil.example/s/not-baidu";

        var rows = await ReadAllAsync(new TextImportParser().ParseAsync(new StringReader(text)));

        Assert.Equal(2, rows.Count);
        Assert.Equal("https://pan.baidu.com/s/ok", rows[0].ShareUrl);
        Assert.Null(rows[0].ValidationError);
        Assert.Equal("https://user@pan.baidu.com/s/bad", rows[1].ShareUrl);
        Assert.Equal("The Baidu link is malformed or unsupported.", rows[1].ValidationError);
        Assert.DoesNotContain(rows, row => row.ShareUrl.Contains("evil.example", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LargeSingleLineUsesLinearMatchAndBoundaryOperationCounts()
    {
        const int linkCount = 10_000;
        var line = string.Join(' ', Enumerable.Range(1, linkCount)
            .Select(index => $"https://pan.baidu.com/s/id-{index}"));
        TextImportParserLineMetrics? observed = null;
        var parser = new TextImportParser(metrics => observed = metrics);

        var rows = await ReadAllAsync(parser.ParseAsync(new StringReader(line)));

        Assert.Equal(linkCount, rows.Count);
        Assert.NotNull(observed);
        Assert.Equal(linkCount, observed!.UrlMatchCount);
        Assert.Equal(linkCount, observed.CandidateEvaluationCount);
        Assert.Equal(linkCount, observed.NextBoundaryReadCount);
        Assert.Equal(3L * linkCount, observed.CountedOperations);
    }

    private static async Task<List<ImportSourceRow>> ReadAllAsync(IAsyncEnumerable<ImportSourceRow> source)
    {
        var rows = new List<ImportSourceRow>();
        await foreach (var row in source) rows.Add(row);
        return rows;
    }
}
