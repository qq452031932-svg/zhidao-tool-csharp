using BaiduBatchTransfer.Application.Jobs;

namespace BaiduBatchTransfer.Application.Tests.Jobs;

public sealed class StuckBatchWatchdogTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);
    private static readonly StuckBatchWatchdogOptions Options =
        new(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(15));

    [Fact]
    public async Task ARunningBatchIsAbandonedOnlyAfterStayingBlockedForThirtyMinutes()
    {
        var batchId = Guid.NewGuid();
        var store = new FakeStore { Snapshot = Blocked(batchId, unfinished: 2, stopped: false, queuedBehind: 3) };
        var clock = new MutableTime(Start);
        var notifier = new RecordingNotifier();
        var watchdog = new StuckBatchWatchdog(store, clock, Options, notifier);

        Assert.Null(await watchdog.CheckAsync());
        clock.Now = Start.AddMinutes(29);
        Assert.Null(await watchdog.CheckAsync());
        Assert.Empty(store.Abandoned);

        clock.Now = Start.AddMinutes(30);
        var result = await watchdog.CheckAsync();

        Assert.NotNull(result);
        Assert.Equal([(batchId, true)], store.Abandoned);
        var message = Assert.Single(notifier.Messages);
        Assert.Contains("「小号A」", message);
        Assert.Contains("30 分钟", message);
        Assert.Contains("请到网盘核对", message);
        Assert.Contains("后面排队的 3 个批次", message);
    }

    [Fact]
    public async Task AStoppedBatchUsesTheShorterLimit()
    {
        var batchId = Guid.NewGuid();
        var store = new FakeStore { Snapshot = Blocked(batchId, unfinished: 1, stopped: true) };
        var clock = new MutableTime(Start);
        var watchdog = new StuckBatchWatchdog(store, clock, Options);

        await watchdog.CheckAsync();
        clock.Now = Start.AddMinutes(10);

        Assert.NotNull(await watchdog.CheckAsync());
        Assert.Single(store.Abandoned);
    }

    [Fact]
    public async Task ProgressOrAnUnblockedCheckRestartsTheClock()
    {
        var batchId = Guid.NewGuid();
        var store = new FakeStore { Snapshot = Blocked(batchId, unfinished: 3, stopped: true) };
        var clock = new MutableTime(Start);
        var watchdog = new StuckBatchWatchdog(store, clock, Options);
        await watchdog.CheckAsync();

        // 有一条收尾了：还在动，重新计时。
        clock.Now = Start.AddMinutes(9);
        store.Snapshot = Blocked(batchId, unfinished: 2, stopped: true);
        Assert.Null(await watchdog.CheckAsync());
        clock.Now = Start.AddMinutes(12);
        Assert.Null(await watchdog.CheckAsync());

        // 账号重新登录了：不算卡住，清掉计时。
        clock.Now = Start.AddMinutes(18);
        store.Snapshot = store.Snapshot with { StuckCount = 1 };
        Assert.Null(await watchdog.CheckAsync());
        clock.Now = Start.AddMinutes(19);
        store.Snapshot = Blocked(batchId, unfinished: 2, stopped: true);
        Assert.Null(await watchdog.CheckAsync());
        clock.Now = Start.AddMinutes(28);
        Assert.Null(await watchdog.CheckAsync());
        Assert.Empty(store.Abandoned);

        clock.Now = Start.AddMinutes(29);
        Assert.NotNull(await watchdog.CheckAsync());
    }

    [Fact]
    public async Task ChecksAreThrottledAndAnActiveSubmissionIsNeverTreatedAsStuck()
    {
        var batchId = Guid.NewGuid();
        var store = new FakeStore { Snapshot = Blocked(batchId, unfinished: 2, stopped: true) with { RunningCount = 1 } };
        var clock = new MutableTime(Start);
        var watchdog = new StuckBatchWatchdog(store, clock, Options);

        await watchdog.CheckAsync();
        await watchdog.CheckAsync();
        Assert.Equal(1, store.SnapshotReads);

        clock.Now = Start.AddHours(2);
        Assert.Null(await watchdog.CheckAsync());
        clock.Now = Start.AddHours(3);
        Assert.Null(await watchdog.CheckAsync());
        Assert.Empty(store.Abandoned);
    }

    [Fact]
    public async Task NothingIsReportedWhenTheStoreFindsTheBatchNoLongerBlocked()
    {
        var batchId = Guid.NewGuid();
        var store = new FakeStore
        {
            Snapshot = Blocked(batchId, unfinished: 1, stopped: true),
            AbandonResult = new(AbandonBatchDisposition.NotBlocked, 0, 0, 0),
        };
        var clock = new MutableTime(Start);
        var notifier = new RecordingNotifier();
        var watchdog = new StuckBatchWatchdog(store, clock, Options, notifier);
        await watchdog.CheckAsync();
        clock.Now = Start.AddMinutes(11);

        Assert.Null(await watchdog.CheckAsync());
        Assert.Empty(notifier.Messages);
    }

    [Fact]
    public void TheBlockedHintSaysWhatItIsWaitingForAndHowToMoveOn()
    {
        var hint = StuckBatchText.BlockedHint(Blocked(Guid.NewGuid(), 2, stopped: true, queuedBehind: 4), TimeSpan.FromMinutes(10));

        Assert.Contains("还剩 2 条", hint);
        Assert.Contains("「小号A」", hint);
        Assert.Contains("后面 4 个批次在排队", hint);
        Assert.Contains("重新登录", hint);
        Assert.Contains("放弃收尾", hint);
        Assert.Contains("10 分钟", hint);
    }

    private static StuckBatchSnapshot Blocked(Guid batchId, int unfinished, bool stopped, int queuedBehind = 0) =>
        new(batchId, unfinished, unfinished, 0, stopped, ["小号A"], queuedBehind);

    private sealed class FakeStore : IBatchRunControlStore
    {
        public StuckBatchSnapshot? Snapshot { get; set; }
        public AbandonBatchResult AbandonResult { get; init; } = new(AbandonBatchDisposition.Ended, 0, 1, 0);
        public int SnapshotReads { get; private set; }
        public List<(Guid BatchJobId, bool OnlyIfBlocked)> Abandoned { get; } = [];

        public Task<StuckBatchSnapshot?> GetActiveStuckSnapshotAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            SnapshotReads++;
            return Task.FromResult(Snapshot);
        }

        public Task<AbandonBatchResult> TryAbandonUnfinishedAsync(
            Guid batchJobId, DateTimeOffset now, bool onlyIfBlocked, CancellationToken cancellationToken = default)
        {
            Abandoned.Add((batchJobId, onlyIfBlocked));
            return Task.FromResult(AbandonResult);
        }

        public Task<PauseBatchDisposition> TryPauseBatchAsync(Guid batchJobId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ResumeBatchDisposition> TryResumeBatchAsync(Guid batchJobId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<RetryFailedTransferDisposition> TryRetryFailedTransferAsync(
            Guid operationId, DateTimeOffset retriedAt, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingNotifier : IStuckBatchNotifier
    {
        public List<string> Messages { get; } = [];
        public void Notify(string message) => Messages.Add(message);
    }

    private sealed class MutableTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
