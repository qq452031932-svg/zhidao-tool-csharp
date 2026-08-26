using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Core.Abstractions;

public sealed record TransferSourceEvidence
{
    public TransferSourceEvidence(
        RemoteItemIdentitySet items,
        int totalCount,
        bool reachedEnd,
        bool isStable,
        string generationToken,
        string baiduUserId,
        AdapterCapabilityVersion capability,
        string observationFingerprint)
    {
        Items = items ?? throw new ArgumentNullException(nameof(items));
        if (totalCount != items.Count) throw new ArgumentException("Source total must match exact items.", nameof(totalCount));
        if (!reachedEnd) throw new ArgumentException("Source enumeration must reach its verified end.", nameof(reachedEnd));
        if (!isStable) throw new ArgumentException("Source enumeration must be stable.", nameof(isStable));
        TotalCount = totalCount;
        ReachedEnd = reachedEnd;
        IsStable = isStable;
        GenerationToken = TransferEvidenceValidation.BoundedText(generationToken, nameof(generationToken));
        BaiduUserId = TransferEvidenceValidation.BoundedText(baiduUserId, nameof(baiduUserId));
        Capability = capability ?? throw new ArgumentNullException(nameof(capability));
        WebAutomationAttemptRecord.ValidateFingerprint(observationFingerprint, nameof(observationFingerprint));
        ObservationFingerprint = observationFingerprint;
    }

    public RemoteItemIdentitySet Items { get; }
    public int TotalCount { get; }
    public bool ReachedEnd { get; }
    public bool IsStable { get; }
    public string GenerationToken { get; }
    public string BaiduUserId { get; }
    public AdapterCapabilityVersion Capability { get; }
    public string ObservationFingerprint { get; }

    public override string ToString() =>
        $"TransferSourceEvidence {{ ItemCount = {Items.Count}, Complete = {ReachedEnd}, Stable = {IsStable}, Values = [REDACTED] }}";
}

public sealed record TransferTargetPreparationEvidence
{
    public TransferTargetPreparationEvidence(
        string targetExactId,
        RemotePath targetPath,
        int totalCount,
        bool reachedEnd,
        bool isStable,
        string targetGenerationToken,
        string baiduUserId,
        WebAutomationBaseline baseline,
        string observationFingerprint)
    {
        TargetExactId = TransferEvidenceValidation.BoundedText(targetExactId, nameof(targetExactId));
        TargetPath = targetPath ?? throw new ArgumentNullException(nameof(targetPath));
        Baseline = baseline ?? throw new ArgumentNullException(nameof(baseline));
        if (totalCount != baseline.Items.Length)
            throw new ArgumentException("Target total must match the complete baseline.", nameof(totalCount));
        if (!reachedEnd) throw new ArgumentException("Target enumeration must reach its verified end.", nameof(reachedEnd));
        if (!isStable) throw new ArgumentException("Target enumeration must be stable.", nameof(isStable));
        TotalCount = totalCount;
        ReachedEnd = reachedEnd;
        IsStable = isStable;
        TargetGenerationToken = TransferEvidenceValidation.BoundedText(
            targetGenerationToken, nameof(targetGenerationToken));
        BaiduUserId = TransferEvidenceValidation.BoundedText(baiduUserId, nameof(baiduUserId));
        WebAutomationAttemptRecord.ValidateFingerprint(observationFingerprint, nameof(observationFingerprint));
        ObservationFingerprint = observationFingerprint;
    }

    public string TargetExactId { get; }
    public RemotePath TargetPath { get; }
    public int TotalCount { get; }
    public bool ReachedEnd { get; }
    public bool IsStable { get; }
    public string TargetGenerationToken { get; }
    public string BaiduUserId { get; }
    public WebAutomationBaseline Baseline { get; }
    public string ObservationFingerprint { get; }

    public override string ToString() =>
        $"TransferTargetPreparationEvidence {{ ItemCount = {TotalCount}, Complete = {ReachedEnd}, Stable = {IsStable}, Values = [REDACTED] }}";
}

public sealed record TransferPreparationEvidence(
    TransferSourceEvidence Source,
    TransferTargetPreparationEvidence Target)
{
    public TransferPreparationEvidence Validate()
    {
        ArgumentNullException.ThrowIfNull(Source);
        ArgumentNullException.ThrowIfNull(Target);
        if (!string.Equals(Source.BaiduUserId, Target.BaiduUserId, StringComparison.Ordinal))
            throw new ArgumentException("Source and target evidence must belong to the same remote account.");
        return this;
    }

    public override string ToString() =>
        "TransferPreparationEvidence { Source = [REDACTED], Target = [REDACTED] }";
}

