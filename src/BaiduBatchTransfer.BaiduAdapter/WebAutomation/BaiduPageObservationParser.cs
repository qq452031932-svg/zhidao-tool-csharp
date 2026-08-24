using System.Collections.Frozen;

namespace BaiduBatchTransfer.BaiduAdapter.WebAutomation;

public static class BaiduPageObservationParser
{
    public const int SchemaVersion = 1;

    private static readonly FrozenSet<string> SensitiveTokens = new[]
    {
        "authorization",
        "baiduid",
        "bduss",
        "body",
        "cookie",
        "extraction-code",
        "header",
        "pwd",
        "secret",
        "stoken",
        "uid",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly StateRule[] Rules =
    [
        new(
            BaiduPageState.ExternalShareReady,
            "external-share",
            ["shared-content", "save-to-drive"],
            "view",
            "share-content",
            ["save-action"]),
        new(
            BaiduPageState.OwnDriveReady,
            "own-drive",
            ["resource-list", "share-entry"],
            "view",
            "drive-list",
            ["share-action"]),
        new(
            BaiduPageState.SaveDestinationReady,
            "save-destination",
            ["choose-destination", "confirm-save"],
            "dialog",
            "save-destination",
            ["confirm-save-action"]),
        new(
            BaiduPageState.TransferAccepted,
            "transfer-result",
            ["transfer-accepted", "destination-confirmed"],
            "outcome",
            "accepted",
            ["accepted-result"]),
        new(
            BaiduPageState.ShareDialogReady,
            "share-dialog",
            ["create-share", "permanent-validity"],
            "dialog",
            "share-create",
            ["create-link-action", "permanent-option"]),
        new(
            BaiduPageState.ShareResultReady,
            "share-result",
            ["share-created", "export-result"],
            "dialog",
            "share-result",
            ["export-result-action"]),
        new(
            BaiduPageState.UserVerificationRequired,
            "verification",
            ["verification-required", "user-must-continue"],
            "challenge",
            "user-verification",
            ["verification-challenge"]),
    ];

    public static BaiduPageParseResult Parse(BaiduPageObservation? observation)
    {
        if (!IsSafe(observation))
            return Unknown("页面观察为空、格式未知或含不安全内容。");

        var matches = Rules.Where(rule => rule.Matches(observation!)).Take(2).ToArray();
        if (matches.Length != 1)
            return Unknown("页面证据不足或互相冲突。");

        return new BaiduPageParseResult(
            matches[0].State,
            true,
            BaiduProtocolValidationStatus.NotValidated,
            "仅识别合成证据，尚未通过现场验证。");
    }

    private static bool IsSafe(BaiduPageObservation? observation)
    {
        if (observation is null ||
            observation.SchemaVersion != SchemaVersion ||
            !Enum.IsDefined(observation.Provenance) ||
            !Enum.IsDefined(observation.ValidationStatus) ||
            observation.Provenance is not BaiduObservationProvenance.Synthetic ||
            observation.ValidationStatus is not BaiduObservationValidationStatus.NotFieldValidated)
        {
            return false;
        }

        if (observation.PageRoles is not { Count: 1 } ||
            observation.VisibleSemanticLabels is not { Count: > 0 and <= 32 } ||
            observation.StableAttributeTokens is not { Count: > 0 and <= 16 } ||
            observation.CandidateCounts is not { Count: > 0 and <= 32 } ||
            observation.IdentityPlaceholders is not { Count: > 0 and <= 64 })
        {
            return false;
        }

        if (!observation.PageRoles.All(IsSafeToken) ||
            !observation.VisibleSemanticLabels.All(IsSafeToken) ||
            !observation.StableAttributeTokens.All(pair =>
                IsSafeToken(pair.Key) && IsSafeToken(pair.Value)) ||
            !observation.CandidateCounts.All(pair =>
                IsSafeToken(pair.Key) && pair.Value == 1) ||
            !observation.IdentityPlaceholders.All(value =>
                value.StartsWith("fictional-", StringComparison.Ordinal) && IsSafeToken(value)) ||
            observation.IdentityPlaceholders.Distinct(StringComparer.Ordinal).Count() !=
                observation.IdentityPlaceholders.Count)
        {
            return false;
        }

        return true;
    }

    private static bool IsSafeToken(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 64 ||
            value[0] is < 'a' or > 'z')
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '-')
                return false;
        }

        return !SensitiveTokens.Any(sensitive =>
            value.Equals(sensitive, StringComparison.Ordinal) ||
            value.StartsWith(sensitive + "-", StringComparison.Ordinal) ||
            value.EndsWith("-" + sensitive, StringComparison.Ordinal));
    }

    private static BaiduPageParseResult Unknown(string reason) => new(
        BaiduPageState.Unknown,
        false,
        BaiduProtocolValidationStatus.NotValidated,
        reason);

    private sealed record StateRule(
        BaiduPageState State,
        string PageRole,
        string[] RequiredLabels,
        string StableAttributeKey,
        string StableAttributeValue,
        string[] RequiredCandidates)
    {
        public bool Matches(BaiduPageObservation observation) =>
            observation.PageRoles[0].Equals(PageRole, StringComparison.Ordinal) &&
            observation.VisibleSemanticLabels.Count == RequiredLabels.Length &&
            RequiredLabels.All(observation.VisibleSemanticLabels.Contains) &&
            observation.StableAttributeTokens.Count == 1 &&
            observation.StableAttributeTokens.TryGetValue(StableAttributeKey, out var token) &&
            token.Equals(StableAttributeValue, StringComparison.Ordinal) &&
            observation.CandidateCounts.Count == RequiredCandidates.Length &&
            RequiredCandidates.All(candidate =>
                observation.CandidateCounts.TryGetValue(candidate, out var count) && count == 1);
    }
}
