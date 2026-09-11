using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
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

    [Theory]
    [InlineData("provenance")]
    [InlineData("validationStatus")]
    public void Parse_JsonMissingRequiredEvidenceEnumFailsClosed(string propertyName)
    {
        var json = JsonNode.Parse(File.ReadAllText(
            Path.Combine(FixtureDirectory, "own-drive-ready.synthetic.json")))!.AsObject();
        Assert.True(json.Remove(propertyName));
        var observation = JsonSerializer.Deserialize<BaiduPageObservation>(
            json.ToJsonString(),
            JsonOptions)!;

        var result = BaiduPageObservationParser.Parse(observation);

        Assert.Equal(BaiduPageState.Unknown, result.State);
        Assert.False(result.IsUnambiguous);
        Assert.Equal(BaiduProtocolValidationStatus.NotValidated, result.ValidationStatus);
    }

    [Theory]
    [InlineData("provenance")]
    [InlineData("validationStatus")]
    public void Parse_JsonNullRequiredEvidenceEnumIsRejectedDuringDeserialization(
        string propertyName)
    {
        var json = JsonNode.Parse(File.ReadAllText(
            Path.Combine(FixtureDirectory, "own-drive-ready.synthetic.json")))!.AsObject();
        json[propertyName] = null;

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<BaiduPageObservation>(json.ToJsonString(), JsonOptions));
    }

    [Fact]
    public void Parse_DefaultEvidenceEnumsFailClosed()
    {
        var observation = Load("own-drive-ready.synthetic.json") with
        {
            Provenance = default,
            ValidationStatus = default,
        };

        Assert.Equal(BaiduObservationProvenance.Unspecified, observation.Provenance);
        Assert.Equal(BaiduObservationValidationStatus.Unspecified, observation.ValidationStatus);
        Assert.Equal(BaiduPageState.Unknown, BaiduPageObservationParser.Parse(observation).State);
    }

    [Fact]
    public void Parse_NullCollectionFailsClosedWithoutThrowing()
    {
        var observation = Load("own-drive-ready.synthetic.json") with
        {
            IdentityPlaceholders = null!,
        };

        var result = BaiduPageObservationParser.Parse(observation);

        Assert.Equal(BaiduPageState.Unknown, result.State);
        Assert.Equal(BaiduProtocolValidationStatus.NotValidated, result.ValidationStatus);
    }

    [Fact]
    public void Parse_UnknownProvenanceIntegerFailsClosed()
    {
        var observation = Load("own-drive-ready.synthetic.json") with
        {
            Provenance = (BaiduObservationProvenance)999,
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
    [InlineData("fictional-https://pan.baidu.com/s/example")]
    [InlineData("fictional-pwd=four-digits")]
    [InlineData("fictional-cookie-token")]
    [InlineData("fictional-C:\\private\\folder")]
    public void Parse_RejectsUnsafeObservationValues(string unsafeValue)
    {
        var observation = Load("own-drive-ready.synthetic.json") with
        {
            IdentityPlaceholders = [unsafeValue],
        };

        var result = BaiduPageObservationParser.Parse(observation);

        Assert.Equal(BaiduPageState.Unknown, result.State);
        Assert.Equal(BaiduProtocolValidationStatus.NotValidated, result.ValidationStatus);
    }

    [Theory]
    [InlineData("fictional-bduss-leak")]
    [InlineData("fictional-uid-value")]
    [InlineData("fictional-cookie-value")]
    [InlineData("fictional-request-header-value")]
    [InlineData("fictional-authorization-value")]
    [InlineData("fictional-pwd-value")]
    [InlineData("fictional-extraction-code-value")]
    [InlineData("fictional-ｂｄｕｓｓ-leak")]
    public void Parse_RejectsSensitiveSegmentInsideOtherwiseValidIdentityPlaceholder(
        string unsafeIdentity)
    {
        var observation = Load("own-drive-ready.synthetic.json") with
        {
            IdentityPlaceholders = [unsafeIdentity],
        };

        var result = BaiduPageObservationParser.Parse(observation);

        Assert.Equal(BaiduPageState.Unknown, result.State);
        Assert.False(result.IsUnambiguous);
        Assert.Equal(BaiduProtocolValidationStatus.NotValidated, result.ValidationStatus);
    }

    [Fact]
    public void Parse_RejectsOversizedObservationToken()
    {
        var observation = Load("own-drive-ready.synthetic.json") with
        {
            IdentityPlaceholders = ["fictional-" + new string('a', 65)],
        };

        Assert.Equal(BaiduPageState.Unknown, BaiduPageObservationParser.Parse(observation).State);
    }

    [Fact]
    public void Parse_RejectsTooManyIdentityPlaceholders()
    {
        var observation = Load("own-drive-ready.synthetic.json") with
        {
            IdentityPlaceholders = Enumerable.Range(0, 65)
                .Select(index => $"fictional-item-{index}")
                .ToArray(),
        };

        Assert.Equal(BaiduPageState.Unknown, BaiduPageObservationParser.Parse(observation).State);
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
        Assert.Contains(
            Directory.EnumerateFiles(FixtureDirectory, "*.json", SearchOption.AllDirectories),
            path => Path.GetFileName(path) == "nested-own-drive-ready.synthetic.json");
        Assert.Empty(SyntheticFixturePrivacyAudit.FindUnsafeJsonFiles(FixtureDirectory));
    }

    [Fact]
    public void FixtureAuditFindsSensitiveJsonInNestedDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "baidu-fixture-audit", Guid.NewGuid().ToString("N"));
        var nested = Path.Combine(root, "nested", "deeper");
        Directory.CreateDirectory(nested);
        try
        {
            var unsafePath = Path.Combine(nested, "unsafe.synthetic.json");
            File.WriteAllText(unsafePath, "{ \"credential-kind\": \"cookie\" }");

            var unsafeFiles = SyntheticFixturePrivacyAudit.FindUnsafeJsonFiles(root);

            Assert.Equal(unsafePath, Assert.Single(unsafeFiles));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
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

internal static class SyntheticFixturePrivacyAudit
{
    private static readonly System.Text.RegularExpressions.Regex UnsafePattern = new(
        @"https?://|pan\.baidu\.com|\bpwd\s*=|extraction.?code|cookie|authorization|request.?header|response.?body|\buid\b|[a-z]:\\|/(?:users|home)/|bduss|stoken|baiduid|client_secret|app_secret",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase |
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public static IReadOnlyList<string> FindUnsafeJsonFiles(string rootDirectory) =>
        Directory.EnumerateFiles(
                rootDirectory,
                "*.synthetic.json",
                SearchOption.AllDirectories)
            .Where(path => UnsafePattern.IsMatch(File.ReadAllText(path)))
            .Order(StringComparer.Ordinal)
            .ToArray();
}
