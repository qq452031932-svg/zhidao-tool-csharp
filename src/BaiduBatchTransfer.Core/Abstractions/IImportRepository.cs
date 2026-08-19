namespace BaiduBatchTransfer.Core.Abstractions;

public interface IImportRepository
{
    Task AddSessionAsync(ImportSessionRecord session, CancellationToken cancellationToken = default);
    Task UpdateSessionAsync(ImportSessionRecord session, CancellationToken cancellationToken = default);
    Task<ImportSessionRecord?> GetSessionAsync(Guid id, CancellationToken cancellationToken = default);
    Task AddRowsAsync(IEnumerable<StagedImportRowRecord> rows, CancellationToken cancellationToken = default);
    Task UpdateRowsAsync(IEnumerable<StagedImportRowRecord> rows, CancellationToken cancellationToken = default);
    Task<StagedImportRowRecord?> GetRowByIdAsync(Guid rowId, CancellationToken cancellationToken = default);
    Task<StagedImportRowRecord?> GetFirstByNormalizedShareIdAsync(
        Guid sessionId,
        string normalizedShareId,
        CancellationToken cancellationToken = default);
    Task<int> GetLastSourceRowNumberAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StagedImportRowRecord>> GetRowsAfterSourceRowAsync(
        Guid sessionId,
        string disposition,
        int afterSourceRowNumber,
        int pageSize,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StagedImportRowRecord>> GetRowsByNormalizedShareIdAfterSourceRowAsync(
        Guid sessionId,
        string normalizedShareId,
        int afterSourceRowNumber,
        int pageSize,
        CancellationToken cancellationToken = default);
    Task<Page<StagedImportRowRecord>> GetRowsPageAsync(StagedImportRowQuery query, CancellationToken cancellationToken = default);
}
