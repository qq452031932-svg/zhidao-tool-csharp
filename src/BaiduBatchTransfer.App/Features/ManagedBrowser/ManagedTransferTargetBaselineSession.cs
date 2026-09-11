using BaiduBatchTransfer.App.Features.BrowserAutomation;
using BaiduBatchTransfer.BaiduAdapter.WebAutomation;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using System.IO;

namespace BaiduBatchTransfer.App.Features.ManagedBrowser;

internal enum ManagedTransferTargetBaselineResultKind
{
    BaselinePersisted = 1,
    PageNotValidated,
    AccountIdentityMismatch,
    LoginRequired,
    VerificationRequired,
    BrowserUnavailable,
    PersistenceConflict,
    TimedOut,
    Cancelled,
    RouteQuarantined,
}

internal sealed class ManagedTransferTargetBaselineResult
{
    internal ManagedTransferTargetBaselineResult(ManagedTransferTargetBaselineResultKind kind,
        TransferTargetPreparationEvidence? evidence = null,
        ManagedTransferTargetBaselineQuarantine? quarantine = null)
    {
        if ((kind == ManagedTransferTargetBaselineResultKind.BaselinePersisted) != (evidence is not null) ||
            (kind == ManagedTransferTargetBaselineResultKind.RouteQuarantined) != (quarantine is not null))
            throw new ArgumentException("The target-baseline result payload is inconsistent.");
        Kind = kind;
        Evidence = evidence;
        Quarantine = quarantine;
    }

    internal ManagedTransferTargetBaselineResultKind Kind { get; }
    internal TransferTargetPreparationEvidence? Evidence { get; }
    internal ManagedTransferTargetBaselineQuarantine? Quarantine { get; }

    public override string ToString() =>
        $"ManagedTransferTargetBaselineResult {{ Kind = {Kind}, Evidence = [REDACTED], Quarantine = {(Quarantine is null ? "None" : "Held")} }}";
}

internal enum ManagedTransferTargetBaselineReconciliationStatus
{
    Released = 1,
    RetainedCancelled,
    RetainedTimedOut,
    RetainedFailed,
    RetainedInconclusive,
    AlreadyInProgress,
    AlreadyReleased,
}

internal sealed class ManagedTransferTargetBaselineQuarantine
{
    private readonly Task settlement;
    private readonly Func<int, CancellationToken, Task<bool>> reconcile;
    private readonly List<Task> cleanupSettlements;
    private IDisposable? lease;
    private int state;

    internal ManagedTransferTargetBaselineQuarantine(Task settlement,
        Func<int, CancellationToken, Task<bool>> reconcile, List<Task> cleanupSettlements,
        IDisposable lease)
    {
        this.settlement = settlement ?? throw new ArgumentNullException(nameof(settlement));
        this.reconcile = reconcile ?? throw new ArgumentNullException(nameof(reconcile));
        this.cleanupSettlements = cleanupSettlements ?? throw new ArgumentNullException(nameof(cleanupSettlements));
        this.lease = lease ?? throw new ArgumentNullException(nameof(lease));
    }

    internal async Task<ManagedTransferTargetBaselineReconciliationStatus> ReconcileAsync(
        int timeoutMilliseconds, CancellationToken cancellationToken = default)
    {
        var prior = Interlocked.CompareExchange(ref state, 1, 0);
        if (prior == 1) return ManagedTransferTargetBaselineReconciliationStatus.AlreadyInProgress;
        if (prior == 2) return ManagedTransferTargetBaselineReconciliationStatus.AlreadyReleased;
        if (timeoutMilliseconds is < 1 or > ManagedBrowserProtocol.MaximumOperationTimeoutMilliseconds)
            return Retain(ManagedTransferTargetBaselineReconciliationStatus.RetainedInconclusive);
        try
        {
            await settlement.WaitAsync(cancellationToken).ConfigureAwait(false);
            Task[] priorCleanup;
            lock (cleanupSettlements) priorCleanup = cleanupSettlements.ToArray();
            await ObserveAllAsync(priorCleanup).WaitAsync(cancellationToken).ConfigureAwait(false);
            if (!await reconcile(timeoutMilliseconds, cancellationToken).ConfigureAwait(false))
                return Retain(ManagedTransferTargetBaselineReconciliationStatus.RetainedInconclusive);
            var owned = lease;
            if (owned is null)
            {
                Volatile.Write(ref state, 2);
                return ManagedTransferTargetBaselineReconciliationStatus.AlreadyReleased;
            }
            owned.Dispose();
            lease = null;
            Volatile.Write(ref state, 2);
            return ManagedTransferTargetBaselineReconciliationStatus.Released;
        }
        catch (OperationCanceledException)
        {
            return Retain(cancellationToken.IsCancellationRequested
                ? ManagedTransferTargetBaselineReconciliationStatus.RetainedCancelled
                : ManagedTransferTargetBaselineReconciliationStatus.RetainedTimedOut);
        }

        catch (Exception)
        {
            return Retain(ManagedTransferTargetBaselineReconciliationStatus.RetainedFailed);
        }

        static async Task ObserveAllAsync(Task[] tasks)
        {
            try { await Task.WhenAll(tasks).ConfigureAwait(false); }
            catch (Exception) { }
        }

        ManagedTransferTargetBaselineReconciliationStatus Retain(
            ManagedTransferTargetBaselineReconciliationStatus status)
        {
            Volatile.Write(ref state, 0);
            return status;
        }
    }

    public override string ToString() =>
        $"ManagedTransferTargetBaselineQuarantine {{ State = {(Volatile.Read(ref state) == 2 ? "Reconciled" : "Held")}, Evidence = [REDACTED] }}";
}

/// <summary>Offline-only, one-lease target-baseline runner. This slice has no production DI or UI wiring.</summary>
internal sealed class ManagedTransferTargetBaselineSession
{
    private readonly Func<Guid, CancellationToken, Task<IDisposable>> acquireLease;
    private readonly Func<Guid, CancellationToken, Task<ManagedBrowserLifecycleSession?>> loadSession;
    private readonly Func<ManagedChromiumSessionHandle, int, CancellationToken, Task<bool>> verifyRuntime;
    private readonly Func<ManagedBrowserRequest, CancellationToken, Task<ManagedBrowserResponse>> send;
    private readonly IManagedTransferRouteProof routeProof;
    private readonly TimeProvider timeProvider;

