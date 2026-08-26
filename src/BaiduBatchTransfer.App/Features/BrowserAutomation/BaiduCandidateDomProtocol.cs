using System.Collections.Immutable;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.BaiduAdapter.WebAutomation;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.App.Features.BrowserAutomation;

public enum BaiduCandidatePageState
{
    ExtractionCode,
    ExternalShare,
    OwnDrive,
    DestinationPicker,
    TransferResult,
    ShareDialog,
    ShareResult,
    OfficialShareList,
}

public sealed record BaiduCandidateDomReadRequest
{
    public BaiduCandidateDomReadRequest(
        Uri officialUri,
        BaiduCandidatePageState expectedPageState,
        string expectedBaiduUserId,
        IEnumerable<string> expectedExactIds,
        string? expectedParentExactId,
        RemotePath? expectedCanonicalPath)
    {
        OfficialUri = officialUri ?? throw new ArgumentNullException(nameof(officialUri));
        if (!BaiduNavigationPolicy.Evaluate(officialUri).IsAllowed)
            throw new ArgumentException("Only an official Baidu URI is allowed.", nameof(officialUri));
        if (!Enum.IsDefined(expectedPageState)) throw new ArgumentOutOfRangeException(nameof(expectedPageState));
        ExpectedPageState = expectedPageState;
        ExpectedBaiduUserId = ValidateText(expectedBaiduUserId, nameof(expectedBaiduUserId));
        ExpectedExactIds = NormalizeExactIds(expectedExactIds);
        ExpectedParentExactId = expectedParentExactId is null
            ? null
            : ValidateText(expectedParentExactId, nameof(expectedParentExactId));
        ExpectedCanonicalPath = expectedCanonicalPath;
    }

    public Uri OfficialUri { get; }
    public BaiduCandidatePageState ExpectedPageState { get; }
    public string ExpectedBaiduUserId { get; }
    public ImmutableArray<string> ExpectedExactIds { get; }
    public string? ExpectedParentExactId { get; }
    public RemotePath? ExpectedCanonicalPath { get; }

    public bool Equals(BaiduCandidateDomReadRequest? other) =>
        other is not null &&
        Equals(OfficialUri, other.OfficialUri) &&
        ExpectedPageState == other.ExpectedPageState &&
        string.Equals(ExpectedBaiduUserId, other.ExpectedBaiduUserId, StringComparison.Ordinal) &&
        ExpectedExactIds.SequenceEqual(other.ExpectedExactIds, StringComparer.Ordinal) &&
        string.Equals(ExpectedParentExactId, other.ExpectedParentExactId, StringComparison.Ordinal) &&
        Equals(ExpectedCanonicalPath, other.ExpectedCanonicalPath);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(OfficialUri);
        hash.Add(ExpectedPageState);
        hash.Add(ExpectedBaiduUserId, StringComparer.Ordinal);
        foreach (var exactId in ExpectedExactIds) hash.Add(exactId, StringComparer.Ordinal);
        hash.Add(ExpectedParentExactId, StringComparer.Ordinal);
        hash.Add(ExpectedCanonicalPath);
        return hash.ToHashCode();
    }

    public override string ToString() =>
        $"BaiduCandidateDomReadRequest {{ ExpectedPageState = {ExpectedPageState}, Uri = [REDACTED], BaiduUserId = [REDACTED], ExactIds = [REDACTED], ParentExactId = [REDACTED], CanonicalPath = [REDACTED] }}";

    private static ImmutableArray<string> NormalizeExactIds(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var result = values.Select(value => ValidateText(value, nameof(values)))
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();
        if (result.Distinct(StringComparer.Ordinal).Count() != result.Length)
            throw new ArgumentException("Exact IDs must be unique.", nameof(values));
        return result;
    }

    private static string ValidateText(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 512 || value.Any(char.IsControl))
            throw new ArgumentException("The value must be bounded non-control text.", parameterName);
        return value;
    }
}

