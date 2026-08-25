using System.Runtime.CompilerServices;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Jobs;

public sealed class ExportStandaloneShareResultsService(
    IStandaloneShareResultProjectionRepository repository,
    TimeProvider? timeProvider = null)
{
    private static readonly ReplacementWorkbookLayout Layout = new(
        ["账号", "资源名称", "资源类型", "新分享链接", "提取码", "状态"]);
    private readonly IStandaloneShareResultProjectionRepository repository =
        repository ?? throw new ArgumentNullException(nameof(repository));
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<StandaloneShareResultExportResult> ExportAsync(
        StandaloneShareResultExportRequest request,
        Stream output,
        IReplacementWorkbookWriter writer,
        CancellationToken cancellationToken = default)
    {
        Validate(request);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(writer);
        long count = 0;
        await writer.WriteAsync(
            output,
            Layout,
            Rows(request, value => count = value, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        return new(count, BuildFileName(timeProvider.GetUtcNow()));
    }

    public async Task<ReplacementWorkbookFilePublishReceipt> ExportToDirectoryAsync(
        StandaloneShareResultExportRequest request,
        string destinationDirectory,
        IReplacementWorkbookFilePublisher publisher,
        CancellationToken cancellationToken = default)
    {
        Validate(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ArgumentNullException.ThrowIfNull(publisher);
        return await ExportToDirectoryAsync(
            request, destinationDirectory, BuildFileName(timeProvider.GetUtcNow()),
            publisher, cancellationToken).ConfigureAwait(false);
    }

    public string GetSuggestedFileName() => BuildFileName(timeProvider.GetUtcNow());

    public ReplacementWorkbookLayout GetExpectedLayout() => Layout;

    public async Task<ReplacementWorkbookFilePublishReceipt> ExportToDirectoryAsync(
        StandaloneShareResultExportRequest request,
        string destinationDirectory,
        string exactTargetFileName,
        IReplacementWorkbookFilePublisher publisher,
        CancellationToken cancellationToken = default,
        LocalResultWorkbookManifest? manifest = null)
    {
        Validate(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        LocalResultExportValidation.SafeFileName(exactTargetFileName, nameof(exactTargetFileName));
        ArgumentNullException.ThrowIfNull(publisher);
        if (manifest is not null)
            return await publisher.PublishWithManifestAsync(
                destinationDirectory,
                exactTargetFileName,
                Layout,
                Rows(request, _ => { }, cancellationToken),
                manifest,
                cancellationToken).ConfigureAwait(false);
        return await publisher.PublishAsync(
            destinationDirectory,
            exactTargetFileName,
            Layout,
            Rows(request, _ => { }, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private async IAsyncEnumerable<ReplacementWorkbookRow> Rows(
        StandaloneShareResultExportRequest request,
        Action<long> report,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var cursor = 0;
        long exported = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await repository.GetPageAsync(
                request.BatchId, cursor, request.PageSize, cancellationToken).ConfigureAwait(false);
            ArgumentNullException.ThrowIfNull(page);
            ArgumentNullException.ThrowIfNull(page.Items);
            if (page.Items.Count == 0)
            {
                if (page.NextSequenceNumber is not null)
                    throw new InvalidOperationException("The standalone result cursor did not advance.");
                yield break;
            }

            var previous = cursor;
            var ids = new HashSet<Guid>();
            foreach (var item in page.Items)
            {
                if (item.SequenceNumber <= previous || !ids.Add(item.OperationId))
                    throw new InvalidOperationException("The standalone result page is not a strict keyset page.");
                previous = item.SequenceNumber;
                yield return BuildRow(item);
                exported = checked(exported + 1);
                report(exported);
            }
            if (page.NextSequenceNumber is not { } next) yield break;
            if (next != previous)
                throw new InvalidOperationException("The standalone result cursor does not match the page boundary.");
            cursor = next;
        }
    }

    private static ReplacementWorkbookRow BuildRow(StandaloneShareResultProjection item)
    {
        var success = item.Status == StandaloneShareOperationStatus.Succeeded;
        if (success && (!Uri.TryCreate(item.ShareUrl, UriKind.Absolute, out var shareUri) ||
                        shareUri.Scheme != Uri.UriSchemeHttps ||
                        string.IsNullOrWhiteSpace(item.ExtractionCode)))
            throw new InvalidOperationException("A successful standalone share result is incomplete.");
        return new(item.SequenceNumber,
        [
            item.AccountDisplayName,
            item.ResourceDisplayName,
            item.ResourceKind == RemoteResourceKind.Folder ? "文件夹" : "文件",
            success ? item.ShareUrl : string.Empty,
            success ? item.ExtractionCode : string.Empty,
            StableStatus(item.Status),
        ]);
    }

    private static string StableStatus(StandaloneShareOperationStatus status) => status switch
    {
        StandaloneShareOperationStatus.Queued => "等待分享",
        StandaloneShareOperationStatus.Reserved or StandaloneShareOperationStatus.Preparing => "准备中",
        StandaloneShareOperationStatus.Submitting => "分享中",
        StandaloneShareOperationStatus.ResultPending => "分享结果待确认",
        StandaloneShareOperationStatus.NeedsDecision => "需要人工确认",
        StandaloneShareOperationStatus.Succeeded => "分享成功",
        StandaloneShareOperationStatus.Failed => "分享失败",
        StandaloneShareOperationStatus.Paused => "暂停",
        StandaloneShareOperationStatus.Cancelled => "已取消",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    private static string BuildFileName(DateTimeOffset now) =>
        $"百度分享结果_{now:yyyyMMdd_HHmmss}.xlsx";

    private static void Validate(StandaloneShareResultExportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.BatchId == Guid.Empty) throw new ArgumentException("A batch is required.", nameof(request));
        if (request.PageSize is < 1 or > 1_000) throw new ArgumentOutOfRangeException(nameof(request.PageSize));
    }
}
