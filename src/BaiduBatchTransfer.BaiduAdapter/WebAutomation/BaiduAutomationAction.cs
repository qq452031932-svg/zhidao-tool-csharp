namespace BaiduBatchTransfer.BaiduAdapter.WebAutomation;

public enum BaiduAutomationAction
{
    OpenOfficialUri = 0,
    SelectExactItems,
    ClickSave,
    ChooseDestination,
    ClickShare,
    ChoosePermanent,
    SetExtractionCode,
    CreateLink,
    ExportResult,
}

public enum BaiduAutomationDenialReason
{
    None = 0,
    UnknownAction,
    AmbiguousOrUnknownState,
    ActionNotAllowedForState,
    ProtocolVersionMismatch,
    SelectorVersionMismatch,
    AcceptanceMissing,
    BuildVersionMismatch,
    CandidateNotUnique,
    ObservationNotValidated,
    CatalogNotValidated,
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

public readonly record struct BaiduAutomationDecision(
    bool IsAllowed,
    BaiduAutomationDenialReason DenialReason)
{
    public static BaiduAutomationDecision Denied(BaiduAutomationDenialReason reason) =>
        new(false, reason);

    public static BaiduAutomationDecision Allowed() => new(true, BaiduAutomationDenialReason.None);
}
