namespace BaiduBatchTransfer.App.Features.Jobs;

public sealed record JobResultRow(
    Guid TransferOperationId,
    Guid? ShareOperationId,
    long CandidateSequence,
    string AccountDisplayName,
    string ResolvedTitle,
    string TransferStatusText,
    string ShareStatusText,
    string? TransferErrorCode,
    string? ShareErrorCode,
    DateTimeOffset TransferUpdatedAt,
    int? ShareGeneration,
    DateTimeOffset? ShareUpdatedAt,
    bool CanReconcileTransfer,
    bool CanReconcileShare,
    bool CanRetryShare,
    bool CanResumeTransfer = false,
    bool CanResumeShare = false,
    int TransferAttemptCount = 0,
    string TransferStatus = "",
    bool CanSafelyRetryTransfer = false,
    string? TransferDetailMessage = null,
    string? OriginalShareUrl = null,
    string? GeneratedShareUrl = null,
    string? GeneratedExtractionCode = null,
    string? TargetPath = null,
    Guid? StagedImportRowId = null,
    string? ShareStatus = null,
    bool ShareEnabled = true)
{
    public override string ToString() =>
        $"JobResultRow {{ TransferOperationId = {TransferOperationId}, CandidateSequence = {CandidateSequence}, TransferStatus = {TransferStatus}, ShareStatus = {ShareStatusText}, UserContent = [REDACTED] }}";
}

public sealed record JobResultsPage(
    IReadOnlyList<JobResultRow> Items,
    long TotalCount,
    long? NextCursor);

public sealed record JobResultsSummary(
    long ResourceCount,
    long OperationCount,
    long FailedCount,
    long PausedCount,
    long CompleteSuccessCount = 0,
    long TerminalProblemCount = 0);

public sealed record JobResultActionOutcome(bool Accepted, bool IsStale, string StatusText);

public interface IJobResultsWorkflow
{
    Task<JobResultsSummary> LoadSummaryAsync(Guid batchJobId, CancellationToken cancellationToken = default);
    Task<JobResultsPage> LoadPageAsync(
        Guid batchJobId,
        long? afterCursor,
        int pageSize,
        CancellationToken cancellationToken = default);
    Task<JobResultActionOutcome> RetryShareOnlyAsync(
        JobResultRow row,
        CancellationToken cancellationToken = default);
    Task<JobResultActionOutcome> ReconcileTransferAsync(
        Guid transferOperationId,
        CancellationToken cancellationToken = default);
    Task<JobResultActionOutcome> ReconcileShareAsync(
        Guid shareOperationId,
        CancellationToken cancellationToken = default);
    Task<JobResultActionOutcome> ResumeTransferAsync(
        JobResultRow row,
        CancellationToken cancellationToken = default);
    Task<JobResultActionOutcome> ResumeShareAsync(
        JobResultRow row,
        CancellationToken cancellationToken = default);
    Task<long> ExportAsync(
        Guid batchJobId,
        string destinationDirectory,
        IProgress<long> progress,
        CancellationToken cancellationToken = default);
}

public interface IJobResultCapabilityAvailability
{
    bool CanUseTransferActions { get; }
    bool CanUseShareActions { get; }
    string TransferStatusMessage { get; }
    string ShareStatusMessage { get; }
}

public sealed class UnavailableJobResultCapabilityAvailability : IJobResultCapabilityAvailability
{
    public bool CanUseTransferActions => false;
    public bool CanUseShareActions => false;
    public string TransferStatusMessage => "百度转存功能尚未通过现场验证，转存操作暂不可用。";
    public string ShareStatusMessage => "百度分享功能尚未通过现场验证，分享操作暂不可用。";
}

public sealed class DirectBackgroundJobResultCapabilityAvailability : IJobResultCapabilityAvailability
{
    public bool CanUseTransferActions => true;
    public bool CanUseShareActions => true;
    public string TransferStatusMessage => "后台转存已就绪。";
    public string ShareStatusMessage => "自动分享已就绪。";
}
