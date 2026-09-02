using System.Collections.Immutable;
using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Application.ShareAnalytics;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Tests.ShareAnalytics;

public sealed class ShareAnalyticsWorkbookExportServiceTests
{
    private static readonly DateOnly[] FrozenDates =
    [
        new(2026, 9, 2),
        new(2026, 9, 1),
        new(2026, 8, 31),
        new(2026, 8, 30),
        new(2026, 8, 29),
    ];

    [Fact]
    public async Task ExportsFrozenLayoutAndSelectedRowOrderWithExactInvariantCells()
    {
        var publisher = new CapturingPublisher();
        var dates = FrozenDates.ToArray();
        long?[] firstDailyValues = [10, 15, null, 0, 9];
        var selectedRows = new List<ShareAnalyticsWorkbookExportRow>
        {
            new ShareAnalyticsWorkbookExportRow(
                "=first title",
                "https://pan.baidu.com/s/first",
                AccountShareRemoteStatus.Valid,
                1234567,
                firstDailyValues),
            new ShareAnalyticsWorkbookExportRow(
                "second title",
                "https://pan.baidu.com/s/second",
                AccountShareRemoteStatus.Invalid,
                null,
                [null, 3, 2, 1, null]),
        };
        var request = new ShareAnalyticsWorkbookExportRequest(
            "exports", AccountShareMetric.Transfer, dates, selectedRows);
        dates[2] = new DateOnly(2000, 1, 1);
        firstDailyValues[0] = 999;
        selectedRows.Reverse();

        var result = await new ShareAnalyticsWorkbookExportService(
                publisher,
                new FixedLocalTimeProvider(
                    new DateTimeOffset(2026, 9, 2, 1, 2, 3, TimeSpan.Zero),
                    TimeZoneInfo.Utc))
            .ExportAsync(request);

        Assert.Equal(
            new[] { "序号", "指标", "历史累计", "新增", "今天", "昨天", "08-31", "08-30", "08-29", "链接", "标题", "状态" },
            publisher.Layout!.Headers);
        Assert.Equal(
            new[] { "1", "转存", "1234567", "-5", "10", "15", "", "0", "9", "https://pan.baidu.com/s/first", "=first title", "有效" },
            publisher.Rows[0].TextCells);
        Assert.Equal(
            new[] { "2", "转存", "", "", "", "3", "2", "1", "", "https://pan.baidu.com/s/second", "second title", "失效" },
            publisher.Rows[1].TextCells);
        Assert.Equal(Path.Combine("exports", publisher.FileNames.Single()), result.FinalPath);
        Assert.Equal(2, result.ExportedRows);
        Assert.Equal(1, result.SheetCount);
    }

    [Theory]
    [InlineData(AccountShareMetric.Transfer, "转存")]
    [InlineData(AccountShareMetric.View, "浏览")]
    [InlineData(AccountShareMetric.Download, "下载")]
    public async Task UsesExplicitChineseMetricLabel(AccountShareMetric metric, string expected)
    {
        var publisher = new CapturingPublisher();
        var row = new ShareAnalyticsWorkbookExportRow(
            "title", "https://pan.baidu.com/s/id", AccountShareRemoteStatus.Valid, 1, [1, 0, 0, 0, 0]);

        await new ShareAnalyticsWorkbookExportService(publisher).ExportAsync(Request(metric, row));

        Assert.Equal(expected, publisher.Rows.Single().TextCells[1]);
        Assert.Equal("1", publisher.Rows.Single().TextCells[3]);
    }

    [Fact]
    public async Task FileNameUsesInjectedLocalTimeRatherThanUtcInstant()
    {
        var publisher = new CapturingPublisher();
        var utcInstant = new DateTimeOffset(2026, 9, 1, 16, 5, 6, TimeSpan.Zero);
        var localZone = TimeZoneInfo.CreateCustomTimeZone(
            "Test China Standard Time", TimeSpan.FromHours(8), "Test CST", "Test CST");

        await new ShareAnalyticsWorkbookExportService(
                publisher,
                new FixedLocalTimeProvider(utcInstant, localZone))
            .ExportAsync(Request(AccountShareMetric.Transfer));

        Assert.Equal("分享数据-20260902-000506.xlsx", publisher.FileNames.Single());
    }

