using System.Text.Json;
using System.Text.Json.Serialization;
using BaiduBatchTransfer.BaiduAdapter.WebAutomation;

namespace BaiduBatchTransfer.BaiduAdapter.Tests.WebAutomation;

public sealed class BaiduPageObservationParserTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    [Theory]
    [InlineData("external-share-ready.synthetic.json", BaiduPageState.ExternalShareReady)]
    [InlineData("own-drive-ready.synthetic.json", BaiduPageState.OwnDriveReady)]
    [InlineData("save-destination-ready.synthetic.json", BaiduPageState.SaveDestinationReady)]
    [InlineData("transfer-accepted.synthetic.json", BaiduPageState.TransferAccepted)]
    [InlineData("share-dialog-ready.synthetic.json", BaiduPageState.ShareDialogReady)]
    [InlineData("share-result-ready.synthetic.json", BaiduPageState.ShareResultReady)]
    [InlineData("verification-required.synthetic.json", BaiduPageState.UserVerificationRequired)]
    public void Parse_RecognizesSyntheticStateWithoutClaimingFieldValidation(
        string fixture,
        BaiduPageState expected)
    {
        var observation = Load(fixture);

        var result = BaiduPageObservationParser.Parse(observation);

        Assert.Equal(BaiduObservationProvenance.Synthetic, observation.Provenance);
        Assert.Equal(BaiduObservationValidationStatus.NotFieldValidated, observation.ValidationStatus);
        Assert.Equal(expected, result.State);
        Assert.True(result.IsUnambiguous);
        Assert.Equal(BaiduProtocolValidationStatus.NotValidated, result.ValidationStatus);
    }

    [Fact]
    public void Parse_RejectsEmptyObservation()
    {
        var result = BaiduPageObservationParser.Parse(null);

        Assert.Equal(BaiduPageState.Unknown, result.State);
        Assert.False(result.IsUnambiguous);
        Assert.Equal(BaiduProtocolValidationStatus.NotValidated, result.ValidationStatus);
    }

    [Fact]
    public void Parse_RejectsAmbiguousRequiredCandidateInsteadOfChoosingFirst()
    {
        var observation = Load("external-share-ready.synthetic.json") with
        {
            CandidateCounts = new Dictionary<string, int>
            {
                ["save-action"] = 2,
            },
        };

        var result = BaiduPageObservationParser.Parse(observation);

        Assert.Equal(BaiduPageState.Unknown, result.State);
        Assert.False(result.IsUnambiguous);
    }

    [Fact]
    public void Parse_RejectsConflictingPageRoles()
    {
        var observation = Load("external-share-ready.synthetic.json") with
        {
            PageRoles = ["external-share", "share-result"],
        };

        Assert.Equal(BaiduPageState.Unknown, BaiduPageObservationParser.Parse(observation).State);
    }

    [Fact]
    public void Parse_RejectsPageRedesignWhenIndependentEvidenceIsMissing()
    {
        var observation = Load("share-dialog-ready.synthetic.json") with
        {
            StableAttributeTokens = new Dictionary<string, string>(),
        };

        Assert.Equal(BaiduPageState.Unknown, BaiduPageObservationParser.Parse(observation).State);
    }

    [Fact]
    public void Parse_RejectsFutureSchemaVersion()
    {
        var observation = Load("own-drive-ready.synthetic.json") with { SchemaVersion = 999 };

        Assert.Equal(BaiduPageState.Unknown, BaiduPageObservationParser.Parse(observation).State);
    }

    [Fact]
    public void Parse_RejectsFutureValidationEnum()
    {
        var observation = Load("own-drive-ready.synthetic.json") with
        {
            ValidationStatus = (BaiduObservationValidationStatus)999,
        };

        Assert.Equal(BaiduPageState.Unknown, BaiduPageObservationParser.Parse(observation).State);
    }

    [Fact]
    public void Parse_DoesNotTrustSelfAssertedFieldValidationBeforeLiveAcceptance()
    {
        var observation = Load("own-drive-ready.synthetic.json") with
        {
            Provenance = BaiduObservationProvenance.FieldEvidence,
            ValidationStatus = BaiduObservationValidationStatus.FieldValidated,
        };

        var result = BaiduPageObservationParser.Parse(observation);

        Assert.Equal(BaiduPageState.Unknown, result.State);
        Assert.Equal(BaiduProtocolValidationStatus.NotValidated, result.ValidationStatus);
    }

    [Fact]
    public void Parse_RejectsUnexpectedOpaqueTokenThatCouldContainSensitiveVisibleText()
    {
        var observation = Load("external-share-ready.synthetic.json") with
        {
            VisibleSemanticLabels = ["shared-content", "save-to-drive", "fourdigits"],
        };

        Assert.Equal(BaiduPageState.Unknown, BaiduPageObservationParser.Parse(observation).State);
    }

    [Fact]
    public void Parse_RejectsDuplicateExactIdentityPlaceholders()
    {
        var observation = Load("own-drive-ready.synthetic.json") with
        {
            IdentityPlaceholders = ["fictional-item-beta", "fictional-item-beta"],
        };

        Assert.Equal(BaiduPageState.Unknown, BaiduPageObservationParser.Parse(observation).State);
    }

    [Fact]
    public void Parse_EmptyDirectoryObservationFailsClosedAndBlocksResourceActions()
    {
        var observation = Load("own-drive-empty-directory.synthetic.json");

        var result = BaiduPageObservationParser.Parse(observation);

        Assert.Equal(BaiduObservationProvenance.Synthetic, observation.Provenance);
        Assert.Equal(BaiduObservationValidationStatus.NotFieldValidated, observation.ValidationStatus);
        Assert.Empty(observation.IdentityPlaceholders);
        Assert.Equal(0, observation.CandidateCounts["resource-item"]);
        Assert.Equal(BaiduPageState.Unknown, result.State);
        Assert.False(result.IsUnambiguous);
        Assert.Equal(BaiduProtocolValidationStatus.NotValidated, result.ValidationStatus);

        var acceptance = new BaiduAutomationAcceptance(
            true,
            BaiduSelectorCatalog.ProtocolVersion,
            BaiduSelectorCatalog.SelectorVersion,
            "build-a");
        var context = new BaiduAutomationActionContext(
            result,
            BaiduSelectorCatalog.ProtocolVersion,
            BaiduSelectorCatalog.SelectorVersion,
            "build-a",
            acceptance,
            0);

        foreach (var action in new[]
                 {
                     BaiduAutomationAction.SelectExactItems,
                     BaiduAutomationAction.ClickShare,
                 })
        {
            var decision = BaiduSelectorCatalog.Production.Authorize(context, action);
            Assert.False(decision.IsAllowed);
            Assert.Equal(BaiduAutomationDenialReason.AmbiguousOrUnknownState, decision.DenialReason);
        }
    }

    [Theory]
    [InlineData("https://pan.baidu.com/s/fictional")]
    [InlineData("pwd=four-digits")]
    [InlineData("cookie-token")]
    [InlineData("C:\\private\\folder")]
    public void Parse_RejectsUnsafeObservationValues(string unsafeValue)
    {
        var observation = Load("own-drive-ready.synthetic.json") with
        {
            VisibleSemanticLabels = [unsafeValue],
        };

        var result = BaiduPageObservationParser.Parse(observation);

        Assert.Equal(BaiduPageState.Unknown, result.State);
        Assert.Equal(BaiduProtocolValidationStatus.NotValidated, result.ValidationStatus);
    }

    [Fact]
    public void Observation_ToStringDoesNotRevealObservedValues()
    {
        var observation = Load("external-share-ready.synthetic.json");

        var text = observation.ToString();

        Assert.Contains("[REDACTED]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("fictional-folder-alpha", text, StringComparison.Ordinal);
        Assert.DoesNotContain("external-share", text, StringComparison.Ordinal);
    }

    [Fact]
    public void SyntheticFixturesContainNoSecretsOrRealIdentifiers()
    {
        foreach (var path in Directory.EnumerateFiles(FixtureDirectory, "*.json"))
        {
            var text = File.ReadAllText(path);
            Assert.DoesNotMatch(@"(?i)https?://|pan\.baidu\.com|\bpwd\s*=|extraction.?code|cookie|authorization|request.?header|response.?body|\buid\b|[a-z]:\\|/(?:users|home)/", text);
            Assert.DoesNotMatch(@"(?i)bduss|stoken|baiduid|client_secret|app_secret", text);
        }
    }

    private static string FixtureDirectory => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    private static BaiduPageObservation Load(string name)
    {
        var json = File.ReadAllText(Path.Combine(FixtureDirectory, name));
        return JsonSerializer.Deserialize<BaiduPageObservation>(json, JsonOptions)
            ?? throw new InvalidOperationException("Synthetic fixture could not be read.");
    }
}
