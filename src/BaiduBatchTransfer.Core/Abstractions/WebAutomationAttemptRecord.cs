using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Core.Abstractions;

public enum WebAutomationOwnerKind
{
    Transfer,
    TransferShare,
    StandaloneShare,
}

public enum WebAutomationAttemptState
{
    Preparing,
    BaselinePersisted,
    Submitting,
    ResultPending,
    UserHandoff,
    Succeeded,
    DefinitelyNotSubmitted,
    NeedsDecision,
}

public sealed record WebAutomationBaseline
{
    private WebAutomationBaseline(ImmutableArray<RemoteItemIdentity> items, string fingerprint)
    {
        Items = items;
        Fingerprint = fingerprint;
    }

    public ImmutableArray<RemoteItemIdentity> Items { get; }
    public string Fingerprint { get; }

    public static WebAutomationBaseline Create(IEnumerable<RemoteItemIdentity> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var snapshot = items
            .Select(item => item ?? throw new ArgumentException("Baseline items cannot contain null.", nameof(items)))
            .OrderBy(item => item.ExactId, StringComparer.Ordinal)
            .ToImmutableArray();
        if (snapshot.Select(item => item.ExactId).Distinct(StringComparer.Ordinal).Count() != snapshot.Length)
            throw new ArgumentException("Baseline item identities must be unique.", nameof(items));
        foreach (var item in snapshot) _ = RemoteItemIdentitySet.GetCanonicalSortKey(item.ExactId);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "web-automation-baseline-v1");
        foreach (var item in snapshot) Append(hash, item.ExactId);
        return new(snapshot, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    public override string ToString() =>
        $"WebAutomationBaseline {{ Count = {Items.Length}, Fingerprint = [REDACTED], Items = [REDACTED] }}";

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(bytes.Length)));
        hash.AppendData(bytes);
    }
}

public sealed record WebAutomationAttemptRecord
{
    public WebAutomationAttemptRecord(
        Guid operationId,
        Guid accountId,
        int generation,
        WebAutomationOwnerKind ownerKind,
        WebAutomationAttemptState state,
        WebAutomationBaseline? baseline,
        string? observationFingerprint,
        bool isWaitingForUser,
        long revision,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An operation is required.", nameof(operationId));
        if (accountId == Guid.Empty) throw new ArgumentException("An account is required.", nameof(accountId));
        if (generation < 1) throw new ArgumentOutOfRangeException(nameof(generation));
        if (!Enum.IsDefined(ownerKind)) throw new ArgumentOutOfRangeException(nameof(ownerKind));
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
        if (revision < 0) throw new ArgumentOutOfRangeException(nameof(revision));
        if (updatedAt < createdAt) throw new ArgumentOutOfRangeException(nameof(updatedAt));
        ValidateFingerprint(observationFingerprint, nameof(observationFingerprint));
        if (RequiresBaseline(state) && baseline is null)
        {
            throw new ArgumentException("This state requires an exact baseline.", nameof(baseline));
        }

        OperationId = operationId;
        AccountId = accountId;
        Generation = generation;
        OwnerKind = ownerKind;
        State = state;
        Baseline = baseline;
        ObservationFingerprint = observationFingerprint;
        IsWaitingForUser = isWaitingForUser;
        Revision = revision;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    public Guid OperationId { get; }
    public Guid AccountId { get; }
    public int Generation { get; }
    public WebAutomationOwnerKind OwnerKind { get; }
    public WebAutomationAttemptState State { get; }
    public WebAutomationBaseline? Baseline { get; }
    public ImmutableArray<RemoteItemIdentity> BaselineRemoteItems => Baseline?.Items ?? [];
    public string? ObservationFingerprint { get; }
    public bool IsWaitingForUser { get; }
    public long Revision { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; }

    public static WebAutomationAttemptRecord Create(
        Guid operationId,
        Guid accountId,
        int generation,
        WebAutomationOwnerKind ownerKind,
        DateTimeOffset createdAt) =>
        new(operationId, accountId, generation, ownerKind, WebAutomationAttemptState.Preparing,
            null, null, false, 0, createdAt, createdAt);

    public override string ToString() =>
        $"WebAutomationAttemptRecord {{ OperationId = {OperationId}, AccountId = {AccountId}, Generation = {Generation}, OwnerKind = {OwnerKind}, State = {State}, Baseline = [REDACTED], Observation = [REDACTED], IsWaitingForUser = {IsWaitingForUser}, Revision = {Revision} }}";

    public static void ValidateFingerprint(string? value, string parameterName)
    {
        if (value is null) return;
        if (value.Length != 64 || value.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("A sanitized lowercase SHA-256 fingerprint is required.", parameterName);
    }

    private static bool RequiresBaseline(WebAutomationAttemptState state) => state is
        WebAutomationAttemptState.BaselinePersisted or WebAutomationAttemptState.Submitting or
        WebAutomationAttemptState.ResultPending or WebAutomationAttemptState.Succeeded or
        WebAutomationAttemptState.NeedsDecision;
}
