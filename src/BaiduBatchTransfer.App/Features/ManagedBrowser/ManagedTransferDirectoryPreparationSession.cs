using BaiduBatchTransfer.App.Features.BrowserAutomation;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.BaiduAdapter.WebAutomation;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.App.Features.ManagedBrowser;

internal sealed class ManagedPreparedDestinationEvidence
{
    private ManagedPreparedDestinationEvidence(WebAutomationRequestIdentity identity,
        long owningAttemptRevision, AdapterCapabilityVersion capability,
        string sourceFingerprint, string sourceObservationFingerprint, RemoteDirectory destination,
        ManagedBrowserDirectorySnapshot folderCandidatesSnapshot)
    {
        OperationId = identity.OperationId;
        AccountId = identity.AccountId;
        Generation = identity.Generation;
        OwningAttemptRevision = owningAttemptRevision;
        ExecutionEngine = BaiduExecutionEngine.ManagedChromiumV1;
        Capability = capability;
        SourceFingerprint = sourceFingerprint;
        SourceObservationFingerprint = sourceObservationFingerprint;
        Destination = destination;
        FolderCandidatesSnapshot = folderCandidatesSnapshot;
    }

    internal static ManagedPreparedDestinationEvidence CreateFromValidatedState(
        WebAutomationRequestIdentity identity, BaiduExecutionContext executionContext,
        TransferDirectoryPreparationRequest request, WebAutomationAttemptRecord attempt,
        TransferSourceEvidence validatedSource, long owningAttemptRevision,
        TransferDirectoryPreparationOutcome outcome,
        ManagedTransferDirectoryEvidence finalEvidence)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(identity);
            ArgumentNullException.ThrowIfNull(executionContext);
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(attempt);
            ArgumentNullException.ThrowIfNull(validatedSource);
            ArgumentNullException.ThrowIfNull(outcome);
            ArgumentNullException.ThrowIfNull(finalEvidence);
            identity.Validate();
            request.Validate();

            var source = attempt.SourceEvidence;
            var destination = outcome.Directory;
            var snapshot = finalEvidence.Snapshot;
            var managedSession = executionContext.ManagedSession;
            if (outcome.Status != TransferDirectoryPreparationStatus.Prepared || destination is null ||
                owningAttemptRevision < 0 || attempt.Revision != owningAttemptRevision ||
                identity.OwnerKind != WebAutomationOwnerKind.Transfer ||
                identity.ExecutionProfile.Engine != BaiduExecutionEngine.ManagedChromiumV1 ||
                executionContext.Engine != BaiduExecutionEngine.ManagedChromiumV1 || managedSession is null ||
                managedSession.AccountId != identity.AccountId ||
                identity.ExecutionProfile.InstallationId != managedSession.InstallationId ||
                identity.ExecutionProfile.SessionId != managedSession.SessionId ||
                identity.ExecutionProfile.BrowserFamily != managedSession.BrowserFamily ||
                !string.Equals(identity.ExecutionProfile.ProfileId, managedSession.ProfileId,
                    StringComparison.Ordinal) ||
                request.OperationId != identity.OperationId || request.BatchId != identity.BatchId ||
                request.AccountId != identity.AccountId || request.Generation != identity.Generation ||
                attempt.OperationId != identity.OperationId || attempt.AccountId != identity.AccountId ||
                attempt.Generation != identity.Generation || attempt.OwnerKind != WebAutomationOwnerKind.Transfer ||
                attempt.State != WebAutomationAttemptState.Preparing || source is null ||
                source.Capability != request.Capability || !SameSourceEvidence(source, validatedSource) ||
                finalEvidence.FolderCandidates.Scope != TransferDirectoryListingScope.FolderCandidates ||
                !finalEvidence.FolderCandidates.IsComplete || !finalEvidence.FolderCandidates.IsStable ||
                !finalEvidence.FolderCandidates.ReachedEnd ||
                finalEvidence.FolderCandidates.TotalCount != finalEvidence.FolderCandidates.DetailedItems.Count ||
                finalEvidence.FolderCandidates.Directory != destination ||
                !string.Equals(snapshot.ParentExactId, destination.RemoteId, StringComparison.Ordinal) ||
                !string.Equals(snapshot.CanonicalPath, destination.Path.Value, StringComparison.Ordinal) ||
                !string.Equals(snapshot.BaiduUserId, source.BaiduUserId, StringComparison.Ordinal) ||
                !string.Equals(snapshot.ObservationFingerprint,
                    snapshot.ComputeFingerprint(request.SourceShare.OriginalUrl), StringComparison.Ordinal))
                throw new InvalidOperationException("Prepared destination evidence was not validated.");

            WebAutomationAttemptRecord.ValidateFingerprint(
                source.ObservationFingerprint, nameof(source.ObservationFingerprint));
            if (source.ObservationFingerprint is null)
                throw new InvalidOperationException("Prepared source evidence was not validated.");
            return new(identity, owningAttemptRevision, request.Capability, source.Items.Fingerprint,
                source.ObservationFingerprint, destination, snapshot);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            throw new InvalidOperationException("Prepared destination evidence was not validated.", error);
        }
    }

    internal Guid OperationId { get; }
    internal Guid AccountId { get; }
    internal int Generation { get; }
    internal long OwningAttemptRevision { get; }
    internal BaiduExecutionEngine ExecutionEngine { get; }
    internal AdapterCapabilityVersion Capability { get; }
    internal string SourceFingerprint { get; }
    internal string SourceObservationFingerprint { get; }
    internal RemoteDirectory Destination { get; }
    internal ManagedBrowserDirectorySnapshot FolderCandidatesSnapshot { get; }
    internal TransferDirectoryListingScope Scope => TransferDirectoryListingScope.FolderCandidates;

    public override string ToString() =>
        "ManagedPreparedDestinationEvidence { Engine = ManagedChromiumV1, " +
        "Scope = FolderCandidates, Evidence = [REDACTED] }";

    private static bool SameSourceEvidence(TransferSourceEvidence left, TransferSourceEvidence right) =>
        left.TotalCount == right.TotalCount && left.ReachedEnd == right.ReachedEnd &&
        left.IsStable == right.IsStable &&
        string.Equals(left.GenerationToken, right.GenerationToken, StringComparison.Ordinal) &&
        string.Equals(left.BaiduUserId, right.BaiduUserId, StringComparison.Ordinal) &&
        left.Capability == right.Capability &&
        string.Equals(left.ObservationFingerprint, right.ObservationFingerprint, StringComparison.Ordinal) &&
        left.Items.Items.Zip(right.Items.Items, (expected, actual) =>
            string.Equals(expected.ExactId, actual.ExactId, StringComparison.Ordinal) &&
            string.Equals(expected.DisplayedTopLevelName, actual.DisplayedTopLevelName,
                StringComparison.Ordinal)).All(equal => equal) &&
        left.Items.Count == right.Items.Count;
}

