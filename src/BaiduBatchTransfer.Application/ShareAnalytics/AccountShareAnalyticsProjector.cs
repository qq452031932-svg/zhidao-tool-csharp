using System.Collections.Immutable;
using System.Globalization;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.ShareAnalytics;

public enum AccountShareSortColumn
{
    SharedAt,
    HistoricalTotal,
    NewValue,
    Today,
    Yesterday,
    PriorDay2,
    PriorDay3,
    PriorDay4,
}

public sealed record AccountShareProjectedRow
{
    public AccountShareProjectedRow(
        AccountShareCachedRecord source,
        AccountShareMetric metric,
        long? historicalTotal,
        IReadOnlyList<DateOnly> dates,
        IReadOnlyList<long?> dailyValues)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(dates);
        ArgumentNullException.ThrowIfNull(dailyValues);
        if (dates.Count != 5 || dailyValues.Count != 5)
            throw new ArgumentException("Exactly five dates and values are required.");
        Source = source;
        Metric = metric;
        HistoricalTotal = historicalTotal;
        Dates = dates.ToImmutableArray();
        DailyValues = dailyValues.ToImmutableArray();
    }

    public AccountShareCachedRecord Source { get; }
    public string ShareId => Source.ShareId;
    public string Title => Source.Title;
    public Uri ShareUri => Source.ShareUri;
    public DateTimeOffset SharedAtUtc => Source.SharedAtUtc;
    public AccountShareRemoteStatus Status => Source.Status;
    public AccountShareMetric Metric { get; }
    public long? HistoricalTotal { get; }
    public IReadOnlyList<DateOnly> Dates { get; }
    public IReadOnlyList<long?> DailyValues { get; }
    public long? Today => DailyValues[0];
    public long? Yesterday => DailyValues[1];
    public long? NewValue => Today is not null && Yesterday is not null
        ? Today.Value - Yesterday.Value
        : null;
    public string HistoricalTotalText => FormatCount(HistoricalTotal);
    public string TodayText => FormatCount(Today);
    public string YesterdayText => FormatCount(Yesterday);
    public string NewText => NewValue switch
    {
        null or 0 => "--",
        > 0 => $"+{NewValue.Value.ToString("N0", CultureInfo.InvariantCulture)}",
        _ => NewValue.Value.ToString("N0", CultureInfo.InvariantCulture),
    };

    public string DailyText(int index) => FormatCount(DailyValues[index]);

    private static string FormatCount(long? value) =>
        value?.ToString("N0", CultureInfo.InvariantCulture) ?? "--";
}

public sealed class AccountShareAnalyticsProjector
{
    private readonly TimeZoneInfo beijingTimeZone;

    public AccountShareAnalyticsProjector()
    {
        beijingTimeZone = ResolveBeijingTimeZone();
    }

    public IReadOnlyList<DateOnly> FiveDayWindow(DateTimeOffset nowUtc)
    {
        var beijing = TimeZoneInfo.ConvertTime(nowUtc, beijingTimeZone);
        var today = DateOnly.FromDateTime(beijing.DateTime);
        return Enumerable.Range(0, 5)
            .Select(offset => today.AddDays(-offset))
            .ToImmutableArray();
    }

    public AccountShareProjectedRow Project(
        AccountShareCachedRecord item,
        AccountShareMetric metric,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!Enum.IsDefined(metric)) throw new ArgumentOutOfRangeException(nameof(metric));
        var history = item.Metrics.Single(value => value.Metric == metric);
        var dates = FiveDayWindow(nowUtc);
        var values = dates.Select(date => DailyValue(history, date)).ToImmutableArray();
        return new(item, metric, history.HistoricalTotal, dates, values);
    }

    public IReadOnlyList<AccountShareProjectedRow> Sort(
        IEnumerable<AccountShareProjectedRow> rows,
        AccountShareSortColumn column,
        bool descending)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (!Enum.IsDefined(column)) throw new ArgumentOutOfRangeException(nameof(column));
        var copy = rows.ToList();
        copy.Sort((left, right) => CompareRows(left, right, column, descending));
        return copy.AsReadOnly();
    }

    private static long? DailyValue(AccountShareMetricHistory history, DateOnly date)
    {
        if (history.DirectDailyValues.TryGetValue(date, out var direct))
            return direct;
        if (!history.CumulativeTotals.TryGetValue(date, out var current) || current is null ||
            !history.CumulativeTotals.TryGetValue(date.AddDays(-1), out var previous) || previous is null ||
            current.Value < previous.Value)
            return null;
        return current.Value - previous.Value;
    }

    private static int CompareRows(
        AccountShareProjectedRow left,
        AccountShareProjectedRow right,
        AccountShareSortColumn column,
        bool descending)
    {
        if (column == AccountShareSortColumn.SharedAt)
        {
            var time = right.SharedAtUtc.CompareTo(left.SharedAtUtc);
            if (!descending) time = -time;
            return time != 0 ? time : StringComparer.Ordinal.Compare(left.ShareId, right.ShareId);
        }

        var leftValue = NumericValue(left, column);
        var rightValue = NumericValue(right, column);
        if (leftValue is null || rightValue is null)
        {
            if (leftValue is null && rightValue is null)
                return BreakTie(left, right);
            return leftValue is null ? 1 : -1;
        }

        var numeric = leftValue.Value.CompareTo(rightValue.Value);
        if (descending) numeric = -numeric;
        return numeric != 0 ? numeric : BreakTie(left, right);
    }

    private static long? NumericValue(
        AccountShareProjectedRow row,
        AccountShareSortColumn column) => column switch
    {
        AccountShareSortColumn.HistoricalTotal => row.HistoricalTotal,
        AccountShareSortColumn.NewValue => row.NewValue,
        AccountShareSortColumn.Today => row.DailyValues[0],
        AccountShareSortColumn.Yesterday => row.DailyValues[1],
        AccountShareSortColumn.PriorDay2 => row.DailyValues[2],
        AccountShareSortColumn.PriorDay3 => row.DailyValues[3],
        AccountShareSortColumn.PriorDay4 => row.DailyValues[4],
        _ => throw new ArgumentOutOfRangeException(nameof(column)),
    };

    private static int BreakTie(AccountShareProjectedRow left, AccountShareProjectedRow right)
    {
        var time = right.SharedAtUtc.CompareTo(left.SharedAtUtc);
        return time != 0 ? time : StringComparer.Ordinal.Compare(left.ShareId, right.ShareId);
    }

    private static TimeZoneInfo ResolveBeijingTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
        }
    }
}
