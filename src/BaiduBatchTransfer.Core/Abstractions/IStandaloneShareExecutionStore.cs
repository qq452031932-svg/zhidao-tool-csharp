using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Core.Abstractions;

public interface IStandaloneShareExecutionStore : IStandaloneShareStore
{
    Task<StandaloneShareOperationLease?> TryLeaseNextAsync(
        Guid batchId,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default);

    Task<bool> TryCompleteBaselineAsync(
        Guid operationId,
        Guid accountId,
        int generation,
        long expectedLeaseGeneration,
        long expectedRevision,
        IReadOnlyCollection<string> existingShareIds,
        string baselineFingerprint,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    Task<StringKeysetPage<string>> GetBaselinePageAsync(
        Guid operationId,
        int generation,
        string? afterShareId,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<bool> TryMarkSubmittingAsync(
        Guid operationId,
        Guid accountId,
        int generation,
        long expectedLeaseGeneration,
        long expectedRevision,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    Task<bool> TryMarkResultPendingAsync(
        Guid operationId,
        Guid accountId,
        int generation,
        long expectedLeaseGeneration,
        long expectedRevision,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    Task<bool> TryFailAsync(
        Guid operationId,
        Guid accountId,
        int generation,
        long expectedLeaseGeneration,
        long expectedRevision,
        SubmissionAcceptance submissionAcceptance,
        string sanitizedErrorCode,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    Task<bool> TryAcceptResultAsync(
        StandaloneShareResultRecord result,
        long expectedLeaseGeneration,
        long expectedRevision,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    Task<StandaloneShareResultRecord?> GetResultAsync(
        Guid operationId,
        int generation,
        CancellationToken cancellationToken = default);
}
