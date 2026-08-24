namespace BaiduBatchTransfer.Application.Jobs;

public enum PauseBatchDisposition
{
    Paused,
    AlreadyPaused,
    NotFound,
    NotPausable,
}

public enum ResumeBatchDisposition
{
    Queued,
    AlreadyResumed,
    NotFound,
    NotResumable,
}

public enum RetryFailedTransferDisposition
{
    Requeued,
    NeedsReconciliation,
    NotRetryable,
    NotFound,
}

public interface IBatchRunControlStore
{
    Task<PauseBatchDisposition> TryPauseBatchAsync(
        Guid batchJobId,
        CancellationToken cancellationToken = default);

    Task<ResumeBatchDisposition> TryResumeBatchAsync(
        Guid batchJobId,
        CancellationToken cancellationToken = default);

    Task<RetryFailedTransferDisposition> TryRetryFailedTransferAsync(
        Guid operationId,
        DateTimeOffset retriedAt,
        CancellationToken cancellationToken = default);
}
