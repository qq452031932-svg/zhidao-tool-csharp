using System.Collections.Immutable;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Jobs;

public enum BatchAccountSelectionDecision
{
    Unspecified,
    Selected,
    NotSelected,
}

public sealed class BatchAccountDraft
{
    public BatchAccountDraft(
        Guid accountId,
        int priorityOrder,
        string defaultTargetPath,
        int targetItemCount,
        int targetSuccessCount,
        BatchAccountSelectionDecision selectionDecision,
        bool shareEnabled = true)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(priorityOrder);
        ArgumentOutOfRangeException.ThrowIfNegative(targetItemCount);
        ArgumentOutOfRangeException.ThrowIfNegative(targetSuccessCount);
        AccountId = accountId == Guid.Empty ? throw new ArgumentException("An account is required.", nameof(accountId)) : accountId;
        PriorityOrder = priorityOrder;
        DefaultTargetPath = string.IsNullOrWhiteSpace(defaultTargetPath)
            ? throw new ArgumentException("A default target path is required.", nameof(defaultTargetPath))
            : defaultTargetPath;
        TargetItemCount = targetItemCount;
        TargetSuccessCount = targetSuccessCount;
        if (!Enum.IsDefined(selectionDecision))
            throw new ArgumentOutOfRangeException(nameof(selectionDecision));
        SelectionDecision = selectionDecision;
        ShareEnabled = shareEnabled;
    }

    public Guid AccountId { get; }
    public int PriorityOrder { get; }
    public string DefaultTargetPath { get; }
    public int TargetItemCount { get; }
    public int TargetSuccessCount { get; }
    public BatchAccountSelectionDecision SelectionDecision { get; }
    public bool IsSelected => SelectionDecision == BatchAccountSelectionDecision.Selected;
    public bool ShareEnabled { get; }

    public override string ToString() =>
        $"BatchAccountDraft {{ AccountId = [REDACTED], PriorityOrder = {PriorityOrder}, DefaultTargetPath = [REDACTED], TargetItemCount = {TargetItemCount}, TargetSuccessCount = {TargetSuccessCount}, SelectionDecision = {SelectionDecision}, ShareEnabled = {ShareEnabled} }}";
}

public sealed class BatchDraft
{
    public BatchDraft(
        Guid batchJobId,
        Guid importSessionId,
        string name,
        TransferMode transferMode,
        QuotaBasis quotaBasis,
        ShortagePolicy shortagePolicy,
        IReadOnlyList<BatchAccountDraft> accounts,
        string defaultTargetPath,
        string folderCreationMode,
        DuplicatePolicy duplicatePolicy,
        string validationMode,
        long queueOrder,
        bool ignoreMappedAccountsForReplication,
        bool isSharePermanent = true,
        bool isShareAccessUnlimited = true,
        ShareExtractionCodeBehavior extractionCodeBehavior = ShareExtractionCodeBehavior.Random,
        string? fixedExtractionCode = null,
        string existingSharePolicy = "Ask")
    {
        BatchJobId = batchJobId == Guid.Empty ? throw new ArgumentException("A batch identifier is required.", nameof(batchJobId)) : batchJobId;
        ImportSessionId = importSessionId == Guid.Empty ? throw new ArgumentException("An import identifier is required.", nameof(importSessionId)) : importSessionId;
        Name = string.IsNullOrWhiteSpace(name) ? throw new ArgumentException("A batch name is required.", nameof(name)) : name;
        Accounts = (accounts ?? throw new ArgumentNullException(nameof(accounts))).ToImmutableArray();
        DefaultTargetPath = string.IsNullOrWhiteSpace(defaultTargetPath) ? throw new ArgumentException("A target path is required.", nameof(defaultTargetPath)) : defaultTargetPath;
        FolderCreationMode = string.IsNullOrWhiteSpace(folderCreationMode) ? throw new ArgumentException("A folder mode is required.", nameof(folderCreationMode)) : folderCreationMode;
        ValidationMode = string.IsNullOrWhiteSpace(validationMode) ? throw new ArgumentException("A validation mode is required.", nameof(validationMode)) : validationMode;
        if (queueOrder < 0) throw new ArgumentOutOfRangeException(nameof(queueOrder));
        TransferMode = transferMode;
        QuotaBasis = quotaBasis;
        ShortagePolicy = shortagePolicy;
        DuplicatePolicy = duplicatePolicy;
        QueueOrder = queueOrder;
        IgnoreMappedAccountsForReplication = ignoreMappedAccountsForReplication;
        if (!isSharePermanent || !isShareAccessUnlimited)
            throw new ArgumentException("Only permanent shares with unlimited access are supported.");
        if (existingSharePolicy is not ("Ask" or "Reuse" or "Regenerate" or "Cancel"))
            throw new ArgumentOutOfRangeException(nameof(existingSharePolicy));
        IsSharePermanent = isSharePermanent;
        IsShareAccessUnlimited = isShareAccessUnlimited;
        ExtractionCodeBehavior = extractionCodeBehavior;
        FixedExtractionCode = extractionCodeBehavior == ShareExtractionCodeBehavior.Fixed
            ? string.IsNullOrWhiteSpace(fixedExtractionCode)
                ? throw new ArgumentException("A fixed extraction code is required.", nameof(fixedExtractionCode))
                : fixedExtractionCode.Trim()
            : fixedExtractionCode is null
                ? null
                : throw new ArgumentException("Random extraction-code mode cannot contain a fixed code.", nameof(fixedExtractionCode));
        ExistingSharePolicy = existingSharePolicy;
        ShareSettingsFingerprint = BaiduBatchTransfer.Core.Domain.ShareSettingsFingerprint.Compute(
            IsSharePermanent, IsShareAccessUnlimited, ExtractionCodeBehavior, FixedExtractionCode);
    }

