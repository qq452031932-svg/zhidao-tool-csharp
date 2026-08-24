namespace BaiduBatchTransfer.BaiduAdapter.WebAutomation;

public enum BaiduPageState
{
    Unknown = 0,
    ExternalShareReady,
    OwnDriveReady,
    SaveDestinationReady,
    TransferAccepted,
    ShareDialogReady,
    ShareResultReady,
    UserVerificationRequired,
}

public enum BaiduObservationProvenance
{
    Synthetic = 0,
    FieldEvidence,
}

public enum BaiduProtocolValidationStatus
{
    NotValidated = 0,
    Validated,
}

public enum BaiduObservationValidationStatus
{
    NotFieldValidated = 0,
    FieldValidated,
}

public sealed record BaiduPageObservation(
    int SchemaVersion,
    BaiduObservationProvenance Provenance,
    BaiduObservationValidationStatus ValidationStatus,
    IReadOnlyList<string> PageRoles,
    IReadOnlyList<string> VisibleSemanticLabels,
    IReadOnlyDictionary<string, string> StableAttributeTokens,
    IReadOnlyDictionary<string, int> CandidateCounts,
    IReadOnlyList<string> IdentityPlaceholders)
{
    public override string ToString() =>
        $"BaiduPageObservation {{ SchemaVersion = {SchemaVersion}, Provenance = {Provenance}, ValidationStatus = {ValidationStatus}, PageRoles = [REDACTED] (Count = {PageRoles?.Count ?? 0}), VisibleSemanticLabels = [REDACTED] (Count = {VisibleSemanticLabels?.Count ?? 0}), StableAttributeTokens = [REDACTED] (Count = {StableAttributeTokens?.Count ?? 0}), CandidateCounts = [REDACTED] (Count = {CandidateCounts?.Count ?? 0}), IdentityPlaceholders = [REDACTED] (Count = {IdentityPlaceholders?.Count ?? 0}) }}";
}

public sealed record BaiduPageParseResult(
    BaiduPageState State,
    bool IsUnambiguous,
    BaiduProtocolValidationStatus ValidationStatus,
    string SafeReason)
{
    public override string ToString() =>
        $"BaiduPageParseResult {{ State = {State}, IsUnambiguous = {IsUnambiguous}, ValidationStatus = {ValidationStatus}, SafeReason = [REDACTED] }}";
}
