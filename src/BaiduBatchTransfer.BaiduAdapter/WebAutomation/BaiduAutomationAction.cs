using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.BaiduAdapter.WebAutomation;

public enum BaiduAutomationAction
{
    Unspecified = 0,
    OpenOfficialUri,
    SelectExactItems,
    ClickSave,
    ChooseDestination,
    ClickShare,
    ChoosePermanent,
    SetExtractionCode,
    CreateLink,
    ExportResult,
    SubmitExtraction,
    OpenDestinationPicker,
    EnterDirectory,
    CreateDirectory,
    ConfirmDirectoryCreation,
    CancelDestinationPicker,
    ClickConfirmTransfer,
}

public enum BaiduAutomationDenialReason
{
    None = 0,
    UnknownAction,
    AmbiguousOrUnknownState,
    ActionNotAllowedForState,
    ActionNotAllowedForNavigation,
    OfficialUriNotAllowed,
    ProtocolVersionMismatch,
    SelectorVersionMismatch,
    AcceptanceMissing,
    BuildVersionMismatch,
    CandidateNotUnique,
    ObservationNotValidated,
    FieldEvidenceNotValidated,
    CatalogNotValidated,
}

public class BaiduAutomationNotValidatedException(
    BaiduAutomationDenialReason denialReason,
    string message,
    BaiduCandidateDiagnosticCode safeDiagnosticCode = BaiduCandidateDiagnosticCode.None) :
    InvalidOperationException(message)
{
    public BaiduAutomationDenialReason DenialReason { get; } = denialReason;
    public BaiduCandidateDiagnosticCode SafeDiagnosticCode { get; } = safeDiagnosticCode;
}

public sealed record BaiduAutomationAcceptance(
    bool IsAccepted,
    string ProtocolVersion,
    string SelectorVersion,
    string ApplicationBuildVersion)
{
    public override string ToString() =>
        $"BaiduAutomationAcceptance {{ IsAccepted = {IsAccepted}, ProtocolVersion = [REDACTED], SelectorVersion = [REDACTED], ApplicationBuildVersion = [REDACTED] }}";
}

public sealed record BaiduAutomationActionContext(
    BaiduPageParseResult Observation,
    string ProtocolVersion,
    string SelectorVersion,
    string ApplicationBuildVersion,
    BaiduAutomationAcceptance Acceptance,
    int CandidateCount)
{
    public override string ToString() =>
        $"BaiduAutomationActionContext {{ Observation = {Observation}, ProtocolVersion = [REDACTED], SelectorVersion = [REDACTED], ApplicationBuildVersion = [REDACTED], Acceptance = {Acceptance}, CandidateCount = {CandidateCount} }}";
}

public sealed record BaiduNavigationAuthorizationRequest(
    BaiduAutomationAction Action,
    Uri OfficialUri,
    string ProtocolVersion,
    string SelectorVersion,
    string ApplicationBuildVersion,
    BaiduAutomationAcceptance Acceptance,
    BaiduProtocolValidationStatus FieldEvidenceValidationStatus,
    BaiduAcceptanceOperationScope? AcceptanceScope = null)
{
    public override string ToString() =>
        $"BaiduNavigationAuthorizationRequest {{ Action = {Action}, OfficialUri = [REDACTED], ProtocolVersion = [REDACTED], SelectorVersion = [REDACTED], ApplicationBuildVersion = [REDACTED], Acceptance = {Acceptance}, FieldEvidenceValidationStatus = {FieldEvidenceValidationStatus} }}";
}

public sealed record BaiduAcceptanceOperationScope(
    string AdapterKind,
    Guid AccountId,
    Guid OperationId,
    int Generation,
    Guid BatchId);

public readonly record struct BaiduAutomationDecision(
    bool IsAllowed,
    BaiduAutomationDenialReason DenialReason)
{
    public static BaiduAutomationDecision Denied(BaiduAutomationDenialReason reason) =>
        new(false, reason);

    public static BaiduAutomationDecision Allowed() => new(true, BaiduAutomationDenialReason.None);
}