public sealed record BaiduCandidateDomMutationRequest
{
    public BaiduCandidateDomMutationRequest(
        AdapterWriteAuthorizationRequest authorization,
        AdapterWriteAuthorizationBinding binding,
        BaiduAutomationAction action,
        BaiduCandidateDomReadRequest readBinding,
        string? typedValue)
    {
        Authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        ReadBinding = readBinding ?? throw new ArgumentNullException(nameof(readBinding));
        if (!Enum.IsDefined(action) || action is BaiduAutomationAction.Unspecified or BaiduAutomationAction.OpenOfficialUri)
            throw new ArgumentOutOfRangeException(nameof(action));
        if (authorization.Capability != binding.Capability ||
            authorization.AccountId != binding.AccountId ||
            authorization.OperationId != binding.OperationId ||
            authorization.Generation != binding.Generation ||
            authorization.BatchId != binding.BatchId)
            throw new ArgumentException("The write binding must match the exact authorization.", nameof(binding));
        var requiresTypedValue = action is BaiduAutomationAction.SetExtractionCode or
            BaiduAutomationAction.CreateDirectory;
        if (requiresTypedValue)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(typedValue);
            if (typedValue.Length > 512 || typedValue.Any(char.IsControl))
                throw new ArgumentException("The typed value is invalid.", nameof(typedValue));
        }
        else if (typedValue is not null)
        {
            throw new ArgumentException("This action cannot contain typed text.", nameof(typedValue));
        }
        Action = action;
        TypedValue = typedValue;
    }

    public AdapterWriteAuthorizationRequest Authorization { get; }
    public AdapterWriteAuthorizationBinding Binding { get; }
    public BaiduAutomationAction Action { get; }
    public BaiduCandidateDomReadRequest ReadBinding { get; }
    public string? TypedValue { get; }

    public override string ToString() =>
        $"BaiduCandidateDomMutationRequest {{ Action = {Action}, Authorization = [REDACTED], Binding = [REDACTED], ReadBinding = [REDACTED], TypedValue = [REDACTED] }}";
}

public enum BaiduCandidateBuiltInScript
{
    Observe,
    Mutate,
    ObserveTransfer,
    MutateTransfer,
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

public sealed record BaiduTransferCandidateItem
{
    public BaiduTransferCandidateItem(string exactId, RemoteResourceKind kind, string serverName)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        ExactId = Validate(exactId, nameof(exactId));
        Kind = kind;
        ServerName = Validate(serverName, nameof(serverName));
    }

    public string ExactId { get; }
    public RemoteResourceKind Kind { get; }
    public string ServerName { get; }

    public override string ToString() =>
        $"BaiduTransferCandidateItem {{ Kind = {Kind}, ExactId = [REDACTED], ServerName = [REDACTED] }}";

    private static string Validate(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 512 || value.Any(char.IsControl))
            throw new ArgumentException("The candidate item field is invalid.", parameterName);
        return value;
    }
}

public sealed record BaiduTransferCandidateObservation(
    BaiduCandidatePageState PageState,
    string BaiduUserId,
    string? ParentExactId,
    RemotePath? CanonicalPath,
    ImmutableArray<string> AncestorExactIds,
    ImmutableArray<BaiduTransferCandidateItem> Items,
    int TotalCount,
    bool ReachedEnd,
    bool IsComplete,
    bool IsStable,
    string GenerationToken,
    string ObservationToken)
{
    public override string ToString() =>
        $"BaiduTransferCandidateObservation {{ PageState = {PageState}, ItemCount = {Items.Length}, TotalCount = {TotalCount}, ReachedEnd = {ReachedEnd}, Complete = {IsComplete}, Stable = {IsStable}, Identity = [REDACTED], Path = [REDACTED], Items = [REDACTED], Tokens = [REDACTED] }}";
}

public readonly record struct BaiduTransferCandidateDomResult(
    bool IsSuccess,
    BaiduAutomationDenialReason DenialReason,
    BaiduTransferCandidateObservation? Observation)
{
    public static BaiduTransferCandidateDomResult Success(BaiduTransferCandidateObservation observation) =>
        new(true, BaiduAutomationDenialReason.None,
            observation ?? throw new ArgumentNullException(nameof(observation)));

    public static BaiduTransferCandidateDomResult Denied(BaiduAutomationDenialReason reason) =>
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

    ValueTask<IBaiduCandidateMutationPermit?> TryAcquireMutationPermitAsync(
        BaiduCandidateDomRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<IBaiduCandidateMutationPermit?> TryAcquireTransferMutationPermitAsync(
        BaiduCandidateDomMutationRequest request,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IBaiduCandidateMutationPermit?>(null);
}

public interface IBaiduCandidateMutationPermit : IAsyncDisposable
{
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

public interface IBaiduTransferCandidateDomExecutor
{
    Task<BaiduTransferCandidateDomResult> ObserveTransferAsync(
        BaiduCandidateDomReadRequest request,
        CancellationToken cancellationToken = default);

    Task<BaiduTransferCandidateDomResult> MutateTransferAsync(
        BaiduCandidateDomMutationRequest request,
        BaiduTransferCandidateObservation observation,
        CancellationToken cancellationToken = default);
}
