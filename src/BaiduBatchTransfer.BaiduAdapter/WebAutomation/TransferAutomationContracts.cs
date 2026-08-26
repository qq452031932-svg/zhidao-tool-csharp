using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.BaiduAdapter.WebAutomation;

public sealed record TransferListingCompleteness(
    bool IsComplete,
    bool IsStable,
    int TotalCount,
    bool ReachedEnd,
    string GenerationToken);

public enum TransferRemoteItemKind
{
    File,
    Folder,
}

public sealed record TransferRemoteItem(
    RemoteItemIdentity Identity,
    TransferRemoteItemKind Kind,
    string ExactServerName);

public sealed record TransferSourceListing
{
    public TransferSourceListing(TransferSourceEvidence evidence) =>
        Evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));

    public TransferSourceEvidence Evidence { get; }
    public RemoteItemIdentitySet ExactItems => Evidence.Items;
    public int TopLevelItemCount => Evidence.TotalCount;
    public string ObservationFingerprint => Evidence.ObservationFingerprint;

    public override string ToString() =>
        $"TransferSourceListing {{ ItemCount = {TopLevelItemCount}, Values = [REDACTED] }}";
}

public sealed record TransferDirectoryListing
{
    public TransferDirectoryListing(
        bool isComplete,
        bool isStable,
        IReadOnlyList<RemoteItemIdentity> topLevelItems,
        string observationFingerprint)
    {
        ArgumentNullException.ThrowIfNull(topLevelItems);
        WebAutomationAttemptRecord.ValidateFingerprint(observationFingerprint, nameof(observationFingerprint));
        var snapshot = topLevelItems
            .Select(item => item ?? throw new ArgumentException("Directory items cannot contain null values.", nameof(topLevelItems)))
            .ToArray();
        if (snapshot.Select(item => item.ExactId).Distinct(StringComparer.Ordinal).Count() != snapshot.Length)
            throw new ArgumentException("Directory item identities must be unique.", nameof(topLevelItems));
        IsComplete = isComplete;
        IsStable = isStable;
        TopLevelItems = Array.AsReadOnly(snapshot);
        ObservationFingerprint = observationFingerprint;
    }

    public bool IsComplete { get; }
    public bool IsStable { get; }
    public IReadOnlyList<RemoteItemIdentity> TopLevelItems { get; }
    public string ObservationFingerprint { get; }

    public override string ToString() =>
        $"TransferDirectoryListing {{ IsComplete = {IsComplete}, IsStable = {IsStable}, ItemCount = {TopLevelItems.Count}, Values = [REDACTED] }}";
}

public enum TransferOfficialAcceptanceStatus
{
    Accepted = 0,
    ExplicitlyRejected = 1,
    Unobserved = 2,
}

public sealed record TransferOfficialAcceptance
{
    public TransferOfficialAcceptance(
        TransferOfficialAcceptanceStatus status,
        string? adapterRequestId,
        RemoteItemIdentitySet? exactRemoteItems,
        TransferIdentityMapping? identityMapping,
        string observationFingerprint)
    {
        if (!Enum.IsDefined(status)) throw new ArgumentOutOfRangeException(nameof(status));
        WebAutomationAttemptRecord.ValidateFingerprint(observationFingerprint, nameof(observationFingerprint));
        Status = status;
        AdapterRequestId = string.IsNullOrWhiteSpace(adapterRequestId) ? null : adapterRequestId.Trim();
        ExactRemoteItems = exactRemoteItems;
        IdentityMapping = identityMapping;
        ObservationFingerprint = observationFingerprint;
    }

    public TransferOfficialAcceptance(
        TransferOfficialAcceptanceStatus status,
        string? adapterRequestId,
        RemoteItemIdentitySet? exactRemoteItems,
        string observationFingerprint)
        : this(status, adapterRequestId, exactRemoteItems, null, observationFingerprint)
    {
    }

    public TransferOfficialAcceptanceStatus Status { get; }
    public bool IsAccepted => Status == TransferOfficialAcceptanceStatus.Accepted;
    public string? AdapterRequestId { get; }
    public RemoteItemIdentitySet? ExactRemoteItems { get; }
    public TransferIdentityMapping? IdentityMapping { get; }
    public string ObservationFingerprint { get; }

    public override string ToString() =>
        $"TransferOfficialAcceptance {{ Status = {Status}, Values = [REDACTED] }}";
}
