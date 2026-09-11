using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Scheduling;

public enum SharePauseReason
{
    AwaitingExistingShareDecision,
    IncompatibleExistingShare,
    CancelledByUser,
    AccountSessionUnavailable,
    AccountVerificationRequired,
    ShareSettingsRejected,
}

public enum ShareExecutionDisposition
{
    Succeeded,
    ReusedExisting,
    ResultPending,
    Failed,
    Paused,
    Stale,
    NotEnabled,
}

public readonly record struct ShareExecutionLease(Guid OperationId, int Generation, long LeaseGeneration)
{
    public override string ToString() =>
        $"ShareExecutionLease {{ Identity = [REDACTED], Generation = {Generation}, LeaseGeneration = {LeaseGeneration} }}";
}

public sealed record ShareExecutionResult(ShareExecutionDisposition Disposition, string StableStatus)
{
    public static ShareExecutionResult NotEnabled { get; } = new(ShareExecutionDisposition.NotEnabled, "未启用分享");

    public override string ToString() =>
        $"ShareExecutionResult {{ Disposition = {Disposition}, StableStatus = {StableStatus} }}";
}

public sealed record ShareExecutionContext(
    ShareOperationRecord Operation,
    BatchShareSettingsRecord Settings,
    bool Selected,
    bool ShareEnabled,
    RemoteItemIdentitySet ExactRemoteItems,
    ExistingShareDecision? ExistingShareDecision,
    BaiduShareRecord? ConfirmedExistingShare = null,
    bool AcceptanceOnly = false)
{
    public override string ToString() =>
        $"ShareExecutionContext {{ Selected = {Selected}, ShareEnabled = {ShareEnabled}, Decision = {ExistingShareDecision}, Identity = [REDACTED], UserContent = [REDACTED] }}";
}

public sealed record ExistingShareAccountSummary(
    Guid AccountId,
    int DiscoveredCount,
    int CompatibleCount,
    int IncompatibleCount,
    string AccountDisplayName = "")
{
    public bool RequiresDecision => DiscoveredCount > 0;

    public override string ToString() =>
        $"ExistingShareAccountSummary {{ Identity = [REDACTED], DiscoveredCount = {DiscoveredCount}, CompatibleCount = {CompatibleCount}, IncompatibleCount = {IncompatibleCount} }}";
}

public sealed record ExistingShareBatchSummary(
    Guid BatchJobId,
    string ConfirmationToken,
    IReadOnlyList<ExistingShareAccountSummary> Accounts)
{
    public override string ToString() =>
        $"ExistingShareBatchSummary {{ Identity = [REDACTED], ConfirmationToken = [REDACTED], AccountCount = {Accounts.Count} }}";
}

public sealed record ConfirmExistingShareDecisionCommand(
    Guid BatchJobId,
    string ConfirmationToken,
    ExistingShareDecision BatchDefault,
    IReadOnlyDictionary<Guid, ExistingShareDecision> PerAccountOverrides)
{
    public override string ToString() =>
        $"ConfirmExistingShareDecisionCommand {{ Identity = [REDACTED], ConfirmationToken = [REDACTED], BatchDefault = {BatchDefault}, OverrideCount = {PerAccountOverrides.Count} }}";
}

public sealed record ExistingShareDecisionConfirmation(bool Accepted, bool IsStale)
{
    public static ExistingShareDecisionConfirmation Stale { get; } = new(false, true);
    public static ExistingShareDecisionConfirmation Success { get; } = new(true, false);
}

public sealed record ShareDecisionAccountContext(
    Guid BatchJobId,
    Guid AccountId,
    BatchShareSettingsRecord Settings,
    string AccountDisplayName = "")
{
    public override string ToString() =>
        "ShareDecisionAccountContext { Identity = [REDACTED], Settings = [REDACTED] }";
}

public sealed record ExistingShareDiscoveryMember(
    Guid BatchJobId,
    Guid AccountId,
    string SettingsFingerprint,
    string DiscoveryFingerprint,
    string ExactItemSetFingerprint,
    string ShareId,
    string ShareUrl,
    string ExtractionCode,
    bool IsPermanent,
    bool IsAccessUnlimited,
    bool IsCompatible)
{
    public override string ToString() =>
        $"ExistingShareDiscoveryMember {{ Identity = [REDACTED], IsCompatible = {IsCompatible}, Share = [REDACTED] }}";
}

public sealed record ExistingShareDiscoveryPage(
    IReadOnlyList<ExistingShareDiscoveryMember> Items,
    string? NextCursor)
{
    public override string ToString() =>
        $"ExistingShareDiscoveryPage {{ ItemCount = {Items.Count}, NextCursor = [REDACTED] }}";
}

