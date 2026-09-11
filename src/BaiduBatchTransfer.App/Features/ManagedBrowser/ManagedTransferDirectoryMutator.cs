using System.IO;
using BaiduBatchTransfer.App.Features.BrowserAutomation;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.BaiduAdapter.WebAutomation;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.App.Features.ManagedBrowser;

internal enum ManagedTransferDirectoryMutationOutcome
{
    RootOpened = 1,
    DirectoryEntered,
    CreateDialogOpened,
    DirectoryCreated,
    PickerClosed,
    PageNotValidated,
    Cancelled,
    TimedOut,
    OutcomeUnknown,
    QuarantineRequired,
}

internal sealed record ManagedTransferDirectoryMutationState(
    bool PickerOpen, bool CreateUiOpen, bool PossibleRemoteMutation)
{
    internal static readonly ManagedTransferDirectoryMutationState None = new(false, false, false);
}

internal sealed class ManagedTransferDirectoryMutationExecutionResult
{
    internal ManagedTransferDirectoryMutationExecutionResult(ManagedTransferDirectoryMutationOutcome outcome,
        ManagedTransferDirectoryMutationState state, ManagedTransferDirectoryEvidence? evidence = null,
        Task? pendingSettlement = null, bool currentBoundaryUncertain = false)
    {
        Outcome = outcome;
        State = state ?? throw new ArgumentNullException(nameof(state));
        Evidence = evidence;
        CurrentBoundaryUncertain = currentBoundaryUncertain;
        PendingSettlement = ObserveNonFaultingAsync(pendingSettlement ?? Task.CompletedTask);
    }

    internal ManagedTransferDirectoryMutationOutcome Outcome { get; }
    internal ManagedTransferDirectoryMutationState State { get; }
    internal ManagedTransferDirectoryEvidence? Evidence { get; }
    internal bool CurrentBoundaryUncertain { get; }
    /// <summary>Completes only after every started helper, authorization and permit cleanup task has settled.</summary>
    internal Task PendingSettlement { get; }
    internal ManagedTransferDirectoryMutationExecutionResult WithSettlement(Task settlement) =>
        new(Outcome, State, Evidence, settlement, CurrentBoundaryUncertain);
    public override string ToString() =>
        $"ManagedTransferDirectoryMutationExecutionResult {{ Outcome = {Outcome}, State = {State}, Evidence = [REDACTED], Settlement = [REDACTED] }}";

    private static async Task ObserveNonFaultingAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception) { /* Settlement is an ownership signal and never discloses private task failures. */ }
    }
}

internal sealed record ManagedTransferDirectoryMutationRuntime(
    ManagedBrowserLifecycleSession CapturedSession, ManagedChromiumSessionHandle ExactHandle);

/// <summary>The complete immutable provenance supplied to each runtime proof. Text output is always redacted.</summary>
internal sealed record ManagedTransferDirectoryMutationProof(
    ManagedTransferDirectoryMutationRuntime? Runtime,
    ManagedBrowserOpenTransferSourceRequest Binding,
    WebAutomationRequestIdentity Identity)
{
    public override string ToString() =>
        "ManagedTransferDirectoryMutationProof { Runtime = [REDACTED], Binding = [REDACTED], Identity = [REDACTED] }";
}

/// <summary>
/// Permit-backed fixed-action executor. The caller retains the account lease. Each public action owns one deadline
/// spanning all proofs, authorization, helper work, fresh evidence and permit settlement.
/// </summary>
internal sealed class ManagedTransferDirectoryMutator
{
    private readonly Func<ManagedBrowserRequest, CancellationToken, Task<ManagedBrowserResponse>> send;
    private readonly IBaiduCandidateActionAuthorizationSource authorization;
    private readonly Func<ManagedTransferDirectoryMutationProof, CancellationToken, Task<bool>> prove;
    private readonly TimeProvider timeProvider;

    internal ManagedTransferDirectoryMutator(ManagedBrowserLifecycleCoordinator lifecycle,
        ManagedChromiumProcessManager manager,
        Func<ManagedBrowserRequest, CancellationToken, Task<ManagedBrowserResponse>> send,
        IBaiduCandidateActionAuthorizationSource authorization,
        TimeProvider? timeProvider = null)
        : this(send, authorization,
            (proof, token) => VerifyExactRuntimeAsync(lifecycle, manager, proof, token), timeProvider)
    {
    }