    public Guid BatchJobId { get; }
    public Guid ImportSessionId { get; }
    public string Name { get; }
    public TransferMode TransferMode { get; }
    public QuotaBasis QuotaBasis { get; }
    public ShortagePolicy ShortagePolicy { get; }
    public ImmutableArray<BatchAccountDraft> Accounts { get; }
    public string DefaultTargetPath { get; }
    public string FolderCreationMode { get; }
    public DuplicatePolicy DuplicatePolicy { get; }
    public string ValidationMode { get; }
    public long QueueOrder { get; }
    public bool IgnoreMappedAccountsForReplication { get; }
    public bool IsSharePermanent { get; }
    public bool IsShareAccessUnlimited { get; }
    public ShareExtractionCodeBehavior ExtractionCodeBehavior { get; }
    public string? FixedExtractionCode { get; }
    public string ExistingSharePolicy { get; }
    public string ShareSettingsFingerprint { get; }

    public override string ToString() =>
        $"BatchDraft {{ BatchJobId = {BatchJobId}, ImportSessionId = {ImportSessionId}, Name = [REDACTED], TransferMode = {TransferMode}, QuotaBasis = {QuotaBasis}, Accounts = {Accounts.Length}, PathsAndUserContent = [REDACTED] }}";
}

public sealed record AppendCandidatesDraft(Guid BatchJobId, Guid ImportSessionId)
{
    public override string ToString() =>
        $"AppendCandidatesDraft {{ BatchJobId = {BatchJobId}, ImportSessionId = {ImportSessionId} }}";
}

public sealed record StorageGrowthEstimate(
    long ResourceCount,
    long OperationCount,
    long DatabaseBytes,
    long IndexBytes,
    long WalBytes,
    long SafetyReserveBytes,
    long RequiredFreeBytes,
    long AvailableFreeBytes,
    bool HasSufficientSpace,
    long ShareOperationCount = 0,
    long ShareResultCount = 0,
    int OutputSnapshotCount = 0,
    int ResultColumnCount = 0);

