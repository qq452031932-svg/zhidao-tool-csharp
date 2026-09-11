using BaiduBatchTransfer.App.Features.BrowserAutomation;
using BaiduBatchTransfer.App.Features.Acceptance;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.BaiduAdapter.WebAutomation;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using System.Collections.Immutable;

namespace BaiduBatchTransfer.App.Features.ManagedBrowser;

internal enum ManagedTransferFinalSubmissionResultKind
{
    ExactSuccess = 1,
    DefinitelyNotDispatched,
    ResultPending,
    UserActionRequired,
    NotEligible,
    TimedOut,
    Cancelled,
}

internal sealed class ManagedTransferFinalSubmissionResult
{
    internal ManagedTransferFinalSubmissionResult(
        ManagedTransferFinalSubmissionResultKind kind,
        string? destinationExactId = null,
        ManagedTransferFinalSubmissionQuarantine? quarantine = null)
    {
        if ((kind == ManagedTransferFinalSubmissionResultKind.ExactSuccess) !=
                (destinationExactId is not null) ||
            (kind == ManagedTransferFinalSubmissionResultKind.ResultPending) !=
                (quarantine is not null))
            throw new ArgumentException("The final-submission result payload is inconsistent.");
        Kind = kind;
        DestinationExactId = destinationExactId;
        Quarantine = quarantine;
    }

    internal ManagedTransferFinalSubmissionResultKind Kind { get; }
    internal string? DestinationExactId { get; }
    internal ManagedTransferFinalSubmissionQuarantine? Quarantine { get; }
    public override string ToString() =>
        $"ManagedTransferFinalSubmissionResult {{ Kind = {Kind}, Destination = [REDACTED], Quarantine = {(Quarantine is null ? "None" : "Held")} }}";
}

internal enum ManagedTransferFinalSubmissionProofRoute
{
    OriginalSource = 1,
    DestinationPicker,
    ExactTargetDirectory,
}

internal sealed record ManagedTransferFinalSubmissionProofExpectation(
    ManagedTransferFinalSubmissionProofRoute State);

internal sealed class ManagedTransferFinalSubmissionProofRequest
{
    internal ManagedTransferFinalSubmissionProofRequest(
        ManagedTransferFinalSubmissionRequest request,
        ManagedTransferFinalSubmissionProofRoute route)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        ExpectedRoute = new(route);
    }

    internal ManagedTransferFinalSubmissionRequest Request { get; }
    internal ManagedTransferFinalSubmissionProofExpectation ExpectedRoute { get; }
    public override string ToString() =>
        $"ManagedTransferFinalSubmissionProofRequest {{ Route = {ExpectedRoute.State}, Evidence = [REDACTED] }}";
}

internal enum ManagedTransferRuntimeProofKind { Exact = 1, NotExact }

internal sealed record ManagedTransferRuntimeProofResult(
    ManagedTransferRuntimeProofKind Kind,
    ManagedTransferFinalSubmissionProofRoute Route,
    ManagedBrowserLifecycleSession? Lifecycle,
    int ProcessId,
    Guid BrowserId,
    string? TargetId,
    Uri? Endpoint,
    string? SourceObservationFingerprint);

internal enum ManagedTransferPermitOwnershipKind { Owned = 1, Lost }

internal sealed record ManagedTransferPermitOwnershipResult(
    ManagedTransferPermitOwnershipKind Kind,
    Guid OperationId,
    Guid AccountId,
    int Generation,
    AdapterWriteAction Action);

internal enum ManagedTransferProcessExitObservationKind { Observed = 1, NotObserved }

internal sealed record ManagedTransferProcessExitObservation(
    ManagedTransferProcessExitObservationKind Kind,
    int ProcessId,
    Guid BrowserId,
    string TargetId);

/// <summary>
/// Closed typed boundary used only by the orchestration session. Implementations return the concrete
/// picker/directory/submission/target-reader results; the session, never the boundary, decides whether
/// those observations prove eligibility, dispatch or success.
/// </summary>
internal interface IManagedTransferFinalSubmissionBoundaries
{
    Task<IDisposable> AcquireAccountLeaseAsync(Guid accountId, CancellationToken cancellationToken);
    Task<ManagedTransferRuntimeProofResult> ObserveRuntimeAsync(
        ManagedTransferFinalSubmissionProofRequest request, CancellationToken cancellationToken);
    Task<ManagedTransferPickerOpenResult> OpenPickerAsync(
        ManagedTransferFinalSubmissionRequest request, CancellationToken cancellationToken);
    Task<ManagedTransferDirectoryMutationExecutionResult> EnterDirectoryForSelectionAsync(
        ManagedTransferFinalSubmissionRequest request, string childExactId,
        CancellationToken cancellationToken);
    Task<ManagedTransferReadinessResult> ObserveReadinessAsync(
        ManagedBrowserTransferSubmissionContext context, CancellationToken cancellationToken);
    ValueTask<IBaiduCandidateMutationPermit?> AcquireFinalPermitAsync(
        ManagedTransferFinalSubmissionRequest request, CancellationToken cancellationToken);
    Task<ManagedTransferPermitOwnershipResult> ObservePermitOwnershipAsync(
        ManagedTransferFinalSubmissionRequest request, IBaiduCandidateMutationPermit permit,
        CancellationToken cancellationToken);
    Task<ManagedTransferSubmitResult> SubmitOnceAsync(
        ManagedBrowserTransferSubmissionReadiness readiness, CancellationToken cancellationToken);
    Task<ManagedTransferOfficialOutcome> ObserveOutcomeAsync(
        ManagedBrowserTransferSubmissionReadiness readiness, CancellationToken cancellationToken);
    Task<ManagedBrowserOpenTransferTargetRootResult> OpenFixedTargetRootAsync(
        ManagedTransferFinalSubmissionRequest request, CancellationToken cancellationToken);
    Task<ManagedBrowserEnterTransferTargetDirectoryResult> EnterFixedTargetDirectoryAsync(
        ManagedTransferFinalSubmissionRequest request,
        ManagedBrowserTargetDirectoryHeader expectedParent,
        string childExactId,
        string childServerName,
        string childCanonicalPath,
        CancellationToken cancellationToken);
    Task<ManagedTransferTargetInventoryReadResult> ReadFixedTargetAsync(
        ManagedTransferFinalSubmissionRequest request,
        string expectedDirectoryExactId,
        string expectedCanonicalPath,
        IReadOnlyList<string> expectedAncestorExactIds,
        CancellationToken cancellationToken);
    Task<ManagedTransferProcessExitObservation> ObserveProcessExitAsync(
        ManagedTransferFinalSubmissionRequest request, CancellationToken cancellationToken);
}

internal interface IManagedTransferFinalSubmissionBoundaryFactory
{
    IManagedTransferFinalSubmissionBoundaries CreateScope(
        ManagedTransferFinalSubmissionRequest request);
}

internal interface IManagedTransferProcessExitObserver
{
    Task<ManagedTransferProcessExitObservation> ObserveAsync(
        ManagedChromiumSessionHandle exactHandle, CancellationToken cancellationToken);
}

/// <summary>
/// Explicit non-DI adapter over the reviewed managed-browser primitives. It owns no policy: every returned
/// observation is rechecked by <see cref="ManagedTransferFinalSubmissionSession"/>.
/// </summary>
internal sealed class ManagedTransferFinalSubmissionConcreteBoundaries :
    IManagedTransferFinalSubmissionBoundaryFactory
{
    private readonly ManagedBrowserLifecycleCoordinator lifecycle;
    private readonly IManagedTransferRouteProof routeProof;
    private readonly ManagedTransferPickerOpener pickerOpener;
    private readonly ManagedTransferDirectoryReader directoryReader;
    private readonly ManagedTransferDirectoryMutator directoryMutator;
    private readonly ManagedTransferSubmissionOperator submissionOperator;
    private readonly ManagedTransferTargetInventoryReader targetReader;
    private readonly Func<ManagedBrowserRequest, CancellationToken, Task<ManagedBrowserResponse>> send;
    private readonly IBaiduCandidateActionAuthorizationSource authorizationSource;
    private readonly IManagedTransferProcessExitObserver processExitObserver;

    internal ManagedTransferFinalSubmissionConcreteBoundaries(
        ManagedBrowserLifecycleCoordinator lifecycle,
        IManagedTransferRouteProof routeProof,
        ManagedTransferPickerOpener pickerOpener,
        ManagedTransferDirectoryReader directoryReader,
        ManagedTransferDirectoryMutator directoryMutator,
        ManagedTransferSubmissionOperator submissionOperator,
        ManagedTransferTargetInventoryReader targetReader,
        ManagedBrowserHelperClient helperClient,
        IBaiduCandidateActionAuthorizationSource authorizationSource,
        IManagedTransferProcessExitObserver processExitObserver)
        : this(lifecycle, routeProof, pickerOpener, directoryReader, directoryMutator,
            submissionOperator, targetReader,
            helperClient is null ? throw new ArgumentNullException(nameof(helperClient)) : helperClient.SendAsync,
            authorizationSource, processExitObserver)
    {
    }

    internal ManagedTransferFinalSubmissionConcreteBoundaries(
        ManagedBrowserLifecycleCoordinator lifecycle,
        IManagedTransferRouteProof routeProof,
        ManagedTransferPickerOpener pickerOpener,
        ManagedTransferDirectoryReader directoryReader,
        ManagedTransferDirectoryMutator directoryMutator,
        ManagedTransferSubmissionOperator submissionOperator,
        ManagedTransferTargetInventoryReader targetReader,
        Func<ManagedBrowserRequest, CancellationToken, Task<ManagedBrowserResponse>> send,
        IBaiduCandidateActionAuthorizationSource authorizationSource,
        IManagedTransferProcessExitObserver processExitObserver)
    {
        this.lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        this.routeProof = routeProof ?? throw new ArgumentNullException(nameof(routeProof));
        this.pickerOpener = pickerOpener ?? throw new ArgumentNullException(nameof(pickerOpener));
        this.directoryReader = directoryReader ?? throw new ArgumentNullException(nameof(directoryReader));
        this.directoryMutator = directoryMutator ?? throw new ArgumentNullException(nameof(directoryMutator));
        this.submissionOperator = submissionOperator ?? throw new ArgumentNullException(nameof(submissionOperator));
        this.targetReader = targetReader ?? throw new ArgumentNullException(nameof(targetReader));
        this.send = send ?? throw new ArgumentNullException(nameof(send));
        this.authorizationSource = authorizationSource ?? throw new ArgumentNullException(nameof(authorizationSource));
        this.processExitObserver = processExitObserver ?? throw new ArgumentNullException(nameof(processExitObserver));
    }

    public IManagedTransferFinalSubmissionBoundaries CreateScope(
        ManagedTransferFinalSubmissionRequest request) => new Scope(this, request);

    private sealed class Scope : IManagedTransferFinalSubmissionBoundaries
    {
    private readonly ManagedBrowserLifecycleCoordinator lifecycle;
    private readonly IManagedTransferRouteProof routeProof;
    private readonly ManagedTransferPickerOpener pickerOpener;
    private readonly ManagedTransferDirectoryReader directoryReader;
    private readonly ManagedTransferDirectoryMutator directoryMutator;
    private readonly ManagedTransferSubmissionOperator submissionOperator;
    private readonly ManagedTransferTargetInventoryReader targetReader;
    private readonly Func<ManagedBrowserRequest, CancellationToken, Task<ManagedBrowserResponse>> send;
    private readonly IBaiduCandidateActionAuthorizationSource authorizationSource;
    private readonly IManagedTransferProcessExitObserver processExitObserver;
    private readonly ManagedTransferFinalSubmissionRequest currentRequest;
    private ManagedTransferDirectoryEvidence? currentPickerDirectory;

    internal Scope(ManagedTransferFinalSubmissionConcreteBoundaries owner,
        ManagedTransferFinalSubmissionRequest request)
    {
        lifecycle = owner.lifecycle;
        routeProof = owner.routeProof;
        pickerOpener = owner.pickerOpener;
        directoryReader = owner.directoryReader;
        directoryMutator = owner.directoryMutator;
        submissionOperator = owner.submissionOperator;
        targetReader = owner.targetReader;
        send = owner.send;
        authorizationSource = owner.authorizationSource;
        processExitObserver = owner.processExitObserver;
        currentRequest = request ?? throw new ArgumentNullException(nameof(request));
    }

    public Task<IDisposable> AcquireAccountLeaseAsync(Guid accountId,
        CancellationToken cancellationToken) =>
        lifecycle.AcquireAccountLeaseAsync(accountId, cancellationToken);

    public async Task<ManagedTransferRuntimeProofResult> ObserveRuntimeAsync(
        ManagedTransferFinalSubmissionProofRequest proof,
        CancellationToken cancellationToken)
    {
        var request = proof.Request;
        var lifecycleSnapshot = await lifecycle.LoadActiveSessionAsync(
            request.Identity.AccountId, cancellationToken).ConfigureAwait(false);
        var route = proof.ExpectedRoute.State switch
        {
            ManagedTransferFinalSubmissionProofRoute.OriginalSource =>
                ManagedTransferRouteExpectation.OriginalSource,
            ManagedTransferFinalSubmissionProofRoute.DestinationPicker =>
                ManagedTransferRouteExpectation.ExactTargetDirectory(
                    request.PreparedDestination.FolderCandidatesSnapshot.ParentExactId,
                    request.PreparedDestination.FolderCandidatesSnapshot.CanonicalPath,
                    request.PreparedDestination.FolderCandidatesSnapshot.AncestorExactIds),
            ManagedTransferFinalSubmissionProofRoute.ExactTargetDirectory =>
                ManagedTransferRouteExpectation.ExactTargetDirectory(
                    request.PreparedDestination.Destination.RemoteId,
                    request.PreparedDestination.Destination.Path.Value,
                    request.PreparedDestination.FolderCandidatesSnapshot.AncestorExactIds),
            _ => throw new ArgumentOutOfRangeException(nameof(proof)),
        };
        var binding = TargetBinding(request);
        var exact = lifecycleSnapshot is not null &&
            await routeProof.VerifyAsync(new ManagedTransferRouteProofRequest(
                    request.ExactHandle, request.ExactManagedSession, request.Identity,
                    binding, route), cancellationToken).ConfigureAwait(false);
        return new(exact ? ManagedTransferRuntimeProofKind.Exact : ManagedTransferRuntimeProofKind.NotExact,
            proof.ExpectedRoute.State, lifecycleSnapshot, request.ExactHandle.RootProcessId,
            request.ExactHandle.BrowserId, request.ExactHandle.TargetId,
            request.ExactHandle.Endpoint, request.ImmutableSource.ObservationFingerprint);
    }

    public async Task<ManagedTransferPickerOpenResult> OpenPickerAsync(
        ManagedTransferFinalSubmissionRequest request, CancellationToken cancellationToken)
    {
        var mutation = Mutation(request, BaiduAutomationAction.OpenDestinationPicker);
        var binding = SourceBinding(request);
        var opened = await pickerOpener.OpenForSubmissionAsync(
            mutation, binding, SourceObservation(request), cancellationToken).ConfigureAwait(false);
        if (opened.Kind != ManagedBrowserTransferPickerResultKind.PickerOpened) return opened;
        var read = await directoryReader.ReadAsync(request.Identity.OperationId,
            request.Identity.AccountId, request.Identity.Generation,
            new ManagedBrowserReadTransferDirectoryRequest(binding, null, null),
            cancellationToken).ConfigureAwait(false);
        if (read.Kind != ManagedBrowserDirectoryListingResultKind.Observed || read.Evidence is null)
            return new(ManagedBrowserTransferPickerResultKind.OutcomeUnknown);
        currentPickerDirectory = read.Evidence;
        return opened;
    }

    public async Task<ManagedTransferDirectoryMutationExecutionResult> EnterDirectoryForSelectionAsync(
        ManagedTransferFinalSubmissionRequest request, string childExactId,
        CancellationToken cancellationToken)
    {
        if (currentPickerDirectory is null)
            return new(ManagedTransferDirectoryMutationOutcome.PageNotValidated,
                ManagedTransferDirectoryMutationState.None);
        var result = await directoryMutator.EnterDirectoryForSelectionAsync(
            request.Identity, request.CandidateBinding, SourceBinding(request),
            currentPickerDirectory, childExactId,
            new ManagedTransferDirectoryMutationState(true, false, false),
            new ManagedTransferDirectoryMutationRuntime(
                request.LifecycleSession, request.ExactHandle), cancellationToken).ConfigureAwait(false);
        if (result.Outcome == ManagedTransferDirectoryMutationOutcome.DirectoryEntered &&
            result.Evidence is not null)
            currentPickerDirectory = result.Evidence;
        return result;
    }

    public Task<ManagedTransferReadinessResult> ObserveReadinessAsync(
        ManagedBrowserTransferSubmissionContext context, CancellationToken cancellationToken) =>
        submissionOperator.ObserveReadyAsync(Current.Identity.OperationId,
            Current.Identity.AccountId, Current.Identity.Generation, context, cancellationToken);

    public ValueTask<IBaiduCandidateMutationPermit?> AcquireFinalPermitAsync(
        ManagedTransferFinalSubmissionRequest request, CancellationToken cancellationToken) =>
        authorizationSource.TryAcquireTransferMutationPermitAsync(
            Mutation(request, BaiduAutomationAction.ClickConfirmTransfer), cancellationToken);

    public Task<ManagedTransferPermitOwnershipResult> ObservePermitOwnershipAsync(
        ManagedTransferFinalSubmissionRequest request, IBaiduCandidateMutationPermit permit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var owned = authorizationSource is AcceptanceWriteLeaseCoordinator coordinator &&
            coordinator.HasExactActiveTransferMutationPermit(
                permit, request.CandidateBinding.Authorization,
                request.CandidateBinding.Binding,
                BaiduAutomationAction.ClickConfirmTransfer);
        return Task.FromResult(new ManagedTransferPermitOwnershipResult(
            owned ? ManagedTransferPermitOwnershipKind.Owned :
                ManagedTransferPermitOwnershipKind.Lost,
            request.Identity.OperationId, request.Identity.AccountId,
            request.Identity.Generation, AdapterWriteAction.SubmitTransfer));
    }

    public Task<ManagedTransferSubmitResult> SubmitOnceAsync(
        ManagedBrowserTransferSubmissionReadiness readiness, CancellationToken cancellationToken) =>
        submissionOperator.SubmitOnceAsync(Current.Identity.OperationId,
            Current.Identity.AccountId, Current.Identity.Generation, readiness, cancellationToken);

    public Task<ManagedTransferOfficialOutcome> ObserveOutcomeAsync(
        ManagedBrowserTransferSubmissionReadiness readiness, CancellationToken cancellationToken) =>
        submissionOperator.ObserveOutcomeAsync(Current.Identity.OperationId,
            Current.Identity.AccountId, Current.Identity.Generation, readiness, cancellationToken);

    public async Task<ManagedBrowserOpenTransferTargetRootResult> OpenFixedTargetRootAsync(
        ManagedTransferFinalSubmissionRequest request, CancellationToken cancellationToken)
    {
        var command = NewRequest(request, ManagedBrowserAction.OpenTransferTargetRoot,
            new ManagedBrowserOpenTransferTargetRootRequest(TargetBinding(request)));
        var response = await send(command, cancellationToken).ConfigureAwait(false);
        ManagedBrowserProtocol.ValidateResponseFor(command, response);
        return response.Result as ManagedBrowserOpenTransferTargetRootResult ??
            new(ManagedBrowserOpenTransferTargetRootResultKind.PageNotValidated,
                ManagedBrowserTargetNavigationState.KnownNotDispatched);
    }

    public async Task<ManagedBrowserEnterTransferTargetDirectoryResult> EnterFixedTargetDirectoryAsync(
        ManagedTransferFinalSubmissionRequest request,
        ManagedBrowserTargetDirectoryHeader expectedParent,
        string childExactId,
        string childServerName,
        string childCanonicalPath,
        CancellationToken cancellationToken)
    {
        var command = NewRequest(request, ManagedBrowserAction.EnterTransferTargetDirectory,
            new ManagedBrowserEnterTransferTargetDirectoryRequest(TargetBinding(request), expectedParent,
                childExactId, childServerName, childCanonicalPath));
        var response = await send(command, cancellationToken).ConfigureAwait(false);
        ManagedBrowserProtocol.ValidateResponseFor(command, response);
        return response.Result as ManagedBrowserEnterTransferTargetDirectoryResult ??
            new(ManagedBrowserEnterTransferTargetDirectoryResultKind.PageNotValidated,
                ManagedBrowserTargetNavigationState.KnownNotDispatched);
    }

    public Task<ManagedTransferTargetInventoryReadResult> ReadFixedTargetAsync(
        ManagedTransferFinalSubmissionRequest request,
        string expectedDirectoryExactId,
        string expectedCanonicalPath,
        IReadOnlyList<string> expectedAncestorExactIds,
        CancellationToken cancellationToken) => targetReader.ReadAsync(
            request.Identity.OperationId, request.Identity.AccountId, request.Identity.Generation,
            new ManagedBrowserReadTransferTargetDirectoryRequest(TargetBinding(request),
                expectedDirectoryExactId, expectedCanonicalPath, expectedAncestorExactIds),
            cancellationToken);

    public Task<ManagedTransferProcessExitObservation> ObserveProcessExitAsync(
        ManagedTransferFinalSubmissionRequest request, CancellationToken cancellationToken) =>
        processExitObserver.ObserveAsync(request.ExactHandle, cancellationToken);

    private static ManagedBrowserOpenTransferSourceRequest SourceBinding(
        ManagedTransferFinalSubmissionRequest request) => new(
            request.ExactHandle.BrowserId, request.ExactHandle.TargetId,
            request.ExactHandle.Endpoint, request.TimeoutMilliseconds,
            request.CanonicalSourceUri, request.ExactManagedSession.StableBaiduUserId);

    private static ManagedBrowserTargetActionBinding TargetBinding(
        ManagedTransferFinalSubmissionRequest request) => new(
            request.ExactHandle.BrowserId, request.ExactHandle.TargetId,
            request.ExactHandle.Endpoint, request.TimeoutMilliseconds,
            request.CanonicalSourceUri, request.ExactManagedSession.StableBaiduUserId,
            request.ImmutableSource);

    private static BaiduTransferCandidateObservation SourceObservation(
        ManagedTransferFinalSubmissionRequest request) => new(
            BaiduCandidatePageState.ExternalShare, request.ImmutableSource.BaiduUserId,
            null, null, ImmutableArray<string>.Empty,
            request.ImmutableSource.Items.Select(item => new BaiduTransferCandidateItem(
                item.ExactId,
                item.Kind == ManagedBrowserSourceItemKind.File
                    ? RemoteResourceKind.File : RemoteResourceKind.Folder,
                item.ServerName)).ToImmutableArray(),
            request.ImmutableSource.TotalCount, true,
            true, true, request.ImmutableSource.GenerationToken,
            request.ImmutableSource.ObservationFingerprint);

    private static BaiduCandidateDomMutationRequest Mutation(
        ManagedTransferFinalSubmissionRequest request, BaiduAutomationAction action) => new(
            request.CandidateBinding.Authorization, request.CandidateBinding.Binding, action,
            new BaiduCandidateDomReadRequest(new Uri(request.CanonicalSourceUri),
                BaiduCandidatePageState.ExternalShare,
                request.ExactManagedSession.StableBaiduUserId,
                request.ImmutableSource.Items.Select(item => item.ExactId), null, null), null);

    private static ManagedBrowserRequest NewRequest(ManagedTransferFinalSubmissionRequest request,
        ManagedBrowserAction action, ManagedBrowserRequestPayload payload) => new(
            Guid.NewGuid(), request.Identity.OperationId, request.Identity.AccountId,
            request.Identity.Generation, action, payload);

    private ManagedTransferFinalSubmissionRequest Current => currentRequest;
    }
}

