using System.Collections.Immutable;

namespace BaiduBatchTransfer.BaiduAdapter.WebAutomation;

public enum BaiduSelectorStrategyKind
{
    SemanticLabel = 0,
    AccessibilityRole,
    StableAttribute,
}

public sealed record BaiduSelectorCandidateStrategy(
    BaiduSelectorStrategyKind Kind,
    string Token)
{
    public override string ToString() =>
        $"BaiduSelectorCandidateStrategy {{ Kind = {Kind}, Token = [REDACTED] }}";
}

public sealed record BaiduSelectorDefinition(
    BaiduAutomationAction Action,
    ImmutableArray<BaiduSelectorCandidateStrategy> CandidateStrategies,
    ImmutableArray<string> ExecutableLocators)
{
    public override string ToString() =>
        $"BaiduSelectorDefinition {{ Action = {Action}, CandidateStrategyCount = {CandidateStrategies.Length}, ExecutableLocatorCount = {ExecutableLocators.Length} }}";
}

public sealed class BaiduSelectorCatalog
{
    public const string ProtocolVersion = "baidu-visible-web-v1";
    public const string SelectorVersion = "baidu-selectors-v1";

    private static readonly ImmutableDictionary<BaiduPageState, ImmutableHashSet<BaiduAutomationAction>>
        AllowedActions = new Dictionary<BaiduPageState, ImmutableHashSet<BaiduAutomationAction>>
        {
            [BaiduPageState.ExternalShareReady] =
                [BaiduAutomationAction.SelectExactItems, BaiduAutomationAction.ClickSave],
            [BaiduPageState.OwnDriveReady] =
                [BaiduAutomationAction.SelectExactItems, BaiduAutomationAction.ClickShare],
            [BaiduPageState.SaveDestinationReady] =
                [BaiduAutomationAction.ChooseDestination, BaiduAutomationAction.ClickSave],
            [BaiduPageState.TransferAccepted] = [],
            [BaiduPageState.ShareDialogReady] =
                [
                    BaiduAutomationAction.ChoosePermanent,
                    BaiduAutomationAction.SetExtractionCode,
                    BaiduAutomationAction.CreateLink,
                ],
            [BaiduPageState.ShareResultReady] = [BaiduAutomationAction.ExportResult],
            [BaiduPageState.UserVerificationRequired] = [],
        }.ToImmutableDictionary();

    private static readonly ImmutableArray<BaiduSelectorDefinition> NotValidatedDefinitions =
        Enum.GetValues<BaiduAutomationAction>()
            .Select(action => new BaiduSelectorDefinition(
                action,
                CandidateStrategiesFor(action),
                ImmutableArray<string>.Empty))
            .ToImmutableArray();

    private BaiduSelectorCatalog()
    {
    }

    public static BaiduSelectorCatalog Production { get; } = new();

    public BaiduProtocolValidationStatus ValidationStatus =>
        BaiduProtocolValidationStatus.NotValidated;

    public ImmutableArray<BaiduSelectorDefinition> Definitions => NotValidatedDefinitions;

    public string HistoricalReferenceNotice =>
        "用户提供的历史分享脚本仅作为不可信、不可执行的参考；未据此启用生产定位器。";

    public bool IsActionAllowedForState(BaiduPageState state, BaiduAutomationAction action)
    {
        if (!Enum.IsDefined(state) || !Enum.IsDefined(action))
            return false;

        if (action is BaiduAutomationAction.OpenOfficialUri)
            return true;

        return AllowedActions.TryGetValue(state, out var actions) && actions.Contains(action);
    }

