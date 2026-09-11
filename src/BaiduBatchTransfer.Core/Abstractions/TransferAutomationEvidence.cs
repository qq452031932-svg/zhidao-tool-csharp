using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
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

public enum TransferTargetInventoryScope
{
    AllItems = 1,
}

public sealed record TransferTargetPreparationEvidence
{
    private const string ObservationFingerprintDomain = "transfer-target-preparation-evidence-v1";

    public TransferTargetPreparationEvidence(
        string targetExactId,
        RemotePath targetPath,
        int totalCount,
        bool reachedEnd,
        bool isStable,
        string targetGenerationToken,
        string baiduUserId,
        TransferTargetInventoryScope scope,
        WebAutomationBaseline baseline,
        string observationFingerprint)
    {
        TargetExactId = TransferEvidenceValidation.ExactId(targetExactId, nameof(targetExactId));
        TargetPath = ValidateTargetPath(targetPath);
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
        Scope = ValidateScope(scope);
        WebAutomationAttemptRecord.ValidateFingerprint(observationFingerprint, nameof(observationFingerprint));
        var expectedFingerprint = ComputeObservationFingerprintCore(
            TargetExactId, TargetPath, TotalCount, ReachedEnd, IsStable,
            TargetGenerationToken, BaiduUserId, Scope, Baseline);
        if (!string.Equals(observationFingerprint, expectedFingerprint, StringComparison.Ordinal))
            throw new ArgumentException(
                "Target observation fingerprint does not match the durable evidence.",
                nameof(observationFingerprint));
        ObservationFingerprint = observationFingerprint;
    }

    public string TargetExactId { get; }
    public RemotePath TargetPath { get; }
    public int TotalCount { get; }
    public bool ReachedEnd { get; }
    public bool IsStable { get; }
    public string TargetGenerationToken { get; }
    public string BaiduUserId { get; }
    public TransferTargetInventoryScope Scope { get; }
    public WebAutomationBaseline Baseline { get; }
    public string ObservationFingerprint { get; }

    public static string ComputeObservationFingerprint(
        string targetExactId,
        RemotePath targetPath,
        int totalCount,
        bool reachedEnd,
        bool isStable,
        string targetGenerationToken,
        string baiduUserId,
        TransferTargetInventoryScope scope,
        WebAutomationBaseline baseline)
    {
        var exactId = TransferEvidenceValidation.ExactId(targetExactId, nameof(targetExactId));
        var path = ValidateTargetPath(targetPath);
        ArgumentNullException.ThrowIfNull(baseline);
        if (totalCount != baseline.Items.Length)
            throw new ArgumentException("Target total must match the complete baseline.", nameof(totalCount));
        if (!reachedEnd)
            throw new ArgumentException("Target enumeration must reach its verified end.", nameof(reachedEnd));
        if (!isStable)
            throw new ArgumentException("Target enumeration must be stable.", nameof(isStable));
        var generation = TransferEvidenceValidation.BoundedText(
            targetGenerationToken, nameof(targetGenerationToken));
        var userId = TransferEvidenceValidation.BoundedText(baiduUserId, nameof(baiduUserId));
        var validatedScope = ValidateScope(scope);
        return ComputeObservationFingerprintCore(
            exactId, path, totalCount, reachedEnd, isStable,
            generation, userId, validatedScope, baseline);
    }

    public override string ToString() =>
        $"TransferTargetPreparationEvidence {{ ItemCount = {TotalCount}, Complete = {ReachedEnd}, Stable = {IsStable}, Values = [REDACTED] }}";