public sealed class BatchAccountConfirmation
{
    public BatchAccountConfirmation(
        Guid accountId,
        int priorityOrder,
        string defaultTargetPath,
        string normalizedDefaultTargetPath,
        int targetItemCount,
        int targetSuccessCount,
        int lockedItemCount,
        int exclusiveCandidateCount,
        int effectiveTargetSuccessCount = -1,
        int fulfilledCount = 0,
        int reservedSlotCount = 0,
        int unusedCandidateCount = 0,
        int newExclusiveCandidateCount = 0,
        int reachableTargetSuccessCount = -1,
        long plannedTransferCount = -1,
        long plannedShareCount = -1,
        bool shareEnabled = true,
        int outputOrdinal = 0,
        string? outputLabel = null)
    {
        if (accountId == Guid.Empty) throw new ArgumentException("An account is required.", nameof(accountId));
        ArgumentOutOfRangeException.ThrowIfNegative(priorityOrder);
        ArgumentOutOfRangeException.ThrowIfNegative(targetItemCount);
        ArgumentOutOfRangeException.ThrowIfNegative(targetSuccessCount);
        ArgumentOutOfRangeException.ThrowIfNegative(lockedItemCount);
        ArgumentOutOfRangeException.ThrowIfNegative(exclusiveCandidateCount);
        ArgumentOutOfRangeException.ThrowIfNegative(fulfilledCount);
        ArgumentOutOfRangeException.ThrowIfNegative(reservedSlotCount);
        ArgumentOutOfRangeException.ThrowIfNegative(unusedCandidateCount);
        ArgumentOutOfRangeException.ThrowIfNegative(newExclusiveCandidateCount);
        AccountId = accountId;
        PriorityOrder = priorityOrder;
        DefaultTargetPath = string.IsNullOrWhiteSpace(defaultTargetPath)
            ? throw new ArgumentException("A target path is required.", nameof(defaultTargetPath))
            : defaultTargetPath;
        NormalizedDefaultTargetPath = string.IsNullOrWhiteSpace(normalizedDefaultTargetPath)
            ? throw new ArgumentException("A normalized target path is required.", nameof(normalizedDefaultTargetPath))
            : normalizedDefaultTargetPath;
        TargetItemCount = targetItemCount;
        TargetSuccessCount = targetSuccessCount;
        LockedItemCount = lockedItemCount;
        ExclusiveCandidateCount = exclusiveCandidateCount;
        EffectiveTargetSuccessCount = effectiveTargetSuccessCount < 0
            ? targetSuccessCount
            : effectiveTargetSuccessCount;
        FulfilledCount = fulfilledCount;
        ReservedSlotCount = reservedSlotCount;
        UnusedCandidateCount = unusedCandidateCount;
        NewExclusiveCandidateCount = newExclusiveCandidateCount;
        ReachableTargetSuccessCount = reachableTargetSuccessCount < 0
            ? EffectiveTargetSuccessCount
            : reachableTargetSuccessCount;
        PlannedTransferCount = plannedTransferCount < 0 ? TargetItemCount : plannedTransferCount;
        PlannedShareCount = plannedShareCount < 0 ? (shareEnabled ? PlannedTransferCount : 0) : plannedShareCount;
        if (PlannedShareCount > PlannedTransferCount || (!shareEnabled && PlannedShareCount != 0))
            throw new ArgumentOutOfRangeException(nameof(plannedShareCount));
        ShareEnabled = shareEnabled;
        OutputOrdinal = outputOrdinal;
        OutputLabel = outputLabel;
        if (EffectiveTargetSuccessCount > TargetSuccessCount || ReachableTargetSuccessCount > TargetSuccessCount)
            throw new ArgumentOutOfRangeException(nameof(effectiveTargetSuccessCount), "Effective and reachable targets cannot exceed the original target.");
    }

    public Guid AccountId { get; }
    public int PriorityOrder { get; }
    public string DefaultTargetPath { get; }
    public string NormalizedDefaultTargetPath { get; }
    public int TargetItemCount { get; }
    public int TargetSuccessCount { get; }
    public int LockedItemCount { get; }
    public int ExclusiveCandidateCount { get; }
    public int EffectiveTargetSuccessCount { get; }
    public int FulfilledCount { get; }
    public int ReservedSlotCount { get; }
    public int UnusedCandidateCount { get; }
    public int NewExclusiveCandidateCount { get; }
    public int ReachableTargetSuccessCount { get; }
    public long PlannedTransferCount { get; }
    public long PlannedShareCount { get; }
    public bool ShareEnabled { get; }
    public int OutputOrdinal { get; }
    public string? OutputLabel { get; }

