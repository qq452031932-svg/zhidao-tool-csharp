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

/// <summary>Distinguishes complete destination inventory from picker-only child-folder candidates.</summary>
public enum TransferDirectoryListingScope
{
    AllItems = 0,
    FolderCandidates = 1,
}

public sealed record TransferRemoteItem
{
    public TransferRemoteItem(
        RemoteItemIdentity identity,
        TransferRemoteItemKind kind,
        string exactServerName)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        ArgumentException.ThrowIfNullOrWhiteSpace(exactServerName);
        if (exactServerName.Length > 512 || exactServerName.Any(char.IsControl))
            throw new ArgumentException("The exact server name is invalid.", nameof(exactServerName));
        Kind = kind;
        ExactServerName = exactServerName;
    }

    public RemoteItemIdentity Identity { get; }
    public TransferRemoteItemKind Kind { get; }
    public string ExactServerName { get; }
}

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
        Scope = TransferDirectoryListingScope.AllItems;
        TopLevelItems = Array.AsReadOnly(snapshot);
        ObservationFingerprint = observationFingerprint;
        DetailedItems = Array.AsReadOnly(snapshot.Select(item => new TransferRemoteItem(
            item, TransferRemoteItemKind.File, item.DisplayedTopLevelName)).ToArray());
        TotalCount = snapshot.Length;
        ReachedEnd = isComplete;
        AncestorExactIds = Array.Empty<string>();
    }

    private TransferDirectoryListing(
        RemoteDirectory directory,
        IReadOnlyList<string> ancestorExactIds,
        IReadOnlyList<TransferRemoteItem> items,
        int totalCount,
        bool reachedEnd,
        bool isStable,
        string generationToken,
        string baiduUserId,
        string observationFingerprint,
        TransferDirectoryListingScope scope)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(ancestorExactIds);
        ArgumentNullException.ThrowIfNull(items);
        if (!Enum.IsDefined(scope)) throw new ArgumentOutOfRangeException(nameof(scope));
        WebAutomationAttemptRecord.ValidateFingerprint(observationFingerprint, nameof(observationFingerprint));
        if (totalCount < 0 || totalCount != items.Count)
            throw new ArgumentOutOfRangeException(nameof(totalCount));
        var itemSnapshot = items.Select(item => item ??
            throw new ArgumentException("Directory items cannot contain null values.", nameof(items))).ToArray();
        if (itemSnapshot.Select(item => item.Identity.ExactId).Distinct(StringComparer.Ordinal).Count() != itemSnapshot.Length)
            throw new ArgumentException("Directory item identities must be unique.", nameof(items));
        if (scope == TransferDirectoryListingScope.FolderCandidates && itemSnapshot.Any(item =>
                item.Kind != TransferRemoteItemKind.Folder || string.IsNullOrWhiteSpace(item.ExactServerName)))
            throw new ArgumentException("Folder candidates must be non-blank folders.", nameof(items));
        var ancestorSnapshot = ancestorExactIds.Select(item =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(item);
            if (item.Length > 512 || item.Any(char.IsControl))
                throw new ArgumentException("Directory ancestor identity is invalid.", nameof(ancestorExactIds));
            return item.Trim();
        }).ToArray();
        if (ancestorSnapshot.Distinct(StringComparer.Ordinal).Count() != ancestorSnapshot.Length)
            throw new ArgumentException("Directory ancestors must be unique.", nameof(ancestorExactIds));
        ArgumentException.ThrowIfNullOrWhiteSpace(generationToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(baiduUserId);
        if (generationToken.Length > 512 || generationToken.Any(char.IsControl) ||
            baiduUserId.Length > 512 || baiduUserId.Any(char.IsControl))
            throw new ArgumentException("Directory listing identity is invalid.");

        Directory = directory;
        AncestorExactIds = Array.AsReadOnly(ancestorSnapshot);
        DetailedItems = Array.AsReadOnly(itemSnapshot);
        TopLevelItems = Array.AsReadOnly(itemSnapshot.Select(item => item.Identity).ToArray());
        TotalCount = totalCount;
        ReachedEnd = reachedEnd;
        IsComplete = reachedEnd && totalCount == itemSnapshot.Length;
        IsStable = isStable;
        Scope = scope;
        GenerationToken = generationToken.Trim();
        BaiduUserId = baiduUserId.Trim();
        ObservationFingerprint = observationFingerprint;
    }

    public static TransferDirectoryListing Exact(
        RemoteDirectory directory,
        IReadOnlyList<string> ancestorExactIds,
        IReadOnlyList<TransferRemoteItem> items,
        int totalCount,
        bool reachedEnd,
        bool isStable,
        string generationToken,
        string baiduUserId,
        string observationFingerprint,
        TransferDirectoryListingScope scope = TransferDirectoryListingScope.AllItems) =>
        new(directory, ancestorExactIds, items, totalCount, reachedEnd, isStable,
            generationToken, baiduUserId, observationFingerprint, scope);

    public bool IsComplete { get; }
    public bool IsStable { get; }
    public TransferDirectoryListingScope Scope { get; }
    public IReadOnlyList<RemoteItemIdentity> TopLevelItems { get; }
    public RemoteDirectory? Directory { get; }
    public IReadOnlyList<string> AncestorExactIds { get; }
    public IReadOnlyList<TransferRemoteItem> DetailedItems { get; }
    public int TotalCount { get; }
    public bool ReachedEnd { get; }
    public string? GenerationToken { get; }
    public string? BaiduUserId { get; }
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
        string observationFingerprint,
        string? targetExactId = null)
    {
        if (!Enum.IsDefined(status)) throw new ArgumentOutOfRangeException(nameof(status));
        WebAutomationAttemptRecord.ValidateFingerprint(observationFingerprint, nameof(observationFingerprint));
        Status = status;
        AdapterRequestId = ValidateOptional(adapterRequestId, nameof(adapterRequestId));
        ExactRemoteItems = exactRemoteItems;
        IdentityMapping = identityMapping;
        ObservationFingerprint = observationFingerprint;
        TargetExactId = ValidateOptional(targetExactId, nameof(targetExactId));
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
    public string? TargetExactId { get; }

    public override string ToString() =>
        $"TransferOfficialAcceptance {{ Status = {Status}, Values = [REDACTED] }}";

    private static string? ValidateOptional(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Length > 512 || value.Any(char.IsControl))
            throw new ArgumentException("The official result identity is invalid.", parameterName);
        return value.Trim();
    }
}
