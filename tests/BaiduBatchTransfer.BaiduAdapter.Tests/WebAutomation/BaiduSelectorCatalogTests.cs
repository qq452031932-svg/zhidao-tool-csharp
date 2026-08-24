using System.Text.Json;
using System.Text.Json.Serialization;
using BaiduBatchTransfer.BaiduAdapter.WebAutomation;

namespace BaiduBatchTransfer.BaiduAdapter.Tests.WebAutomation;

public sealed class BaiduSelectorCatalogTests
{
    [Fact]
    public void ProductionCatalogIsNotValidatedAndHasNoExecutableLocators()
    {
        var catalog = BaiduSelectorCatalog.Production;

        Assert.Equal(BaiduProtocolValidationStatus.NotValidated, catalog.ValidationStatus);
        Assert.NotEmpty(catalog.Definitions);
        Assert.All(catalog.Definitions, definition =>
        {
            Assert.Empty(definition.ExecutableLocators);
            Assert.NotEmpty(definition.CandidateStrategies);
            Assert.All(
                definition.CandidateStrategies,
                strategy => Assert.Contains(
                    strategy.Kind,
                    new[]
                    {
                        BaiduSelectorStrategyKind.SemanticLabel,
                        BaiduSelectorStrategyKind.AccessibilityRole,
                        BaiduSelectorStrategyKind.StableAttribute,
                    }));
        });

        var description = catalog.ToString();
        Assert.DoesNotContain("xpath", description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("css", description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript", description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(@"\b\d+\s*,\s*\d+\b", description);
    }

    [Fact]
    public void CatalogContainsOnlyTheFixedAllowlistedActions()
    {
        var expected = new[]
        {
            BaiduAutomationAction.OpenOfficialUri,
            BaiduAutomationAction.SelectExactItems,
            BaiduAutomationAction.ClickSave,
            BaiduAutomationAction.ChooseDestination,
            BaiduAutomationAction.ClickShare,
            BaiduAutomationAction.ChoosePermanent,
            BaiduAutomationAction.SetExtractionCode,
            BaiduAutomationAction.CreateLink,
            BaiduAutomationAction.ExportResult,
        };

        Assert.Equal(expected.Order(), BaiduSelectorCatalog.Production.Definitions.Select(x => x.Action).Order());
    }

    [Theory]
    [InlineData(BaiduPageState.ExternalShareReady, BaiduAutomationAction.ClickSave, true)]
    [InlineData(BaiduPageState.OwnDriveReady, BaiduAutomationAction.ClickShare, true)]
    [InlineData(BaiduPageState.SaveDestinationReady, BaiduAutomationAction.ChooseDestination, true)]
    [InlineData(BaiduPageState.ShareDialogReady, BaiduAutomationAction.CreateLink, true)]
    [InlineData(BaiduPageState.ShareResultReady, BaiduAutomationAction.ExportResult, true)]
    [InlineData(BaiduPageState.ExternalShareReady, BaiduAutomationAction.CreateLink, false)]
    [InlineData(BaiduPageState.UserVerificationRequired, BaiduAutomationAction.ClickSave, false)]
    public void StateTransitionTableIsExplicit(
        BaiduPageState state,
        BaiduAutomationAction action,
        bool expected)
    {
        Assert.Equal(expected, BaiduSelectorCatalog.Production.IsActionAllowedForState(state, action));
    }

    [Fact]
    public void Authorize_RejectsOtherwiseValidWriteBecauseCatalogHasNoFieldValidation()
    {
        var observation = Load("share-dialog-ready.synthetic.json");
        var parsed = BaiduPageObservationParser.Parse(observation);
        var acceptance = new BaiduAutomationAcceptance(
            true,
            BaiduSelectorCatalog.ProtocolVersion,
            BaiduSelectorCatalog.SelectorVersion,
            "build-a");
        var context = new BaiduAutomationActionContext(
            parsed,
            BaiduSelectorCatalog.ProtocolVersion,
            BaiduSelectorCatalog.SelectorVersion,
            "build-a",
            acceptance,
            1);

        var decision = BaiduSelectorCatalog.Production.Authorize(
            context,
            BaiduAutomationAction.CreateLink);

        Assert.False(decision.IsAllowed);
        Assert.Equal(BaiduAutomationDenialReason.CatalogNotValidated, decision.DenialReason);
    }

    [Theory]
    [InlineData(BaiduPageState.Unknown, BaiduAutomationAction.ClickSave, 1, BaiduAutomationDenialReason.AmbiguousOrUnknownState)]
    [InlineData(BaiduPageState.ExternalShareReady, BaiduAutomationAction.CreateLink, 1, BaiduAutomationDenialReason.ActionNotAllowedForState)]
    [InlineData(BaiduPageState.ExternalShareReady, BaiduAutomationAction.ClickSave, 2, BaiduAutomationDenialReason.CandidateNotUnique)]
    public void Authorize_FailsClosedBeforeAnyWrite(
        BaiduPageState state,
        BaiduAutomationAction action,
        int candidateCount,
        BaiduAutomationDenialReason expected)
    {
        var parsed = new BaiduPageParseResult(
            state,
            state != BaiduPageState.Unknown,
            BaiduProtocolValidationStatus.NotValidated,
            "safe-test-reason");
        var acceptance = new BaiduAutomationAcceptance(
            true,
            BaiduSelectorCatalog.ProtocolVersion,
            BaiduSelectorCatalog.SelectorVersion,
            "build-a");
        var context = new BaiduAutomationActionContext(
            parsed,
            BaiduSelectorCatalog.ProtocolVersion,
            BaiduSelectorCatalog.SelectorVersion,
            "build-a",
            acceptance,
            candidateCount);

        var decision = BaiduSelectorCatalog.Production.Authorize(context, action);

        Assert.False(decision.IsAllowed);
        Assert.Equal(expected, decision.DenialReason);
    }

    [Theory]
    [InlineData("future-protocol", "baidu-selectors-v1", "build-a", true, BaiduAutomationDenialReason.ProtocolVersionMismatch)]
    [InlineData("baidu-visible-web-v1", "future-selectors", "build-a", true, BaiduAutomationDenialReason.SelectorVersionMismatch)]
    [InlineData("baidu-visible-web-v1", "baidu-selectors-v1", "build-b", true, BaiduAutomationDenialReason.BuildVersionMismatch)]
    [InlineData("baidu-visible-web-v1", "baidu-selectors-v1", "build-a", false, BaiduAutomationDenialReason.AcceptanceMissing)]
    public void Authorize_RejectsMismatchedOrMissingAcceptance(
        string acceptedProtocol,
        string acceptedSelector,
        string acceptedBuild,
        bool isAccepted,
        BaiduAutomationDenialReason expected)
    {
        var parsed = new BaiduPageParseResult(
            BaiduPageState.ExternalShareReady,
            true,
            BaiduProtocolValidationStatus.NotValidated,
            "safe-test-reason");
        var acceptance = new BaiduAutomationAcceptance(
            isAccepted,
            acceptedProtocol,
            acceptedSelector,
            acceptedBuild);
        var context = new BaiduAutomationActionContext(
            parsed,
            BaiduSelectorCatalog.ProtocolVersion,
            BaiduSelectorCatalog.SelectorVersion,
            "build-a",
            acceptance,
            1);

        var decision = BaiduSelectorCatalog.Production.Authorize(
            context,
            BaiduAutomationAction.ClickSave);

        Assert.False(decision.IsAllowed);
        Assert.Equal(expected, decision.DenialReason);
    }

    [Fact]
    public void Authorize_RejectsFutureActionEnum()
    {
        var context = new BaiduAutomationActionContext(
            new BaiduPageParseResult(
                BaiduPageState.ExternalShareReady,
                true,
                BaiduProtocolValidationStatus.NotValidated,
                "safe-test-reason"),
            BaiduSelectorCatalog.ProtocolVersion,
            BaiduSelectorCatalog.SelectorVersion,
            "build-a",
            new BaiduAutomationAcceptance(
                true,
                BaiduSelectorCatalog.ProtocolVersion,
                BaiduSelectorCatalog.SelectorVersion,
                "build-a"),
            1);

        var decision = BaiduSelectorCatalog.Production.Authorize(
            context,
            (BaiduAutomationAction)int.MaxValue);

        Assert.False(decision.IsAllowed);
        Assert.Equal(BaiduAutomationDenialReason.UnknownAction, decision.DenialReason);
    }

    private static BaiduPageObservation Load(string name)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() },
        };
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
        return JsonSerializer.Deserialize<BaiduPageObservation>(json, options)!;
    }
}
