using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.ShareAnalytics;

public sealed record ShareAnalyticsWorkbookExportRow
{
    public ShareAnalyticsWorkbookExportRow(
        string title,
        string shareLink,
        AccountShareRemoteStatus status,
        long? historicalTotal,
        IReadOnlyList<long?> dailyValues)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(shareLink);
        ArgumentNullException.ThrowIfNull(dailyValues);
        if (!Enum.IsDefined(status)) throw new ArgumentOutOfRangeException(nameof(status));
        if (dailyValues.Count != 5)
            throw new ArgumentException("Exactly five daily values are required.", nameof(dailyValues));

        Title = title;
        ShareLink = shareLink;
        Status = status;
        HistoricalTotal = historicalTotal;
        DailyValues = dailyValues.ToImmutableArray();
    }

    public string Title { get; }
    public string ShareLink { get; }
    public AccountShareRemoteStatus Status { get; }
    public long? HistoricalTotal { get; }
    public ImmutableArray<long?> DailyValues { get; }
    public long? NewValue => DailyValues[0] is { } today && DailyValues[1] is { } yesterday
        ? today - yesterday
        : null;

    public override string ToString() =>
        $"ShareAnalyticsWorkbookExportRow {{ Status = {Status}, Values = [REDACTED] }}";
}

public sealed record ShareAnalyticsWorkbookExportRequest
{
    public ShareAnalyticsWorkbookExportRequest(
        string destinationDirectory,
        AccountShareMetric metric,
        IReadOnlyList<DateOnly> dates,
        IEnumerable<ShareAnalyticsWorkbookExportRow> selectedRows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ArgumentNullException.ThrowIfNull(dates);
        ArgumentNullException.ThrowIfNull(selectedRows);
        if (!Enum.IsDefined(metric)) throw new ArgumentOutOfRangeException(nameof(metric));
        if (dates.Count != 5)
            throw new ArgumentException("Exactly five dates are required.", nameof(dates));

        DestinationDirectory = destinationDirectory;
        Metric = metric;
        Dates = dates.ToImmutableArray();
        SelectedRows = selectedRows.ToImmutableArray();
        if (SelectedRows.Any(row => row is null))
            throw new ArgumentException("Selected rows cannot contain null.", nameof(selectedRows));
    }

    public string DestinationDirectory { get; }
    public AccountShareMetric Metric { get; }
    public ImmutableArray<DateOnly> Dates { get; }
    public ImmutableArray<ShareAnalyticsWorkbookExportRow> SelectedRows { get; }

    public override string ToString() =>
        $"ShareAnalyticsWorkbookExportRequest {{ Metric = {Metric}, DateCount = {Dates.Length}, SelectedRowCount = {SelectedRows.Length}, Destination = [REDACTED] }}";
}

public sealed record ShareAnalyticsWorkbookExportResult(
    string FinalPath,
    long ExportedRows,
    int SheetCount)
{
    public override string ToString() =>
        $"ShareAnalyticsWorkbookExportResult {{ ExportedRows = {ExportedRows}, SheetCount = {SheetCount}, FinalPath = [REDACTED] }}";
}

public interface IShareAnalyticsWorkbookExportService
{
    Task<ShareAnalyticsWorkbookExportResult> ExportAsync(
        ShareAnalyticsWorkbookExportRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class ShareAnalyticsWorkbookExportService(
    IReplacementWorkbookFilePublisher publisher,
    TimeProvider? timeProvider = null) : IShareAnalyticsWorkbookExportService
{
    private const int MaximumFileNameAttempts = 999;
    private readonly IReplacementWorkbookFilePublisher publisher =
        publisher ?? throw new ArgumentNullException(nameof(publisher));
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<ShareAnalyticsWorkbookExportResult> ExportAsync(
        ShareAnalyticsWorkbookExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var localNow = timeProvider.GetLocalNow();
        var timestamp = localNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var layout = BuildLayout(request.Dates);
        for (var attempt = 1; attempt <= MaximumFileNameAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var suffix = attempt == 1 ? string.Empty : $"({attempt.ToString(CultureInfo.InvariantCulture)})";
            var fileName = $"分享数据-{timestamp}{suffix}.xlsx";
            try
            {
                var receipt = await publisher.PublishAsync(
                    request.DestinationDirectory,
                    fileName,
                    layout,
                    StreamRows(request, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                return new(receipt.FinalPath, receipt.ExportedRows, receipt.SheetCount);
            }
            catch (ReplacementWorkbookFilePublishException error) when (
                error.Code == ReplacementWorkbookFilePublishErrorCode.DestinationExists &&
                attempt < MaximumFileNameAttempts)
            {
            }
        }

        throw new InvalidOperationException("The share analytics workbook publish loop did not complete.");
    }

    private static ReplacementWorkbookLayout BuildLayout(ImmutableArray<DateOnly> dates) => new(
    [
        "序号",
        "指标",
        "历史累计",
        "新增",
        "今天",
        "昨天",
        dates[2].ToString("MM-dd", CultureInfo.InvariantCulture),
        dates[3].ToString("MM-dd", CultureInfo.InvariantCulture),
        dates[4].ToString("MM-dd", CultureInfo.InvariantCulture),
        "链接",
        "标题",
        "状态",
    ]);

    private static async IAsyncEnumerable<ReplacementWorkbookRow> StreamRows(
        ShareAnalyticsWorkbookExportRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        long sequence = 0;
        foreach (var row in request.SelectedRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sequence = checked(sequence + 1);
            yield return new(sequence,
            [
                sequence.ToString(CultureInfo.InvariantCulture),
                MetricLabel(request.Metric),
                Format(row.HistoricalTotal),
                Format(row.NewValue),
                Format(row.DailyValues[0]),
                Format(row.DailyValues[1]),
                Format(row.DailyValues[2]),
                Format(row.DailyValues[3]),
                Format(row.DailyValues[4]),
                row.ShareLink,
                row.Title,
                row.Status == AccountShareRemoteStatus.Valid ? "有效" : "失效",
            ]);
        }
        await Task.CompletedTask;
    }

    private static string MetricLabel(AccountShareMetric metric) => metric switch
    {
        AccountShareMetric.Transfer => "转存",
        AccountShareMetric.View => "浏览",
        AccountShareMetric.Download => "下载",
        _ => throw new ArgumentOutOfRangeException(nameof(metric)),
    };

    private static string Format(long? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
}
