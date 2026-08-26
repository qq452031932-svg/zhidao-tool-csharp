using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Core.Abstractions;

public record PageQuery(int PageNumber, int PageSize)
{
    public int Offset => checked((PageNumber - 1) * PageSize);

    public void Validate()
    {
        if (PageNumber < 1) throw new ArgumentOutOfRangeException(nameof(PageNumber));
        if (PageSize is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(PageSize));
    }
}

public sealed record Page<T>(IReadOnlyList<T> Items, long TotalCount, int PageNumber, int PageSize);

public sealed record KeysetPage<T>(IReadOnlyList<T> Items, long? TotalCount, long? NextCursor);

public sealed record StringKeysetPage<T>(IReadOnlyList<T> Items, string? NextCursor)
{
    public override string ToString() =>
        $"StringKeysetPage {{ Count = {Items.Count}, HasNext = {NextCursor is not null}, Cursor = [REDACTED], Items = [REDACTED] }}";
}

public sealed record CursorKeysetPage<T, TCursor>(IReadOnlyList<T> Items, TCursor? NextCursor)
    where TCursor : class
{
    public override string ToString() =>
        $"CursorKeysetPage {{ Count = {Items.Count}, HasNext = {NextCursor is not null}, Cursor = [REDACTED], Items = [REDACTED] }}";
}

public sealed record StagedImportKeysetQuery(
    Guid ImportSessionId,
    string? Disposition,
    string? PrecheckStatus,
    int PageSize,
    long AfterSourceRowNumber = 0,
    bool IncludeTotalCount = true)
{
    public void Validate()
    {
        if (ImportSessionId == Guid.Empty) throw new ArgumentException("An import session is required.", nameof(ImportSessionId));
        if (PageSize is < 1 or > 1_000) throw new ArgumentOutOfRangeException(nameof(PageSize));
        if (AfterSourceRowNumber < 0) throw new ArgumentOutOfRangeException(nameof(AfterSourceRowNumber));
    }
}

public sealed record ResourceResultKeysetQuery(
    Guid BatchJobId,
    string? CandidateStatus,
    string? GlobalValidityStatus,
    int PageSize,
    long AfterCandidateSequence = 0)
{
    public void Validate()
    {
        if (BatchJobId == Guid.Empty) throw new ArgumentException("A batch is required.", nameof(BatchJobId));
        if (PageSize is < 1 or > 1_000) throw new ArgumentOutOfRangeException(nameof(PageSize));
        if (AfterCandidateSequence < 0) throw new ArgumentOutOfRangeException(nameof(AfterCandidateSequence));
    }
}

public sealed record OperationResultKeysetQuery(
    Guid BatchJobId,
    Guid? AccountId,
    string? Status,
    DateTimeOffset? UpdatedFrom,
    DateTimeOffset? UpdatedTo,
    int PageSize,
    long AfterAllocationOrdinal = 0,
    bool IncludeTotalCount = true)
{
    public void Validate()
    {
        if (BatchJobId == Guid.Empty) throw new ArgumentException("A batch is required.", nameof(BatchJobId));
        if (AccountId == Guid.Empty) throw new ArgumentException("An account filter cannot be empty.", nameof(AccountId));
        if (PageSize is < 1 or > 1_000) throw new ArgumentOutOfRangeException(nameof(PageSize));
        if (AfterAllocationOrdinal < 0) throw new ArgumentOutOfRangeException(nameof(AfterAllocationOrdinal));
        if (UpdatedFrom > UpdatedTo) throw new ArgumentException("The result time range is invalid.");
    }
}

