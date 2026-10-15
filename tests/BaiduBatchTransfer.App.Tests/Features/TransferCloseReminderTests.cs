using BaiduBatchTransfer.App.Features.Transfers;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Application.Jobs;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class TransferCloseReminderTests
{
    [Fact]
    public async Task ClosingWithARunningBatchAsksFirstAndLogsTheAnswer()
    {
        // 用户 10-14：没暂停就关软件会按停止处理，关之前要先提醒。
        var asked = new List<string>();
        var log = new RecordingRunLog();

        var closed = await TransferCloseReminder.ConfirmAsync(
            new EndBatchService(new RunningStore(2)), message => { asked.Add(message); return false; }, log);

        Assert.False(closed);
        var message = Assert.Single(asked);
        Assert.Contains("2 个转存批次正在进行", message, StringComparison.Ordinal);
        Assert.Contains("暂停", message, StringComparison.Ordinal);
        Assert.Contains("用户选择不关闭", Assert.Single(log.Lines), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmingClosesAndRecordsThatTheBatchesWillBeStopped()
    {
        var log = new RecordingRunLog();

        var closed = await TransferCloseReminder.ConfirmAsync(
            new EndBatchService(new RunningStore(1)), _ => true, log);

        Assert.True(closed);
        Assert.Contains("下次启动按停止处理", Assert.Single(log.Lines), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NothingRunningClosesWithoutAsking()
    {
        var closed = await TransferCloseReminder.ConfirmAsync(
            new EndBatchService(new RunningStore(0)), _ => throw new InvalidOperationException("不应该提醒"));

        Assert.True(closed);
    }

    [Fact]
    public async Task AnUnreadableStateNeverKeepsTheUserFromClosing()
    {
        Assert.True(await TransferCloseReminder.ConfirmAsync(
            new EndBatchService(new RunningStore(-1)), _ => throw new InvalidOperationException("不应该提醒")));
        Assert.True(await TransferCloseReminder.ConfirmAsync(
            null, _ => throw new InvalidOperationException("不应该提醒")));
    }

    [Fact]
    public void StartupNoticesPostedBeforeThePageExistsAreHandedOverAndLaterOnesForwarded()
    {
        var notices = new StartupNotices();
        notices.Post("先到的");
        var forwarded = new List<string>();

        var earlier = notices.Attach(forwarded.Add);
        notices.Post("后到的");

        Assert.Equal(["先到的"], earlier);
        Assert.Equal(["后到的"], forwarded);
        Assert.Empty(notices.Attach(_ => { }));
    }

    private sealed class RunningStore(int running) : IBatchRunControlStore
    {
        public Task<IReadOnlyList<Guid>> ListRunningBatchIdsAsync(CancellationToken cancellationToken = default) =>
            running < 0
                ? throw new InvalidOperationException("database unavailable")
                : Task.FromResult<IReadOnlyList<Guid>>(Enumerable.Range(0, running).Select(_ => Guid.NewGuid()).ToArray());
        public Task<PauseBatchDisposition> TryPauseBatchAsync(Guid batchJobId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ResumeBatchDisposition> TryResumeBatchAsync(Guid batchJobId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RetryFailedTransferDisposition> TryRetryFailedTransferAsync(Guid operationId, DateTimeOffset retriedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class RecordingRunLog : IRunLog
    {
        public List<string> Lines { get; } = [];
        public void Write(string source, string? message) => Lines.Add($"[{source}] {message}");
    }
}
