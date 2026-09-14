namespace BaiduBatchTransfer.Core.Abstractions;

public enum LocalImportDraftCleanupDisposition
{
    Deleted,
    AlreadyAbsent,
    BatchConflict,
    LocalDependencyConflict,
}

public sealed record LocalImportDraftCleanupResult
{
    public LocalImportDraftCleanupResult(
        LocalImportDraftCleanupDisposition disposition,
        int deletedStagedRowCount)
    {
        if (!Enum.IsDefined(disposition))
            throw new ArgumentOutOfRangeException(nameof(disposition));
        if (deletedStagedRowCount < 0 ||
            (disposition != LocalImportDraftCleanupDisposition.Deleted && deletedStagedRowCount != 0))
            throw new ArgumentOutOfRangeException(nameof(deletedStagedRowCount));

        Disposition = disposition;
        DeletedStagedRowCount = deletedStagedRowCount;
    }

    public LocalImportDraftCleanupDisposition Disposition { get; }
    public int DeletedStagedRowCount { get; }

    public override string ToString() =>
        $"LocalImportDraftCleanupResult {{ Disposition = {Disposition}, DeletedStagedRowCount = {DeletedStagedRowCount} }}";
}

public interface ILocalImportDraftCleanupStore
{
    Task<LocalImportDraftCleanupResult> DeleteAsync(
        Guid importSessionId,
        CancellationToken cancellationToken = default);
}
