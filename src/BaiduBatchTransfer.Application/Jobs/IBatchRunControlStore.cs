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

/// <summary>
/// 一个没结束的批次还剩哪些活、是不是卡住了。同一时间只跑一个批次，前一批卡住后面的全都排队不动
/// （用户 10-15 晚运行日志：一批剩 2 条等账号登录后核对，之后新建的 4 个批次一直停在 0）。
/// </summary>
/// <param name="UnfinishedCount">还没结束的转存与分享（等转存完成的分享跟着转存走，不单算）。</param>
/// <param name="StuckCount">其中自己走不下去的：账号已失效/停用，或结果待核对、因账号问题停住。</param>
/// <param name="RunningCount">此刻正在提交、不能打断的。</param>
/// <param name="WasStopped">用户点过「停止」（或启动时按停止处理）。</param>
/// <param name="UnavailableAccountNames">卡住的记录所属的、已失效或停用的账号名称（最多几个）。</param>
/// <param name="QueuedBehindCount">排在后面等它的批次数。</param>
public sealed record StuckBatchSnapshot(
    Guid BatchJobId,
    int UnfinishedCount,
    int StuckCount,
    int RunningCount,
    bool WasStopped,
    IReadOnlyList<string> UnavailableAccountNames,
    int QueuedBehindCount)
{
    /// <summary>剩下的全都走不下去、也没有正在提交的：只能等用户处理账号，或者放弃。</summary>
    public bool IsBlocked => UnfinishedCount > 0 && RunningCount == 0 && StuckCount == UnfinishedCount;
}

public enum AbandonBatchDisposition
{
    /// <summary>批次已结束（Cancelled），后面排队的批次可以开始。</summary>
    Ended,
    /// <summary>能放弃的都放弃了，但还有正在提交的，它们完成后批次才结束。</summary>
    Stopping,
    /// <summary>只在卡住时才放弃（自动处理），而批次此刻并没有卡住，什么也没改。</summary>
    NotBlocked,
    AlreadyEnded,
    NotFound,
}

/// <param name="CancelledCount">从未提交、直接取消的转存数。</param>
/// <param name="AbandonedCount">已提交过或账号不可用、按「结果未确认」记为失败的转存与分享数。</param>
/// <param name="StillRunningCount">正在提交、没有动的。</param>
public sealed record AbandonBatchResult(
    AbandonBatchDisposition Disposition,
    int CancelledCount,
    int AbandonedCount,
    int StillRunningCount);

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

    /// <summary>某个没结束的批次的情况；已结束或不存在时为 null。</summary>
    Task<StuckBatchSnapshot?> GetStuckSnapshotAsync(
        Guid batchJobId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This store cannot inspect batches.");

    /// <summary>正在运行（Active）的那个批次的情况；没有时为 null。</summary>
    Task<StuckBatchSnapshot?> GetActiveStuckSnapshotAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This store cannot inspect batches.");

    /// <summary>
    /// 放弃收尾：先像「停止」一样取消没开始的，再把剩下不在提交中的转存与分享按「结果未确认」记为失败，
    /// 不再核对；没有正在提交的就直接结束批次。<paramref name="onlyIfBlocked"/> 为 true 时
    /// （自动处理），在同一个事务里确认批次确实卡住才动手。
    /// </summary>
    Task<AbandonBatchResult> TryAbandonUnfinishedAsync(
        Guid batchJobId,
        DateTimeOffset now,
        bool onlyIfBlocked,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This store cannot abandon batches.");

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