public sealed record OperationResultRecord(
    Guid OperationId,
    Guid ResourceItemId,
    Guid BatchJobId,
    long CandidateSequence,
    long AllocationOrdinal,
    string ShareUrl,
    string? Note,
    Guid AccountId,
    string AccountDisplayName,
    string TargetPath,
    string Status,
    string? OutcomeKind,
    string? LastErrorCode,
    string? LastErrorMessage,
    DateTimeOffset UpdatedAt,
    string? ResolvedTitle = null,
    Guid? ShareOperationId = null,
    string? ShareStatus = null,
    string? ShareLastErrorCode = null,
    string? ShareLastErrorMessage = null,
    bool HasCompleteShareEvidence = false,
    bool IsBatchShareActionAllowed = false,
    bool IsAccountActive = false,
    bool ShareEnabled = false,
      bool ShareCanRestartSafely = false,
      int? ShareGeneration = null,
      DateTimeOffset? ShareUpdatedAt = null,
      int TransferAttemptCount = 0,
      bool CanSafelyRetryTransfer = false)
{
    public override string ToString() =>
        $"OperationResultRecord {{ OperationId = {OperationId}, CandidateSequence = {CandidateSequence}, Status = {Status}, UserContent = [REDACTED] }}";
}

public sealed record AccountBatchStatisticsRecord(
    Guid AccountId,
    string AccountDisplayName,
    int PriorityOrder,
    long NewCount,
    long ExistingCount,
    long FailedCount,
    long PendingCount,
    long PausedCount)
{
    public override string ToString() =>
        $"AccountBatchStatisticsRecord {{ AccountId = {AccountId}, PriorityOrder = {PriorityOrder}, NewCount = {NewCount}, ExistingCount = {ExistingCount}, FailedCount = {FailedCount}, PendingCount = {PendingCount}, PausedCount = {PausedCount}, DisplayName = [REDACTED] }}";
}

public sealed record BatchStatisticsRecord(
    Guid BatchJobId,
    long ResourceCount,
    long OperationCount,
    IReadOnlyList<AccountBatchStatisticsRecord> Accounts);

public sealed record StagedImportRowQuery(Guid ImportSessionId, string? Disposition, int PageNumber, int PageSize)
    : PageQuery(PageNumber, PageSize);

public sealed record RequestedAccountNameGroupRecord(string RequestedAccountName, long RowCount)
{
    public override string ToString() =>
        $"RequestedAccountNameGroupRecord {{ RequestedAccountName = [REDACTED], RowCount = {RowCount} }}";
}

public sealed record RequestedAccountResolutionCounts(
    long TotalGroups,
    long UnresolvedGroups,
    IReadOnlyList<Guid>? MappedAccountIds = null);

public sealed record ImportDispositionCounts(
    int TotalRows,
    int ReadyRows,
    int InvalidRows,
    int DuplicateRows,
    int ConflictRows,
    int NeedsResolutionRows,
    int ExcludedRows);

public sealed record ImportSessionRecord(
    Guid Id, string SourceType, string SourceName, string Status, int TotalRows, int ReadyRows,
    int InvalidRows, int DuplicateRows, int ExcludedRows, Guid? PrecheckAccountId,
    Guid? CreatedBatchJobId, Guid? AppendToBatchJobId, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    string? ColumnMappingFingerprint = null,
    string? SourceContentFingerprint = null,
    int? HeaderRowNumber = null)
{
    public static ImportSessionRecord Create(Guid id, string sourceType, string sourceName)
    {
        var now = DateTimeOffset.UtcNow;
        return new(id, sourceType, sourceName, "Parsing", 0, 0, 0, 0, 0, null, null, null, now, now);
    }

    public override string ToString() =>
        $"ImportSessionRecord {{ Status = {Status}, TotalRows = {TotalRows}, MappingConfigured = {ColumnMappingFingerprint is not null}, UserContent = [REDACTED] }}";
}

public sealed record StagedImportRowRecord(
    Guid Id, Guid ImportSessionId, int SourceRowNumber, string ShareUrl, string? NormalizedShareId,
    string? ExtractionCode, string? TargetPath, string? CreateFolderName, string? RequestedAccountName,
    Guid? MappedAccountId, string? Note, string Disposition, Guid? DuplicateOfStagedRowId,
    string? ValidationMessage, string? PrecheckStatus, string? PrecheckErrorCode,
    DateTimeOffset? PrecheckedAt, DateTimeOffset UpdatedAt)
{
    public static StagedImportRowRecord Create(Guid id, Guid sessionId, int row, string shareUrl, string disposition) =>
        new(id, sessionId, row, shareUrl, null, null, null, null, null, null, null, disposition, null, null, null, null, null, DateTimeOffset.UtcNow);

    public override string ToString() =>
        $"StagedImportRowRecord {{ Id = {Id}, ImportSessionId = {ImportSessionId}, SourceRowNumber = {SourceRowNumber}, Disposition = {Disposition}, UserContent = [REDACTED] }}";
}

