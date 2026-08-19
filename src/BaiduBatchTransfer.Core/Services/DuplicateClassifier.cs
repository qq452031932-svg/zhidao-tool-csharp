using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Core.Services;

public sealed class DuplicateClassifier
{
    public DuplicateClassification Classify(
        TransferMode mode,
        DuplicateCandidate firstIncluded,
        DuplicateCandidate incoming)
    {
        ArgumentNullException.ThrowIfNull(firstIncluded);
        ArgumentNullException.ThrowIfNull(incoming);

        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        if (mode == TransferMode.Replication &&
            (firstIncluded.HasPerRowAccount || incoming.HasPerRowAccount))
        {
            return DuplicateClassification.Conflict(
                DuplicateConflictReason.ReplicationAccountSubsetUnsupported,
                firstIncluded.Note,
                "Replication does not support per-row accounts; ignore the account column or switch to distribution mode.");
        }

        if (!string.Equals(
                firstIncluded.NormalizedShareId,
                incoming.NormalizedShareId,
                StringComparison.Ordinal))
        {
            return DuplicateClassification.Distinct(
                incoming.Note,
                expandsByBatchAccounts: mode == TransferMode.Replication);
        }

        if (HaveEquivalentSourceSettings(firstIncluded, incoming))
        {
            return DuplicateClassification.Duplicate(
                firstIncluded.Note,
                expandsByBatchAccounts: mode == TransferMode.Replication);
        }

        return DuplicateClassification.Conflict(
            DuplicateConflictReason.SourceSettingsDiffer,
            firstIncluded.Note,
            "Rows for the same share use different source settings.");
    }

    private static bool HaveEquivalentSourceSettings(
        DuplicateCandidate left,
        DuplicateCandidate right) =>
        string.Equals(left.ExtractionCode, right.ExtractionCode, StringComparison.Ordinal) &&
        string.Equals(left.NormalizedTargetPath, right.NormalizedTargetPath, StringComparison.Ordinal) &&
        string.Equals(left.OuterFolderName, right.OuterFolderName, StringComparison.Ordinal) &&
        string.Equals(left.RequestedAccountName, right.RequestedAccountName, StringComparison.Ordinal) &&
        left.MappedAccountId == right.MappedAccountId;
}

public sealed class DuplicateCandidate
{
    public DuplicateCandidate(
        string normalizedShareId,
        string? extractionCode,
        string? normalizedTargetPath,
        string? outerFolderName,
        string? requestedAccountName,
        Guid? mappedAccountId,
        string? note)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedShareId);

        NormalizedShareId = normalizedShareId.Trim();
        ExtractionCode = PreserveOpaqueOptional(extractionCode);
        NormalizedTargetPath = NormalizeOptional(normalizedTargetPath);
        OuterFolderName = NormalizeOptional(outerFolderName);
        RequestedAccountName = NormalizeOptional(requestedAccountName);
        MappedAccountId = mappedAccountId;
        Note = note;
    }

    public string NormalizedShareId { get; }

    public string? ExtractionCode { get; }

    public string? NormalizedTargetPath { get; }

    public string? OuterFolderName { get; }

    public string? RequestedAccountName { get; }

    public Guid? MappedAccountId { get; }

    public string? Note { get; }

    public bool HasPerRowAccount => RequestedAccountName is not null || MappedAccountId is not null;

    public override string ToString() =>
        "DuplicateCandidate { NormalizedShareId = [REDACTED], ExtractionCode = [REDACTED], SourceSettings = [REDACTED], Note = [REDACTED] }";

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? PreserveOpaqueOptional(string? value) =>
        string.IsNullOrEmpty(value) ? null : value;
}

public sealed class DuplicateClassification
{
    private DuplicateClassification(
        DuplicateKind kind,
        DuplicateConflictReason conflictReason,
        string? noteToRetain,
        bool expandsByBatchAccounts,
        string message)
    {
        Kind = kind;
        ConflictReason = conflictReason;
        NoteToRetain = noteToRetain;
        ExpandsByBatchAccounts = expandsByBatchAccounts;
        Message = message;
    }

    public DuplicateKind Kind { get; }

    public DuplicateConflictReason ConflictReason { get; }

    public string? NoteToRetain { get; }

    public bool ExpandsByBatchAccounts { get; }

    public string Message { get; }

    internal static DuplicateClassification Distinct(
        string? noteToRetain,
        bool expandsByBatchAccounts) => new(
        DuplicateKind.Distinct,
        DuplicateConflictReason.None,
        noteToRetain,
        expandsByBatchAccounts,
        "The row represents a distinct source resource.");

    internal static DuplicateClassification Duplicate(
        string? noteToRetain,
        bool expandsByBatchAccounts) => new(
        DuplicateKind.Duplicate,
        DuplicateConflictReason.None,
        noteToRetain,
        expandsByBatchAccounts,
        "The row is an exact duplicate and can be folded.");

    internal static DuplicateClassification Conflict(
        DuplicateConflictReason conflictReason,
        string? noteToRetain,
        string message) => new(
        DuplicateKind.Conflict,
        conflictReason,
        noteToRetain,
        expandsByBatchAccounts: false,
        message);

    public override string ToString() =>
        $"DuplicateClassification {{ Kind = {Kind}, ConflictReason = {ConflictReason}, NoteToRetain = [REDACTED] }}";
}

public enum DuplicateKind
{
    Distinct,
    Duplicate,
    Conflict,
}

public enum DuplicateConflictReason
{
    None,
    SourceSettingsDiffer,
    ReplicationAccountSubsetUnsupported,
}
