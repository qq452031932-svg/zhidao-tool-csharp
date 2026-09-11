using System.Data.Common;
using System.IO;
using BaiduBatchTransfer.App.Features.BrowserAutomation;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.BaiduAdapter.WebAutomation;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.App.Features.ManagedBrowser;

internal sealed class ManagedTransferPickerSessionResult
{
    internal ManagedTransferPickerSessionResult(ManagedBrowserTransferPickerResultKind kind,
        BaiduTransferCandidateObservation? observation = null, ManagedTransferPickerQuarantine? quarantine = null)
    {
        Kind = kind;
        Observation = observation;
        Quarantine = quarantine;
    }

    internal ManagedBrowserTransferPickerResultKind Kind { get; }
    internal BaiduTransferCandidateObservation? Observation { get; }
    internal ManagedTransferPickerQuarantine? Quarantine { get; }
    public override string ToString() =>
        $"ManagedTransferPickerSessionResult {{ Kind = {Kind}, Observation = [REDACTED], Quarantine = {(Quarantine is null ? "None" : "Held")} }}";
}

internal sealed class ManagedTransferPickerHeldPreparationResult
{
    internal ManagedTransferPickerHeldPreparationResult(ManagedTransferPickerHeldPreparationKind kind,
        ManagedTransferPickerHeldPreparation? preparation = null)
    {
        if ((kind == ManagedTransferPickerHeldPreparationKind.ReadyNotOpened) != (preparation is not null))
            throw new ArgumentException("Only a validated held preparation carries ownership.", nameof(preparation));
        Kind = kind;
        Preparation = preparation;
    }

    internal ManagedTransferPickerHeldPreparationKind Kind { get; }
    internal ManagedTransferPickerHeldPreparation? Preparation { get; }
    public override string ToString() =>
        $"ManagedTransferPickerHeldPreparationResult {{ Kind = {Kind}, Preparation = {(Preparation is null ? "None" : "Held")} }}";
}

internal enum ManagedTransferPickerHeldPreparationKind
{
    ReadyNotOpened = 1,
    PageNotValidated,
    AccountIdentityMismatch,
    TimedOut,
    Cancelled,
    BrowserUnavailable,
}

internal sealed class ManagedTransferOperationDeadline : IDisposable
{
    private readonly CancellationToken callerToken;
    private readonly TimeProvider timeProvider;
    private readonly CancellationTokenSource linked;
    private readonly ITimer timer;
    private readonly long started;