public interface IShareExecutionStore
{
    Task<ShareExecutionContext?> LoadAsync(Guid operationId, CancellationToken cancellationToken = default);
    Task<bool> TryMarkPreparingAsync(Guid operationId, int generation, long leaseGeneration, DateTimeOffset updatedAt, CancellationToken cancellationToken = default);
    Task AppendDiscoveryBaselinePageAsync(Guid operationId, long leaseGeneration, IEnumerable<ShareDiscoveryBaselineRecord> entries, CancellationToken cancellationToken = default);
    Task<StringKeysetPage<ShareDiscoveryBaselineRecord>> GetDiscoveryBaselineKeysetPageAsync(Guid operationId, string? afterShareId, int pageSize, CancellationToken cancellationToken = default);
    Task<bool> TryCompleteDiscoveryBaselineAsync(Guid operationId, int generation, long leaseGeneration, string fingerprint, int count, DateTimeOffset completedAt, CancellationToken cancellationToken = default);
    Task<bool> TryMarkSubmittingAsync(Guid operationId, int generation, long leaseGeneration, string descriptor, DateTimeOffset submittedAt, CancellationToken cancellationToken = default);
    Task<bool> TryRecordAdapterRequestAsync(Guid operationId, int generation, long leaseGeneration, string requestId, DateTimeOffset updatedAt, CancellationToken cancellationToken = default);
    Task<bool> TryRecordDefinitelyNotAcceptedRetryAsync(Guid operationId, int generation, long leaseGeneration, int expectedAttemptCount, DateTimeOffset updatedAt, CancellationToken cancellationToken = default);
    Task<bool> TryBeginSubmissionAttemptAsync(Guid operationId, int generation, long leaseGeneration, int expectedAttemptCount, DateTimeOffset updatedAt, CancellationToken cancellationToken = default);
    Task<bool> TryMarkResultPendingAsync(Guid operationId, int generation, long leaseGeneration, DateTimeOffset nextReconcileAt, DateTimeOffset updatedAt, CancellationToken cancellationToken = default);
    Task<bool> TryPauseAsync(Guid operationId, int generation, long leaseGeneration, SharePauseReason reason, DateTimeOffset updatedAt, CancellationToken cancellationToken = default);
    Task<bool> TryFailAsync(Guid operationId, int generation, long leaseGeneration, string errorCode, string safeMessage, DateTimeOffset updatedAt, CancellationToken cancellationToken = default);
    Task<bool> AcceptResultAsync(Guid operationId, ShareOperationStatus expectedStatus, long leaseGeneration, BaiduShareResultRecord result, DateTimeOffset updatedAt, CancellationToken cancellationToken = default);
    Task<bool> ReuseExistingResultAsync(Guid operationId, long leaseGeneration, BaiduShareResultRecord result, DateTimeOffset updatedAt, CancellationToken cancellationToken = default);
}

public interface IExistingShareDecisionStore
{
    Task<IReadOnlyList<Guid>> GetPendingDecisionBatchIdsAsync(
        int maximumCount,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Pending existing-share decision queries are not supported by this store.");
    Task<IReadOnlyList<ShareDecisionAccountContext>> GetDecisionAccountsAsync(Guid batchJobId, CancellationToken cancellationToken = default);
    Task<StringKeysetPage<string>> GetDecisionExactSetFingerprintsKeysetPageAsync(Guid batchJobId, Guid accountId, string? afterFingerprint, int pageSize, CancellationToken cancellationToken = default);
    Task<long> BeginDiscoveryAsync(Guid runId, ShareDecisionAccountContext account, CancellationToken cancellationToken = default);
    Task<StringKeysetPage<string>> GetDiscoveryRunExactSetFingerprintsKeysetPageAsync(Guid runId, Guid accountId, string? afterFingerprint, int pageSize, CancellationToken cancellationToken = default);
    Task<bool> ContainsDecisionExactSetAsync(Guid batchJobId, Guid accountId, string exactItemSetFingerprint, CancellationToken cancellationToken = default);
    Task StageDiscoveryPageAsync(Guid runId, ShareDecisionAccountContext account, IReadOnlyList<BaiduShareRecord> candidates, CancellationToken cancellationToken = default);
    Task<ExistingShareDiscoveryPage> GetStagedDiscoveryPageAsync(Guid runId, Guid accountId, string? afterCursor, int pageSize, CancellationToken cancellationToken = default);
    Task AbortDiscoveryAsync(Guid runId, CancellationToken cancellationToken = default);
    Task<bool> TrySaveDiscoveryAndPauseAsync(Guid runId, IReadOnlyList<BatchShareDiscoverySnapshotRecord> snapshots, CancellationToken cancellationToken = default);
    Task<BatchShareDiscoverySnapshotRecord?> GetDiscoverySnapshotAsync(Guid batchJobId, Guid accountId, string settingsFingerprint, CancellationToken cancellationToken = default);
    Task<bool> ApplyDecisionSnapshotsAsync(IReadOnlyList<BatchShareDecisionSnapshotRecord> snapshots, CancellationToken cancellationToken = default);
}