    private static string ComputeObservationFingerprintCore(
        string targetExactId,
        RemotePath targetPath,
        int totalCount,
        bool reachedEnd,
        bool isStable,
        string targetGenerationToken,
        string baiduUserId,
        TransferTargetInventoryScope scope,
        WebAutomationBaseline baseline)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        TransferEvidenceValidation.AppendStrict(hash, ObservationFingerprintDomain);
        TransferEvidenceValidation.AppendStrict(hash, targetExactId);
        TransferEvidenceValidation.AppendStrict(hash, targetPath.Value);
        TransferEvidenceValidation.AppendStrict(hash, totalCount.ToString(CultureInfo.InvariantCulture));
        TransferEvidenceValidation.AppendStrict(hash, reachedEnd ? "true" : "false");
        TransferEvidenceValidation.AppendStrict(hash, isStable ? "true" : "false");
        TransferEvidenceValidation.AppendStrict(hash, targetGenerationToken);
        TransferEvidenceValidation.AppendStrict(hash, baiduUserId);
        TransferEvidenceValidation.AppendStrict(hash, GetScopeMarker(scope));
        TransferEvidenceValidation.AppendStrict(hash, baseline.Fingerprint);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static RemotePath ValidateTargetPath(RemotePath targetPath)
    {
        ArgumentNullException.ThrowIfNull(targetPath);
        if (targetPath.Value.Length > 2_048 || targetPath.Value.Any(char.IsControl))
            throw new ArgumentException("The target path is invalid.", nameof(targetPath));
        return targetPath;
    }

    private static TransferTargetInventoryScope ValidateScope(TransferTargetInventoryScope scope)
    {
        if (scope != TransferTargetInventoryScope.AllItems)
            throw new ArgumentOutOfRangeException(nameof(scope));
        return scope;
    }

    private static string GetScopeMarker(TransferTargetInventoryScope scope) => scope switch
    {
        TransferTargetInventoryScope.AllItems => "all-items-v1",
        _ => throw new ArgumentOutOfRangeException(nameof(scope)),
    };
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
        : this(
            sourceFingerprint, targetExactId, targetBaselineFingerprint, baiduUserId, capability,
            markedSubmittingAtUtc, null, null)
    {
    }

    private TransferSubmissionEvidence(
        string sourceFingerprint,
        string targetExactId,
        string targetBaselineFingerprint,
        string baiduUserId,
        AdapterCapabilityVersion capability,
        DateTimeOffset markedSubmittingAtUtc,
        Guid? submissionId,
        string? readinessFingerprint)
    {
        ArgumentNullException.ThrowIfNull(sourceFingerprint);
        ArgumentNullException.ThrowIfNull(targetBaselineFingerprint);
        WebAutomationAttemptRecord.ValidateFingerprint(sourceFingerprint, nameof(sourceFingerprint));
        WebAutomationAttemptRecord.ValidateFingerprint(targetBaselineFingerprint, nameof(targetBaselineFingerprint));
        if (submissionId.HasValue != (readinessFingerprint is not null))
            throw new ArgumentException(
                "Submission ID and readiness fingerprint must either both be present or both be absent.",
                nameof(readinessFingerprint));
        if (submissionId == Guid.Empty)
            throw new ArgumentException("A locally bound submission requires a nonempty ID.", nameof(submissionId));
        if (readinessFingerprint is not null)
            WebAutomationAttemptRecord.ValidateFingerprint(readinessFingerprint, nameof(readinessFingerprint));
        SourceFingerprint = sourceFingerprint;
        TargetExactId = TransferEvidenceValidation.BoundedText(targetExactId, nameof(targetExactId));
        TargetBaselineFingerprint = targetBaselineFingerprint;
        BaiduUserId = TransferEvidenceValidation.BoundedText(baiduUserId, nameof(baiduUserId));
        Capability = capability ?? throw new ArgumentNullException(nameof(capability));
        MarkedSubmittingAtUtc = markedSubmittingAtUtc.ToUniversalTime();
        SubmissionId = submissionId;
        ReadinessFingerprint = readinessFingerprint;
    }

    public static TransferSubmissionEvidence CreateBound(
        Guid submissionId,
        string readinessFingerprint,
        string sourceFingerprint,
        string targetExactId,
        string targetBaselineFingerprint,
        string baiduUserId,
        AdapterCapabilityVersion capability,
        DateTimeOffset markedSubmittingAtUtc) =>
        new(sourceFingerprint, targetExactId, targetBaselineFingerprint, baiduUserId, capability,
            markedSubmittingAtUtc, submissionId, readinessFingerprint);

    public string SourceFingerprint { get; }
    public string TargetExactId { get; }
    public string TargetBaselineFingerprint { get; }
    public string BaiduUserId { get; }
    public AdapterCapabilityVersion Capability { get; }
    public DateTimeOffset MarkedSubmittingAtUtc { get; }
    public Guid? SubmissionId { get; }
    public string? ReadinessFingerprint { get; }
    public bool IsLocallyBound => SubmissionId is not null && ReadinessFingerprint is not null;

