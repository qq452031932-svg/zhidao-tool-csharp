namespace BaiduBatchTransfer.Core.Abstractions;

public interface IStandaloneShareStore
{
    Task CreateBatchAsync(
        StandaloneShareBatchRecord batch,
        IReadOnlyList<StandaloneShareOperationRecord> operations,
        CancellationToken cancellationToken = default);

    Task<Page<StandaloneShareBatchRecord>> GetBatchesAsync(
        PageQuery query,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StandaloneShareOperationRecord>> GetOperationsAsync(
        Guid batchId,
        CancellationToken cancellationToken = default);

    Task<StandaloneShareMutationResult<StandaloneShareBatchRecord>> TryPauseBatchAsync(
        Guid batchId,
        long expectedRevision,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    Task<StandaloneShareMutationResult<StandaloneShareBatchRecord>> TryResumeBatchAsync(
        Guid batchId,
        long expectedRevision,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    Task<StandaloneShareMutationResult<StandaloneShareOperationRecord>> TryRetryFailedOperationAsync(
        Guid operationId,
        int expectedGeneration,
        long expectedRevision,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);
}