    public BaiduAutomationDecision Authorize(
        BaiduAutomationActionContext? context,
        BaiduAutomationAction action)
    {
        if (!Enum.IsDefined(action))
            return BaiduAutomationDecision.Denied(BaiduAutomationDenialReason.UnknownAction);

        if (context?.Observation is null ||
            !context.Observation.IsUnambiguous ||
            context.Observation.State is BaiduPageState.Unknown ||
            !Enum.IsDefined(context.Observation.State))
        {
            return BaiduAutomationDecision.Denied(
                BaiduAutomationDenialReason.AmbiguousOrUnknownState);
        }

        if (!IsActionAllowedForState(context.Observation.State, action))
            return BaiduAutomationDecision.Denied(BaiduAutomationDenialReason.ActionNotAllowedForState);

        if (!string.Equals(context.ProtocolVersion, ProtocolVersion, StringComparison.Ordinal) ||
            !string.Equals(context.Acceptance?.ProtocolVersion, ProtocolVersion, StringComparison.Ordinal))
        {
            return BaiduAutomationDecision.Denied(BaiduAutomationDenialReason.ProtocolVersionMismatch);
        }

        if (!string.Equals(context.SelectorVersion, SelectorVersion, StringComparison.Ordinal) ||
            !string.Equals(context.Acceptance?.SelectorVersion, SelectorVersion, StringComparison.Ordinal))
        {
            return BaiduAutomationDecision.Denied(BaiduAutomationDenialReason.SelectorVersionMismatch);
        }

        if (context.Acceptance is not { IsAccepted: true })
            return BaiduAutomationDecision.Denied(BaiduAutomationDenialReason.AcceptanceMissing);

        if (string.IsNullOrWhiteSpace(context.ApplicationBuildVersion) ||
            !string.Equals(
                context.ApplicationBuildVersion,
                context.Acceptance.ApplicationBuildVersion,
                StringComparison.Ordinal))
        {
            return BaiduAutomationDecision.Denied(BaiduAutomationDenialReason.BuildVersionMismatch);
        }

        if (action is not BaiduAutomationAction.OpenOfficialUri && context.CandidateCount != 1)
            return BaiduAutomationDecision.Denied(BaiduAutomationDenialReason.CandidateNotUnique);

        if (ValidationStatus is not BaiduProtocolValidationStatus.Validated)
            return BaiduAutomationDecision.Denied(BaiduAutomationDenialReason.CatalogNotValidated);

        if (context.Observation.ValidationStatus is not BaiduProtocolValidationStatus.Validated)
            return BaiduAutomationDecision.Denied(BaiduAutomationDenialReason.ObservationNotValidated);

        return BaiduAutomationDecision.Allowed();
    }

    public override string ToString() =>
        $"BaiduSelectorCatalog {{ ProtocolVersion = [REDACTED], SelectorVersion = [REDACTED], ValidationStatus = {ValidationStatus}, DefinitionCount = {Definitions.Length}, ExecutableLocatorCount = 0, HistoricalReference = [REDACTED] }}";

    private static ImmutableArray<BaiduSelectorCandidateStrategy> CandidateStrategiesFor(
        BaiduAutomationAction action) =>
        [
            new(BaiduSelectorStrategyKind.SemanticLabel, $"{ActionToken(action)}-semantic"),
            new(BaiduSelectorStrategyKind.AccessibilityRole, $"{ActionToken(action)}-role"),
            new(BaiduSelectorStrategyKind.StableAttribute, $"{ActionToken(action)}-attribute"),
        ];

    private static string ActionToken(BaiduAutomationAction action) => action switch
    {
        BaiduAutomationAction.OpenOfficialUri => "official-navigation",
        BaiduAutomationAction.SelectExactItems => "exact-item-selection",
        BaiduAutomationAction.ClickSave => "save-action",
        BaiduAutomationAction.ChooseDestination => "destination-choice",
        BaiduAutomationAction.ClickShare => "share-action",
        BaiduAutomationAction.ChoosePermanent => "permanent-choice",
        BaiduAutomationAction.SetExtractionCode => "code-setting",
        BaiduAutomationAction.CreateLink => "create-link-action",
        BaiduAutomationAction.ExportResult => "export-result-action",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };
}
