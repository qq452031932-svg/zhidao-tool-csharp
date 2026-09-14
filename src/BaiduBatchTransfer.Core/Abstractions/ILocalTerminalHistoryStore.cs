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
        int TerminalBatchCount,
        bool HasConflicts,
        string? SnapshotToken)
    {
        if (TerminalBatchCount < 0)
            throw new ArgumentOutOfRangeException(nameof(TerminalBatchCount));
        if (SnapshotToken is not null &&
            (string.IsNullOrWhiteSpace(SnapshotToken) || SnapshotToken.Length > MaximumSnapshotTokenLength))
            throw new ArgumentException("A bounded opaque snapshot token is required.", nameof(SnapshotToken));
        if (TerminalBatchCount > 0 && SnapshotToken is null)
            throw new ArgumentException("A snapshot token is required when terminal batches exist.", nameof(SnapshotToken));

        this.TerminalBatchCount = TerminalBatchCount;
        this.HasConflicts = HasConflicts;
        this.SnapshotToken = SnapshotToken;
    }

    public int TerminalBatchCount { get; }
    public bool HasConflicts { get; }
    public string? SnapshotToken { get; }
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
        LocalHistoryKind kind,
        string snapshotToken,
        CancellationToken cancellationToken = default);
}
