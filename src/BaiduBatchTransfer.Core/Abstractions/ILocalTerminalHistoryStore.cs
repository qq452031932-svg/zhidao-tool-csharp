namespace BaiduBatchTransfer.Core.Abstractions;

public enum LocalHistoryKind
{
    Transfer,
    StandaloneShare,
}

public enum LocalHistoryCleanupDisposition
{
    Deleted,
    NothingToDelete,
    Conflict,
}

public sealed record LocalHistoryCleanupPreview(
    int TerminalBatchCount,
    bool HasConflicts);

public sealed record LocalHistoryCleanupResult(
    int DeletedBatchCount,
    LocalHistoryCleanupDisposition Disposition);

public interface ILocalTerminalHistoryStore
{
    Task<LocalHistoryCleanupPreview> PreviewAsync(
        LocalHistoryKind kind,
        CancellationToken cancellationToken = default);

    Task<LocalHistoryCleanupResult> DeleteAsync(
        LocalHistoryKind kind,
        CancellationToken cancellationToken = default);
}
