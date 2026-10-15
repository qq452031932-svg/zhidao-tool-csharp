namespace BaiduBatchTransfer.Application.Jobs;

/// <summary>
/// 结束一个还没跑完的转存批次。导入错了、或者不想再跑了，用户需要一个能让批次停下并归档的出口，
/// 否则未结束的批次既停不下来，也清不掉历史。
/// </summary>
public sealed class EndBatchService(IBatchRunControlStore store, TimeProvider? timeProvider = null)
{
    private readonly IBatchRunControlStore store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

    public Task<EndBatchResult> EndAsync(Guid batchJobId, CancellationToken cancellationToken = default)
    {
        if (batchJobId == Guid.Empty) throw new ArgumentException("A batch ID is required.", nameof(batchJobId));
        return store.TryEndBatchAsync(batchJobId, timeProvider.GetUtcNow(), cancellationToken);
    }

    public async Task<int> CountUnfinishedAsync(CancellationToken cancellationToken = default) =>
        (await store.ListUnfinishedBatchIdsAsync(cancellationToken).ConfigureAwait(false)).Count;

    /// <summary>没有暂停、还在运行的批次数：现在关软件，下次启动它们就按停止处理。</summary>
    public async Task<int> CountLeftRunningAsync(CancellationToken cancellationToken = default) =>
        (await store.ListRunningBatchIdsAsync(cancellationToken).ConfigureAwait(false)).Count;

    /// <summary>
    /// 清空历史前把所有未结束的批次一并结束。正在提交的不打断：那些批次先停下，
    /// 记在 <see cref="EndAllResult.StillRunning"/> 里，收尾后再清空一次即可。
    /// </summary>
    public async Task<EndAllResult> EndAllUnfinishedAsync(CancellationToken cancellationToken = default) =>
        await EndAllAsync(await store.ListUnfinishedBatchIdsAsync(cancellationToken).ConfigureAwait(false), cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// 软件启动时调用：上次关闭时没有暂停的批次就是不要了（与采集一致：只有暂停的才能继续），
    /// 按停止处理，不让它在新打开的软件里自己接着跑。用户暂停的批次原样保留。
    /// </summary>
    public async Task<EndAllResult> EndAllLeftRunningAsync(CancellationToken cancellationToken = default) =>
        await EndAllAsync(await store.ListRunningBatchIdsAsync(cancellationToken).ConfigureAwait(false), cancellationToken)
            .ConfigureAwait(false);

    private async Task<EndAllResult> EndAllAsync(IReadOnlyList<Guid> batchJobIds, CancellationToken cancellationToken)
    {
        var ended = 0;
        var stillRunning = 0;
        var cancelled = 0;
        foreach (var batchJobId in batchJobIds)
        {
            var result = await store.TryEndBatchAsync(batchJobId, timeProvider.GetUtcNow(), cancellationToken)
                .ConfigureAwait(false);
            cancelled += result.CancelledCount;
            if (result.Disposition == EndBatchDisposition.Ended) ended++;
            else if (result.Disposition == EndBatchDisposition.Stopping) stillRunning++;
        }
        return new(ended, stillRunning, cancelled);
    }
}

/// <param name="Ended">这次结束的批次数。</param>
/// <param name="StillRunning">已停下、但还有操作在提交，暂时不能结束的批次数。</param>
/// <param name="CancelledOperations">取消的、从未开始的转存操作数。</param>
public sealed record EndAllResult(int Ended, int StillRunning, int CancelledOperations);
