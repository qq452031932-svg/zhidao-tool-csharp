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

public enum EndBatchDisposition
{
    /// <summary>批次已结束（Cancelled），可以清空。</summary>
    Ended,
    /// <summary>未开始的已取消、批次已暂停，但还有正在提交或等待确认的操作；它们收尾后再结束一次即可。</summary>
    Stopping,
    AlreadyEnded,
    NotFound,
}

/// <param name="CancelledCount">这一次取消的、从未提交过的转存操作数。</param>
/// <param name="InFlightCount">仍在进行、不能中途打断的操作数（转存与分享合计）。</param>
public sealed record EndBatchResult(EndBatchDisposition Disposition, int CancelledCount, int InFlightCount);

public interface IBatchRunControlStore
{
    /// <summary>
    /// 结束整批：停止领取新操作，把可证明从未提交过的操作标为已取消；正在提交的不打断，
    /// 等它们收尾后批次才真正结束。
    /// </summary>
    Task<EndBatchResult> TryEndBatchAsync(
        Guid batchJobId,
        DateTimeOffset endedAt,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This store cannot end batches.");

    /// <summary>还没结束的转存批次（排队、进行中、已暂停等），按排队顺序。</summary>
    Task<IReadOnlyList<Guid>> ListUnfinishedBatchIdsAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This store cannot list unfinished batches.");

    /// <summary>还没结束、也不是用户暂停的转存批次（排队、进行中等）。</summary>
    Task<IReadOnlyList<Guid>> ListRunningBatchIdsAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This store cannot list running batches.");

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
