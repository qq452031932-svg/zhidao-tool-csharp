using System.Collections.Immutable;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Jobs;

public sealed class BatchAccountDraft
{
    public BatchAccountDraft(
        Guid accountId,
        int priorityOrder,
        string defaultTargetPath,
        int targetItemCount,
        int targetSuccessCount)
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
    }

    public Guid AccountId { get; }
    public int PriorityOrder { get; }
    public string DefaultTargetPath { get; }
    public int TargetItemCount { get; }
    public int TargetSuccessCount { get; }

    public override string ToString() =>
        $"BatchAccountDraft {{ AccountId = [REDACTED], PriorityOrder = {PriorityOrder}, DefaultTargetPath = [REDACTED], TargetItemCount = {TargetItemCount}, TargetSuccessCount = {TargetSuccessCount} }}";
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
        bool ignoreMappedAccountsForReplication)
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
    bool HasSufficientSpace);

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
        int reachableTargetSuccessCount = -1)
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

    public override string ToString() =>
        $"BatchAccountConfirmation {{ AccountId = [REDACTED], PriorityOrder = {PriorityOrder}, Path = [REDACTED], TargetItemCount = {TargetItemCount}, TargetSuccessCount = {TargetSuccessCount}, EffectiveTargetSuccessCount = {EffectiveTargetSuccessCount}, FulfilledCount = {FulfilledCount}, ReservedSlotCount = {ReservedSlotCount}, LockedItemCount = {LockedItemCount}, ExclusiveCandidateCount = {ExclusiveCandidateCount}, UnusedCandidateCount = {UnusedCandidateCount}, NewExclusiveCandidateCount = {NewExclusiveCandidateCount}, ReachableTargetSuccessCount = {ReachableTargetSuccessCount} }}";
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
        string confirmationToken)
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
    }

    public Guid ImportSessionId { get; }
    public DateTimeOffset ImportUpdatedAt { get; }
    public long ResourceCount { get; }
    public long OperationCount { get; }
    public ImmutableArray<BatchAccountConfirmation> Accounts { get; }
    public StorageGrowthEstimate StorageEstimate { get; }
    public string ConfirmationToken { get; }

    public override string ToString() =>
        $"BatchConfirmationSummary {{ ImportSessionId = {ImportSessionId}, ResourceCount = {ResourceCount}, OperationCount = {OperationCount}, Accounts = {Accounts.Length}, ConfirmationToken = [REDACTED] }}";
}

public sealed class BatchDraftValidationException(string message) : InvalidOperationException(message);

public sealed class BatchConfirmationException(string message) : InvalidOperationException(message);

public sealed class StorageCapacityException(long requiredBytes, long availableBytes)
    : InvalidOperationException("There is not enough local disk space to create the batch safely.")
{
    public long RequiredBytes { get; } = requiredBytes;
    public long AvailableBytes { get; } = availableBytes;
}