    public override string ToString() =>
        "TransferSubmissionEvidence { Values = [REDACTED] }";
}

public sealed record TransferTargetConfirmationEvidence
{
    private TransferTargetConfirmationEvidence(
        Guid submissionId,
        string readinessFingerprint,
        string sourceFingerprint,
        string targetExactId,
        string targetBaselineFingerprint,
        string officialRequestId,
        string officialMappingFingerprint,
        string addedTargetExactId,
        string currentTargetInventoryFingerprint,
        string targetObservationFingerprint,
        DateTimeOffset confirmedAtUtc)
    {
        SubmissionId = submissionId;
        ReadinessFingerprint = readinessFingerprint;
        SourceFingerprint = sourceFingerprint;
        TargetExactId = targetExactId;
        TargetBaselineFingerprint = targetBaselineFingerprint;
        OfficialRequestId = officialRequestId;
        OfficialMappingFingerprint = officialMappingFingerprint;
        AddedTargetExactId = addedTargetExactId;
        CurrentTargetInventoryFingerprint = currentTargetInventoryFingerprint;
        TargetObservationFingerprint = targetObservationFingerprint;
        ConfirmedAtUtc = confirmedAtUtc;
    }

    public Guid SubmissionId { get; }
    public string ReadinessFingerprint { get; }
    public string SourceFingerprint { get; }
    public string TargetExactId { get; }
    public string TargetBaselineFingerprint { get; }
    public string OfficialRequestId { get; }
    public string OfficialMappingFingerprint { get; }
    public string AddedTargetExactId { get; }
    public string CurrentTargetInventoryFingerprint { get; }
    public string TargetObservationFingerprint { get; }
    public DateTimeOffset ConfirmedAtUtc { get; }

    public static TransferTargetConfirmationEvidence Create(
        TransferSubmissionEvidence submission,
        string officialRequestId,
        TransferIdentityMapping officialMapping,
        TransferTargetPreparationEvidence target,
        WebAutomationBaseline currentInventory,
        string targetObservationFingerprint,
        DateTimeOffset confirmedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentNullException.ThrowIfNull(officialMapping);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(currentInventory);
        if (!submission.IsLocallyBound)
            throw new ArgumentException("Target confirmation requires locally bound submission evidence.",
                nameof(submission));
        var requestId = TransferEvidenceValidation.BoundedText(officialRequestId, nameof(officialRequestId));
        ArgumentNullException.ThrowIfNull(targetObservationFingerprint);
        WebAutomationAttemptRecord.ValidateFingerprint(
            targetObservationFingerprint, nameof(targetObservationFingerprint));
        if (officialMapping.Entries.Length != 1)
            throw new ArgumentException("Target confirmation requires exactly one official source mapping.",
                nameof(officialMapping));
        if (!string.Equals(submission.TargetExactId, target.TargetExactId, StringComparison.Ordinal) ||
            !string.Equals(submission.TargetBaselineFingerprint, target.Baseline.Fingerprint,
                StringComparison.Ordinal))
            throw new ArgumentException("Submission evidence must bind the confirmed target baseline.",
                nameof(target));
        if (!string.Equals(submission.BaiduUserId, target.BaiduUserId, StringComparison.Ordinal))
            throw new ArgumentException("Submission and target evidence must belong to the same remote account.",
                nameof(target));
        var confirmedAt = confirmedAtUtc.ToUniversalTime();
        if (confirmedAt < submission.MarkedSubmittingAtUtc)
            throw new ArgumentException("Target confirmation cannot predate submission evidence.",
                nameof(confirmedAtUtc));

        var mappingEntry = officialMapping.Entries[0];
        var mappedSource = new RemoteItemIdentitySet([
            new RemoteItemIdentity(mappingEntry.SourceExactId, null),
        ]);
        if (!string.Equals(mappedSource.Fingerprint, submission.SourceFingerprint, StringComparison.Ordinal))
            throw new ArgumentException("Official mapping must cover the submitted source item.",
                nameof(officialMapping));

        var baselineIds = target.Baseline.Items
            .Select(item => item.ExactId)
            .ToHashSet(StringComparer.Ordinal);
        var currentIds = currentInventory.Items
            .Select(item => item.ExactId)
            .ToHashSet(StringComparer.Ordinal);
        if (!baselineIds.IsSubsetOf(currentIds))
            throw new ArgumentException("Target confirmation cannot remove baseline IDs.", nameof(currentInventory));
        var addedTargetIds = currentIds
            .Where(exactId => !baselineIds.Contains(exactId))
            .OrderBy(exactId => exactId, StringComparer.Ordinal)
            .ToArray();
        if (addedTargetIds.Length != 1)
            throw new ArgumentException("Target confirmation requires exactly one added target ID.",
                nameof(currentInventory));
        if (!string.Equals(mappingEntry.DestinationExactId, addedTargetIds[0], StringComparison.Ordinal))
            throw new ArgumentException("Official mapping must name the sole added target ID.",
                nameof(officialMapping));

        return new(
            submission.SubmissionId!.Value,
            submission.ReadinessFingerprint!,
            submission.SourceFingerprint,
            submission.TargetExactId,
            submission.TargetBaselineFingerprint,
            requestId,
            officialMapping.Fingerprint,
            addedTargetIds[0],
            currentInventory.Fingerprint,
            targetObservationFingerprint,
            confirmedAt);
    }

