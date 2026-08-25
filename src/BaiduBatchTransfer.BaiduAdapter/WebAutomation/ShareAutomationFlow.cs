using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.BaiduAdapter.WebAutomation;

public enum ShareOfficialAcceptanceStatus
{
    Accepted = 0,
    ExplicitlyRejected = 1,
    Unobserved = 2,
}

public sealed record ShareOfficialAcceptance
{
    private ShareOfficialAcceptance(
        ShareOfficialAcceptanceStatus status,
        string? adapterRequestId,
        string? shareId,
        string? shareUrl,
        RemoteItemIdentitySet? exactRemoteItems,
        EffectiveShareSettings? effectiveSettings,
        string observationFingerprint)
    {
        if (!Enum.IsDefined(status)) throw new ArgumentOutOfRangeException(nameof(status));
        ValidateFingerprint(observationFingerprint, nameof(observationFingerprint));

        if (status == ShareOfficialAcceptanceStatus.Accepted)
        {
            AdapterRequestId = RequireText(adapterRequestId, nameof(adapterRequestId));
            ShareId = RequireText(shareId, nameof(shareId));
            ShareUrl = RequireHttpsUrl(shareUrl, nameof(shareUrl));
            ExactRemoteItems = exactRemoteItems ?? throw new ArgumentNullException(nameof(exactRemoteItems));
            EffectiveSettings = effectiveSettings ?? throw new ArgumentNullException(nameof(effectiveSettings));
            if (!EffectiveSettings.IsFullyObserved)
                throw new ArgumentException(
                    "Accepted official evidence must contain complete share settings and an extraction code.",
                    nameof(effectiveSettings));
        }
        else if (adapterRequestId is not null || shareId is not null || shareUrl is not null ||
                 exactRemoteItems is not null || effectiveSettings is not null)
        {
            throw new ArgumentException("Only accepted evidence can contain share result details.");
        }

        Status = status;
        ObservationFingerprint = observationFingerprint;
    }

    public ShareOfficialAcceptanceStatus Status { get; }
    public bool IsAccepted => Status == ShareOfficialAcceptanceStatus.Accepted;
    public string? AdapterRequestId { get; }
    public string? ShareId { get; }
    public string? ShareUrl { get; }
    public RemoteItemIdentitySet? ExactRemoteItems { get; }
    public EffectiveShareSettings? EffectiveSettings { get; }
    public string ObservationFingerprint { get; }

    public static ShareOfficialAcceptance Accepted(
        string adapterRequestId,
        string shareId,
        string shareUrl,
        RemoteItemIdentitySet exactRemoteItems,
        EffectiveShareSettings effectiveSettings,
        string observationFingerprint) =>
        new(
            ShareOfficialAcceptanceStatus.Accepted,
            adapterRequestId,
            shareId,
            shareUrl,
            exactRemoteItems,
            effectiveSettings,
            observationFingerprint);

    public static ShareOfficialAcceptance ExplicitlyRejected(string observationFingerprint) =>
        new(ShareOfficialAcceptanceStatus.ExplicitlyRejected, null, null, null, null, null, observationFingerprint);

    public static ShareOfficialAcceptance Unobserved(string observationFingerprint) =>
        new(ShareOfficialAcceptanceStatus.Unobserved, null, null, null, null, null, observationFingerprint);

    public override string ToString() =>
        $"ShareOfficialAcceptance {{ Status = {Status}, AdapterRequestId = [REDACTED], ShareId = [REDACTED], ShareUrl = [REDACTED], ExactRemoteItems = [REDACTED], EffectiveSettings = [REDACTED], ObservationFingerprint = [REDACTED] }}";

    private static string RequireText(string? value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }

    private static string RequireHttpsUrl(string? value, string parameterName)
    {
        var candidate = RequireText(value, parameterName);
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("A valid HTTPS share URL is required.", parameterName);
        }

        return uri.AbsoluteUri;
    }

    private static void ValidateFingerprint(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        WebAutomationAttemptRecord.ValidateFingerprint(value, parameterName);
    }
}