public sealed record TransferSubmissionEvidence
{
    public TransferSubmissionEvidence(
        string sourceFingerprint,
        string targetExactId,
        string targetBaselineFingerprint,
        string baiduUserId,
        AdapterCapabilityVersion capability,
        DateTimeOffset markedSubmittingAtUtc)
    {
        WebAutomationAttemptRecord.ValidateFingerprint(sourceFingerprint, nameof(sourceFingerprint));
        WebAutomationAttemptRecord.ValidateFingerprint(targetBaselineFingerprint, nameof(targetBaselineFingerprint));
        SourceFingerprint = sourceFingerprint;
        TargetExactId = TransferEvidenceValidation.BoundedText(targetExactId, nameof(targetExactId));
        TargetBaselineFingerprint = targetBaselineFingerprint;
        BaiduUserId = TransferEvidenceValidation.BoundedText(baiduUserId, nameof(baiduUserId));
        Capability = capability ?? throw new ArgumentNullException(nameof(capability));
        MarkedSubmittingAtUtc = markedSubmittingAtUtc.ToUniversalTime();
    }

    public string SourceFingerprint { get; }
    public string TargetExactId { get; }
    public string TargetBaselineFingerprint { get; }
    public string BaiduUserId { get; }
    public AdapterCapabilityVersion Capability { get; }
    public DateTimeOffset MarkedSubmittingAtUtc { get; }

    public override string ToString() =>
        "TransferSubmissionEvidence { Values = [REDACTED] }";
}

public sealed record TransferIdentityMappingEntry
{
    public TransferIdentityMappingEntry(string sourceExactId, string destinationExactId)
    {
        SourceExactId = TransferEvidenceValidation.ExactId(sourceExactId, nameof(sourceExactId));
        DestinationExactId = TransferEvidenceValidation.ExactId(destinationExactId, nameof(destinationExactId));
    }

    public string SourceExactId { get; }
    public string DestinationExactId { get; }

    public override string ToString() => "TransferIdentityMappingEntry { Values = [REDACTED] }";
}

public sealed record TransferIdentityMapping
{
    public TransferIdentityMapping(IEnumerable<TransferIdentityMappingEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var snapshot = entries
            .Select(entry => entry ?? throw new ArgumentException("Mapping entries cannot be null.", nameof(entries)))
            .OrderBy(entry => entry.SourceExactId, StringComparer.Ordinal)
            .ThenBy(entry => entry.DestinationExactId, StringComparer.Ordinal)
            .ToImmutableArray();
        if (snapshot.IsEmpty) throw new ArgumentException("At least one mapping entry is required.", nameof(entries));
        if (snapshot.Select(entry => entry.SourceExactId).Distinct(StringComparer.Ordinal).Count() != snapshot.Length ||
            snapshot.Select(entry => entry.DestinationExactId).Distinct(StringComparer.Ordinal).Count() != snapshot.Length)
            throw new ArgumentException("Transfer mapping must be one-to-one.", nameof(entries));
        Entries = snapshot;
        Fingerprint = ComputeFingerprint(snapshot);
    }

    public ImmutableArray<TransferIdentityMappingEntry> Entries { get; }
    public string Fingerprint { get; }

    public bool Covers(RemoteItemIdentitySet source) =>
        source is not null && Entries.Select(entry => entry.SourceExactId)
            .SequenceEqual(source.Items.Select(item => item.ExactId), StringComparer.Ordinal);

    public override string ToString() =>
        $"TransferIdentityMapping {{ Count = {Entries.Length}, Values = [REDACTED] }}";