internal sealed class ManagedTransferDirectoryPreparationResult
{
    internal ManagedTransferDirectoryPreparationResult(TransferDirectoryPreparationOutcome outcome,
        ManagedTransferDirectoryPreparationQuarantine? quarantine = null,
        ManagedPreparedDestinationEvidence? preparedDestination = null,
        ManagedBrowserSourceSnapshot? validatedSourceSnapshot = null)
    {
        Outcome = outcome ?? throw new ArgumentNullException(nameof(outcome));
        var isPrepared = outcome.Status == TransferDirectoryPreparationStatus.Prepared;
        if (isPrepared != (preparedDestination is not null) ||
            isPrepared != (validatedSourceSnapshot is not null) ||
            preparedDestination is not null && (preparedDestination.Destination != outcome.Directory ||
                quarantine is not null ||
                !string.Equals(preparedDestination.SourceObservationFingerprint,
                    validatedSourceSnapshot!.ObservationFingerprint, StringComparison.Ordinal) ||
                !string.Equals(preparedDestination.FolderCandidatesSnapshot.BaiduUserId,
                    validatedSourceSnapshot.BaiduUserId, StringComparison.Ordinal) ||
                !string.Equals(preparedDestination.SourceFingerprint,
                    new RemoteItemIdentitySet(validatedSourceSnapshot.Items.Select(item =>
                        new RemoteItemIdentity(item.ExactId, item.ServerName))).Fingerprint,
                    StringComparison.Ordinal)))
            throw new ArgumentException("Only an exact prepared outcome carries validated source and destination evidence.",
                nameof(preparedDestination));
        Quarantine = quarantine;
        PreparedDestination = preparedDestination;
        ValidatedSourceSnapshot = validatedSourceSnapshot;
    }

    internal TransferDirectoryPreparationOutcome Outcome { get; }
    internal ManagedTransferDirectoryPreparationQuarantine? Quarantine { get; }
    internal ManagedPreparedDestinationEvidence? PreparedDestination { get; }
    internal ManagedBrowserSourceSnapshot? ValidatedSourceSnapshot { get; }
    public override string ToString() =>
        $"ManagedTransferDirectoryPreparationResult {{ Outcome = {Outcome.Status}, Quarantine = {(Quarantine is null ? "None" : "Held")}, PreparedDestination = {(PreparedDestination is null ? "None" : "Held")}, ValidatedSource = {(ValidatedSourceSnapshot is null ? "None" : "Held")}, Evidence = [REDACTED] }}";
}

internal enum ManagedTransferDirectoryPreparationReconciliationStatus
{
    Released = 1,
    RetainedCancelled,
    RetainedTimedOut,
    RetainedFailed,
    RetainedInconclusive,
    AlreadyInProgress,
    AlreadyReleased,
}

/// <summary>
/// Opaque, non-disposable process-local containment. Reconciliation is fixed and read-only; it never retries a
/// mutation, resolves durable business evidence, or synthesizes success. Durable OutcomeUnknown remains the
/// restart-safe write blocker. Production wiring must reconstruct ResultPending/quarantine ownership after restart.
/// </summary>
internal sealed class ManagedTransferDirectoryPreparationQuarantine
{
    private readonly ManagedTransferDirectoryPreparationSession session;
    private readonly ITransferDirectoryPreparationEvidenceStore store;
    private IDisposable? accountLease;
    private int state;

    internal ManagedTransferDirectoryPreparationQuarantine(ManagedTransferDirectoryPreparationSession session,
        ITransferDirectoryPreparationEvidenceStore store, IDisposable accountLease)
    {
        this.session = session;
        this.store = store;
        this.accountLease = accountLease;
    }

    internal async Task<ManagedTransferDirectoryPreparationReconciliationStatus> ReconcileAsync(
        int timeoutMilliseconds, CancellationToken cancellationToken = default)
    {
        var prior = Interlocked.CompareExchange(ref state, 1, 0);
        if (prior == 1) return ManagedTransferDirectoryPreparationReconciliationStatus.AlreadyInProgress;
        if (prior == 2) return ManagedTransferDirectoryPreparationReconciliationStatus.AlreadyReleased;
        if (timeoutMilliseconds is < 1 or > ManagedBrowserProtocol.MaximumOperationTimeoutMilliseconds)
            return Retain(ManagedTransferDirectoryPreparationReconciliationStatus.RetainedInconclusive);
        try
        {
            if (!await session.ReconcileReadOnlyAsync(store, timeoutMilliseconds, cancellationToken)
                    .ConfigureAwait(false))
                return Retain(ManagedTransferDirectoryPreparationReconciliationStatus.RetainedInconclusive);
            var owned = accountLease;
            if (owned is null)
            {
                Volatile.Write(ref state, 2);
                return ManagedTransferDirectoryPreparationReconciliationStatus.AlreadyReleased;
            }
            owned.Dispose();
            accountLease = null;
            Volatile.Write(ref state, 2);
            return ManagedTransferDirectoryPreparationReconciliationStatus.Released;
        }
        catch (OperationCanceledException)
        {
            return Retain(cancellationToken.IsCancellationRequested
                ? ManagedTransferDirectoryPreparationReconciliationStatus.RetainedCancelled
                : ManagedTransferDirectoryPreparationReconciliationStatus.RetainedTimedOut);
        }
        catch (TimeoutException)
        {
            return Retain(ManagedTransferDirectoryPreparationReconciliationStatus.RetainedTimedOut);
        }
        catch (Exception)
        {
            return Retain(ManagedTransferDirectoryPreparationReconciliationStatus.RetainedFailed);
        }

        ManagedTransferDirectoryPreparationReconciliationStatus Retain(
            ManagedTransferDirectoryPreparationReconciliationStatus status)
        {
            Volatile.Write(ref state, 0);
            return status;
        }
    }

    public override string ToString() =>
        $"ManagedTransferDirectoryPreparationQuarantine {{ State = {(Volatile.Read(ref state) == 2 ? "Reconciled" : "Held")}, Evidence = [REDACTED] }}";
}

/// <summary>Offline-only runner; no DI, UI, final-save or transfer wiring is provided by this slice.</summary>
internal sealed class ManagedTransferDirectoryPreparationRunner
{
    private readonly ManagedBrowserLifecycleCoordinator lifecycle;
    private readonly ManagedChromiumProcessManager manager;
    private readonly Func<ManagedBrowserRequest, CancellationToken, Task<ManagedBrowserResponse>> send;
    private readonly IBaiduCandidateActionAuthorizationSource authorization;
    private readonly TimeProvider timeProvider;

