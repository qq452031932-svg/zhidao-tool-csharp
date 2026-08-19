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
}
