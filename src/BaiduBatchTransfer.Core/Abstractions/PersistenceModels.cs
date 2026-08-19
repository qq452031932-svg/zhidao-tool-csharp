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
    DateTimeOffset UpdatedAt)
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
    long PendingCount)
{
    public override string ToString() =>
        $"AccountBatchStatisticsRecord {{ AccountId = {AccountId}, PriorityOrder = {PriorityOrder}, NewCount = {NewCount}, ExistingCount = {ExistingCount}, FailedCount = {FailedCount}, PendingCount = {PendingCount}, DisplayName = [REDACTED] }}";
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
    Guid? CreatedBatchJobId, Guid? AppendToBatchJobId, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static ImportSessionRecord Create(Guid id, string sourceType, string sourceName)
    {
        var now = DateTimeOffset.UtcNow;
        return new(id, sourceType, sourceName, "Parsing", 0, 0, 0, 0, 0, null, null, null, now, now);
    }
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
    DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, Guid? ImportSessionId, long QueueOrder);

public sealed record ResourceItemRecord(
    Guid Id, Guid BatchJobId, Guid? StagedImportRowId, long CandidateSequence, string ShareUrl,
    string NormalizedShareId, string? ExtractionCode, string? Note, string? TargetPathOverride,
    string? NormalizedTargetPathOverride, string? CreateFolderName, string FolderCreationModeOverride,
    string GlobalValidityStatus, Guid? CandidateConstraintAccountId, string CandidateStatus,
    Guid? ReservedAccountId, DateTimeOffset? ReservationExpiresAt, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
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
    DateTimeOffset? LeaseExpiresAt, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public override string ToString() =>
        $"TransferOperationRecord {{ Id = {Id}, BatchJobId = {BatchJobId}, SequenceNumber = {SequenceNumber}, AllocationOrdinal = {AllocationOrdinal}, Status = {Status}, OutcomeKind = {OutcomeKind}, UserContent = [REDACTED] }}";
}

public sealed record OperationSubmissionPreparation(
    Guid OperationId, string ConfirmationDescriptorJson, DateTimeOffset LeaseExpiresAt, DateTimeOffset UpdatedAt);

public sealed record OperationSubmissionResponse(
    Guid OperationId, string? AdapterRequestId, string? RemoteItemIdsJson, string SubmitState, string Status, DateTimeOffset UpdatedAt);

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
