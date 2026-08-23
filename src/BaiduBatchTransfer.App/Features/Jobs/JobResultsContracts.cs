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
    int TransferAttemptCount = 0);

public sealed record JobResultsPage(
    IReadOnlyList<JobResultRow> Items,
    long TotalCount,
    long? NextCursor);

public sealed record JobResultsSummary(
    long ResourceCount,
    long OperationCount,
    long FailedCount,
    long PausedCount);

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
    public string TransferStatusMessage => "百度转存适配器尚未通过现场验证，转存操作暂不可用。";
    public string ShareStatusMessage => "百度分享适配器尚未通过现场验证，分享操作暂不可用。";
}