    // Fake-only seam: it receives the complete proof input, never a request factory, click, browser or lease.
    internal ManagedTransferDirectoryMutator(
        Func<ManagedBrowserRequest, CancellationToken, Task<ManagedBrowserResponse>> send,
        IBaiduCandidateActionAuthorizationSource authorization,
        Func<ManagedTransferDirectoryMutationProof, CancellationToken, Task<bool>> fakeProof,
        TimeProvider? timeProvider = null)
    {
        this.send = send ?? throw new ArgumentNullException(nameof(send));
        this.authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        prove = fakeProof ?? throw new ArgumentNullException(nameof(fakeProof));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal Task<ManagedTransferDirectoryMutationExecutionResult> OpenRootAsync(
        WebAutomationRequestIdentity identity, BaiduCandidateSessionBinding candidateBinding,
        ManagedBrowserOpenTransferSourceRequest binding, ManagedTransferDirectoryEvidence evidence,
        ManagedTransferDirectoryMutationState state, CancellationToken cancellationToken = default) =>
        OpenRootAsync(identity, candidateBinding, binding, evidence, state, runtime: null, cancellationToken);

    internal Task<ManagedTransferDirectoryMutationExecutionResult> OpenRootAsync(
        WebAutomationRequestIdentity identity, BaiduCandidateSessionBinding candidateBinding,
        ManagedBrowserOpenTransferSourceRequest binding, ManagedTransferDirectoryEvidence evidence,
        ManagedTransferDirectoryMutationState state, ManagedTransferDirectoryMutationRuntime? runtime,
        CancellationToken cancellationToken = default) =>
        ExecuteAndReadAsync(identity, candidateBinding, binding, evidence, runtime,
            ManagedBrowserAction.OpenTransferDirectoryRoot, null, null, state, selectionOnlyPath: false,
            cancellationToken);

    internal Task<ManagedTransferDirectoryMutationExecutionResult> OpenRootForSelectionAsync(
        WebAutomationRequestIdentity identity, BaiduCandidateSessionBinding candidateBinding,
        ManagedBrowserOpenTransferSourceRequest binding, ManagedTransferDirectoryEvidence evidence,
        ManagedTransferDirectoryMutationState state, CancellationToken cancellationToken = default) =>
        OpenRootForSelectionAsync(identity, candidateBinding, binding, evidence, state, runtime: null,
            cancellationToken);

    internal Task<ManagedTransferDirectoryMutationExecutionResult> OpenRootForSelectionAsync(
        WebAutomationRequestIdentity identity, BaiduCandidateSessionBinding candidateBinding,
        ManagedBrowserOpenTransferSourceRequest binding, ManagedTransferDirectoryEvidence evidence,
        ManagedTransferDirectoryMutationState state, ManagedTransferDirectoryMutationRuntime? runtime,
        CancellationToken cancellationToken = default) =>
        ExecuteAndReadAsync(identity, candidateBinding, binding, evidence, runtime,
            ManagedBrowserAction.OpenTransferDirectoryRoot, null, null, state, selectionOnlyPath: true,
            cancellationToken);

    internal Task<ManagedTransferDirectoryMutationExecutionResult> EnterDirectoryAsync(
        WebAutomationRequestIdentity identity, BaiduCandidateSessionBinding candidateBinding,
        ManagedBrowserOpenTransferSourceRequest binding, ManagedTransferDirectoryEvidence evidence,
        string childExactId, ManagedTransferDirectoryMutationState state, CancellationToken cancellationToken = default) =>
        EnterDirectoryAsync(identity, candidateBinding, binding, evidence, childExactId, state, runtime: null, cancellationToken);

    internal Task<ManagedTransferDirectoryMutationExecutionResult> EnterDirectoryAsync(
        WebAutomationRequestIdentity identity, BaiduCandidateSessionBinding candidateBinding,
        ManagedBrowserOpenTransferSourceRequest binding, ManagedTransferDirectoryEvidence evidence, string childExactId,
        ManagedTransferDirectoryMutationState state, ManagedTransferDirectoryMutationRuntime? runtime,
        CancellationToken cancellationToken = default) =>
        ExecuteAndReadAsync(identity, candidateBinding, binding, evidence, runtime,
            ManagedBrowserAction.EnterTransferDirectory, childExactId, null, state, selectionOnlyPath: false,
            cancellationToken);

    internal Task<ManagedTransferDirectoryMutationExecutionResult> EnterDirectoryForSelectionAsync(
        WebAutomationRequestIdentity identity, BaiduCandidateSessionBinding candidateBinding,
        ManagedBrowserOpenTransferSourceRequest binding, ManagedTransferDirectoryEvidence evidence,
        string childExactId, ManagedTransferDirectoryMutationState state,
        CancellationToken cancellationToken = default) =>
        EnterDirectoryForSelectionAsync(identity, candidateBinding, binding, evidence, childExactId, state,
            runtime: null, cancellationToken);

    internal Task<ManagedTransferDirectoryMutationExecutionResult> EnterDirectoryForSelectionAsync(
        WebAutomationRequestIdentity identity, BaiduCandidateSessionBinding candidateBinding,
        ManagedBrowserOpenTransferSourceRequest binding, ManagedTransferDirectoryEvidence evidence,
        string childExactId, ManagedTransferDirectoryMutationState state,
        ManagedTransferDirectoryMutationRuntime? runtime,
        CancellationToken cancellationToken = default) =>
        ExecuteAndReadAsync(identity, candidateBinding, binding, evidence, runtime,
            ManagedBrowserAction.EnterTransferDirectory, childExactId, null, state, selectionOnlyPath: true,
            cancellationToken);

    internal Task<ManagedTransferDirectoryMutationExecutionResult> BeginCreateAsync(
        WebAutomationRequestIdentity identity, BaiduCandidateSessionBinding candidateBinding,
        ManagedBrowserOpenTransferSourceRequest binding, ManagedTransferDirectoryEvidence evidence, string segment,
        ManagedTransferDirectoryMutationState state, CancellationToken cancellationToken = default) =>
        BeginCreateAsync(identity, candidateBinding, binding, evidence, segment, state, runtime: null, cancellationToken);

    internal Task<ManagedTransferDirectoryMutationExecutionResult> BeginCreateAsync(
        WebAutomationRequestIdentity identity, BaiduCandidateSessionBinding candidateBinding,
        ManagedBrowserOpenTransferSourceRequest binding, ManagedTransferDirectoryEvidence evidence, string segment,
        ManagedTransferDirectoryMutationState state, ManagedTransferDirectoryMutationRuntime? runtime,
        CancellationToken cancellationToken = default) =>
        ExecuteSingleMutationAsync(identity, candidateBinding, binding, evidence, runtime,
            ManagedBrowserAction.BeginCreateTransferDirectory, null, segment, state, cancellationToken);

    internal Task<ManagedTransferDirectoryMutationExecutionResult> ConfirmCreateAsync(
        WebAutomationRequestIdentity identity, BaiduCandidateSessionBinding candidateBinding,
        ManagedBrowserOpenTransferSourceRequest binding, ManagedTransferDirectoryEvidence evidence, string segment,
        ManagedTransferDirectoryMutationState state, CancellationToken cancellationToken = default) =>
        ConfirmCreateCoreAsync(identity, candidateBinding, binding, evidence, segment, state, runtime: null, cancellationToken);

    internal Task<ManagedTransferDirectoryMutationExecutionResult> ConfirmCreateAsync(
        WebAutomationRequestIdentity identity, BaiduCandidateSessionBinding candidateBinding,
        ManagedBrowserOpenTransferSourceRequest binding, ManagedTransferDirectoryEvidence evidence, string segment,
        ManagedTransferDirectoryMutationState state, ManagedTransferDirectoryMutationRuntime? runtime,
        CancellationToken cancellationToken = default) =>
        ConfirmCreateCoreAsync(identity, candidateBinding, binding, evidence, segment, state, runtime, cancellationToken);

    internal Task<ManagedTransferDirectoryMutationExecutionResult> CancelDestinationPickerAsync(
        WebAutomationRequestIdentity identity, BaiduCandidateSessionBinding candidateBinding,
        ManagedBrowserOpenTransferSourceRequest binding, ManagedTransferDirectoryEvidence evidence,
        ManagedBrowserSourceSnapshot originalSource, ManagedTransferDirectoryMutationState state,
        CancellationToken cancellationToken = default) =>
        CancelDestinationPickerCoreAsync(identity, candidateBinding, binding, evidence, originalSource,
            state, runtime: null, cancellationToken);

    internal Task<ManagedTransferDirectoryMutationExecutionResult> CancelDestinationPickerAsync(
        WebAutomationRequestIdentity identity, BaiduCandidateSessionBinding candidateBinding,
        ManagedBrowserOpenTransferSourceRequest binding, ManagedTransferDirectoryEvidence evidence,
        ManagedBrowserSourceSnapshot originalSource, ManagedTransferDirectoryMutationState state,
        ManagedTransferDirectoryMutationRuntime? runtime, CancellationToken cancellationToken = default) =>
        CancelDestinationPickerCoreAsync(identity, candidateBinding, binding, evidence, originalSource,
            state, runtime, cancellationToken);

    private async Task<ManagedTransferDirectoryMutationExecutionResult> ExecuteSingleMutationAsync(
        WebAutomationRequestIdentity identity, BaiduCandidateSessionBinding candidateBinding,
        ManagedBrowserOpenTransferSourceRequest binding, ManagedTransferDirectoryEvidence evidence,
        ManagedTransferDirectoryMutationRuntime? runtime, ManagedBrowserAction action, string? childExactId,
        string? segment, ManagedTransferDirectoryMutationState state, CancellationToken cancellationToken)
    {
        if (!state.PickerOpen || state.CreateUiOpen ||
            !TryValidateInputs(identity, candidateBinding, binding, evidence, action, childExactId, segment))
            return Denied(ManagedTransferDirectoryMutationOutcome.PageNotValidated, state);
        var settlement = new SettlementTracker();
        using var deadline = new ManagedTransferOperationDeadline(
            binding.TimeoutMilliseconds, cancellationToken, timeProvider);
        var result = await ExecuteMutationAsync(identity, candidateBinding, binding, evidence, runtime, action,
            childExactId, segment, state, deadline, settlement, selectionOnlyPath: false).ConfigureAwait(false);
        return Complete(result, settlement);
    }

    private async Task<ManagedTransferDirectoryMutationExecutionResult> ExecuteAndReadAsync(
        WebAutomationRequestIdentity identity, BaiduCandidateSessionBinding candidateBinding,
        ManagedBrowserOpenTransferSourceRequest binding, ManagedTransferDirectoryEvidence evidence,
        ManagedTransferDirectoryMutationRuntime? runtime, ManagedBrowserAction action, string? childExactId,
        string? segment, ManagedTransferDirectoryMutationState state, bool selectionOnlyPath,
        CancellationToken cancellationToken)
    {
        if (!state.PickerOpen || state.CreateUiOpen ||
            !TryValidateInputs(identity, candidateBinding, binding, evidence, action, childExactId, segment,
                selectionOnlyPath))
            return Denied(ManagedTransferDirectoryMutationOutcome.PageNotValidated, state);
        var settlement = new SettlementTracker();
        using var deadline = new ManagedTransferOperationDeadline(
            binding.TimeoutMilliseconds, cancellationToken, timeProvider);
        var mutation = await ExecuteMutationAsync(identity, candidateBinding, binding, evidence, runtime, action,
            childExactId, segment, state, deadline, settlement, selectionOnlyPath).ConfigureAwait(false);
        if (mutation.Outcome is not (ManagedTransferDirectoryMutationOutcome.RootOpened or
            ManagedTransferDirectoryMutationOutcome.DirectoryEntered)) return Complete(mutation, settlement);

        var expectedParent = action == ManagedBrowserAction.OpenTransferDirectoryRoot
            ? evidence.RootExactId : childExactId!;
        var expectedPath = action == ManagedBrowserAction.OpenTransferDirectoryRoot
            ? "/" : ChildPath(evidence.Snapshot, childExactId!);
        var expectedAncestors = action == ManagedBrowserAction.OpenTransferDirectoryRoot
            ? new[] { evidence.RootExactId }
            : evidence.RootInclusiveAncestorExactIds.Append(childExactId!).ToArray();
        try
        {
            var refreshed = await ReadFreshAsync(identity, binding, expectedParent, expectedPath,
                expectedAncestors, deadline, settlement).ConfigureAwait(false);
            if (refreshed is null || !await ProveAsync(runtime, binding, identity, deadline, settlement).ConfigureAwait(false))
                return Complete(Unknown(mutation.State), settlement);
            deadline.RequireBudget();
            return Complete(new(mutation.Outcome, mutation.State, refreshed), settlement);
        }
        catch (Exception)
        {
            return Complete(Unknown(mutation.State), settlement);
        }
    }

    private async Task<ManagedTransferDirectoryMutationExecutionResult> ConfirmCreateCoreAsync(
        WebAutomationRequestIdentity identity, BaiduCandidateSessionBinding candidateBinding,
        ManagedBrowserOpenTransferSourceRequest binding, ManagedTransferDirectoryEvidence evidence, string segment,
        ManagedTransferDirectoryMutationState state, ManagedTransferDirectoryMutationRuntime? runtime,
        CancellationToken cancellationToken)
    {
        if (!state.PickerOpen || !state.CreateUiOpen ||
            !TryValidateInputs(identity, candidateBinding, binding, evidence,
                ManagedBrowserAction.ConfirmCreateTransferDirectory, null, segment))
            return Denied(ManagedTransferDirectoryMutationOutcome.PageNotValidated, state);
        var settlement = new SettlementTracker();
        using var deadline = new ManagedTransferOperationDeadline(
            binding.TimeoutMilliseconds, cancellationToken, timeProvider);
        var confirmed = await ExecuteMutationAsync(identity, candidateBinding, binding, evidence, runtime,
            ManagedBrowserAction.ConfirmCreateTransferDirectory, null, segment, state, deadline, settlement).ConfigureAwait(false);
        if (confirmed.Outcome != ManagedTransferDirectoryMutationOutcome.DirectoryCreated)
            return Complete(confirmed.Outcome is ManagedTransferDirectoryMutationOutcome.PageNotValidated or
                ManagedTransferDirectoryMutationOutcome.Cancelled or ManagedTransferDirectoryMutationOutcome.TimedOut
                ? RequireQuarantine(state) : confirmed, settlement);
        try
        {
            var refreshed = await ReadFreshAsync(identity, binding, evidence.Snapshot.ParentExactId,
                evidence.Snapshot.CanonicalPath, evidence.RootInclusiveAncestorExactIds, deadline, settlement).ConfigureAwait(false);
            if (refreshed is null || !await ProveAsync(runtime, binding, identity, deadline, settlement).ConfigureAwait(false))
                return Complete(Unknown(confirmed.State), settlement);
            deadline.RequireBudget();
            return Complete(new(ManagedTransferDirectoryMutationOutcome.DirectoryCreated, confirmed.State, refreshed), settlement);
        }
        catch (Exception)
        {
            return Complete(Unknown(confirmed.State), settlement);
        }
    }

    private async Task<ManagedTransferDirectoryMutationExecutionResult> CancelDestinationPickerCoreAsync(
        WebAutomationRequestIdentity identity, BaiduCandidateSessionBinding candidateBinding,
        ManagedBrowserOpenTransferSourceRequest binding, ManagedTransferDirectoryEvidence evidence,
        ManagedBrowserSourceSnapshot originalSource, ManagedTransferDirectoryMutationState state,
        ManagedTransferDirectoryMutationRuntime? runtime, CancellationToken cancellationToken)
    {
        if (!state.PickerOpen || originalSource is null ||
            !TryValidateInputs(identity, candidateBinding, binding, evidence,
                ManagedBrowserAction.CancelTransferPicker, null, null))
            return Denied(ManagedTransferDirectoryMutationOutcome.PageNotValidated, state);
        var settlement = new SettlementTracker();
        using var deadline = new ManagedTransferOperationDeadline(
            binding.TimeoutMilliseconds, cancellationToken, timeProvider);
        var cancelled = await ExecuteMutationAsync(identity, candidateBinding, binding, evidence, runtime,
            ManagedBrowserAction.CancelTransferPicker, null, null, state, deadline, settlement).ConfigureAwait(false);
        if (cancelled.Outcome != ManagedTransferDirectoryMutationOutcome.PickerClosed)
            return Complete(RequireQuarantine(state), settlement);
        try
        {
            var payload = new ManagedBrowserObserveTransferPickerClosedRequest(
                deadline.RemainingBinding(binding), originalSource);
            var request = NewRequest(identity, ManagedBrowserAction.ObserveTransferPickerClosed, payload);
            var response = await AwaitObservedAsync(send(request, deadline.Token), deadline.Token, settlement).ConfigureAwait(false);
            deadline.RequireBudget();
            ManagedBrowserProtocol.ValidateResponseFor(request, response);
            if (response.Result is not ManagedBrowserTransferPickerClosedResult
                { Kind: ManagedBrowserTransferPickerClosedResultKind.PickerClosed } ||
                !await ProveAsync(runtime, binding, identity, deadline, settlement).ConfigureAwait(false))
                return Complete(RequireQuarantine(state), settlement);
            deadline.RequireBudget();
            return Complete(new(ManagedTransferDirectoryMutationOutcome.PickerClosed,
                new(false, false, state.PossibleRemoteMutation)), settlement);
        }
        catch (Exception)
        {
            return Complete(RequireQuarantine(state), settlement);
        }
    }

    private async Task<ManagedTransferDirectoryMutationExecutionResult> ExecuteMutationAsync(
        WebAutomationRequestIdentity identity, BaiduCandidateSessionBinding candidateBinding,
        ManagedBrowserOpenTransferSourceRequest binding, ManagedTransferDirectoryEvidence evidence,
        ManagedTransferDirectoryMutationRuntime? runtime, ManagedBrowserAction action, string? childExactId,
        string? segment, ManagedTransferDirectoryMutationState state, ManagedTransferOperationDeadline deadline,
        SettlementTracker settlement, bool selectionOnlyPath = false)
    {
        IBaiduCandidateMutationPermit? permit = null;
        Task<ManagedBrowserResponse>? pendingSend = null;
        var dispatched = false;
        ManagedTransferDirectoryMutationExecutionResult execution;
        try
        {
            deadline.RequireBudget();
            if (!await ProveAsync(runtime, binding, identity, deadline, settlement).ConfigureAwait(false))
                return Denied(ManagedTransferDirectoryMutationOutcome.PageNotValidated, state);
            deadline.RequireBudget();
            var permitRequest = BuildPermitRequest(candidateBinding, binding, evidence, action, childExactId, segment);
            permit = await AwaitPermitAsync(
                authorization.TryAcquireTransferMutationPermitAsync(permitRequest, deadline.Token),
                deadline.Token, settlement).ConfigureAwait(false);
            deadline.RequireBudget();
            if (permit is null)
                execution = Denied(ManagedTransferDirectoryMutationOutcome.PageNotValidated, state);
            else if (!TryValidateInputs(identity, candidateBinding, binding, evidence,
                         action, childExactId, segment, selectionOnlyPath))
                execution = Denied(ManagedTransferDirectoryMutationOutcome.PageNotValidated, state);
            else if (!await ProveAsync(runtime, binding, identity, deadline, settlement).ConfigureAwait(false))
                execution = Denied(ManagedTransferDirectoryMutationOutcome.PageNotValidated, state);
            else
            {
                deadline.RequireBudget();
                var payload = BuildPayload(deadline.RemainingBinding(binding), evidence.Snapshot,
                    action, childExactId, segment);
                var request = NewRequest(identity, action, payload);
                dispatched = true;
                pendingSend = send(request, deadline.Token);
                var response = await AwaitObservedAsync(pendingSend, deadline.Token, settlement).ConfigureAwait(false);
                deadline.RequireBudget();
                ManagedBrowserProtocol.ValidateResponseFor(request, response);
                execution = response.Result is ManagedBrowserDirectoryMutationResult result
                    ? Map(result.Kind, action, state)
                    : Unknown(StateAfterPossibleDispatch(action, state));
            }
        }
        catch (OperationCanceledException)
        {
            execution = dispatched ? Unknown(StateAfterPossibleDispatch(action, state)) :
                Denied(deadline.CallerCancelled ? ManagedTransferDirectoryMutationOutcome.Cancelled :
                    ManagedTransferDirectoryMutationOutcome.TimedOut, state);
        }
        catch (TimeoutException)
        {
            execution = dispatched ? Unknown(StateAfterPossibleDispatch(action, state)) :
                Denied(ManagedTransferDirectoryMutationOutcome.TimedOut, state);
        }
        catch (Exception error) when (error is IOException or ManagedBrowserProtocolException or
            ManagedBrowserHelperClientException or ArgumentException or InvalidOperationException)
        {
            execution = dispatched ? Unknown(StateAfterPossibleDispatch(action, state)) :
                Denied(ManagedTransferDirectoryMutationOutcome.PageNotValidated, state);
        }
        catch (Exception)
        {
            execution = dispatched ? Unknown(StateAfterPossibleDispatch(action, state)) :
                Denied(ManagedTransferDirectoryMutationOutcome.PageNotValidated, state);
        }

        if (permit is not null)
        {
            if (dispatched && pendingSend is { IsCompleted: false })
            {
                settlement.Track(DisposePermitAfterSendSettlementAsync(pendingSend, permit));
                return execution;
            }
            try
            {
                await AwaitObservedAsync(permit.DisposeAsync().AsTask(), deadline.Token, settlement).ConfigureAwait(false);
                deadline.RequireBudget();
            }
            catch (Exception)
            {
                if (dispatched) return Unknown(StateAfterPossibleDispatch(action, state));
            }
        }
        return execution;
    }

    private async Task<ManagedTransferDirectoryEvidence?> ReadFreshAsync(
        WebAutomationRequestIdentity identity, ManagedBrowserOpenTransferSourceRequest binding,
        string expectedParentExactId, string expectedCanonicalPath, IEnumerable<string> expectedAncestors,
        ManagedTransferOperationDeadline deadline, SettlementTracker settlement)
    {
        var payload = new ManagedBrowserReadTransferDirectoryRequest(deadline.RemainingBinding(binding),
            expectedParentExactId, expectedCanonicalPath);
        var request = NewRequest(identity, ManagedBrowserAction.ReadTransferDirectory, payload);
        var response = await AwaitObservedAsync(send(request, deadline.Token), deadline.Token, settlement).ConfigureAwait(false);
        deadline.RequireBudget();
        ManagedBrowserProtocol.ValidateResponseFor(request, response);
        if (response.Result is not ManagedBrowserDirectoryListingResult
            { Kind: ManagedBrowserDirectoryListingResultKind.Observed, Snapshot: { } snapshot } ||
            !string.Equals(snapshot.BaiduUserId, binding.ExpectedBaiduUserId, StringComparison.Ordinal) ||
            !string.Equals(snapshot.ParentExactId, expectedParentExactId, StringComparison.Ordinal) ||
            !string.Equals(snapshot.CanonicalPath, expectedCanonicalPath, StringComparison.Ordinal) ||
            !snapshot.AncestorExactIds.SequenceEqual(expectedAncestors, StringComparer.Ordinal) ||
            snapshot.ComputeFingerprint(binding.SourceUri) != snapshot.ObservationFingerprint)
            return null;
        return ManagedTransferDirectoryEvidence.CreateFromReaderValidated(snapshot);
    }

    private async Task<bool> ProveAsync(ManagedTransferDirectoryMutationRuntime? runtime,
        ManagedBrowserOpenTransferSourceRequest binding, WebAutomationRequestIdentity identity,
        ManagedTransferOperationDeadline deadline, SettlementTracker settlement)
    {
        var proof = new ManagedTransferDirectoryMutationProof(runtime, deadline.RemainingBinding(binding), identity);
        var result = await AwaitObservedAsync(prove(proof, deadline.Token), deadline.Token, settlement).ConfigureAwait(false);
        deadline.RequireBudget();
        return result;
    }

    private static async Task<T> AwaitObservedAsync<T>(Task<T> pending, CancellationToken token,
        SettlementTracker settlement)
    {
        try
        {
            return await pending.WaitAsync(token).ConfigureAwait(false);
        }
        catch
        {
            settlement.Track(pending);
            throw;
        }
    }

    private static async Task AwaitObservedAsync(Task pending, CancellationToken token,
        SettlementTracker settlement)
    {
        try
        {
            await pending.WaitAsync(token).ConfigureAwait(false);
        }
        catch
        {
            settlement.Track(pending);
            throw;
        }
    }

    private static async Task<IBaiduCandidateMutationPermit?> AwaitPermitAsync(
        ValueTask<IBaiduCandidateMutationPermit?> pendingValue, CancellationToken token,
        SettlementTracker settlement)
    {
        var pending = pendingValue.AsTask();
        try
        {
            return await pending.WaitAsync(token).ConfigureAwait(false);
        }
        catch
        {
            settlement.Track(DisposeLatePermitAsync(pending));
            throw;
        }
    }

    private static async Task DisposeLatePermitAsync(Task<IBaiduCandidateMutationPermit?> pending)
    {
        try
        {
            var permit = await pending.ConfigureAwait(false);
            if (permit is not null) await permit.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception) { /* A late authorization or disposal fault is contained and redacted. */ }
    }

    private static async Task DisposePermitAfterSendSettlementAsync(Task sendTask, IBaiduCandidateMutationPermit permit)
    {
        try { await sendTask.ConfigureAwait(false); }
        catch (Exception) { /* The mutation outcome is already unknown; consume the late helper fault. */ }
        try { await permit.DisposeAsync().ConfigureAwait(false); }
        catch (Exception) { /* Private late permit failures remain redacted. */ }
    }

    private bool TryValidateInputs(WebAutomationRequestIdentity identity,
        BaiduCandidateSessionBinding candidateBinding, ManagedBrowserOpenTransferSourceRequest binding,
        ManagedTransferDirectoryEvidence evidence, ManagedBrowserAction action, string? childExactId, string? segment,
        bool selectionOnlyPath = false)
    {
        try
        {
            identity.Validate();
            var selectionOnly = candidateBinding.Authorization.Action == AdapterWriteAction.SubmitTransfer;
            if (selectionOnly != selectionOnlyPath)
                return false;
            if (selectionOnly)
                ValidateSelectionIdentity(candidateBinding, identity);
            else
                candidateBinding.ValidateIdentity(identity);
            var now = timeProvider.GetUtcNow();
            if (candidateBinding.Binding.IssuedAtUtc > now ||
                candidateBinding.Binding.ExpiresAtUtc <= now ||
                candidateBinding.Binding.ExpiresAtUtc <= candidateBinding.Binding.IssuedAtUtc ||
                candidateBinding.Authorization.Capability.AdapterKind != "BaiduTransfer" ||
                candidateBinding.Authorization.IsRecovery || candidateBinding.Binding.IsRecovery ||
                !string.Equals(candidateBinding.ExpectedBaiduUserId, binding.ExpectedBaiduUserId, StringComparison.Ordinal) ||
                evidence is null ||
                !string.Equals(evidence.Snapshot.BaiduUserId, binding.ExpectedBaiduUserId, StringComparison.Ordinal) ||
                evidence.Snapshot.ComputeFingerprint(binding.SourceUri) != evidence.Snapshot.ObservationFingerprint)
                return false;
            if (selectionOnly && action is not (ManagedBrowserAction.OpenTransferDirectoryRoot or
                    ManagedBrowserAction.EnterTransferDirectory) ||
                !selectionOnly && candidateBinding.Authorization.Action != AdapterWriteAction.EnsureDirectory)
                return false;
            ValidateActionArguments(evidence, action, childExactId, segment);
            _ = BuildPermitRequest(candidateBinding, binding, evidence, action, childExactId, segment);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void ValidateSelectionIdentity(
        BaiduCandidateSessionBinding candidateBinding,
        WebAutomationRequestIdentity identity)
    {
        var authorization = candidateBinding.Authorization;
        var binding = candidateBinding.Binding;
        if (identity.OwnerKind != WebAutomationOwnerKind.Transfer ||
            string.IsNullOrWhiteSpace(candidateBinding.ExpectedBaiduUserId) ||
            candidateBinding.ExpectedBaiduUserId.Length > 512 ||
            candidateBinding.ExpectedBaiduUserId.Any(char.IsControl) ||
            authorization.Capability.AdapterKind != "BaiduTransfer" ||
            authorization.Action != AdapterWriteAction.SubmitTransfer ||
            identity.AccountId != authorization.AccountId ||
            identity.OperationId != authorization.OperationId ||
            identity.Generation != authorization.Generation ||
            identity.BatchId != authorization.BatchId ||
            authorization.Capability != binding.Capability ||
            authorization.AccountId != binding.AccountId ||
            authorization.OperationId != binding.OperationId ||
            authorization.Generation != binding.Generation ||
            authorization.BatchId != binding.BatchId ||
            authorization.IsRecovery != binding.IsRecovery)
            throw new InvalidOperationException("Selection binding does not match the exact transfer operation.");
    }

    private static void ValidateActionArguments(ManagedTransferDirectoryEvidence evidence,
        ManagedBrowserAction action, string? childExactId, string? segment)
    {
        switch (action)
        {
            case ManagedBrowserAction.OpenTransferDirectoryRoot:
            case ManagedBrowserAction.CancelTransferPicker:
                if (childExactId is not null || segment is not null) throw new ArgumentException("Unexpected action field.");
                break;
            case ManagedBrowserAction.EnterTransferDirectory:
                if (segment is not null || childExactId is null) throw new ArgumentException("An exact child is required.");
                _ = ChildPath(evidence.Snapshot, childExactId);
                break;
            case ManagedBrowserAction.BeginCreateTransferDirectory:
            case ManagedBrowserAction.ConfirmCreateTransferDirectory:
                if (childExactId is not null || segment is null) throw new ArgumentException("An exact segment is required.");
                ManagedBrowserProtocol.ValidateBoundedText(segment, nameof(segment), 256);
                if (segment is "." or ".." || segment.Contains('/') || segment.Contains('\\'))
                    throw new ArgumentException("A single safe directory segment is required.");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action));
        }
    }

    private static BaiduCandidateDomMutationRequest BuildPermitRequest(BaiduCandidateSessionBinding candidateBinding,
        ManagedBrowserOpenTransferSourceRequest binding, ManagedTransferDirectoryEvidence evidence,
        ManagedBrowserAction action, string? childExactId, string? segment)
    {
        var snapshot = evidence.Snapshot;
        var read = new BaiduCandidateDomReadRequest(new Uri(binding.SourceUri), BaiduCandidatePageState.DestinationPicker,
            snapshot.BaiduUserId, snapshot.Items.Select(item => item.ExactId), snapshot.ParentExactId,
            new RemotePath(snapshot.CanonicalPath));
        return action switch
        {
            ManagedBrowserAction.OpenTransferDirectoryRoot => new(candidateBinding.Authorization, candidateBinding.Binding,
                BaiduAutomationAction.EnterDirectory, read, null, evidence.RootExactId),
            ManagedBrowserAction.EnterTransferDirectory => new(candidateBinding.Authorization, candidateBinding.Binding,
                BaiduAutomationAction.EnterDirectory, read, null, childExactId),
            ManagedBrowserAction.BeginCreateTransferDirectory => new(candidateBinding.Authorization, candidateBinding.Binding,
                BaiduAutomationAction.CreateDirectory, read, segment),
            ManagedBrowserAction.ConfirmCreateTransferDirectory => new(candidateBinding.Authorization, candidateBinding.Binding,
                BaiduAutomationAction.ConfirmDirectoryCreation, read, null),
            ManagedBrowserAction.CancelTransferPicker => new(candidateBinding.Authorization, candidateBinding.Binding,
                BaiduAutomationAction.CancelDestinationPicker, read, null),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }

    private static ManagedBrowserDirectoryMutationRequest BuildPayload(ManagedBrowserOpenTransferSourceRequest binding,
        ManagedBrowserDirectorySnapshot snapshot, ManagedBrowserAction action, string? childExactId, string? segment) =>
        action switch
        {
            ManagedBrowserAction.OpenTransferDirectoryRoot => ManagedBrowserDirectoryMutationRequest.ForOpenRoot(
                binding, snapshot, snapshot.AncestorExactIds[0]),
            ManagedBrowserAction.EnterTransferDirectory => ManagedBrowserDirectoryMutationRequest.ForEnterDirectory(
                binding, snapshot, childExactId!, ChildPath(snapshot, childExactId!)),
            ManagedBrowserAction.BeginCreateTransferDirectory => ManagedBrowserDirectoryMutationRequest.ForBeginCreate(
                binding, snapshot, segment!),
            ManagedBrowserAction.ConfirmCreateTransferDirectory => ManagedBrowserDirectoryMutationRequest.ForConfirmCreate(
                binding, snapshot, segment!),
            ManagedBrowserAction.CancelTransferPicker => ManagedBrowserDirectoryMutationRequest.ForCancelPicker(binding, snapshot),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };

    private static string ChildPath(ManagedBrowserDirectorySnapshot snapshot, string childExactId)
    {
        var child = snapshot.Items.Single(item => item.ExactId == childExactId &&
            item.Kind == ManagedBrowserSourceItemKind.Folder);
        return snapshot.CanonicalPath == "/" ? "/" + child.ServerName : snapshot.CanonicalPath + "/" + child.ServerName;
    }

    private static ManagedBrowserRequest NewRequest(WebAutomationRequestIdentity identity, ManagedBrowserAction action,
        ManagedBrowserRequestPayload payload) => new(Guid.NewGuid(), identity.OperationId, identity.AccountId,
            identity.Generation, action, payload);

    private static ManagedTransferDirectoryMutationExecutionResult Map(
        ManagedBrowserDirectoryMutationResultKind kind, ManagedBrowserAction action,
        ManagedTransferDirectoryMutationState state)
    {
        var expected = action switch
        {
            ManagedBrowserAction.OpenTransferDirectoryRoot => ManagedBrowserDirectoryMutationResultKind.RootOpened,
            ManagedBrowserAction.EnterTransferDirectory => ManagedBrowserDirectoryMutationResultKind.DirectoryEntered,
            ManagedBrowserAction.BeginCreateTransferDirectory => ManagedBrowserDirectoryMutationResultKind.CreateDialogOpened,
            ManagedBrowserAction.ConfirmCreateTransferDirectory => ManagedBrowserDirectoryMutationResultKind.DirectoryCreated,
            ManagedBrowserAction.CancelTransferPicker => ManagedBrowserDirectoryMutationResultKind.PickerCancelled,
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
        if (kind == expected)
        {
            var outcome = action switch
            {
                ManagedBrowserAction.OpenTransferDirectoryRoot => ManagedTransferDirectoryMutationOutcome.RootOpened,
                ManagedBrowserAction.EnterTransferDirectory => ManagedTransferDirectoryMutationOutcome.DirectoryEntered,
                ManagedBrowserAction.BeginCreateTransferDirectory => ManagedTransferDirectoryMutationOutcome.CreateDialogOpened,
                ManagedBrowserAction.ConfirmCreateTransferDirectory => ManagedTransferDirectoryMutationOutcome.DirectoryCreated,
                _ => ManagedTransferDirectoryMutationOutcome.PickerClosed,
            };
            return new(outcome, StateAfterConfirmedAction(action, state));
        }
        return kind switch
        {
            ManagedBrowserDirectoryMutationResultKind.OutcomeUnknown => Unknown(StateAfterPossibleDispatch(action, state)),
            ManagedBrowserDirectoryMutationResultKind.Cancelled => Denied(ManagedTransferDirectoryMutationOutcome.Cancelled, state),
            ManagedBrowserDirectoryMutationResultKind.TimedOut => Denied(ManagedTransferDirectoryMutationOutcome.TimedOut, state),
            ManagedBrowserDirectoryMutationResultKind.PageNotValidated or
            ManagedBrowserDirectoryMutationResultKind.AccountIdentityMismatch or
            ManagedBrowserDirectoryMutationResultKind.LoginRequired or
            ManagedBrowserDirectoryMutationResultKind.VerificationRequired or
            ManagedBrowserDirectoryMutationResultKind.BrowserUnavailable =>
                Denied(ManagedTransferDirectoryMutationOutcome.PageNotValidated, state),
            _ => Unknown(StateAfterPossibleDispatch(action, state)),
        };
    }

    private static ManagedTransferDirectoryMutationState StateAfterConfirmedAction(ManagedBrowserAction action,
        ManagedTransferDirectoryMutationState state) => action switch
    {
        ManagedBrowserAction.BeginCreateTransferDirectory => new(true, true, state.PossibleRemoteMutation),
        ManagedBrowserAction.ConfirmCreateTransferDirectory => new(true, false, true),
        ManagedBrowserAction.CancelTransferPicker => state,
        _ => new(true, false, state.PossibleRemoteMutation),
    };

    private static ManagedTransferDirectoryMutationState StateAfterPossibleDispatch(ManagedBrowserAction action,
        ManagedTransferDirectoryMutationState state) => action switch
    {
        ManagedBrowserAction.BeginCreateTransferDirectory => new(true, true, state.PossibleRemoteMutation),
        ManagedBrowserAction.ConfirmCreateTransferDirectory => new(true, state.CreateUiOpen, true),
        ManagedBrowserAction.CancelTransferPicker => state,
        _ => new(true, state.CreateUiOpen, state.PossibleRemoteMutation),
    };

    private static ManagedTransferDirectoryMutationExecutionResult Denied(
        ManagedTransferDirectoryMutationOutcome outcome, ManagedTransferDirectoryMutationState state) => new(outcome, state);
    private static ManagedTransferDirectoryMutationExecutionResult Unknown(ManagedTransferDirectoryMutationState state) =>
        new(ManagedTransferDirectoryMutationOutcome.OutcomeUnknown, state, currentBoundaryUncertain: true);
    private static ManagedTransferDirectoryMutationExecutionResult RequireQuarantine(
        ManagedTransferDirectoryMutationState state) => new(
            ManagedTransferDirectoryMutationOutcome.QuarantineRequired, state, currentBoundaryUncertain: true);

    private static ManagedTransferDirectoryMutationExecutionResult Complete(
        ManagedTransferDirectoryMutationExecutionResult result, SettlementTracker settlement) =>
        result.WithSettlement(settlement.Completion);

    private static async Task<bool> VerifyExactRuntimeAsync(ManagedBrowserLifecycleCoordinator lifecycle,
        ManagedChromiumProcessManager manager, ManagedTransferDirectoryMutationProof proof, CancellationToken token)
    {
        if (lifecycle is null || manager is null || proof.Runtime is not { } runtime) return false;
        var captured = runtime.CapturedSession;
        var exact = runtime.ExactHandle;
        var binding = proof.Binding;
        var identity = proof.Identity;
        if (!SameIdentity(captured.Session, identity) || !MatchesHandle(captured, exact) ||
            exact.BrowserId != binding.BrowserId ||
            !string.Equals(exact.TargetId, binding.TargetId, StringComparison.Ordinal) ||
            exact.Endpoint != binding.DevToolsEndpoint ||
            !string.Equals(captured.Session.StableBaiduUserId, binding.ExpectedBaiduUserId, StringComparison.Ordinal))
            return false;
        ManagedBrowserLifecycleSession? current;
        try { current = await lifecycle.LoadPersistedAsync(identity.AccountId, token).ConfigureAwait(false); }
        catch (InvalidOperationException) { return false; }
        return current is not null && SameSnapshot(current, captured) && MatchesHandle(current, exact) &&
            await manager.VerifyTransferSourceAsync(exact, binding, token).ConfigureAwait(false);
    }

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

    private static bool SameIdentity(ManagedBrowserSessionReference left, WebAutomationRequestIdentity right) =>
        left.AccountId == right.AccountId && right.OwnerKind == WebAutomationOwnerKind.Transfer &&
        right.ExecutionProfile.Engine == BaiduExecutionEngine.ManagedChromiumV1 &&
        right.ExecutionProfile.InstallationId == left.InstallationId &&
        right.ExecutionProfile.SessionId == left.SessionId &&
        right.ExecutionProfile.BrowserFamily == left.BrowserFamily &&
        string.Equals(right.ExecutionProfile.ProfileId, left.ProfileId, StringComparison.Ordinal);

    private static bool MatchesHandle(ManagedBrowserLifecycleSession snapshot, ManagedChromiumSessionHandle handle) =>
        handle is not null && handle.Installation.InstallationId == snapshot.InstallationId &&
        handle.Installation.BrowserFamily == snapshot.BrowserFamily && handle.Profile.AccountId == snapshot.AccountId &&
        handle.Profile.Family == snapshot.BrowserFamily &&
        Guid.TryParse(snapshot.ProfileId, out var profileId) && handle.Profile.ProfileId == profileId &&
        Guid.TryParse(snapshot.ProfileOwnershipId, out var ownershipId) && handle.Profile.OwnershipId == ownershipId &&
        string.Equals(handle.Profile.CanonicalSourcePath, snapshot.CanonicalProfilePath, StringComparison.OrdinalIgnoreCase);

    private sealed class SettlementTracker
    {
        private readonly object gate = new();
        private readonly List<Task> pending = [];

        internal void Track(Task task)
        {
            ArgumentNullException.ThrowIfNull(task);
            var observed = ObserveAsync(task);
            lock (gate) pending.Add(observed);
        }

        internal Task Completion
        {
            get
            {
                lock (gate)
                    return pending.Count == 0 ? Task.CompletedTask : Task.WhenAll(pending.ToArray());
            }
        }

        private static async Task ObserveAsync(Task task)
        {
            try { await task.ConfigureAwait(false); }
            catch (Exception) { /* Ownership settlement never propagates private helper/authorization faults. */ }
        }
    }

}
