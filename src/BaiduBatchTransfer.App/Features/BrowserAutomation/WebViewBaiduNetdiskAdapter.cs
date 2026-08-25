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

    public async Task<BaiduAdapterResult<TransferReceipt>> SaveWholeShareForOperationAsync(
        TransferOperationAutomationContext context,
        AccountSession session,
        ShareReference share,
        RemoteDirectory destination,
        DuplicatePolicy duplicatePolicy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(share);
        ArgumentNullException.ThrowIfNull(destination);
        context.Validate();
        if (context.AccountId != session.LocalAccountId || context.SourceShare != share)
            throw new InvalidOperationException("The transfer automation identity does not match its account or source share.");

        var identity = new WebAutomationRequestIdentity(
            context.OperationId,
            context.BatchId,
            context.AccountId,
            context.Generation,
            WebAutomationOwnerKind.Transfer);
        var profilePath = profilePaths.GetAccountProfilePath(context.AccountId);
        profilePaths.EnsureSafeAccountPath(context.AccountId, profilePath);
        var request = new VisibleWebAutomationRequest<BaiduAdapterResult<TransferReceipt>>(
            identity,
            profilePath,
            Navigation(new Uri(share.OriginalUrl)),
            async (visibleSession, visibleContext, token) =>
            {
                if (visibleSession is not ITransferAutomationSession automationSession)
                    return NotValidated<TransferReceipt>(SubmissionAcceptance.DefinitelyNotAccepted);
                return await flow.ExecuteAsync(
                    new PersistentTransferAutomationSession(automationSession, visibleContext),
                    new TransferAutomationRequest(share, destination, duplicatePolicy),
                    token).ConfigureAwait(false);
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
        if (attempt is not
            {
                OwnerKind: WebAutomationOwnerKind.Transfer,
                State: WebAutomationAttemptState.ResultPending,
                Baseline: not null,
            })
            return UnknownConfirmation();

        var identity = new WebAutomationRequestIdentity(
            context.OperationId,
            context.BatchId,
            context.AccountId,
            context.Generation,
            WebAutomationOwnerKind.Transfer);
        var profilePath = profilePaths.GetAccountProfilePath(context.AccountId);
        profilePaths.EnsureSafeAccountPath(context.AccountId, profilePath);
        var request = new VisibleWebAutomationRequest<BaiduAdapterResult<TransferConfirmationResult>>(
            identity,
            profilePath,
            Navigation(new Uri(context.SourceShare.OriginalUrl)),
            async (visibleSession, _, token) =>
            {
                if (visibleSession is not ITransferAutomationSession automationSession)
                    return NotValidated<TransferConfirmationResult>(SubmissionAcceptance.Unknown);
                return await flow.ConfirmExactAsync(
                    automationSession,
                    context.SourceShare,
                    new RemoteDirectory(query.TargetPath.Value, query.TargetPath),
                    attempt.Baseline,
                    officialAcceptancePersisted: true,
                    token).ConfigureAwait(false);
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
            await context.AdvancePersistentStageAsync(
                WebAutomationAttemptState.Submitting,
                WebAutomationAttemptState.ResultPending,
                acceptance.ObservationFingerprint,
                cancellationToken).ConfigureAwait(false);
            await inner.PersistOfficialAcceptanceAsync(acceptance, cancellationToken).ConfigureAwait(false);
        }

        public Task<TransferDirectoryListing> ReopenDestinationAndReadStableListingAsync(RemoteDirectory destination, CancellationToken cancellationToken) =>
            inner.ReopenDestinationAndReadStableListingAsync(destination, cancellationToken);
    }
}
