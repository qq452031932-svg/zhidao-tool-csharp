namespace BaiduBatchTransfer.Core.Abstractions;

public enum LocalHistoryKind
{
    Unspecified = 0,
    Transfer = 1,
    StandaloneShare = 2,
}

public enum LocalHistoryCleanupDisposition
{
    Unspecified = 0,
    Deleted = 1,
    NothingToDelete = 2,
    Conflict = 3,
}

public sealed record LocalHistoryCleanupPreview
{
    public const int MaximumSnapshotTokenLength = 256;

    public LocalHistoryCleanupPreview(
        LocalHistoryKind Kind,
        int TerminalBatchCount,
        bool HasConflicts,
        string? SnapshotToken,
        int RetryableFailureCount = 0)
    {
        if (!Enum.IsDefined(Kind) || Kind == LocalHistoryKind.Unspecified)
            throw new ArgumentOutOfRangeException(nameof(Kind));
        if (TerminalBatchCount < 0)
            throw new ArgumentOutOfRangeException(nameof(TerminalBatchCount));
        if (RetryableFailureCount < 0)
            throw new ArgumentOutOfRangeException(nameof(RetryableFailureCount));
        if (SnapshotToken is not null &&
            (string.IsNullOrWhiteSpace(SnapshotToken) || SnapshotToken.Length > MaximumSnapshotTokenLength))
            throw new ArgumentException("A bounded opaque snapshot token is required.", nameof(SnapshotToken));
        if (TerminalBatchCount > 0 && SnapshotToken is null)
            throw new ArgumentException("A snapshot token is required when terminal batches exist.", nameof(SnapshotToken));

        this.Kind = Kind;
        this.TerminalBatchCount = TerminalBatchCount;
        this.HasConflicts = HasConflicts;
        this.SnapshotToken = SnapshotToken;
        this.RetryableFailureCount = RetryableFailureCount;
    }

    public LocalHistoryKind Kind { get; }
    public int TerminalBatchCount { get; }
    public bool HasConflicts { get; }
    public string? SnapshotToken { get; }

    // 仍可重试的失败项数量。它不再算作冲突把清理硬挡住——删除的只是本地记录，
    // 不碰网盘文件；用户明确要清空，就该让他清。但必须在确认框里说清楚代价：
    // 清空之后这些失败项无法再重试。
    // 之前把它当硬冲突，结果是：软件崩溃一次留下几条"准备提交"状态的失败项，
    // 用户既无法重试（状态推进不了）也无法清空，历史就永远卡在那里。
    public int RetryableFailureCount { get; }
}

public sealed record LocalHistoryCleanupResult
{
    public LocalHistoryCleanupResult(
        int DeletedBatchCount,
        LocalHistoryCleanupDisposition Disposition)
    {
        if (DeletedBatchCount < 0)
            throw new ArgumentOutOfRangeException(nameof(DeletedBatchCount));
        if (!Enum.IsDefined(Disposition) || Disposition == LocalHistoryCleanupDisposition.Unspecified)
            throw new ArgumentOutOfRangeException(nameof(Disposition));
        if (Disposition == LocalHistoryCleanupDisposition.Deleted && DeletedBatchCount == 0)
            throw new ArgumentOutOfRangeException(nameof(DeletedBatchCount));
        if (Disposition != LocalHistoryCleanupDisposition.Deleted && DeletedBatchCount != 0)
            throw new ArgumentOutOfRangeException(nameof(DeletedBatchCount));

        this.DeletedBatchCount = DeletedBatchCount;
        this.Disposition = Disposition;
    }

    public int DeletedBatchCount { get; }
    public LocalHistoryCleanupDisposition Disposition { get; }
}

public interface ILocalTerminalHistoryStore
{
    Task<LocalHistoryCleanupPreview> PreviewAsync(
        LocalHistoryKind kind,
        CancellationToken cancellationToken = default);

    Task<LocalHistoryCleanupResult> DeleteAsync(
        LocalHistoryCleanupPreview confirmedPreview,
        CancellationToken cancellationToken = default);
}
