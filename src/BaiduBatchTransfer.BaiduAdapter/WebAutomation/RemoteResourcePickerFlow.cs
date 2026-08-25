using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.BaiduAdapter.WebAutomation;

public enum RemoteResourcePickerDecision
{
    ConfirmSelection = 0,
    Cancel = 1,
    WindowClosed = 2,
}

public enum RemoteResourcePickerDisposition
{
    Confirmed = 0,
    Cancelled = 1,
    UnsafeObservation = 2,
}

public sealed record RemoteResourceObservedItem(
    string? StableId,
    string DisplayName,
    RemoteResourceKind Kind,
    RemotePath ParentPath,
    bool IsSelected)
{
    public override string ToString() =>
        $"RemoteResourceObservedItem {{ StableId = [REDACTED], DisplayName = [REDACTED], Kind = {Kind}, ParentPath = [REDACTED], IsSelected = {IsSelected} }}";
}

public sealed record RemoteResourceSelectionPage
{
    public RemoteResourceSelectionPage(
        Guid accountId,
        bool isStable,
        bool isFullyLoaded,
        string? nextPageToken,
        IReadOnlyList<RemoteResourceObservedItem> items,
        string observationFingerprint)
    {
        ArgumentNullException.ThrowIfNull(items);
        WebAutomationAttemptRecord.ValidateFingerprint(
            observationFingerprint,
            nameof(observationFingerprint));
        if (nextPageToken is { Length: > 256 })
            throw new ArgumentOutOfRangeException(nameof(nextPageToken));

        AccountId = accountId;
        IsStable = isStable;
        IsFullyLoaded = isFullyLoaded;
        NextPageToken = string.IsNullOrWhiteSpace(nextPageToken) ? null : nextPageToken;
        Items = Array.AsReadOnly(items
            .Select(item => item ?? throw new ArgumentException(
                "Observed resource items cannot contain null values.",
                nameof(items)))
            .ToArray());
        ObservationFingerprint = observationFingerprint;
    }

    public Guid AccountId { get; }
    public bool IsStable { get; }
    public bool IsFullyLoaded { get; }
    public string? NextPageToken { get; }
    public IReadOnlyList<RemoteResourceObservedItem> Items { get; }
    public string ObservationFingerprint { get; }

    public override string ToString() =>
        $"RemoteResourceSelectionPage {{ AccountId = {AccountId}, IsStable = {IsStable}, IsFullyLoaded = {IsFullyLoaded}, HasNextPage = {NextPageToken is not null}, ItemCount = {Items.Count}, Items = [REDACTED], ObservationFingerprint = [REDACTED] }}";
}

public sealed record RemoteResourcePickerOutcome(
    RemoteResourcePickerDisposition Disposition,
    IReadOnlyList<OwnedRemoteSelection> Selections,
    string SafeReason)
{
    public override string ToString() =>
        $"RemoteResourcePickerOutcome {{ Disposition = {Disposition}, SelectionCount = {Selections.Count}, Selections = [REDACTED], SafeReason = [REDACTED] }}";
}

public interface IRemoteResourcePickerSession
{
    Task<RemoteResourcePickerDecision> WaitForApplicationDecisionAsync(
        CancellationToken cancellationToken = default);

    Task<RemoteResourceSelectionPage> ReadSelectionPageAsync(
        string? pageToken,
        CancellationToken cancellationToken = default);
}

public sealed class RemoteResourcePickerFlow
{
    private const int MaximumPageCount = 256;
    private const int MaximumObservedItemCount = 10_000;

    public async Task<RemoteResourcePickerOutcome> ExecuteAsync(
        IRemoteResourcePickerSession session,
        Guid expectedAccountId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (expectedAccountId == Guid.Empty)
            throw new ArgumentException("An expected account is required.", nameof(expectedAccountId));

        var decision = await session.WaitForApplicationDecisionAsync(cancellationToken)
            .ConfigureAwait(false);
        if (decision is RemoteResourcePickerDecision.Cancel or RemoteResourcePickerDecision.WindowClosed)
            return Cancelled();
        if (decision != RemoteResourcePickerDecision.ConfirmSelection)
            return Unsafe();

        var selections = new List<OwnedRemoteSelection>();
        var stableIds = new HashSet<string>(StringComparer.Ordinal);
        var requestedTokens = new HashSet<string>(StringComparer.Ordinal);
        string? pageToken = null;
        var observedItemCount = 0;

        for (var pageNumber = 0; pageNumber < MaximumPageCount; pageNumber++)
        {
            var page = await session.ReadSelectionPageAsync(pageToken, cancellationToken)
                .ConfigureAwait(false);
            if (page is null ||
                page.AccountId != expectedAccountId ||
                !page.IsStable ||
                !page.IsFullyLoaded)
            {
                return Unsafe();
            }

            observedItemCount = checked(observedItemCount + page.Items.Count);
            if (observedItemCount > MaximumObservedItemCount)
                return Unsafe();

            foreach (var item in page.Items.Where(item => item.IsSelected))
            {
                if (string.IsNullOrWhiteSpace(item.StableId) ||
                    string.IsNullOrWhiteSpace(item.DisplayName) ||
                    !Enum.IsDefined(item.Kind) ||
                    item.ParentPath is null ||
                    !stableIds.Add(item.StableId))
                {
                    return Unsafe();
                }

                try
                {
                    var exactItems = new RemoteItemIdentitySet(
                        [new RemoteItemIdentity(item.StableId, item.DisplayName)]);
                    selections.Add(new OwnedRemoteSelection(
                        expectedAccountId,
                        exactItems,
                        item.DisplayName,
                        item.Kind,
                        item.ParentPath));
                }
                catch (ArgumentException)
                {
                    return Unsafe();
                }
            }

            if (page.NextPageToken is null)
                return new(
                    RemoteResourcePickerDisposition.Confirmed,
                    selections.AsReadOnly(),
                    "用户已确认完整、稳定且账号匹配的精确资源选择。");
            if (!requestedTokens.Add(page.NextPageToken))
                return Unsafe();
            pageToken = page.NextPageToken;
        }

        return Unsafe();
    }

    private static RemoteResourcePickerOutcome Cancelled() => new(
        RemoteResourcePickerDisposition.Cancelled,
        Array.Empty<OwnedRemoteSelection>(),
        "用户取消或关闭了资源选择窗口。");

    private static RemoteResourcePickerOutcome Unsafe() => new(
        RemoteResourcePickerDisposition.UnsafeObservation,
        Array.Empty<OwnedRemoteSelection>(),
        "资源选择证据不完整、不稳定或与账号不匹配。");
}
