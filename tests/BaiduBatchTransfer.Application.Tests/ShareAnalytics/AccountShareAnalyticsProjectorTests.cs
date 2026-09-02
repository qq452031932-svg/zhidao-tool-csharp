using BaiduBatchTransfer.Application.ShareAnalytics;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Tests.ShareAnalytics;

public sealed class AccountShareAnalyticsProjectorTests
{
    [Fact]
    public void BeijingMidnightDefinesTheFiveDateWindow()
    {
        var now = new DateTimeOffset(2026, 9, 1, 16, 30, 0, TimeSpan.Zero);

        Assert.Equal(
            [new DateOnly(2026, 9, 2), new(2026, 9, 1), new(2026, 8, 31), new(2026, 8, 30), new(2026, 8, 29)],
            Projector().FiveDayWindow(now));
    }

    [Fact]
    public void DirectDailyValueTakesPriorityOverCumulativeDifference()
    {
        var today = new DateOnly(2026, 9, 2);
        var history = MetricHistory(
            AccountShareMetric.Transfer,
            historicalTotal: 120,
            cumulative: new Dictionary<DateOnly, long?>
            {
                [today] = 120,
                [today.AddDays(-1)] = 100,
            },
            direct: new Dictionary<DateOnly, long?> { [today] = 7 });

        var row = Projector().Project(Item(history), AccountShareMetric.Transfer, Noon(today));

        Assert.Equal(7, row.Today);
        Assert.Equal("7", row.TodayText);
    }

    [Fact]
    public void CumulativeSnapshotsDeriveDailyValuesAndNewValue()
    {
        var today = new DateOnly(2026, 9, 2);
        var history = MetricHistory(
            AccountShareMetric.Transfer,
            historicalTotal: 130,
            cumulative: new Dictionary<DateOnly, long?>
            {
                [today] = 130,
                [today.AddDays(-1)] = 100,
                [today.AddDays(-2)] = 80,
            });

        var row = Projector().Project(Item(history), AccountShareMetric.Transfer, Noon(today));

        Assert.Equal(30, row.Today);
        Assert.Equal(20, row.Yesterday);
        Assert.Equal(10, row.NewValue);
        Assert.Equal("+10", row.NewText);
    }

    [Fact]
    public void MissingYesterdayMakesNewValueUnavailable()
    {
        var today = new DateOnly(2026, 9, 2);
        var history = MetricHistory(
            AccountShareMetric.Transfer,
            historicalTotal: 12,
            direct: new Dictionary<DateOnly, long?> { [today] = 12 });

        var row = Projector().Project(Item(history), AccountShareMetric.Transfer, Noon(today));

        Assert.Equal("12", row.TodayText);
        Assert.Equal("--", row.YesterdayText);
        Assert.Equal("--", row.NewText);
    }

    [Fact]
    public void DecliningOrMissingCumulativeSnapshotsProduceMissingDailyValues()
    {
        var today = new DateOnly(2026, 9, 2);
        var history = MetricHistory(
            AccountShareMetric.View,
            historicalTotal: 90,
            cumulative: new Dictionary<DateOnly, long?>
            {
                [today] = 90,
                [today.AddDays(-1)] = 100,
            });

        var row = Projector().Project(Item(history), AccountShareMetric.View, Noon(today));

        Assert.Null(row.Today);
        Assert.Equal("--", row.TodayText);
    }

    [Fact]
    public void MissingValuesAlwaysSortLastAndTiesAreDeterministic()
    {
        var now = Noon(new DateOnly(2026, 9, 2));
        var older = Projector().Project(Item(MetricHistory(AccountShareMetric.Download, 5), "b", now.AddDays(-1)), AccountShareMetric.Download, now);
        var newer = Projector().Project(Item(MetricHistory(AccountShareMetric.Download, 5), "a", now), AccountShareMetric.Download, now);
        var missing = Projector().Project(Item(MetricHistory(AccountShareMetric.Download, null), "c", now.AddDays(1)), AccountShareMetric.Download, now);

        Assert.Equal(
            ["a", "b", "c"],
            Projector().Sort([older, missing, newer], AccountShareSortColumn.HistoricalTotal, descending: false)
                .Select(row => row.ShareId));
        Assert.Equal(
            ["a", "b", "c"],
            Projector().Sort([older, missing, newer], AccountShareSortColumn.HistoricalTotal, descending: true)
                .Select(row => row.ShareId));
    }

    private static AccountShareAnalyticsProjector Projector() => new();

    private static AccountShareMetricHistory MetricHistory(
        AccountShareMetric metric,
        long? historicalTotal,
        IReadOnlyDictionary<DateOnly, long?>? cumulative = null,
        IReadOnlyDictionary<DateOnly, long?>? direct = null) =>
        new(metric, historicalTotal, cumulative, direct);

    private static AccountShareCachedRecord Item(
        AccountShareMetricHistory metric,
        string shareId = "share-1",
        DateTimeOffset? sharedAt = null) => new(
            shareId,
            "测试标题",
            new Uri($"https://pan.baidu.com/s/{shareId}"),
            sharedAt ?? Noon(new DateOnly(2026, 9, 1)),
            AccountShareRemoteStatus.Valid,
            [
                metric,
                metric.Metric == AccountShareMetric.Transfer
                    ? MetricHistory(AccountShareMetric.View, null)
                    : MetricHistory(AccountShareMetric.Transfer, null),
                metric.Metric == AccountShareMetric.Download
                    ? MetricHistory(AccountShareMetric.View, null)
                    : MetricHistory(AccountShareMetric.Download, null),
            ]);

    private static DateTimeOffset Noon(DateOnly date) =>
        new(date.Year, date.Month, date.Day, 4, 0, 0, TimeSpan.Zero);
}
