using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Core.Abstractions;

public interface IStandaloneShareExecutionStore : IStandaloneShareStore
{
    Task<StandaloneShareOperationLease?> TryLeaseNextAsync(
        Guid batchId,
        Guid leaseOwnerId,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default);

    Task<bool> TryCompleteBaselineAsync(
        Guid operationId,
        Guid batchId,
        Guid accountId,
        int generation,
        long expectedLeaseGeneration,
        Guid expectedLeaseOwnerId,
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
        Guid batchId,
        Guid accountId,
        int generation,
        long expectedLeaseGeneration,
        Guid expectedLeaseOwnerId,
        long expectedRevision,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    Task<bool> TryRecordDefinitelyNotAcceptedAttemptAsync(
        Guid operationId,
        Guid batchId,
        Guid accountId,
        int generation,
        long expectedLeaseGeneration,
        Guid expectedLeaseOwnerId,
        long expectedRevision,
        int completedAttemptCount,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    Task<bool> TryBeginSubmissionRetryAsync(
        Guid operationId,
        Guid batchId,
        Guid accountId,
        int generation,
        long expectedLeaseGeneration,
        Guid expectedLeaseOwnerId,
        long expectedRevision,
        int completedAttemptCount,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    Task<bool> TryMarkResultPendingAsync(
        Guid operationId,
        Guid batchId,
        Guid accountId,
        int generation,
        long expectedLeaseGeneration,
        Guid expectedLeaseOwnerId,
        long expectedRevision,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    Task<bool> TryFailAsync(
        Guid operationId,
        Guid batchId,
        Guid accountId,
        int generation,
        long expectedLeaseGeneration,
        Guid expectedLeaseOwnerId,
        long expectedRevision,
        SubmissionAcceptance submissionAcceptance,
        string sanitizedErrorCode,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    Task<bool> TryAcceptResultAsync(
        StandaloneShareResultRecord result,
        long expectedLeaseGeneration,
        Guid expectedLeaseOwnerId,
        long expectedRevision,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    Task<bool> TryReuseExistingResultAsync(
        StandaloneShareResultRecord result,
        long expectedLeaseGeneration,
        Guid expectedLeaseOwnerId,
        long expectedRevision,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    Task<StandaloneShareResultRecord?> GetResultAsync(
        Guid operationId,
        int generation,
        CancellationToken cancellationToken = default);

    Task<int> RecoverExpiredLeasesAsync(
        DateTimeOffset now,
        int maximumCount,
        CancellationToken cancellationToken = default);

    Task<StandaloneShareOperationLease?> TryClaimReconciliationAsync(
        Guid batchId,
        Guid leaseOwnerId,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default);

    Task<bool> TryReleaseReconciliationAsync(
        Guid operationId,
        Guid batchId,
        Guid accountId,
        int generation,
        long expectedLeaseGeneration,
        Guid expectedLeaseOwnerId,
        long expectedRevision,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);
}