    [Fact]
    public async Task DestinationCollisionRetriesWithSuffixAndFreshRepeatableRowStream()
    {
        var publisher = new CollisionThenSuccessPublisher();
        var row = new ShareAnalyticsWorkbookExportRow(
            "title", "https://pan.baidu.com/s/id", AccountShareRemoteStatus.Valid, 9, [5, 2, 1, 1, 0]);
        var time = new FixedLocalTimeProvider(
            new DateTimeOffset(2026, 9, 2, 1, 2, 3, TimeSpan.Zero), TimeZoneInfo.Utc);

        var result = await new ShareAnalyticsWorkbookExportService(publisher, time)
            .ExportAsync(Request(AccountShareMetric.View, row));

        Assert.Equal(
            new[] { "分享数据-20260902-010203.xlsx", "分享数据-20260902-010203(2).xlsx" },
            publisher.FileNames);
        Assert.Equal(2, publisher.AttemptRows.Count);
        Assert.Equal(
            publisher.AttemptRows[0].Single().TextCells.ToArray(),
            publisher.AttemptRows[1].Single().TextCells.ToArray());
        Assert.EndsWith("分享数据-20260902-010203(2).xlsx", result.FinalPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CollisionRetriesStopAtFiniteNineHundredNinetyNineNameLimit()
    {
        var publisher = new AlwaysCollisionPublisher();
        var service = new ShareAnalyticsWorkbookExportService(
            publisher,
            new FixedLocalTimeProvider(
                new DateTimeOffset(2026, 9, 2, 1, 2, 3, TimeSpan.Zero), TimeZoneInfo.Utc));

        var error = await Assert.ThrowsAsync<ReplacementWorkbookFilePublishException>(() =>
            service.ExportAsync(Request(AccountShareMetric.Download)));

        Assert.Equal(ReplacementWorkbookFilePublishErrorCode.DestinationExists, error.Code);
        Assert.Equal(999, publisher.FileNames.Count);
        Assert.Equal("分享数据-20260902-010203.xlsx", publisher.FileNames[0]);
        Assert.Equal("分享数据-20260902-010203(999).xlsx", publisher.FileNames[^1]);
    }

    [Fact]
    public async Task NonCollisionPublishFailurePropagatesUnchangedWithoutRetry()
    {
        var expected = new ReplacementWorkbookFilePublishException(
            ReplacementWorkbookFilePublishErrorCode.InvalidWorkbook,
            "invalid workbook");
        var publisher = new ThrowingPublisher(expected);

        var actual = await Assert.ThrowsAsync<ReplacementWorkbookFilePublishException>(() =>
            new ShareAnalyticsWorkbookExportService(publisher)
                .ExportAsync(Request(AccountShareMetric.Transfer)));

        Assert.Same(expected, actual);
        Assert.Equal(1, publisher.CallCount);
    }

    [Fact]
    public async Task CancellationFlowsThroughRowEnumerationAndPublish()
    {
        using var cancellation = new CancellationTokenSource();
        var publisher = new CancellingPublisher(cancellation);
        var first = new ShareAnalyticsWorkbookExportRow(
            "first", "https://pan.baidu.com/s/first", AccountShareRemoteStatus.Valid, 1, [1, 0, 0, 0, 0]);
        var second = new ShareAnalyticsWorkbookExportRow(
            "second", "https://pan.baidu.com/s/second", AccountShareRemoteStatus.Valid, 2, [2, 1, 0, 0, 0]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ShareAnalyticsWorkbookExportService(publisher)
                .ExportAsync(Request(AccountShareMetric.Transfer, first, second), cancellation.Token));

        Assert.Equal(1, publisher.EnumeratedRows);
    }

    [Fact]
    public async Task CancellationStopsBeforeANameCollisionRetry()
    {
        using var cancellation = new CancellationTokenSource();
        var publisher = new CancellingCollisionPublisher(cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ShareAnalyticsWorkbookExportService(publisher)
                .ExportAsync(Request(AccountShareMetric.Transfer), cancellation.Token));

        Assert.Equal(1, publisher.CallCount);
    }

    [Fact]
    public void ExportContractsRedactDestinationAndUserContentFromDiagnostics()
    {
        var row = new ShareAnalyticsWorkbookExportRow(
            "private title",
            "https://pan.baidu.com/s/private-link",
            AccountShareRemoteStatus.Valid,
            1,
            [1, 0, 0, 0, 0]);
        var request = new ShareAnalyticsWorkbookExportRequest(
            @"C:\private-export-directory",
            AccountShareMetric.Transfer,
            FrozenDates,
            [row]);

        Assert.DoesNotContain("private title", row.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-link", row.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-export-directory", request.ToString(), StringComparison.Ordinal);
    }

    private static ShareAnalyticsWorkbookExportRequest Request(
        AccountShareMetric metric,
        params ShareAnalyticsWorkbookExportRow[] rows) =>
        new("exports", metric, FrozenDates, rows);

    private sealed class CapturingPublisher : IReplacementWorkbookFilePublisher
    {
        public ReplacementWorkbookLayout? Layout { get; private set; }
        public List<string> FileNames { get; } = [];
        public List<ReplacementWorkbookRow> Rows { get; } = [];

        public async Task<ReplacementWorkbookFilePublishReceipt> PublishAsync(
            string destinationDirectory,
            string finalFileName,
            ReplacementWorkbookLayout layout,
            IAsyncEnumerable<ReplacementWorkbookRow> rows,
            CancellationToken cancellationToken = default)
        {
            Layout = layout;
            FileNames.Add(finalFileName);
            await foreach (var row in rows.WithCancellation(cancellationToken)) Rows.Add(row);
            return new(Path.Combine(destinationDirectory, finalFileName), Rows.Count, 1);
        }
    }

    private sealed class CollisionThenSuccessPublisher : IReplacementWorkbookFilePublisher
    {
        public List<string> FileNames { get; } = [];
        public List<ImmutableArray<ReplacementWorkbookRow>> AttemptRows { get; } = [];

        public async Task<ReplacementWorkbookFilePublishReceipt> PublishAsync(
            string destinationDirectory,
            string finalFileName,
            ReplacementWorkbookLayout layout,
            IAsyncEnumerable<ReplacementWorkbookRow> rows,
            CancellationToken cancellationToken = default)
        {
            FileNames.Add(finalFileName);
            var attemptRows = ImmutableArray.CreateBuilder<ReplacementWorkbookRow>();
            await foreach (var row in rows.WithCancellation(cancellationToken)) attemptRows.Add(row);
            AttemptRows.Add(attemptRows.ToImmutable());
            if (FileNames.Count == 1)
                throw new ReplacementWorkbookFilePublishException(
                    ReplacementWorkbookFilePublishErrorCode.DestinationExists,
                    "collision");
            return new(Path.Combine(destinationDirectory, finalFileName), attemptRows.Count, 1);
        }
    }

    private sealed class AlwaysCollisionPublisher : IReplacementWorkbookFilePublisher
    {
        public List<string> FileNames { get; } = [];

        public Task<ReplacementWorkbookFilePublishReceipt> PublishAsync(
            string destinationDirectory,
            string finalFileName,
            ReplacementWorkbookLayout layout,
            IAsyncEnumerable<ReplacementWorkbookRow> rows,
            CancellationToken cancellationToken = default)
        {
            FileNames.Add(finalFileName);
            throw new ReplacementWorkbookFilePublishException(
                ReplacementWorkbookFilePublishErrorCode.DestinationExists,
                "collision");
        }
    }

    private sealed class ThrowingPublisher(Exception error) : IReplacementWorkbookFilePublisher
    {
        public int CallCount { get; private set; }

        public Task<ReplacementWorkbookFilePublishReceipt> PublishAsync(
            string destinationDirectory,
            string finalFileName,
            ReplacementWorkbookLayout layout,
            IAsyncEnumerable<ReplacementWorkbookRow> rows,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            throw error;
        }
    }

    private sealed class CancellingPublisher(CancellationTokenSource cancellation)
        : IReplacementWorkbookFilePublisher
    {
        public int EnumeratedRows { get; private set; }

        public async Task<ReplacementWorkbookFilePublishReceipt> PublishAsync(
            string destinationDirectory,
            string finalFileName,
            ReplacementWorkbookLayout layout,
            IAsyncEnumerable<ReplacementWorkbookRow> rows,
            CancellationToken cancellationToken = default)
        {
            await using var enumerator = rows.GetAsyncEnumerator(cancellationToken);
            while (await enumerator.MoveNextAsync())
            {
                EnumeratedRows++;
                if (EnumeratedRows == 1) cancellation.Cancel();
            }
            return new(Path.Combine(destinationDirectory, finalFileName), EnumeratedRows, 1);
        }
    }

    private sealed class CancellingCollisionPublisher(CancellationTokenSource cancellation)
        : IReplacementWorkbookFilePublisher
    {
        public int CallCount { get; private set; }

        public Task<ReplacementWorkbookFilePublishReceipt> PublishAsync(
            string destinationDirectory,
            string finalFileName,
            ReplacementWorkbookLayout layout,
            IAsyncEnumerable<ReplacementWorkbookRow> rows,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (CallCount == 1)
            {
                cancellation.Cancel();
                throw new ReplacementWorkbookFilePublishException(
                    ReplacementWorkbookFilePublishErrorCode.DestinationExists,
                    "collision");
            }
            return Task.FromResult(new ReplacementWorkbookFilePublishReceipt(
                Path.Combine(destinationDirectory, finalFileName), 0, 1));
        }
    }

    private sealed class FixedLocalTimeProvider(
        DateTimeOffset utcNow,
        TimeZoneInfo localTimeZone) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
        public override TimeZoneInfo LocalTimeZone => localTimeZone;
    }
}
