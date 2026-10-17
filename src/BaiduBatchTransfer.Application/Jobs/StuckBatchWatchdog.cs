namespace BaiduBatchTransfer.Application.Jobs;

/// <param name="RunningBatchLimit">批次还在正常运行时，卡住多久后自动放弃收尾。</param>
/// <param name="StoppedBatchLimit">用户已点过「停止」的批次，卡住多久后自动放弃收尾。</param>
/// <param name="CheckInterval">多久查一次。</param>
public sealed record StuckBatchWatchdogOptions(
    TimeSpan RunningBatchLimit,
    TimeSpan StoppedBatchLimit,
    TimeSpan CheckInterval)
{
    public static StuckBatchWatchdogOptions Default { get; } =
        new(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(15));

    public void Validate()
    {
        if (RunningBatchLimit <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(RunningBatchLimit));
        if (StoppedBatchLimit <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(StoppedBatchLimit));
        if (CheckInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(CheckInterval));
    }
}

/// <summary>自动放弃收尾之后要告诉用户的事（写运行日志、显示在转存页）。</summary>
public interface IStuckBatchNotifier
{
    void Notify(string message);
}

/// <summary>
/// 盯着正在运行的批次：剩下的活全都走不下去（账号已失效、结果核对不了），而且一直没有进展，
/// 超过上限就按「结果未确认」放弃收尾、结束这一批，让后面排队的批次开始。
/// 只记在内存里：软件重开后重新计时。
/// </summary>
public sealed class StuckBatchWatchdog
{
    private readonly IBatchRunControlStore store;
    private readonly TimeProvider timeProvider;
    private readonly StuckBatchWatchdogOptions options;
    private readonly IStuckBatchNotifier? notifier;
    private DateTimeOffset nextCheckAt = DateTimeOffset.MinValue;
    private (Guid BatchJobId, int UnfinishedCount, DateTimeOffset Since)? blocked;

    public StuckBatchWatchdog(
        IBatchRunControlStore store,
        TimeProvider? timeProvider = null,
        StuckBatchWatchdogOptions? options = null,
        IStuckBatchNotifier? notifier = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.options = options ?? StuckBatchWatchdogOptions.Default;
        this.options.Validate();
        this.notifier = notifier;
    }

    /// <summary>调度器空闲时调用；到了检查间隔才真正去查。放弃收尾了就返回结果。</summary>
    public async Task<AbandonBatchResult?> CheckAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        if (now < nextCheckAt) return null;
        nextCheckAt = now.Add(options.CheckInterval);

        var snapshot = await store.GetActiveStuckSnapshotAsync(now, cancellationToken).ConfigureAwait(false);
        if (snapshot is null || !snapshot.IsBlocked)
        {
            blocked = null;
            return null;
        }
        // 换了批次、或者剩下的条数变了（有一条收尾了），说明还在动，从头计时。
        if (blocked is not { } current || current.BatchJobId != snapshot.BatchJobId ||
            current.UnfinishedCount != snapshot.UnfinishedCount)
        {
            blocked = (snapshot.BatchJobId, snapshot.UnfinishedCount, now);
            return null;
        }
        var limit = snapshot.WasStopped ? options.StoppedBatchLimit : options.RunningBatchLimit;
        if (now - current.Since < limit) return null;

        blocked = null;
        var result = await store.TryAbandonUnfinishedAsync(snapshot.BatchJobId, now, onlyIfBlocked: true, cancellationToken)
            .ConfigureAwait(false);
        if (result.Disposition is not (AbandonBatchDisposition.Ended or AbandonBatchDisposition.Stopping)) return null;
        notifier?.Notify(DescribeAutomaticAbandon(snapshot, result, limit));
        return result;
    }

    public static string DescribeAutomaticAbandon(StuckBatchSnapshot snapshot, AbandonBatchResult result, TimeSpan limit)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(result);
        var reason = snapshot.UnavailableAccountNames.Count > 0
            ? $"账号{StuckBatchText.AccountList(snapshot.UnavailableAccountNames)}登录失效或不可用"
            : "结果一直核对不了";
        var text = $"有一批转存{reason}，已卡住超过 {limit.TotalMinutes:0} 分钟，已自动放弃收尾：" +
            StuckBatchText.Outcome(result) + "。";
        if (snapshot.QueuedBehindCount > 0) text += $"后面排队的 {snapshot.QueuedBehindCount:N0} 个批次会接着开始。";
        return text;
    }
}

/// <summary>卡住提示与放弃结果的统一说法，转存页与自动处理共用。</summary>
public static class StuckBatchText
{
    public static string AccountList(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return names.Count == 0 ? string.Empty : string.Join("、", names.Select(name => $"「{name}」"));
    }

    public static string Outcome(AbandonBatchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var parts = new List<string>();
        if (result.CancelledCount > 0) parts.Add($"未开始的 {result.CancelledCount:N0} 条已取消");
        if (result.AbandonedCount > 0)
            parts.Add($"{result.AbandonedCount:N0} 条按「结果未确认」记为失败（可能已经转存或分享成功，请到网盘核对）");
        if (result.StillRunningCount > 0) parts.Add($"还有 {result.StillRunningCount:N0} 条正在提交，完成后这一批自动结束");
        return parts.Count == 0 ? "没有需要处理的记录" : string.Join("；", parts);
    }

    /// <summary>批次卡住时给用户的说明：在等什么、怎么办。</summary>
    public static string BlockedHint(StuckBatchSnapshot snapshot, TimeSpan automaticLimit)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var waitingFor = snapshot.UnavailableAccountNames.Count > 0
            ? $"在等账号{AccountList(snapshot.UnavailableAccountNames)}重新登录"
            : "结果一直核对不了";
        var text = $"这一批还剩 {snapshot.UnfinishedCount:N0} 条走不下去（{waitingFor}）";
        text += snapshot.QueuedBehindCount > 0
            ? $"，后面 {snapshot.QueuedBehindCount:N0} 个批次在排队等它。"
            : "。";
        text += snapshot.UnavailableAccountNames.Count > 0
            ? "可以在「账号管理」里点「重新登录」，登录后结果能核对清楚；"
            : "可以稍后再看；";
        text += $"不想等就点「放弃收尾」。一直没有进展的，约 {automaticLimit.TotalMinutes:0} 分钟后会自动放弃收尾。";
        return text;
    }
}