    public override string ToString() =>
        $"BatchAccountConfirmation {{ AccountId = [REDACTED], PriorityOrder = {PriorityOrder}, Path = [REDACTED], TargetItemCount = {TargetItemCount}, TargetSuccessCount = {TargetSuccessCount}, EffectiveTargetSuccessCount = {EffectiveTargetSuccessCount}, FulfilledCount = {FulfilledCount}, ReservedSlotCount = {ReservedSlotCount}, LockedItemCount = {LockedItemCount}, ExclusiveCandidateCount = {ExclusiveCandidateCount}, UnusedCandidateCount = {UnusedCandidateCount}, NewExclusiveCandidateCount = {NewExclusiveCandidateCount}, ReachableTargetSuccessCount = {ReachableTargetSuccessCount}, PlannedTransferCount = {PlannedTransferCount}, PlannedShareCount = {PlannedShareCount}, ShareEnabled = {ShareEnabled}, OutputOrdinal = {OutputOrdinal}, OutputLabel = [REDACTED] }}";
}

public sealed class BatchConfirmationSummary
{
    public BatchConfirmationSummary(
        Guid importSessionId,
        DateTimeOffset importUpdatedAt,
        long resourceCount,
        long operationCount,
        IReadOnlyList<BatchAccountConfirmation> accounts,
        StorageGrowthEstimate storageEstimate,
        string confirmationToken,
        ResultColumnLayout? resultColumnLayout = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(resourceCount);
        ArgumentOutOfRangeException.ThrowIfNegative(operationCount);
        ImportSessionId = importSessionId;
        ImportUpdatedAt = importUpdatedAt;
        ResourceCount = resourceCount;
        OperationCount = operationCount;
        Accounts = (accounts ?? throw new ArgumentNullException(nameof(accounts))).ToImmutableArray();
        StorageEstimate = storageEstimate ?? throw new ArgumentNullException(nameof(storageEstimate));
        ConfirmationToken = string.IsNullOrWhiteSpace(confirmationToken)
            ? throw new ArgumentException("A confirmation token is required.", nameof(confirmationToken))
            : confirmationToken;
        ResultColumnLayout = resultColumnLayout;
        checked
        {
            TransferCount = Accounts.Sum(account => account.PlannedTransferCount);
            ShareCount = Accounts.Sum(account => account.PlannedShareCount);
        }
    }

    public Guid ImportSessionId { get; }
    public DateTimeOffset ImportUpdatedAt { get; }
    public long ResourceCount { get; }
    public long OperationCount { get; }
    public ImmutableArray<BatchAccountConfirmation> Accounts { get; }
    public StorageGrowthEstimate StorageEstimate { get; }
    public string ConfirmationToken { get; }
    public long TransferCount { get; }
    public long ShareCount { get; }
    public long TotalActionCount => checked(TransferCount + ShareCount);
    public ResultColumnLayout? ResultColumnLayout { get; }
    public int ResultColumnCount => ResultColumnLayout?.ColumnCount ?? 0;

    public override string ToString() =>
        $"BatchConfirmationSummary {{ ImportSessionId = {ImportSessionId}, ResourceCount = {ResourceCount}, OperationCount = {OperationCount}, TransferCount = {TransferCount}, ShareCount = {ShareCount}, Accounts = {Accounts.Length}, ResultColumnCount = {ResultColumnCount}, ConfirmationToken = [REDACTED] }}";
}

public sealed class BatchDraftValidationException(string message) : InvalidOperationException(message);

public sealed class BatchConfirmationException(string message) : InvalidOperationException(message);

public sealed class StorageCapacityException(long requiredBytes, long availableBytes)
    : InvalidOperationException("There is not enough local disk space to create the batch safely.")
{
    public long RequiredBytes { get; } = requiredBytes;
    public long AvailableBytes { get; } = availableBytes;
}