public sealed record ShareOfficialListEntry
{
    public ShareOfficialListEntry(
        string shareId,
        string shareUrl,
        RemoteItemIdentitySet exactRemoteItems,
        EffectiveShareSettings effectiveSettings,
        string? displayedName = null,
        DateTimeOffset? createdAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shareId);
        ArgumentException.ThrowIfNullOrWhiteSpace(shareUrl);
        if (!Uri.TryCreate(shareUrl.Trim(), UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("A valid HTTPS share URL is required.", nameof(shareUrl));
        }
        ArgumentNullException.ThrowIfNull(exactRemoteItems);
        ArgumentNullException.ThrowIfNull(effectiveSettings);
        if (!effectiveSettings.IsFullyObserved)
            throw new ArgumentException("An official share-list entry requires complete settings.", nameof(effectiveSettings));

        ShareId = shareId.Trim();
        ShareUrl = uri.AbsoluteUri;
        ExactRemoteItems = exactRemoteItems;
        EffectiveSettings = effectiveSettings;
        DisplayedName = string.IsNullOrWhiteSpace(displayedName) ? null : displayedName.Trim();
        CreatedAt = createdAt;
    }

    public string ShareId { get; }
    public string ShareUrl { get; }
    public RemoteItemIdentitySet ExactRemoteItems { get; }
    public EffectiveShareSettings EffectiveSettings { get; }
    public string? DisplayedName { get; }
    public DateTimeOffset? CreatedAt { get; }

    public override string ToString() =>
        $"ShareOfficialListEntry {{ ShareId = [REDACTED], ShareUrl = [REDACTED], ExactRemoteItems = [REDACTED], EffectiveSettings = [REDACTED], DisplayedName = [REDACTED], CreatedAt = [REDACTED] }}";
}

public sealed record ShareOfficialListing
{
    public ShareOfficialListing(
        bool isComplete,
        bool isStable,
        IReadOnlyList<ShareOfficialListEntry> shares,
        string observationFingerprint)
    {
        ArgumentNullException.ThrowIfNull(shares);
        ArgumentNullException.ThrowIfNull(observationFingerprint);
        WebAutomationAttemptRecord.ValidateFingerprint(observationFingerprint, nameof(observationFingerprint));
        var snapshot = shares
            .Select(share => share ?? throw new ArgumentException("Share-list entries cannot contain null values.", nameof(shares)))
            .ToArray();
        if (snapshot.Select(share => share.ShareId).Distinct(StringComparer.Ordinal).Count() != snapshot.Length)
            throw new ArgumentException("Official share identities must be unique.", nameof(shares));

        IsComplete = isComplete;
        IsStable = isStable;
        Shares = Array.AsReadOnly(snapshot);
        ObservationFingerprint = observationFingerprint;
    }

    public bool IsComplete { get; }
    public bool IsStable { get; }
    public IReadOnlyList<ShareOfficialListEntry> Shares { get; }
    public string ObservationFingerprint { get; }

    public override string ToString() =>
        $"ShareOfficialListing {{ IsComplete = {IsComplete}, IsStable = {IsStable}, ShareCount = {Shares.Count}, ObservationFingerprint = [REDACTED], Shares = [REDACTED] }}";
}

public interface IShareAutomationSession
{
    Task SelectExactRemoteItemsAsSingleGroupAsync(
        RemoteItemIdentitySet remoteItems,
        CancellationToken cancellationToken);

    Task ConfigurePermanentShareAsync(
        PermanentShareRequest request,
        CancellationToken cancellationToken);

    Task<ShareOfficialListing> ReadCompleteStableOfficialShareListAsync(
        CancellationToken cancellationToken);

    Task PersistBaselineAsync(
        WebAutomationBaseline baseline,
        string observationFingerprint,
        CancellationToken cancellationToken);

    Task MarkSubmittingAsync(CancellationToken cancellationToken);

    Task<ShareOfficialAcceptance> CreateShareOnceAsync(CancellationToken cancellationToken);

