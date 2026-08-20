using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Core.Abstractions;

public interface IShareSchedulingStore
{
    Task<ShareOperationRecord?> TryReserveNextAsync(
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);
    Task<StringKeysetPage<ShareOperationRemoteItemRecord>> GetRemoteItemsKeysetPageAsync(
        Guid shareOperationId,
        string? afterRemoteItemId,
        int pageSize,
        CancellationToken cancellationToken = default);
    Task AppendDiscoveryBaselinePageAsync(
        Guid shareOperationId,
        long expectedLeaseGeneration,
        IEnumerable<ShareDiscoveryBaselineRecord> entries,
        CancellationToken cancellationToken = default);
    Task<StringKeysetPage<ShareDiscoveryBaselineRecord>> GetDiscoveryBaselineKeysetPageAsync(
        Guid shareOperationId,
        string? afterShareId,
        int pageSize,
        CancellationToken cancellationToken = default);
    Task<bool> TryCompleteDiscoveryBaselineAsync(
        Guid shareOperationId,
        int expectedGeneration,
        long expectedLeaseGeneration,
        string baselineFingerprint,
        int expectedShareCount,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default);
    Task<ShareDiscoveryBaselineStateRecord?> GetDiscoveryBaselineStateAsync(
        Guid shareOperationId,
        CancellationToken cancellationToken = default);
    Task<bool> TryMarkSubmittingAsync(Guid shareOperationId, int expectedGeneration, long expectedLeaseGeneration, string requestDescriptorJson,
        DateTimeOffset submittedAt, CancellationToken cancellationToken = default);
    Task<bool> TryRecordAdapterRequestAsync(Guid shareOperationId, int expectedGeneration, long expectedLeaseGeneration, string adapterRequestId,
        DateTimeOffset updatedAt, CancellationToken cancellationToken = default);
    Task<bool> TryMarkResultPendingAsync(Guid shareOperationId, int expectedGeneration, long expectedLeaseGeneration,
        DateTimeOffset nextReconcileAt, DateTimeOffset updatedAt, CancellationToken cancellationToken = default);
    Task<bool> TryStartNextGenerationAsync(Guid shareOperationId, ShareOperationStatus expectedStatus, int expectedGeneration, long expectedLeaseGeneration,
        DateTimeOffset leaseExpiresAt, DateTimeOffset updatedAt, CancellationToken cancellationToken = default);
    Task<bool> TrySaveDiscoverySnapshotAsync(
        BatchShareDiscoverySnapshotRecord snapshot,
        CancellationToken cancellationToken = default);
    Task<BatchShareDiscoverySnapshotRecord?> GetDiscoverySnapshotAsync(
        Guid batchJobId,
        Guid accountId,
        string settingsFingerprint,
        CancellationToken cancellationToken = default);
    Task SaveDecisionSnapshotAsync(BatchShareDecisionSnapshotRecord snapshot, CancellationToken cancellationToken = default);
    Task<BatchShareDecisionSnapshotRecord?> GetDecisionSnapshotAsync(
        Guid batchJobId,
        Guid accountId,
        CancellationToken cancellationToken = default);
    Task<bool> AcceptResultAsync(
        Guid shareOperationId,
        ShareOperationStatus expectedStatus,
        long expectedLeaseGeneration,
        BaiduShareResultRecord result,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);
    Task<bool> ReuseExistingResultAsync(Guid shareOperationId, long expectedLeaseGeneration, BaiduShareResultRecord result,
        DateTimeOffset updatedAt, CancellationToken cancellationToken = default);
    Task<BaiduShareResultRecord?> GetAcceptedResultAsync(Guid shareOperationId, CancellationToken cancellationToken = default);
    Task<CursorKeysetPage<BaiduShareResultRecord, ShareResultCursor>> GetResultsKeysetPageAsync(
        ShareResultKeysetQuery query,
        CancellationToken cancellationToken = default);
}