internal sealed class ManagedTransferFinalSubmissionRequest
{
    private readonly IManagedTransferFinalSubmissionBoundaries? boundaries;
    internal ManagedTransferFinalSubmissionRequest(
        WebAutomationRequestIdentity identity,
        BaiduCandidateSessionBinding candidateBinding,
        BaiduExecutionContext executionContext,
        ManagedChromiumSessionHandle exactHandle,
        ManagedBrowserLifecycleSession lifecycleSession,
        ManagedBrowserSessionReference exactManagedSession,
        string canonicalSourceUri,
        ManagedBrowserSourceSnapshot immutableSource,
        ManagedPreparedDestinationEvidence preparedDestination,
        DuplicatePolicy duplicatePolicy,
        long expectedAttemptRevision,
        int timeoutMilliseconds,
        IWebAutomationAttemptStore attemptStore,
        IManagedTransferFinalSubmissionBoundaries boundaries)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        CandidateBinding = candidateBinding ?? throw new ArgumentNullException(nameof(candidateBinding));
        ExecutionContext = executionContext ?? throw new ArgumentNullException(nameof(executionContext));
        ExactHandle = exactHandle ?? throw new ArgumentNullException(nameof(exactHandle));
        LifecycleSession = lifecycleSession ?? throw new ArgumentNullException(nameof(lifecycleSession));
        ExactManagedSession = exactManagedSession ?? throw new ArgumentNullException(nameof(exactManagedSession));
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalSourceUri);
        CanonicalSourceUri = canonicalSourceUri;
        ImmutableSource = immutableSource ?? throw new ArgumentNullException(nameof(immutableSource));
        PreparedDestination = preparedDestination ?? throw new ArgumentNullException(nameof(preparedDestination));
        if (!Enum.IsDefined(duplicatePolicy))
            throw new ArgumentOutOfRangeException(nameof(duplicatePolicy));
        DuplicatePolicy = duplicatePolicy;
        ExpectedAttemptRevision = expectedAttemptRevision;
        TimeoutMilliseconds = timeoutMilliseconds;
        AttemptStore = attemptStore ?? throw new ArgumentNullException(nameof(attemptStore));
        this.boundaries = boundaries ?? throw new ArgumentNullException(nameof(boundaries));
    }

    internal ManagedTransferFinalSubmissionRequest(
        WebAutomationRequestIdentity identity,
        BaiduCandidateSessionBinding candidateBinding,
        BaiduExecutionContext executionContext,
        ManagedChromiumSessionHandle exactHandle,
        ManagedBrowserLifecycleSession lifecycleSession,
        ManagedBrowserSessionReference exactManagedSession,
        string canonicalSourceUri,
        ManagedBrowserSourceSnapshot immutableSource,
        ManagedPreparedDestinationEvidence preparedDestination,
        DuplicatePolicy duplicatePolicy,
        long expectedAttemptRevision,
        int timeoutMilliseconds,
        IWebAutomationAttemptStore attemptStore,
        IManagedTransferFinalSubmissionBoundaryFactory boundaryFactory)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        CandidateBinding = candidateBinding ?? throw new ArgumentNullException(nameof(candidateBinding));
        ExecutionContext = executionContext ?? throw new ArgumentNullException(nameof(executionContext));
        ExactHandle = exactHandle ?? throw new ArgumentNullException(nameof(exactHandle));
        LifecycleSession = lifecycleSession ?? throw new ArgumentNullException(nameof(lifecycleSession));
        ExactManagedSession = exactManagedSession ?? throw new ArgumentNullException(nameof(exactManagedSession));
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalSourceUri);
        CanonicalSourceUri = canonicalSourceUri;
        ImmutableSource = immutableSource ?? throw new ArgumentNullException(nameof(immutableSource));
        PreparedDestination = preparedDestination ?? throw new ArgumentNullException(nameof(preparedDestination));
        if (!Enum.IsDefined(duplicatePolicy))
            throw new ArgumentOutOfRangeException(nameof(duplicatePolicy));
        DuplicatePolicy = duplicatePolicy;
        ExpectedAttemptRevision = expectedAttemptRevision;
        TimeoutMilliseconds = timeoutMilliseconds;
        AttemptStore = attemptStore ?? throw new ArgumentNullException(nameof(attemptStore));
        BoundaryFactory = boundaryFactory ?? throw new ArgumentNullException(nameof(boundaryFactory));
    }

    internal WebAutomationRequestIdentity Identity { get; }
    internal BaiduCandidateSessionBinding CandidateBinding { get; }
    internal BaiduExecutionContext ExecutionContext { get; }
    internal ManagedChromiumSessionHandle ExactHandle { get; }
    internal ManagedBrowserLifecycleSession LifecycleSession { get; }
    internal ManagedBrowserSessionReference ExactManagedSession { get; }
    internal string CanonicalSourceUri { get; }
    internal ManagedBrowserSourceSnapshot ImmutableSource { get; }
    internal ManagedPreparedDestinationEvidence PreparedDestination { get; }
    internal DuplicatePolicy DuplicatePolicy { get; }
    internal long ExpectedAttemptRevision { get; }
    internal int TimeoutMilliseconds { get; }
    internal IWebAutomationAttemptStore AttemptStore { get; }
    internal IManagedTransferFinalSubmissionBoundaries Boundaries => boundaries ??
        throw new InvalidOperationException("A boundary scope has not been created.");
    internal IManagedTransferFinalSubmissionBoundaryFactory? BoundaryFactory { get; }

    internal ManagedTransferFinalSubmissionRequest CreateBoundaryScope()
    {
        if (BoundaryFactory is null) return this;
        var scoped = BoundaryFactory.CreateScope(this) ??
            throw new InvalidOperationException("The final-submission boundary factory returned no scope.");
        return new ManagedTransferFinalSubmissionRequest(
            Identity, CandidateBinding, ExecutionContext, ExactHandle, LifecycleSession,
            ExactManagedSession, CanonicalSourceUri, ImmutableSource, PreparedDestination,
            DuplicatePolicy, ExpectedAttemptRevision, TimeoutMilliseconds, AttemptStore, scoped);
    }
    public override string ToString() =>
        "ManagedTransferFinalSubmissionRequest { Identity = [REDACTED], Evidence = [REDACTED] }";
}

internal enum ManagedTransferFinalSubmissionReconciliationStatus
{
    Released = 1,
    RetainedCancelled,
    RetainedTimedOut,
    RetainedInconclusive,
    AlreadyInProgress,
    AlreadyReleased,
}

[Flags]
internal enum ManagedTransferFinalSubmissionOperationRisk
{
    None = 0,
    ReadOnlyPreBoundary = 1,
    DurableMutation = 2,
    PossibleDispatch = 4,
}

internal readonly record struct ManagedTransferFinalSubmissionOperationSummary(
    ManagedTransferFinalSubmissionOperationRisk Failed,
    ManagedTransferFinalSubmissionOperationRisk Cancelled)
{
    internal ManagedTransferFinalSubmissionOperationRisk Unreconciled(
        ManagedTransferFinalSubmissionOperationRisk configuredReconciler) =>
        (Failed | Cancelled) & ~configuredReconciler;
}

internal sealed class ManagedTransferFinalSubmissionOperationTracker
{
    private readonly object sync = new();
    private readonly Dictionary<Task, ManagedTransferFinalSubmissionOperationRisk> active = [];
    private int failedRiskMask;
    private int cancelledRiskMask;

    internal Task<T> Track<T>(Task<T> task,
        ManagedTransferFinalSubmissionOperationRisk risk)
    {
        ArgumentNullException.ThrowIfNull(task);
        TrackCore(task, ValidateRisk(risk));
        return task;
    }

    internal Task Track(Task task, ManagedTransferFinalSubmissionOperationRisk risk)
    {
        ArgumentNullException.ThrowIfNull(task);
        TrackCore(task, ValidateRisk(risk));
        return task;
    }

    internal async Task<ManagedTransferFinalSubmissionOperationSummary> DrainAsync()
    {
        while (true)
        {
            Task[] snapshot;
            lock (sync) snapshot = active.Keys.ToArray();
            if (snapshot.Length == 0)
                return new((ManagedTransferFinalSubmissionOperationRisk)
                        Volatile.Read(ref failedRiskMask),
                    (ManagedTransferFinalSubmissionOperationRisk)
                        Volatile.Read(ref cancelledRiskMask));
            await Task.WhenAll(snapshot.Select(SettleAsync)).ConfigureAwait(false);
        }
    }

    private void TrackCore(Task task, ManagedTransferFinalSubmissionOperationRisk risk)
    {
        if (task.IsCompleted)
        {
            RecordOutcome(task, risk);
            return;
        }
        lock (sync) active.Add(task, risk);
        _ = task.ContinueWith(completed =>
        {
            RecordOutcome(completed, risk);
            lock (sync) active.Remove(completed);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void RecordOutcome(Task task, ManagedTransferFinalSubmissionOperationRisk risk)
    {
        if (task.IsCanceled)
            Interlocked.Or(ref cancelledRiskMask, (int)risk);
        else if (task.IsFaulted)
            Interlocked.Or(ref failedRiskMask, (int)risk);
    }

    private static ManagedTransferFinalSubmissionOperationRisk ValidateRisk(
        ManagedTransferFinalSubmissionOperationRisk risk) =>
        risk is ManagedTransferFinalSubmissionOperationRisk.ReadOnlyPreBoundary or
            ManagedTransferFinalSubmissionOperationRisk.DurableMutation or
            ManagedTransferFinalSubmissionOperationRisk.PossibleDispatch
            ? risk
            : throw new ArgumentOutOfRangeException(nameof(risk));

    private static async Task SettleAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception) { }
    }
}

internal sealed class ManagedTransferFinalSubmissionQuarantine
{
    private readonly object sync = new();
    private readonly ManagedTransferFinalSubmissionOperationTracker tracker;
    private readonly Func<int, CancellationToken, Task<bool>> reconcile;
    private readonly Func<int, CancellationToken, Task<bool>>? reconcileAfterPermit;
    private readonly ManagedTransferFinalSubmissionOperationRisk reconciledRisks;
    private readonly int cleanupTimeoutMilliseconds;
    private IDisposable? lease;
    private IBaiduCandidateMutationPermit? permit;
    private Task<ManagedTransferFinalSubmissionReconciliationStatus>? reconciliationTask;
    private int activeReconciliations;
    private int maximumConcurrentReconciliations;
    private int state;

