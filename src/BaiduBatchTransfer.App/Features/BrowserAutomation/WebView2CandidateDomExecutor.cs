using System.Collections.Immutable;
using System.Text.Json;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.BaiduAdapter.WebAutomation;
using Microsoft.Web.WebView2.Core;

namespace BaiduBatchTransfer.App.Features.BrowserAutomation;

public sealed class WebView2CandidateDomExecutor(
    IBaiduCandidateDomScriptHost host,
    IBaiduCandidateActionAuthorizationSource authorizationSource) :
    IBaiduCandidateDomExecutor,
    IBaiduTransferCandidateDomReader
{
    private static readonly HashSet<string> ResponseFields = new(StringComparer.Ordinal)
    {
        "status", "reason", "officialUriMatched", "pageState", "controlCount", "complete",
        "stable", "exactIds", "observationToken", "mutationCount",
    };

    public const string UnprovenFieldResponse =
        "{\"status\":\"notValidated\",\"reason\":\"field-evidence-not-validated\",\"officialUriMatched\":true,\"pageState\":\"OwnDrive\",\"controlCount\":0,\"complete\":false,\"stable\":false,\"exactIds\":[],\"observationToken\":\"\",\"mutationCount\":0}";

    public const string ObserveScript = """
        function(argsJson) {
          'use strict';
          const args = JSON.parse(argsJson);
          const fail = () => ({
            status: 'notValidated', reason: 'field-evidence-not-validated',
            officialUriMatched: location.href === args.officialUri, pageState: args.expectedPageState,
            controlCount: 0, complete: false, stable: false, exactIds: [],
            observationToken: '', mutationCount: 0
          });
          if (location.href !== args.officialUri) return fail();
          return fail();
        }
        """;

    public const string MutateScript = """
        function(argsJson) {
          'use strict';
          const args = JSON.parse(argsJson);
          const fail = (reason) => ({
            status: 'notValidated', reason,
            officialUriMatched: location.href === args.officialUri, pageState: args.expectedPageState,
            controlCount: 0, complete: false, stable: false, exactIds: [],
            observationToken: '', mutationCount: 0
          });
          if (location.href !== args.officialUri) return fail('observation-changed');
          const currentObservation = null;
          if (currentObservation === null) return fail('field-evidence-not-validated');
          if (currentObservation.pageState !== args.expectedPageState ||
              currentObservation.controlCount !== 1 ||
              currentObservation.complete !== true || currentObservation.stable !== true ||
              currentObservation.observationToken !== args.expectedObservationToken ||
              JSON.stringify(currentObservation.exactIds) !== JSON.stringify(args.expectedExactIds))
            return fail('observation-changed');
          return fail('field-evidence-not-validated');
        }
        """;

    public async Task<BaiduCandidateDomResult> ObserveAsync(
        BaiduCandidateDomRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var arguments = SerializeArguments(request, expectedObservationToken: null);
        var raw = await host.ExecuteAsync(
            BaiduCandidateBuiltInScript.Observe, arguments, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return ParseAndValidate(raw, request, expectedMutationCount: 0, expectedObservationToken: null);
    }

    public async Task<BaiduTransferCandidateDomResult> ObserveTransferAsync(
        BaiduCandidateDomReadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var arguments = JsonSerializer.Serialize(new
        {
            officialUri = request.OfficialUri.AbsoluteUri,
            expectedPageState = request.ExpectedPageState.ToString(),
            expectedBaiduUserId = request.ExpectedBaiduUserId,
            expectedExactIds = request.ExpectedExactIds,
            expectedParentExactId = request.ExpectedParentExactId,
            expectedCanonicalPath = request.ExpectedCanonicalPath?.Value,
        });
        var raw = await host.ExecuteAsync(
            BaiduCandidateBuiltInScript.ObserveTransfer, arguments, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return BaiduTransferCandidateScripts.ParseAndValidate(raw, request);
    }

    public async Task<BaiduCandidateDomResult> MutateAsync(
        BaiduCandidateDomRequest request,
        BaiduCandidateDomObservation observation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(observation);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Matches(request, observation))
            return BaiduCandidateDomResult.Denied(BaiduAutomationDenialReason.ObservationNotValidated);
        await using var permit = await authorizationSource
            .TryAcquireMutationPermitAsync(request, cancellationToken).ConfigureAwait(false);
        if (permit is null)
            return BaiduCandidateDomResult.Denied(BaiduAutomationDenialReason.AcceptanceMissing);
        cancellationToken.ThrowIfCancellationRequested();
        var arguments = SerializeArguments(request, observation.ObservationToken);
        var raw = await host.ExecuteAsync(
            BaiduCandidateBuiltInScript.Mutate, arguments, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return ParseAndValidate(raw, request, expectedMutationCount: 1, observation.ObservationToken);
    }

    private static string SerializeArguments(
        BaiduCandidateDomRequest request,
        string? expectedObservationToken) =>
        JsonSerializer.Serialize(new
        {
            officialUri = request.OfficialUri.AbsoluteUri,
            expectedPageState = request.ExpectedPageState.ToString(),
            expectedExactIds = request.ExpectedExactIds,
            expectedObservationToken,
            action = request.Action.ToString(),
        });

    private static BaiduCandidateDomResult ParseAndValidate(
        string raw,
        BaiduCandidateDomRequest request,
        int expectedMutationCount,
        string? expectedObservationToken)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 16_384)
            return BaiduCandidateDomResult.Denied(BaiduAutomationDenialReason.ObservationNotValidated);
        try
        {
            using var document = JsonDocument.Parse(raw, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 4,
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Invalid();
            var properties = root.EnumerateObject().ToArray();
            if (properties.Length != ResponseFields.Count ||
                properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length ||
                properties.Any(property => !ResponseFields.Contains(property.Name)))
                return Invalid();

            var status = RequiredString(root, "status", 32);
            var reason = RequiredString(root, "reason", 64);
            if (!string.Equals(status, "validated", StringComparison.Ordinal))
                return BaiduCandidateDomResult.Denied(
                    string.Equals(reason, "field-evidence-not-validated", StringComparison.Ordinal)
                        ? BaiduAutomationDenialReason.FieldEvidenceNotValidated
                        : BaiduAutomationDenialReason.ObservationNotValidated);
            if (!string.Equals(reason, "none", StringComparison.Ordinal)) return Invalid();

            if (!root.GetProperty("officialUriMatched").GetBoolean()) return Invalid();
            if (!Enum.TryParse<BaiduCandidatePageState>(
                    RequiredString(root, "pageState", 64), ignoreCase: false, out var pageState) ||
                pageState != request.ExpectedPageState)
                return BaiduCandidateDomResult.Denied(BaiduAutomationDenialReason.ActionNotAllowedForState);
            var controlCount = root.GetProperty("controlCount").GetInt32();
            if (controlCount != 1)
                return BaiduCandidateDomResult.Denied(BaiduAutomationDenialReason.CandidateNotUnique);
            var complete = root.GetProperty("complete").GetBoolean();
            var stable = root.GetProperty("stable").GetBoolean();
            var exactIds = ReadExactIds(root.GetProperty("exactIds"));
            var token = RequiredString(root, "observationToken", 64);
            var mutationCount = root.GetProperty("mutationCount").GetInt32();
            if (!complete || !stable || !exactIds.SequenceEqual(request.ExpectedExactIds, StringComparer.Ordinal) ||
                !IsFingerprint(token) || mutationCount != expectedMutationCount ||
                expectedObservationToken is not null &&
                !string.Equals(token, expectedObservationToken, StringComparison.Ordinal))
                return Invalid();
            return BaiduCandidateDomResult.Success(new(
                request.OfficialUri, pageState, controlCount, complete, stable, exactIds, token));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or
                                           FormatException or ArgumentException or OverflowException)
        {
            return Invalid();
        }
    }

    private static bool Matches(
        BaiduCandidateDomRequest request,
        BaiduCandidateDomObservation observation) =>
        observation.OfficialUri == request.OfficialUri &&
        observation.PageState == request.ExpectedPageState &&
        observation.ControlCount == 1 && observation.IsComplete && observation.IsStable &&
        observation.ExactIds.SequenceEqual(request.ExpectedExactIds, StringComparer.Ordinal) &&
        IsFingerprint(observation.ObservationToken);

    private static string RequiredString(JsonElement root, string name, int maximumLength)
    {
        var value = root.GetProperty(name).GetString();
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength ||
            value.Any(char.IsControl))
            throw new JsonException("The structured candidate response contains invalid text.");
        return value;
    }

    private static ImmutableArray<string> ReadExactIds(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() is < 1 or > 500)
            throw new JsonException("The exact ID set is invalid.");
        var values = element.EnumerateArray().Select(value =>
            value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : throw new JsonException("An exact ID is not text."))
            .Select(value => !string.IsNullOrWhiteSpace(value) && value.Length <= 256 &&
                             value.All(character => !char.IsControl(character))
                ? value
                : throw new JsonException("An exact ID is invalid."))
            .ToImmutableArray();
        if (!values.SequenceEqual(values.Order(StringComparer.Ordinal), StringComparer.Ordinal) ||
            values.Distinct(StringComparer.Ordinal).Count() != values.Length)
            throw new JsonException("Exact IDs must be strictly ordered and unique.");
        return values!;
    }

    private static bool IsFingerprint(string value) =>
        value.Length == 64 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static BaiduCandidateDomResult Invalid() =>
        BaiduCandidateDomResult.Denied(BaiduAutomationDenialReason.ObservationNotValidated);
}

public sealed class CoreWebView2CandidateDomScriptHost(CoreWebView2 core) : IBaiduCandidateDomScriptHost
{
    public Task<string> ExecuteAsync(
        BaiduCandidateBuiltInScript script,
        string argumentsJson,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var builtIn = script switch
        {
            BaiduCandidateBuiltInScript.Observe => WebView2CandidateDomExecutor.ObserveScript,
            BaiduCandidateBuiltInScript.Mutate => WebView2CandidateDomExecutor.MutateScript,
            BaiduCandidateBuiltInScript.ObserveTransfer => BaiduTransferCandidateScripts.ObserveScript,
            _ => throw new ArgumentOutOfRangeException(nameof(script)),
        };
        var encodedArguments = JsonSerializer.Serialize(argumentsJson);
        return core.ExecuteScriptAsync($"({builtIn})({encodedArguments})");
    }
}