public sealed record AccountRecord(
    Guid Id, string BaiduUserId, string DisplayName, byte[]? EncryptedSession, string? WebView2ProfileDirectory,
    string LoginMethod, string Status, long? CapacityTotal, long? CapacityUsed, int DailyLimit,
    int DailyExecutedCount, int SortOrder, bool IsEnabled, DateTimeOffset? LastValidatedAt,
    string DailyCountDateKey, bool IsDeleted, DateTimeOffset? DeletedAt)
{
    public static AccountRecord Create(Guid id, string baiduUserId, string displayName) =>
        new(id, baiduUserId, displayName, null, null, "WebView2", "Active", null, null, 0, 0, 0, true, null,
            DateTimeOffset.Now.ToString("yyyy-MM-dd"), false, null);

    public override string ToString() =>
        $"AccountRecord {{ Id = {Id}, Identity = [REDACTED], DisplayName = [REDACTED], EncryptedSession = [REDACTED], Status = {Status} }}";
}

public sealed record AccountRuleRecord(Guid Id, Guid AccountId, string RuleType, string RuleValue, int Priority, bool IsEnabled);

public sealed record BatchJobRecord(
    Guid Id, string Name, string SourceType, string TransferMode, string QuotaBasis, string ShortagePolicy,
    int ResourceCount, int OperationCount, string DefaultTargetPath, string FolderCreationMode,
    string DuplicatePolicy, string ValidationMode, string Status, DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, Guid? ImportSessionId, long QueueOrder,
    int ShareOperationCount = 0)
{
    public override string ToString() =>
        $"BatchJobRecord {{ Status = {Status}, ResourceCount = {ResourceCount}, OperationCount = {OperationCount}, ShareOperationCount = {ShareOperationCount}, UserContent = [REDACTED] }}";
}

public sealed record ResourceItemRecord(
    Guid Id, Guid BatchJobId, Guid? StagedImportRowId, long CandidateSequence, string ShareUrl,
    string NormalizedShareId, string? ExtractionCode, string? Note, string? TargetPathOverride,
    string? NormalizedTargetPathOverride, string? CreateFolderName, string FolderCreationModeOverride,
    string GlobalValidityStatus, Guid? CandidateConstraintAccountId, string CandidateStatus,
    Guid? ReservedAccountId, DateTimeOffset? ReservationExpiresAt, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    string? ResolvedOutputTitle = null)
{
    public override string ToString() =>
        $"ResourceItemRecord {{ Id = {Id}, BatchJobId = {BatchJobId}, CandidateSequence = {CandidateSequence}, CandidateStatus = {CandidateStatus}, UserContent = [REDACTED] }}";
}

public sealed record TransferOperationRecord(
    Guid Id, Guid ResourceItemId, Guid BatchJobId, long SequenceNumber, long AllocationOrdinal,
    string TargetPath, string NormalizedTargetPath, string CreateFolderName, Guid AssignedAccountId,
    string AllocationSource, string Status, string? OutcomeKind, int AttemptCount, string OperationFingerprint,
    string? ConfirmationDescriptorJson, string? AdapterRequestId, string? RemoteItemIdsJson, string SubmitState,
    string TargetSlotState, DateTimeOffset? TargetSlotReservedAt, DateTimeOffset? DailyCountedAt,
    DateTimeOffset? SubmitStartedAt, string? LastErrorCode, string? LastErrorMessage,
    DateTimeOffset? LeaseExpiresAt, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    ExecutionProfileSnapshot? PersistedExecutionProfile = null)
{
    public ExecutionProfileSnapshot ExecutionProfile { get; } =
        PersistedExecutionProfile ?? ExecutionProfileSnapshot.LegacyForAccount(AssignedAccountId);

    public override string ToString() =>
        $"TransferOperationRecord {{ Id = {Id}, BatchJobId = {BatchJobId}, SequenceNumber = {SequenceNumber}, AllocationOrdinal = {AllocationOrdinal}, Status = {Status}, OutcomeKind = {OutcomeKind}, UserContent = [REDACTED] }}";
}

