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
            BaiduAutomationAction.SubmitExtraction,
            BaiduAutomationAction.OpenDestinationPicker,
            BaiduAutomationAction.EnterDirectory,
            BaiduAutomationAction.CreateDirectory,
            BaiduAutomationAction.ConfirmDirectoryCreation,
            BaiduAutomationAction.CancelDestinationPicker,
            BaiduAutomationAction.ClickConfirmTransfer,
        };

        Assert.Equal(
            expected.Order(),
            BaiduSelectorCatalog.Production.Definitions
                .Select(x => x.Action)
                .Append(BaiduSelectorCatalog.NavigationAction)
                .Distinct()
                .Order());
    }

    [Fact]
    public void TransferCatalogV2UsesIndependentVersionsAndExplicitPageActions()
    {
        Assert.Equal("baidu-visible-transfer-v2", BaiduSelectorCatalog.TransferProtocolVersion);
        Assert.Equal("baidu-transfer-pages-v2", BaiduSelectorCatalog.TransferSelectorVersion);
        Assert.NotEqual(BaiduSelectorCatalog.ProtocolVersion, BaiduSelectorCatalog.TransferProtocolVersion);
        Assert.NotEqual(BaiduSelectorCatalog.SelectorVersion, BaiduSelectorCatalog.TransferSelectorVersion);

        Assert.True(BaiduSelectorCatalog.Production.IsActionAllowedForState(
            BaiduPageState.ExtractionCodeReady, BaiduAutomationAction.SubmitExtraction));
        Assert.True(BaiduSelectorCatalog.Production.IsActionAllowedForState(
            BaiduPageState.ExternalShareReady, BaiduAutomationAction.OpenDestinationPicker));
        Assert.True(BaiduSelectorCatalog.Production.IsActionAllowedForState(
            BaiduPageState.DestinationPickerReady, BaiduAutomationAction.EnterDirectory));
        Assert.True(BaiduSelectorCatalog.Production.IsActionAllowedForState(
            BaiduPageState.DestinationPickerReady, BaiduAutomationAction.CreateDirectory));
        Assert.True(BaiduSelectorCatalog.Production.IsActionAllowedForState(
            BaiduPageState.DestinationPickerReady, BaiduAutomationAction.ConfirmDirectoryCreation));
        Assert.True(BaiduSelectorCatalog.Production.IsActionAllowedForState(
            BaiduPageState.DestinationPickerReady, BaiduAutomationAction.CancelDestinationPicker));
        Assert.True(BaiduSelectorCatalog.Production.IsActionAllowedForState(
            BaiduPageState.DestinationPickerReady, BaiduAutomationAction.ClickConfirmTransfer));
    }

    [Fact]
    public void CatalogDefinitionsAreUniquelyBoundToRequiredStateAndAction()
    {
        var definitions = BaiduSelectorCatalog.Production.Definitions;

        Assert.Equal(
            definitions.Length,
            definitions.Select(definition => (definition.RequiredState, definition.Action)).Distinct().Count());
        Assert.Contains(
            definitions,
            definition => definition.RequiredState is BaiduPageState.OwnDriveReady &&
                          definition.Action is BaiduAutomationAction.ClickShare);
        Assert.DoesNotContain(
            definitions,
            definition => definition.RequiredState is BaiduPageState.ExternalShareReady &&
                          definition.Action is BaiduAutomationAction.ClickShare);
        Assert.DoesNotContain(
            definitions,
            definition => definition.RequiredState is BaiduPageState.Unknown ||
                          definition.Action is BaiduAutomationAction.OpenOfficialUri);

        var externalSelectionTokens = definitions.Single(definition =>
                definition.RequiredState is BaiduPageState.ExternalShareReady &&
                definition.Action is BaiduAutomationAction.SelectExactItems)
            .CandidateStrategies.Select(strategy => strategy.Token);
        var ownDriveSelectionTokens = definitions.Single(definition =>
                definition.RequiredState is BaiduPageState.OwnDriveReady &&
                definition.Action is BaiduAutomationAction.SelectExactItems)
            .CandidateStrategies.Select(strategy => strategy.Token);
        Assert.Empty(externalSelectionTokens.Intersect(ownDriveSelectionTokens, StringComparer.Ordinal));
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

    [Fact]
    public void Authorize_RejectsDefaultActionEnum()
    {
        var context = new BaiduAutomationActionContext(
            new BaiduPageParseResult(
                BaiduPageState.OwnDriveReady,
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

        Assert.Equal(BaiduAutomationAction.Unspecified, default(BaiduAutomationAction));
        var decision = BaiduSelectorCatalog.Production.Authorize(context, default);

        Assert.False(decision.IsAllowed);
        Assert.Equal(BaiduAutomationDenialReason.UnknownAction, decision.DenialReason);
    }

    [Fact]
    public void CatalogDoesNotReuseStateBoundSelectorAcrossPages()
    {
        var parsed = new BaiduPageParseResult(
            BaiduPageState.ExternalShareReady,
            true,
            BaiduProtocolValidationStatus.NotValidated,
            "safe-test-reason");
        var context = new BaiduAutomationActionContext(
            parsed,
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
            BaiduAutomationAction.ClickShare);

        Assert.False(decision.IsAllowed);
        Assert.Equal(BaiduAutomationDenialReason.ActionNotAllowedForState, decision.DenialReason);
    }

    [Fact]
    public void AuthorizeNavigation_ValidOfficialUriReachesProductionCatalogValidationGate()
    {
        var decision = BaiduSelectorCatalog.Production.AuthorizeNavigation(
            NavigationRequest(new Uri("https://pan.baidu.com/")));

        Assert.False(decision.IsAllowed);
        Assert.Equal(BaiduAutomationDenialReason.CatalogNotValidated, decision.DenialReason);
        Assert.Equal(BaiduProtocolValidationStatus.NotValidated, BaiduSelectorCatalog.Production.ValidationStatus);
    }

    [Theory]
    [InlineData("/disk/home")]
    [InlineData("http://pan.baidu.com/")]
    [InlineData("https://pan.baidu.com.evil/")]
    [InlineData("https://user@pan.baidu.com/")]
    [InlineData("https://pan.baidu.com:444/")]
    [InlineData("https://pan.baidu.com/#section")]
    public void AuthorizeNavigation_RejectsUnsafeOfficialUri(string value)
    {
        var decision = BaiduSelectorCatalog.Production.AuthorizeNavigation(
            NavigationRequest(new Uri(value, UriKind.RelativeOrAbsolute)));

        Assert.False(decision.IsAllowed);
        Assert.Equal(BaiduAutomationDenialReason.OfficialUriNotAllowed, decision.DenialReason);
    }

    [Fact]
    public void AuthorizeNavigation_RejectsMissingOfficialUri()
    {
        var decision = BaiduSelectorCatalog.Production.AuthorizeNavigation(
            NavigationRequest(new Uri("https://pan.baidu.com/")) with { OfficialUri = null! });

        Assert.False(decision.IsAllowed);
        Assert.Equal(BaiduAutomationDenialReason.OfficialUriNotAllowed, decision.DenialReason);
    }

    [Theory]
    [InlineData(BaiduAutomationAction.Unspecified)]
    [InlineData(BaiduAutomationAction.SelectExactItems)]
    [InlineData((BaiduAutomationAction)999)]
    public void AuthorizeNavigation_RejectsAnythingExceptExactNavigationAction(
        BaiduAutomationAction action)
    {
        var decision = BaiduSelectorCatalog.Production.AuthorizeNavigation(
            NavigationRequest(new Uri("https://pan.baidu.com/")) with { Action = action });

        Assert.False(decision.IsAllowed);
        Assert.Equal(
            Enum.IsDefined(action) && action is not BaiduAutomationAction.Unspecified
                ? BaiduAutomationDenialReason.ActionNotAllowedForNavigation
                : BaiduAutomationDenialReason.UnknownAction,
            decision.DenialReason);
    }

    [Fact]
    public void Authorize_DomActionStillRequiresObservation()
    {
        var decision = BaiduSelectorCatalog.Production.Authorize(
            null,
            BaiduAutomationAction.ClickShare);

        Assert.False(decision.IsAllowed);
        Assert.Equal(BaiduAutomationDenialReason.AmbiguousOrUnknownState, decision.DenialReason);
    }

    [Theory]
    [InlineData("future-protocol", "baidu-selectors-v1", "build-a", true, BaiduAutomationDenialReason.ProtocolVersionMismatch)]
    [InlineData("baidu-visible-web-v1", "future-selectors", "build-a", true, BaiduAutomationDenialReason.SelectorVersionMismatch)]
    [InlineData("baidu-visible-web-v1", "baidu-selectors-v1", "build-b", true, BaiduAutomationDenialReason.BuildVersionMismatch)]
    [InlineData("baidu-visible-web-v1", "baidu-selectors-v1", "build-a", false, BaiduAutomationDenialReason.AcceptanceMissing)]
    public void AuthorizeNavigation_RejectsMismatchedOrMissingAcceptance(
        string acceptedProtocol,
        string acceptedSelector,
        string acceptedBuild,
        bool isAccepted,
        BaiduAutomationDenialReason expected)
    {
        var request = NavigationRequest(new Uri("https://pan.baidu.com/")) with
        {
            Acceptance = new BaiduAutomationAcceptance(
                isAccepted,
                acceptedProtocol,
                acceptedSelector,
                acceptedBuild),
        };

        var decision = BaiduSelectorCatalog.Production.AuthorizeNavigation(request);

        Assert.False(decision.IsAllowed);
        Assert.Equal(expected, decision.DenialReason);
    }

    [Theory]
    [InlineData("future-protocol", "baidu-selectors-v1", "build-a", BaiduAutomationDenialReason.ProtocolVersionMismatch)]
    [InlineData("baidu-visible-web-v1", "future-selectors", "build-a", BaiduAutomationDenialReason.SelectorVersionMismatch)]
    [InlineData("baidu-visible-web-v1", "baidu-selectors-v1", "build-b", BaiduAutomationDenialReason.BuildVersionMismatch)]
    public void AuthorizeNavigation_RejectsCurrentRequestVersionOrBuildMismatch(
        string protocol,
        string selector,
        string build,
        BaiduAutomationDenialReason expected)
    {
        var request = NavigationRequest(new Uri("https://pan.baidu.com/")) with
        {
            ProtocolVersion = protocol,
            SelectorVersion = selector,
            ApplicationBuildVersion = build,
        };

        var decision = BaiduSelectorCatalog.Production.AuthorizeNavigation(request);

        Assert.False(decision.IsAllowed);
        Assert.Equal(expected, decision.DenialReason);
    }

    [Fact]
    public void AuthorizeNavigation_FutureValidatedCatalogCanAuthorizeWithoutChangingProduction()
    {
        var validatedCatalog = new BaiduSelectorCatalog(BaiduProtocolValidationStatus.Validated);

        var decision = validatedCatalog.AuthorizeNavigation(
            NavigationRequest(new Uri("https://pan.baidu.com/disk/main")));

        Assert.True(decision.IsAllowed);
        Assert.Equal(BaiduAutomationDenialReason.None, decision.DenialReason);
        Assert.Equal(BaiduProtocolValidationStatus.Validated, validatedCatalog.ValidationStatus);
        Assert.Equal(BaiduProtocolValidationStatus.NotValidated, BaiduSelectorCatalog.Production.ValidationStatus);
        Assert.All(validatedCatalog.Definitions, definition => Assert.Empty(definition.ExecutableLocators));
    }

    [Fact]
    public void AuthorizeNavigation_RejectsUnvalidatedFieldEvidence()
    {
        var validatedCatalog = new BaiduSelectorCatalog(BaiduProtocolValidationStatus.Validated);
        var request = NavigationRequest(new Uri("https://pan.baidu.com/")) with
        {
            FieldEvidenceValidationStatus = BaiduProtocolValidationStatus.NotValidated,
        };

        var decision = validatedCatalog.AuthorizeNavigation(request);

        Assert.False(decision.IsAllowed);
        Assert.Equal(BaiduAutomationDenialReason.FieldEvidenceNotValidated, decision.DenialReason);
    }

    [Fact]
    public void NavigationRequest_ToStringRedactsOfficialUriAndVersions()
    {
        var request = NavigationRequest(new Uri("https://pan.baidu.com/disk/main?private=value"));

        var text = request.ToString();

        Assert.Contains("[REDACTED]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("pan.baidu.com", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("build-a", text, StringComparison.Ordinal);
    }

    private static BaiduNavigationAuthorizationRequest NavigationRequest(Uri uri) => new(
        BaiduAutomationAction.OpenOfficialUri,
        uri,
        BaiduSelectorCatalog.ProtocolVersion,
        BaiduSelectorCatalog.SelectorVersion,
        "build-a",
        new BaiduAutomationAcceptance(
            true,
            BaiduSelectorCatalog.ProtocolVersion,
            BaiduSelectorCatalog.SelectorVersion,
            "build-a"),
        BaiduProtocolValidationStatus.Validated);

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
