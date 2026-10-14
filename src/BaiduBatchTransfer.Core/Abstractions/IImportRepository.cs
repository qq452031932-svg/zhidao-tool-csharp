namespace BaiduBatchTransfer.Core.Abstractions;

public interface IImportRepository
{
    Task AddSessionAsync(ImportSessionRecord session, CancellationToken cancellationToken = default);
    Task UpdateSessionAsync(ImportSessionRecord session, CancellationToken cancellationToken = default);
    Task<bool> TryFinalizeBatchCreationAsync(
        ImportSessionRecord expectedSession,
        Guid batchJobId,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Atomic import-session batch finalization is not supported by this repository.");
    Task<ImportSessionRecord?> GetSessionAsync(Guid id, CancellationToken cancellationToken = default);
    Task<ImportSessionRecord?> GetLatestResumableExcelSessionAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<ImportSessionRecord?>(null);
    Task AddRowsAsync(IEnumerable<StagedImportRowRecord> rows, CancellationToken cancellationToken = default);
    Task UpdateRowsAsync(IEnumerable<StagedImportRowRecord> rows, CancellationToken cancellationToken = default);
    /// <summary>
    /// 从草稿中彻底移除一行及其附带内容；指向它的重复标记一并解除，由调用方随后重新归类。
    /// 已被任务引用的行不能删除。
    /// </summary>
    Task DeleteRowAsync(Guid rowId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This import repository cannot delete staged rows.");
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
    async Task<RequestedAccountResolutionCounts> GetRequestedAccountResolutionCountsAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        var groups = await GetRequestedAccountNameGroupsPageAsync(
            sessionId,
            new PageQuery(1, 1),
            cancellationToken).ConfigureAwait(false);
        return new RequestedAccountResolutionCounts(groups.TotalCount, groups.TotalCount);
    }
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
    Task<IReadOnlyList<StagedImportRowPayloadRecord>> GetRowPayloadsAsync(
        IEnumerable<Guid> stagedImportRowIds,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Raw import payload paging is not supported by this repository.");
    Task UpdateRowPayloadAsync(
        StagedImportRowPayloadRecord payload,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Raw import payload updates are not supported by this repository.");
}