public sealed record OperationSubmissionPreparation(
    Guid OperationId, string ConfirmationDescriptorJson, DateTimeOffset LeaseExpiresAt, DateTimeOffset UpdatedAt);

public sealed record OperationSubmissionResponse(
    Guid OperationId, string? AdapterRequestId, string? RemoteItemIdsJson, string SubmitState, string Status, DateTimeOffset UpdatedAt);

public sealed record ImportColumnDefinitionRecord(
    Guid ImportSessionId,
    int ColumnOrdinal,
    string? SourceHeader,
    string MappedRole,
    bool IsRequired)
{
    public override string ToString() =>
        $"ImportColumnDefinitionRecord {{ ColumnOrdinal = {ColumnOrdinal}, MappedRole = {MappedRole}, IsRequired = {IsRequired}, UserContent = [REDACTED] }}";
}

public sealed record StagedImportRowPayloadRecord(
    Guid StagedImportRowId,
    string? SourceTitle,
    string? ResolvedOutputTitle,
    string OriginalShareUrl,
    string RawValuesJson)
{
    public override string ToString() =>
        "StagedImportRowPayloadRecord { Identity = [REDACTED], UserContent = [REDACTED] }";
}

public sealed record BatchShareSettingsRecord(
    Guid BatchJobId,
    bool IsPermanent,
    bool IsAccessUnlimited,
    string ExtractionCodeMode,
    string? FixedExtractionCode,
    string ExistingSharePolicy,
    DateTimeOffset CreatedAt,
    string SettingsFingerprint)
{
    public override string ToString() =>
        $"BatchShareSettingsRecord {{ Identity = [REDACTED], IsPermanent = {IsPermanent}, IsAccessUnlimited = {IsAccessUnlimited}, ExtractionCodeMode = {ExtractionCodeMode}, ExistingSharePolicy = {ExistingSharePolicy}, FixedExtractionCode = [REDACTED], Fingerprint = [REDACTED] }}";
}

public sealed record BatchOutputAccountRecord(
    Guid BatchJobId,
    Guid AccountId,
    int OutputOrdinal,
    string OutputLabel,
    int PlannedTransferCount,
    int PlannedShareCount,
    bool ShareEnabled)
{
    public override string ToString() =>
        $"BatchOutputAccountRecord {{ Identity = [REDACTED], OutputOrdinal = {OutputOrdinal}, PlannedTransferCount = {PlannedTransferCount}, PlannedShareCount = {PlannedShareCount}, ShareEnabled = {ShareEnabled}, OutputLabel = [REDACTED] }}";
}

public sealed record BatchSourceColumnRecord(
    Guid BatchJobId,
    int ColumnOrdinal,
    string? SourceHeader,
    string MappedRole,
    bool IsRequired)
{
    public override string ToString() =>
        $"BatchSourceColumnRecord {{ Identity = [REDACTED], ColumnOrdinal = {ColumnOrdinal}, MappedRole = {MappedRole}, IsRequired = {IsRequired}, Header = [REDACTED] }}";
}

public sealed record BatchCreationReceiptRecord(
    string CommandFingerprint,
    string ConfirmationTokenHash,
    string SnapshotFingerprint,
    Guid ImportSessionId,
    Guid BatchJobId,
    string? ColumnMappingFingerprint,
    DateTimeOffset CreatedAt,
    string ConfirmationSummaryJson)
{
    public override string ToString() =>
        "BatchCreationReceiptRecord { Identity = [REDACTED], Fingerprints = [REDACTED] }";
}

public sealed record PendingShareOperationRecord(
    Guid Id,
    Guid TransferOperationId,
    Guid ResourceItemId,
    Guid BatchJobId,
    Guid AccountId,
    int OutputOrdinal,
    string SettingsFingerprint,
    DateTimeOffset CreatedAt)
{
    public override string ToString() =>
        $"PendingShareOperationRecord {{ Identity = [REDACTED], OutputOrdinal = {OutputOrdinal}, Status = WaitingTransfer }}";
}