    public override string ToString() =>
        "TransferTargetConfirmationEvidence { Values = [REDACTED] }";
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
        if (CanonicalChildPath.Value.Length > 2_048 || CanonicalChildPath.Value.Any(char.IsControl))
            throw new ArgumentException("The canonical child path is invalid.", nameof(canonicalChildPath));
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
        BaiduErrorKind? errorKind,
        BaiduCandidateDiagnosticCode safeDiagnosticCode)
    {
        Status = status;
        Directory = directory;
        ConfirmedPartialPath = confirmedPartialPath;
        ErrorKind = errorKind;
        SafeDiagnosticCode = safeDiagnosticCode;
    }

    public TransferDirectoryPreparationStatus Status { get; }
    public RemoteDirectory? Directory { get; }
    public RemotePath? ConfirmedPartialPath { get; }
    public BaiduErrorKind? ErrorKind { get; }
    public BaiduCandidateDiagnosticCode SafeDiagnosticCode { get; }

    public static TransferDirectoryPreparationOutcome Prepared(RemoteDirectory directory) =>
        new(TransferDirectoryPreparationStatus.Prepared,
            directory ?? throw new ArgumentNullException(nameof(directory)), null, null,
            BaiduCandidateDiagnosticCode.None);

    public static TransferDirectoryPreparationOutcome NeedsAttention(
        RemotePath confirmedPartialPath,
        BaiduErrorKind errorKind,
        BaiduCandidateDiagnosticCode safeDiagnosticCode = BaiduCandidateDiagnosticCode.None)
    {
        ArgumentNullException.ThrowIfNull(confirmedPartialPath);
        if (confirmedPartialPath.Value.Length > 2_048 || confirmedPartialPath.Value.Any(char.IsControl))
            throw new ArgumentException("The confirmed partial path contains control characters.",
                nameof(confirmedPartialPath));
        if (!Enum.IsDefined(safeDiagnosticCode))
            throw new ArgumentOutOfRangeException(nameof(safeDiagnosticCode));
        if (safeDiagnosticCode != BaiduCandidateDiagnosticCode.None &&
            errorKind != BaiduErrorKind.NotValidated)
            throw new ArgumentException(
                "Candidate diagnostics are valid only for unvalidated candidate observations.",
                nameof(safeDiagnosticCode));
        return new(TransferDirectoryPreparationStatus.NeedsAttention, null,
            confirmedPartialPath, errorKind, safeDiagnosticCode);
    }

    public override string ToString() =>
        $"TransferDirectoryPreparationOutcome {{ Status = {Status}, Values = [REDACTED] }}";
}

internal static class TransferEvidenceValidation
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

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
        Append(hash, bytes);
    }

    public static void AppendStrict(IncrementalHash hash, string value)
    {
        byte[] bytes;
        try
        {
            bytes = StrictUtf8.GetBytes(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException("Transfer evidence text must be valid Unicode.", nameof(value), exception);
        }
        Append(hash, bytes);
    }

    private static void Append(IncrementalHash hash, byte[] bytes)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
