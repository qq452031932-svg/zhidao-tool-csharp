using BaiduBatchTransfer.BaiduAdapter.WebAutomation;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.App.Features.ManagedBrowser;

internal interface IManagedShareSubmissionBoundaries
{
    Task<ManagedBrowserResponse> SendAsync(
        ManagedBrowserRequest request,
        CancellationToken cancellationToken);

    Task PersistBaselineAsync(
        WebAutomationBaseline baseline,
        string observationFingerprint,
        CancellationToken cancellationToken);

    Task MarkSubmittingAsync(CancellationToken cancellationToken);

    Task PersistOfficialAcceptanceAsync(
        ShareOfficialAcceptance acceptance,
        CancellationToken cancellationToken);
}

internal sealed class ManagedShareSubmissionSession : IShareAutomationSession
{
    private readonly Guid operationId;
    private readonly Guid accountId;
    private readonly long generation;
    private readonly ManagedBrowserShareSubmissionContext context;
    private readonly IManagedShareSubmissionBoundaries boundaries;
    private readonly bool recoveryOnly;
    private readonly RemoteItemIdentitySet exactItems;
    private PermanentShareRequest? configuredRequest;
    private ManagedBrowserShareOfficialListing? lastListing;
    private WebAutomationBaseline? baseline;
    private string? baselineObservationFingerprint;
    private bool submitting;
    private bool submitCalled;