public sealed record ShareOperationRecord(
    Guid Id,
    Guid TransferOperationId,
    Guid ResourceItemId,
    Guid BatchJobId,
    Guid AccountId,
    int OutputOrdinal,
    string Status,
    string ExactItemSetFingerprint,
    string SettingsFingerprint,
    int Generation,
    string? RequestDescriptorJson,
    string? AdapterRequestId,
    string SubmissionAcceptance,
    int AttemptCount,
    DateTimeOffset? LeaseExpiresAt,
    DateTimeOffset? SubmitStartedAt,
    DateTimeOffset? NextReconcileAt,
    string? LastErrorCode,
    string? LastErrorMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long LeaseGeneration = 0,
    ExecutionProfileSnapshot? PersistedExecutionProfile = null)
{
    public ExecutionProfileSnapshot ExecutionProfile =>
        PersistedExecutionProfile ?? ExecutionProfileSnapshot.LegacyForAccount(AccountId);

    public override string ToString() =>
        $"ShareOperationRecord {{ Identity = [REDACTED], OutputOrdinal = {OutputOrdinal}, Status = {Status}, Generation = {Generation}, UserContent = [REDACTED] }}";
}

public sealed record ShareOperationRemoteItemRecord(
    Guid ShareOperationId,
    int ItemOrdinal,
    string RemoteItemId,
    string DisplayName)
{
    public override string ToString() =>
        $"ShareOperationRemoteItemRecord {{ Identity = [REDACTED], ItemOrdinal = {ItemOrdinal}, RemoteItem = [REDACTED] }}";
}

public sealed record ShareDiscoveryBaselineRecord(
    Guid ShareOperationId,
    string ShareId,
    int Generation = 1,
    string? ShareUrl = null,
    string? ExtractionCode = null,
    bool? IsPermanent = null,
    bool? IsAccessUnlimited = null)
{
    public override string ToString() =>
        $"ShareDiscoveryBaselineRecord {{ Identity = [REDACTED], Generation = {Generation}, ShareId = [REDACTED] }}";
}

public sealed record ShareDiscoveryBaselineStateRecord(
    Guid ShareOperationId,
    int Generation,
    string? Fingerprint,
    int ShareCount,
    bool IsComplete,
    DateTimeOffset? CompletedAt)
{
    public override string ToString() =>
        $"ShareDiscoveryBaselineStateRecord {{ Identity = [REDACTED], Generation = {Generation}, ShareCount = {ShareCount}, IsComplete = {IsComplete}, Discovery = [REDACTED] }}";
}

public sealed record BatchShareDecisionSnapshotRecord(
    Guid BatchJobId,
    Guid AccountId,
    string SettingsFingerprint,
    string Decision,
    string DiscoveryFingerprint,
    int DiscoveredCount,
    int CompatibleCount,
    int IncompatibleCount,
    string Generation,
    DateTimeOffset ConfirmedAt,
    long DiscoveryVersion = 1)
{
    public override string ToString() =>
        $"BatchShareDecisionSnapshotRecord {{ Identity = [REDACTED], Decision = {Decision}, DiscoveredCount = {DiscoveredCount}, CompatibleCount = {CompatibleCount}, IncompatibleCount = {IncompatibleCount}, Discovery = [REDACTED] }}";
}

public sealed record BatchShareDiscoverySnapshotRecord(
    Guid BatchJobId,
    Guid AccountId,
    string SettingsFingerprint,
    string DiscoveryFingerprint,
    int DiscoveredCount,
    int CompatibleCount,
    int IncompatibleCount,
    string Generation,
    DateTimeOffset CompletedAt,
    long Version)
{
    public override string ToString() =>
        $"BatchShareDiscoverySnapshotRecord {{ Identity = [REDACTED], Version = {Version}, DiscoveredCount = {DiscoveredCount}, CompatibleCount = {CompatibleCount}, IncompatibleCount = {IncompatibleCount}, Discovery = [REDACTED] }}";
}

