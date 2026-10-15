using BaiduBatchTransfer.Application.Jobs;

namespace BaiduBatchTransfer.App.Features.Jobs;

public sealed class LocalJobResultsWorkflow(
    JobPageQuery pageQuery,
    BatchStatisticsQuery statisticsQuery,
    ReplacementWorkbookExportService exportService,
    IReplacementWorkbookFilePublisher workbookPublisher,
    IJobResultActions actions) : IJobResultsWorkflow
{
    private readonly JobPageQuery pageQuery = pageQuery ?? throw new ArgumentNullException(nameof(pageQuery));
    private readonly BatchStatisticsQuery statisticsQuery = statisticsQuery ?? throw new ArgumentNullException(nameof(statisticsQuery));
    private readonly ReplacementWorkbookExportService exportService = exportService ?? throw new ArgumentNullException(nameof(exportService));
    private readonly IReplacementWorkbookFilePublisher workbookPublisher = workbookPublisher ?? throw new ArgumentNullException(nameof(workbookPublisher));
    private readonly IJobResultActions actions = actions ?? throw new ArgumentNullException(nameof(actions));

    public async Task<JobResultsSummary> LoadSummaryAsync(
        Guid batchJobId,
        CancellationToken cancellationToken = default)
    {
        var statistics = await statisticsQuery.ExecuteAsync(batchJobId, cancellationToken).ConfigureAwait(false);
        return new(
            statistics.ResourceCount,
            statistics.OperationCount,
            statistics.Accounts.Sum(account => account.FailedCount),
            statistics.Accounts.Sum(account => account.PausedCount),
            statistics.Accounts.Sum(account => account.CompleteSuccessCount),
            statistics.Accounts.Sum(account => account.TerminalProblemCount),
            statistics.Accounts.Sum(account => account.CancelledCount));
    }

    public async Task<JobResultsPage> LoadPageAsync(
        Guid batchJobId,
        long? afterCursor,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var page = await pageQuery.GetOperationsAsync(
            new OperationPageRequest(batchJobId, null, null, null, null, pageSize, afterCursor),
            cancellationToken).ConfigureAwait(false);
        return new(
            page.Items.Select(item => new JobResultRow(
                item.OperationId,
                item.ShareOperationId,
                item.CandidateSequence,
                item.AccountDisplayName,
                item.ResolvedTitle,
                item.TransferStatusText,
                item.ShareStatusText,
                item.LastErrorCode,
                item.ShareLastErrorCode,
                item.UpdatedAt,
                item.ShareGeneration,
                item.ShareUpdatedAt,
                item.CanReconcileTransfer,
                item.CanReconcileShare,
                item.CanRetryShare,
                item.CanResumeTransfer,
                item.CanResumeShare,
                item.TransferAttemptCount,
                item.Status,
                item.CanSafelyRetryTransfer,
                item.LastErrorMessage,
                item.ShareUrl,
                item.GeneratedShareUrl,
                item.GeneratedExtractionCode,
                item.TargetPath,
                item.StagedImportRowId,
                item.ShareStatus,
                item.ShareEnabled)).ToArray(),
            page.TotalCount,
            page.NextCursor);
    }

    public async Task<JobResultActionOutcome> RetryShareOnlyAsync(
        JobResultRow row,
        CancellationToken cancellationToken = default) =>
        Map(await actions.RetryShareOnlyAsync(
            row.ShareOperationId ?? Guid.Empty,
            row.ShareGeneration ?? -1,
            row.ShareUpdatedAt ?? DateTimeOffset.MinValue,
            cancellationToken).ConfigureAwait(false));

    public async Task<JobResultActionOutcome> ReconcileTransferAsync(
        Guid transferOperationId,
        CancellationToken cancellationToken = default) =>
        Map(await actions.ReconcileTransferAsync(transferOperationId, cancellationToken).ConfigureAwait(false));

    public async Task<JobResultActionOutcome> ReconcileShareAsync(
        Guid shareOperationId,
        CancellationToken cancellationToken = default) =>
        Map(await actions.ReconcileShareAsync(shareOperationId, cancellationToken).ConfigureAwait(false));

    public async Task<JobResultActionOutcome> ResumeTransferAsync(
        JobResultRow row,
        CancellationToken cancellationToken = default) =>
        Map(await actions.ResumeTransferAsync(
            row.TransferOperationId, row.TransferAttemptCount, row.TransferUpdatedAt, cancellationToken).ConfigureAwait(false));

    public async Task<JobResultActionOutcome> ResumeShareAsync(
        JobResultRow row,
        CancellationToken cancellationToken = default) =>
        Map(await actions.ResumeShareAsync(
            row.ShareOperationId ?? Guid.Empty,
            row.ShareGeneration ?? -1,
            row.ShareUpdatedAt ?? DateTimeOffset.MinValue,
            cancellationToken).ConfigureAwait(false));

    public async Task<long> ExportAsync(
        Guid batchJobId,
        string destinationDirectory,
        IProgress<long> progress,
        CancellationToken cancellationToken = default)
    {
        // 一个网盘账号一个文件，文件名是账号名+时间戳，每个文件固定五列。
        // 之前是把所有账号写进同一个文件，列随账号数翻倍，文件名还跟着原始导入表格走。
        var receipts = await exportService.ExportPerAccountToDirectoryAsync(
            new ReplacementWorkbookExportRequest(batchJobId),
            destinationDirectory,
            workbookPublisher,
            progress,
            cancellationToken).ConfigureAwait(false);
        return receipts.Sum(receipt => receipt.Receipt.ExportedRows);
    }

    private static JobResultActionOutcome Map(JobResultActionResult result) =>
        new(result.Accepted, result.IsStale, result.StatusText);
}
