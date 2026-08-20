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
    Task<KeysetPage<StagedImportRowRecord>> GetRowsKeysetPageAsync(
        StagedImportKeysetQuery query,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetDuplicateNormalizedShareIdsPageAsync(
        Guid sessionId,
        string? afterNormalizedShareId,
        int pageSize,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Duplicate identity paging is not supported by this repository.");
    Task<KeysetPage<StagedImportRowRecord>> GetNonDuplicateClassifiableRowsKeysetPageAsync(
        Guid sessionId,
        long afterSourceRowNumber,
        int pageSize,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Non-duplicate staging row paging is not supported by this repository.");
    Task<Page<RequestedAccountNameGroupRecord>> GetRequestedAccountNameGroupsPageAsync(
        Guid sessionId,
        PageQuery query,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Requested-account aggregation is not supported by this repository.");
    Task<ImportDispositionCounts> GetDispositionCountsAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Disposition aggregation is not supported by this repository.");
    Task SetColumnDefinitionsAsync(
        Guid sessionId,
        IEnumerable<ImportColumnDefinitionRecord> definitions,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Import column definitions are not supported by this repository.");
    Task<IReadOnlyList<ImportColumnDefinitionRecord>> GetColumnDefinitionsAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Import column definitions are not supported by this repository.");
    Task AddRowPayloadsAsync(
        IEnumerable<StagedImportRowPayloadRecord> payloads,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Raw import payloads are not supported by this repository.");
    Task<StagedImportRowPayloadRecord?> GetRowPayloadAsync(
        Guid stagedImportRowId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Raw import payloads are not supported by this repository.");
}
