namespace BaiduBatchTransfer.Core.Abstractions;

public interface IImportRepository
{
    Task AddSessionAsync(ImportSessionRecord session, CancellationToken cancellationToken = default);
    Task<ImportSessionRecord?> GetSessionAsync(Guid id, CancellationToken cancellationToken = default);
    Task AddRowsAsync(IEnumerable<StagedImportRowRecord> rows, CancellationToken cancellationToken = default);
    Task<Page<StagedImportRowRecord>> GetRowsPageAsync(StagedImportRowQuery query, CancellationToken cancellationToken = default);
}
