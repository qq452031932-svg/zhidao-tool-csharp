namespace BaiduBatchTransfer.Core.Abstractions;

public interface IJobRepository
{
    Task AddAsync(BatchJobRecord job, CancellationToken cancellationToken = default);
    Task UpdateAsync(BatchJobRecord job, CancellationToken cancellationToken = default);
    Task<BatchJobRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task AddResourcesAsync(IEnumerable<ResourceItemRecord> resources, CancellationToken cancellationToken = default);
    Task<Page<ResourceItemRecord>> GetResourcesPageAsync(Guid batchJobId, PageQuery query, CancellationToken cancellationToken = default);
    Task AddOperationsAsync(IEnumerable<TransferOperationRecord> operations, CancellationToken cancellationToken = default);
    Task<Page<TransferOperationRecord>> GetOperationsPageAsync(Guid batchJobId, PageQuery query, CancellationToken cancellationToken = default);
    Task PrepareSubmissionAsync(OperationSubmissionPreparation preparation, CancellationToken cancellationToken = default);
    Task RecordSubmissionResponseAsync(OperationSubmissionResponse response, CancellationToken cancellationToken = default);
    Task AddAllocationsAsync(IEnumerable<AccountAllocationRecord> allocations, CancellationToken cancellationToken = default);
    Task UpdateAllocationsAsync(IEnumerable<AccountAllocationRecord> allocations, CancellationToken cancellationToken = default);
    Task<Page<AccountAllocationRecord>> GetAllocationsPageAsync(Guid batchJobId, PageQuery query, CancellationToken cancellationToken = default);
    Task WriteAuditAsync(AuditEventRecord audit, CancellationToken cancellationToken = default);
    Task<ExecutionLogRecord?> GetExecutionLogAsync(Guid id, CancellationToken cancellationToken = default);
    Task<KeysetPage<ResourceItemRecord>> GetResourceResultsKeysetPageAsync(
        ResourceResultKeysetQuery query,
        CancellationToken cancellationToken = default);
    Task<KeysetPage<OperationResultRecord>> GetOperationResultsKeysetPageAsync(
        OperationResultKeysetQuery query,
        CancellationToken cancellationToken = default);
    Task<BatchStatisticsRecord> GetBatchStatisticsAsync(
        Guid batchJobId,
        CancellationToken cancellationToken = default);
    Task SetShareSettingsAsync(BatchShareSettingsRecord settings, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Batch share settings are not supported by this repository.");
    Task<BatchShareSettingsRecord?> GetShareSettingsAsync(Guid batchJobId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Batch share settings are not supported by this repository.");
    Task AddOutputAccountsAsync(IEnumerable<BatchOutputAccountRecord> accounts, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Batch output snapshots are not supported by this repository.");
    Task<IReadOnlyList<BatchOutputAccountRecord>> GetOutputAccountsAsync(Guid batchJobId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Batch output snapshots are not supported by this repository.");
    Task AddSourceColumnsAsync(IEnumerable<BatchSourceColumnRecord> columns, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Batch source-column snapshots are not supported by this repository.");
    Task<IReadOnlyList<BatchSourceColumnRecord>> GetSourceColumnsAsync(Guid batchJobId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Batch source-column snapshots are not supported by this repository.");
    Task AddCreationReceiptAsync(BatchCreationReceiptRecord receipt, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Batch creation receipts are not supported by this repository.");
    Task<BatchCreationReceiptRecord?> GetCreationReceiptAsync(Guid batchJobId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Batch creation receipts are not supported by this repository.");
    Task AddPendingShareOperationsAsync(
        IEnumerable<PendingShareOperationRecord> operations,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Pending share operations are not supported by this repository.");
    Task AddShareOperationsAsync(
        IEnumerable<ShareOperationRecord> operations,
        IEnumerable<ShareOperationRemoteItemRecord> remoteItems,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Share operations are not supported by this repository.");
}
