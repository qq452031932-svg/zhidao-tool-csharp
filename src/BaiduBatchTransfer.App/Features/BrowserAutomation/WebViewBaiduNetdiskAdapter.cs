using System.Collections.Immutable;
using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.BaiduAdapter.WebAutomation;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.App.Features.Acceptance;

namespace BaiduBatchTransfer.App.Features.BrowserAutomation;

public sealed class WebViewBaiduNetdiskAdapter : IOperationBoundBaiduNetdiskAdapter
{
    private readonly IBaiduNetdiskAdapter readOnlyAdapter;
    private readonly IVisibleWebAutomationBroker broker;
    private readonly IAccountProfilePathPolicy profilePaths;
    private readonly TransferAutomationFlow flow;
    private readonly string applicationBuildVersion;
    private readonly IWebAutomationAttemptStore? attemptStore;
    private readonly IAcceptanceOperationEvidenceSink? acceptanceEvidence;
    private readonly ITransferRecoveryAuthorizationSource? recoveryAuthorization;

    public WebViewBaiduNetdiskAdapter(
        IBaiduNetdiskAdapter readOnlyAdapter,
        IVisibleWebAutomationBroker broker,
        IAccountProfilePathPolicy profilePaths,
        TransferAutomationFlow flow,
        string applicationBuildVersion,
        IWebAutomationAttemptStore? attemptStore = null,
        IAcceptanceOperationEvidenceSink? acceptanceEvidence = null,
        ITransferRecoveryAuthorizationSource? recoveryAuthorization = null)
    {
        this.readOnlyAdapter = readOnlyAdapter ?? throw new ArgumentNullException(nameof(readOnlyAdapter));
        this.broker = broker ?? throw new ArgumentNullException(nameof(broker));
        this.profilePaths = profilePaths ?? throw new ArgumentNullException(nameof(profilePaths));
        this.flow = flow ?? throw new ArgumentNullException(nameof(flow));
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationBuildVersion);
        this.applicationBuildVersion = applicationBuildVersion.Trim();
        this.attemptStore = attemptStore;
        this.acceptanceEvidence = acceptanceEvidence;
        this.recoveryAuthorization = recoveryAuthorization;
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