    internal ManagedTransferFinalSubmissionQuarantine(
        IDisposable lease,
        Func<int, CancellationToken, Task<bool>> reconcile,
        ManagedTransferFinalSubmissionOperationRisk reconciledRisks,
        ManagedTransferFinalSubmissionOperationTracker? tracker = null,
        Task? settlement = null,
        IBaiduCandidateMutationPermit? permit = null,
        Func<int, CancellationToken, Task<bool>>? reconcileAfterPermit = null,
        int cleanupTimeoutMilliseconds = ManagedBrowserProtocol.MaximumOperationTimeoutMilliseconds)
    {
        this.lease = lease ?? throw new ArgumentNullException(nameof(lease));
        this.reconcile = reconcile ?? throw new ArgumentNullException(nameof(reconcile));
        this.reconciledRisks = reconciledRisks;
        this.tracker = tracker ?? new ManagedTransferFinalSubmissionOperationTracker();
        _ = settlement; // The caller registers it with Track(task, risk) before handoff.
        this.permit = permit;
        this.reconcileAfterPermit = reconcileAfterPermit;
        this.cleanupTimeoutMilliseconds = cleanupTimeoutMilliseconds;
    }

    internal int MaximumConcurrentReconciliations =>
        Volatile.Read(ref maximumConcurrentReconciliations);

    internal async Task<ManagedTransferFinalSubmissionReconciliationStatus> ReconcileAsync(
        int timeoutMilliseconds,
        CancellationToken cancellationToken = default)
    {
        if (timeoutMilliseconds is < 1 or > ManagedBrowserProtocol.MaximumOperationTimeoutMilliseconds)
            return ManagedTransferFinalSubmissionReconciliationStatus.RetainedInconclusive;
        Task<ManagedTransferFinalSubmissionReconciliationStatus> ownedReconciliation;
        lock (sync)
        {
            if (state == 2)
                return ManagedTransferFinalSubmissionReconciliationStatus.AlreadyReleased;
            if (reconciliationTask is null || reconciliationTask.IsCompleted && state == 0)
            {
                state = 1;
                reconciliationTask = ReconcileCoreAsync();
            }
            ownedReconciliation = reconciliationTask;
        }
        try
        {
            using var deadline = new ManagedTransferOperationDeadline(
                timeoutMilliseconds, cancellationToken);
            return await ownedReconciliation.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return cancellationToken.IsCancellationRequested
                ? ManagedTransferFinalSubmissionReconciliationStatus.RetainedCancelled
                : ManagedTransferFinalSubmissionReconciliationStatus.RetainedTimedOut;
        }
        catch (TimeoutException)
        {
            return ManagedTransferFinalSubmissionReconciliationStatus.RetainedTimedOut;
        }
        catch (Exception)
        {
            return ManagedTransferFinalSubmissionReconciliationStatus.RetainedInconclusive;
        }
    }

    private async Task<ManagedTransferFinalSubmissionReconciliationStatus> ReconcileCoreAsync()
    {
        var active = Interlocked.Increment(ref activeReconciliations);
        UpdateMaximum(active);
        var released = false;
        try
        {
            var settled = await tracker.DrainAsync().ConfigureAwait(false);
            if (settled.Unreconciled(reconciledRisks) !=
                ManagedTransferFinalSubmissionOperationRisk.None)
                return ManagedTransferFinalSubmissionReconciliationStatus.RetainedInconclusive;
            var beforePermit = tracker.Track(reconcile(
                    cleanupTimeoutMilliseconds, CancellationToken.None),
                ManagedTransferFinalSubmissionOperationRisk.ReadOnlyPreBoundary);
            if (!await beforePermit.ConfigureAwait(false))
                return ManagedTransferFinalSubmissionReconciliationStatus.RetainedInconclusive;
            var ownedPermit = permit;
            if (ownedPermit is not null)
            {
                var disposal = tracker.Track(ownedPermit.DisposeAsync().AsTask(),
                    ManagedTransferFinalSubmissionOperationRisk.ReadOnlyPreBoundary);
                await disposal.ConfigureAwait(false);
                permit = null;
            }
            if (reconcileAfterPermit is not null)
            {
                var afterPermit = tracker.Track(reconcileAfterPermit(
                        cleanupTimeoutMilliseconds, CancellationToken.None),
                    ManagedTransferFinalSubmissionOperationRisk.DurableMutation);
                if (!await afterPermit.ConfigureAwait(false))
                    return ManagedTransferFinalSubmissionReconciliationStatus.RetainedInconclusive;
            }
            var owned = Interlocked.Exchange(ref lease, null);
            owned?.Dispose();
            Volatile.Write(ref state, 2);
            released = true;
            return ManagedTransferFinalSubmissionReconciliationStatus.Released;
        }
        catch (Exception)
        {
            return ManagedTransferFinalSubmissionReconciliationStatus.RetainedInconclusive;
        }
        finally
        {
            Interlocked.Decrement(ref activeReconciliations);
            if (!released) Volatile.Write(ref state, 0);
        }
    }

    private void UpdateMaximum(int candidate)
    {
        while (true)
        {
            var current = Volatile.Read(ref maximumConcurrentReconciliations);
            if (candidate <= current ||
                Interlocked.CompareExchange(ref maximumConcurrentReconciliations,
                    candidate, current) == current) return;
        }
    }

    public override string ToString() =>
        $"ManagedTransferFinalSubmissionQuarantine {{ State = {(Volatile.Read(ref state) == 2 ? "Released" : "Held")}, Evidence = [REDACTED] }}";
}

/// <summary>
/// Offline-only, one-account-lease final submission runner. It has deliberately no production DI/UI wiring.
/// Every helper boundary is fixed and injected from the already-reviewed managed transfer primitives.
/// </summary>
internal sealed class ManagedTransferFinalSubmissionSession
{
    private const ManagedTransferFinalSubmissionOperationRisk ReadRisk =
        ManagedTransferFinalSubmissionOperationRisk.ReadOnlyPreBoundary;
    private const ManagedTransferFinalSubmissionOperationRisk StoreRisk =
        ManagedTransferFinalSubmissionOperationRisk.DurableMutation;
    private const ManagedTransferFinalSubmissionOperationRisk DispatchRisk =
        ManagedTransferFinalSubmissionOperationRisk.PossibleDispatch;
    private readonly TimeProvider timeProvider;

    internal ManagedTransferFinalSubmissionSession(TimeProvider? timeProvider = null) =>
        this.timeProvider = timeProvider ?? TimeProvider.System;