    Task PersistOfficialAcceptanceAsync(
        ShareOfficialAcceptance acceptance,
        CancellationToken cancellationToken);
}

public sealed record ShareAutomationPreparation(WebAutomationBaseline Baseline)
{
    public ShareAutomationPreparation Validate()
    {
        ArgumentNullException.ThrowIfNull(Baseline);
        return this;
    }
}

public sealed class ShareAutomationFlow(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<BaiduAdapterResult<ShareCreationReceipt>> ExecuteAsync(
        IShareAutomationSession session,
        PermanentShareRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);

        var preparation = await PrepareAsync(session, request, cancellationToken).ConfigureAwait(false);
        if (!preparation.IsSuccess)
        {
            return BaiduAdapterResult<ShareCreationReceipt>.Failure(
                preparation.ErrorKind!.Value,
                adapterDiagnostic: null,
                preparation.RetryAfter,
                preparation.SubmissionAcceptance);
        }

        return await SubmitAsync(session, request, preparation.Value!.Baseline, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<BaiduAdapterResult<ShareAutomationPreparation>> PrepareAsync(
        IShareAutomationSession session,
        PermanentShareRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            await session.SelectExactRemoteItemsAsSingleGroupAsync(request.RemoteItems, cancellationToken)
                .ConfigureAwait(false);
            await session.ConfigurePermanentShareAsync(request, cancellationToken).ConfigureAwait(false);

            var baselineListing = await session
                .ReadCompleteStableOfficialShareListAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!IsStableComplete(baselineListing)) return PreparationFailed();

            var baseline = WebAutomationBaseline.Create(baselineListing.Shares.Select(share => share.ShareId));
            await session.PersistBaselineAsync(
                baseline,
                baselineListing.ObservationFingerprint,
                cancellationToken).ConfigureAwait(false);
            return BaiduAdapterResult<ShareAutomationPreparation>.Success(
                new ShareAutomationPreparation(baseline),
                SubmissionAcceptance.DefinitelyNotAccepted);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return PreparationFailed();
        }
    }

    public async Task<BaiduAdapterResult<ShareCreationReceipt>> SubmitAsync(
        IShareAutomationSession session,
        PermanentShareRequest request,
        WebAutomationBaseline baseline,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(baseline);

        var submitted = false;
        try
        {
            await session.SelectExactRemoteItemsAsSingleGroupAsync(request.RemoteItems, cancellationToken)
                .ConfigureAwait(false);
            await session.ConfigurePermanentShareAsync(request, cancellationToken).ConfigureAwait(false);

            await session.MarkSubmittingAsync(cancellationToken).ConfigureAwait(false);
            submitted = true;
            var acceptance = await session.CreateShareOnceAsync(cancellationToken).ConfigureAwait(false);
            if (acceptance.Status == ShareOfficialAcceptanceStatus.ExplicitlyRejected)
                return DefinitelyNotAccepted();
            if (acceptance.Status != ShareOfficialAcceptanceStatus.Accepted)
                return ResultPending();

            await session.PersistOfficialAcceptanceAsync(acceptance, cancellationToken).ConfigureAwait(false);
            if (!AcceptanceMatchesRequest(acceptance, request)) return ResultPending();

            var current = await session
                .ReadCompleteStableOfficialShareListAsync(cancellationToken)
                .ConfigureAwait(false);
            return ConfirmFromListing(request, baseline, acceptance, current);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return submitted ? ResultPending() : DefinitelyNotAccepted();
        }
    }

    public async Task<BaiduAdapterResult<ShareCreationReceipt>> ConfirmExactAsync(
        IShareAutomationSession session,
        PermanentShareRequest request,
        WebAutomationBaseline baseline,
        ShareOfficialAcceptance acceptedEvidence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(acceptedEvidence);
        if (!AcceptanceMatchesRequest(acceptedEvidence, request)) return ResultPending();

        try
        {
            var current = await session
                .ReadCompleteStableOfficialShareListAsync(cancellationToken)
                .ConfigureAwait(false);
            return ConfirmFromListing(request, baseline, acceptedEvidence, current);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return ResultPending();
        }
    }

    private static bool IsStableComplete(ShareOfficialListing listing) =>
        listing is { IsComplete: true, IsStable: true };

    private static bool AcceptanceMatchesRequest(
        ShareOfficialAcceptance acceptance,
        PermanentShareRequest request)
    {
        if (!acceptance.IsAccepted || acceptance.ExactRemoteItems != request.RemoteItems)
            return false;

        var settings = acceptance.EffectiveSettings!;
        if (settings.IsPermanent != true || settings.IsAccessUnlimited != true)
            return false;
        if (request.UsesRandomExtractionCode)
            return settings.ExtractionCodeBehavior == ShareExtractionCodeBehavior.Random;
        return settings.ExtractionCodeBehavior == ShareExtractionCodeBehavior.Fixed &&
               string.Equals(settings.ExtractionCode, request.FixedExtractionCode, StringComparison.Ordinal);
    }

    private static bool ExactlyMatchesAcceptedShare(
        ShareOfficialListEntry share,
        ShareOfficialAcceptance acceptance) =>
        string.Equals(share.ShareId, acceptance.ShareId, StringComparison.Ordinal) &&
        share.ExactRemoteItems == acceptance.ExactRemoteItems &&
        SettingsEqual(share.EffectiveSettings, acceptance.EffectiveSettings!);

    private static bool SettingsEqual(EffectiveShareSettings left, EffectiveShareSettings right) =>
        left.IsPermanent == right.IsPermanent &&
        left.IsAccessUnlimited == right.IsAccessUnlimited &&
        left.ExtractionCodeBehavior == right.ExtractionCodeBehavior &&
        string.Equals(left.ExtractionCode, right.ExtractionCode, StringComparison.Ordinal);

    private BaiduAdapterResult<ShareCreationReceipt> ConfirmFromListing(
        PermanentShareRequest request,
        WebAutomationBaseline baseline,
        ShareOfficialAcceptance acceptance,
        ShareOfficialListing current)
    {
        if (!IsStableComplete(current)) return ResultPending();

        var currentIds = current.Shares.Select(share => share.ShareId).ToHashSet(StringComparer.Ordinal);
        if (baseline.Items.Any(item => !currentIds.Contains(item.ExactId))) return ResultPending();

        var baselineIds = baseline.Items.Select(item => item.ExactId).ToHashSet(StringComparer.Ordinal);
        var added = current.Shares.Where(share => !baselineIds.Contains(share.ShareId)).ToArray();
        if (added.Length != 1 || !ExactlyMatchesAcceptedShare(added[0], acceptance))
            return ResultPending();

        return BaiduAdapterResult<ShareCreationReceipt>.Success(
            new ShareCreationReceipt(
                timeProvider.GetUtcNow(),
                acceptance.AdapterRequestId!,
                request,
                acceptance.ShareId!,
                acceptance.ShareUrl!,
                acceptance.EffectiveSettings!),
            SubmissionAcceptance.Unknown);
    }

    private static BaiduAdapterResult<ShareCreationReceipt> DefinitelyNotAccepted() =>
        BaiduAdapterResult<ShareCreationReceipt>.Failure(
            BaiduErrorKind.UnknownResponse,
            adapterDiagnostic: null,
            submissionAcceptance: SubmissionAcceptance.DefinitelyNotAccepted);

    private static BaiduAdapterResult<ShareAutomationPreparation> PreparationFailed() =>
        BaiduAdapterResult<ShareAutomationPreparation>.Failure(
            BaiduErrorKind.UnknownResponse,
            adapterDiagnostic: null,
            submissionAcceptance: SubmissionAcceptance.DefinitelyNotAccepted);

    private static BaiduAdapterResult<ShareCreationReceipt> ResultPending() =>
        BaiduAdapterResult<ShareCreationReceipt>.Failure(
            BaiduErrorKind.UnknownResponse,
            adapterDiagnostic: null,
            submissionAcceptance: SubmissionAcceptance.Unknown);
}