    public async Task<BaiduAdapterResult<TransferDirectoryPreparationOutcome>> EnsureDirectoryForOperationAsync(
        TransferOperationAutomationContext context,
        AccountSession session,
        RemotePath path,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(path);
        context.Validate();
        RequireWebView2ExecutionProfile(context.ExecutionProfile);
        if (context.AccountId != session.LocalAccountId)
            throw new InvalidOperationException("The directory preparation identity does not match its account.");
        if (!HasExactCandidateBinding(context) || string.IsNullOrWhiteSpace(session.BaiduUserId) ||
            attemptStore is null)
            return NotValidated<TransferDirectoryPreparationOutcome>(SubmissionAcceptance.DefinitelyNotAccepted);
        var request = Request(
            context,
            new Uri(context.SourceShare.OriginalUrl),
            async (visibleSession, visibleContext, token) =>
            {
                if (visibleSession is not ITransferDirectoryPreparationSession automation)
                    return NotValidated<TransferDirectoryPreparationOutcome>(SubmissionAcceptance.DefinitelyNotAccepted);
                var preparationRequest = new TransferDirectoryPreparationRequest(
                    context.OperationId, context.BatchId, context.AccountId, context.Generation,
                    context.SourceShare, path, context.AuthorizationBinding!.Capability);
                var outcome = await new TransferDirectoryPreparationFlow().ExecuteAsync(
                    automation, new TransferDirectoryPreparationEvidenceStore(
                        attemptStore, recoveryAuthorization),
                    preparationRequest, token).ConfigureAwait(false);
                return BaiduAdapterResult<TransferDirectoryPreparationOutcome>.Success(
                    outcome,
                    SubmissionAcceptance.DefinitelyNotAccepted);
            },
            session.BaiduUserId);
        try
        {
            var result = await broker.EnqueueAsync(request, cancellationToken).ConfigureAwait(false);
            return result.Disposition == VisibleWebAutomationDisposition.ActionCompleted && result.Value is not null
                ? result.Value
                : NotValidated<TransferDirectoryPreparationOutcome>(SubmissionAcceptance.DefinitelyNotAccepted);
        }
        catch (WebAutomationAuthorizationException)
        {
            return NotValidated<TransferDirectoryPreparationOutcome>(SubmissionAcceptance.DefinitelyNotAccepted);
        }
    }

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
        if (string.IsNullOrWhiteSpace(session.BaiduUserId))
            return NotValidated<TransferPreparationReceipt>(SubmissionAcceptance.DefinitelyNotAccepted);
        if (attemptStore is not { } store)
            return NotValidated<TransferPreparationReceipt>(SubmissionAcceptance.DefinitelyNotAccepted);
        {
            var existing = await store.GetAsync(
                context.OperationId,
                context.AccountId,
                context.Generation,
                cancellationToken).ConfigureAwait(false);
            var existingStage = EffectiveRecoverableStage(existing);
            if (!MatchesStableBaiduUserId(session, existing))
                return NotValidated<TransferPreparationReceipt>(SubmissionAcceptance.DefinitelyNotAccepted);
            if (existingStage == WebAutomationAttemptState.BaselinePersisted)
            {
                if (existing?.SourceEvidence is null || existing.TargetPreparationEvidence is null)
                    return NotValidated<TransferPreparationReceipt>(SubmissionAcceptance.DefinitelyNotAccepted);
                var prepared = Prepared();
                if (existing!.State != WebAutomationAttemptState.UserHandoff)
                    return prepared;
                if (!HasExactCandidateBinding(context))
                    return NotValidated<TransferPreparationReceipt>(SubmissionAcceptance.DefinitelyNotAccepted);
                try
                {
                    var resumed = await broker.EnqueueAsync(
                        Request(
                            context,
                            share,
                            (_, _, _) => Task.FromResult(prepared),
                            session.BaiduUserId),
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
        if (!HasExactCandidateBinding(context))
            return NotValidated<TransferPreparationReceipt>(SubmissionAcceptance.DefinitelyNotAccepted);
        var request = Request(
            context,
            share,
            async (visibleSession, visibleContext, token) =>
            {
                if (visibleSession is not ITransferAutomationSession automationSession)
                    return NotValidated<TransferPreparationReceipt>(SubmissionAcceptance.DefinitelyNotAccepted);
                var current = await store.GetAsync(
                    context.OperationId, context.AccountId, context.Generation, token)
                    .ConfigureAwait(false);
                if (current?.SourceEvidence is null)
                    return NotValidated<TransferPreparationReceipt>(SubmissionAcceptance.DefinitelyNotAccepted);
                var result = await flow.PrepareFromPersistedSourceAsync(
                    new PersistentTransferAutomationSession(
                        automationSession, visibleContext, store, TimeProvider.System),
                    new TransferAutomationRequest(share, destination, duplicatePolicy),
                    current.SourceEvidence,
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
            },
            session.BaiduUserId);
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
        if (!MatchesStableBaiduUserId(session, attempt))
            return NotValidated<TransferReceipt>(SubmissionAcceptance.DefinitelyNotAccepted);
        if (EffectiveRecoverableStage(attempt) != WebAutomationAttemptState.BaselinePersisted)
            return Unknown<TransferReceipt>(
                attempt?.State == WebAutomationAttemptState.UserHandoff
                    ? SubmissionAcceptance.Unknown
                    : SubmissionAcceptance.DefinitelyNotAccepted);
        if (!HasExactCandidateBinding(context))
            return NotValidated<TransferReceipt>(SubmissionAcceptance.DefinitelyNotAccepted);
        if (attempt?.SourceEvidence is null || attempt.TargetPreparationEvidence is null)
            return NotValidated<TransferReceipt>(SubmissionAcceptance.DefinitelyNotAccepted);
        TransferPreparationEvidence preparation;
        try
        {
            preparation = new TransferPreparationEvidence(
                attempt.SourceEvidence, attempt.TargetPreparationEvidence).Validate();
        }
        catch (ArgumentException)
        {
            return NotValidated<TransferReceipt>(SubmissionAcceptance.DefinitelyNotAccepted);
        }
        var request = Request(
            context,
            share,
            async (visibleSession, visibleContext, token) =>
            {
                if (visibleSession is not ITransferAutomationSession automationSession)
                    return NotValidated<TransferReceipt>(SubmissionAcceptance.DefinitelyNotAccepted);
                var result = await flow.SubmitAsync(
                    new PersistentTransferAutomationSession(
                        automationSession, visibleContext, attemptStore, TimeProvider.System),
                    new TransferAutomationRequest(share, destination, duplicatePolicy),
                    preparation,
                    token).ConfigureAwait(false);
                return await MarkSucceededAfterExactAsync(result, visibleContext, token).ConfigureAwait(false);
            },
            session.BaiduUserId!);
        VisibleWebAutomationResult<BaiduAdapterResult<TransferReceipt>> result;
        try { result = await broker.EnqueueAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (WebAutomationAuthorizationException)
        {
            return NotValidated<TransferReceipt>(SubmissionAcceptance.DefinitelyNotAccepted);
        }
        if (result.Disposition is VisibleWebAutomationDisposition.ActionCompleted && result.Value is not null)
        {
            if (result.Value is { IsSuccess: true, Value.ExactRemoteItems: not null } exact && acceptanceEvidence is not null)
                await RecordAcceptanceEvidenceSafelyAsync(
                    EvidenceRequest(context, AdapterWriteAction.SubmitTransfer),
                    exact.Value!.ExactRemoteItems.Fingerprint,
                    recovery: false).ConfigureAwait(false);
            return result.Value;
        }
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
        RequireWebView2ExecutionProfile(context.ExecutionProfile);
        if (context.AccountId != session.LocalAccountId ||
            !string.Equals(context.SourceShare.StableShareId, query.SourceShareId, StringComparison.Ordinal))
            throw new InvalidOperationException("The transfer recovery identity does not match its account or source share.");
        if (attemptStore is null) return UnknownConfirmation();
        var attempt = await attemptStore.GetAsync(
            context.OperationId,
            context.AccountId,
            context.Generation,
            cancellationToken).ConfigureAwait(false);
        if (!MatchesStableBaiduUserId(session, attempt))
            return NotValidated<TransferConfirmationResult>(SubmissionAcceptance.Unknown);
        if (attempt?.State == WebAutomationAttemptState.Succeeded)
        {
            if (!HasCompleteCurrentSucceededEvidence(session, query, attempt))
                return NotValidated<TransferConfirmationResult>(SubmissionAcceptance.Unknown);
            var acceptedRemoteItems = attempt.AcceptedRemoteItems!;
            if (acceptanceEvidence is not null)
            {
                await RecordAcceptanceEvidenceSafelyAsync(
                    EvidenceRequest(context, AdapterWriteAction.SubmitTransfer),
                    acceptedRemoteItems.Fingerprint,
                    recovery: false).ConfigureAwait(false);
            }
            return BaiduAdapterResult<TransferConfirmationResult>.Success(
                TransferConfirmationResult.ExactPresent(acceptedRemoteItems),
                SubmissionAcceptance.Unknown);
        }
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
                        (_, _, _) => Task.FromResult(absent),
                        session.BaiduUserId!),
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
        if (attempt?.SourceEvidence is null || attempt.TargetPreparationEvidence is null ||
            attempt.SubmissionEvidence is null || recoveryAuthorization is null ||
            string.IsNullOrWhiteSpace(session.BaiduUserId))
            return UnknownConfirmation();
        var destination = new RemoteDirectory(
            attempt.TargetPreparationEvidence.TargetExactId,
            attempt.TargetPreparationEvidence.TargetPath);
        if (destination.Path != query.TargetPath)
            return UnknownConfirmation();
        var recoveryBinding = await recoveryAuthorization.TryAuthorizeSubmissionRecoveryAsync(
            new TransferSubmissionRecoveryAuthorizationRequest(
                context.OperationId,
                context.BatchId,
                context.AccountId,
                context.Generation,
                destination,
                session.BaiduUserId,
                attempt.SourceEvidence.Capability),
            cancellationToken).ConfigureAwait(false);
        if (recoveryBinding is null) return UnknownConfirmation();
        TransferOfficialAcceptance? officialAcceptance = null;
        if (attempt.AcceptedRemoteItems is not null &&
            attempt.TransferIdentityMapping is not null &&
            !string.IsNullOrWhiteSpace(attempt.OfficialRequestId))
        {
            officialAcceptance = new TransferOfficialAcceptance(
                TransferOfficialAcceptanceStatus.Accepted,
                attempt.OfficialRequestId,
                attempt.AcceptedRemoteItems,
                attempt.TransferIdentityMapping,
                attempt.ObservationFingerprint ?? attempt.AcceptedRemoteItems.Fingerprint,
                destination.RemoteId);
        }

        var request = RecoveryRequest(
            context,
            recoveryBinding,
            async (visibleSession, visibleContext, token) =>
            {
                if (visibleSession is not ITransferAutomationSession automationSession)
                    return NotValidated<TransferConfirmationResult>(SubmissionAcceptance.Unknown);
                var result = await flow.ConfirmExactFromTargetAsync(
                    automationSession,
                    destination,
                    new TransferPreparationEvidence(
                        attempt.SourceEvidence, attempt.TargetPreparationEvidence).Validate(),
                    attempt.SubmissionEvidence,
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
        if (result.Disposition is not VisibleWebAutomationDisposition.ActionCompleted || result.Value is null)
            return UnknownConfirmation();
        if (result.Value is { IsSuccess: true, Value.State: TransferConfirmationState.ExactPresent } && acceptanceEvidence is not null)
        {
            await RecordAcceptanceEvidenceSafelyAsync(
                EvidenceRequest(context, AdapterWriteAction.SubmitTransfer),
                attempt.AcceptedRemoteItems!.Fingerprint,
                recovery: true).ConfigureAwait(false);
        }
        return result.Value;
    }

    private AdapterWriteAuthorizationRequest EvidenceRequest(
        TransferOperationAutomationContext context,
        AdapterWriteAction action) => new(
            new AdapterCapabilityVersion(
                "BaiduTransfer",
                BaiduSelectorCatalog.TransferProtocolVersion,
                BaiduSelectorCatalog.TransferSelectorVersion,
                applicationBuildVersion),
            context.AccountId,
            context.OperationId,
            context.Generation,
            context.BatchId,
            action,
            context.IsRecovery);

    private async Task RecordAcceptanceEvidenceSafelyAsync(
        AdapterWriteAuthorizationRequest request,
        string fingerprint,
        bool recovery)
    {
        if (acceptanceEvidence is null) return;
        try
        {
            await acceptanceEvidence.RecordRemoteResultAsync(
                request, fingerprint, CancellationToken.None).ConfigureAwait(false);
            if (recovery)
                await acceptanceEvidence.RecordRecoveryAsync(
                    request, fingerprint, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Acceptance telemetry follows the persisted exact result and cannot change it.
        }
    }

    private BaiduNavigationAuthorizationRequest Navigation(
        TransferOperationAutomationContext context,
        Uri officialUri) => new(
        BaiduAutomationAction.OpenOfficialUri,
        officialUri,
        BaiduSelectorCatalog.TransferProtocolVersion,
        BaiduSelectorCatalog.TransferSelectorVersion,
        applicationBuildVersion,
        new BaiduAutomationAcceptance(
            false,
            BaiduSelectorCatalog.TransferProtocolVersion,
            BaiduSelectorCatalog.TransferSelectorVersion,
            applicationBuildVersion),
        BaiduProtocolValidationStatus.NotValidated,
        new BaiduAcceptanceOperationScope(
            "BaiduTransfer", context.AccountId, context.OperationId,
            context.Generation, context.BatchId));

    private VisibleWebAutomationRequest<T> Request<T>(
        TransferOperationAutomationContext context,
        ShareReference share,
        Func<IVisibleWebAutomationSession, VisibleWebAutomationContext, CancellationToken, Task<T>> action,
        string expectedBaiduUserId)
    {
        return Request(context, new Uri(share.OriginalUrl), action, expectedBaiduUserId);
    }

    private VisibleWebAutomationRequest<T> Request<T>(
        TransferOperationAutomationContext context,
        Uri officialUri,
        Func<IVisibleWebAutomationSession, VisibleWebAutomationContext, CancellationToken, Task<T>> action,
        string expectedBaiduUserId)
    {
        var identity = new WebAutomationRequestIdentity(
            context.OperationId,
            context.BatchId,
            context.AccountId,
            context.Generation,
            WebAutomationOwnerKind.Transfer,
            context.ExecutionProfile);
        var profilePath = profilePaths.GetAccountProfilePath(context.AccountId);
        profilePaths.EnsureSafeAccountPath(context.AccountId, profilePath);
        var candidate = context.AuthorizationBinding is not { } binding
            ? null
            : new BaiduCandidateSessionBinding(
                new AdapterWriteAuthorizationRequest(
                    binding.Capability,
                    context.AccountId,
                    context.OperationId,
                    context.Generation,
                    context.BatchId,
                    AdapterWriteAction.EnsureDirectory,
                    context.IsRecovery),
                binding,
                expectedBaiduUserId);
        return new(identity, profilePath, Navigation(context, officialUri), action, candidate);
    }

    private VisibleWebAutomationRequest<T> RecoveryRequest<T>(
        TransferOperationAutomationContext context,
        ReadOnlyTransferRecoveryBinding binding,
        Func<IVisibleWebAutomationSession, VisibleWebAutomationContext, CancellationToken, Task<T>> action)
    {
        var identity = new WebAutomationRequestIdentity(
            context.OperationId, context.BatchId, context.AccountId, context.Generation,
            WebAutomationOwnerKind.Transfer, context.ExecutionProfile);
        var profilePath = profilePaths.GetAccountProfilePath(context.AccountId);
        profilePaths.EnsureSafeAccountPath(context.AccountId, profilePath);
        var navigation = new BaiduNavigationAuthorizationRequest(
            BaiduAutomationAction.OpenOfficialUri,
            binding.OfficialTargetUri,
            binding.Capability.ProtocolVersion,
            binding.Capability.SelectorVersion,
            binding.Capability.BuildVersion,
            new BaiduAutomationAcceptance(
                false,
                binding.Capability.ProtocolVersion,
                binding.Capability.SelectorVersion,
                binding.Capability.BuildVersion),
            BaiduProtocolValidationStatus.NotValidated,
            new BaiduAcceptanceOperationScope(
                "BaiduTransfer", context.AccountId, context.OperationId,
                context.Generation, context.BatchId));
        return new VisibleWebAutomationRequest<T>(
            identity, profilePath, navigation, action,
            candidateBinding: null, transferRecoveryBinding: binding);
    }

    private bool HasExactCandidateBinding(TransferOperationAutomationContext context) =>
        context.AuthorizationBinding is { } binding &&
        binding.Capability == new AdapterCapabilityVersion(
            "BaiduTransfer",
            BaiduSelectorCatalog.TransferProtocolVersion,
            BaiduSelectorCatalog.TransferSelectorVersion,
            applicationBuildVersion) &&
        binding.AccountId == context.AccountId &&
        binding.OperationId == context.OperationId &&
        binding.Generation == context.Generation &&
        binding.BatchId == context.BatchId &&
        binding.IsRecovery == context.IsRecovery;

    private bool HasCompleteCurrentSucceededEvidence(
        AccountSession session,
        TransferConfirmationQuery query,
        WebAutomationAttemptRecord attempt)
    {
        if (attempt.OwnerKind != WebAutomationOwnerKind.Transfer ||
            attempt.SourceEvidence is not { } source ||
            attempt.TargetPreparationEvidence is not { } target ||
            attempt.SubmissionEvidence is not { } submission ||
            attempt.AcceptedRemoteItems is not { } accepted ||
            string.IsNullOrWhiteSpace(attempt.OfficialRequestId) ||
            attempt.TransferIdentityMapping is not { } mapping ||
            target.TargetPath != query.TargetPath)
            return false;
        var expectedCapability = new AdapterCapabilityVersion(
            "BaiduTransfer",
            BaiduSelectorCatalog.TransferProtocolVersion,
            BaiduSelectorCatalog.TransferSelectorVersion,
            applicationBuildVersion);
        if (source.Capability != expectedCapability || submission.Capability != expectedCapability ||
            !string.Equals(source.BaiduUserId, session.BaiduUserId, StringComparison.Ordinal) ||
            !string.Equals(target.BaiduUserId, session.BaiduUserId, StringComparison.Ordinal) ||
            !string.Equals(submission.BaiduUserId, session.BaiduUserId, StringComparison.Ordinal) ||
            !string.Equals(submission.SourceFingerprint, source.Items.Fingerprint, StringComparison.Ordinal) ||
            !string.Equals(submission.TargetExactId, target.TargetExactId, StringComparison.Ordinal) ||
            !string.Equals(
                submission.TargetBaselineFingerprint,
                target.Baseline.Fingerprint,
                StringComparison.Ordinal) ||
            !mapping.Covers(source.Items))
            return false;
        try
        {
            _ = new TransferPreparationEvidence(source, target).Validate();
            var mappedItems = new RemoteItemIdentitySet(mapping.Entries.Select(entry =>
                new RemoteItemIdentity(entry.DestinationExactId, null)));
            return mappedItems == accepted;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private bool MatchesStableBaiduUserId(
        AccountSession session,
        WebAutomationAttemptRecord? attempt)
    {
        if (session.LocalAccountId is null || string.IsNullOrWhiteSpace(session.BaiduUserId))
            return false;
        if (attempt?.SourceEvidence is null) return true;
        var expectedCapability = new AdapterCapabilityVersion(
            "BaiduTransfer",
            BaiduSelectorCatalog.TransferProtocolVersion,
            BaiduSelectorCatalog.TransferSelectorVersion,
            applicationBuildVersion);
        return attempt.SourceEvidence.Capability == expectedCapability &&
            string.Equals(
                attempt.SourceEvidence.BaiduUserId,
                session.BaiduUserId,
                StringComparison.Ordinal) &&
            (attempt.TargetPreparationEvidence is null || string.Equals(
                attempt.TargetPreparationEvidence.BaiduUserId,
                session.BaiduUserId,
                StringComparison.Ordinal)) &&
            (attempt.SubmissionEvidence is null || string.Equals(
                attempt.SubmissionEvidence.BaiduUserId,
                session.BaiduUserId,
                StringComparison.Ordinal));
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
        RequireWebView2ExecutionProfile(context.ExecutionProfile);
        if (context.AccountId != session.LocalAccountId || context.SourceShare != share)
            throw new InvalidOperationException("The transfer automation identity does not match its account or source share.");
    }

    private static void RequireWebView2ExecutionProfile(ExecutionProfileSnapshot snapshot)
    {
        if (snapshot.Engine != BaiduExecutionEngine.WebView2V1 ||
            snapshot.BrowserFamily != BrowserFamily.LegacyWebView2)
            throw new InvalidOperationException("The persisted execution engine is unavailable in this adapter.");
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
        VisibleWebAutomationContext context,
        IWebAutomationAttemptStore attemptStore,
        TimeProvider timeProvider) : ITransferAutomationSession
    {
        public Task<TransferSourceListing> OpenExternalShareAsync(ShareReference share, CancellationToken cancellationToken) =>
            inner.OpenExternalShareAsync(share, cancellationToken);

        public Task<TransferSourceListing> OpenAndReadSourceForSubmissionAsync(
            ShareReference share,
            CancellationToken cancellationToken) =>
            inner.OpenAndReadSourceForSubmissionAsync(share, cancellationToken);

        public Task EnterExtractionCodeAsync(string extractionCode, CancellationToken cancellationToken) =>
            inner.EnterExtractionCodeAsync(extractionCode, cancellationToken);

        public Task<TransferDirectoryListing> OpenDestinationAndReadStableListingAsync(RemoteDirectory destination, CancellationToken cancellationToken) =>
            inner.OpenDestinationAndReadStableListingAsync(destination, cancellationToken);

        public Task<TransferDirectoryListing> OpenExactTargetAndReadStableListingAsync(
            RemoteDirectory destination,
            CancellationToken cancellationToken) =>
            inner.OpenExactTargetAndReadStableListingAsync(destination, cancellationToken);

        public async Task PersistBaselineAsync(WebAutomationBaseline baseline, string observationFingerprint, CancellationToken cancellationToken)
        {
            await context.PersistBaselineAsync(baseline, observationFingerprint, cancellationToken).ConfigureAwait(false);
            await inner.PersistBaselineAsync(baseline, observationFingerprint, cancellationToken).ConfigureAwait(false);
        }

        public async Task PersistTargetPreparationAsync(
            TransferTargetPreparationEvidence evidence,
            CancellationToken cancellationToken)
        {
            var identity = context.Identity;
            var attempt = await attemptStore.GetAsync(
                identity.OperationId, identity.AccountId, identity.Generation, cancellationToken)
                .ConfigureAwait(false) ?? throw new InvalidOperationException("转存尝试不存在。");
            if (attempt.State != WebAutomationAttemptState.Preparing ||
                !await attemptStore.TryPersistTransferTargetPreparationAsync(
                    identity.OperationId,
                    identity.AccountId,
                    identity.Generation,
                    attempt.Revision,
                    evidence,
                    timeProvider.GetUtcNow(),
                    cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("转存目标基线未能持久化。");
            context.TrackPersistentStage(
                WebAutomationAttemptState.BaselinePersisted,
                evidence.ObservationFingerprint);
            await inner.PersistTargetPreparationAsync(evidence, cancellationToken).ConfigureAwait(false);
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

        public async Task MarkSubmittingAsync(
            TransferSubmissionEvidence evidence,
            CancellationToken cancellationToken)
        {
            var identity = context.Identity;
            var attempt = await attemptStore.GetAsync(
                identity.OperationId, identity.AccountId, identity.Generation, cancellationToken)
                .ConfigureAwait(false) ?? throw new InvalidOperationException("转存尝试不存在。");
            if (attempt.State != WebAutomationAttemptState.BaselinePersisted ||
                !await attemptStore.TryPersistTransferSubmissionAsync(
                    identity.OperationId,
                    identity.AccountId,
                    identity.Generation,
                    attempt.Revision,
                    evidence,
                    evidence.MarkedSubmittingAtUtc,
                    cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("转存提交边界未能持久化。");
            context.TrackPersistentStage(WebAutomationAttemptState.Submitting);
            await inner.MarkSubmittingAsync(evidence, cancellationToken).ConfigureAwait(false);
        }

        public async Task MarkSubmissionResultPendingAsync(CancellationToken cancellationToken)
        {
            await context.AdvancePersistentStageAsync(
                WebAutomationAttemptState.Submitting,
                WebAutomationAttemptState.ResultPending,
                null,
                cancellationToken).ConfigureAwait(false);
            await inner.MarkSubmissionResultPendingAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task MarkSubmissionDefinitelyNotAcceptedAsync(CancellationToken cancellationToken)
        {
            await context.AdvancePersistentStageAsync(
                WebAutomationAttemptState.Submitting,
                WebAutomationAttemptState.DefinitelyNotSubmitted,
                null,
                cancellationToken).ConfigureAwait(false);
            await inner.MarkSubmissionDefinitelyNotAcceptedAsync(cancellationToken).ConfigureAwait(false);
        }

        public Task<TransferOfficialAcceptance> SaveWholeShareAsync(RemoteDirectory destination, DuplicatePolicy duplicatePolicy, CancellationToken cancellationToken) =>
            inner.SaveWholeShareAsync(destination, duplicatePolicy, cancellationToken);

        public Task<TransferOfficialAcceptance> SaveWholeShareExactlyOnceAsync(
            RemoteDirectory destination,
            TransferSourceEvidence source,
            DuplicatePolicy duplicatePolicy,
            CancellationToken cancellationToken) =>
            inner.SaveWholeShareExactlyOnceAsync(
                destination, source, duplicatePolicy, cancellationToken);

        public Task PrepareWholeShareSubmissionAsync(
            RemoteDirectory destination,
            TransferSourceEvidence source,
            DuplicatePolicy duplicatePolicy,
            CancellationToken cancellationToken) =>
            inner.PrepareWholeShareSubmissionAsync(
                destination, source, duplicatePolicy, cancellationToken);

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

        public async Task PersistTransferAcceptanceAsync(
            TransferOfficialAcceptance acceptance,
            CancellationToken cancellationToken)
        {
            if (!acceptance.IsAccepted || acceptance.ExactRemoteItems is null ||
                acceptance.IdentityMapping is null ||
                string.IsNullOrWhiteSpace(acceptance.AdapterRequestId))
                throw new InvalidOperationException("官方转存结果缺少精确身份映射。");
            var identity = context.Identity;
            var attempt = await attemptStore.GetAsync(
                identity.OperationId, identity.AccountId, identity.Generation, cancellationToken)
                .ConfigureAwait(false) ?? throw new InvalidOperationException("转存尝试不存在。");
            var updatedAt = timeProvider.GetUtcNow();
            if (attempt.State != WebAutomationAttemptState.Submitting ||
                !await attemptStore.TryPersistTransferAcceptanceAsync(
                    identity.OperationId,
                    identity.AccountId,
                    identity.Generation,
                    attempt.Revision,
                    acceptance.AdapterRequestId,
                    acceptance.IdentityMapping,
                    acceptance.ObservationFingerprint,
                    updatedAt,
                    cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("官方转存结果未能持久化。");
            context.TrackPersistentStage(
                WebAutomationAttemptState.ResultPending,
                acceptance.ObservationFingerprint);
            await inner.PersistTransferAcceptanceAsync(acceptance, cancellationToken).ConfigureAwait(false);
        }

        public Task<TransferDirectoryListing> ReopenDestinationAndReadStableListingAsync(RemoteDirectory destination, CancellationToken cancellationToken) =>
            inner.ReopenDestinationAndReadStableListingAsync(destination, cancellationToken);
    }
}