    internal async Task<ManagedTransferFinalSubmissionResult> RunAsync(
        ManagedTransferFinalSubmissionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = request.CreateBoundaryScope();
        var tracker = new ManagedTransferFinalSubmissionOperationTracker();
        using var deadline = new ManagedTransferOperationDeadline(
            request.TimeoutMilliseconds, cancellationToken, timeProvider);
        WebAutomationAttemptRecord? attempt;
        try
        {
            deadline.RequireBudget();
            attempt = await tracker.Track(request.AttemptStore.GetAsync(
                request.Identity.OperationId, request.Identity.AccountId,
                request.Identity.Generation, deadline.Token), ReadRisk)
                .WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new(cancellationToken.IsCancellationRequested
                ? ManagedTransferFinalSubmissionResultKind.Cancelled
                : ManagedTransferFinalSubmissionResultKind.TimedOut);
        }
        catch (Exception)
        {
            return NotEligible();
        }
        if (!IsEligible(request, attempt, timeProvider.GetUtcNow())) return NotEligible();

        IDisposable? lease = null;
        IBaiduCandidateMutationPermit? finalPermit = null;
        try
        {
            deadline.RequireBudget();
            var leaseTask = tracker.Track(request.Boundaries.AcquireAccountLeaseAsync(
                request.Identity.AccountId, deadline.Token), ReadRisk);
            try
            {
                lease = await leaseTask.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or TimeoutException)
            {
                _ = DisposeLateLeaseAsync(leaseTask);
                throw;
            }
            deadline.RequireBudget();
            var afterLeaseTask = tracker.Track(request.AttemptStore.GetAsync(
                request.Identity.OperationId, request.Identity.AccountId,
                request.Identity.Generation, deadline.Token), ReadRisk);
            WebAutomationAttemptRecord? afterLease;
            try
            {
                afterLease = await afterLeaseTask.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or TimeoutException)
            {
                return Pending(lease, afterLeaseTask, (timeout, token) =>
                    ReconcilePreBoundaryAsync(request, attempt!, tracker, timeout, token,
                        ManagedTransferFinalSubmissionProofRoute.OriginalSource),
                    ReadRisk);
            }
            if (!IsEligible(request, afterLease, timeProvider.GetUtcNow()))
                return Finish(NotEligible());
            attempt = afterLease;
            if (!await ProveAsync(request, ManagedTransferFinalSubmissionProofRoute.OriginalSource,
                    deadline, tracker).ConfigureAwait(false))
                return Finish(NotEligible());
            var pickerTask = tracker.Track(
                request.Boundaries.OpenPickerAsync(request, deadline.Token), ReadRisk);
            ManagedTransferPickerOpenResult picker;
            try
            {
                picker = await pickerTask.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or TimeoutException)
            {
                return Pending(lease, pickerTask, (timeout, token) =>
                    ReconcilePreBoundaryAsync(request, attempt!, tracker, timeout, token),
                    ReadRisk);
            }
            if (picker.Kind != ManagedBrowserTransferPickerResultKind.PickerOpened)
                return picker.Kind == ManagedBrowserTransferPickerResultKind.OutcomeUnknown
                    ? Pending(lease)
                    : Finish(MapPicker(picker.Kind));
            foreach (var exactId in request.PreparedDestination.FolderCandidatesSnapshot
                         .AncestorExactIds.Skip(1))
            {
                deadline.RequireBudget();
                var enterTask = tracker.Track(
                    request.Boundaries.EnterDirectoryForSelectionAsync(
                        request, exactId, deadline.Token), ReadRisk);
                ManagedTransferDirectoryMutationExecutionResult entered;
                try
                {
                    entered = await enterTask.WaitAsync(deadline.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is OperationCanceledException or TimeoutException)
                {
                    return Pending(lease, enterTask, (timeout, token) =>
                        ReconcilePreBoundaryAsync(request, attempt!, tracker, timeout, token),
                        ReadRisk);
                }
                if (entered.Outcome != ManagedTransferDirectoryMutationOutcome.DirectoryEntered)
                    return entered.Outcome is ManagedTransferDirectoryMutationOutcome.OutcomeUnknown or
                        ManagedTransferDirectoryMutationOutcome.QuarantineRequired
                        ? Pending(lease)
                        : Finish(MapDirectory(entered.Outcome));
            }

            var context = BuildContext(request, attempt!.TargetPreparationEvidence!,
                DeterministicHelperTimeout(request.TimeoutMilliseconds));
            var readinessTask = tracker.Track(
                request.Boundaries.ObserveReadinessAsync(context, deadline.Token), ReadRisk);
            ManagedTransferReadinessResult ready;
            try
            {
                ready = await readinessTask.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or TimeoutException)
            {
                return Pending(lease, readinessTask, (timeout, token) =>
                    ReconcilePreBoundaryAsync(request, attempt!, tracker, timeout, token),
                    ReadRisk);
            }
            if (ready.Kind != ManagedBrowserTransferSubmissionReadinessResultKind.Ready ||
                ready.Readiness is null || !ready.Readiness.Context.EqualsEvidence(context))
                return Finish(NotEligible());
            var permitTask = tracker.Track(request.Boundaries
                .AcquireFinalPermitAsync(request, deadline.Token).AsTask(), ReadRisk);
            try
            {
                finalPermit = await permitTask.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or TimeoutException)
            {
                var latePermitDisposal = tracker.Track(
                    DisposeLatePermitAsync(permitTask), ReadRisk);
                return Pending(lease, latePermitDisposal, async (timeout, token) =>
                    await latePermitDisposal.ConfigureAwait(false) &&
                    await ReconcilePreBoundaryAsync(
                        request, attempt!, tracker, timeout, token).ConfigureAwait(false),
                    ReadRisk);
            }
            if (finalPermit is null) return Finish(NotEligible());

            var submittingAt = timeProvider.GetUtcNow();
            var submission = TransferSubmissionEvidence.CreateBound(
                Guid.NewGuid(), ready.Readiness.ReadinessFingerprint,
                attempt.SourceEvidence!.Items.Fingerprint,
                attempt.TargetPreparationEvidence!.TargetExactId,
                attempt.TargetPreparationEvidence.Baseline.Fingerprint,
                attempt.SourceEvidence.BaiduUserId,
                attempt.SourceEvidence.Capability,
                submittingAt);
            var persistSubmissionTask = tracker.Track(
                request.AttemptStore.TryPersistTransferSubmissionAsync(
                    request.Identity.OperationId, request.Identity.AccountId,
                    request.Identity.Generation, request.ExpectedAttemptRevision,
                    submission, submittingAt, deadline.Token), StoreRisk);
            bool persisted;
            try
            {
                persisted = await persistSubmissionTask.WaitAsync(deadline.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or TimeoutException)
            {
                return PendingAfterBoundary(lease, persistSubmissionTask,
                    (timeout, token) => ReconcileAmbiguousSubmissionPersistenceAsync(
                        request, attempt!, submission, persistSubmissionTask, timeout, token, tracker),
                    (timeout, token) => ResolveAmbiguousSubmissionAfterPermitAsync(
                        request, attempt!, submission, persistSubmissionTask, timeout, token, tracker),
                    ReadRisk | StoreRisk);
            }
            catch (Exception)
            {
                return PendingAfterBoundary(lease, persistSubmissionTask,
                    (timeout, token) => ReconcileAmbiguousSubmissionPersistenceAsync(
                        request, attempt!, submission, persistSubmissionTask, timeout, token, tracker),
                    (timeout, token) => ResolveAmbiguousSubmissionAfterPermitAsync(
                        request, attempt!, submission, persistSubmissionTask, timeout, token, tracker),
                    ReadRisk | StoreRisk);
            }
            if (!persisted)
            {
                WebAutomationAttemptRecord? first;
                WebAutomationAttemptRecord? second;
                try
                {
                    first = await tracker.Track(request.AttemptStore.GetAsync(
                            request.Identity.OperationId, request.Identity.AccountId,
                            request.Identity.Generation, deadline.Token), ReadRisk)
                        .WaitAsync(deadline.Token).ConfigureAwait(false);
                    second = await tracker.Track(request.AttemptStore.GetAsync(
                            request.Identity.OperationId, request.Identity.AccountId,
                            request.Identity.Generation, deadline.Token), ReadRisk)
                        .WaitAsync(deadline.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is OperationCanceledException or TimeoutException)
                {
                    return PendingAfterBoundary(lease, reconcile:
                        (timeout, token) => ReconcileRejectedSubmissionPersistenceAsync(
                            request, attempt!, timeout, token, tracker));
                }
                catch (Exception)
                {
                    return PendingAfterBoundary(lease);
                }
                if (!SameDurableAttempt(first, second) || !SameDurableAttempt(first, attempt) ||
                    !await ProveAsync(request,
                        ManagedTransferFinalSubmissionProofRoute.DestinationPicker,
                        deadline, tracker).ConfigureAwait(false))
                    return PendingAfterBoundary(lease);
                try
                {
                    await tracker.Track(finalPermit.DisposeAsync().AsTask(), ReadRisk)
                        .ConfigureAwait(false);
                }
                catch (Exception)
                {
                    return PendingAfterBoundary(lease);
                }
                finalPermit = null;
                return Finish(NotEligible());
            }

            var boundTask = tracker.Track(request.AttemptStore.GetAsync(
                request.Identity.OperationId, request.Identity.AccountId,
                request.Identity.Generation, deadline.Token), ReadRisk);
            WebAutomationAttemptRecord? bound;
            try
            {
                bound = await boundTask.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or TimeoutException)
            {
                return PendingAfterBoundary(lease, boundTask,
                    (timeout, token) => PrepareKnownNotDispatchedAsync(
                        request, attempt!, submission, timeout, token, tracker),
                    (timeout, token) => ResolveKnownNotDispatchedAfterPermitAsync(
                        request, attempt!, submission, timeout, token, tracker));
            }
            ManagedTransferPermitOwnershipResult ownership;
            var ownershipTask = tracker.Track(request.Boundaries.ObservePermitOwnershipAsync(
                request, finalPermit, deadline.Token), ReadRisk);
            try
            {
                ownership = await ownershipTask.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or TimeoutException)
            {
                return PendingAfterBoundary(lease, ownershipTask,
                    (timeout, token) => PrepareKnownNotDispatchedAsync(
                        request, attempt!, submission, timeout, token, tracker),
                    (timeout, token) => ResolveKnownNotDispatchedAfterPermitAsync(
                        request, attempt!, submission, timeout, token, tracker));
            }
            if (!ExactBoundAttempt(request, attempt, bound, submission) ||
                !CandidateStillValid(request, timeProvider.GetUtcNow()) ||
                !ExactPermitOwnership(request, ownership) ||
                !await ProveAsync(request, ManagedTransferFinalSubmissionProofRoute.DestinationPicker,
                    deadline, tracker).ConfigureAwait(false))
                return PendingAfterBoundary(lease);

            WebAutomationAttemptRecord? finalBound;
            var finalBoundTask = tracker.Track(request.AttemptStore.GetAsync(
                request.Identity.OperationId, request.Identity.AccountId,
                request.Identity.Generation, deadline.Token), ReadRisk);
            try
            {
                finalBound = await finalBoundTask.WaitAsync(deadline.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or TimeoutException)
            {
                return PendingAfterBoundary(lease, finalBoundTask,
                    (timeout, token) => PrepareKnownNotDispatchedAsync(
                        request, attempt!, submission, timeout, token, tracker),
                    (timeout, token) => ResolveKnownNotDispatchedAfterPermitAsync(
                        request, attempt!, submission, timeout, token, tracker));
            }
            catch (Exception)
            {
                return PendingAfterBoundary(lease, finalBoundTask);
            }

            ManagedTransferPermitOwnershipResult finalOwnership;
            var finalOwnershipTask = tracker.Track(request.Boundaries.ObservePermitOwnershipAsync(
                request, finalPermit, deadline.Token), ReadRisk);
            try
            {
                finalOwnership = await finalOwnershipTask.WaitAsync(deadline.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or TimeoutException)
            {
                return PendingAfterBoundary(lease, finalOwnershipTask,
                    (timeout, token) => PrepareKnownNotDispatchedAsync(
                        request, attempt!, submission, timeout, token, tracker),
                    (timeout, token) => ResolveKnownNotDispatchedAfterPermitAsync(
                        request, attempt!, submission, timeout, token, tracker));
            }
            catch (Exception)
            {
                return PendingAfterBoundary(lease, finalOwnershipTask);
            }

            if (!SameDurableAttempt(bound, finalBound) ||
                !ExactBoundAttempt(request, attempt, finalBound, submission) ||
                !CandidateStillValid(request, timeProvider.GetUtcNow()) ||
                !ExactPermitOwnership(request, finalOwnership))
                return PendingAfterBoundary(lease);

            var submitTask = tracker.Track(
                request.Boundaries.SubmitOnceAsync(ready.Readiness, deadline.Token), DispatchRisk);
            ManagedTransferSubmitResult submitted;
            try
            {
                submitted = await submitTask.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or TimeoutException)
            {
                return PendingAfterBoundary(lease, submitTask,
                    (timeout, token) => ReconcileHeldLeaseAsync(
                        request, attempt!, submission, ready.Readiness, timeout, token, tracker),
                    reconciledRisks: ReadRisk | StoreRisk | DispatchRisk);
            }
            catch (Exception)
            {
                return PendingAfterBoundary(lease, submitTask,
                    (timeout, token) => ReconcileHeldLeaseAsync(
                        request, attempt!, submission, ready.Readiness, timeout, token, tracker),
                    reconciledRisks: ReadRisk | StoreRisk | DispatchRisk);
            }
            ManagedTransferProcessExitObservation processExit;
            var processExitTask = tracker.Track(
                request.Boundaries.ObserveProcessExitAsync(request, deadline.Token), ReadRisk);
            try
            {
                processExit = await processExitTask.WaitAsync(deadline.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                return PendingAfterBoundary(lease, processExitTask, reconcile:
                    (timeout, token) => ReconcileHeldLeaseAsync(
                        request, attempt!, submission, ready.Readiness, timeout, token, tracker),
                    reconciledRisks: ReadRisk | StoreRisk | DispatchRisk);
            }
            if (!ExactProcessExit(request, processExit))
                return PendingAfterBoundary(lease, reconcile:
                    (timeout, token) => ReconcileHeldLeaseAsync(
                        request, attempt!, submission, ready.Readiness, timeout, token, tracker));
            if (submitted.DispatchState == ManagedBrowserTransferSubmitDispatchState.KnownNotDispatched)
            {
                WebAutomationAttemptRecord? negativeOne;
                WebAutomationAttemptRecord? negativeTwo;
                try
                {
                    negativeOne = await tracker.Track(request.AttemptStore.GetAsync(
                            request.Identity.OperationId, request.Identity.AccountId,
                            request.Identity.Generation, deadline.Token), ReadRisk)
                        .WaitAsync(deadline.Token).ConfigureAwait(false);
                    negativeTwo = await tracker.Track(request.AttemptStore.GetAsync(
                            request.Identity.OperationId, request.Identity.AccountId,
                            request.Identity.Generation, deadline.Token), ReadRisk)
                        .WaitAsync(deadline.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is OperationCanceledException or TimeoutException)
                {
                    return PendingAfterBoundary(lease,
                        reconcile: (timeout, token) => PrepareKnownNotDispatchedAsync(
                            request, attempt!, submission, timeout, token, tracker),
                        reconcileAfterPermit: (timeout, token) =>
                            ResolveKnownNotDispatchedAfterPermitAsync(
                                request, attempt!, submission, timeout, token, tracker));
                }
                if (!SameDurableAttempt(negativeOne, negativeTwo) ||
                    !ExactBoundAttempt(request, attempt, negativeOne, submission) ||
                    !CandidateStillValid(request, timeProvider.GetUtcNow()) ||
                    !await ProveAsync(request,
                        ManagedTransferFinalSubmissionProofRoute.OriginalSource,
                        deadline, tracker).ConfigureAwait(false) ||
                    !await ProveAsync(request,
                        ManagedTransferFinalSubmissionProofRoute.DestinationPicker,
                        deadline, tracker).ConfigureAwait(false))
                    return PendingAfterBoundary(lease);
                try
                {
                    await tracker.Track(finalPermit.DisposeAsync().AsTask(), ReadRisk)
                        .ConfigureAwait(false);
                }
                catch (Exception)
                {
                    return PendingAfterBoundary(lease,
                        reconcile: (timeout, token) => PrepareKnownNotDispatchedAsync(
                            request, attempt!, submission, timeout, token, tracker),
                        reconcileAfterPermit: (timeout, token) =>
                            ResolveKnownNotDispatchedAfterPermitAsync(
                                request, attempt!, submission, timeout, token, tracker),
                        reconciledRisks: StoreRisk | DispatchRisk);
                }
                finalPermit = null;
                var resolveTask = tracker.Track(
                    request.AttemptStore.TryResolveTransferKnownNotDispatchedAsync(
                        request.Identity.OperationId, request.Identity.AccountId,
                        request.Identity.Generation, negativeOne!.Revision,
                        submission.SubmissionId!.Value, submission.ReadinessFingerprint!,
                        timeProvider.GetUtcNow(), deadline.Token), StoreRisk);
                bool resolved;
                try
                {
                    resolved = await resolveTask.WaitAsync(deadline.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is OperationCanceledException or TimeoutException)
                {
                    return Pending(lease, resolveTask, async (timeout, token) =>
                    {
                        bool completed;
                        try { completed = await resolveTask.ConfigureAwait(false); }
                        catch (Exception) { return false; }
                        return completed
                            ? await ReconcileTerminalHeldLeaseAsync(
                                request, WebAutomationAttemptState.DefinitelyNotSubmitted,
                                timeout, token, tracker).ConfigureAwait(false)
                            : await ResolveKnownNotDispatchedAfterPermitAsync(
                                request, attempt!, submission, timeout, token, tracker)
                                .ConfigureAwait(false);
                    });
                }
                if (!resolved) return Pending(lease, reconcile:
                    (timeout, token) => ResolveKnownNotDispatchedAfterPermitAsync(
                        request, attempt!, submission, timeout, token, tracker));
                lease.Dispose();
                lease = null;
                return new(ManagedTransferFinalSubmissionResultKind.DefinitelyNotDispatched);
            }
            if (submitted.DispatchState != ManagedBrowserTransferSubmitDispatchState.ClickDispatched)
            {
                var pendingStoreTask = tracker.Track(request.AttemptStore.TryAdvanceAsync(
                    request.Identity.OperationId, request.Identity.AccountId,
                    request.Identity.Generation, WebAutomationAttemptState.Submitting,
                    bound!.Revision, WebAutomationAttemptState.ResultPending,
                    observationFingerprint: null, isWaitingForUser: false,
                    timeProvider.GetUtcNow(), deadline.Token), StoreRisk);
                try
                {
                    _ = await pendingStoreTask.WaitAsync(deadline.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is OperationCanceledException or TimeoutException)
                {
                    return PendingAfterBoundary(lease, pendingStoreTask, (timeout, token) =>
                        ReconcileHeldLeaseAsync(
                            request, attempt!, submission, ready.Readiness, timeout, token, tracker));
                }
                return PendingAfterBoundary(lease, reconcile:
                    (timeout, token) => ReconcileHeldLeaseAsync(
                        request, attempt!, submission, ready.Readiness, timeout, token, tracker));
            }

            Task<ManagedTransferOfficialOutcome>? officialTask = null;
            ManagedTransferOfficialOutcome official;
            try
            {
                officialTask = tracker.Track(request.Boundaries.ObserveOutcomeAsync(
                    ready.Readiness, deadline.Token), ReadRisk);
                official = await officialTask.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return PendingAfterBoundary(lease, officialTask, (timeout, token) =>
                    ReconcileHeldLeaseAsync(
                        request, attempt!, submission, ready.Readiness, timeout, token, tracker));
            }
            if (official is not { Kind: ManagedBrowserTransferOutcomeKind.AcceptedBound,
                    OfficialOutcome: { } officialEvidence } ||
                !ExactOfficial(request, attempt, officialEvidence))
            {
                return PendingAfterBoundary(lease, reconcile:
                    (timeout, token) => ReconcileHeldLeaseAsync(
                        request, attempt!, submission, ready.Readiness, timeout, token, tracker));
            }
            var mapping = new TransferIdentityMapping([
                new TransferIdentityMappingEntry(officialEvidence.SourceExactId!,
                    officialEvidence.DestinationExactId!),
            ]);
            var officialPersistenceTask = tracker.Track(
                request.AttemptStore.TryPersistTransferAcceptanceAsync(
                    request.Identity.OperationId, request.Identity.AccountId,
                    request.Identity.Generation, bound!.Revision,
                    officialEvidence.OfficialRequestId!, mapping,
                    officialEvidence.OfficialObservationFingerprint!, timeProvider.GetUtcNow(),
                    deadline.Token), StoreRisk);
            bool officialPersisted;
            try
            {
                officialPersisted = await officialPersistenceTask.WaitAsync(deadline.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                return PendingAfterBoundary(lease, officialPersistenceTask,
                    (timeout, token) => ReconcileHeldLeaseAsync(
                        request, attempt!, submission, ready.Readiness, timeout, token, tracker));
            }
            if (!officialPersisted) return PendingAfterBoundary(lease, reconcile:
                (timeout, token) => ReconcileHeldLeaseAsync(
                    request, attempt!, submission, ready.Readiness, timeout, token, tracker));

            var target = await ReadExactTargetRouteAsync(request, deadline, tracker)
                .ConfigureAwait(false);
            if (!ExactTargetRead(request, attempt, mapping, target)) return PendingAfterBoundary(lease,
                reconcile: (timeout, token) => ReconcileHeldLeaseAsync(
                    request, attempt!, submission, ready.Readiness, timeout, token, tracker));
            var confirmation = TransferTargetConfirmationEvidence.Create(
                submission, officialEvidence.OfficialRequestId!, mapping,
                attempt.TargetPreparationEvidence,
                target!.DurableEvidence.Baseline,
                target.WireSnapshot.ObservationFingerprint,
                timeProvider.GetUtcNow());
            var confirmationPersistenceTask = tracker.Track(
                request.AttemptStore.TryPersistTransferTargetConfirmationAsync(
                    request.Identity.OperationId, request.Identity.AccountId,
                    request.Identity.Generation, bound.Revision + 1, confirmation, deadline.Token),
                StoreRisk);
            bool confirmed;
            try
            {
                confirmed = await confirmationPersistenceTask.WaitAsync(deadline.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                return PendingAfterBoundary(lease, confirmationPersistenceTask,
                    (timeout, token) => ReconcileHeldLeaseAsync(
                        request, attempt!, submission, ready.Readiness, timeout, token, tracker));
            }
            if (!confirmed) return PendingAfterBoundary(lease, reconcile:
                (timeout, token) => ReconcileHeldLeaseAsync(
                    request, attempt!, submission, ready.Readiness, timeout, token, tracker));

            WebAutomationAttemptRecord? finalOne;
            WebAutomationAttemptRecord? finalTwo;
            try
            {
                finalOne = await tracker.Track(request.AttemptStore.GetAsync(
                        request.Identity.OperationId, request.Identity.AccountId,
                        request.Identity.Generation, deadline.Token), ReadRisk)
                    .WaitAsync(deadline.Token).ConfigureAwait(false);
                finalTwo = await tracker.Track(request.AttemptStore.GetAsync(
                        request.Identity.OperationId, request.Identity.AccountId,
                        request.Identity.Generation, deadline.Token), ReadRisk)
                    .WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or TimeoutException)
            {
                return PendingAfterBoundary(lease, reconcile:
                    (timeout, token) => ReconcileHeldLeaseAsync(
                        request, attempt!, submission, ready.Readiness, timeout, token, tracker));
            }
            if (!SameFinal(finalOne, finalTwo, submission, confirmation) ||
                !await ProveAsync(request,
                    ManagedTransferFinalSubmissionProofRoute.ExactTargetDirectory,
                    deadline, tracker).ConfigureAwait(false))
                return PendingAfterBoundary(lease, reconcile:
                    (timeout, token) => ReconcileHeldLeaseAsync(
                        request, attempt!, submission, ready.Readiness, timeout, token, tracker));
            try
            {
                await tracker.Track(finalPermit.DisposeAsync().AsTask(), ReadRisk)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                return PendingAfterBoundary(lease, reconcile:
                    (timeout, token) => ReconcileHeldLeaseAsync(
                        request, attempt!, submission, ready.Readiness, timeout, token, tracker),
                    reconciledRisks: StoreRisk | DispatchRisk);
            }
            finalPermit = null;
            lease.Dispose();
            lease = null;
            return new(ManagedTransferFinalSubmissionResultKind.ExactSuccess,
                officialEvidence.DestinationExactId);
        }
        catch (OperationCanceledException)
        {
            return lease is null
                ? new(cancellationToken.IsCancellationRequested
                    ? ManagedTransferFinalSubmissionResultKind.Cancelled
                    : ManagedTransferFinalSubmissionResultKind.TimedOut)
                : finalPermit is null ? Pending(lease) : PendingAfterBoundary(lease);
        }
        catch (TimeoutException)
        {
            return lease is null
                ? new(ManagedTransferFinalSubmissionResultKind.TimedOut)
                : finalPermit is null ? Pending(lease) : PendingAfterBoundary(lease);
        }
        catch (Exception)
        {
            return lease is null ? NotEligible() : finalPermit is null
                ? Pending(lease)
                : PendingAfterBoundary(lease);
        }
        finally
        {
            if (finalPermit is not null)
            {
                try
                {
                    await tracker.Track(finalPermit.DisposeAsync().AsTask(), ReadRisk)
                        .ConfigureAwait(false);
                }
                catch (Exception) { }
            }
            lease?.Dispose();
        }

        ManagedTransferFinalSubmissionResult Finish(ManagedTransferFinalSubmissionResult result)
        {
            lease?.Dispose();
            lease = null;
            return result;
        }

        ManagedTransferFinalSubmissionResult Pending(
            IDisposable ownedLease,
            Task? settlement = null,
            Func<int, CancellationToken, Task<bool>>? reconcile = null,
            ManagedTransferFinalSubmissionOperationRisk reconciledRisks =
                ReadRisk | StoreRisk)
        {
            var quarantine = new ManagedTransferFinalSubmissionQuarantine(
                ownedLease, reconcile ?? ((_, _) => Task.FromResult(false)), reconciledRisks,
                tracker, settlement, cleanupTimeoutMilliseconds: request.TimeoutMilliseconds);
            lease = null;
            return new(ManagedTransferFinalSubmissionResultKind.ResultPending,
                quarantine: quarantine);
        }

        ManagedTransferFinalSubmissionResult PendingAfterBoundary(
            IDisposable ownedLease,
            Task? settlement = null,
            Func<int, CancellationToken, Task<bool>>? reconcile = null,
            Func<int, CancellationToken, Task<bool>>? reconcileAfterPermit = null,
            ManagedTransferFinalSubmissionOperationRisk reconciledRisks =
                ReadRisk | StoreRisk | DispatchRisk)
        {
            var quarantine = new ManagedTransferFinalSubmissionQuarantine(
                ownedLease, reconcile ?? ((_, _) => Task.FromResult(false)), reconciledRisks, tracker,
                settlement, finalPermit, reconcileAfterPermit, request.TimeoutMilliseconds);
            finalPermit = null;
            lease = null;
            return new(ManagedTransferFinalSubmissionResultKind.ResultPending,
                quarantine: quarantine);
        }
    }

    private static async Task<bool> DisposeLatePermitAsync(
        Task<IBaiduCandidateMutationPermit?> acquisition)
    {
        try
        {
            var permit = await acquisition.ConfigureAwait(false);
            if (permit is not null) await permit.DisposeAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception) { return false; }
    }

    private static async Task DisposeLateLeaseAsync(Task<IDisposable> acquisition)
    {
        try
        {
            var lateLease = await acquisition.ConfigureAwait(false);
            lateLease.Dispose();
        }
        catch (Exception) { }
    }

    private async Task<bool> ReconcilePreBoundaryAsync(
        ManagedTransferFinalSubmissionRequest request,
        WebAutomationAttemptRecord initial,
        ManagedTransferFinalSubmissionOperationTracker tracker,
        int timeoutMilliseconds,
        CancellationToken cancellationToken,
        ManagedTransferFinalSubmissionProofRoute route =
            ManagedTransferFinalSubmissionProofRoute.DestinationPicker)
    {
        using var deadline = new ManagedTransferOperationDeadline(
            timeoutMilliseconds, cancellationToken, timeProvider);
        var first = await tracker.Track(request.AttemptStore.GetAsync(
                request.Identity.OperationId, request.Identity.AccountId,
                request.Identity.Generation, deadline.Token), ReadRisk)
            .WaitAsync(deadline.Token).ConfigureAwait(false);
        var second = await tracker.Track(request.AttemptStore.GetAsync(
                request.Identity.OperationId, request.Identity.AccountId,
                request.Identity.Generation, deadline.Token), ReadRisk)
            .WaitAsync(deadline.Token).ConfigureAwait(false);
        return SameDurableAttempt(first, second) && SameDurableAttempt(first, initial) &&
            await ProveAsync(request,
                route,
                deadline, tracker).ConfigureAwait(false);
    }

    private async Task<bool> ReconcileRejectedSubmissionPersistenceAsync(
        ManagedTransferFinalSubmissionRequest request,
        WebAutomationAttemptRecord initial,
        int timeoutMilliseconds,
        CancellationToken cancellationToken,
        ManagedTransferFinalSubmissionOperationTracker tracker)
    {
        using var deadline = new ManagedTransferOperationDeadline(
            timeoutMilliseconds, cancellationToken, timeProvider);
        var first = await tracker.Track(request.AttemptStore.GetAsync(
                request.Identity.OperationId, request.Identity.AccountId,
                request.Identity.Generation, deadline.Token), ReadRisk)
            .WaitAsync(deadline.Token).ConfigureAwait(false);
        var second = await tracker.Track(request.AttemptStore.GetAsync(
                request.Identity.OperationId, request.Identity.AccountId,
                request.Identity.Generation, deadline.Token), ReadRisk)
            .WaitAsync(deadline.Token).ConfigureAwait(false);
        return SameDurableAttempt(first, second) && SameDurableAttempt(first, initial) &&
            await ProveAsync(request,
                ManagedTransferFinalSubmissionProofRoute.DestinationPicker,
                deadline, tracker).ConfigureAwait(false);
    }

    private async Task<bool> ReconcileAmbiguousSubmissionPersistenceAsync(
        ManagedTransferFinalSubmissionRequest request,
        WebAutomationAttemptRecord initial,
        TransferSubmissionEvidence submission,
        Task<bool> persistence,
        int timeoutMilliseconds,
        CancellationToken cancellationToken,
        ManagedTransferFinalSubmissionOperationTracker tracker)
    {
        bool? persisted;
        try { persisted = await persistence.ConfigureAwait(false); }
        catch (Exception) { persisted = null; }

        using var deadline = new ManagedTransferOperationDeadline(
            timeoutMilliseconds, cancellationToken, timeProvider);
        if (persisted == true && !ExactProcessExit(request,
            await tracker.Track(request.Boundaries.ObserveProcessExitAsync(request, deadline.Token),
                    ReadRisk)
                .WaitAsync(deadline.Token).ConfigureAwait(false)))
            return false;
        var first = await tracker.Track(request.AttemptStore.GetAsync(
                request.Identity.OperationId, request.Identity.AccountId,
                request.Identity.Generation, deadline.Token), ReadRisk)
            .WaitAsync(deadline.Token).ConfigureAwait(false);
        var second = await tracker.Track(request.AttemptStore.GetAsync(
                request.Identity.OperationId, request.Identity.AccountId,
                request.Identity.Generation, deadline.Token), ReadRisk)
            .WaitAsync(deadline.Token).ConfigureAwait(false);
        if (!SameDurableAttempt(first, second) ||
            (persisted == true && !await ProveAsync(request,
                ManagedTransferFinalSubmissionProofRoute.OriginalSource,
                deadline, tracker).ConfigureAwait(false)) ||
            !await ProveAsync(request,
                ManagedTransferFinalSubmissionProofRoute.DestinationPicker,
                deadline, tracker).ConfigureAwait(false))
            return false;
        if (SameDurableAttempt(first, initial)) return persisted != true;
        return ExactBoundAttempt(request, initial, first, submission);
    }

    private async Task<bool> ResolveAmbiguousSubmissionAfterPermitAsync(
        ManagedTransferFinalSubmissionRequest request,
        WebAutomationAttemptRecord initial,
        TransferSubmissionEvidence submission,
        Task<bool> persistence,
        int timeoutMilliseconds,
        CancellationToken cancellationToken,
        ManagedTransferFinalSubmissionOperationTracker tracker)
    {
        bool? persisted;
        try { persisted = await persistence.ConfigureAwait(false); }
        catch (Exception) { persisted = null; }
        if (persisted == false) return true;
        if (persisted is null)
        {
            using var deadline = new ManagedTransferOperationDeadline(
                timeoutMilliseconds, cancellationToken, timeProvider);
            var first = await tracker.Track(request.AttemptStore.GetAsync(
                    request.Identity.OperationId, request.Identity.AccountId,
                    request.Identity.Generation, deadline.Token), ReadRisk)
                .WaitAsync(deadline.Token).ConfigureAwait(false);
            var second = await tracker.Track(request.AttemptStore.GetAsync(
                    request.Identity.OperationId, request.Identity.AccountId,
                    request.Identity.Generation, deadline.Token), ReadRisk)
                .WaitAsync(deadline.Token).ConfigureAwait(false);
            if (!SameDurableAttempt(first, second)) return false;
            if (SameDurableAttempt(first, initial)) return true;
            if (!ExactBoundAttempt(request, initial, first, submission)) return false;
        }
        return await ResolveKnownNotDispatchedAfterPermitAsync(
                request, initial, submission, timeoutMilliseconds, cancellationToken, tracker)
            .ConfigureAwait(false);
    }

    private async Task<bool> PrepareKnownNotDispatchedAsync(
        ManagedTransferFinalSubmissionRequest request,
        WebAutomationAttemptRecord initial,
        TransferSubmissionEvidence submission,
        int timeoutMilliseconds,
        CancellationToken cancellationToken,
        ManagedTransferFinalSubmissionOperationTracker tracker)
    {
        using var deadline = new ManagedTransferOperationDeadline(
            timeoutMilliseconds, cancellationToken, timeProvider);
        if (!ExactProcessExit(request,
                await tracker.Track(request.Boundaries.ObserveProcessExitAsync(request, deadline.Token),
                        ReadRisk)
                    .WaitAsync(deadline.Token).ConfigureAwait(false)) ||
            !await ProveAsync(request,
                ManagedTransferFinalSubmissionProofRoute.OriginalSource,
                deadline, tracker).ConfigureAwait(false))
            return false;
        var first = await tracker.Track(request.AttemptStore.GetAsync(
                request.Identity.OperationId, request.Identity.AccountId,
                request.Identity.Generation, deadline.Token), ReadRisk)
            .WaitAsync(deadline.Token).ConfigureAwait(false);
        var second = await tracker.Track(request.AttemptStore.GetAsync(
                request.Identity.OperationId, request.Identity.AccountId,
                request.Identity.Generation, deadline.Token), ReadRisk)
            .WaitAsync(deadline.Token).ConfigureAwait(false);
        return SameDurableAttempt(first, second) &&
            ExactBoundAttempt(request, initial, first, submission) &&
            CandidateStillValid(request, timeProvider.GetUtcNow()) &&
            await ProveAsync(request,
                ManagedTransferFinalSubmissionProofRoute.DestinationPicker,
                deadline, tracker).ConfigureAwait(false);
    }

    private async Task<bool> ResolveKnownNotDispatchedAfterPermitAsync(
        ManagedTransferFinalSubmissionRequest request,
        WebAutomationAttemptRecord initial,
        TransferSubmissionEvidence submission,
        int timeoutMilliseconds,
        CancellationToken cancellationToken,
        ManagedTransferFinalSubmissionOperationTracker tracker)
    {
        using var deadline = new ManagedTransferOperationDeadline(
            timeoutMilliseconds, cancellationToken, timeProvider);
        var current = await tracker.Track(request.AttemptStore.GetAsync(
                request.Identity.OperationId, request.Identity.AccountId,
                request.Identity.Generation, deadline.Token), ReadRisk)
            .WaitAsync(deadline.Token).ConfigureAwait(false);
        if (!ExactBoundAttempt(request, initial, current, submission)) return false;
        return await tracker.Track(request.AttemptStore.TryResolveTransferKnownNotDispatchedAsync(
                request.Identity.OperationId, request.Identity.AccountId,
                request.Identity.Generation, current!.Revision,
                submission.SubmissionId!.Value, submission.ReadinessFingerprint!,
                timeProvider.GetUtcNow(), deadline.Token), StoreRisk)
            .WaitAsync(deadline.Token).ConfigureAwait(false);
    }

    private async Task<bool> ReconcileHeldLeaseAsync(
        ManagedTransferFinalSubmissionRequest request,
        WebAutomationAttemptRecord initial,
        TransferSubmissionEvidence submission,
        ManagedBrowserTransferSubmissionReadiness readiness,
        int timeoutMilliseconds,
        CancellationToken cancellationToken,
        ManagedTransferFinalSubmissionOperationTracker tracker)
    {
        using var deadline = new ManagedTransferOperationDeadline(
            timeoutMilliseconds, cancellationToken, timeProvider);
        if (!ExactProcessExit(request,
                await tracker.Track(request.Boundaries.ObserveProcessExitAsync(request, deadline.Token),
                        ReadRisk)
                    .WaitAsync(deadline.Token).ConfigureAwait(false)) ||
            !await ProveAsync(request, ManagedTransferFinalSubmissionProofRoute.OriginalSource,
                deadline, tracker).ConfigureAwait(false))
            return false;

        var first = await LoadAsync().ConfigureAwait(false);
        var second = await LoadAsync().ConfigureAwait(false);
        if (!SameDurableAttempt(first, second)) return false;
        var current = first!;
        var observedNoBoundOfficial = false;

        if (current.State == WebAutomationAttemptState.Submitting)
        {
            if (!ExactBoundAttempt(request, initial, current, submission) ||
                !await ProveAsync(request,
                    ManagedTransferFinalSubmissionProofRoute.DestinationPicker,
                    deadline, tracker).ConfigureAwait(false))
                return false;
            ManagedTransferOfficialOutcome? observed;
            try
            {
                observed = await tracker.Track(request.Boundaries.ObserveOutcomeAsync(
                        readiness, deadline.Token), ReadRisk)
                    .WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException ||
                !cancellationToken.IsCancellationRequested)
            {
                observed = null;
            }
            if (observed is { Kind: ManagedBrowserTransferOutcomeKind.AcceptedBound,
                    OfficialOutcome: { } official } &&
                ExactOfficial(request, initial, official))
            {
                var mapping = new TransferIdentityMapping([
                    new TransferIdentityMappingEntry(official.SourceExactId!,
                        official.DestinationExactId!),
                ]);
                if (!await tracker.Track(request.AttemptStore.TryPersistTransferAcceptanceAsync(
                        request.Identity.OperationId, request.Identity.AccountId,
                        request.Identity.Generation, current.Revision,
                        official.OfficialRequestId!, mapping,
                        official.OfficialObservationFingerprint!, timeProvider.GetUtcNow(),
                        deadline.Token), StoreRisk).WaitAsync(deadline.Token).ConfigureAwait(false))
                    return false;
            }
            else
            {
                var advance = tracker.Track(request.AttemptStore.TryAdvanceAsync(
                    request.Identity.OperationId, request.Identity.AccountId,
                    request.Identity.Generation, WebAutomationAttemptState.Submitting,
                    current.Revision, WebAutomationAttemptState.ResultPending,
                    observationFingerprint: null, isWaitingForUser: false,
                    timeProvider.GetUtcNow(), deadline.Token), StoreRisk);
                if (!await advance.WaitAsync(deadline.Token).ConfigureAwait(false)) return false;
                observedNoBoundOfficial = true;
            }
            first = await LoadAsync().ConfigureAwait(false);
            second = await LoadAsync().ConfigureAwait(false);
            if (!SameDurableAttempt(first, second)) return false;
            current = first!;
        }

        if (current.State == WebAutomationAttemptState.ResultPending)
        {
            if (current.SubmissionEvidence != submission) return false;
            if (current is { OfficialRequestId: null, TransferIdentityMapping: null,
                    AcceptedRemoteItems: null, OfficialObservationFingerprint: null })
            {
                if (observedNoBoundOfficial) return false;
                var observed = await tracker.Track(request.Boundaries.ObserveOutcomeAsync(
                        readiness, deadline.Token), ReadRisk)
                    .WaitAsync(deadline.Token).ConfigureAwait(false);
                if (observed is not { Kind: ManagedBrowserTransferOutcomeKind.AcceptedBound,
                        OfficialOutcome: { } official } ||
                    !ExactOfficial(request, initial, official))
                    return false;
                var lateMapping = new TransferIdentityMapping([
                    new TransferIdentityMappingEntry(official.SourceExactId!,
                        official.DestinationExactId!),
                ]);
                if (!await tracker.Track(request.AttemptStore.TryPersistTransferLateAcceptanceAsync(
                        request.Identity.OperationId, request.Identity.AccountId,
                        request.Identity.Generation, current.Revision,
                        submission.SubmissionId!.Value,
                        submission.ReadinessFingerprint!,
                        submission.TargetBaselineFingerprint,
                        official.OfficialRequestId!, lateMapping,
                        official.OfficialObservationFingerprint!, timeProvider.GetUtcNow(),
                        deadline.Token), StoreRisk).WaitAsync(deadline.Token).ConfigureAwait(false))
                    return false;
                first = await LoadAsync().ConfigureAwait(false);
                second = await LoadAsync().ConfigureAwait(false);
                if (!SameDurableAttempt(first, second)) return false;
                current = first!;
            }
            if (current.OfficialRequestId is null ||
                current.TransferIdentityMapping is not { Entries.Length: 1 } mapping ||
                !string.Equals(mapping.Entries[0].SourceExactId,
                    request.ImmutableSource.Items.Single().ExactId, StringComparison.Ordinal))
                return false;
            var target = await ReadExactTargetRouteAsync(request, deadline, tracker)
                .ConfigureAwait(false);
            if (!ExactTargetRead(request, initial, mapping, target)) return false;
            TransferTargetConfirmationEvidence confirmation;
            try
            {
                confirmation = TransferTargetConfirmationEvidence.Create(
                    submission, current.OfficialRequestId, mapping,
                    initial.TargetPreparationEvidence!,
                    target!.DurableEvidence.Baseline,
                    target.WireSnapshot.ObservationFingerprint,
                    timeProvider.GetUtcNow());
            }
            catch (ArgumentException)
            {
                return false;
            }
            if (!await tracker.Track(request.AttemptStore.TryPersistTransferTargetConfirmationAsync(
                    request.Identity.OperationId, request.Identity.AccountId,
                    request.Identity.Generation, current.Revision, confirmation,
                    deadline.Token), StoreRisk).WaitAsync(deadline.Token).ConfigureAwait(false))
                return false;
            first = await LoadAsync().ConfigureAwait(false);
            second = await LoadAsync().ConfigureAwait(false);
            if (!SameFinal(first, second, submission, confirmation)) return false;
            current = first!;
        }

        return current.State == WebAutomationAttemptState.Succeeded &&
            current.SubmissionEvidence == submission &&
            current.TargetConfirmationEvidence is not null &&
            current.TransferIdentityMapping is { Entries.Length: 1 } finalMapping &&
            string.Equals(finalMapping.Entries[0].DestinationExactId,
                current.TargetConfirmationEvidence.AddedTargetExactId,
                StringComparison.Ordinal) &&
            await ProveAsync(request,
                ManagedTransferFinalSubmissionProofRoute.ExactTargetDirectory,
                deadline, tracker).ConfigureAwait(false);

        Task<WebAutomationAttemptRecord?> LoadAsync() => tracker.Track(request.AttemptStore.GetAsync(
            request.Identity.OperationId, request.Identity.AccountId,
            request.Identity.Generation, deadline.Token), ReadRisk).WaitAsync(deadline.Token);
    }

    private async Task<bool> ReconcileTerminalHeldLeaseAsync(
        ManagedTransferFinalSubmissionRequest request,
        WebAutomationAttemptState expectedState,
        int timeoutMilliseconds,
        CancellationToken cancellationToken,
        ManagedTransferFinalSubmissionOperationTracker tracker)
    {
        using var deadline = new ManagedTransferOperationDeadline(
            timeoutMilliseconds, cancellationToken, timeProvider);
        var first = await tracker.Track(request.AttemptStore.GetAsync(
                request.Identity.OperationId, request.Identity.AccountId,
                request.Identity.Generation, deadline.Token), ReadRisk)
            .WaitAsync(deadline.Token).ConfigureAwait(false);
        var second = await tracker.Track(request.AttemptStore.GetAsync(
                request.Identity.OperationId, request.Identity.AccountId,
                request.Identity.Generation, deadline.Token), ReadRisk)
            .WaitAsync(deadline.Token).ConfigureAwait(false);
        if (!SameDurableAttempt(first, second) || first!.State != expectedState ||
            !ExactProcessExit(request,
                await tracker.Track(request.Boundaries.ObserveProcessExitAsync(request, deadline.Token),
                        ReadRisk)
                    .WaitAsync(deadline.Token).ConfigureAwait(false)))
            return false;
        return await ProveAsync(request,
            expectedState == WebAutomationAttemptState.Succeeded
                ? ManagedTransferFinalSubmissionProofRoute.ExactTargetDirectory
                : ManagedTransferFinalSubmissionProofRoute.DestinationPicker,
            deadline, tracker).ConfigureAwait(false);
    }

    private async Task<bool> ReconcileRecoveryHeldLeaseAsync(
        ManagedTransferFinalSubmissionRequest request,
        int timeoutMilliseconds,
        CancellationToken cancellationToken,
        ManagedTransferFinalSubmissionOperationTracker tracker)
    {
        using var deadline = new ManagedTransferOperationDeadline(
            timeoutMilliseconds, cancellationToken, timeProvider);
        var first = await tracker.Track(request.AttemptStore.GetAsync(
                request.Identity.OperationId, request.Identity.AccountId,
                request.Identity.Generation, deadline.Token), ReadRisk)
            .WaitAsync(deadline.Token).ConfigureAwait(false);
        var second = await tracker.Track(request.AttemptStore.GetAsync(
                request.Identity.OperationId, request.Identity.AccountId,
                request.Identity.Generation, deadline.Token), ReadRisk)
            .WaitAsync(deadline.Token).ConfigureAwait(false);
        if (!SameDurableAttempt(first, second) || first is null ||
            !IsRecoveryEligible(request, first) ||
            !await ProveAsync(request,
                ManagedTransferFinalSubmissionProofRoute.OriginalSource,
                deadline, tracker).ConfigureAwait(false))
            return false;
        var current = first;
        if (current.State == WebAutomationAttemptState.DefinitelyNotSubmitted)
            return true;

        var submission = current.SubmissionEvidence!;
        var context = BuildContext(request, current.TargetPreparationEvidence!,
            DeterministicHelperTimeout(request.TimeoutMilliseconds));
        var readiness = new ManagedBrowserTransferSubmissionReadiness(context, 1);
        if (!string.Equals(readiness.ReadinessFingerprint,
                submission.ReadinessFingerprint, StringComparison.Ordinal))
            return false;

        if (current.State == WebAutomationAttemptState.Submitting)
        {
            var observed = await tracker.Track(request.Boundaries.ObserveOutcomeAsync(
                    readiness, deadline.Token), ReadRisk).WaitAsync(deadline.Token)
                .ConfigureAwait(false);
            if (observed is not { Kind: ManagedBrowserTransferOutcomeKind.AcceptedBound,
                    OfficialOutcome: { } official } || !ExactOfficial(request, current, official))
            {
                _ = await tracker.Track(request.AttemptStore.TryAdvanceAsync(
                        request.Identity.OperationId, request.Identity.AccountId,
                        request.Identity.Generation, WebAutomationAttemptState.Submitting,
                        current.Revision, WebAutomationAttemptState.ResultPending,
                        observationFingerprint: null, isWaitingForUser: false,
                        timeProvider.GetUtcNow(), deadline.Token), StoreRisk)
                    .WaitAsync(deadline.Token).ConfigureAwait(false);
                return false;
            }
            var mapping = new TransferIdentityMapping([
                new TransferIdentityMappingEntry(official.SourceExactId!,
                    official.DestinationExactId!),
            ]);
            if (!await tracker.Track(request.AttemptStore.TryPersistTransferAcceptanceAsync(
                    request.Identity.OperationId, request.Identity.AccountId,
                    request.Identity.Generation, current.Revision,
                    official.OfficialRequestId!, mapping,
                    official.OfficialObservationFingerprint!, timeProvider.GetUtcNow(),
                    deadline.Token), StoreRisk).WaitAsync(deadline.Token).ConfigureAwait(false))
                return false;
            var accepted = await LoadStableAsync().ConfigureAwait(false);
            if (accepted is null) return false;
            current = accepted;
        }

        if (current is { State: WebAutomationAttemptState.ResultPending,
                OfficialRequestId: null, TransferIdentityMapping: null,
                AcceptedRemoteItems: null, OfficialObservationFingerprint: null })
        {
            var observed = await tracker.Track(request.Boundaries.ObserveOutcomeAsync(
                    readiness, deadline.Token), ReadRisk).WaitAsync(deadline.Token)
                .ConfigureAwait(false);
            if (observed is not { Kind: ManagedBrowserTransferOutcomeKind.AcceptedBound,
                    OfficialOutcome: { } official } || !ExactOfficial(request, current, official))
                return false;
            var mapping = new TransferIdentityMapping([
                new TransferIdentityMappingEntry(official.SourceExactId!,
                    official.DestinationExactId!),
            ]);
            if (!await tracker.Track(request.AttemptStore.TryPersistTransferLateAcceptanceAsync(
                    request.Identity.OperationId, request.Identity.AccountId,
                    request.Identity.Generation, current.Revision,
                    submission.SubmissionId!.Value, submission.ReadinessFingerprint!,
                    submission.TargetBaselineFingerprint,
                    official.OfficialRequestId!, mapping,
                    official.OfficialObservationFingerprint!, timeProvider.GetUtcNow(),
                    deadline.Token), StoreRisk).WaitAsync(deadline.Token).ConfigureAwait(false))
                return false;
            var lateAccepted = await LoadStableAsync().ConfigureAwait(false);
            if (lateAccepted is null) return false;
            current = lateAccepted;
        }

        if (current is { State: WebAutomationAttemptState.ResultPending,
                OfficialRequestId: not null,
                TransferIdentityMapping: { Entries.Length: 1 } officialMapping })
        {
            var target = await ReadExactTargetRouteAsync(request, deadline, tracker)
                .ConfigureAwait(false);
            if (!ExactTargetRead(request, current, officialMapping, target)) return false;
            TransferTargetConfirmationEvidence confirmation;
            try
            {
                confirmation = TransferTargetConfirmationEvidence.Create(
                    submission, current.OfficialRequestId, officialMapping,
                    current.TargetPreparationEvidence!, target!.DurableEvidence.Baseline,
                    target.WireSnapshot.ObservationFingerprint, timeProvider.GetUtcNow());
            }
            catch (ArgumentException)
            {
                return false;
            }
            if (!await tracker.Track(request.AttemptStore.TryPersistTransferTargetConfirmationAsync(
                    request.Identity.OperationId, request.Identity.AccountId,
                    request.Identity.Generation, current.Revision, confirmation,
                    deadline.Token), StoreRisk).WaitAsync(deadline.Token).ConfigureAwait(false))
                return false;
            var confirmed = await LoadStableAsync().ConfigureAwait(false);
            if (confirmed is null) return false;
            current = confirmed;
        }

        return current.State == WebAutomationAttemptState.Succeeded &&
            current.TargetConfirmationEvidence is not null &&
            current.TransferIdentityMapping is { Entries.Length: 1 } finalMapping &&
            string.Equals(finalMapping.Entries[0].DestinationExactId,
                current.TargetConfirmationEvidence.AddedTargetExactId,
                StringComparison.Ordinal) &&
            await ProveAsync(request,
                ManagedTransferFinalSubmissionProofRoute.ExactTargetDirectory,
                deadline, tracker).ConfigureAwait(false);

        async Task<WebAutomationAttemptRecord?> LoadStableAsync()
        {
            var left = await tracker.Track(request.AttemptStore.GetAsync(
                    request.Identity.OperationId, request.Identity.AccountId,
                    request.Identity.Generation, deadline.Token), ReadRisk)
                .WaitAsync(deadline.Token).ConfigureAwait(false);
            var right = await tracker.Track(request.AttemptStore.GetAsync(
                    request.Identity.OperationId, request.Identity.AccountId,
                    request.Identity.Generation, deadline.Token), ReadRisk)
                .WaitAsync(deadline.Token).ConfigureAwait(false);
            return SameDurableAttempt(left, right) ? left : null;
        }
    }

    internal async Task<ManagedTransferFinalSubmissionResult> RecoverAsync(
        ManagedTransferFinalSubmissionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = request.CreateBoundaryScope();
        var tracker = new ManagedTransferFinalSubmissionOperationTracker();
        using var deadline = new ManagedTransferOperationDeadline(
            request.TimeoutMilliseconds, cancellationToken, timeProvider);
        IDisposable? lease = null;
        try
        {
            var attempt = await tracker.Track(request.AttemptStore.GetAsync(
                    request.Identity.OperationId, request.Identity.AccountId,
                    request.Identity.Generation, deadline.Token), ReadRisk)
                .WaitAsync(deadline.Token).ConfigureAwait(false);
            if (!IsRecoveryEligible(request, attempt)) return NotEligible();
            if (attempt!.State == WebAutomationAttemptState.DefinitelyNotSubmitted)
                return new(ManagedTransferFinalSubmissionResultKind.DefinitelyNotDispatched);

            var leaseTask = tracker.Track(request.Boundaries.AcquireAccountLeaseAsync(
                request.Identity.AccountId, deadline.Token), ReadRisk);
            try
            {
                lease = await leaseTask.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or TimeoutException)
            {
                _ = DisposeLateLeaseAsync(leaseTask);
                throw;
            }
            deadline.RequireBudget();
            if (!await ProveAsync(request,
                    ManagedTransferFinalSubmissionProofRoute.OriginalSource,
                    deadline, tracker).ConfigureAwait(false))
                return Pending();

            if (attempt.State == WebAutomationAttemptState.Succeeded)
            {
                var confirmedOne = await tracker.Track(request.AttemptStore.GetAsync(
                        request.Identity.OperationId, request.Identity.AccountId,
                        request.Identity.Generation, deadline.Token), ReadRisk)
                    .WaitAsync(deadline.Token).ConfigureAwait(false);
                var confirmedTwo = await tracker.Track(request.AttemptStore.GetAsync(
                        request.Identity.OperationId, request.Identity.AccountId,
                        request.Identity.Generation, deadline.Token), ReadRisk)
                    .WaitAsync(deadline.Token).ConfigureAwait(false);
                if (!SameDurableAttempt(confirmedOne, confirmedTwo) ||
                    !SameDurableAttempt(confirmedOne, attempt) ||
                    attempt.TargetConfirmationEvidence is null ||
                    attempt.TransferIdentityMapping is not { Entries.Length: 1 } mapping ||
                    !string.Equals(mapping.Entries[0].DestinationExactId,
                        attempt.TargetConfirmationEvidence.AddedTargetExactId,
                        StringComparison.Ordinal) ||
                    !await ProveAsync(request,
                        ManagedTransferFinalSubmissionProofRoute.ExactTargetDirectory,
                        deadline, tracker).ConfigureAwait(false))
                    return Pending();
                var alreadyConfirmedDestination = mapping.Entries[0].DestinationExactId;
                lease.Dispose();
                lease = null;
                return new(ManagedTransferFinalSubmissionResultKind.ExactSuccess,
                    alreadyConfirmedDestination);
            }

            if (attempt.State == WebAutomationAttemptState.Submitting)
            {
                var context = BuildContext(request, attempt.TargetPreparationEvidence!,
                    DeterministicHelperTimeout(request.TimeoutMilliseconds));
                var reconstructed = new ManagedBrowserTransferSubmissionReadiness(context, 1);
                if (!string.Equals(reconstructed.ReadinessFingerprint,
                        attempt.SubmissionEvidence!.ReadinessFingerprint,
                        StringComparison.Ordinal))
                    return Pending();
                var official = await tracker.Track(request.Boundaries.ObserveOutcomeAsync(
                        reconstructed, deadline.Token), ReadRisk).WaitAsync(deadline.Token)
                    .ConfigureAwait(false);
                if (official is not { Kind: ManagedBrowserTransferOutcomeKind.AcceptedBound,
                        OfficialOutcome: { } observed } ||
                    !ExactOfficial(request, attempt, observed))
                    return Pending();
                var observedMapping = new TransferIdentityMapping([
                    new TransferIdentityMappingEntry(observed.SourceExactId!,
                        observed.DestinationExactId!),
                ]);
                if (!await tracker.Track(request.AttemptStore.TryPersistTransferAcceptanceAsync(
                        request.Identity.OperationId, request.Identity.AccountId,
                        request.Identity.Generation, attempt.Revision,
                        observed.OfficialRequestId!, observedMapping,
                        observed.OfficialObservationFingerprint!, timeProvider.GetUtcNow(),
                        deadline.Token), StoreRisk).WaitAsync(deadline.Token).ConfigureAwait(false))
                    return Pending();
                attempt = await tracker.Track(request.AttemptStore.GetAsync(
                        request.Identity.OperationId, request.Identity.AccountId,
                        request.Identity.Generation, deadline.Token), ReadRisk)
                    .WaitAsync(deadline.Token).ConfigureAwait(false);
            }

            if (attempt is { State: WebAutomationAttemptState.ResultPending,
                    SubmissionEvidence.IsLocallyBound: true,
                    OfficialRequestId: null, TransferIdentityMapping: null,
                    AcceptedRemoteItems: null, OfficialObservationFingerprint: null })
            {
                var context = BuildContext(request, attempt.TargetPreparationEvidence!,
                    DeterministicHelperTimeout(request.TimeoutMilliseconds));
                var reconstructed = new ManagedBrowserTransferSubmissionReadiness(context, 1);
                if (!string.Equals(reconstructed.ReadinessFingerprint,
                        attempt.SubmissionEvidence!.ReadinessFingerprint,
                        StringComparison.Ordinal))
                    return Pending();
                var late = await tracker.Track(request.Boundaries.ObserveOutcomeAsync(
                        reconstructed, deadline.Token), ReadRisk).WaitAsync(deadline.Token)
                    .ConfigureAwait(false);
                if (late is not { Kind: ManagedBrowserTransferOutcomeKind.AcceptedBound,
                        OfficialOutcome: { } observed } ||
                    !ExactOfficial(request, attempt, observed))
                    return Pending();
                var lateMapping = new TransferIdentityMapping([
                    new TransferIdentityMappingEntry(observed.SourceExactId!,
                        observed.DestinationExactId!),
                ]);
                if (!await tracker.Track(request.AttemptStore.TryPersistTransferLateAcceptanceAsync(
                        request.Identity.OperationId, request.Identity.AccountId,
                        request.Identity.Generation, attempt.Revision,
                        attempt.SubmissionEvidence.SubmissionId!.Value,
                        attempt.SubmissionEvidence.ReadinessFingerprint!,
                        attempt.SubmissionEvidence.TargetBaselineFingerprint,
                        observed.OfficialRequestId!, lateMapping,
                        observed.OfficialObservationFingerprint!, timeProvider.GetUtcNow(),
                        deadline.Token), StoreRisk).WaitAsync(deadline.Token).ConfigureAwait(false))
                    return Pending();
                attempt = await tracker.Track(request.AttemptStore.GetAsync(
                        request.Identity.OperationId, request.Identity.AccountId,
                        request.Identity.Generation, deadline.Token), ReadRisk)
                    .WaitAsync(deadline.Token).ConfigureAwait(false);
            }

            if (attempt is not { State: WebAutomationAttemptState.ResultPending,
                    SubmissionEvidence.IsLocallyBound: true,
                    OfficialRequestId: not null,
                    TransferIdentityMapping: not null } ||
                attempt.TransferIdentityMapping.Entries.Length != 1)
                return Pending();
            var target = await ReadExactTargetRouteAsync(request, deadline, tracker)
                .ConfigureAwait(false);
            if (!ExactTargetRead(request, attempt, attempt.TransferIdentityMapping, target))
                return Pending();
            TransferTargetConfirmationEvidence confirmation;
            try
            {
                confirmation = TransferTargetConfirmationEvidence.Create(
                    attempt.SubmissionEvidence!, attempt.OfficialRequestId,
                    attempt.TransferIdentityMapping, attempt.TargetPreparationEvidence!,
                    target!.DurableEvidence.Baseline,
                    target.WireSnapshot.ObservationFingerprint,
                    timeProvider.GetUtcNow());
            }
            catch (ArgumentException)
            {
                return Pending();
            }
            if (!await tracker.Track(request.AttemptStore.TryPersistTransferTargetConfirmationAsync(
                    request.Identity.OperationId, request.Identity.AccountId,
                    request.Identity.Generation, attempt.Revision, confirmation,
                    deadline.Token), StoreRisk).WaitAsync(deadline.Token).ConfigureAwait(false))
                return Pending();
            var finalOne = await tracker.Track(request.AttemptStore.GetAsync(
                    request.Identity.OperationId, request.Identity.AccountId,
                    request.Identity.Generation, deadline.Token), ReadRisk)
                .WaitAsync(deadline.Token).ConfigureAwait(false);
            var finalTwo = await tracker.Track(request.AttemptStore.GetAsync(
                    request.Identity.OperationId, request.Identity.AccountId,
                    request.Identity.Generation, deadline.Token), ReadRisk)
                .WaitAsync(deadline.Token).ConfigureAwait(false);
            if (!SameFinal(finalOne, finalTwo, attempt.SubmissionEvidence!, confirmation) ||
                !await ProveAsync(request,
                    ManagedTransferFinalSubmissionProofRoute.ExactTargetDirectory,
                    deadline, tracker).ConfigureAwait(false))
                return Pending();
            var destination = attempt.TransferIdentityMapping.Entries[0].DestinationExactId;
            lease.Dispose();
            lease = null;
            return new(ManagedTransferFinalSubmissionResultKind.ExactSuccess, destination);
        }
        catch (OperationCanceledException)
        {
            return lease is null
                ? new(cancellationToken.IsCancellationRequested
                    ? ManagedTransferFinalSubmissionResultKind.Cancelled
                    : ManagedTransferFinalSubmissionResultKind.TimedOut)
                : Pending();
        }
        catch (TimeoutException)
        {
            return lease is null
                ? new(ManagedTransferFinalSubmissionResultKind.TimedOut)
                : Pending();
        }
        catch (Exception)
        {
            return lease is null ? NotEligible() : Pending();
        }
        finally
        {
            lease?.Dispose();
        }

        ManagedTransferFinalSubmissionResult Pending()
        {
            var quarantine = new ManagedTransferFinalSubmissionQuarantine(
                lease!, (timeout, token) => ReconcileRecoveryHeldLeaseAsync(
                    request, timeout, token, tracker), ReadRisk | StoreRisk, tracker,
                cleanupTimeoutMilliseconds: request.TimeoutMilliseconds);
            lease = null;
            return new(ManagedTransferFinalSubmissionResultKind.ResultPending,
                quarantine: quarantine);
        }
    }

    private static ManagedBrowserTransferSubmissionContext BuildContext(
        ManagedTransferFinalSubmissionRequest request,
        TransferTargetPreparationEvidence target,
        int timeoutMilliseconds)
    {
        var source = request.ImmutableSource.Items.Single();
        return new(request.ExactHandle.BrowserId, request.ExactHandle.TargetId,
            request.ExactHandle.Endpoint, timeoutMilliseconds, request.CanonicalSourceUri,
            request.ExactManagedSession.StableBaiduUserId,
            request.ImmutableSource.GenerationToken,
            request.ImmutableSource.ObservationFingerprint,
            request.PreparedDestination.SourceFingerprint,
            source.ExactId, source.Kind,
            request.PreparedDestination.Destination.RemoteId,
            request.PreparedDestination.Destination.Path.Value,
            target.Baseline.Fingerprint,
            request.DuplicatePolicy);
    }

    private static int DeterministicHelperTimeout(int sessionTimeoutMilliseconds) =>
        sessionTimeoutMilliseconds == 1 ? 1 : sessionTimeoutMilliseconds - 1;

    private static bool IsEligible(ManagedTransferFinalSubmissionRequest request,
        WebAutomationAttemptRecord? attempt, DateTimeOffset now)
    {
        try
        {
            request.Identity.Validate();
            if (attempt is null || attempt.OwnerKind != WebAutomationOwnerKind.Transfer ||
                attempt.State != WebAutomationAttemptState.BaselinePersisted ||
                attempt.Revision != request.ExpectedAttemptRevision ||
                attempt.OperationId != request.Identity.OperationId ||
                attempt.AccountId != request.Identity.AccountId ||
                attempt.Generation != request.Identity.Generation ||
                !attempt.ExecutionProfile.Equals(request.Identity.ExecutionProfile) ||
                attempt.SourceEvidence is not { Items.Count: 1 } source ||
                attempt.TargetPreparationEvidence is not { Scope: TransferTargetInventoryScope.AllItems } target ||
                attempt.Baseline is null || attempt.Baseline.Fingerprint != target.Baseline.Fingerprint ||
                attempt.SubmissionEvidence is not null || attempt.OfficialRequestId is not null ||
                attempt.TransferIdentityMapping is not null || attempt.AcceptedRemoteItems is not null ||
                attempt.TargetConfirmationEvidence is not null ||
                request.PreparedDestination.OperationId != request.Identity.OperationId ||
                request.PreparedDestination.AccountId != request.Identity.AccountId ||
                request.PreparedDestination.Generation != request.Identity.Generation ||
                request.PreparedDestination.OwningAttemptRevision + 1 != attempt.Revision ||
                request.PreparedDestination.ExecutionEngine != BaiduExecutionEngine.ManagedChromiumV1 ||
                request.PreparedDestination.Capability != source.Capability ||
                request.PreparedDestination.SourceFingerprint != source.Items.Fingerprint ||
                request.PreparedDestination.Destination.RemoteId != target.TargetExactId ||
                request.PreparedDestination.Destination.Path != target.TargetPath ||
                request.PreparedDestination.Scope != TransferDirectoryListingScope.FolderCandidates ||
                request.ImmutableSource.Items.Length != 1 ||
                request.ImmutableSource.BaiduUserId != source.BaiduUserId ||
                request.ImmutableSource.GenerationToken != source.GenerationToken ||
                request.ImmutableSource.ObservationFingerprint != source.ObservationFingerprint ||
                request.ImmutableSource.ComputeFingerprint(request.CanonicalSourceUri) !=
                    request.ImmutableSource.ObservationFingerprint ||
                request.ExactManagedSession.AccountId != request.Identity.AccountId ||
                request.ExactManagedSession.StableBaiduUserId != source.BaiduUserId ||
                request.ExecutionContext.Engine != BaiduExecutionEngine.ManagedChromiumV1 ||
                !ReferenceEquals(request.ExecutionContext.ManagedSession, request.ExactManagedSession) ||
                request.LifecycleSession.Session != request.ExactManagedSession ||
                request.LifecycleSession.CanonicalProfilePath !=
                    request.ExactHandle.Profile.CanonicalSourcePath ||
                request.ExactHandle.Installation.InstallationId !=
                    request.ExactManagedSession.InstallationId ||
                request.ExactHandle.Profile.AccountId != request.ExactManagedSession.AccountId ||
                request.ExactHandle.Profile.ProfileId.ToString("N") !=
                    request.ExactManagedSession.ProfileId ||
                request.ExactHandle.Profile.OwnershipId.ToString("N") !=
                    request.ExactManagedSession.ProfileOwnershipId ||
                request.ExactHandle.Installation.BrowserFamily !=
                    request.ExactManagedSession.BrowserFamily ||
                request.ExactHandle.RootProcessId <= 0 || request.ExactHandle.BrowserId == Guid.Empty ||
                request.CandidateBinding.Authorization.Action != AdapterWriteAction.SubmitTransfer ||
                request.CandidateBinding.Authorization.Capability != source.Capability ||
                request.CandidateBinding.Authorization.AccountId != request.Identity.AccountId ||
                request.CandidateBinding.Authorization.OperationId != request.Identity.OperationId ||
                request.CandidateBinding.Authorization.Generation != request.Identity.Generation ||
                request.CandidateBinding.Authorization.BatchId != request.Identity.BatchId ||
                request.CandidateBinding.Binding.Capability != source.Capability ||
                request.CandidateBinding.Binding.AccountId != request.Identity.AccountId ||
                request.CandidateBinding.Binding.OperationId != request.Identity.OperationId ||
                request.CandidateBinding.Binding.Generation != request.Identity.Generation ||
                request.CandidateBinding.Binding.BatchId != request.Identity.BatchId ||
                request.CandidateBinding.Binding.ExpiresAtUtc <=
                    request.CandidateBinding.Binding.IssuedAtUtc ||
                request.CandidateBinding.Binding.IssuedAtUtc > now ||
                request.CandidateBinding.Binding.ExpiresAtUtc <= now ||
                request.CandidateBinding.Binding.IsRecovery ||
                request.CandidateBinding.Authorization.IsRecovery ||
                request.CandidateBinding.ExpectedBaiduUserId != source.BaiduUserId)
                return false;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool ExactBoundAttempt(ManagedTransferFinalSubmissionRequest request,
        WebAutomationAttemptRecord initial, WebAutomationAttemptRecord? current,
        TransferSubmissionEvidence submission) =>
        current is not null && current.State == WebAutomationAttemptState.Submitting &&
        current.Revision == initial.Revision + 1 &&
        current.OperationId == initial.OperationId && current.AccountId == initial.AccountId &&
        current.Generation == initial.Generation && current.OwnerKind == initial.OwnerKind &&
        current.ExecutionProfile.Equals(initial.ExecutionProfile) &&
        string.Equals(current.SourceEvidence?.ObservationFingerprint,
            initial.SourceEvidence?.ObservationFingerprint, StringComparison.Ordinal) &&
        string.Equals(current.TargetPreparationEvidence?.ObservationFingerprint,
            initial.TargetPreparationEvidence?.ObservationFingerprint, StringComparison.Ordinal) &&
        current.SubmissionEvidence == submission &&
        current.OfficialRequestId is null && current.TransferIdentityMapping is null &&
        current.AcceptedRemoteItems is null && current.TargetConfirmationEvidence is null;

    private static bool IsRecoveryEligible(ManagedTransferFinalSubmissionRequest request,
        WebAutomationAttemptRecord? attempt)
    {
        if (attempt is null || attempt.OperationId != request.Identity.OperationId ||
            attempt.AccountId != request.Identity.AccountId ||
            attempt.Generation != request.Identity.Generation ||
            attempt.OwnerKind != WebAutomationOwnerKind.Transfer ||
            !attempt.ExecutionProfile.Equals(request.Identity.ExecutionProfile) ||
            attempt.SourceEvidence is not { Items.Count: 1 } source ||
            attempt.TargetPreparationEvidence is not { Scope: TransferTargetInventoryScope.AllItems } target ||
            attempt.SubmissionEvidence is not { IsLocallyBound: true } submission ||
            submission.SourceFingerprint != source.Items.Fingerprint ||
            submission.TargetExactId != target.TargetExactId ||
            submission.TargetBaselineFingerprint != target.Baseline.Fingerprint ||
            submission.BaiduUserId != source.BaiduUserId ||
            submission.Capability != source.Capability)
            return false;
        try
        {
            var frozenReadiness = new ManagedBrowserTransferSubmissionReadiness(
                BuildContext(request, target,
                    DeterministicHelperTimeout(request.TimeoutMilliseconds)), 1);
            if (!string.Equals(frozenReadiness.ReadinessFingerprint,
                    submission.ReadinessFingerprint, StringComparison.Ordinal))
                return false;
        }
        catch (Exception)
        {
            return false;
        }
        return attempt.State is WebAutomationAttemptState.Submitting or
            WebAutomationAttemptState.ResultPending or
            WebAutomationAttemptState.Succeeded or
            WebAutomationAttemptState.DefinitelyNotSubmitted;
    }

    private static bool CandidateStillValid(ManagedTransferFinalSubmissionRequest request,
        DateTimeOffset now) =>
        request.CandidateBinding.Binding.IssuedAtUtc <= now &&
        request.CandidateBinding.Binding.ExpiresAtUtc > now &&
        request.CandidateBinding.Authorization.Action == AdapterWriteAction.SubmitTransfer;

    private static bool SameDurableAttempt(
        WebAutomationAttemptRecord? left,
        WebAutomationAttemptRecord? right) =>
        left is not null && right is not null &&
        left.OperationId == right.OperationId && left.AccountId == right.AccountId &&
        left.Generation == right.Generation && left.OwnerKind == right.OwnerKind &&
        left.State == right.State && left.ResumeStage == right.ResumeStage &&
        left.Revision == right.Revision && left.CreatedAt == right.CreatedAt &&
        left.UpdatedAt == right.UpdatedAt &&
        left.IsWaitingForUser == right.IsWaitingForUser &&
        string.Equals(left.ObservationFingerprint, right.ObservationFingerprint,
            StringComparison.Ordinal) &&
        string.Equals(left.Baseline?.Fingerprint, right.Baseline?.Fingerprint,
            StringComparison.Ordinal) &&
        string.Equals(left.AcceptedRemoteItems?.Fingerprint,
            right.AcceptedRemoteItems?.Fingerprint, StringComparison.Ordinal) &&
        string.Equals(left.SourceEvidence?.ObservationFingerprint,
            right.SourceEvidence?.ObservationFingerprint, StringComparison.Ordinal) &&
        string.Equals(left.TargetPreparationEvidence?.ObservationFingerprint,
            right.TargetPreparationEvidence?.ObservationFingerprint, StringComparison.Ordinal) &&
        left.SubmissionEvidence?.SubmissionId == right.SubmissionEvidence?.SubmissionId &&
        string.Equals(left.SubmissionEvidence?.ReadinessFingerprint,
            right.SubmissionEvidence?.ReadinessFingerprint, StringComparison.Ordinal) &&
        string.Equals(left.SubmissionEvidence?.SourceFingerprint,
            right.SubmissionEvidence?.SourceFingerprint, StringComparison.Ordinal) &&
        string.Equals(left.OfficialRequestId, right.OfficialRequestId,
            StringComparison.Ordinal) &&
        string.Equals(left.TransferIdentityMapping?.Fingerprint,
            right.TransferIdentityMapping?.Fingerprint, StringComparison.Ordinal) &&
        left.ExecutionProfile.Equals(right.ExecutionProfile) &&
        string.Equals(left.OfficialObservationFingerprint,
            right.OfficialObservationFingerprint, StringComparison.Ordinal) &&
        left.TargetConfirmationEvidence?.SubmissionId ==
            right.TargetConfirmationEvidence?.SubmissionId &&
        string.Equals(left.TargetConfirmationEvidence?.OfficialMappingFingerprint,
            right.TargetConfirmationEvidence?.OfficialMappingFingerprint,
            StringComparison.Ordinal) &&
        string.Equals(left.TargetConfirmationEvidence?.TargetObservationFingerprint,
            right.TargetConfirmationEvidence?.TargetObservationFingerprint,
            StringComparison.Ordinal);

    private static bool SameFinal(WebAutomationAttemptRecord? left,
        WebAutomationAttemptRecord? right, TransferSubmissionEvidence submission,
        TransferTargetConfirmationEvidence confirmation) =>
        SameDurableAttempt(left, right) && left is not null &&
        left.State == WebAutomationAttemptState.Succeeded &&
        left.SubmissionEvidence == submission &&
        left.TargetConfirmationEvidence == confirmation;

    private static async Task<ManagedTransferTargetInventoryEvidence?> ReadExactTargetRouteAsync(
        ManagedTransferFinalSubmissionRequest request,
        ManagedTransferOperationDeadline deadline,
        ManagedTransferFinalSubmissionOperationTracker tracker)
    {
        var rootTask = request.Boundaries.OpenFixedTargetRootAsync(request, deadline.Token);
        _ = tracker.Track(rootTask, ReadRisk);
        var root = await rootTask
            .WaitAsync(deadline.Token).ConfigureAwait(false);
        if (root.Kind != ManagedBrowserOpenTransferTargetRootResultKind.RootOpened ||
            root.NavigationState != ManagedBrowserTargetNavigationState.RouteOpened)
            return null;
        var ancestors = request.PreparedDestination.FolderCandidatesSnapshot.AncestorExactIds;
        if (ancestors.Length == 0) return null;
        var current = await ReadAtAsync(ancestors[0], "/", ancestors.Take(1).ToArray())
            .ConfigureAwait(false);
        if (current is null) return null;
        var segments = request.PreparedDestination.Destination.Path.Value == "/"
            ? Array.Empty<string>()
            : request.PreparedDestination.Destination.Path.Value[1..].Split('/');
        if (segments.Length != ancestors.Length - 1) return null;
        for (var depth = 1; depth < ancestors.Length; depth++)
        {
            var childId = ancestors[depth];
            var childName = segments[depth - 1];
            var child = current.WireSnapshot.Items.SingleOrDefault(item =>
                item.Kind == ManagedBrowserTargetInventoryItemKind.Folder &&
                string.Equals(item.ExactId, childId, StringComparison.Ordinal) &&
                string.Equals(item.ServerName, childName, StringComparison.Ordinal));
            if (child is null) return null;
            var childPath = "/" + string.Join('/', segments.Take(depth));
            var enterTask = request.Boundaries.EnterFixedTargetDirectoryAsync(
                request, current.WireSnapshot.ToHeader(), childId, childName, childPath,
                deadline.Token);
            _ = tracker.Track(enterTask, ReadRisk);
            var entered = await enterTask.WaitAsync(deadline.Token).ConfigureAwait(false);
            if (entered.Kind != ManagedBrowserEnterTransferTargetDirectoryResultKind.DirectoryEntered ||
                entered.NavigationState != ManagedBrowserTargetNavigationState.RouteOpened)
                return null;
            current = await ReadAtAsync(childId, childPath, ancestors.Take(depth + 1).ToArray())
                .ConfigureAwait(false);
            if (current is null) return null;
        }
        return current;

        async Task<ManagedTransferTargetInventoryEvidence?> ReadAtAsync(
            string exactId, string path, IReadOnlyList<string> expectedAncestors)
        {
            var readTask = request.Boundaries.ReadFixedTargetAsync(
                request, exactId, path, expectedAncestors, deadline.Token);
            _ = tracker.Track(readTask, ReadRisk);
            var read = await readTask
                .WaitAsync(deadline.Token).ConfigureAwait(false);
            if (read.Kind != ManagedBrowserReadTransferTargetDirectoryResultKind.Observed ||
                read.Evidence is not { } evidence ||
                !ExactTargetRouteRead(request, evidence, exactId, path, expectedAncestors))
                return null;
            return evidence;
        }
    }

    private static bool ExactTargetRouteRead(
        ManagedTransferFinalSubmissionRequest request,
        ManagedTransferTargetInventoryEvidence target,
        string exactId,
        string path,
        IReadOnlyList<string> ancestors) =>
        ExactTargetProjection(target) &&
        target.WireSnapshot.BrowserId == request.ExactHandle.BrowserId &&
        string.Equals(target.WireSnapshot.TargetId, request.ExactHandle.TargetId,
            StringComparison.Ordinal) &&
        target.WireSnapshot.DevToolsEndpoint == request.ExactHandle.Endpoint &&
        string.Equals(target.WireSnapshot.BaiduUserId,
            request.ExactManagedSession.StableBaiduUserId, StringComparison.Ordinal) &&
        string.Equals(target.WireSnapshot.DirectoryExactId, exactId, StringComparison.Ordinal) &&
        string.Equals(target.WireSnapshot.CanonicalPath, path, StringComparison.Ordinal) &&
        target.WireSnapshot.AncestorExactIds.SequenceEqual(ancestors, StringComparer.Ordinal) &&
        target.WireSnapshot.Scope == ManagedBrowserTargetInventoryScope.AllItems &&
        target.WireSnapshot.ReachedEnd && target.WireSnapshot.IsStable &&
        target.WireSnapshot.TotalCount == target.WireSnapshot.Items.Count &&
        string.Equals(target.DurableEvidence.BaiduUserId,
            target.WireSnapshot.BaiduUserId, StringComparison.Ordinal) &&
        string.Equals(target.DurableEvidence.TargetGenerationToken,
            target.WireSnapshot.GenerationToken, StringComparison.Ordinal) &&
        string.Equals(target.DurableEvidence.TargetExactId,
            target.WireSnapshot.DirectoryExactId, StringComparison.Ordinal) &&
        string.Equals(target.DurableEvidence.TargetPath.Value,
            target.WireSnapshot.CanonicalPath, StringComparison.Ordinal) &&
        target.DurableEvidence.Scope == TransferTargetInventoryScope.AllItems &&
        target.DurableEvidence.ReachedEnd && target.DurableEvidence.IsStable &&
        target.DurableEvidence.TotalCount == target.WireSnapshot.TotalCount;

    private static bool ExactTargetRead(
        ManagedTransferFinalSubmissionRequest request,
        WebAutomationAttemptRecord attempt,
        TransferIdentityMapping? officialMapping,
        ManagedTransferTargetInventoryEvidence? target)
    {
        if (target is null || !ExactTargetProjection(target) ||
            target.WireSnapshot.BrowserId != request.ExactHandle.BrowserId ||
            !string.Equals(target.WireSnapshot.TargetId, request.ExactHandle.TargetId,
                StringComparison.Ordinal) ||
            target.WireSnapshot.DevToolsEndpoint != request.ExactHandle.Endpoint ||
            !string.Equals(target.WireSnapshot.BaiduUserId,
                request.ExactManagedSession.StableBaiduUserId, StringComparison.Ordinal) ||
            !string.Equals(target.WireSnapshot.DirectoryExactId,
                attempt.TargetPreparationEvidence!.TargetExactId, StringComparison.Ordinal) ||
            !string.Equals(target.WireSnapshot.CanonicalPath,
                attempt.TargetPreparationEvidence.TargetPath.Value, StringComparison.Ordinal) ||
            target.WireSnapshot.AncestorExactIds.Count == 0 ||
            !string.Equals(target.WireSnapshot.AncestorExactIds[^1],
                attempt.TargetPreparationEvidence.TargetExactId, StringComparison.Ordinal) ||
            target.WireSnapshot.Scope != ManagedBrowserTargetInventoryScope.AllItems ||
            !target.WireSnapshot.ReachedEnd || !target.WireSnapshot.IsStable ||
            target.WireSnapshot.TotalCount != target.WireSnapshot.Items.Count ||
            !string.Equals(target.DurableEvidence.BaiduUserId,
                target.WireSnapshot.BaiduUserId, StringComparison.Ordinal) ||
            !string.Equals(target.DurableEvidence.TargetGenerationToken,
                target.WireSnapshot.GenerationToken, StringComparison.Ordinal) ||
            !string.Equals(target.DurableEvidence.TargetExactId,
                target.WireSnapshot.DirectoryExactId, StringComparison.Ordinal) ||
            !string.Equals(target.DurableEvidence.TargetPath.Value,
                target.WireSnapshot.CanonicalPath, StringComparison.Ordinal) ||
            target.DurableEvidence.Scope != TransferTargetInventoryScope.AllItems ||
            !target.DurableEvidence.ReachedEnd || !target.DurableEvidence.IsStable ||
            target.DurableEvidence.TotalCount != target.WireSnapshot.TotalCount)
            return false;
        if (officialMapping is not { Entries.Length: 1 } mapping)
            return false;
        var baselineIds = attempt.TargetPreparationEvidence.Baseline.Items
            .Select(item => item.ExactId).ToHashSet(StringComparer.Ordinal);
        var currentIds = target.DurableEvidence.Baseline.Items
            .Select(item => item.ExactId).ToArray();
        if (!baselineIds.IsSubsetOf(currentIds)) return false;
        var additions = currentIds.Where(id => !baselineIds.Contains(id)).ToArray();
        return additions.Length == 1 &&
            string.Equals(additions[0], mapping.Entries[0].DestinationExactId,
                StringComparison.Ordinal);
    }

    private static bool ExactTargetProjection(
        ManagedTransferTargetInventoryEvidence target)
    {
        var wire = target.WireSnapshot;
        var all = target.AllItems;
        var durable = target.DurableEvidence;
        var wireIds = wire.Items.Select(item => item.ExactId).ToArray();
        var allIds = all.DetailedItems.Select(item => item.Identity.ExactId).ToArray();
        var topIds = all.TopLevelItems.Select(item => item.ExactId).ToArray();
        var durableIds = durable.Baseline.Items.Select(item => item.ExactId).ToArray();
        if (!IsUniqueCanonical(wireIds) || !IsUniqueCanonical(allIds) ||
            !IsUniqueCanonical(topIds) || !IsUniqueCanonical(durableIds) ||
            !wireIds.SequenceEqual(allIds, StringComparer.Ordinal) ||
            !wireIds.SequenceEqual(topIds, StringComparer.Ordinal) ||
            !wireIds.SequenceEqual(durableIds, StringComparer.Ordinal))
            return false;
        for (var index = 0; index < wire.Items.Count; index++)
        {
            var wireItem = wire.Items[index];
            var allItem = all.DetailedItems[index];
            var expectedKind = wireItem.Kind switch
            {
                ManagedBrowserTargetInventoryItemKind.File => TransferRemoteItemKind.File,
                ManagedBrowserTargetInventoryItemKind.Folder => TransferRemoteItemKind.Folder,
                _ => (TransferRemoteItemKind)(-1),
            };
            if (!Enum.IsDefined(expectedKind) || allItem.Kind != expectedKind ||
                !string.Equals(allItem.ExactServerName, wireItem.ServerName,
                    StringComparison.Ordinal) ||
                !string.Equals(allItem.Identity.DisplayedTopLevelName, wireItem.ServerName,
                    StringComparison.Ordinal) ||
                !string.Equals(all.TopLevelItems[index].DisplayedTopLevelName,
                    wireItem.ServerName,
                    StringComparison.Ordinal))
                return false;
        }
        WebAutomationBaseline recomputed;
        try { recomputed = WebAutomationBaseline.Create(wireIds); }
        catch (ArgumentException) { return false; }
        string durableFingerprint;
        try
        {
            durableFingerprint = TransferTargetPreparationEvidence.ComputeObservationFingerprint(
                durable.TargetExactId, durable.TargetPath, durable.TotalCount,
                durable.ReachedEnd, durable.IsStable, durable.TargetGenerationToken,
                durable.BaiduUserId, durable.Scope, durable.Baseline);
        }
        catch (ArgumentException)
        {
            return false;
        }
        return all.Scope == TransferDirectoryListingScope.AllItems &&
            string.Equals(wire.ComputeFingerprint(), wire.ObservationFingerprint,
                StringComparison.Ordinal) &&
            all.IsComplete && all.IsStable && all.ReachedEnd &&
            all.TotalCount == all.DetailedItems.Count &&
            all.Directory is not null &&
            string.Equals(all.Directory.RemoteId, wire.DirectoryExactId,
                StringComparison.Ordinal) &&
            all.Directory.Path.Value == wire.CanonicalPath &&
            all.AncestorExactIds.SequenceEqual(wire.AncestorExactIds.Skip(1),
                StringComparer.Ordinal) &&
            string.Equals(all.GenerationToken, wire.GenerationToken, StringComparison.Ordinal) &&
            string.Equals(all.BaiduUserId, wire.BaiduUserId, StringComparison.Ordinal) &&
            string.Equals(all.ObservationFingerprint, wire.ObservationFingerprint,
                StringComparison.Ordinal) &&
            string.Equals(recomputed.Fingerprint, durable.Baseline.Fingerprint,
                StringComparison.Ordinal) &&
            string.Equals(durableFingerprint, durable.ObservationFingerprint,
                StringComparison.Ordinal);

        static bool IsUniqueCanonical(IReadOnlyList<string> ids) =>
            ids.Distinct(StringComparer.Ordinal).Count() == ids.Count &&
            ids.SequenceEqual(ids.OrderBy(id => id, StringComparer.Ordinal),
                StringComparer.Ordinal);
    }

    private static bool ExactOfficial(
        ManagedTransferFinalSubmissionRequest request,
        WebAutomationAttemptRecord attempt,
        ManagedBrowserTransferSubmissionOutcomeResult official) =>
        string.Equals(official.ExactTargetId,
            attempt.TargetPreparationEvidence!.TargetExactId, StringComparison.Ordinal) &&
        string.Equals(official.SourceExactId,
            request.ImmutableSource.Items.Single().ExactId, StringComparison.Ordinal);

    private static async Task<bool> ProveAsync(ManagedTransferFinalSubmissionRequest request,
        ManagedTransferFinalSubmissionProofRoute route,
        ManagedTransferOperationDeadline deadline,
        ManagedTransferFinalSubmissionOperationTracker tracker)
    {
        deadline.RequireBudget();
        var proofTask = request.Boundaries.ObserveRuntimeAsync(
            new ManagedTransferFinalSubmissionProofRequest(request, route), deadline.Token);
        _ = tracker.Track(proofTask, ReadRisk);
        var proof = await proofTask
            .WaitAsync(deadline.Token).ConfigureAwait(false);
        return ExactRuntimeProof(request, route, proof);
    }

    private static bool ExactRuntimeProof(ManagedTransferFinalSubmissionRequest request,
        ManagedTransferFinalSubmissionProofRoute route,
        ManagedTransferRuntimeProofResult proof) =>
        proof.Kind == ManagedTransferRuntimeProofKind.Exact && proof.Route == route &&
        proof.Lifecycle == request.LifecycleSession &&
        proof.ProcessId == request.ExactHandle.RootProcessId &&
        proof.BrowserId == request.ExactHandle.BrowserId &&
        string.Equals(proof.TargetId, request.ExactHandle.TargetId, StringComparison.Ordinal) &&
        proof.Endpoint == request.ExactHandle.Endpoint &&
        string.Equals(proof.SourceObservationFingerprint,
            request.ImmutableSource.ObservationFingerprint, StringComparison.Ordinal);

    private static bool ExactPermitOwnership(ManagedTransferFinalSubmissionRequest request,
        ManagedTransferPermitOwnershipResult ownership) =>
        ownership.Kind == ManagedTransferPermitOwnershipKind.Owned &&
        ownership.OperationId == request.Identity.OperationId &&
        ownership.AccountId == request.Identity.AccountId &&
        ownership.Generation == request.Identity.Generation &&
        ownership.Action == AdapterWriteAction.SubmitTransfer;

    private static bool ExactProcessExit(ManagedTransferFinalSubmissionRequest request,
        ManagedTransferProcessExitObservation observation) =>
        observation.Kind == ManagedTransferProcessExitObservationKind.Observed &&
        observation.ProcessId == request.ExactHandle.RootProcessId &&
        observation.BrowserId == request.ExactHandle.BrowserId &&
        string.Equals(observation.TargetId, request.ExactHandle.TargetId, StringComparison.Ordinal);

    private static ManagedTransferFinalSubmissionResult MapPicker(
        ManagedBrowserTransferPickerResultKind result) => result switch
        {
            ManagedBrowserTransferPickerResultKind.LoginRequired =>
                new(ManagedTransferFinalSubmissionResultKind.UserActionRequired),
            _ => NotEligible(),
        };

    private static ManagedTransferFinalSubmissionResult MapDirectory(
        ManagedTransferDirectoryMutationOutcome result) => result switch
        {
            ManagedTransferDirectoryMutationOutcome.Cancelled =>
                new(ManagedTransferFinalSubmissionResultKind.Cancelled),
            ManagedTransferDirectoryMutationOutcome.TimedOut =>
                new(ManagedTransferFinalSubmissionResultKind.TimedOut),
            _ => NotEligible(),
        };

    private static ManagedTransferFinalSubmissionResult NotEligible() =>
        new(ManagedTransferFinalSubmissionResultKind.NotEligible);
}