public sealed record BaiduShareResultRecord(
    Guid Id,
    Guid ShareOperationId,
    Guid BatchJobId,
    Guid ResourceItemId,
    Guid AccountId,
    string ExactItemSetFingerprint,
    string SettingsFingerprint,
    int Generation,
    string ShareId,
    string ShareUrl,
    string ExtractionCode,
    bool IsAccepted,
    bool IsCurrent,
    DateTimeOffset SharedAt,
    DateTimeOffset UpdatedAt)
{
    public override string ToString() =>
        $"BaiduShareResultRecord {{ Identity = [REDACTED], Generation = {Generation}, IsAccepted = {IsAccepted}, IsCurrent = {IsCurrent}, UserContent = [REDACTED] }}";
}

public sealed record ShareResultCursor(long CandidateSequence, int OutputOrdinal, Guid ResultId)
{
    public override string ToString() =>
        $"ShareResultCursor {{ CandidateSequence = {CandidateSequence}, Identity = [REDACTED] }}";
}

public sealed record ShareResultKeysetQuery(Guid BatchJobId, int PageSize, ShareResultCursor? After = null)
{
    public void Validate()
    {
        if (BatchJobId == Guid.Empty) throw new ArgumentException("A batch is required.", nameof(BatchJobId));
        if (PageSize is < 1 or > 1_000) throw new ArgumentOutOfRangeException(nameof(PageSize));
        if (After is { CandidateSequence: < 0 }) throw new ArgumentOutOfRangeException(nameof(After));
        if (After is { OutputOrdinal: <= 0 }) throw new ArgumentException("A result cursor output ordinal is required.", nameof(After));
        if (After is { ResultId: var resultId } && resultId == Guid.Empty) throw new ArgumentException("A result cursor identity is required.", nameof(After));
    }

    public override string ToString() =>
        $"ShareResultKeysetQuery {{ PageSize = {PageSize}, HasCursor = {After is not null}, Identity = [REDACTED] }}";
}

public sealed record AdapterCapabilityAcceptanceRecord(
    string AdapterKind,
    string ProtocolVersion,
    string SelectorVersion,
    string AdapterBuildVersion,
    DateTimeOffset AcceptedAt,
    string EvidenceHash)
{
    public override string ToString() =>
        $"AdapterCapabilityAcceptanceRecord {{ AdapterKind = {AdapterKind}, Versions = [REDACTED], AcceptedAt = {AcceptedAt:O}, EvidenceHash = [REDACTED] }}";
}

public sealed record AccountAllocationRecord(
    Guid Id, Guid BatchJobId, Guid AccountId, int PriorityOrder, string DefaultTargetPath,
    string NormalizedDefaultTargetPath, int TargetItemCount, int TargetSuccessCount,
    int EffectiveTargetSuccessCount, int AssignedItemCount, int AttemptedItemCount, int NewSuccessItemCount,
    int AlreadySatisfiedItemCount, int SuccessItemCount, int FailedItemCount, int LockedItemCount,
    int ExclusiveCandidateCount, int UnusedCandidateCount, int ReservedSlotCount, int FulfilledCount);

public sealed record ExecutionLogRecord(
    Guid Id, Guid? BatchJobId, Guid? ResourceItemId, Guid? TransferOperationId, Guid? AccountId,
    string Stage, string Level, string EventCode, string SanitizedMessage, DateTimeOffset CreatedAt)
{
    public override string ToString() =>
        $"ExecutionLogRecord {{ Id = {Id}, Stage = {Stage}, Level = {Level}, EventCode = {EventCode}, SanitizedMessage = [REDACTED] }}";
}

public enum AuditEventKind
{
    BatchCreated,
    CandidatesAppended,
    ShortfallReopened,
}

public sealed record AuditEventRecord(
    Guid Id,
    Guid? BatchJobId,
    Guid? ResourceItemId,
    Guid? TransferOperationId,
    Guid? AccountId,
    AuditEventKind EventKind,
    DateTimeOffset CreatedAt)
{
    public override string ToString() =>
        $"AuditEventRecord {{ Id = {Id}, EventKind = {EventKind}, EntityIds = [REDACTED] }}";
}
