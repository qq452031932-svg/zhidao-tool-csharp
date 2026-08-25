using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.BaiduAdapter.WebAutomation;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.App.Features.BrowserAutomation;

public interface IShareAutomationAcceptanceEvidenceStore
{
    Task PersistAsync(
        ShareOperationAutomationContext context,
        ShareOfficialAcceptance acceptance,
        CancellationToken cancellationToken = default);

    Task<ShareOfficialAcceptance?> GetAsync(
        ShareOperationAutomationContext context,
        CancellationToken cancellationToken = default);
}

public sealed class WebViewBaiduShareAdapter : IOperationBoundBaiduShareAdapter
{
    private static readonly Uri OfficialFilesUri = new("https://pan.baidu.com/disk/main");

    private readonly IBaiduShareAdapter readOnlyAdapter;
    private readonly IVisibleWebAutomationBroker broker;
    private readonly IAccountProfilePathPolicy profilePaths;
    private readonly ShareAutomationFlow flow;
    private readonly string applicationBuildVersion;
    private readonly IWebAutomationAttemptStore? attemptStore;
    private readonly IShareAutomationAcceptanceEvidenceStore? acceptanceStore;

    public WebViewBaiduShareAdapter(
        IBaiduShareAdapter readOnlyAdapter,
        IVisibleWebAutomationBroker broker,
        IAccountProfilePathPolicy profilePaths,
        ShareAutomationFlow flow,
        string applicationBuildVersion,
        IWebAutomationAttemptStore? attemptStore = null,
        IShareAutomationAcceptanceEvidenceStore? acceptanceStore = null)
    {
        this.readOnlyAdapter = readOnlyAdapter ?? throw new ArgumentNullException(nameof(readOnlyAdapter));
        this.broker = broker ?? throw new ArgumentNullException(nameof(broker));
        this.profilePaths = profilePaths ?? throw new ArgumentNullException(nameof(profilePaths));
        this.flow = flow ?? throw new ArgumentNullException(nameof(flow));
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationBuildVersion);
        this.applicationBuildVersion = applicationBuildVersion.Trim();
        this.attemptStore = attemptStore;
        this.acceptanceStore = acceptanceStore;
    }

    public Task<BaiduAdapterResult<ShareCreationReceipt>> CreatePermanentShareAsync(
        AccountSession session,
        PermanentShareRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult(NotValidated<ShareCreationReceipt>(SubmissionAcceptance.DefinitelyNotAccepted));

    public Task<BaiduAdapterResult<BaiduSharePage>> ListSharesAsync(
        AccountSession session,
        string? cursor,
        CancellationToken cancellationToken) =>
        readOnlyAdapter.ListSharesAsync(session, cursor, cancellationToken);

    public async Task<BaiduAdapterResult<SharePreparationReceipt>> PreparePermanentShareForOperationAsync(
        ShareOperationAutomationContext context,
        AccountSession session,
        PermanentShareRequest request,
        CancellationToken cancellationToken)
    {
        ValidateOperationIdentity(context, session, request);
        if (attemptStore is not null)
        {
            var existing = await LoadAttemptAsync(context, cancellationToken).ConfigureAwait(false);
            var stage = EffectiveRecoverableStage(existing, context);
            if (stage == WebAutomationAttemptState.BaselinePersisted)
            {
                var prepared = Prepared();
                if (existing!.State != WebAutomationAttemptState.UserHandoff) return prepared;
                return await ResumeWithoutActionAsync(
                    context,
                    prepared,
                    SubmissionAcceptance.DefinitelyNotAccepted,
                    cancellationToken).ConfigureAwait(false);
            }
            if (existing is not null && stage != WebAutomationAttemptState.Preparing)
                return Unknown<SharePreparationReceipt>(SubmissionAcceptance.Unknown);
        }

        var automationRequest = Request(
            context,
            async (visibleSession, visibleContext, token) =>
            {
                if (visibleSession is not IShareAutomationSession automationSession)
                    return NotValidated<SharePreparationReceipt>(SubmissionAcceptance.DefinitelyNotAccepted);
                var result = await flow.PrepareAsync(
                    new PersistentShareAutomationSession(
                        automationSession,
                        visibleContext,
                        context,
                        acceptanceStore),
                    request,
                    token).ConfigureAwait(false);
                return result.IsSuccess
                    ? Prepared()
                    : BaiduAdapterResult<SharePreparationReceipt>.Failure(
                        result.ErrorKind!.Value,
                        adapterDiagnostic: null,
                        result.RetryAfter,
                        result.SubmissionAcceptance);
            });
        try
        {
            var result = await broker.EnqueueAsync(automationRequest, cancellationToken).ConfigureAwait(false);
            if (result.Disposition == VisibleWebAutomationDisposition.ActionCompleted && result.Value is not null)
                return result.Value;
            return Unknown<SharePreparationReceipt>(SubmissionAcceptance.DefinitelyNotAccepted);
        }
        catch (WebAutomationAuthorizationException)
        {
            return NotValidated<SharePreparationReceipt>(SubmissionAcceptance.DefinitelyNotAccepted);
        }
    }

    public async Task<BaiduAdapterResult<ShareCreationReceipt>> SubmitPermanentShareForOperationAsync(
        ShareOperationAutomationContext context,
        AccountSession session,
        PermanentShareRequest request,
        CancellationToken cancellationToken)
    {
        ValidateOperationIdentity(context, session, request);
        if (attemptStore is null || acceptanceStore is null)
            return NotValidated<ShareCreationReceipt>(SubmissionAcceptance.DefinitelyNotAccepted);

        var attempt = await LoadAttemptAsync(context, cancellationToken).ConfigureAwait(false);
        if (EffectiveRecoverableStage(attempt, context) != WebAutomationAttemptState.BaselinePersisted)
        {
            return Unknown<ShareCreationReceipt>(
                attempt?.State == WebAutomationAttemptState.UserHandoff
                    ? SubmissionAcceptance.Unknown
                    : SubmissionAcceptance.DefinitelyNotAccepted);
        }

        var baseline = attempt!.Baseline!;
        var automationRequest = Request(
            context,
            async (visibleSession, visibleContext, token) =>
            {
                if (visibleSession is not IShareAutomationSession automationSession)
                    return NotValidated<ShareCreationReceipt>(SubmissionAcceptance.DefinitelyNotAccepted);
                var result = await flow.SubmitAsync(
                    new PersistentShareAutomationSession(
                        automationSession,
                        visibleContext,
                        context,
                        acceptanceStore),
                    request,
                    baseline,
                    token).ConfigureAwait(false);
                return await MarkSucceededAfterExactAsync(result, visibleContext, token).ConfigureAwait(false);
            });
        VisibleWebAutomationResult<BaiduAdapterResult<ShareCreationReceipt>> result;
        try
        {
            result = await broker.EnqueueAsync(automationRequest, cancellationToken).ConfigureAwait(false);
        }
        catch (WebAutomationAuthorizationException)
        {
            return NotValidated<ShareCreationReceipt>(SubmissionAcceptance.DefinitelyNotAccepted);
        }

        if (result.Disposition == VisibleWebAutomationDisposition.ActionCompleted && result.Value is not null)
            return result.Value;
        return Unknown<ShareCreationReceipt>(
            result.PersistentState == WebAutomationAttemptState.DefinitelyNotSubmitted
                ? SubmissionAcceptance.DefinitelyNotAccepted
                : SubmissionAcceptance.Unknown);
    }

    public async Task<BaiduAdapterResult<ShareCreationReceipt>> ConfirmExactShareForOperationAsync(
        ShareOperationAutomationContext context,
        AccountSession session,
        PermanentShareRequest request,
        CancellationToken cancellationToken)
    {
        ValidateOperationIdentity(context, session, request);
        if (attemptStore is null || acceptanceStore is null)
            return Unknown<ShareCreationReceipt>(SubmissionAcceptance.Unknown);

        var attempt = await LoadAttemptAsync(context, cancellationToken).ConfigureAwait(false);
        var stage = EffectiveRecoverableStage(attempt, context);
        if (stage == WebAutomationAttemptState.BaselinePersisted)
        {
            var absent = Unknown<ShareCreationReceipt>(SubmissionAcceptance.DefinitelyNotAccepted);
            if (attempt!.State != WebAutomationAttemptState.UserHandoff) return absent;
            return await ResumeWithoutActionAsync(
                context,
                absent,
                SubmissionAcceptance.Unknown,
                cancellationToken).ConfigureAwait(false);
        }
        if (stage is not (WebAutomationAttemptState.ResultPending or WebAutomationAttemptState.Succeeded))
            return Unknown<ShareCreationReceipt>(SubmissionAcceptance.Unknown);

        var acceptedEvidence = await acceptanceStore.GetAsync(context, cancellationToken).ConfigureAwait(false);
        if (!EvidenceMatchesAttempt(acceptedEvidence, attempt!, request))
            return Unknown<ShareCreationReceipt>(SubmissionAcceptance.Unknown);
        if (stage == WebAutomationAttemptState.Succeeded)
            return Receipt(request, acceptedEvidence!);

        var automationRequest = Request(
            context,
            async (visibleSession, visibleContext, token) =>
            {
                if (visibleSession is not IShareAutomationSession automationSession)
                    return NotValidated<ShareCreationReceipt>(SubmissionAcceptance.Unknown);
                var result = await flow.ConfirmExactAsync(
                    automationSession,
                    request,
                    attempt!.Baseline!,
                    acceptedEvidence!,
                    token).ConfigureAwait(false);
                return await MarkSucceededAfterExactAsync(result, visibleContext, token).ConfigureAwait(false);
            });
        try
        {
            var result = await broker.EnqueueAsync(automationRequest, cancellationToken).ConfigureAwait(false);
            return result.Disposition == VisibleWebAutomationDisposition.ActionCompleted && result.Value is not null
                ? result.Value
                : Unknown<ShareCreationReceipt>(SubmissionAcceptance.Unknown);
        }
        catch (WebAutomationAuthorizationException)
        {
            return NotValidated<ShareCreationReceipt>(SubmissionAcceptance.Unknown);
        }
    }

    private async Task<WebAutomationAttemptRecord?> LoadAttemptAsync(
        ShareOperationAutomationContext context,
        CancellationToken cancellationToken) =>
        await attemptStore!.GetAsync(
            context.OperationId,
            context.AccountId,
            context.Generation,
            cancellationToken).ConfigureAwait(false);

    private BaiduNavigationAuthorizationRequest Navigation() => new(
        BaiduAutomationAction.OpenOfficialUri,
        OfficialFilesUri,
        BaiduSelectorCatalog.ProtocolVersion,
        BaiduSelectorCatalog.SelectorVersion,
        applicationBuildVersion,
        new BaiduAutomationAcceptance(
            false,
            BaiduSelectorCatalog.ProtocolVersion,
            BaiduSelectorCatalog.SelectorVersion,
            applicationBuildVersion),
        BaiduProtocolValidationStatus.NotValidated);

    private VisibleWebAutomationRequest<T> Request<T>(
        ShareOperationAutomationContext context,
        Func<IVisibleWebAutomationSession, VisibleWebAutomationContext, CancellationToken, Task<T>> action)
    {
        var identity = new WebAutomationRequestIdentity(
            context.OperationId,
            context.BatchId,
            context.AccountId,
            context.Generation,
            context.OwnerKind);
        var profilePath = profilePaths.GetAccountProfilePath(context.AccountId);
        profilePaths.EnsureSafeAccountPath(context.AccountId, profilePath);
        return new(identity, profilePath, Navigation(), action);
    }

    private async Task<BaiduAdapterResult<T>> ResumeWithoutActionAsync<T>(
        ShareOperationAutomationContext context,
        BaiduAdapterResult<T> value,
        SubmissionAcceptance fallbackAcceptance,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await broker.EnqueueAsync(
                Request(context, (_, _, _) => Task.FromResult(value)),
                cancellationToken).ConfigureAwait(false);
            return result.Disposition == VisibleWebAutomationDisposition.ActionCompleted && result.Value is not null
                ? result.Value
                : Unknown<T>(fallbackAcceptance);
        }
        catch (WebAutomationAuthorizationException)
        {
            return NotValidated<T>(fallbackAcceptance);
        }
    }

    private static void ValidateOperationIdentity(
        ShareOperationAutomationContext context,
        AccountSession session,
        PermanentShareRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        context.Validate();
        if (context.AccountId != session.LocalAccountId || context.ExactRemoteItems != request.RemoteItems)
            throw new InvalidOperationException("The share automation identity does not match its account or exact remote items.");
    }

    private static WebAutomationAttemptState? EffectiveRecoverableStage(
        WebAutomationAttemptRecord? attempt,
        ShareOperationAutomationContext context)
    {
        if (attempt is null || attempt.OwnerKind != context.OwnerKind) return null;
        var stage = attempt.State == WebAutomationAttemptState.UserHandoff
            ? attempt.ResumeStage
            : attempt.State;
        return stage switch
        {
            WebAutomationAttemptState.Preparing
                when attempt.Baseline is null && attempt.AcceptedRemoteItems is null => stage,
            WebAutomationAttemptState.BaselinePersisted
                when attempt.Baseline is not null && attempt.AcceptedRemoteItems is null => stage,
            WebAutomationAttemptState.ResultPending
                when attempt.Baseline is not null && attempt.AcceptedRemoteItems == context.ExactRemoteItems => stage,
            WebAutomationAttemptState.Succeeded
                when attempt.Baseline is not null && attempt.AcceptedRemoteItems == context.ExactRemoteItems => stage,
            _ => null,
        };
    }

    private static bool EvidenceMatchesAttempt(
        ShareOfficialAcceptance? evidence,
        WebAutomationAttemptRecord attempt,
        PermanentShareRequest request)
    {
        if (evidence is not { IsAccepted: true } ||
            evidence.ExactRemoteItems != attempt.AcceptedRemoteItems ||
            evidence.ExactRemoteItems != request.RemoteItems)
            return false;
        var settings = evidence.EffectiveSettings!;
        if (settings.IsPermanent != true || settings.IsAccessUnlimited != true)
            return false;
        return request.UsesRandomExtractionCode
            ? settings.ExtractionCodeBehavior == ShareExtractionCodeBehavior.Random
            : settings.ExtractionCodeBehavior == ShareExtractionCodeBehavior.Fixed &&
              string.Equals(settings.ExtractionCode, request.FixedExtractionCode, StringComparison.Ordinal);
    }

    private static async Task<BaiduAdapterResult<ShareCreationReceipt>> MarkSucceededAfterExactAsync(
        BaiduAdapterResult<ShareCreationReceipt> result,
        VisibleWebAutomationContext context,
        CancellationToken cancellationToken)
    {
        if (!result.IsSuccess || !result.Value!.IsFinalAcceptance) return result;
        try
        {
            await context.AdvancePersistentStageAsync(
                WebAutomationAttemptState.ResultPending,
                WebAutomationAttemptState.Succeeded,
                null,
                cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Unknown<ShareCreationReceipt>(SubmissionAcceptance.Unknown);
        }
    }

    private BaiduAdapterResult<ShareCreationReceipt> Receipt(
        PermanentShareRequest request,
        ShareOfficialAcceptance evidence) =>
        BaiduAdapterResult<ShareCreationReceipt>.Success(
            new ShareCreationReceipt(
                TimeProvider.System.GetUtcNow(),
                evidence.AdapterRequestId!,
                request,
                evidence.ShareId!,
                evidence.ShareUrl!,
                evidence.EffectiveSettings!),
            SubmissionAcceptance.Unknown);

    private static BaiduAdapterResult<SharePreparationReceipt> Prepared() =>
        BaiduAdapterResult<SharePreparationReceipt>.Success(
            SharePreparationReceipt.Ready,
            SubmissionAcceptance.DefinitelyNotAccepted);

    private static BaiduAdapterResult<T> NotValidated<T>(SubmissionAcceptance acceptance) =>
        BaiduAdapterResult<T>.Failure(
            BaiduErrorKind.NotValidated,
            adapterDiagnostic: null,
            submissionAcceptance: acceptance);

    private static BaiduAdapterResult<T> Unknown<T>(SubmissionAcceptance acceptance) =>
        BaiduAdapterResult<T>.Failure(
            BaiduErrorKind.UnknownResponse,
            adapterDiagnostic: null,
            submissionAcceptance: acceptance);

    private sealed class PersistentShareAutomationSession(
        IShareAutomationSession inner,
        VisibleWebAutomationContext visibleContext,
        ShareOperationAutomationContext operationContext,
        IShareAutomationAcceptanceEvidenceStore? acceptanceStore) : IShareAutomationSession
    {
        public Task SelectExactRemoteItemsAsSingleGroupAsync(RemoteItemIdentitySet remoteItems, CancellationToken cancellationToken) =>
            inner.SelectExactRemoteItemsAsSingleGroupAsync(remoteItems, cancellationToken);

        public Task ConfigurePermanentShareAsync(PermanentShareRequest request, CancellationToken cancellationToken) =>
            inner.ConfigurePermanentShareAsync(request, cancellationToken);

        public Task<ShareOfficialListing> ReadCompleteStableOfficialShareListAsync(CancellationToken cancellationToken) =>
            inner.ReadCompleteStableOfficialShareListAsync(cancellationToken);

        public async Task PersistBaselineAsync(WebAutomationBaseline baseline, string observationFingerprint, CancellationToken cancellationToken)
        {
            await visibleContext.PersistBaselineAsync(baseline, observationFingerprint, cancellationToken)
                .ConfigureAwait(false);
            await inner.PersistBaselineAsync(baseline, observationFingerprint, cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task MarkSubmittingAsync(CancellationToken cancellationToken)
        {
            await visibleContext.AdvancePersistentStageAsync(
                WebAutomationAttemptState.BaselinePersisted,
                WebAutomationAttemptState.Submitting,
                null,
                cancellationToken).ConfigureAwait(false);
            await inner.MarkSubmittingAsync(cancellationToken).ConfigureAwait(false);
        }

        public Task<ShareOfficialAcceptance> CreateShareOnceAsync(CancellationToken cancellationToken) =>
            inner.CreateShareOnceAsync(cancellationToken);

        public async Task PersistOfficialAcceptanceAsync(ShareOfficialAcceptance acceptance, CancellationToken cancellationToken)
        {
            if (!acceptance.IsAccepted || acceptance.ExactRemoteItems is null)
                throw new InvalidOperationException("Official acceptance did not contain complete typed share evidence.");
            if (acceptanceStore is null)
                throw new InvalidOperationException("A persistent share-acceptance evidence store is required.");

            await acceptanceStore.PersistAsync(operationContext, acceptance, cancellationToken)
                .ConfigureAwait(false);
            await visibleContext.PersistOfficialAcceptanceAsync(
                acceptance.ExactRemoteItems,
                acceptance.ObservationFingerprint,
                cancellationToken).ConfigureAwait(false);
            await inner.PersistOfficialAcceptanceAsync(acceptance, cancellationToken).ConfigureAwait(false);
        }
    }
}