    internal ManagedShareSubmissionSession(
        Guid operationId,
        Guid accountId,
        long generation,
        ManagedBrowserShareSubmissionContext context,
        IManagedShareSubmissionBoundaries boundaries,
        bool recoveryOnly,
        WebAutomationBaseline? persistedBaseline = null,
        string? baselineObservationFingerprint = null)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An operation id is required.", nameof(operationId));
        if (accountId == Guid.Empty) throw new ArgumentException("An account id is required.", nameof(accountId));
        if (generation < 1) throw new ArgumentOutOfRangeException(nameof(generation));
        this.context = context ?? throw new ArgumentNullException(nameof(context));
        this.boundaries = boundaries ?? throw new ArgumentNullException(nameof(boundaries));
        this.operationId = operationId;
        this.accountId = accountId;
        this.generation = generation;
        this.recoveryOnly = recoveryOnly;
        exactItems = context.ExactItems;
        if (!string.Equals(exactItems.Fingerprint, context.ExactItemSetFingerprint, StringComparison.Ordinal))
            throw new ArgumentException("The share context exact-item fingerprint is inconsistent.", nameof(context));
        if ((persistedBaseline is null) != (baselineObservationFingerprint is null))
            throw new ArgumentException("Persisted baseline identity and observation must be supplied together.");
        if (baselineObservationFingerprint is not null)
            ManagedBrowserSubmitShareOnceRequest.ValidateFingerprint(
                baselineObservationFingerprint, nameof(baselineObservationFingerprint));
        baseline = persistedBaseline;
        this.baselineObservationFingerprint = baselineObservationFingerprint;
    }

    public Task SelectExactRemoteItemsAsSingleGroupAsync(
        RemoteItemIdentitySet remoteItems,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(remoteItems);
        if (remoteItems != exactItems)
            throw new InvalidOperationException("Managed sharing is bound to one exact remote-item set.");
        return Task.CompletedTask;
    }

    public Task ConfigurePermanentShareAsync(
        PermanentShareRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        var requestMode = request.UsesRandomExtractionCode
            ? ShareExtractionCodeBehavior.Random
            : ShareExtractionCodeBehavior.Fixed;
        if (request.RemoteItems != exactItems || !request.IsPermanent || !request.IsAccessUnlimited ||
            requestMode != context.CodeMode ||
            !string.Equals(request.FixedExtractionCode, context.FixedCode, StringComparison.Ordinal))
            throw new InvalidOperationException("The permanent-share request does not match the immutable Managed context.");
        configuredRequest = request;
        return Task.CompletedTask;
    }

    public async Task<ShareOfficialListing> ReadCompleteStableOfficialShareListAsync(
        CancellationToken cancellationToken)
    {
        var request = Request(
            ManagedBrowserAction.ReadShareOfficialListing,
            new ManagedBrowserReadShareOfficialListingRequest(context));
        var response = await boundaries.SendAsync(request, cancellationToken).ConfigureAwait(false);
        ManagedBrowserProtocol.ValidateResponseFor(request, response);
        var result = (ManagedBrowserReadShareOfficialListingResult)response.Result;
        if (result.Kind != ManagedBrowserShareListingResultKind.Observed || result.Listing is null)
            throw new InvalidOperationException("A complete stable official share listing was not proven.");
        lastListing = result.Listing;
        return new ShareOfficialListing(
            true,
            true,
            result.Listing.Entries.Select(ToOfficialEntry).ToArray(),
            result.Listing.ObservationFingerprint);
    }

    public async Task PersistBaselineAsync(
        WebAutomationBaseline baseline,
        string observationFingerprint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        if (recoveryOnly) throw new InvalidOperationException("A recovery-only share session cannot create a new baseline.");
        if (configuredRequest is null || lastListing is null ||
            !string.Equals(baseline.Fingerprint, lastListing.ShareIdsFingerprint, StringComparison.Ordinal) ||
            !string.Equals(observationFingerprint, lastListing.ObservationFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("The baseline is not the latest complete stable official listing.");
        await boundaries.PersistBaselineAsync(baseline, observationFingerprint, cancellationToken)
            .ConfigureAwait(false);
        this.baseline = baseline;
        baselineObservationFingerprint = observationFingerprint;
    }

    public async Task MarkSubmittingAsync(CancellationToken cancellationToken)
    {
        if (recoveryOnly) throw new InvalidOperationException("A recovery-only share session cannot submit.");
        if (baseline is null || baselineObservationFingerprint is null || configuredRequest is null || submitting)
            throw new InvalidOperationException("A persisted exact share baseline is required before submission.");
        await boundaries.MarkSubmittingAsync(cancellationToken).ConfigureAwait(false);
        submitting = true;
    }

    public async Task<ShareOfficialAcceptance> CreateShareOnceAsync(CancellationToken cancellationToken)
    {
        if (recoveryOnly) throw new InvalidOperationException("A recovery-only share session cannot submit.");
        if (!submitting || baseline is null || baselineObservationFingerprint is null || configuredRequest is null)
            throw new InvalidOperationException("The share operation is not durably ready for submission.");
        if (submitCalled) throw new InvalidOperationException("The share submit boundary may be crossed at most once.");
        submitCalled = true;

        var submitRequest = Request(
            ManagedBrowserAction.SubmitShareOnce,
            new ManagedBrowserSubmitShareOnceRequest(
                context, baseline.Fingerprint, baselineObservationFingerprint));
        var submitResponse = await boundaries.SendAsync(submitRequest, cancellationToken).ConfigureAwait(false);
        ManagedBrowserProtocol.ValidateResponseFor(submitRequest, submitResponse);
        var submit = (ManagedBrowserSubmitShareOnceResult)submitResponse.Result;
        if (submit.DispatchState == ManagedBrowserShareSubmitDispatchState.KnownNotDispatched)
            return ShareOfficialAcceptance.ExplicitlyRejected(baselineObservationFingerprint);
        if (submit.DispatchState == ManagedBrowserShareSubmitDispatchState.DispatchOutcomeUnknown)
            return ShareOfficialAcceptance.Unobserved(baselineObservationFingerprint);

        var outcomeRequest = Request(
            ManagedBrowserAction.ObserveShareSubmissionOutcome,
            new ManagedBrowserObserveShareSubmissionOutcomeRequest(
                context, baseline.Fingerprint, baselineObservationFingerprint));
        var outcomeResponse = await boundaries.SendAsync(outcomeRequest, cancellationToken).ConfigureAwait(false);
        ManagedBrowserProtocol.ValidateResponseFor(outcomeRequest, outcomeResponse);
        var outcome = (ManagedBrowserShareSubmissionOutcomeResult)outcomeResponse.Result;
        return outcome.Kind switch
        {
            ManagedBrowserShareOutcomeKind.AcceptedBound => ToAcceptance(outcome),
            ManagedBrowserShareOutcomeKind.ExplicitlyRejected =>
                ShareOfficialAcceptance.ExplicitlyRejected(baselineObservationFingerprint),
            _ => ShareOfficialAcceptance.Unobserved(baselineObservationFingerprint),
        };
    }

    public async Task PersistOfficialAcceptanceAsync(
        ShareOfficialAcceptance acceptance,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(acceptance);
        if (!submitCalled || !acceptance.IsAccepted || acceptance.ExactRemoteItems != exactItems ||
            acceptance.EffectiveSettings is not { IsPermanent: true, IsAccessUnlimited: true } settings ||
            settings.ExtractionCodeBehavior != context.CodeMode ||
            context.CodeMode == ShareExtractionCodeBehavior.Fixed &&
            !string.Equals(settings.ExtractionCode, context.FixedCode, StringComparison.Ordinal))
            throw new InvalidOperationException("Official acceptance does not match the submitted exact item and settings.");
        await boundaries.PersistOfficialAcceptanceAsync(acceptance, cancellationToken).ConfigureAwait(false);
    }

    private ManagedBrowserRequest Request(
        ManagedBrowserAction action,
        ManagedBrowserRequestPayload payload) =>
        new(Guid.NewGuid(), operationId, accountId, generation, action, payload);

    private static ShareOfficialListEntry ToOfficialEntry(ManagedBrowserShareOfficialEntry entry) => new(
        entry.ShareId,
        entry.ShareUrl,
        entry.ExactItems,
        new EffectiveShareSettings(
            entry.IsPermanent,
            entry.IsAccessUnlimited,
            entry.CodeMode,
            entry.EffectiveCode));

    private ShareOfficialAcceptance ToAcceptance(ManagedBrowserShareSubmissionOutcomeResult outcome) =>
        ShareOfficialAcceptance.Accepted(
            outcome.AdapterRequestId!,
            outcome.ShareId!,
            outcome.ShareUrl!,
            exactItems,
            new EffectiveShareSettings(
                outcome.IsPermanent,
                outcome.IsAccessUnlimited,
                outcome.CodeMode!.Value,
                outcome.EffectiveCode),
            outcome.OfficialObservationFingerprint!);
}
