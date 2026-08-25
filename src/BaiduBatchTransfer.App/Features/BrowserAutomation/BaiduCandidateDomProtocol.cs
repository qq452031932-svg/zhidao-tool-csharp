using System.Collections.Immutable;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.BaiduAdapter.WebAutomation;

namespace BaiduBatchTransfer.App.Features.BrowserAutomation;

public enum BaiduCandidatePageState
{
    ExternalShare,
    OwnDrive,
    DestinationPicker,
    TransferResult,
    ShareDialog,
    ShareResult,
    OfficialShareList,
}

public enum BaiduCandidateBuiltInScript
{
    Observe,
    Mutate,
}

public sealed record BaiduCandidateDomRequest
{
    public BaiduCandidateDomRequest(
        AdapterWriteAuthorizationRequest authorization,
        AdapterWriteAuthorizationBinding binding,
        BaiduAutomationAction action,
        Uri officialUri,
        BaiduCandidatePageState expectedPageState,
        IEnumerable<string> expectedExactIds)
    {
        Authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        if (action is BaiduAutomationAction.Unspecified or BaiduAutomationAction.OpenOfficialUri)
            throw new ArgumentOutOfRangeException(nameof(action));
        Action = action;
        OfficialUri = officialUri ?? throw new ArgumentNullException(nameof(officialUri));
        if (!BaiduNavigationPolicy.Evaluate(officialUri).IsAllowed)
            throw new ArgumentException("Only an official Baidu URI is allowed.", nameof(officialUri));
        if (!Enum.IsDefined(expectedPageState)) throw new ArgumentOutOfRangeException(nameof(expectedPageState));
        ExpectedPageState = expectedPageState;
        ExpectedExactIds = NormalizeExactIds(expectedExactIds);
    }

    public AdapterWriteAuthorizationRequest Authorization { get; init; }
    public AdapterWriteAuthorizationBinding Binding { get; init; }
    public BaiduAutomationAction Action { get; init; }
    public Uri OfficialUri { get; init; }
    public BaiduCandidatePageState ExpectedPageState { get; init; }
    public ImmutableArray<string> ExpectedExactIds { get; init; }

    public override string ToString() =>
        $"BaiduCandidateDomRequest {{ Action = {Action}, Identity = [REDACTED], Versions = [REDACTED], Uri = [REDACTED], ExpectedPageState = {ExpectedPageState}, ExactIds = [REDACTED] }}";

    private static ImmutableArray<string> NormalizeExactIds(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var result = values.Select(value =>
            !string.IsNullOrWhiteSpace(value) && value.Length <= 256 &&
            value.All(character => !char.IsControl(character))
                ? value
                : throw new ArgumentException("Exact IDs must be bounded non-control text.", nameof(values)))
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();
        if (result.IsDefaultOrEmpty || result.Distinct(StringComparer.Ordinal).Count() != result.Length)
            throw new ArgumentException("Exact IDs must be non-empty and unique.", nameof(values));
        return result;
    }
}

public sealed record BaiduCandidateDomObservation(
    Uri OfficialUri,
    BaiduCandidatePageState PageState,
    int ControlCount,
    bool IsComplete,
    bool IsStable,
    ImmutableArray<string> ExactIds,
    string ObservationToken);

public readonly record struct BaiduCandidateDomResult(
    bool IsSuccess,
    BaiduAutomationDenialReason DenialReason,
    BaiduCandidateDomObservation? Observation)
{
    public static BaiduCandidateDomResult Success(BaiduCandidateDomObservation observation) =>
        new(true, BaiduAutomationDenialReason.None, observation);

    public static BaiduCandidateDomResult Denied(BaiduAutomationDenialReason reason) =>
        new(false, reason, null);
}

public interface IBaiduCandidateDomScriptHost
{
    Task<string> ExecuteAsync(
        BaiduCandidateBuiltInScript script,
        string argumentsJson,
        CancellationToken cancellationToken = default);
}

public interface IBaiduCandidateActionAuthorizationSource
{
    ValueTask<bool> IsAuthorizedAsync(
        BaiduCandidateDomRequest request,
        CancellationToken cancellationToken = default);
}

public interface IBaiduCandidateDomExecutor
{
    Task<BaiduCandidateDomResult> ObserveAsync(
        BaiduCandidateDomRequest request,
        CancellationToken cancellationToken = default);

    Task<BaiduCandidateDomResult> MutateAsync(
        BaiduCandidateDomRequest request,
        BaiduCandidateDomObservation observation,
        CancellationToken cancellationToken = default);
}
