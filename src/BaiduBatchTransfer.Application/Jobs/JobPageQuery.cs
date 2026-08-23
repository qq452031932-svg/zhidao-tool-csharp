using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Jobs;

public sealed record ResourcePageRequest(
    Guid BatchJobId,
    string? CandidateStatus,
    string? GlobalValidityStatus,
    int PageSize,
    long? AfterCandidateSequence = null);

public sealed record ResourcePageResult(
    IReadOnlyList<ResourceItemRecord> Items,
    long TotalCount,
    long? NextCursor);

public sealed record OperationPageRequest(
    Guid BatchJobId,
    Guid? AccountId,
    string? Status,
    DateTimeOffset? UpdatedFrom,
    DateTimeOffset? UpdatedTo,
    int PageSize,
    long? AfterAllocationOrdinal = null);

public sealed record OperationPageResult(
    IReadOnlyList<OperationPageItem> Items,
    long TotalCount,
    long? NextCursor);

public sealed record OperationPageItem(
    Guid OperationId,
    Guid ResourceItemId,
    Guid BatchJobId,
    long CandidateSequence,
    long AllocationOrdinal,
    string ShareUrl,
    string? Note,
    string ResolvedTitle,
    Guid AccountId,
    string AccountDisplayName,
    string TargetPath,
    string Status,
    string TransferStatusText,
    string? OutcomeKind,
    string? LastErrorCode,
    string? LastErrorMessage,
    DateTimeOffset UpdatedAt,
    Guid? ShareOperationId,
    string? ShareStatus,
    string ShareStatusText,
    string? ShareLastErrorCode,
    string? ShareLastErrorMessage,
    int? ShareGeneration,
    DateTimeOffset? ShareUpdatedAt,
    bool CanRetryShare,
    bool CanReconcileTransfer,
    bool CanReconcileShare,
    bool CanResumeTransfer,
    bool CanResumeShare,
    int TransferAttemptCount)
{
    public string StatusText => TransferStatusText;

    internal static OperationPageItem FromPersistence(OperationResultRecord record)
    {
        var transferStatusText = Enum.TryParse<OperationStatus>(record.Status, ignoreCase: false, out var status) &&
            Enum.IsDefined(status)
            ? OperationStatusChineseMapper.Map(status)
            : "状态未知";
        var shareStatusText = record.ShareStatus is null
            ? record.ShareEnabled
                ? record.Status is "Failed" or "Cancelled" ? "转存未成功，未生成分享" : "等待生成分享"
                : "未启用分享"
            : Enum.TryParse<ShareOperationStatus>(record.ShareStatus, ignoreCase: false, out var shareStatus) &&
              Enum.IsDefined(shareStatus)
                ? ShareOperationStatusChineseMapper.Map(shareStatus)
                : "状态未知";
        var shareActionGate = record.HasCompleteShareEvidence && record.IsBatchShareActionAllowed &&
            record.IsAccountActive && record.ShareEnabled && record.ShareCanRestartSafely;
        return new(
            record.OperationId,
            record.ResourceItemId,
            record.BatchJobId,
            record.CandidateSequence,
            record.AllocationOrdinal,
            record.ShareUrl,
            record.Note,
            string.IsNullOrWhiteSpace(record.ResolvedTitle) ? "未命名资源" : record.ResolvedTitle,
            record.AccountId,
            record.AccountDisplayName,
            record.TargetPath,
            record.Status,
            transferStatusText,
            record.OutcomeKind,
            record.LastErrorCode,
            record.LastErrorMessage,
            record.UpdatedAt,
            record.ShareOperationId,
            record.ShareStatus,
            shareStatusText,
            record.ShareLastErrorCode,
            record.ShareLastErrorMessage,
            record.ShareGeneration,
            record.ShareUpdatedAt,
            record.Status == OperationStatus.Succeeded.ToString() &&
                record.ShareStatus == ShareOperationStatus.Failed.ToString() && shareActionGate,
            record.Status == OperationStatus.ResultPending.ToString(),
            record.ShareStatus == ShareOperationStatus.ResultPending.ToString(),
            record.Status == OperationStatus.Paused.ToString(),
            record.ShareStatus == ShareOperationStatus.Paused.ToString() && shareActionGate &&
                record.ShareLastErrorCode is not ("AwaitingExistingShareDecision" or "CancelledByUser" or "IncompatibleExistingShare"),
            record.TransferAttemptCount);
    }

    public override string ToString() =>
        $"OperationPageItem {{ OperationId = {OperationId}, CandidateSequence = {CandidateSequence}, TransferStatus = {Status}, TransferStatusText = {TransferStatusText}, ShareStatus = {ShareStatus}, UserContent = [REDACTED] }}";
}

public sealed class JobPageQuery(IUnitOfWorkFactory unitOfWorkFactory)
{
    private readonly IUnitOfWorkFactory unitOfWorkFactory =
        unitOfWorkFactory ?? throw new ArgumentNullException(nameof(unitOfWorkFactory));

    public async Task<ResourcePageResult> GetResourcesAsync(
        ResourcePageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var persistenceQuery = new ResourceResultKeysetQuery(
            request.BatchJobId,
            request.CandidateStatus,
            request.GlobalValidityStatus,
            request.PageSize,
            request.AfterCandidateSequence ?? 0);
        persistenceQuery.Validate();
        await using var unit = await unitOfWorkFactory.BeginAsync(cancellationToken).ConfigureAwait(false);
        var page = await unit.Jobs.GetResourceResultsKeysetPageAsync(persistenceQuery, cancellationToken)
            .ConfigureAwait(false);
        return new(
            page.Items,
            page.TotalCount ?? throw new InvalidOperationException("The resource query did not return its total count."),
            page.NextCursor);
    }

    public async Task<OperationPageResult> GetOperationsAsync(
        OperationPageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var persistenceQuery = new OperationResultKeysetQuery(
            request.BatchJobId,
            request.AccountId,
            request.Status,
            request.UpdatedFrom,
            request.UpdatedTo,
            request.PageSize,
            request.AfterAllocationOrdinal ?? 0);
        persistenceQuery.Validate();
        await using var unit = await unitOfWorkFactory.BeginAsync(cancellationToken).ConfigureAwait(false);
        var page = await unit.Jobs.GetOperationResultsKeysetPageAsync(persistenceQuery, cancellationToken)
            .ConfigureAwait(false);
        return new(
            page.Items.Select(OperationPageItem.FromPersistence).ToArray(),
            page.TotalCount ?? throw new InvalidOperationException("The operation query did not return its total count."),
            page.NextCursor);
    }
}
