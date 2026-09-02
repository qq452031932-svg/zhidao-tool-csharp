using BaiduBatchTransfer.Application.ShareAnalytics;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.App.Features.ShareAnalytics;

public enum ShareAnalyticsStatusFilter
{
    All,
    Valid,
    Invalid,
}

public sealed record ShareAnalyticsStatusOption(ShareAnalyticsStatusFilter Value, string Label)
{
    public override string ToString() => Label;
}

public interface IShareLinkLauncher
{
    bool TryOpen(Uri uri, out string safeMessage);
}

public interface IShareAnalyticsDelay
{
    Task WaitAsync(TimeSpan delay, CancellationToken token);
}

public sealed class ShareAnalyticsRow(AccountShareProjectedRow projected)
{
    public string ShareId => projected.ShareId;
    public string Title => projected.Title;
    public Uri ShareUri => projected.ShareUri;
    public DateTimeOffset SharedAtUtc => projected.SharedAtUtc;
    public string SharedAtText => projected.SharedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public AccountShareRemoteStatus Status => projected.Status;
    public string StatusText => Status == AccountShareRemoteStatus.Valid ? "有效" : "失效";
    public AccountShareMetric Metric => projected.Metric;
    public long? HistoricalTotal => projected.HistoricalTotal;
    public long? NewValue => projected.NewValue;
    public string HistoricalTotalText => projected.HistoricalTotalText;
    public string NewText => projected.NewText;
    public string NewForeground => NewValue switch { > 0 => "#E5484D", < 0 => "#1E8E5A", _ => "#52677B" };
    public string TodayText => projected.TodayText;
    public string YesterdayText => projected.YesterdayText;
    public string PriorDay2Text => projected.DailyText(2);
    public string PriorDay3Text => projected.DailyText(3);
    public string PriorDay4Text => projected.DailyText(4);
    internal AccountShareProjectedRow Projected => projected;
}