    internal ManagedTransferDirectoryPreparationRunner(ManagedBrowserLifecycleCoordinator lifecycle,
        ManagedChromiumProcessManager manager,
        Func<ManagedBrowserRequest, CancellationToken, Task<ManagedBrowserResponse>> send,
        IBaiduCandidateActionAuthorizationSource authorization, TimeProvider? timeProvider = null)
    {
        this.lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        this.manager = manager ?? throw new ArgumentNullException(nameof(manager));
        this.send = send ?? throw new ArgumentNullException(nameof(send));
        this.authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal async Task<ManagedTransferDirectoryPreparationResult> RunAsync(
        WebAutomationRequestIdentity identity, BaiduCandidateSessionBinding candidateBinding,
        BaiduExecutionContext executionContext, ManagedChromiumSessionHandle exactHandle,
        string canonicalSourceUri, int timeoutMilliseconds,
        ITransferDirectoryPreparationEvidenceStore store, TransferDirectoryPreparationRequest request,
        CancellationToken cancellationToken = default)
    {
        ManagedTransferOperationDeadline? deadline = null;
        try { deadline = new(timeoutMilliseconds, cancellationToken, timeProvider); }
        catch (ArgumentOutOfRangeException) { return Denied(); }
        using (deadline)
        {
            if (!ValidateInputs(identity, candidateBinding, canonicalSourceUri, store, request))
                return Denied();
            var operationStore = new DeadlineEvidenceStore(store, deadline);

            TransferSourceEvidence? persistedSource;
            try
            {
                deadline.RequireBudget();
                var initialAttempt = await operationStore.GetAttemptAsync(request, deadline.Token)
                    .WaitAsync(deadline.Token).ConfigureAwait(false);
                deadline.RequireBudget();
                persistedSource = initialAttempt?.SourceEvidence;
            }
            catch (OperationCanceledException) { return Denied(); }
            catch (TimeoutException) { return Denied(); }

            var picker = new ManagedTransferPickerSession(lifecycle, manager, send, authorization, timeProvider);
            var heldResult = await picker.BeginHeldPreparationUnderDeadlineAsync(identity, candidateBinding, executionContext,
                exactHandle, canonicalSourceUri, deadline).ConfigureAwait(false);
            if (heldResult.Kind != ManagedTransferPickerHeldPreparationKind.ReadyNotOpened ||
                heldResult.Preparation is not { } held)
                return Denied();

            IDisposable? lease = null;
            try
            {
                lease = held.ConsumeAccountLease();
                var session = new ManagedTransferDirectoryPreparationSession(lifecycle, manager, send,
                    authorization, held, persistedSource, () => operationStore.LatestSourceEvidence,
                    operationStore, timeProvider);
                TransferDirectoryPreparationOutcome outcome;
                try
                {
                    outcome = await new TransferDirectoryPreparationFlow(timeProvider)
                        .ExecuteAsync(session, operationStore, request, deadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    outcome = TransferDirectoryPreparationOutcome.NeedsAttention(
                        session.ConfirmedPath, BaiduErrorKind.UnknownResponse);
                }
                catch (TimeoutException)
                {
                    outcome = TransferDirectoryPreparationOutcome.NeedsAttention(
                        session.ConfirmedPath, BaiduErrorKind.UnknownResponse);
                }
                catch (Exception)
                {
                    outcome = TransferDirectoryPreparationOutcome.NeedsAttention(
                        session.ConfirmedPath, BaiduErrorKind.UnknownResponse);
                }

                ManagedPreparedDestinationEvidence? preparedDestination = null;
                if (outcome.Status == TransferDirectoryPreparationStatus.Prepared)
                {
                    var owningAttempt = operationStore.LatestAttempt ??
                        throw new InvalidOperationException("The owning attempt was not validated.");
                    preparedDestination = session.CreatePreparedDestinationEvidence(
                        outcome, request, owningAttempt);
                }

                if (outcome.Status == TransferDirectoryPreparationStatus.NeedsAttention &&
                    session.HasPossibleRemoteMutation && operationStore.ConfirmedResolutionFailed &&
                    !session.RequiresQuarantine)
                    session.MarkDurableResolutionBoundary();
                if (!session.RequiresQuarantine && session.PickerOpen)
                    await session.TryCancelForCleanupAsync(deadline.Token).ConfigureAwait(false);
                if (!session.RequiresQuarantine)
                {
                    session.TrackSettlement(operationStore.Completion);
                    try
                    {
                        await session.WaitForSettlementAsync(deadline.Token).ConfigureAwait(false);
                        lease.Dispose();
                        lease = null;
                    }
                    catch (Exception error) when (error is OperationCanceledException or TimeoutException)
                    {
                        _ = ReleaseAfterSettlementAsync(lease!, session);
                        lease = null;
                    }
                    return new(outcome, preparedDestination: preparedDestination,
                        validatedSourceSnapshot: preparedDestination is null
                            ? null
                            : session.ValidatedSourceSnapshot);
                }

                try { await session.CaptureQuarantineIdentityAsync(operationStore, deadline.Token).ConfigureAwait(false); }
                catch (Exception) { session.MarkQuarantineIdentityUnavailable(); }
                session.TrackSettlement(operationStore.Completion);
                var quarantine = new ManagedTransferDirectoryPreparationQuarantine(session, store, lease);
                lease = null;
                return new(outcome, quarantine);
            }
            catch (OperationCanceledException)
            {
                if (lease is not null)
                {
                    try { lease.Dispose(); }
                    catch (Exception) { }
                }
                return Denied();
            }
            catch (TimeoutException)
            {
                if (lease is not null)
                {
                    try { lease.Dispose(); }
                    catch (Exception) { }
                }
                return Denied();
            }
            catch (Exception)
            {
                if (lease is not null)
                {
                    try { lease.Dispose(); }
                    catch (Exception) { }
                }
                return Denied();
            }
            finally
            {
                held.ReleaseIfUnconsumed();
            }
        }

        ManagedTransferDirectoryPreparationResult Denied() => new(
            TransferDirectoryPreparationOutcome.NeedsAttention(new RemotePath("/"),
                BaiduErrorKind.NotValidated));
    }

    private static async Task ReleaseAfterSettlementAsync(IDisposable lease,
        ManagedTransferDirectoryPreparationSession session)
    {
        try { await session.WaitForSettlementAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception) { }
        try { lease.Dispose(); }
        catch (Exception) { }
    }

    private sealed class DeadlineEvidenceStore(
        ITransferDirectoryPreparationEvidenceStore inner,
        ManagedTransferOperationDeadline deadline) : ITransferDirectoryPreparationEvidenceStore
    {
        private readonly object gate = new();
        private readonly List<Task> settlements = [];
        internal TransferSourceEvidence? LatestSourceEvidence { get; private set; }
        internal WebAutomationAttemptRecord? LatestAttempt { get; private set; }
        internal bool ConfirmedResolutionFailed { get; private set; }

        internal Task Completion
        {
            get
            {
                lock (gate) return settlements.Count == 0
                    ? Task.CompletedTask
                    : Task.WhenAll(settlements.ToArray());
            }
        }

        public async Task<WebAutomationAttemptRecord?> GetAttemptAsync(TransferDirectoryPreparationRequest request,
            CancellationToken cancellationToken)
        {
            var attempt = await Invoke(token => inner.GetAttemptAsync(request, token)).ConfigureAwait(false);
            LatestAttempt = attempt;
            LatestSourceEvidence = attempt?.SourceEvidence;
            return attempt;
        }

        public Task<IReadOnlyList<TransferDirectoryMutationRecord>> GetUnresolvedMutationsAsync(Guid operationId,
            Guid accountId, CancellationToken cancellationToken) =>
            Invoke(token => inner.GetUnresolvedMutationsAsync(operationId, accountId, token));

        public Task<TransferDirectoryRecoveryAuthorization?> AuthorizeRecoveryAsync(
            TransferDirectoryRecoveryAuthorizationRequest request, CancellationToken cancellationToken) =>
            Invoke(token => inner.AuthorizeRecoveryAsync(request, token));

        public Task<bool> PersistSourceEvidenceAsync(TransferDirectoryPreparationRequest request,
            long expectedRevision, TransferSourceEvidence evidence, DateTimeOffset updatedAt,
            CancellationToken cancellationToken) =>
            Invoke(token => inner.PersistSourceEvidenceAsync(request, expectedRevision, evidence, updatedAt, token));

        public Task<bool> BeginMutationAsync(TransferDirectoryMutationRecord mutation, DateTimeOffset updatedAt,
            CancellationToken cancellationToken) =>
            Invoke(token => inner.BeginMutationAsync(mutation, updatedAt, token));

        public async Task<bool> ResolveMutationAsync(TransferDirectoryMutationRecord expected,
            TransferDirectoryMutationState nextState, string? childExactId, DateTimeOffset updatedAt,
            CancellationToken cancellationToken)
        {
            try
            {
                if (nextState == TransferDirectoryMutationState.OutcomeUnknown)
                {
                    var pending = inner.ResolveMutationAsync(
                        expected, nextState, childExactId, updatedAt, CancellationToken.None);
                    lock (gate) settlements.Add(Observe(pending));
                    return await pending.ConfigureAwait(false);
                }
                var resolved = await Invoke(token => inner.ResolveMutationAsync(
                    expected, nextState, childExactId, updatedAt, token)).ConfigureAwait(false);
                if (nextState == TransferDirectoryMutationState.Confirmed && !resolved)
                    ConfirmedResolutionFailed = true;
                return resolved;
            }
            catch
            {
                if (nextState == TransferDirectoryMutationState.Confirmed)
                    ConfirmedResolutionFailed = true;
                throw;
            }
        }

        private async Task<T> Invoke<T>(Func<CancellationToken, Task<T>> action)
        {
            deadline.RequireBudget();
            var pending = action(deadline.Token);
            lock (gate) settlements.Add(Observe(pending));
            var result = await pending.WaitAsync(deadline.Token).ConfigureAwait(false);
            deadline.RequireBudget();
            return result;
        }

        private static async Task Observe(Task pending)
        {
            try { await pending.ConfigureAwait(false); }
            catch (Exception) { }
        }
    }

    private bool ValidateInputs(WebAutomationRequestIdentity identity,
        BaiduCandidateSessionBinding candidateBinding, string canonicalSourceUri,
        ITransferDirectoryPreparationEvidenceStore store, TransferDirectoryPreparationRequest request)
    {
        if (identity is null || candidateBinding is null || store is null || request is null) return false;
        try
        {
            identity.Validate();
            request.Validate();
            candidateBinding.ValidateIdentity(identity);
            var now = timeProvider.GetUtcNow();
            return request.OperationId == identity.OperationId && request.BatchId == identity.BatchId &&
                request.AccountId == identity.AccountId && request.Generation == identity.Generation &&
                request.Capability == candidateBinding.Binding.Capability &&
                string.Equals(request.SourceShare.OriginalUrl, canonicalSourceUri, StringComparison.Ordinal) &&
                !candidateBinding.Authorization.IsRecovery && !candidateBinding.Binding.IsRecovery &&
                candidateBinding.Binding.IssuedAtUtc <= now && candidateBinding.Binding.ExpiresAtUtc > now &&
                candidateBinding.Binding.ExpiresAtUtc > candidateBinding.Binding.IssuedAtUtc;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return false; }
    }
}

/// <summary>
/// Lease-neutral implementation of the existing evidence workflow. Account ownership is supplied and retained by
/// its runner/quarantine; this type owns only action permits and their observable settlement tasks.
/// </summary>
internal sealed class ManagedTransferDirectoryPreparationSession : ITransferDirectoryPreparationSession
{
    private readonly ManagedBrowserLifecycleCoordinator lifecycle;
    private readonly ManagedChromiumProcessManager manager;
    private readonly Func<ManagedBrowserRequest, CancellationToken, Task<ManagedBrowserResponse>> send;
    private readonly IBaiduCandidateActionAuthorizationSource authorization;
    private readonly ManagedTransferPickerHeldPreparation held;
    private readonly TransferSourceEvidence? persistedSource;
    private readonly Func<TransferSourceEvidence?> latestSourceEvidence;
    private readonly ITransferDirectoryPreparationEvidenceStore store;
    private readonly ManagedTransferOperationDeadline deadline;
    private readonly TimeProvider timeProvider;
    private readonly List<Task> settlements = [];
    private ManagedTransferDirectoryMutationState mutationState = ManagedTransferDirectoryMutationState.None;
    private BaiduTransferCandidateObservation? sourceObservation;
    private ManagedBrowserSourceSnapshot? originalSource;
    private ManagedTransferDirectoryEvidence? current;
    private string? pendingSegment;
    private ReconciliationBoundary boundary;
    private ManagedTransferDirectoryEvidence? boundaryParent;
    private RemoteDirectory? boundaryChild;
    private TransferDirectoryMutationRecord? quarantineMutation;
    private bool quarantineIdentityCaptured;
    private bool quarantineIdentityAvailable;

    internal ManagedTransferDirectoryPreparationSession(ManagedBrowserLifecycleCoordinator lifecycle,
        ManagedChromiumProcessManager manager,
        Func<ManagedBrowserRequest, CancellationToken, Task<ManagedBrowserResponse>> send,
        IBaiduCandidateActionAuthorizationSource authorization, ManagedTransferPickerHeldPreparation held,
        TransferSourceEvidence? persistedSource = null,
        Func<TransferSourceEvidence?>? latestSourceEvidence = null,
        ITransferDirectoryPreparationEvidenceStore? store = null,
        TimeProvider? timeProvider = null)
    {
        this.lifecycle = lifecycle;
        this.manager = manager;
        this.send = send;
        this.authorization = authorization;
        this.held = held;
        this.persistedSource = persistedSource;
        this.latestSourceEvidence = latestSourceEvidence ?? (() => null);
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        deadline = held.Deadline;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal bool PickerOpen => mutationState.PickerOpen;
    internal bool HasPossibleRemoteMutation => mutationState.PossibleRemoteMutation;
    internal bool RequiresQuarantine { get; private set; }
    internal RemotePath ConfirmedPath => current?.FolderCandidates.Directory?.Path ?? new RemotePath("/");
    internal ManagedBrowserSourceSnapshot ValidatedSourceSnapshot =>
        originalSource ?? throw new InvalidOperationException("Prepared source state was not validated.");

    internal ManagedPreparedDestinationEvidence CreatePreparedDestinationEvidence(
        TransferDirectoryPreparationOutcome outcome, TransferDirectoryPreparationRequest request,
        WebAutomationAttemptRecord owningAttempt)
    {
        if (RequiresQuarantine || mutationState.PickerOpen || boundary != ReconciliationBoundary.None ||
            current is null)
            throw new InvalidOperationException("Prepared destination state was not validated.");
        return ManagedPreparedDestinationEvidence.CreateFromValidatedState(
            held.Identity, held.ExecutionContext, request, owningAttempt,
            ToSourceEvidence(sourceObservation ?? throw new InvalidOperationException(
                "Prepared source state was not validated.")), owningAttempt.Revision, outcome, current);
    }

    public Task OpenSourceAsync(ShareReference share, CancellationToken cancellationToken)
    {
        RequireAuthorization();
        if (share is null || !string.Equals(share.OriginalUrl, held.Binding.SourceUri, StringComparison.Ordinal))
            throw new InvalidOperationException("Source evidence was not validated.");
        return Task.CompletedTask;
    }

    public async Task EnterExtractionCodeAsync(string extractionCode, CancellationToken cancellationToken)
    {
        RequireAuthorization();
        if (extractionCode is null || extractionCode.Length != 4 ||
            extractionCode.Any(character => !char.IsAsciiLetterOrDigit(character)))
            throw new BaiduAutomationNotValidatedException(BaiduAutomationDenialReason.ActionNotAllowedForState,
                "The managed extraction code was not validated.");

        var read = new BaiduCandidateDomReadRequest(
            new Uri(held.Binding.SourceUri), BaiduCandidatePageState.ExtractionCode,
            held.Binding.ExpectedBaiduUserId, [], null, null);
        var setRequest = new BaiduCandidateDomMutationRequest(
            held.CandidateBinding.Authorization, held.CandidateBinding.Binding,
            BaiduAutomationAction.SetExtractionCode, read, extractionCode);
        var submitRequest = new BaiduCandidateDomMutationRequest(
            held.CandidateBinding.Authorization, held.CandidateBinding.Binding,
            BaiduAutomationAction.SubmitExtraction, read, null);
        IBaiduCandidateMutationPermit? setPermit = null;
        IBaiduCandidateMutationPermit? submitPermit = null;
        var dispatched = false;
        try
        {
            setPermit = await authorization.TryAcquireTransferMutationPermitAsync(
                setRequest, deadline.Token).ConfigureAwait(false);
            deadline.RequireBudget();
            submitPermit = await authorization.TryAcquireTransferMutationPermitAsync(
                submitRequest, deadline.Token).ConfigureAwait(false);
            deadline.RequireBudget();
            if (setPermit is null || submitPermit is null ||
                !await VerifyRuntimeAsync(deadline.Token).ConfigureAwait(false))
                throw new BaiduAutomationNotValidatedException(BaiduAutomationDenialReason.ActionNotAllowedForState,
                    "Managed extraction authorization was not validated.");

            var auth = held.CandidateBinding.Authorization;
            var command = new ManagedBrowserRequest(
                Guid.NewGuid(), auth.OperationId, auth.AccountId, auth.Generation,
                ManagedBrowserAction.SubmitExtractionCode,
                new ManagedBrowserSubmitExtractionCodeRequest(RemainingBinding(), extractionCode));
            dispatched = true;
            var response = await send(command, deadline.Token).ConfigureAwait(false);
            deadline.RequireBudget();
            ManagedBrowserProtocol.ValidateResponseFor(command, response);
            if (response.Result is ManagedBrowserSubmitExtractionCodeResult
                {
                    Kind: ManagedBrowserExtractionCodeResultKind.SourceOpened,
                    DispatchState: ManagedBrowserExtractionDispatchState.ClickDispatched,
                } && await VerifyRuntimeAsync(deadline.Token).ConfigureAwait(false))
                return;
            if (response.Result is ManagedBrowserSubmitExtractionCodeResult
                {
                    Kind: ManagedBrowserExtractionCodeResultKind.OutcomeUnknown,
                })
            {
                RequiresQuarantine = true;
                mutationState = mutationState with { PossibleRemoteMutation = true };
            }
            throw new BaiduAutomationNotValidatedException(BaiduAutomationDenialReason.ActionNotAllowedForState,
                "Managed extraction result was not validated.");
        }
        catch (BaiduAutomationNotValidatedException) { throw; }
        catch (Exception)
        {
            if (dispatched)
            {
                RequiresQuarantine = true;
                mutationState = mutationState with { PossibleRemoteMutation = true };
            }
            throw new BaiduAutomationNotValidatedException(BaiduAutomationDenialReason.ActionNotAllowedForState,
                "Managed extraction result was not validated.");
        }
        finally
        {
            if (submitPermit is not null) await submitPermit.DisposeAsync().ConfigureAwait(false);
            if (setPermit is not null) await setPermit.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<TransferSourceListing> ReadCompleteSourceAsync(CancellationToken cancellationToken)
    {
        await EnsureSourceAsync(cancellationToken).ConfigureAwait(false);
        return new(ToSourceEvidence(sourceObservation!));
    }

    public async Task<TransferDirectoryListing> OpenDestinationPickerAsync(CancellationToken cancellationToken)
    {
        await EnsureSourceAsync(cancellationToken).ConfigureAwait(false);
        RequireAuthorization();
        var read = new BaiduCandidateDomReadRequest(new Uri(held.Binding.SourceUri),
            BaiduCandidatePageState.ExternalShare, sourceObservation!.BaiduUserId,
            sourceObservation.Items.Select(item => item.ExactId), null, null);
        var request = new BaiduCandidateDomMutationRequest(held.CandidateBinding.Authorization,
            held.CandidateBinding.Binding, BaiduAutomationAction.OpenDestinationPicker, read, null);
        boundary = ReconciliationBoundary.PickerOpen;
        var opened = await new ManagedTransferPickerOpener(send, authorization,
                token => VerifyRuntimeAsync(token), timeProvider)
            .OpenAsync(request, RemainingBinding(), sourceObservation, deadline.Token).ConfigureAwait(false);
        deadline.RequireBudget();
        if (opened.Kind != ManagedBrowserTransferPickerResultKind.PickerOpened)
        {
            RequiresQuarantine = opened.Kind == ManagedBrowserTransferPickerResultKind.OutcomeUnknown;
            throw new InvalidOperationException("Picker opening was not validated.");
        }
        mutationState = new(true, false, false);
        try
        {
            if (!await VerifyRuntimeAsync(deadline.Token).ConfigureAwait(false))
                throw new InvalidOperationException("Picker runtime proof was not validated.");
            current = await ReadDirectoryAsync(null, null, deadline.Token).ConfigureAwait(false);
            if (!await VerifyRuntimeAsync(deadline.Token).ConfigureAwait(false))
                throw new InvalidOperationException("Picker final runtime proof was not validated.");
        }
        catch
        {
            RequiresQuarantine = true;
            throw;
        }
        boundary = ReconciliationBoundary.None;
        return current.FolderCandidates;
    }

    public async Task<TransferDirectoryListing> OpenVerifiedRootAsync(CancellationToken cancellationToken)
    {
        RequireCurrent();
        RequireAuthorization();
        boundary = ReconciliationBoundary.Root;
        boundaryParent = current;
        var result = await Mutator().OpenRootAsync(held.Identity, held.CandidateBinding, RemainingBinding(), current!,
            mutationState, Runtime, deadline.Token).ConfigureAwait(false);
        await AcceptSettlementAsync(result, deadline.Token).ConfigureAwait(false);
        if (result.Outcome != ManagedTransferDirectoryMutationOutcome.RootOpened || result.Evidence is null)
            throw UnknownOrDenied(result);
        mutationState = result.State;
        current = result.Evidence;
        boundary = ReconciliationBoundary.None;
        return current.FolderCandidates;
    }

    public async Task<TransferDirectoryListing> EnterDirectoryAsync(RemoteDirectory directory,
        CancellationToken cancellationToken)
    {
        RequireCurrent();
        RequireAuthorization();
        boundary = ReconciliationBoundary.Enter;
        boundaryParent = current;
        boundaryChild = directory;
        var result = await Mutator().EnterDirectoryAsync(held.Identity, held.CandidateBinding, RemainingBinding(),
            current!, directory.RemoteId, mutationState, Runtime, deadline.Token).ConfigureAwait(false);
        await AcceptSettlementAsync(result, deadline.Token).ConfigureAwait(false);
        if (result.Outcome != ManagedTransferDirectoryMutationOutcome.DirectoryEntered || result.Evidence is null)
            throw UnknownOrDenied(result);
        mutationState = result.State;
        current = result.Evidence;
        boundary = ReconciliationBoundary.None;
        return current.FolderCandidates;
    }

    public async Task BeginCreateDirectoryAsync(string segment, CancellationToken cancellationToken)
    {
        RequireCurrent();
        RequireAuthorization();
        pendingSegment = segment;
        boundary = ReconciliationBoundary.BeginCreate;
        boundaryParent = current;
        await CaptureCreateIdentityAsync(segment, deadline.Token).ConfigureAwait(false);
        var result = await Mutator().BeginCreateAsync(held.Identity, held.CandidateBinding, RemainingBinding(),
            current!, segment, mutationState, Runtime, deadline.Token).ConfigureAwait(false);
        await AcceptSettlementAsync(result, deadline.Token).ConfigureAwait(false);
        mutationState = result.State;
        if (result.Outcome != ManagedTransferDirectoryMutationOutcome.CreateDialogOpened)
            throw UnknownOrDenied(result);
    }

    public async Task ConfirmDirectoryCreationAsync(CancellationToken cancellationToken)
    {
        RequireCurrent();
        RequireAuthorization();
        if (pendingSegment is null) throw new InvalidOperationException("Create intent is missing.");
        boundary = ReconciliationBoundary.ConfirmCreate;
        boundaryParent = current;
        var result = await Mutator().ConfirmCreateAsync(held.Identity, held.CandidateBinding, RemainingBinding(),
            current!, pendingSegment, mutationState, Runtime, deadline.Token).ConfigureAwait(false);
        await AcceptSettlementAsync(result, deadline.Token).ConfigureAwait(false);
        mutationState = result.State;
        if (result.Outcome != ManagedTransferDirectoryMutationOutcome.DirectoryCreated || result.Evidence is null)
            throw UnknownOrDenied(result);
        current = result.Evidence;
    }

    public Task<TransferDirectoryListing> ReadCurrentDirectoryAsync(CancellationToken cancellationToken)
    {
        RequireCurrent();
        RequireAuthorization();
        boundary = ReconciliationBoundary.None;
        return Task.FromResult(current!.FolderCandidates);
    }

    public async Task CancelDestinationPickerAsync(CancellationToken cancellationToken)
    {
        if (!await CancelCoreAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Picker closure was not validated.");
    }

    public Task<TransferDirectoryListing> ReadExactParentForRecoveryAsync(RemoteDirectory parent,
        IReadOnlyList<string> expectedAncestors, TransferDirectoryRecoveryAuthorization authorization,
        CancellationToken cancellationToken)
    {
        RequireCurrent();
        RequireAuthorization();
        if (authorization is null || parent is null || current!.FolderCandidates.Directory != parent ||
            !current.FolderCandidates.AncestorExactIds.SequenceEqual(expectedAncestors, StringComparer.Ordinal))
            throw new InvalidOperationException("Recovery parent is not already proven.");
        return Task.FromResult(current.FolderCandidates);
    }

    internal async Task TryCancelForCleanupAsync(CancellationToken cancellationToken)
    {
        if (!mutationState.PickerOpen || current is null || originalSource is null) return;
        try { _ = await CancelCoreAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception)
        {
            RequiresQuarantine = true;
            boundary = ReconciliationBoundary.Cancel;
        }
    }

    internal Task WaitForSettlementAsync(CancellationToken cancellationToken) =>
        Task.WhenAll(settlements.ToArray()).WaitAsync(cancellationToken);

    internal void TrackSettlement(Task settlement) => settlements.Add(settlement);

    internal void MarkDurableResolutionBoundary()
    {
        RequiresQuarantine = true;
        boundary = ReconciliationBoundary.DurableResolution;
        boundaryParent = current;
    }

    internal async Task CaptureQuarantineIdentityAsync(ITransferDirectoryPreparationEvidenceStore store,
        CancellationToken cancellationToken)
    {
        var initiallyCaptured = quarantineMutation;
        quarantineIdentityCaptured = true;
        quarantineIdentityAvailable = false;
        var unresolved = await store.GetUnresolvedMutationsAsync(
            held.Identity.OperationId, held.Identity.AccountId, cancellationToken).ConfigureAwait(false);
        if (boundary is not (ReconciliationBoundary.BeginCreate or ReconciliationBoundary.ConfirmCreate or
                ReconciliationBoundary.DurableResolution))
        {
            quarantineMutation = null;
            quarantineIdentityAvailable = unresolved.Count == 0;
            return;
        }
        quarantineMutation = null;
        if (boundaryParent is null || pendingSegment is null || initiallyCaptured is null) return;
        var expectedChildPath = Append(boundaryParent.Snapshot.CanonicalPath, pendingSegment);
        var expectedOrdinal = expectedChildPath.Split('/', StringSplitOptions.RemoveEmptyEntries).Length - 1;
        var matches = unresolved.Where(mutation =>
            mutation.OperationId == held.Identity.OperationId && mutation.AccountId == held.Identity.AccountId &&
            mutation.Generation == held.Identity.Generation && mutation.SegmentOrdinal == expectedOrdinal &&
            string.Equals(mutation.ParentExactId, boundaryParent.Snapshot.ParentExactId, StringComparison.Ordinal) &&
            mutation.CanonicalChildPath == new RemotePath(expectedChildPath) &&
            mutation.Capability == held.CandidateBinding.Binding.Capability &&
            string.Equals(mutation.BaiduUserId, held.Binding.ExpectedBaiduUserId, StringComparison.Ordinal) &&
            SameMutationIdentity(mutation, initiallyCaptured)).ToArray();
        if (unresolved.Count == 1 && matches.Length == 1)
        {
            quarantineMutation = initiallyCaptured;
            quarantineIdentityAvailable = true;
        }
    }

    internal void MarkQuarantineIdentityUnavailable()
    {
        quarantineIdentityCaptured = true;
        quarantineIdentityAvailable = false;
        quarantineMutation = null;
    }

    private async Task CaptureCreateIdentityAsync(string segment, CancellationToken cancellationToken)
    {
        quarantineIdentityCaptured = false;
        quarantineIdentityAvailable = false;
        quarantineMutation = null;
        if (boundaryParent is null) return;
        var expectedChildPath = Append(boundaryParent.Snapshot.CanonicalPath, segment);
        var expectedOrdinal = expectedChildPath.Split('/', StringSplitOptions.RemoveEmptyEntries).Length - 1;
        var unresolved = await store.GetUnresolvedMutationsAsync(
            held.Identity.OperationId, held.Identity.AccountId, cancellationToken).ConfigureAwait(false);
        var matches = unresolved.Where(mutation =>
            mutation.OperationId == held.Identity.OperationId && mutation.AccountId == held.Identity.AccountId &&
            mutation.Generation == held.Identity.Generation && mutation.SegmentOrdinal == expectedOrdinal &&
            string.Equals(mutation.ParentExactId, boundaryParent.Snapshot.ParentExactId, StringComparison.Ordinal) &&
            mutation.CanonicalChildPath == new RemotePath(expectedChildPath) &&
            mutation.Capability == held.CandidateBinding.Binding.Capability &&
            string.Equals(mutation.BaiduUserId, held.Binding.ExpectedBaiduUserId, StringComparison.Ordinal) &&
            mutation.State == TransferDirectoryMutationState.Intent).ToArray();
        quarantineIdentityCaptured = true;
        if (unresolved.Count == 1 && matches.Length == 1)
        {
            quarantineMutation = matches[0];
            quarantineIdentityAvailable = true;
        }
    }

    internal async Task<bool> ReconcileReadOnlyAsync(ITransferDirectoryPreparationEvidenceStore store,
        int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        using var reconciliationDeadline = new ManagedTransferOperationDeadline(
            timeoutMilliseconds, cancellationToken, timeProvider);
        await WaitForSettlementAsync(reconciliationDeadline.Token).ConfigureAwait(false);
        reconciliationDeadline.RequireBudget();
        if (!quarantineIdentityCaptured || !quarantineIdentityAvailable) return false;
        if (boundary == ReconciliationBoundary.Cancel)
        {
            if (originalSource is null) return false;
            var request = new ManagedBrowserRequest(Guid.NewGuid(), held.Identity.OperationId,
                held.Identity.AccountId, held.Identity.Generation, ManagedBrowserAction.ObserveTransferPickerClosed,
                new ManagedBrowserObserveTransferPickerClosedRequest(
                    reconciliationDeadline.RemainingBinding(held.Binding), originalSource));
            var pending = send(request, reconciliationDeadline.Token);
            settlements.Add(Observe(pending));
            var response = await pending.WaitAsync(reconciliationDeadline.Token).ConfigureAwait(false);
            reconciliationDeadline.RequireBudget();
            ManagedBrowserProtocol.ValidateResponseFor(request, response);
            if (response.Result is not ManagedBrowserTransferPickerClosedResult
                { Kind: ManagedBrowserTransferPickerClosedResultKind.PickerClosed }) return false;
            var runtimeProven = await VerifyRuntimeAsync(reconciliationDeadline.Token,
                reconciliationDeadline.RemainingBinding(held.Binding)).ConfigureAwait(false);
            reconciliationDeadline.RequireBudget();
            return runtimeProven && (await ReadUnresolvedAsync().ConfigureAwait(false)).Count == 0;
        }

        if (boundary is ReconciliationBoundary.Enter or ReconciliationBoundary.Root)
        {
            var unchanged = await ProbeDirectoryAsync(boundaryParent?.Snapshot.ParentExactId,
                boundaryParent?.Snapshot.CanonicalPath).ConfigureAwait(false);
            var allowed = SameDirectory(unchanged, boundaryParent);
            if (!allowed && boundary == ReconciliationBoundary.Root && boundaryParent is not null)
            {
                var root = await ProbeDirectoryAsync(boundaryParent.RootExactId, "/").ConfigureAwait(false);
                allowed = root is not null && root.FolderCandidates.Directory ==
                    new RemoteDirectory(boundaryParent.RootExactId, new RemotePath("/")) &&
                    root.RootInclusiveAncestorExactIds.SequenceEqual(
                        new[] { boundaryParent.RootExactId }, StringComparer.Ordinal);
            }
            if (!allowed && boundary == ReconciliationBoundary.Enter && boundaryParent is not null &&
                boundaryChild is not null)
            {
                var entered = await ProbeDirectoryAsync(
                    boundaryChild.RemoteId, boundaryChild.Path.Value).ConfigureAwait(false);
                allowed = entered is not null && entered.FolderCandidates.Directory == boundaryChild &&
                    entered.RootInclusiveAncestorExactIds.SequenceEqual(
                        boundaryParent.RootInclusiveAncestorExactIds.Append(boundaryChild.RemoteId),
                        StringComparer.Ordinal);
            }
            if (!allowed) return false;
            var unresolved = await ReadUnresolvedAsync().ConfigureAwait(false);
            return unresolved.Count == 0;
        }

        ManagedTransferDirectoryEvidence observed;
        try
        {
            observed = await ReadDirectoryAsync(boundaryParent?.Snapshot.ParentExactId,
                boundaryParent?.Snapshot.CanonicalPath, reconciliationDeadline.Token,
                reconciliationDeadline.RemainingBinding(held.Binding), SendReconciliationReadAsync).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            reconciliationDeadline.RequireBudget();
            throw;
        }
        reconciliationDeadline.RequireBudget();
        if (!await VerifyRuntimeAsync(reconciliationDeadline.Token,
                reconciliationDeadline.RemainingBinding(held.Binding)).ConfigureAwait(false)) return false;
        reconciliationDeadline.RequireBudget();
        if (boundary == ReconciliationBoundary.PickerOpen)
            return (await ReadUnresolvedAsync().ConfigureAwait(false)).Count == 0;
        if (boundary is ReconciliationBoundary.BeginCreate or ReconciliationBoundary.ConfirmCreate or
            ReconciliationBoundary.DurableResolution)
        {
            if (quarantineMutation is null ||
                !SameDirectory(observed, boundaryParent)) return false;
            var unresolved = await ReadUnresolvedAsync().ConfigureAwait(false);
            return unresolved.Count == 1 && SameMutationIdentity(unresolved[0], quarantineMutation) &&
                unresolved[0].State == TransferDirectoryMutationState.OutcomeUnknown;
        }
        return false;

        async Task<IReadOnlyList<TransferDirectoryMutationRecord>> ReadUnresolvedAsync()
        {
            reconciliationDeadline.RequireBudget();
            var pending = store.GetUnresolvedMutationsAsync(held.Identity.OperationId,
                held.Identity.AccountId, reconciliationDeadline.Token);
            settlements.Add(Observe(pending));
            var unresolved = await pending.WaitAsync(reconciliationDeadline.Token).ConfigureAwait(false);
            reconciliationDeadline.RequireBudget();
            return unresolved;
        }

        async Task<ManagedTransferDirectoryEvidence?> ProbeDirectoryAsync(string? parentExactId, string? path)
        {
            ManagedTransferDirectoryEvidence? evidence = null;
            try
            {
                evidence = await ReadDirectoryAsync(parentExactId, path, reconciliationDeadline.Token,
                    reconciliationDeadline.RemainingBinding(held.Binding), SendReconciliationReadAsync).ConfigureAwait(false);
            }
            catch (InvalidOperationException) { }
            reconciliationDeadline.RequireBudget();
            if (!await VerifyRuntimeAsync(reconciliationDeadline.Token,
                    reconciliationDeadline.RemainingBinding(held.Binding)).ConfigureAwait(false)) return null;
            reconciliationDeadline.RequireBudget();
            return evidence;
        }

        async Task<ManagedBrowserResponse> SendReconciliationReadAsync(
            ManagedBrowserRequest request, CancellationToken token)
        {
            reconciliationDeadline.RequireBudget();
            var pending = send(request, token);
            settlements.Add(Observe(pending));
            var response = await pending.WaitAsync(reconciliationDeadline.Token).ConfigureAwait(false);
            reconciliationDeadline.RequireBudget();
            return response;
        }
    }

    private async Task EnsureSourceAsync(CancellationToken cancellationToken)
    {
        if (sourceObservation is not null) return;
        RequireAuthorization();
        var source = await new ManagedTransferSourceSession(lifecycle, manager, send)
            .OpenAndReadUnderAccountLeaseAsync(held.Identity.OperationId, held.Identity.Generation,
                held.ExecutionContext, held.ExactHandle, held.Binding.SourceUri,
                deadline.RemainingMilliseconds(), deadline.Token).ConfigureAwait(false);
        settlements.Add(source.PendingSendsSettled);
        deadline.RequireBudget();
        if (source.ReadResult.Kind != ManagedBrowserSourceListingResultKind.Observed ||
            source.ReadResult.Observation is not { } observation)
            throw new InvalidOperationException("Source evidence was not validated.");
        var freshEvidence = ToSourceEvidence(observation);
        var expectedSource = persistedSource ?? latestSourceEvidence();
        if (expectedSource is not null && !SameSourceEvidence(expectedSource, freshEvidence))
            throw new InvalidOperationException("Persisted source evidence changed.");
        sourceObservation = observation;
        originalSource = new(observation.BaiduUserId, observation.GenerationToken, observation.TotalCount,
            observation.ReachedEnd, observation.IsStable, observation.Items.Select(item => new ManagedBrowserSourceItem(
                item.ExactId, item.Kind == RemoteResourceKind.Folder ? ManagedBrowserSourceItemKind.Folder :
                    ManagedBrowserSourceItemKind.File, item.ServerName)), observation.ObservationToken);
    }

    private async Task<bool> CancelCoreAsync(CancellationToken cancellationToken)
    {
        RequireCurrent();
        RequireAuthorization();
        if (originalSource is null) return false;
        boundary = ReconciliationBoundary.Cancel;
        var result = await Mutator().CancelDestinationPickerAsync(held.Identity, held.CandidateBinding,
            RemainingBinding(), current!, originalSource, mutationState, Runtime, deadline.Token).ConfigureAwait(false);
        await AcceptSettlementAsync(result, deadline.Token).ConfigureAwait(false);
        mutationState = result.State;
        if (result.Outcome != ManagedTransferDirectoryMutationOutcome.PickerClosed)
        {
            RequiresQuarantine = true;
            return false;
        }
        boundary = ReconciliationBoundary.None;
        return true;
    }

    private async Task<ManagedTransferDirectoryEvidence> ReadDirectoryAsync(string? parentId,
        string? path, CancellationToken cancellationToken,
        ManagedBrowserOpenTransferSourceRequest? exactBinding = null,
        Func<ManagedBrowserRequest, CancellationToken, Task<ManagedBrowserResponse>>? sendOverride = null)
    {
        if (exactBinding is null) deadline.RequireBudget();
        var result = await new ManagedTransferDirectoryReader(sendOverride ?? send).ReadAsync(held.Identity.OperationId,
            held.Identity.AccountId, held.Identity.Generation,
            new ManagedBrowserReadTransferDirectoryRequest(exactBinding ?? RemainingBinding(), parentId, path), cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (exactBinding is null) deadline.RequireBudget();
        return result.Kind == ManagedBrowserDirectoryListingResultKind.Observed && result.Evidence is not null
            ? result.Evidence
            : throw new InvalidOperationException("Directory evidence was not validated.");
    }

    private async Task AcceptSettlementAsync(ManagedTransferDirectoryMutationExecutionResult result,
        CancellationToken cancellationToken)
    {
        settlements.Add(result.PendingSettlement);
        try { await result.PendingSettlement.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            if (result.CurrentBoundaryUncertain || result.State.CreateUiOpen) RequiresQuarantine = true;
            throw;
        }
        deadline.RequireBudget();
    }

    private Exception UnknownOrDenied(ManagedTransferDirectoryMutationExecutionResult result)
    {
        if (result.CurrentBoundaryUncertain || result.State.CreateUiOpen)
            RequiresQuarantine = true;
        return new InvalidOperationException("Directory action was not validated.");
    }

    private TransferSourceEvidence ToSourceEvidence(BaiduTransferCandidateObservation observation) => new(
        new RemoteItemIdentitySet(observation.Items.Select(item => new RemoteItemIdentity(item.ExactId, item.ServerName))),
        observation.TotalCount, observation.ReachedEnd, observation.IsStable, observation.GenerationToken,
        observation.BaiduUserId, held.CandidateBinding.Binding.Capability, observation.ObservationToken);

    private ManagedTransferDirectoryMutator Mutator() => new(lifecycle, manager, send, authorization, timeProvider);
    private ManagedTransferDirectoryMutationRuntime Runtime => new(held.CapturedSession, held.ExactHandle);

    private async Task<bool> VerifyRuntimeAsync(CancellationToken cancellationToken,
        ManagedBrowserOpenTransferSourceRequest? exactBinding = null)
    {
        if (exactBinding is null)
        {
            if (!CandidateBindingIsCurrent()) return false;
            deadline.RequireBudget();
        }
        ManagedBrowserLifecycleSession? currentSession;
        try { currentSession = await lifecycle.LoadPersistedAsync(held.Identity.AccountId, cancellationToken).ConfigureAwait(false); }
        catch (InvalidOperationException) { return false; }
        if (currentSession is null || !SameSnapshot(currentSession, held.CapturedSession)) return false;
        var proven = await manager.VerifyTransferSourceAsync(
            held.ExactHandle, exactBinding ?? RemainingBinding(), cancellationToken).ConfigureAwait(false);
        if (exactBinding is null) deadline.RequireBudget();
        return proven;
    }

    private ManagedBrowserOpenTransferSourceRequest RemainingBinding() => deadline.RemainingBinding(held.Binding);

    private void RequireAuthorization()
    {
        deadline.RequireBudget();
        if (!CandidateBindingIsCurrent())
            throw new InvalidOperationException("The candidate binding is no longer current.");
    }

    private bool CandidateBindingIsCurrent()
    {
        var now = timeProvider.GetUtcNow();
        var binding = held.CandidateBinding.Binding;
        return !binding.IsRecovery && !held.CandidateBinding.Authorization.IsRecovery &&
            binding.IssuedAtUtc <= now && binding.ExpiresAtUtc > now && binding.ExpiresAtUtc > binding.IssuedAtUtc;
    }

    private void RequireCurrent()
    {
        if (current is null || !mutationState.PickerOpen)
            throw new InvalidOperationException("A proven current picker directory is required.");
    }

    private static bool SameDirectory(ManagedTransferDirectoryEvidence? left,
        ManagedTransferDirectoryEvidence? right) => left is not null && right is not null &&
        left.FolderCandidates.Directory == right.FolderCandidates.Directory &&
        left.RootInclusiveAncestorExactIds.SequenceEqual(right.RootInclusiveAncestorExactIds, StringComparer.Ordinal);

    private static bool SameSourceEvidence(TransferSourceEvidence left, TransferSourceEvidence right) =>
        left.TotalCount == right.TotalCount && left.ReachedEnd == right.ReachedEnd && left.IsStable == right.IsStable &&
        string.Equals(left.GenerationToken, right.GenerationToken, StringComparison.Ordinal) &&
        string.Equals(left.BaiduUserId, right.BaiduUserId, StringComparison.Ordinal) && left.Capability == right.Capability &&
        string.Equals(left.ObservationFingerprint, right.ObservationFingerprint, StringComparison.Ordinal) &&
        left.Items.Items.Zip(right.Items.Items, (expected, actual) =>
            string.Equals(expected.ExactId, actual.ExactId, StringComparison.Ordinal) &&
            string.Equals(expected.DisplayedTopLevelName, actual.DisplayedTopLevelName, StringComparison.Ordinal)).All(equal => equal) &&
        left.Items.Count == right.Items.Count;

    private static bool SameMutationIdentity(TransferDirectoryMutationRecord current,
        TransferDirectoryMutationRecord expected) =>
        current.OperationId == expected.OperationId && current.AccountId == expected.AccountId &&
        current.Generation == expected.Generation && current.SegmentOrdinal == expected.SegmentOrdinal &&
        string.Equals(current.ParentExactId, expected.ParentExactId, StringComparison.Ordinal) &&
        string.Equals(current.ChildExactId, expected.ChildExactId, StringComparison.Ordinal) &&
        current.CanonicalChildPath == expected.CanonicalChildPath &&
        string.Equals(current.ListingGenerationToken, expected.ListingGenerationToken, StringComparison.Ordinal) &&
        string.Equals(current.BaiduUserId, expected.BaiduUserId, StringComparison.Ordinal) &&
        current.Capability == expected.Capability && current.OwningAttemptRevision == expected.OwningAttemptRevision;

    private static string Append(string parent, string segment) =>
        parent == "/" ? "/" + segment : parent + "/" + segment;

    private static bool SameSnapshot(ManagedBrowserLifecycleSession left, ManagedBrowserLifecycleSession right) =>
        left.InstallationRevision == right.InstallationRevision && left.SessionRevision == right.SessionRevision &&
        SameIdentity(left.Session, right.Session) &&
        string.Equals(left.CanonicalProfilePath, right.CanonicalProfilePath, StringComparison.OrdinalIgnoreCase);

    private static bool SameIdentity(ManagedBrowserSessionReference left, ManagedBrowserSessionReference right) =>
        left.SessionId == right.SessionId && left.InstallationId == right.InstallationId &&
        left.AccountId == right.AccountId && left.BrowserFamily == right.BrowserFamily &&
        string.Equals(left.StableBaiduUserId, right.StableBaiduUserId, StringComparison.Ordinal) &&
        string.Equals(left.ProfileId, right.ProfileId, StringComparison.Ordinal) &&
        string.Equals(left.ProfileOwnershipId, right.ProfileOwnershipId, StringComparison.Ordinal);

    private static async Task Observe(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception) { }
    }

    private enum ReconciliationBoundary
    {
        None,
        PickerOpen,
        Root,
        Enter,
        BeginCreate,
        ConfirmCreate,
        DurableResolution,
        Cancel,
    }
}