    private static string ComputeFingerprint(IEnumerable<TransferIdentityMappingEntry> entries)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        TransferEvidenceValidation.Append(hash, "transfer-identity-mapping-v1");
        foreach (var entry in entries)
        {
            TransferEvidenceValidation.Append(hash, entry.SourceExactId);
            TransferEvidenceValidation.Append(hash, entry.DestinationExactId);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}

public enum TransferDirectoryMutationState
{
    Intent,
    Confirmed,
    OutcomeUnknown,
}

public sealed record TransferDirectoryMutationRecord
{
    public TransferDirectoryMutationRecord(
        Guid operationId,
        Guid accountId,
        int generation,
        int segmentOrdinal,
        string parentExactId,
        string? childExactId,
        RemotePath canonicalChildPath,
        string listingGenerationToken,
        string baiduUserId,
        AdapterCapabilityVersion capability,
        TransferDirectoryMutationState state,
        long owningAttemptRevision)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An operation is required.", nameof(operationId));
        if (accountId == Guid.Empty) throw new ArgumentException("An account is required.", nameof(accountId));
        if (generation < 1) throw new ArgumentOutOfRangeException(nameof(generation));
        if (segmentOrdinal < 0) throw new ArgumentOutOfRangeException(nameof(segmentOrdinal));
        if (owningAttemptRevision < 0) throw new ArgumentOutOfRangeException(nameof(owningAttemptRevision));
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
        if (state == TransferDirectoryMutationState.Confirmed && string.IsNullOrWhiteSpace(childExactId))
            throw new ArgumentException("A confirmed directory mutation requires its exact child ID.", nameof(childExactId));
        OperationId = operationId;
        AccountId = accountId;
        Generation = generation;
        SegmentOrdinal = segmentOrdinal;
        ParentExactId = TransferEvidenceValidation.ExactId(parentExactId, nameof(parentExactId));
        ChildExactId = childExactId is null ? null : TransferEvidenceValidation.ExactId(childExactId, nameof(childExactId));
        CanonicalChildPath = canonicalChildPath ?? throw new ArgumentNullException(nameof(canonicalChildPath));
        ListingGenerationToken = TransferEvidenceValidation.BoundedText(
            listingGenerationToken, nameof(listingGenerationToken));
        BaiduUserId = TransferEvidenceValidation.BoundedText(baiduUserId, nameof(baiduUserId));
        Capability = capability ?? throw new ArgumentNullException(nameof(capability));
        State = state;
        OwningAttemptRevision = owningAttemptRevision;
    }

    public Guid OperationId { get; }
    public Guid AccountId { get; }
    public int Generation { get; }
    public int SegmentOrdinal { get; }
    public string ParentExactId { get; }
    public string? ChildExactId { get; }
    public RemotePath CanonicalChildPath { get; }
    public string ListingGenerationToken { get; }
    public string BaiduUserId { get; }
    public AdapterCapabilityVersion Capability { get; }
    public TransferDirectoryMutationState State { get; }
    public long OwningAttemptRevision { get; }

    public override string ToString() =>
        $"TransferDirectoryMutationRecord {{ Generation = {Generation}, SegmentOrdinal = {SegmentOrdinal}, State = {State}, Values = [REDACTED] }}";
}

public enum TransferDirectoryPreparationStatus
{
    Prepared,
    NeedsAttention,
}

public sealed record TransferDirectoryPreparationOutcome
{
    private TransferDirectoryPreparationOutcome(
        TransferDirectoryPreparationStatus status,
        RemoteDirectory? directory,
        RemotePath? confirmedPartialPath,
        BaiduErrorKind? errorKind)
    {
        Status = status;
        Directory = directory;
        ConfirmedPartialPath = confirmedPartialPath;
        ErrorKind = errorKind;
    }

    public TransferDirectoryPreparationStatus Status { get; }
    public RemoteDirectory? Directory { get; }
    public RemotePath? ConfirmedPartialPath { get; }
    public BaiduErrorKind? ErrorKind { get; }

    public static TransferDirectoryPreparationOutcome Prepared(RemoteDirectory directory) =>
        new(TransferDirectoryPreparationStatus.Prepared,
            directory ?? throw new ArgumentNullException(nameof(directory)), null, null);

    public static TransferDirectoryPreparationOutcome NeedsAttention(
        RemotePath confirmedPartialPath,
        BaiduErrorKind errorKind) =>
        new(TransferDirectoryPreparationStatus.NeedsAttention, null,
            confirmedPartialPath ?? throw new ArgumentNullException(nameof(confirmedPartialPath)), errorKind);

    public override string ToString() =>
        $"TransferDirectoryPreparationOutcome {{ Status = {Status}, Values = [REDACTED] }}";
}

internal static class TransferEvidenceValidation
{
    public static string BoundedText(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Length > 512 || normalized.Any(char.IsControl))
            throw new ArgumentException("Transfer evidence text is invalid.", parameterName);
        return normalized;
    }

    public static string ExactId(string value, string parameterName)
    {
        var normalized = BoundedText(value, parameterName);
        _ = new RemoteItemIdentitySet([new RemoteItemIdentity(normalized, null)]);
        return normalized;
    }

    public static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