    internal ManagedTransferOperationDeadline(int timeoutMilliseconds, CancellationToken callerToken,
        TimeProvider? timeProvider = null)
    {
        if (timeoutMilliseconds is < 1 or > ManagedBrowserProtocol.MaximumOperationTimeoutMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
        TimeoutMilliseconds = timeoutMilliseconds;
        this.callerToken = callerToken;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        started = this.timeProvider.GetTimestamp();
        linked = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        timer = this.timeProvider.CreateTimer(CancelDeadline, linked,
            TimeSpan.FromMilliseconds(timeoutMilliseconds), Timeout.InfiniteTimeSpan);
    }

    internal int TimeoutMilliseconds { get; }
    internal CancellationToken Token => linked.Token;
    internal bool CallerCancelled => callerToken.IsCancellationRequested;

    internal void RequireBudget()
    {
        callerToken.ThrowIfCancellationRequested();
        if (ElapsedMilliseconds >= TimeoutMilliseconds) throw new TimeoutException();
        linked.Token.ThrowIfCancellationRequested();
    }

    internal int RemainingMilliseconds()
    {
        RequireBudget();
        var calculated = (int)Math.Ceiling(TimeoutMilliseconds - ElapsedMilliseconds);
        return TimeoutMilliseconds == 1
            ? 1
            : Math.Max(1, Math.Min(TimeoutMilliseconds - 1, calculated));
    }

    internal int RemainingOrOneMillisecond() =>
        Math.Max(1, Math.Min(ManagedBrowserProtocol.MaximumOperationTimeoutMilliseconds,
            (int)Math.Ceiling(TimeoutMilliseconds - ElapsedMilliseconds)));

    internal ManagedBrowserOpenTransferSourceRequest RemainingBinding(
        ManagedBrowserOpenTransferSourceRequest binding) => new(
            binding.BrowserId, binding.TargetId, binding.DevToolsEndpoint, RemainingMilliseconds(),
            binding.SourceUri, binding.ExpectedBaiduUserId);

    private double ElapsedMilliseconds => timeProvider.GetElapsedTime(started).TotalMilliseconds;

    private static void CancelDeadline(object? state)
    {
        try { ((CancellationTokenSource)state!).Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        timer.Dispose();
        linked.Dispose();
    }
}

/// <summary>
/// Opaque process-local continuation holding the coordinator account fence. It has no public release surface;
/// only the directory-preparation runner may consume it. Production wiring must reconstruct any durable
/// ResultPending/quarantine ownership after process restart before allowing this account to be reused.
/// </summary>
internal sealed class ManagedTransferPickerHeldPreparation
{
    private IDisposable? accountLease;
    private readonly bool ownsDeadline;

    internal ManagedTransferPickerHeldPreparation(ManagedBrowserLifecycleSession capturedSession,
        ManagedChromiumSessionHandle exactHandle, ManagedBrowserOpenTransferSourceRequest binding,
        WebAutomationRequestIdentity identity, BaiduCandidateSessionBinding candidateBinding,
        BaiduExecutionContext executionContext, IDisposable accountLease,
        ManagedTransferOperationDeadline deadline, bool ownsDeadline)
    {
        CapturedSession = capturedSession;
        ExactHandle = exactHandle;
        Binding = binding;
        Identity = identity;
        CandidateBinding = candidateBinding;
        ExecutionContext = executionContext;
        Deadline = deadline;
        this.ownsDeadline = ownsDeadline;
        this.accountLease = accountLease;
    }

    internal ManagedBrowserLifecycleSession CapturedSession { get; }
    internal ManagedChromiumSessionHandle ExactHandle { get; }
    internal ManagedBrowserOpenTransferSourceRequest Binding { get; }
    internal WebAutomationRequestIdentity Identity { get; }
    internal BaiduCandidateSessionBinding CandidateBinding { get; }
    internal BaiduExecutionContext ExecutionContext { get; }
    internal ManagedTransferOperationDeadline Deadline { get; }

    internal IDisposable ConsumeAccountLease()
    {
        var owned = Interlocked.Exchange(ref accountLease, null) ??
            throw new InvalidOperationException("The held preparation was already consumed.");
        return ownsDeadline ? new DeadlineOwningLease(owned, Deadline) : owned;
    }

    internal void ReleaseIfUnconsumed()
    {
        var owned = Interlocked.Exchange(ref accountLease, null);
        if (owned is null) return;
        try { owned.Dispose(); }
        catch (Exception) { /* Coordinator leases are nonthrowing; remain defensive and redact. */ }
        if (ownsDeadline) Deadline.Dispose();
    }

    public override string ToString() => "ManagedTransferPickerHeldPreparation { Ownership = Held, Evidence = [REDACTED] }";

    private sealed class DeadlineOwningLease(IDisposable accountLease,
        ManagedTransferOperationDeadline deadline) : IDisposable
    {
        private IDisposable? ownedLease = accountLease;
        private ManagedTransferOperationDeadline? ownedDeadline = deadline;

        public void Dispose()
        {
            var lease = Interlocked.Exchange(ref ownedLease, null);
            var deadlineToDispose = Interlocked.Exchange(ref ownedDeadline, null);
            try { lease?.Dispose(); }
            finally { deadlineToDispose?.Dispose(); }
        }
    }
}

internal enum ManagedTransferPickerReconciliationStatus
{
    Released = 1,
    RetainedCancelled,
    RetainedTimedOut,
    RetainedFailed,
    RetainedStale,
    RetainedMismatch,
    RetainedInconclusive,
    AlreadyInProgress,
    AlreadyReleased,
}

/// <summary>
/// Process-local containment for a possibly-dispatched picker mutation. This is deliberately not disposable:
/// it can surrender its account lease only after its fixed read-only reconciliation proves the captured state.
/// Process-local quarantine is insufficient across restart; production wiring must durably recover ResultPending
/// before allowing the account to be reused.
/// </summary>
internal sealed class ManagedTransferPickerQuarantine
{
    private readonly ManagedBrowserLifecycleCoordinator lifecycle;
    private readonly ManagedChromiumProcessManager manager;
    private readonly Func<ManagedBrowserRequest, CancellationToken, Task<ManagedBrowserResponse>> send;
    private readonly ManagedBrowserLifecycleSession captured;
    private readonly ManagedChromiumSessionHandle exactHandle;
    private readonly ManagedBrowserOpenTransferSourceRequest fixedBinding;
    private readonly Guid operationId;
    private readonly Guid accountId;
    private readonly long generation;
    private readonly TimeProvider timeProvider;
    private IDisposable? lease;
    private Task<ManagedBrowserResponse>? unsettledRead;
    private int state;

    internal ManagedTransferPickerQuarantine(ManagedBrowserLifecycleCoordinator lifecycle,
        ManagedChromiumProcessManager manager,
        Func<ManagedBrowserRequest, CancellationToken, Task<ManagedBrowserResponse>> send,
        ManagedBrowserLifecycleSession captured, ManagedChromiumSessionHandle exactHandle,
        ManagedBrowserOpenTransferSourceRequest fixedBinding, Guid operationId, Guid accountId,
        long generation, IDisposable lease, TimeProvider timeProvider)
    {
        this.lifecycle = lifecycle;
        this.manager = manager;
        this.send = send;
        this.captured = captured;
        this.exactHandle = exactHandle;
        this.fixedBinding = fixedBinding;
        this.operationId = operationId;
        this.accountId = accountId;
        this.generation = generation;
        this.lease = lease;
        this.timeProvider = timeProvider;
    }

    internal async Task<ManagedTransferPickerReconciliationStatus> ReconcileAsync(int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        var prior = Interlocked.CompareExchange(ref state, 1, 0);
        if (prior == 1) return ManagedTransferPickerReconciliationStatus.AlreadyInProgress;
        if (prior == 2) return ManagedTransferPickerReconciliationStatus.AlreadyReleased;
        if (timeoutMilliseconds is < 1 or > ManagedBrowserProtocol.MaximumOperationTimeoutMilliseconds)
        {
            Volatile.Write(ref state, 0);
            return ManagedTransferPickerReconciliationStatus.RetainedInconclusive;
        }

        using var deadline = new ManagedTransferOperationDeadline(
            timeoutMilliseconds, cancellationToken, timeProvider);
        try
        {
            await SettlePriorReadAsync(deadline.Token).ConfigureAwait(false);
            deadline.RequireBudget();
            var initial = await LoadSnapshotAsync(deadline.Token).ConfigureAwait(false);
            deadline.RequireBudget();
            if (initial is null || !SameSnapshot(initial, captured))
                return Retained(ManagedTransferPickerReconciliationStatus.RetainedStale);
            if (!await manager.VerifyTransferSourceAsync(exactHandle,
                    deadline.RemainingBinding(fixedBinding), deadline.Token).ConfigureAwait(false))
                return Retained(ManagedTransferPickerReconciliationStatus.RetainedMismatch);
            deadline.RequireBudget();

            var reader = new ManagedTransferDirectoryReader(SendReadOnlyAsync);
            var directory = await reader.ReadAsync(operationId, accountId, generation,
                new ManagedBrowserReadTransferDirectoryRequest(
                    deadline.RemainingBinding(fixedBinding), null, null), deadline.Token).ConfigureAwait(false);
            deadline.RequireBudget();
            if (directory.Kind != ManagedBrowserDirectoryListingResultKind.Observed || directory.Observation is null)
                return Retained(Map(directory.Kind));

            var final = await LoadSnapshotAsync(deadline.Token).ConfigureAwait(false);
            deadline.RequireBudget();
            if (final is null || !SameSnapshot(final, captured))
                return Retained(ManagedTransferPickerReconciliationStatus.RetainedStale);
            if (!await manager.VerifyTransferSourceAsync(exactHandle,
                    deadline.RemainingBinding(fixedBinding), deadline.Token).ConfigureAwait(false))
                return Retained(ManagedTransferPickerReconciliationStatus.RetainedMismatch);
            deadline.RequireBudget();
            if (unsettledRead is not null)
                return Retained(ManagedTransferPickerReconciliationStatus.RetainedInconclusive);

            var owned = lease;
            if (owned is null)
            {
                Volatile.Write(ref state, 2);
                return ManagedTransferPickerReconciliationStatus.AlreadyReleased;
            }
            // Every quarantine lease originates from the coordinator's private idempotent, nonthrowing AccountLease.
            // Keep the reference until that known release has completed so ownership is never cleared prematurely.
            owned.Dispose();
            lease = null;
            Volatile.Write(ref state, 2);
            return ManagedTransferPickerReconciliationStatus.Released;
        }
        catch (OperationCanceledException)
        {
            return Retained(cancellationToken.IsCancellationRequested
                ? ManagedTransferPickerReconciliationStatus.RetainedCancelled
                : ManagedTransferPickerReconciliationStatus.RetainedTimedOut);
        }
        catch (TimeoutException)
        {
            return Retained(ManagedTransferPickerReconciliationStatus.RetainedTimedOut);
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or ManagedBrowserProtocolException)
        {
            return Retained(ManagedTransferPickerReconciliationStatus.RetainedInconclusive);
        }
        catch (Exception)
        {
            return Retained(ManagedTransferPickerReconciliationStatus.RetainedFailed);
        }

        ManagedTransferPickerReconciliationStatus Retained(ManagedTransferPickerReconciliationStatus status)
        {
            Volatile.Write(ref state, 0);
            return status;
        }

    }

    private async Task<ManagedBrowserResponse> SendReadOnlyAsync(ManagedBrowserRequest request, CancellationToken token)
    {
        var pending = send(request, token);
        unsettledRead = pending;
        try
        {
            var response = await pending.WaitAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return response;
        }
        finally
        {
            // Attach before inspecting completion so cancellation cannot race a late fault between checks.
            ObserveDetachedRead(pending);
            if (pending.IsCompleted && ReferenceEquals(unsettledRead, pending)) unsettledRead = null;
        }
    }

    private static void ObserveDetachedRead(Task<ManagedBrowserResponse> pending)
    {
        // Observation is containment only: it neither clears unsettledRead, releases the lease,
        // nor starts another reconciliation. A later attempt still settles this exact task itself.
        _ = ObserveAsync(pending);

        static async Task ObserveAsync(Task task)
        {
            try { await task.ConfigureAwait(false); }
            catch (Exception) { /* Private late helper failures never escape quarantine. */ }
        }
    }

    private async Task SettlePriorReadAsync(CancellationToken token)
    {
        var pending = unsettledRead;
        if (pending is null) return;
        try { _ = await pending.WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { /* A settled private helper failure is consumed before a fresh fixed read. */ }
        finally
        {
            if (pending.IsCompleted && ReferenceEquals(unsettledRead, pending)) unsettledRead = null;
        }
    }

    private async Task<ManagedBrowserLifecycleSession?> LoadSnapshotAsync(CancellationToken token)
    {
        try { return await lifecycle.LoadPersistedAsync(accountId, token).ConfigureAwait(false); }
        catch (InvalidOperationException) { return null; }
    }

    private static ManagedTransferPickerReconciliationStatus Map(ManagedBrowserDirectoryListingResultKind kind) => kind switch
    {
        ManagedBrowserDirectoryListingResultKind.Cancelled => ManagedTransferPickerReconciliationStatus.RetainedCancelled,
        ManagedBrowserDirectoryListingResultKind.TimedOut => ManagedTransferPickerReconciliationStatus.RetainedTimedOut,
        ManagedBrowserDirectoryListingResultKind.AccountIdentityMismatch => ManagedTransferPickerReconciliationStatus.RetainedMismatch,
        ManagedBrowserDirectoryListingResultKind.BrowserUnavailable => ManagedTransferPickerReconciliationStatus.RetainedFailed,
        _ => ManagedTransferPickerReconciliationStatus.RetainedInconclusive,
    };

    public override string ToString() =>
        $"ManagedTransferPickerQuarantine {{ State = {(Volatile.Read(ref state) == 2 ? "Reconciled" : "Held")}, Evidence = [REDACTED] }}";

    private static bool SameSnapshot(ManagedBrowserLifecycleSession left, ManagedBrowserLifecycleSession right) =>
        left.InstallationRevision == right.InstallationRevision && left.SessionRevision == right.SessionRevision &&
        SameIdentity(left.Session, right.Session) &&
        string.Equals(left.CanonicalProfilePath, right.CanonicalProfilePath, StringComparison.OrdinalIgnoreCase);

    private static bool SameIdentity(ManagedBrowserSessionReference left, ManagedBrowserSessionReference right) =>
        left.SessionId == right.SessionId && left.InstallationId == right.InstallationId && left.AccountId == right.AccountId &&
        left.BrowserFamily == right.BrowserFamily &&
        string.Equals(left.StableBaiduUserId, right.StableBaiduUserId, StringComparison.Ordinal) &&
        string.Equals(left.ProfileId, right.ProfileId, StringComparison.Ordinal) &&
        string.Equals(left.ProfileOwnershipId, right.ProfileOwnershipId, StringComparison.Ordinal);
}

/// <summary>Internal account-bound source-to-picker orchestration; no production wiring is provided here.</summary>
internal sealed class ManagedTransferPickerSession
{
    private readonly ManagedBrowserLifecycleCoordinator lifecycle;
    private readonly ManagedChromiumProcessManager manager;
    private readonly Func<ManagedBrowserRequest, CancellationToken, Task<ManagedBrowserResponse>> send;
    private readonly IBaiduCandidateActionAuthorizationSource authorizationSource;
    private readonly TimeProvider timeProvider;

    internal ManagedTransferPickerSession(ManagedBrowserLifecycleCoordinator lifecycle,
        ManagedChromiumProcessManager manager,
        Func<ManagedBrowserRequest, CancellationToken, Task<ManagedBrowserResponse>> send,
        IBaiduCandidateActionAuthorizationSource authorizationSource,
        TimeProvider? timeProvider = null)
    {
        this.lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        this.manager = manager ?? throw new ArgumentNullException(nameof(manager));
        this.send = send ?? throw new ArgumentNullException(nameof(send));
        this.authorizationSource = authorizationSource ?? throw new ArgumentNullException(nameof(authorizationSource));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal async Task<ManagedTransferPickerHeldPreparationResult> BeginHeldPreparationAsync(
        WebAutomationRequestIdentity identity, BaiduCandidateSessionBinding candidateBinding,
        BaiduExecutionContext executionContext, ManagedChromiumSessionHandle exactHandle,
        string canonicalSourceUri, int timeoutMilliseconds, CancellationToken cancellationToken = default)
    {
        ManagedTransferOperationDeadline? deadline = null;
        try
        {
            deadline = new(timeoutMilliseconds, cancellationToken, timeProvider);
            var result = await BeginHeldPreparationUnderDeadlineAsync(identity, candidateBinding, executionContext, exactHandle,
                canonicalSourceUri, deadline, ownsDeadline: true).ConfigureAwait(false);
            if (result.Preparation is not null) deadline = null;
            return result;
        }
        catch (ArgumentOutOfRangeException)
        {
            return new(ManagedTransferPickerHeldPreparationKind.PageNotValidated);
        }
        finally
        {
            deadline?.Dispose();
        }
    }

    internal async Task<ManagedTransferPickerHeldPreparationResult> BeginHeldPreparationUnderDeadlineAsync(
        WebAutomationRequestIdentity identity, BaiduCandidateSessionBinding candidateBinding,
        BaiduExecutionContext executionContext, ManagedChromiumSessionHandle exactHandle,
        string canonicalSourceUri, ManagedTransferOperationDeadline deadline, bool ownsDeadline = false)
    {
        if (identity is null || candidateBinding is null ||
            executionContext?.Engine != BaiduExecutionEngine.ManagedChromiumV1 ||
            executionContext.ManagedSession is not { } managedIdentity || exactHandle is null || deadline is null)
            return new(ManagedTransferPickerHeldPreparationKind.PageNotValidated);

        try
        {
            deadline.RequireBudget();
            identity.Validate();
            candidateBinding.ValidateIdentity(identity);
            var now = timeProvider.GetUtcNow();
            if (candidateBinding.Authorization.IsRecovery || candidateBinding.Binding.IsRecovery ||
                candidateBinding.Binding.IssuedAtUtc > now ||
                candidateBinding.Binding.ExpiresAtUtc <= now ||
                candidateBinding.Binding.ExpiresAtUtc <= candidateBinding.Binding.IssuedAtUtc ||
                candidateBinding.Binding.Capability.AdapterKind != "BaiduTransfer" ||
                !SameIdentity(managedIdentity, exactHandle, identity) ||
                !string.Equals(candidateBinding.ExpectedBaiduUserId, managedIdentity.StableBaiduUserId,
                    StringComparison.Ordinal))
                return new(ManagedTransferPickerHeldPreparationKind.PageNotValidated);
            _ = new ManagedBrowserOpenTransferSourceRequest(exactHandle.BrowserId, exactHandle.TargetId,
                exactHandle.Endpoint, deadline.RemainingMilliseconds(), canonicalSourceUri, managedIdentity.StableBaiduUserId);
        }
        catch (ArgumentException) { return new(ManagedTransferPickerHeldPreparationKind.PageNotValidated); }
        catch (InvalidOperationException) { return new(ManagedTransferPickerHeldPreparationKind.PageNotValidated); }
        catch (TimeoutException) { return new(ManagedTransferPickerHeldPreparationKind.TimedOut); }

        IDisposable? lease = null;
        try
        {
            lease = await lifecycle.AcquireAccountLeaseAsync(identity.AccountId, deadline.Token).ConfigureAwait(false);
            deadline.RequireBudget();
            if (!CandidateBindingIsCurrent(candidateBinding))
                return DeniedHeld(ManagedTransferPickerHeldPreparationKind.PageNotValidated);
            var captured = await LoadSnapshotAsync(identity.AccountId, deadline.Token).ConfigureAwait(false);
            deadline.RequireBudget();
            if (captured is null || !SameIdentity(captured.Session, managedIdentity) ||
                !MatchesHandle(captured, exactHandle))
                return DeniedHeld(ManagedTransferPickerHeldPreparationKind.AccountIdentityMismatch);
            var binding = new ManagedBrowserOpenTransferSourceRequest(exactHandle.BrowserId, exactHandle.TargetId,
                exactHandle.Endpoint, deadline.RemainingMilliseconds(), canonicalSourceUri, managedIdentity.StableBaiduUserId);
            var held = new ManagedTransferPickerHeldPreparation(captured, exactHandle, binding, identity,
                candidateBinding, executionContext, lease, deadline, ownsDeadline);
            lease = null;
            return new(ManagedTransferPickerHeldPreparationKind.ReadyNotOpened, held);
        }
        catch (OperationCanceledException)
        {
            return DeniedHeld(deadline.CallerCancelled
                ? ManagedTransferPickerHeldPreparationKind.Cancelled
                : ManagedTransferPickerHeldPreparationKind.TimedOut);
        }
        catch (TimeoutException)
        {
            return DeniedHeld(ManagedTransferPickerHeldPreparationKind.TimedOut);
        }
        catch (Exception)
        {
            return DeniedHeld(ManagedTransferPickerHeldPreparationKind.BrowserUnavailable);
        }

        ManagedTransferPickerHeldPreparationResult DeniedHeld(ManagedTransferPickerHeldPreparationKind kind)
        {
            try { lease?.Dispose(); }
            catch (Exception) { }
            lease = null;
            return new(kind);
        }
    }

    private bool CandidateBindingIsCurrent(BaiduCandidateSessionBinding candidateBinding)
    {
        var now = timeProvider.GetUtcNow();
        return !candidateBinding.Authorization.IsRecovery && !candidateBinding.Binding.IsRecovery &&
            candidateBinding.Binding.IssuedAtUtc <= now && candidateBinding.Binding.ExpiresAtUtc > now &&
            candidateBinding.Binding.ExpiresAtUtc > candidateBinding.Binding.IssuedAtUtc;
    }

    internal async Task<ManagedTransferPickerSessionResult> OpenAsync(BaiduCandidateDomMutationRequest request,
        BaiduExecutionContext executionContext, ManagedChromiumSessionHandle exactHandle, string canonicalSourceUri,
        int timeoutMilliseconds, CancellationToken cancellationToken = default)
    {
        if (request is null || executionContext?.Engine != BaiduExecutionEngine.ManagedChromiumV1 ||
            executionContext.ManagedSession is not { } identity || exactHandle is null ||
            timeoutMilliseconds is < 1 or > ManagedBrowserProtocol.MaximumOperationTimeoutMilliseconds ||
            request.Authorization.AccountId != identity.AccountId || request.Authorization.Generation <= 0 ||
            !RequestBindingIsCurrent(request, identity, canonicalSourceUri))
            return Denied(ManagedBrowserTransferPickerResultKind.PageNotValidated);

        try
        {
            _ = FixedBinding(timeoutMilliseconds);
        }
        catch (ArgumentException)
        {
            return Denied(ManagedBrowserTransferPickerResultKind.PageNotValidated);
        }

        using var deadline = new ManagedTransferOperationDeadline(
            timeoutMilliseconds, cancellationToken, timeProvider);
        IDisposable? accountLease = null;
        Task sourceSettlement = Task.CompletedTask;
        ManagedBrowserLifecycleSession? captured = null;
        var possiblePickerDispatch = false;
        try
        {
            RequireBudget();
            // Only the coordinator's private idempotent, nonthrowing AccountLease may cross into quarantine.
            accountLease = await lifecycle.AcquireAccountLeaseAsync(identity.AccountId, deadline.Token).ConfigureAwait(false);
            RequireBudget();
            captured = await LoadSnapshotAsync(identity.AccountId, deadline.Token).ConfigureAwait(false);
            RequireBudget();
            if (captured is null || !SameIdentity(captured.Session, identity) || !MatchesHandle(captured, exactHandle))
                return Finish(Denied(ManagedBrowserTransferPickerResultKind.AccountIdentityMismatch));

            var sourceSession = new ManagedTransferSourceSession(lifecycle, manager, send);
            var source = await sourceSession.OpenAndReadUnderAccountLeaseAsync(request.Authorization.OperationId,
                request.Authorization.Generation, executionContext, exactHandle, canonicalSourceUri,
                RemainingMilliseconds(), deadline.Token).ConfigureAwait(false);
            sourceSettlement = source.PendingSendsSettled;
            RequireBudget();
            if (source.ReadResult.Kind != ManagedBrowserSourceListingResultKind.Observed || source.ReadResult.Observation is null)
                return Finish(Denied(Map(source.ReadResult.Kind)));
            await sourceSettlement.WaitAsync(deadline.Token).ConfigureAwait(false);
            RequireBudget();

            if (!await manager.VerifyTransferSourceAsync(exactHandle, RemainingBinding(), deadline.Token).ConfigureAwait(false))
                return Finish(Denied(ManagedBrowserTransferPickerResultKind.PageNotValidated));
            RequireBudget();
            var preClick = await LoadSnapshotAsync(identity.AccountId, deadline.Token).ConfigureAwait(false);
            RequireBudget();
            if (preClick is null || !SameSnapshot(preClick, captured) || !MatchesHandle(preClick, exactHandle))
                return Finish(Denied(ManagedBrowserTransferPickerResultKind.AccountIdentityMismatch));

            possiblePickerDispatch = true;
            var opened = await new ManagedTransferPickerOpener(send, authorizationSource,
                RevalidateBeforeDispatchAsync, timeProvider).OpenAsync(request,
                RemainingBinding(), source.ReadResult.Observation, deadline.Token).ConfigureAwait(false);
            if (opened.Kind == ManagedBrowserTransferPickerResultKind.OutcomeUnknown)
                return Quarantine();
            if (opened.Kind != ManagedBrowserTransferPickerResultKind.PickerOpened || opened.Observation is null)
            {
                possiblePickerDispatch = false;
                // The opener's linked token is our deadline token, so it may conservatively report Cancelled.
                // Recheck the outer clock/caller token before publishing a known-no-dispatch result.
                RequireBudget();
                return Finish(Denied(opened.Kind));
            }

            if (!await manager.VerifyTransferSourceAsync(exactHandle, RemainingBinding(), deadline.Token).ConfigureAwait(false))
                return Quarantine();
            RequireBudget();
            var final = await LoadSnapshotAsync(identity.AccountId, deadline.Token).ConfigureAwait(false);
            RequireBudget();
            if (final is null || !SameSnapshot(final, preClick) || !MatchesHandle(final, exactHandle))
                return Quarantine();

            possiblePickerDispatch = false;
            accountLease?.Dispose();
            accountLease = null;
            return new(ManagedBrowserTransferPickerResultKind.PickerOpened, opened.Observation);
        }
        catch (OperationCanceledException)
        {
            if (possiblePickerDispatch && captured is not null && accountLease is not null) return Quarantine();
            return Finish(Denied(cancellationToken.IsCancellationRequested
                ? ManagedBrowserTransferPickerResultKind.Cancelled
                : ManagedBrowserTransferPickerResultKind.TimedOut));
        }
        catch (TimeoutException)
        {
            if (possiblePickerDispatch && captured is not null && accountLease is not null) return Quarantine();
            return Finish(Denied(ManagedBrowserTransferPickerResultKind.TimedOut));
        }
        catch (Exception error) when (error is IOException or DbException or UnauthorizedAccessException or ObjectDisposedException or
            InvalidOperationException or ArgumentException or ManagedBrowserProtocolException or ManagedBrowserHelperClientException)
        {
            if (possiblePickerDispatch && captured is not null && accountLease is not null) return Quarantine();
            return Finish(Denied(ManagedBrowserTransferPickerResultKind.BrowserUnavailable));
        }
        catch (Exception)
        {
            if (possiblePickerDispatch && captured is not null && accountLease is not null) return Quarantine();
            return Finish(Denied(ManagedBrowserTransferPickerResultKind.BrowserUnavailable));
        }

        ManagedTransferPickerSessionResult Quarantine()
        {
            var binding = FixedBinding(RemainingOrOneMillisecond());
            var owned = accountLease!;
            var quarantine = new ManagedTransferPickerQuarantine(lifecycle, manager, send, captured!, exactHandle,
                binding, request.Authorization.OperationId, identity.AccountId,
                request.Authorization.Generation, owned, timeProvider);
            accountLease = null;
            return new(ManagedBrowserTransferPickerResultKind.OutcomeUnknown, quarantine: quarantine);
        }

        ManagedTransferPickerSessionResult Finish(ManagedTransferPickerSessionResult result)
        {
            var owned = accountLease;
            if (owned is not null)
            {
                _ = DisposeAfterSettlementAsync(owned, sourceSettlement);
                accountLease = null;
            }
            return result;
        }

        void RequireBudget() => deadline.RequireBudget();

        int RemainingMilliseconds() => deadline.RemainingMilliseconds();

        int RemainingOrOneMillisecond() => deadline.RemainingOrOneMillisecond();

        ManagedBrowserOpenTransferSourceRequest RemainingBinding() => FixedBinding(RemainingMilliseconds());
        ManagedBrowserOpenTransferSourceRequest FixedBinding(int milliseconds) => new(exactHandle.BrowserId,
            exactHandle.TargetId, exactHandle.Endpoint, milliseconds, canonicalSourceUri, identity.StableBaiduUserId);

        async Task<bool> RevalidateBeforeDispatchAsync(CancellationToken token)
        {
            RequireBudget();
            if (!RequestBindingIsCurrent(request, identity, canonicalSourceUri)) return false;
            if (!await manager.VerifyTransferSourceAsync(exactHandle, RemainingBinding(), token).ConfigureAwait(false))
                return false;
            RequireBudget();
            var current = await LoadSnapshotAsync(identity.AccountId, token).ConfigureAwait(false);
            RequireBudget();
            return current is not null && SameSnapshot(current, captured!) && MatchesHandle(current, exactHandle);
        }
    }

    private bool RequestBindingIsCurrent(BaiduCandidateDomMutationRequest request,
        ManagedBrowserSessionReference identity, string canonicalSourceUri)
    {
        var now = timeProvider.GetUtcNow();
        var binding = request.Binding;
        return !binding.IsRecovery && binding.IssuedAtUtc <= now && binding.ExpiresAtUtc > now &&
            binding.ExpiresAtUtc > binding.IssuedAtUtc && binding.AccountId == identity.AccountId &&
            request.Authorization.AccountId == binding.AccountId &&
            request.Authorization.OperationId == binding.OperationId &&
            request.Authorization.Generation == binding.Generation &&
            request.Authorization.BatchId == binding.BatchId &&
            request.Authorization.Capability == binding.Capability &&
            string.Equals(request.ReadBinding.ExpectedBaiduUserId, identity.StableBaiduUserId,
                StringComparison.Ordinal) &&
            string.Equals(request.ReadBinding.OfficialUri.AbsoluteUri, canonicalSourceUri, StringComparison.Ordinal);
    }

    private async Task<ManagedBrowserLifecycleSession?> LoadSnapshotAsync(Guid accountId, CancellationToken token)
    {
        try { return await lifecycle.LoadPersistedAsync(accountId, token).ConfigureAwait(false); }
        catch (InvalidOperationException) { return null; }
    }

    private static async Task DisposeAfterSettlementAsync(IDisposable lease, Task settlement)
    {
        try { await settlement.ConfigureAwait(false); }
        catch (Exception) { /* The source session reports a non-faulting task; remain defensive and redact. */ }
        try { lease.Dispose(); }
        catch (Exception) { /* Lease disposal errors never disclose account state. */ }
    }

    private static ManagedBrowserTransferPickerResultKind Map(ManagedBrowserSourceListingResultKind kind) => kind switch
    {
        ManagedBrowserSourceListingResultKind.LoginRequired => ManagedBrowserTransferPickerResultKind.LoginRequired,
        ManagedBrowserSourceListingResultKind.VerificationRequired => ManagedBrowserTransferPickerResultKind.VerificationRequired,
        ManagedBrowserSourceListingResultKind.AccountIdentityMismatch => ManagedBrowserTransferPickerResultKind.AccountIdentityMismatch,
        ManagedBrowserSourceListingResultKind.BrowserUnavailable => ManagedBrowserTransferPickerResultKind.BrowserUnavailable,
        ManagedBrowserSourceListingResultKind.TimedOut => ManagedBrowserTransferPickerResultKind.TimedOut,
        ManagedBrowserSourceListingResultKind.Cancelled => ManagedBrowserTransferPickerResultKind.Cancelled,
        _ => ManagedBrowserTransferPickerResultKind.PageNotValidated,
    };

    private static ManagedTransferPickerSessionResult Denied(ManagedBrowserTransferPickerResultKind kind) => new(kind);

    private static bool SameSnapshot(ManagedBrowserLifecycleSession left, ManagedBrowserLifecycleSession right) =>
        left.InstallationRevision == right.InstallationRevision && left.SessionRevision == right.SessionRevision &&
        SameIdentity(left.Session, right.Session) &&
        string.Equals(left.CanonicalProfilePath, right.CanonicalProfilePath, StringComparison.OrdinalIgnoreCase);

    private static bool SameIdentity(ManagedBrowserSessionReference left, ManagedBrowserSessionReference right) =>
        left.SessionId == right.SessionId && left.InstallationId == right.InstallationId && left.AccountId == right.AccountId &&
        left.BrowserFamily == right.BrowserFamily &&
        string.Equals(left.StableBaiduUserId, right.StableBaiduUserId, StringComparison.Ordinal) &&
        string.Equals(left.ProfileId, right.ProfileId, StringComparison.Ordinal) &&
        string.Equals(left.ProfileOwnershipId, right.ProfileOwnershipId, StringComparison.Ordinal);

    private static bool MatchesHandle(ManagedBrowserLifecycleSession snapshot, ManagedChromiumSessionHandle handle) =>
        handle.Installation.InstallationId == snapshot.InstallationId && handle.Installation.BrowserFamily == snapshot.BrowserFamily &&
        handle.Profile.AccountId == snapshot.AccountId && handle.Profile.Family == snapshot.BrowserFamily &&
        Guid.TryParse(snapshot.ProfileId, out var profileId) && handle.Profile.ProfileId == profileId &&
        Guid.TryParse(snapshot.ProfileOwnershipId, out var ownershipId) && handle.Profile.OwnershipId == ownershipId &&
        string.Equals(handle.Profile.CanonicalSourcePath, snapshot.CanonicalProfilePath, StringComparison.OrdinalIgnoreCase);

    private static bool SameIdentity(ManagedBrowserSessionReference managedIdentity,
        ManagedChromiumSessionHandle exactHandle, WebAutomationRequestIdentity identity) =>
        managedIdentity.AccountId == identity.AccountId && identity.OwnerKind == WebAutomationOwnerKind.Transfer &&
        identity.ExecutionProfile.Engine == BaiduExecutionEngine.ManagedChromiumV1 &&
        identity.ExecutionProfile.InstallationId == managedIdentity.InstallationId &&
        identity.ExecutionProfile.SessionId == managedIdentity.SessionId &&
        identity.ExecutionProfile.BrowserFamily == managedIdentity.BrowserFamily &&
        string.Equals(identity.ExecutionProfile.ProfileId, managedIdentity.ProfileId, StringComparison.Ordinal) &&
        exactHandle.Installation.InstallationId == managedIdentity.InstallationId &&
        exactHandle.Profile.AccountId == managedIdentity.AccountId;
}