    internal ManagedTransferTargetBaselineSession(
        ManagedBrowserLifecycleCoordinator lifecycle,
        ManagedChromiumProcessManager manager,
        Func<ManagedBrowserRequest, CancellationToken, Task<ManagedBrowserResponse>> send,
        IManagedTransferRouteProof routeProof,
        TimeProvider? timeProvider = null)
        : this(
            (accountId, token) => lifecycle.AcquireAccountLeaseAsync(accountId, token),
            (accountId, token) => lifecycle.LoadPersistedAsync(accountId, token),
            async (handle, _, token) =>
            {
                var ready = await manager.ReconnectAsync(handle, token).ConfigureAwait(false);
                return ready.Status == ManagedChromiumLaunchStatus.Ready &&
                    ReferenceEquals(ready.Session, handle);
            },
            send, routeProof, timeProvider)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(manager);
    }

    internal ManagedTransferTargetBaselineSession(
        Func<Guid, CancellationToken, Task<IDisposable>> acquireLease,
        Func<Guid, CancellationToken, Task<ManagedBrowserLifecycleSession?>> loadSession,
        Func<ManagedChromiumSessionHandle, int, CancellationToken, Task<bool>> verifyRuntime,
        Func<ManagedBrowserRequest, CancellationToken, Task<ManagedBrowserResponse>> send,
        IManagedTransferRouteProof routeProof,
        TimeProvider? timeProvider = null)
    {
        this.acquireLease = acquireLease ?? throw new ArgumentNullException(nameof(acquireLease));
        this.loadSession = loadSession ?? throw new ArgumentNullException(nameof(loadSession));
        this.verifyRuntime = verifyRuntime ?? throw new ArgumentNullException(nameof(verifyRuntime));
        this.send = send ?? throw new ArgumentNullException(nameof(send));
        this.routeProof = routeProof ?? throw new ArgumentNullException(nameof(routeProof));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal async Task<ManagedTransferTargetBaselineResult> RunAsync(
        WebAutomationRequestIdentity identity,
        BaiduCandidateSessionBinding candidateBinding,
        BaiduExecutionContext executionContext,
        ManagedChromiumSessionHandle exactHandle,
        ManagedBrowserSessionReference exactManagedSession,
        string canonicalSourceUri,
        ManagedBrowserSourceSnapshot immutableSourceEvidence,
        ManagedPreparedDestinationEvidence preparedDestination,
        long expectedAttemptRevision,
        int timeoutMilliseconds,
        IWebAutomationAttemptStore attemptStore,
        CancellationToken cancellationToken = default)
    {
        if (!TryValidateStaticInputs(identity, candidateBinding, executionContext, exactHandle,
                exactManagedSession, canonicalSourceUri, immutableSourceEvidence, preparedDestination,
                expectedAttemptRevision, timeoutMilliseconds, attemptStore))
            return Denied();

        using var deadline = new ManagedTransferOperationDeadline(
            timeoutMilliseconds, cancellationToken, timeProvider);
        WebAutomationAttemptRecord? attempt;
        try
        {
            deadline.RequireBudget();
            attempt = await attemptStore.GetAsync(identity.OperationId, identity.AccountId,
                    identity.Generation, deadline.Token).WaitAsync(deadline.Token)
                .ConfigureAwait(false);
            deadline.RequireBudget();
        }
        catch (OperationCanceledException)
        {
            return new(cancellationToken.IsCancellationRequested
                ? ManagedTransferTargetBaselineResultKind.Cancelled
                : ManagedTransferTargetBaselineResultKind.TimedOut);
        }
        catch (TimeoutException)
        {
            return new(ManagedTransferTargetBaselineResultKind.TimedOut);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return Denied();
        }
        catch (Exception)
        {
            return Denied();
        }

        if (!ExactPreparingAttempt(attempt, identity, immutableSourceEvidence,
                preparedDestination, expectedAttemptRevision))
            return Denied();

        IDisposable? lease = null;
        var pending = new List<Task>();
        var cleanupSettlements = new List<Task>();
        var routeMayHaveChanged = false;
        var persistenceStarted = false;
        TransferTargetPreparationEvidence? targetEvidence = null;
        ManagedBrowserTargetDirectoryHeader? lastTargetHeader = null;
        ManagedTransferRouteExpectation? possibleTargetRoute = null;
        DateTimeOffset? persistedAt = null;
        ManagedBrowserLifecycleSession? capturedSession = null;
        try
        {
            deadline.RequireBudget();
            var leaseAcquisition = acquireLease(identity.AccountId, deadline.Token);
            try
            {
                lease = await leaseAcquisition.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                _ = DisposeLateLeaseAsync(leaseAcquisition);
                throw;
            }
            deadline.RequireBudget();
            var gatedRead = attemptStore.GetAsync(identity.OperationId, identity.AccountId,
                identity.Generation, deadline.Token);
            pending.Add(gatedRead);
            var gatedAttempt = await gatedRead.WaitAsync(deadline.Token).ConfigureAwait(false);
            deadline.RequireBudget();
            if (!ExactPreparingAttempt(gatedAttempt, identity, immutableSourceEvidence,
                    preparedDestination, expectedAttemptRevision) || !SameAttempt(attempt, gatedAttempt))
                return Finish(Denied());
            attempt = gatedAttempt;
            capturedSession = await LoadExactSessionAsync(identity.AccountId, exactManagedSession,
                exactHandle, null, deadline, pending).ConfigureAwait(false);
            var captured = capturedSession;
            if (captured is null) return Finish(
                new(ManagedTransferTargetBaselineResultKind.AccountIdentityMismatch));

            var currentRoute = ManagedTransferRouteExpectation.OriginalSource;
            if (!await RevalidateAsync(currentRoute, captured, identity, candidateBinding,
                    exactManagedSession, exactHandle, canonicalSourceUri, immutableSourceEvidence,
                    deadline, pending).ConfigureAwait(false))
                return Finish(Denied());
            var initialSource = await ReadExactSourceAsync(identity, exactHandle, exactManagedSession,
                canonicalSourceUri, immutableSourceEvidence, deadline, pending).ConfigureAwait(false);
            if (initialSource != ManagedBrowserSourceListingResultKind.Observed)
                return Finish(new(MapSource(initialSource)));

            if (!await RevalidateAsync(currentRoute, captured, identity, candidateBinding,
                    exactManagedSession, exactHandle, canonicalSourceUri, immutableSourceEvidence,
                    deadline, pending).ConfigureAwait(false))
                return Finish(Denied());
            var rootBinding = Binding(exactHandle, exactManagedSession, canonicalSourceUri,
                immutableSourceEvidence, deadline.RemainingMilliseconds());
            var rootRequest = NewRequest(identity, ManagedBrowserAction.OpenTransferTargetRoot,
                new ManagedBrowserOpenTransferTargetRootRequest(rootBinding));
            routeMayHaveChanged = true;
            var rootResponse = await SendAsync(rootRequest, deadline, pending).ConfigureAwait(false);
            ManagedBrowserProtocol.ValidateResponseFor(rootRequest, rootResponse);
            if (rootResponse.Result is not ManagedBrowserOpenTransferTargetRootResult rootResult)
                return Finish(Denied());
            if (rootResult.NavigationState == ManagedBrowserTargetNavigationState.KnownNotDispatched)
                routeMayHaveChanged = false;
            if (rootResult.Kind != ManagedBrowserOpenTransferTargetRootResultKind.RootOpened ||
                rootResult.NavigationState != ManagedBrowserTargetNavigationState.RouteOpened)
                return Finish(new(MapRoot(rootResult.Kind)));

            currentRoute = ManagedTransferRouteExpectation.OfficialTargetRoot;
            if (!await RevalidateAsync(currentRoute, captured, identity, candidateBinding,
                    exactManagedSession, exactHandle, canonicalSourceUri, immutableSourceEvidence,
                    deadline, pending).ConfigureAwait(false))
                return Finish(Denied());
            var preparedAncestors = preparedDestination.FolderCandidatesSnapshot.AncestorExactIds;
            possibleTargetRoute = ManagedTransferRouteExpectation.ExactTargetDirectory(
                preparedAncestors[0], "/", preparedAncestors.Take(1).ToArray());
            var rootInventory = await ReadTargetAsync(identity, exactHandle, exactManagedSession,
                canonicalSourceUri, immutableSourceEvidence, null, null, null, deadline, pending)
                .ConfigureAwait(false);
            if (rootInventory is null || !InventoryMatchesRoute(rootInventory.WireSnapshot,
                    preparedDestination, exactHandle, expectedDepth: 0))
                return Finish(Denied());
            currentRoute = Route(rootInventory.WireSnapshot);
            possibleTargetRoute = currentRoute;
            lastTargetHeader = rootInventory.WireSnapshot.ToHeader();
            if (!await VerifyRouteAsync(currentRoute, identity, exactHandle, exactManagedSession,
                    canonicalSourceUri, immutableSourceEvidence, deadline, pending).ConfigureAwait(false))
                return Finish(Denied());

            var targetAncestors = preparedDestination.FolderCandidatesSnapshot.AncestorExactIds;
            var pathSegments = preparedDestination.Destination.Path.Value == "/"
                ? Array.Empty<string>()
                : preparedDestination.Destination.Path.Value[1..].Split('/');
            var current = rootInventory;
            for (var depth = 1; depth < targetAncestors.Length; depth++)
            {
                var childId = targetAncestors[depth];
                var childName = pathSegments[depth - 1];
                var child = current.WireSnapshot.Items.SingleOrDefault(item =>
                    item.Kind == ManagedBrowserTargetInventoryItemKind.Folder &&
                    string.Equals(item.ExactId, childId, StringComparison.Ordinal) &&
                    string.Equals(item.ServerName, childName, StringComparison.Ordinal));
                if (child is null) return Finish(Denied());
                var childPath = "/" + string.Join('/', pathSegments.Take(depth));
                if (!await RevalidateAsync(currentRoute, captured, identity, candidateBinding,
                        exactManagedSession, exactHandle, canonicalSourceUri, immutableSourceEvidence,
                        deadline, pending).ConfigureAwait(false))
                    return Finish(Denied());
                var enterBinding = Binding(exactHandle, exactManagedSession, canonicalSourceUri,
                    immutableSourceEvidence, deadline.RemainingMilliseconds());
                var enterRequest = NewRequest(identity, ManagedBrowserAction.EnterTransferTargetDirectory,
                    new ManagedBrowserEnterTransferTargetDirectoryRequest(enterBinding,
                        current.WireSnapshot.ToHeader(), childId, childName, childPath));
                var childRoute = ManagedTransferRouteExpectation.ExactTargetDirectory(
                    childId, childPath, targetAncestors.Take(depth + 1).ToArray());
                possibleTargetRoute = childRoute;
                var entered = await SendAsync(enterRequest, deadline, pending).ConfigureAwait(false);
                ManagedBrowserProtocol.ValidateResponseFor(enterRequest, entered);
                if (entered.Result is not ManagedBrowserEnterTransferTargetDirectoryResult enterResult)
                    return Finish(Denied());
                if (enterResult.NavigationState == ManagedBrowserTargetNavigationState.KnownNotDispatched)
                    possibleTargetRoute = currentRoute;
                if (enterResult.Kind != ManagedBrowserEnterTransferTargetDirectoryResultKind.DirectoryEntered ||
                    enterResult.NavigationState != ManagedBrowserTargetNavigationState.RouteOpened)
                    return Finish(Denied());

                currentRoute = childRoute;
                if (!await RevalidateAsync(currentRoute, captured, identity, candidateBinding,
                        exactManagedSession, exactHandle, canonicalSourceUri, immutableSourceEvidence,
                        deadline, pending).ConfigureAwait(false))
                    return Finish(Denied());
                var childInventory = await ReadTargetAsync(identity, exactHandle, exactManagedSession,
                    canonicalSourceUri, immutableSourceEvidence, childId, childPath,
                    targetAncestors.Take(depth + 1).ToArray(), deadline, pending).ConfigureAwait(false);
                if (childInventory is null || !InventoryMatchesRoute(childInventory.WireSnapshot,
                        preparedDestination, exactHandle, depth))
                    return Finish(Denied());
                current = childInventory;
                currentRoute = Route(current.WireSnapshot);
                possibleTargetRoute = currentRoute;
                lastTargetHeader = current.WireSnapshot.ToHeader();
                if (!await VerifyRouteAsync(currentRoute, identity, exactHandle, exactManagedSession,
                        canonicalSourceUri, immutableSourceEvidence, deadline, pending).ConfigureAwait(false))
                    return Finish(Denied());
            }

            if (!string.Equals(current.WireSnapshot.DirectoryExactId,
                    preparedDestination.Destination.RemoteId, StringComparison.Ordinal) ||
                !string.Equals(current.WireSnapshot.CanonicalPath,
                    preparedDestination.Destination.Path.Value, StringComparison.Ordinal))
                return Finish(Denied());

            if (!await RevalidateAsync(currentRoute, captured, identity, candidateBinding,
                    exactManagedSession, exactHandle, canonicalSourceUri, immutableSourceEvidence,
                    deadline, pending).ConfigureAwait(false))
                return Finish(Denied());
            var restoreBinding = Binding(exactHandle, exactManagedSession, canonicalSourceUri,
                immutableSourceEvidence, deadline.RemainingMilliseconds());
            var restoreRequest = NewRequest(identity,
                ManagedBrowserAction.OpenTransferSourceAfterTargetRead,
                new ManagedBrowserOpenTransferSourceAfterTargetReadRequest(
                    restoreBinding, current.WireSnapshot.ToHeader()));
            var restored = await SendAsync(restoreRequest, deadline, pending).ConfigureAwait(false);
            ManagedBrowserProtocol.ValidateResponseFor(restoreRequest, restored);
            if (restored.Result is not ManagedBrowserOpenTransferSourceAfterTargetReadResult
                {
                    Kind: ManagedBrowserOpenTransferSourceAfterTargetReadResultKind.SourceRestored,
                    NavigationState: ManagedBrowserTargetNavigationState.RouteOpened,
                })
                return Finish(Denied());

            currentRoute = ManagedTransferRouteExpectation.OriginalSource;
            if (!await RevalidateAsync(currentRoute, captured, identity, candidateBinding,
                    exactManagedSession, exactHandle, canonicalSourceUri, immutableSourceEvidence,
                    deadline, pending).ConfigureAwait(false) ||
                await ReadExactSourceAsync(identity, exactHandle, exactManagedSession,
                    canonicalSourceUri, immutableSourceEvidence, deadline, pending).ConfigureAwait(false) !=
                ManagedBrowserSourceListingResultKind.Observed)
                return Finish(Denied());
            if (!CandidateIsCurrent(candidateBinding, identity)) return Finish(Denied());
            routeMayHaveChanged = false;

            deadline.RequireBudget();
            targetEvidence = current.DurableEvidence;
            persistenceStarted = true;
            persistedAt = timeProvider.GetUtcNow();
            var persistence = attemptStore.TryPersistTransferTargetPreparationAsync(
                    identity.OperationId, identity.AccountId, identity.Generation,
                    expectedAttemptRevision, targetEvidence, persistedAt.Value, deadline.Token);
            pending.Add(persistence);
            var persisted = await persistence.WaitAsync(deadline.Token).ConfigureAwait(false);
            deadline.RequireBudget();
            if (!persisted)
            {
                if (await CleanupProofAsync(identity, candidateBinding, exactHandle,
                        exactManagedSession, canonicalSourceUri, immutableSourceEvidence, captured,
                        expectedAttemptRevision, targetEvidence, attempt, attemptStore, 5_000,
                        CancellationToken.None, preparedDestination, lastTargetHeader,
                        possibleTargetRoute, persistedAt, cleanupSettlements).ConfigureAwait(false))
                {
                    persistenceStarted = false;
                    return Finish(new(ManagedTransferTargetBaselineResultKind.PersistenceConflict));
                }
                return Finish(new(ManagedTransferTargetBaselineResultKind.PersistenceConflict));
            }

            if (!await CleanupProofAsync(identity, candidateBinding, exactHandle,
                    exactManagedSession, canonicalSourceUri, immutableSourceEvidence, captured,
                    expectedAttemptRevision, targetEvidence, attempt, attemptStore, 5_000,
                    CancellationToken.None, preparedDestination, lastTargetHeader,
                    possibleTargetRoute, persistedAt, cleanupSettlements)
                .ConfigureAwait(false))
                return Finish(Denied());
            return Finish(new(ManagedTransferTargetBaselineResultKind.BaselinePersisted,
                current.DurableEvidence));
        }
        catch (OperationCanceledException)
        {
            return Finish(new(cancellationToken.IsCancellationRequested
                ? ManagedTransferTargetBaselineResultKind.Cancelled
                : ManagedTransferTargetBaselineResultKind.TimedOut));
        }
        catch (TimeoutException)
        {
            return Finish(new(ManagedTransferTargetBaselineResultKind.TimedOut));
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or
            IOException or UnauthorizedAccessException or ManagedBrowserProtocolException or
            ManagedBrowserHelperClientException)
        {
            return Finish(Denied());
        }
        catch (Exception)
        {
            return Finish(Denied());
        }

        ManagedTransferTargetBaselineResult Finish(ManagedTransferTargetBaselineResult result)
        {
            if (lease is not null && result.Kind != ManagedTransferTargetBaselineResultKind.BaselinePersisted &&
                (routeMayHaveChanged || persistenceStarted))
            {
                var settlement = ObserveSettlementAsync(pending.ToArray());
                var owned = lease;
                var quarantine = new ManagedTransferTargetBaselineQuarantine(settlement,
                    (milliseconds, token) => CleanupProofAsync(identity, candidateBinding, exactHandle,
                        exactManagedSession, canonicalSourceUri, immutableSourceEvidence,
                        capturedSession, expectedAttemptRevision, targetEvidence, attempt,
                        attemptStore, milliseconds, token, preparedDestination, lastTargetHeader,
                        possibleTargetRoute, persistenceStarted ? persistedAt : null,
                        cleanupSettlements),
                    cleanupSettlements, owned);
                lease = null;
                return new(ManagedTransferTargetBaselineResultKind.RouteQuarantined,
                    quarantine: quarantine);
            }
            if (lease is not null && pending.Any(task => !task.IsCompleted))
            {
                _ = ReleaseAfterSettlementAsync(lease, pending.ToArray());
                lease = null;
                return result;
            }
            try { lease?.Dispose(); }
            catch (Exception) { }
            lease = null;
            return result;
        }
    }

    private async Task<ManagedBrowserLifecycleSession?> LoadExactSessionAsync(Guid accountId,
        ManagedBrowserSessionReference exactManagedSession, ManagedChromiumSessionHandle exactHandle,
        ManagedBrowserLifecycleSession? captured, ManagedTransferOperationDeadline deadline,
        List<Task> pending)
    {
        deadline.RequireBudget();
        var loading = loadSession(accountId, deadline.Token);
        Track(pending, loading);
        var current = await loading.WaitAsync(deadline.Token).ConfigureAwait(false);
        deadline.RequireBudget();
        if (current is null || !SameIdentity(current.Session, exactManagedSession) ||
            !HandleMatches(exactHandle, exactManagedSession) ||
            !string.Equals(current.CanonicalProfilePath, exactHandle.Profile.CanonicalSourcePath,
                StringComparison.OrdinalIgnoreCase) || captured is not null && !SameSnapshot(current, captured))
            return null;
        return current;
    }

    private async Task<bool> RevalidateAsync(ManagedTransferRouteExpectation route,
        ManagedBrowserLifecycleSession captured, WebAutomationRequestIdentity identity,
        BaiduCandidateSessionBinding candidateBinding, ManagedBrowserSessionReference exactManagedSession,
        ManagedChromiumSessionHandle exactHandle, string sourceUri,
        ManagedBrowserSourceSnapshot source, ManagedTransferOperationDeadline deadline,
        List<Task> pending)
    {
        if (!CandidateIsCurrent(candidateBinding, identity)) return false;
        return await RevalidateObservationAsync(route, captured, identity, exactManagedSession,
            exactHandle, sourceUri, source, deadline, pending)
            .ConfigureAwait(false);
    }

    private async Task<bool> RevalidateObservationAsync(ManagedTransferRouteExpectation route,
        ManagedBrowserLifecycleSession captured, WebAutomationRequestIdentity identity,
        ManagedBrowserSessionReference exactManagedSession,
        ManagedChromiumSessionHandle exactHandle, string canonicalSourceUri,
        ManagedBrowserSourceSnapshot immutableSourceEvidence,
        ManagedTransferOperationDeadline deadline, List<Task> pending)
    {
        if (await LoadExactSessionAsync(identity.AccountId, exactManagedSession, exactHandle,
                captured, deadline, pending).ConfigureAwait(false) is null)
            return false;
        deadline.RequireBudget();
        var runtime = verifyRuntime(exactHandle, deadline.RemainingMilliseconds(), deadline.Token);
        Track(pending, runtime);
        if (!await runtime.WaitAsync(deadline.Token).ConfigureAwait(false))
            return false;
        deadline.RequireBudget();
        return await VerifyRouteAsync(route, identity, exactHandle, exactManagedSession,
            canonicalSourceUri, immutableSourceEvidence, deadline, pending).ConfigureAwait(false);
    }

    private async Task<bool> VerifyRouteAsync(ManagedTransferRouteExpectation route,
        WebAutomationRequestIdentity identity, ManagedChromiumSessionHandle exactHandle,
        ManagedBrowserSessionReference exactManagedSession, string sourceUri,
        ManagedBrowserSourceSnapshot source, ManagedTransferOperationDeadline deadline,
        List<Task> pending)
    {
        deadline.RequireBudget();
        var request = new ManagedTransferRouteProofRequest(exactHandle, exactManagedSession,
            identity, Binding(exactHandle, exactManagedSession, sourceUri, source,
                deadline.RemainingMilliseconds()), route);
        var proving = routeProof.VerifyAsync(request, deadline.Token);
        Track(pending, proving);
        var proved = await proving.WaitAsync(deadline.Token).ConfigureAwait(false);
        deadline.RequireBudget();
        return proved;
    }

    private async Task<ManagedBrowserSourceListingResultKind> ReadExactSourceAsync(WebAutomationRequestIdentity identity,
        ManagedChromiumSessionHandle exactHandle, ManagedBrowserSessionReference exactManagedSession,
        string sourceUri, ManagedBrowserSourceSnapshot immutableSource,
        ManagedTransferOperationDeadline deadline, List<Task> pending)
    {
        var binding = new ManagedBrowserReadTransferSourceRequest(exactHandle.BrowserId,
            exactHandle.TargetId, exactHandle.Endpoint, deadline.RemainingMilliseconds(),
            sourceUri, exactManagedSession.StableBaiduUserId);
        var request = NewRequest(identity, ManagedBrowserAction.ReadTransferSource, binding);
        var response = await SendAsync(request, deadline, pending).ConfigureAwait(false);
        ManagedBrowserProtocol.ValidateResponseFor(request, response);
        if (response.Result is not ManagedBrowserSourceListingResult result)
            return ManagedBrowserSourceListingResultKind.PageNotValidated;
        if (result.Kind != ManagedBrowserSourceListingResultKind.Observed || result.Snapshot is not { } observed)
            return result.Kind;
        return SameSource(observed, immutableSource)
            ? ManagedBrowserSourceListingResultKind.Observed
            : ManagedBrowserSourceListingResultKind.PageNotValidated;
    }

    private async Task<ManagedTransferTargetInventoryEvidence?> ReadTargetAsync(
        WebAutomationRequestIdentity identity, ManagedChromiumSessionHandle exactHandle,
        ManagedBrowserSessionReference exactManagedSession, string sourceUri,
        ManagedBrowserSourceSnapshot source, string? directoryExactId, string? canonicalPath,
        IReadOnlyList<string>? ancestors, ManagedTransferOperationDeadline deadline,
        List<Task> pending)
    {
        var payload = new ManagedBrowserReadTransferTargetDirectoryRequest(
            Binding(exactHandle, exactManagedSession, sourceUri, source,
                deadline.RemainingMilliseconds()), directoryExactId, canonicalPath, ancestors);
        var result = await new ManagedTransferTargetInventoryReader(
                (request, _) => SendAsync(request, deadline, pending))
            .ReadAsync(identity.OperationId, identity.AccountId, identity.Generation,
                payload, deadline.Token).ConfigureAwait(false);
        deadline.RequireBudget();
        return result.Kind == ManagedBrowserReadTransferTargetDirectoryResultKind.Observed
            ? result.Evidence
            : null;
    }

    private async Task<ManagedBrowserResponse> SendAsync(ManagedBrowserRequest request,
        ManagedTransferOperationDeadline deadline, List<Task> pending)
    {
        deadline.RequireBudget();
        var operation = send(request, deadline.Token);
        Track(pending, operation);
        var response = await operation.WaitAsync(deadline.Token).ConfigureAwait(false);
        deadline.RequireBudget();
        return response;
    }

    private async Task<bool> CleanupProofAsync(WebAutomationRequestIdentity identity,
        BaiduCandidateSessionBinding candidateBinding, ManagedChromiumSessionHandle exactHandle,
        ManagedBrowserSessionReference exactManagedSession, string sourceUri,
        ManagedBrowserSourceSnapshot source, ManagedBrowserLifecycleSession? captured,
        long expectedRevision, TransferTargetPreparationEvidence? written,
        WebAutomationAttemptRecord? initialAttempt, IWebAutomationAttemptStore store,
        int timeoutMilliseconds, CancellationToken cancellationToken,
        ManagedPreparedDestinationEvidence preparedDestination,
        ManagedBrowserTargetDirectoryHeader? lastTargetHeader,
        ManagedTransferRouteExpectation? possibleTargetRoute,
        DateTimeOffset? persistedAt,
        List<Task>? settlementSink = null)
    {
        using var cleanup = new ManagedTransferOperationDeadline(
            Math.Min(timeoutMilliseconds, ManagedBrowserProtocol.MaximumOperationTimeoutMilliseconds),
            cancellationToken, timeProvider);
        var cleanupPending = settlementSink ?? [];
        try
        {
            var firstRead = store.GetAsync(identity.OperationId, identity.AccountId,
                identity.Generation, cleanup.Token);
            lock (cleanupPending) cleanupPending.Add(firstRead);
            var first = await firstRead.WaitAsync(cleanup.Token).ConfigureAwait(false);
            captured ??= await LoadExactSessionAsync(identity.AccountId, exactManagedSession,
                exactHandle, null, cleanup, cleanupPending).ConfigureAwait(false);
            if (captured is null) return false;
            var runtime = verifyRuntime(exactHandle, cleanup.RemainingMilliseconds(), cleanup.Token);
            Track(cleanupPending, runtime);
            if (!await runtime.WaitAsync(cleanup.Token).ConfigureAwait(false)) return false;
            var onSource = await VerifyRouteAsync(ManagedTransferRouteExpectation.OriginalSource,
                identity, exactHandle, exactManagedSession, sourceUri, source, cleanup,
                cleanupPending).ConfigureAwait(false);
            if (!onSource)
            {
                var targetHeader = lastTargetHeader;
                if (possibleTargetRoute is { State: ManagedTransferRouteState.ExactTargetDirectory } possible &&
                    (targetHeader is null || !HeaderMatchesRoute(targetHeader, possible)))
                {
                    var onPossibleTarget = await VerifyRouteAsync(possible, identity, exactHandle,
                            exactManagedSession, sourceUri, source, cleanup, cleanupPending)
                        .ConfigureAwait(false);
                    if (onPossibleTarget)
                    {
                        var observed = await ReadTargetAsync(identity, exactHandle, exactManagedSession,
                            sourceUri, source, possible.DirectoryExactId, possible.CanonicalPath,
                            possible.AncestorExactIds, cleanup, cleanupPending).ConfigureAwait(false);
                        var expectedDepth = possible.AncestorExactIds.Count - 1;
                        if (observed is null || !InventoryMatchesRoute(observed.WireSnapshot,
                                preparedDestination, exactHandle, expectedDepth))
                            return false;
                        targetHeader = observed.WireSnapshot.ToHeader();
                    }
                    else if (targetHeader is not null)
                    {
                        if (!await VerifyRouteAsync(ManagedTransferRouteExpectation.ExactTargetDirectory(
                                    targetHeader.DirectoryExactId, targetHeader.CanonicalPath,
                                    targetHeader.AncestorExactIds), identity, exactHandle,
                                exactManagedSession, sourceUri, source, cleanup, cleanupPending)
                            .ConfigureAwait(false))
                            return false;
                    }
                    else if (IsPreparedRootRoute(possible, preparedDestination))
                    {
                        if (!await VerifyRouteAsync(ManagedTransferRouteExpectation.OfficialTargetRoot,
                                identity, exactHandle, exactManagedSession, sourceUri, source, cleanup,
                                cleanupPending).ConfigureAwait(false))
                            return false;
                        var root = await ReadTargetAsync(identity, exactHandle, exactManagedSession,
                            sourceUri, source, null, null, null, cleanup, cleanupPending)
                            .ConfigureAwait(false);
                        if (root is null || !InventoryMatchesRoute(root.WireSnapshot,
                                preparedDestination, exactHandle, 0))
                            return false;
                        targetHeader = root.WireSnapshot.ToHeader();
                    }
                    else return false;
                }
                else if (targetHeader is not null)
                {
                    if (!await VerifyRouteAsync(ManagedTransferRouteExpectation.ExactTargetDirectory(
                                targetHeader.DirectoryExactId, targetHeader.CanonicalPath,
                                targetHeader.AncestorExactIds), identity, exactHandle,
                            exactManagedSession, sourceUri, source, cleanup, cleanupPending)
                        .ConfigureAwait(false))
                        return false;
                }
                else
                {
                    if (!await VerifyRouteAsync(ManagedTransferRouteExpectation.OfficialTargetRoot,
                            identity, exactHandle, exactManagedSession, sourceUri, source, cleanup,
                            cleanupPending).ConfigureAwait(false))
                        return false;
                    var root = await ReadTargetAsync(identity, exactHandle, exactManagedSession,
                        sourceUri, source, null, null, null, cleanup, cleanupPending).ConfigureAwait(false);
                    if (root is null || !InventoryMatchesRoute(root.WireSnapshot,
                            preparedDestination, exactHandle, 0))
                        return false;
                    targetHeader = root.WireSnapshot.ToHeader();
                }
                if (!await RevalidateObservationAsync(ManagedTransferRouteExpectation.ExactTargetDirectory(
                            targetHeader.DirectoryExactId, targetHeader.CanonicalPath,
                            targetHeader.AncestorExactIds), captured, identity, exactManagedSession,
                        exactHandle, sourceUri, source, cleanup, cleanupPending).ConfigureAwait(false) ||
                    !CandidateIsCurrent(candidateBinding, identity))
                    return false;
                var restore = NewRequest(identity,
                    ManagedBrowserAction.OpenTransferSourceAfterTargetRead,
                    new ManagedBrowserOpenTransferSourceAfterTargetReadRequest(
                        Binding(exactHandle, exactManagedSession, sourceUri, source,
                            cleanup.RemainingMilliseconds()), targetHeader));
                var restored = await SendAsync(restore, cleanup, cleanupPending).ConfigureAwait(false);
                ManagedBrowserProtocol.ValidateResponseFor(restore, restored);
                if (restored.Result is not ManagedBrowserOpenTransferSourceAfterTargetReadResult
                    {
                        Kind: ManagedBrowserOpenTransferSourceAfterTargetReadResultKind.SourceRestored,
                        NavigationState: ManagedBrowserTargetNavigationState.RouteOpened,
                    })
                    return false;
            }
            if (!await RevalidateObservationAsync(ManagedTransferRouteExpectation.OriginalSource,
                    captured, identity, exactManagedSession, exactHandle, sourceUri, source, cleanup,
                    cleanupPending).ConfigureAwait(false) ||
                await ReadExactSourceAsync(identity, exactHandle, exactManagedSession,
                    sourceUri, source, cleanup, cleanupPending).ConfigureAwait(false) !=
                ManagedBrowserSourceListingResultKind.Observed)
                return false;
            var secondRead = store.GetAsync(identity.OperationId, identity.AccountId,
                identity.Generation, cleanup.Token);
            lock (cleanupPending) cleanupPending.Add(secondRead);
            var second = await secondRead.WaitAsync(cleanup.Token).ConfigureAwait(false);
            return SameAttempt(first, second) &&
                (written is not null && ExactWrittenAttempt(first,
                    identity, expectedRevision + 1, written, source, initialAttempt,
                    candidateBinding.Binding.Capability, persistedAt) ||
                 ExactUnchangedPreparing(first, initialAttempt, identity, expectedRevision));
        }
        catch (Exception error) when (error is OperationCanceledException or TimeoutException or
            ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool CandidateIsCurrent(BaiduCandidateSessionBinding candidate,
        WebAutomationRequestIdentity identity)
    {
        try { candidate.ValidateIdentity(identity); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return false; }
        var now = timeProvider.GetUtcNow();
        return !candidate.Authorization.IsRecovery && !candidate.Binding.IsRecovery &&
            candidate.Binding.IssuedAtUtc <= now && candidate.Binding.ExpiresAtUtc > now &&
            candidate.Binding.ExpiresAtUtc > candidate.Binding.IssuedAtUtc;
    }

    private static bool InventoryMatchesRoute(ManagedBrowserTargetInventorySnapshot snapshot,
        ManagedPreparedDestinationEvidence prepared, ManagedChromiumSessionHandle exactHandle,
        int expectedDepth)
    {
        var expectedAncestors = prepared.FolderCandidatesSnapshot.AncestorExactIds;
        var segments = prepared.Destination.Path.Value == "/"
            ? Array.Empty<string>()
            : prepared.Destination.Path.Value[1..].Split('/');
        var expectedPath = expectedDepth == 0
            ? "/"
            : "/" + string.Join('/', segments.Take(expectedDepth));
        return snapshot.AncestorExactIds.Count == expectedDepth + 1 &&
            snapshot.AncestorExactIds.SequenceEqual(expectedAncestors.Take(expectedDepth + 1),
                StringComparer.Ordinal) &&
            string.Equals(snapshot.DirectoryExactId, expectedAncestors[expectedDepth],
                StringComparison.Ordinal) &&
            string.Equals(snapshot.CanonicalPath, expectedPath, StringComparison.Ordinal) &&
            snapshot.BrowserId == exactHandle.BrowserId &&
            string.Equals(snapshot.TargetId, exactHandle.TargetId, StringComparison.Ordinal) &&
            string.Equals(snapshot.DevToolsEndpoint.AbsoluteUri,
                exactHandle.Endpoint.AbsoluteUri, StringComparison.Ordinal) &&
            string.Equals(snapshot.GenerationToken,
                prepared.FolderCandidatesSnapshot.GenerationToken, StringComparison.Ordinal) &&
            string.Equals(snapshot.BaiduUserId,
                prepared.FolderCandidatesSnapshot.BaiduUserId, StringComparison.Ordinal);
    }

    private static ManagedTransferRouteExpectation Route(ManagedBrowserTargetInventorySnapshot snapshot) =>
        ManagedTransferRouteExpectation.ExactTargetDirectory(snapshot.DirectoryExactId,
            snapshot.CanonicalPath, snapshot.AncestorExactIds);

    private static bool HeaderMatchesRoute(ManagedBrowserTargetDirectoryHeader header,
        ManagedTransferRouteExpectation route) =>
        route.State == ManagedTransferRouteState.ExactTargetDirectory &&
        string.Equals(header.DirectoryExactId, route.DirectoryExactId, StringComparison.Ordinal) &&
        string.Equals(header.CanonicalPath, route.CanonicalPath, StringComparison.Ordinal) &&
        header.AncestorExactIds.SequenceEqual(route.AncestorExactIds, StringComparer.Ordinal);

    private static bool IsPreparedRootRoute(ManagedTransferRouteExpectation route,
        ManagedPreparedDestinationEvidence prepared)
    {
        var ancestors = prepared.FolderCandidatesSnapshot.AncestorExactIds;
        return route.State == ManagedTransferRouteState.ExactTargetDirectory &&
            ancestors.Length > 0 && route.AncestorExactIds.Count == 1 &&
            string.Equals(route.DirectoryExactId, ancestors[0], StringComparison.Ordinal) &&
            string.Equals(route.CanonicalPath, "/", StringComparison.Ordinal) &&
            string.Equals(route.AncestorExactIds[0], ancestors[0], StringComparison.Ordinal);
    }

    private static ManagedBrowserTargetActionBinding Binding(ManagedChromiumSessionHandle handle,
        ManagedBrowserSessionReference session, string sourceUri, ManagedBrowserSourceSnapshot source,
        int timeoutMilliseconds) => new(handle.BrowserId, handle.TargetId, handle.Endpoint,
            timeoutMilliseconds, sourceUri, session.StableBaiduUserId, source);

    private static ManagedBrowserRequest NewRequest(WebAutomationRequestIdentity identity,
        ManagedBrowserAction action, ManagedBrowserRequestPayload payload) =>
        new(Guid.NewGuid(), identity.OperationId, identity.AccountId, identity.Generation, action, payload);

    private static bool SameSource(ManagedBrowserSourceSnapshot left,
        ManagedBrowserSourceSnapshot right) =>
        string.Equals(left.BaiduUserId, right.BaiduUserId, StringComparison.Ordinal) &&
        string.Equals(left.GenerationToken, right.GenerationToken, StringComparison.Ordinal) &&
        string.Equals(left.ObservationFingerprint, right.ObservationFingerprint, StringComparison.Ordinal) &&
        left.Items.Length == right.Items.Length && left.Items.Zip(right.Items, (x, y) =>
            x.Kind == y.Kind && string.Equals(x.ExactId, y.ExactId, StringComparison.Ordinal) &&
            string.Equals(x.ServerName, y.ServerName, StringComparison.Ordinal)).All(equal => equal);

    private static bool SameSnapshot(ManagedBrowserLifecycleSession left,
        ManagedBrowserLifecycleSession right) =>
        left.InstallationRevision == right.InstallationRevision &&
        left.SessionRevision == right.SessionRevision && SameIdentity(left.Session, right.Session) &&
        string.Equals(left.CanonicalProfilePath, right.CanonicalProfilePath,
            StringComparison.OrdinalIgnoreCase);

    private static bool SameAttempt(WebAutomationAttemptRecord? left,
        WebAutomationAttemptRecord? right) =>
        left is not null && right is not null && left.OperationId == right.OperationId &&
        left.AccountId == right.AccountId && left.Generation == right.Generation &&
        left.OwnerKind == right.OwnerKind && left.State == right.State && left.Revision == right.Revision &&
        left.ResumeStage == right.ResumeStage && left.IsWaitingForUser == right.IsWaitingForUser &&
        left.CreatedAt == right.CreatedAt && left.UpdatedAt == right.UpdatedAt &&
        Equals(left.ExecutionProfile, right.ExecutionProfile) &&
        SameBaseline(left.Baseline, right.Baseline) &&
        string.Equals(left.ObservationFingerprint, right.ObservationFingerprint, StringComparison.Ordinal) &&
        Equals(left.AcceptedRemoteItems, right.AcceptedRemoteItems) &&
        SameSourceEvidence(left.SourceEvidence, right.SourceEvidence) &&
        SameTargetEvidence(left.TargetPreparationEvidence, right.TargetPreparationEvidence) &&
        SameSubmissionEvidence(left.SubmissionEvidence, right.SubmissionEvidence) &&
        string.Equals(left.OfficialRequestId, right.OfficialRequestId, StringComparison.Ordinal) &&
        SameMapping(left.TransferIdentityMapping, right.TransferIdentityMapping);

    private static bool ExactWrittenAttempt(WebAutomationAttemptRecord? attempt,
        WebAutomationRequestIdentity identity, long revision,
        TransferTargetPreparationEvidence written, ManagedBrowserSourceSnapshot source,
        WebAutomationAttemptRecord? initial, AdapterCapabilityVersion capability,
        DateTimeOffset? persistedAt) =>
        attempt is not null && initial is not null && persistedAt is not null &&
        attempt.OperationId == identity.OperationId &&
        attempt.AccountId == identity.AccountId && attempt.Generation == identity.Generation &&
        attempt.OwnerKind == WebAutomationOwnerKind.Transfer &&
        attempt.State == WebAutomationAttemptState.BaselinePersisted && attempt.Revision == revision &&
        attempt.ResumeStage is null && !attempt.IsWaitingForUser &&
        attempt.CreatedAt == initial.CreatedAt && attempt.UpdatedAt == persistedAt.Value &&
        Equals(attempt.ExecutionProfile, identity.ExecutionProfile) &&
        string.Equals(attempt.ObservationFingerprint, written.ObservationFingerprint,
            StringComparison.Ordinal) &&
        attempt.SourceEvidence is { } persistedSource && initial.SourceEvidence is { } initialSource &&
        SameSourceEvidence(persistedSource, initialSource) && SourceMatches(persistedSource, source) &&
        Equals(persistedSource.Capability, capability) &&
        attempt.TargetPreparationEvidence is { } durable &&
        attempt.Baseline is { } baseline &&
        SameTargetEvidence(durable, written) && SameBaseline(baseline, written.Baseline) &&
        attempt.SubmissionEvidence is null && attempt.AcceptedRemoteItems is null &&
        attempt.OfficialRequestId is null && attempt.TransferIdentityMapping is null;

    private static bool SameBaseline(WebAutomationBaseline? left, WebAutomationBaseline? right) =>
        left is null && right is null || left is not null && right is not null &&
        string.Equals(left.Fingerprint, right.Fingerprint, StringComparison.Ordinal) &&
        left.Items.Select(item => item.ExactId).SequenceEqual(
            right.Items.Select(item => item.ExactId), StringComparer.Ordinal);

    private static bool SameSourceEvidence(TransferSourceEvidence? left,
        TransferSourceEvidence? right) =>
        left is null && right is null || left is not null && right is not null &&
        Equals(left.Items, right.Items) && left.TotalCount == right.TotalCount &&
        left.ReachedEnd == right.ReachedEnd && left.IsStable == right.IsStable &&
        string.Equals(left.GenerationToken, right.GenerationToken, StringComparison.Ordinal) &&
        string.Equals(left.BaiduUserId, right.BaiduUserId, StringComparison.Ordinal) &&
        Equals(left.Capability, right.Capability) &&
        string.Equals(left.ObservationFingerprint, right.ObservationFingerprint, StringComparison.Ordinal);

    private static bool SameTargetEvidence(TransferTargetPreparationEvidence? left,
        TransferTargetPreparationEvidence? right) =>
        left is null && right is null || left is not null && right is not null &&
        string.Equals(left.TargetExactId, right.TargetExactId, StringComparison.Ordinal) &&
        string.Equals(left.TargetPath.Value, right.TargetPath.Value, StringComparison.Ordinal) &&
        left.TotalCount == right.TotalCount && left.ReachedEnd == right.ReachedEnd &&
        left.IsStable == right.IsStable &&
        string.Equals(left.TargetGenerationToken, right.TargetGenerationToken, StringComparison.Ordinal) &&
        string.Equals(left.BaiduUserId, right.BaiduUserId, StringComparison.Ordinal) &&
        left.Scope == right.Scope && SameBaseline(left.Baseline, right.Baseline) &&
        string.Equals(left.ObservationFingerprint, right.ObservationFingerprint, StringComparison.Ordinal);

    private static bool SameSubmissionEvidence(TransferSubmissionEvidence? left,
        TransferSubmissionEvidence? right) =>
        left is null && right is null || left is not null && right is not null &&
        string.Equals(left.SourceFingerprint, right.SourceFingerprint, StringComparison.Ordinal) &&
        string.Equals(left.TargetExactId, right.TargetExactId, StringComparison.Ordinal) &&
        string.Equals(left.TargetBaselineFingerprint, right.TargetBaselineFingerprint,
            StringComparison.Ordinal) &&
        string.Equals(left.BaiduUserId, right.BaiduUserId, StringComparison.Ordinal) &&
        Equals(left.Capability, right.Capability) &&
        left.MarkedSubmittingAtUtc == right.MarkedSubmittingAtUtc;

    private static bool SameMapping(TransferIdentityMapping? left, TransferIdentityMapping? right) =>
        left is null && right is null || left is not null && right is not null &&
        string.Equals(left.Fingerprint, right.Fingerprint, StringComparison.Ordinal) &&
        left.Entries.Select(entry => (entry.SourceExactId, entry.DestinationExactId)).SequenceEqual(
            right.Entries.Select(entry => (entry.SourceExactId, entry.DestinationExactId)));

    private static bool ExactUnchangedPreparing(WebAutomationAttemptRecord? attempt,
        WebAutomationAttemptRecord? initial, WebAutomationRequestIdentity identity, long revision) =>
        attempt is not null && initial is not null && SameAttempt(attempt, initial) &&
        attempt.OperationId == identity.OperationId && attempt.AccountId == identity.AccountId &&
        attempt.Generation == identity.Generation && attempt.OwnerKind == WebAutomationOwnerKind.Transfer &&
        attempt.State == WebAutomationAttemptState.Preparing && attempt.Revision == revision &&
        attempt.Baseline is null && attempt.ResumeStage is null && !attempt.IsWaitingForUser &&
        attempt.TargetPreparationEvidence is null && attempt.SubmissionEvidence is null &&
        attempt.AcceptedRemoteItems is null && attempt.OfficialRequestId is null &&
        attempt.TransferIdentityMapping is null;

    private static async Task ObserveSettlementAsync(Task[] pending)
    {
        try { await Task.WhenAll(pending).ConfigureAwait(false); }
        catch (Exception) { }
    }

    private static async Task ReleaseAfterSettlementAsync(IDisposable lease, Task[] pending)
    {
        await ObserveSettlementAsync(pending).ConfigureAwait(false);
        try { lease.Dispose(); }
        catch (Exception) { }
    }

    private static async Task DisposeLateLeaseAsync(Task<IDisposable> acquisition)
    {
        try
        {
            var acquired = await acquisition.ConfigureAwait(false);
            acquired.Dispose();
        }
        catch (Exception) { }
    }

    private static void Track(List<Task> pending, Task task)
    {
        lock (pending) pending.Add(task);
    }

    private static ManagedTransferTargetBaselineResultKind MapSource(
        ManagedBrowserSourceListingResultKind kind) => kind switch
    {
        ManagedBrowserSourceListingResultKind.LoginRequired =>
            ManagedTransferTargetBaselineResultKind.LoginRequired,
        ManagedBrowserSourceListingResultKind.VerificationRequired =>
            ManagedTransferTargetBaselineResultKind.VerificationRequired,
        ManagedBrowserSourceListingResultKind.AccountIdentityMismatch =>
            ManagedTransferTargetBaselineResultKind.AccountIdentityMismatch,
        ManagedBrowserSourceListingResultKind.BrowserUnavailable =>
            ManagedTransferTargetBaselineResultKind.BrowserUnavailable,
        ManagedBrowserSourceListingResultKind.TimedOut =>
            ManagedTransferTargetBaselineResultKind.TimedOut,
        ManagedBrowserSourceListingResultKind.Cancelled =>
            ManagedTransferTargetBaselineResultKind.Cancelled,
        _ => ManagedTransferTargetBaselineResultKind.PageNotValidated,
    };

    private static ManagedTransferTargetBaselineResultKind MapRoot(
        ManagedBrowserOpenTransferTargetRootResultKind kind) => kind switch
    {
        ManagedBrowserOpenTransferTargetRootResultKind.LoginRequired =>
            ManagedTransferTargetBaselineResultKind.LoginRequired,
        ManagedBrowserOpenTransferTargetRootResultKind.VerificationRequired =>
            ManagedTransferTargetBaselineResultKind.VerificationRequired,
        ManagedBrowserOpenTransferTargetRootResultKind.AccountIdentityMismatch =>
            ManagedTransferTargetBaselineResultKind.AccountIdentityMismatch,
        ManagedBrowserOpenTransferTargetRootResultKind.BrowserUnavailable =>
            ManagedTransferTargetBaselineResultKind.BrowserUnavailable,
        ManagedBrowserOpenTransferTargetRootResultKind.TimedOut =>
            ManagedTransferTargetBaselineResultKind.TimedOut,
        ManagedBrowserOpenTransferTargetRootResultKind.Cancelled =>
            ManagedTransferTargetBaselineResultKind.Cancelled,
        _ => ManagedTransferTargetBaselineResultKind.PageNotValidated,
    };

    private bool TryValidateStaticInputs(WebAutomationRequestIdentity identity,
        BaiduCandidateSessionBinding candidateBinding, BaiduExecutionContext executionContext,
        ManagedChromiumSessionHandle exactHandle, ManagedBrowserSessionReference exactManagedSession,
        string canonicalSourceUri, ManagedBrowserSourceSnapshot immutableSourceEvidence,
        ManagedPreparedDestinationEvidence preparedDestination, long expectedAttemptRevision,
        int timeoutMilliseconds, IWebAutomationAttemptStore attemptStore)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(identity);
            ArgumentNullException.ThrowIfNull(candidateBinding);
            ArgumentNullException.ThrowIfNull(executionContext);
            ArgumentNullException.ThrowIfNull(exactHandle);
            ArgumentNullException.ThrowIfNull(exactManagedSession);
            ArgumentNullException.ThrowIfNull(immutableSourceEvidence);
            ArgumentNullException.ThrowIfNull(preparedDestination);
            ArgumentNullException.ThrowIfNull(attemptStore);
            identity.Validate();
            candidateBinding.ValidateIdentity(identity);
            var now = timeProvider.GetUtcNow();
            if (expectedAttemptRevision < 0 ||
                timeoutMilliseconds is < 1 or > ManagedBrowserProtocol.MaximumOperationTimeoutMilliseconds ||
                candidateBinding.Authorization.IsRecovery || candidateBinding.Binding.IsRecovery ||
                candidateBinding.Binding.IssuedAtUtc > now || candidateBinding.Binding.ExpiresAtUtc <= now ||
                candidateBinding.Binding.ExpiresAtUtc <= candidateBinding.Binding.IssuedAtUtc ||
                candidateBinding.Binding.Capability.AdapterKind != "BaiduTransfer" ||
                executionContext.Engine != BaiduExecutionEngine.ManagedChromiumV1 ||
                executionContext.ManagedSession is not { } executionSession ||
                identity.OwnerKind != WebAutomationOwnerKind.Transfer ||
                !SameIdentity(executionSession, exactManagedSession) ||
                !IdentityMatches(identity, exactManagedSession) ||
                !HandleMatches(exactHandle, exactManagedSession) ||
                !string.Equals(candidateBinding.ExpectedBaiduUserId,
                    exactManagedSession.StableBaiduUserId, StringComparison.Ordinal) ||
                candidateBinding.Binding.Capability != preparedDestination.Capability ||
                preparedDestination.ExecutionEngine != BaiduExecutionEngine.ManagedChromiumV1 ||
                preparedDestination.OperationId != identity.OperationId ||
                preparedDestination.AccountId != identity.AccountId ||
                preparedDestination.Generation != identity.Generation ||
                preparedDestination.OwningAttemptRevision != expectedAttemptRevision ||
                !string.Equals(preparedDestination.SourceObservationFingerprint,
                    immutableSourceEvidence.ObservationFingerprint, StringComparison.Ordinal) ||
                !string.Equals(preparedDestination.FolderCandidatesSnapshot.BaiduUserId,
                    exactManagedSession.StableBaiduUserId, StringComparison.Ordinal) ||
                !string.Equals(immutableSourceEvidence.BaiduUserId,
                    exactManagedSession.StableBaiduUserId, StringComparison.Ordinal) ||
                !string.Equals(immutableSourceEvidence.ComputeFingerprint(canonicalSourceUri),
                    immutableSourceEvidence.ObservationFingerprint, StringComparison.Ordinal))
                return false;
            _ = new ManagedBrowserTargetActionBinding(exactHandle.BrowserId, exactHandle.TargetId,
                exactHandle.Endpoint, timeoutMilliseconds, canonicalSourceUri,
                exactManagedSession.StableBaiduUserId, immutableSourceEvidence);
            return true;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool ExactPreparingAttempt(WebAutomationAttemptRecord? attempt,
        WebAutomationRequestIdentity identity, ManagedBrowserSourceSnapshot source,
        ManagedPreparedDestinationEvidence preparedDestination, long expectedRevision)
    {
        if (attempt is null || attempt.OperationId != identity.OperationId ||
            attempt.AccountId != identity.AccountId || attempt.Generation != identity.Generation ||
            attempt.OwnerKind != WebAutomationOwnerKind.Transfer ||
            attempt.State != WebAutomationAttemptState.Preparing || attempt.Revision != expectedRevision ||
            attempt.ResumeStage is not null || attempt.IsWaitingForUser ||
            attempt.Baseline is not null || attempt.TargetPreparationEvidence is not null ||
            attempt.SubmissionEvidence is not null || attempt.OfficialRequestId is not null ||
            attempt.TransferIdentityMapping is not null || attempt.AcceptedRemoteItems is not null ||
            attempt.SourceEvidence is not { } persistedSource ||
            !attempt.ExecutionProfile.Equals(identity.ExecutionProfile) ||
            persistedSource.Capability != preparedDestination.Capability ||
            !string.Equals(persistedSource.Items.Fingerprint,
                preparedDestination.SourceFingerprint, StringComparison.Ordinal) ||
            !string.Equals(persistedSource.ObservationFingerprint,
                preparedDestination.SourceObservationFingerprint, StringComparison.Ordinal))
            return false;
        return SourceMatches(persistedSource, source);
    }

    private static bool SourceMatches(TransferSourceEvidence durable, ManagedBrowserSourceSnapshot wire) =>
        durable.TotalCount == wire.TotalCount && durable.ReachedEnd && durable.IsStable &&
        string.Equals(durable.GenerationToken, wire.GenerationToken, StringComparison.Ordinal) &&
        string.Equals(durable.BaiduUserId, wire.BaiduUserId, StringComparison.Ordinal) &&
        string.Equals(durable.ObservationFingerprint, wire.ObservationFingerprint, StringComparison.Ordinal) &&
        durable.Items.Items.Select(item => item.ExactId)
            .SequenceEqual(wire.Items.Select(item => item.ExactId), StringComparer.Ordinal);

    private static bool IdentityMatches(WebAutomationRequestIdentity identity,
        ManagedBrowserSessionReference session) =>
        identity.AccountId == session.AccountId &&
        identity.ExecutionProfile.Engine == BaiduExecutionEngine.ManagedChromiumV1 &&
        identity.ExecutionProfile.InstallationId == session.InstallationId &&
        identity.ExecutionProfile.SessionId == session.SessionId &&
        identity.ExecutionProfile.BrowserFamily == session.BrowserFamily &&
        string.Equals(identity.ExecutionProfile.ProfileId, session.ProfileId, StringComparison.Ordinal);

    private static bool SameIdentity(ManagedBrowserSessionReference left,
        ManagedBrowserSessionReference right) =>
        left.SessionId == right.SessionId && left.InstallationId == right.InstallationId &&
        left.AccountId == right.AccountId && left.BrowserFamily == right.BrowserFamily &&
        string.Equals(left.StableBaiduUserId, right.StableBaiduUserId, StringComparison.Ordinal) &&
        string.Equals(left.ProfileId, right.ProfileId, StringComparison.Ordinal) &&
        string.Equals(left.ProfileOwnershipId, right.ProfileOwnershipId, StringComparison.Ordinal);

    private static bool HandleMatches(ManagedChromiumSessionHandle handle,
        ManagedBrowserSessionReference session) =>
        handle.Installation.InstallationId == session.InstallationId &&
        handle.Installation.BrowserFamily == session.BrowserFamily &&
        handle.Profile.AccountId == session.AccountId && handle.Profile.Family == session.BrowserFamily &&
        Guid.TryParse(session.ProfileId, out var profileId) && handle.Profile.ProfileId == profileId &&
        Guid.TryParse(session.ProfileOwnershipId, out var ownershipId) &&
        handle.Profile.OwnershipId == ownershipId;

    private static ManagedTransferTargetBaselineResult Denied() =>
        new(ManagedTransferTargetBaselineResultKind.PageNotValidated);
}
