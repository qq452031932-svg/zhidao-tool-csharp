using System.Collections.Immutable;
using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.BaiduAdapter.WebAutomation;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.App.Features.BrowserAutomation;

public sealed class WebViewBaiduNetdiskAdapter : IOperationBoundBaiduNetdiskAdapter
{
    private readonly IBaiduNetdiskAdapter readOnlyAdapter;
    private readonly IVisibleWebAutomationBroker broker;
    private readonly IAccountProfilePathPolicy profilePaths;
    private readonly TransferAutomationFlow flow;
    private readonly string applicationBuildVersion;
    private readonly IWebAutomationAttemptStore? attemptStore;

    public WebViewBaiduNetdiskAdapter(
        IBaiduNetdiskAdapter readOnlyAdapter,
        IVisibleWebAutomationBroker broker,
        IAccountProfilePathPolicy profilePaths,
        TransferAutomationFlow flow,
        string applicationBuildVersion,
        IWebAutomationAttemptStore? attemptStore = null)
    {
        this.readOnlyAdapter = readOnlyAdapter ?? throw new ArgumentNullException(nameof(readOnlyAdapter));
        this.broker = broker ?? throw new ArgumentNullException(nameof(broker));
        this.profilePaths = profilePaths ?? throw new ArgumentNullException(nameof(profilePaths));
        this.flow = flow ?? throw new ArgumentNullException(nameof(flow));
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationBuildVersion);
        this.applicationBuildVersion = applicationBuildVersion.Trim();
        this.attemptStore = attemptStore;
    }

    public Task<BaiduAdapterResult<BaiduAccountIdentity>> ValidateSessionAsync(
        AccountSession session,
        CancellationToken cancellationToken) =>
        readOnlyAdapter.ValidateSessionAsync(session, cancellationToken);

    public Task<BaiduAdapterResult<AccountCapacity>> GetCapacityAsync(
        AccountSession session,
        CancellationToken cancellationToken) =>
        readOnlyAdapter.GetCapacityAsync(session, cancellationToken);

    public Task<BaiduAdapterResult<ImmutableArray<RemoteDirectory>>> ListDirectoriesAsync(
        AccountSession session,
        RemotePath path,
        CancellationToken cancellationToken) =>
        readOnlyAdapter.ListDirectoriesAsync(session, path, cancellationToken);

    public Task<BaiduAdapterResult<RemoteDirectory>> EnsureDirectoryAsync(
        AccountSession session,
        RemotePath path,
        CancellationToken cancellationToken) =>
        readOnlyAdapter.EnsureDirectoryAsync(session, path, cancellationToken);

    public Task<BaiduAdapterResult<ShareMetadata>> InspectShareAsync(
        AccountSession session,
        ShareReference share,
        CancellationToken cancellationToken) =>
        readOnlyAdapter.InspectShareAsync(session, share, cancellationToken);

    public Task<BaiduAdapterResult<TransferReceipt>> SaveWholeShareAsync(
        AccountSession session,
        ShareReference share,
        RemoteDirectory destination,
        DuplicatePolicy duplicatePolicy,
        CancellationToken cancellationToken) =>
        Task.FromResult(NotValidated<TransferReceipt>(SubmissionAcceptance.DefinitelyNotAccepted));

    public Task<BaiduAdapterResult<TransferConfirmation>> ConfirmTransferAsync(
        AccountSession session,
        TransferConfirmationQuery query,
        CancellationToken cancellationToken) =>
        readOnlyAdapter.ConfirmTransferAsync(session, query, cancellationToken);

    public async Task<BaiduAdapterResult<TransferPreparationReceipt>> PrepareWholeShareForOperationAsync(
        TransferOperationAutomationContext context,
        AccountSession session,
        ShareReference share,
        RemoteDirectory destination,
        DuplicatePolicy duplicatePolicy,
        CancellationToken cancellationToken)
    {
        ValidateOperationIdentity(context, session, share);
        if (attemptStore is not null)
        {
            var existing = await attemptStore.GetAsync(
                context.OperationId,
                context.AccountId,
                context.Generation,
                cancellationToken).ConfigureAwait(false);
            var existingStage = EffectiveRecoverableStage(existing);
            if (existingStage == WebAutomationAttemptState.BaselinePersisted)
            {
                var prepared = Prepared();
                if (existing!.State != WebAutomationAttemptState.UserHandoff)
                    return prepared;
                try
                {
                    var resumed = await broker.EnqueueAsync(
                        Request(
                            context,
                            share,
                            (_, _, _) => Task.FromResult(prepared)),
                        cancellationToken).ConfigureAwait(false);
                    return resumed.Disposition is VisibleWebAutomationDisposition.ActionCompleted &&
                           resumed.Value is not null
                        ? resumed.Value
                        : Unknown<TransferPreparationReceipt>(SubmissionAcceptance.DefinitelyNotAccepted);
                }
                catch (WebAutomationAuthorizationException)
                {
                    return NotValidated<TransferPreparationReceipt>(SubmissionAcceptance.DefinitelyNotAccepted);
                }
            }
            if (existing is not null && existingStage != WebAutomationAttemptState.Preparing)
                return Unknown<TransferPreparationReceipt>(SubmissionAcceptance.Unknown);
        }
        var request = Request(
            context,
            share,
            async (visibleSession, visibleContext, token) =>
            {
                if (visibleSession is not ITransferAutomationSession automationSession)
                    return NotValidated<TransferPreparationReceipt>(SubmissionAcceptance.DefinitelyNotAccepted);
                var result = await flow.PrepareAsync(
                    new PersistentTransferAutomationSession(automationSession, visibleContext),
                    new TransferAutomationRequest(share, destination, duplicatePolicy),
                    token).ConfigureAwait(false);
                return result.IsSuccess
                    ? BaiduAdapterResult<TransferPreparationReceipt>.Success(
                        TransferPreparationReceipt.Ready,
                        SubmissionAcceptance.DefinitelyNotAccepted)
                    : BaiduAdapterResult<TransferPreparationReceipt>.Failure(
                        result.ErrorKind!.Value,
                        adapterDiagnostic: null,
                        result.RetryAfter,
                        result.SubmissionAcceptance);
            });
        try
        {
            var result = await broker.EnqueueAsync(request, cancellationToken).ConfigureAwait(false);
            if (result.Disposition is VisibleWebAutomationDisposition.ActionCompleted && result.Value is not null)
                return result.Value;
            return Unknown<TransferPreparationReceipt>(SubmissionAcceptance.DefinitelyNotAccepted);
        }
        catch (WebAutomationAuthorizationException)
        {
            return NotValidated<TransferPreparationReceipt>(SubmissionAcceptance.DefinitelyNotAccepted);
        }
    }

    public async Task<BaiduAdapterResult<TransferReceipt>> SaveWholeShareForOperationAsync(
        TransferOperationAutomationContext context,
        AccountSession session,
        ShareReference share,
        RemoteDirectory destination,
        DuplicatePolicy duplicatePolicy,
        CancellationToken cancellationToken)
    {
        ValidateOperationIdentity(context, session, share);
        ArgumentNullException.ThrowIfNull(destination);
        if (attemptStore is null) return Unknown<TransferReceipt>(SubmissionAcceptance.DefinitelyNotAccepted);
        var attempt = await attemptStore.GetAsync(
            context.OperationId,
            context.AccountId,
            context.Generation,
            cancellationToken).ConfigureAwait(false);
        if (EffectiveRecoverableStage(attempt) != WebAutomationAttemptState.BaselinePersisted)
            return Unknown<TransferReceipt>(
                attempt?.State == WebAutomationAttemptState.UserHandoff
                    ? SubmissionAcceptance.Unknown
                    : SubmissionAcceptance.DefinitelyNotAccepted);
        var baseline = attempt!.Baseline!;
        var request = Request(
            context,
            share,
            async (visibleSession, visibleContext, token) =>
            {
                if (visibleSession is not ITransferAutomationSession automationSession)
                    return NotValidated<TransferReceipt>(SubmissionAcceptance.DefinitelyNotAccepted);
                var result = await flow.SubmitAsync(
                    new PersistentTransferAutomationSession(automationSession, visibleContext),
                    new TransferAutomationRequest(share, destination, duplicatePolicy),
                    baseline,
                    token).ConfigureAwait(false);
                return await MarkSucceededAfterExactAsync(result, visibleContext, token).ConfigureAwait(false);
            });
        VisibleWebAutomationResult<BaiduAdapterResult<TransferReceipt>> result;
        try { result = await broker.EnqueueAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (WebAutomationAuthorizationException)
        {
            return NotValidated<TransferReceipt>(SubmissionAcceptance.DefinitelyNotAccepted);
        }
        if (result.Disposition is VisibleWebAutomationDisposition.ActionCompleted && result.Value is not null)
            return result.Value;
        return Unknown<TransferReceipt>(
            result.PersistentState is WebAutomationAttemptState.DefinitelyNotSubmitted
                ? SubmissionAcceptance.DefinitelyNotAccepted
                : SubmissionAcceptance.Unknown);
    }

    public async Task<BaiduAdapterResult<TransferConfirmationResult>> ConfirmExactTransferForOperationAsync(
        TransferOperationAutomationContext context,
        AccountSession session,
        TransferConfirmationQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(query);
        context.Validate();
        if (context.AccountId != session.LocalAccountId ||
            !string.Equals(context.SourceShare.StableShareId, query.SourceShareId, StringComparison.Ordinal))
            throw new InvalidOperationException("The transfer recovery identity does not match its account or source share.");
        if (attemptStore is null) return UnknownConfirmation();
        var attempt = await attemptStore.GetAsync(
            context.OperationId,
            context.AccountId,
            context.Generation,
            cancellationToken).ConfigureAwait(false);
        if (attempt is
            {
                OwnerKind: WebAutomationOwnerKind.Transfer,
                State: WebAutomationAttemptState.Succeeded,
                AcceptedRemoteItems: not null,
            })
            return BaiduAdapterResult<TransferConfirmationResult>.Success(
                TransferConfirmationResult.ExactPresent(attempt.AcceptedRemoteItems),
                SubmissionAcceptance.Unknown);
        var effectiveStage = EffectiveRecoverableStage(attempt);
        if (effectiveStage == WebAutomationAttemptState.BaselinePersisted)
        {
            var absent = DefinitelyNotSubmittedConfirmation();
            if (attempt!.State != WebAutomationAttemptState.UserHandoff)
                return absent;
            try
            {
                var resumed = await broker.EnqueueAsync(
                    Request(
                        context,
                        context.SourceShare,
                        (_, _, _) => Task.FromResult(absent)),
                    cancellationToken).ConfigureAwait(false);
                return resumed.Disposition is VisibleWebAutomationDisposition.ActionCompleted &&
                       resumed.Value is not null
                    ? resumed.Value
                    : UnknownConfirmation();
            }
            catch (WebAutomationAuthorizationException)
            {
                return NotValidated<TransferConfirmationResult>(SubmissionAcceptance.Unknown);
            }
        }
        if (effectiveStage != WebAutomationAttemptState.ResultPending)
            return UnknownConfirmation();
        var baseline = attempt!.Baseline!;
        var acceptedRemoteItems = attempt.AcceptedRemoteItems!;

        var request = Request(
            context,
            context.SourceShare,
            async (visibleSession, visibleContext, token) =>
            {
                if (visibleSession is not ITransferAutomationSession automationSession)
                    return NotValidated<TransferConfirmationResult>(SubmissionAcceptance.Unknown);
                var officialAcceptance = new TransferOfficialAcceptance(
                    TransferOfficialAcceptanceStatus.Accepted,
                    adapterRequestId: null,
                    acceptedRemoteItems,
                    attempt.ObservationFingerprint ?? acceptedRemoteItems.Fingerprint);
                var result = await flow.ConfirmExactAsync(
                    automationSession,
                    context.SourceShare,
                    new RemoteDirectory(query.TargetPath.Value, query.TargetPath),
                    baseline,
                    officialAcceptance,
                    token).ConfigureAwait(false);
                return await MarkSucceededAfterExactAsync(result, visibleContext, token).ConfigureAwait(false);
            });
        VisibleWebAutomationResult<BaiduAdapterResult<TransferConfirmationResult>> result;
        try { result = await broker.EnqueueAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (WebAutomationAuthorizationException)
        {
            return NotValidated<TransferConfirmationResult>(SubmissionAcceptance.Unknown);
        }
        return result.Disposition is VisibleWebAutomationDisposition.ActionCompleted && result.Value is not null
            ? result.Value
            : UnknownConfirmation();
    }

    private BaiduNavigationAuthorizationRequest Navigation(Uri officialUri) => new(
        BaiduAutomationAction.OpenOfficialUri,
        officialUri,
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
        TransferOperationAutomationContext context,
        ShareReference share,
        Func<IVisibleWebAutomationSession, VisibleWebAutomationContext, CancellationToken, Task<T>> action)
    {
        var identity = new WebAutomationRequestIdentity(
            context.OperationId,
            context.BatchId,
            context.AccountId,
            context.Generation,
            WebAutomationOwnerKind.Transfer);
        var profilePath = profilePaths.GetAccountProfilePath(context.AccountId);
        profilePaths.EnsureSafeAccountPath(context.AccountId, profilePath);
        return new(identity, profilePath, Navigation(new Uri(share.OriginalUrl)), action);
    }

    private static void ValidateOperationIdentity(
        TransferOperationAutomationContext context,
        AccountSession session,
        ShareReference share)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(share);
        context.Validate();
        if (context.AccountId != session.LocalAccountId || context.SourceShare != share)
            throw new InvalidOperationException("The transfer automation identity does not match its account or source share.");
    }

    private static WebAutomationAttemptState? EffectiveRecoverableStage(
        WebAutomationAttemptRecord? attempt)
    {
        if (attempt is null || attempt.OwnerKind != WebAutomationOwnerKind.Transfer)
            return null;
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
                when attempt.Baseline is not null && attempt.AcceptedRemoteItems is not null => stage,
            _ => null,
        };
    }

    private static async Task<BaiduAdapterResult<TransferReceipt>> MarkSucceededAfterExactAsync(
        BaiduAdapterResult<TransferReceipt> result,
        VisibleWebAutomationContext context,
        CancellationToken cancellationToken)
    {
        if (!result.IsSuccess || result.Value!.ExactRemoteItems is null) return result;
        try
        {
            await context.AdvancePersistentStageAsync(
                WebAutomationAttemptState.ResultPending,
                WebAutomationAttemptState.Succeeded,
                null,
                cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return Unknown<TransferReceipt>(SubmissionAcceptance.Unknown); }
    }

    private static async Task<BaiduAdapterResult<TransferConfirmationResult>> MarkSucceededAfterExactAsync(
        BaiduAdapterResult<TransferConfirmationResult> result,
        VisibleWebAutomationContext context,
        CancellationToken cancellationToken)
    {
        if (!result.IsSuccess || result.Value!.State != TransferConfirmationState.ExactPresent) return result;
        try
        {
            await context.AdvancePersistentStageAsync(
                WebAutomationAttemptState.ResultPending,
                WebAutomationAttemptState.Succeeded,
                null,
                cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return UnknownConfirmation(); }
    }

    private static BaiduAdapterResult<T> NotValidated<T>(SubmissionAcceptance acceptance) =>
        BaiduAdapterResult<T>.Failure(
            BaiduErrorKind.NotValidated,
            adapterDiagnostic: null,
            submissionAcceptance: acceptance);

    private static BaiduAdapterResult<TransferPreparationReceipt> Prepared() =>
        BaiduAdapterResult<TransferPreparationReceipt>.Success(
            TransferPreparationReceipt.Ready,
            SubmissionAcceptance.DefinitelyNotAccepted);

    private static BaiduAdapterResult<TransferConfirmationResult> DefinitelyNotSubmittedConfirmation() =>
        BaiduAdapterResult<TransferConfirmationResult>.Success(
            TransferConfirmationResult.Absent,
            SubmissionAcceptance.DefinitelyNotAccepted);

    private static BaiduAdapterResult<T> Unknown<T>(SubmissionAcceptance acceptance) =>
        BaiduAdapterResult<T>.Failure(
            BaiduErrorKind.UnknownResponse,
            adapterDiagnostic: null,
            submissionAcceptance: acceptance);

    private static BaiduAdapterResult<TransferConfirmationResult> UnknownConfirmation() =>
        BaiduAdapterResult<TransferConfirmationResult>.Failure(
            BaiduErrorKind.UnknownResponse,
            adapterDiagnostic: null,
            submissionAcceptance: SubmissionAcceptance.Unknown);

    private sealed class PersistentTransferAutomationSession(
        ITransferAutomationSession inner,
        VisibleWebAutomationContext context) : ITransferAutomationSession
    {
        public Task<TransferSourceListing> OpenExternalShareAsync(ShareReference share, CancellationToken cancellationToken) =>
            inner.OpenExternalShareAsync(share, cancellationToken);

        public Task EnterExtractionCodeAsync(string extractionCode, CancellationToken cancellationToken) =>
            inner.EnterExtractionCodeAsync(extractionCode, cancellationToken);

        public Task<TransferDirectoryListing> OpenDestinationAndReadStableListingAsync(RemoteDirectory destination, CancellationToken cancellationToken) =>
            inner.OpenDestinationAndReadStableListingAsync(destination, cancellationToken);

        public async Task PersistBaselineAsync(WebAutomationBaseline baseline, string observationFingerprint, CancellationToken cancellationToken)
        {
            await context.PersistBaselineAsync(baseline, observationFingerprint, cancellationToken).ConfigureAwait(false);
            await inner.PersistBaselineAsync(baseline, observationFingerprint, cancellationToken).ConfigureAwait(false);
        }

        public Task ReturnToExternalShareAsync(ShareReference share, CancellationToken cancellationToken) =>
            inner.ReturnToExternalShareAsync(share, cancellationToken);

        public async Task MarkSubmittingAsync(CancellationToken cancellationToken)
        {
            await context.AdvancePersistentStageAsync(
                WebAutomationAttemptState.BaselinePersisted,
                WebAutomationAttemptState.Submitting,
                null,
                cancellationToken).ConfigureAwait(false);
            await inner.MarkSubmittingAsync(cancellationToken).ConfigureAwait(false);
        }

        public Task<TransferOfficialAcceptance> SaveWholeShareAsync(RemoteDirectory destination, DuplicatePolicy duplicatePolicy, CancellationToken cancellationToken) =>
            inner.SaveWholeShareAsync(destination, duplicatePolicy, cancellationToken);

        public async Task PersistOfficialAcceptanceAsync(TransferOfficialAcceptance acceptance, CancellationToken cancellationToken)
        {
            if (!acceptance.IsAccepted || acceptance.ExactRemoteItems is null)
                throw new InvalidOperationException("Official acceptance did not include exact remote identities.");
            await context.PersistOfficialAcceptanceAsync(
                acceptance.ExactRemoteItems,
                acceptance.ObservationFingerprint,
                cancellationToken).ConfigureAwait(false);
            await inner.PersistOfficialAcceptanceAsync(acceptance, cancellationToken).ConfigureAwait(false);
        }

        public Task<TransferDirectoryListing> ReopenDestinationAndReadStableListingAsync(RemoteDirectory destination, CancellationToken cancellationToken) =>
            inner.ReopenDestinationAndReadStableListingAsync(destination, cancellationToken);
    }
}
