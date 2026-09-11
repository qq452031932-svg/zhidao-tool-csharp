using BaiduBatchTransfer.App.Features.BrowserAutomation;
using BaiduBatchTransfer.App.Features.ManagedBrowser;
using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.BaiduAdapter.WebAutomation;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Infrastructure.Database;
using BaiduBatchTransfer.Infrastructure.Security;
using static BaiduBatchTransfer.App.Tests.Features.ManagedChromiumProcessManagerTests;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class ManagedTransferDirectoryMutatorTests
{
    [Fact]
    public async Task SubmissionSelectionUsesOneFreshPermitPerExactPickerStepAndNeverOverlaps()
    {
        var f = new Fixture();
        f.SetAuthorizationStage(AdapterWriteAction.SubmitTransfer);

        var root = await f.SelectionOpenRoot();
        var entered = await f.SelectionEnter();

        Assert.Equal(ManagedTransferDirectoryMutationOutcome.RootOpened, root.Outcome);
        Assert.Equal(ManagedTransferDirectoryMutationOutcome.DirectoryEntered, entered.Outcome);
        Assert.Equal(2, f.Authorization.Requests.Count);
        Assert.Equal(2, f.Authorization.Disposed);
        Assert.Equal(1, f.Authorization.MaximumActive);
        Assert.All(f.Authorization.Requests,
            request => Assert.Equal(AdapterWriteAction.SubmitTransfer, request.Authorization.Action));
        Assert.All(f.Authorization.Requests,
            request => Assert.Equal(BaiduAutomationAction.EnterDirectory, request.Action));
    }

    [Fact]
    public async Task SubmissionSelectionCannotSubstitutePreparationOrDispatchCreateCancelActions()
    {
        var f = new Fixture();
        f.SetAuthorizationStage(AdapterWriteAction.SubmitTransfer);

        Assert.Equal(ManagedTransferDirectoryMutationOutcome.PageNotValidated, (await f.OpenRoot()).Outcome);
        Assert.Equal(ManagedTransferDirectoryMutationOutcome.PageNotValidated, (await f.Enter()).Outcome);
        Assert.Equal(ManagedTransferDirectoryMutationOutcome.PageNotValidated, (await f.Begin()).Outcome);
        Assert.Equal(ManagedTransferDirectoryMutationOutcome.PageNotValidated,
            (await f.Confirm(new(true, true, false))).Outcome);
        Assert.Equal(ManagedTransferDirectoryMutationOutcome.PageNotValidated,
            (await f.Cancel(new(true, false, false))).Outcome);
        Assert.Empty(f.Authorization.Requests);
        Assert.Empty(f.Requests);

        var preparation = new Fixture();
        Assert.Equal(ManagedTransferDirectoryMutationOutcome.PageNotValidated,
            (await preparation.SelectionOpenRoot()).Outcome);
        Assert.Equal(ManagedTransferDirectoryMutationOutcome.PageNotValidated,
            (await preparation.SelectionEnter()).Outcome);
        Assert.Empty(preparation.Authorization.Requests);
        Assert.Empty(preparation.Requests);
    }

    [Fact]
    public async Task SubmissionSelectionOutcomeUnknownSendsNoFreshReadOrLaterPickerCall()
    {
        var f = new Fixture { MutationKindOverride = ManagedBrowserDirectoryMutationResultKind.OutcomeUnknown };
        f.SetAuthorizationStage(AdapterWriteAction.SubmitTransfer);

        var result = await f.SelectionEnter();

        Assert.Equal(ManagedTransferDirectoryMutationOutcome.OutcomeUnknown, result.Outcome);
        Assert.Single(f.Requests);
        Assert.Equal(ManagedBrowserAction.EnterTransferDirectory, f.Requests[0].Action);
        Assert.Equal(1, f.Authorization.Disposed);
        Assert.Equal(1, f.Authorization.MaximumActive);
    }

    [Fact]
    public async Task OpenRootBindsFreshReadToExactRootAndValidatesRootInclusiveTransition()
    {
        var f = new Fixture();
        var result = await f.OpenRoot();

        Assert.Equal(ManagedTransferDirectoryMutationOutcome.RootOpened, result.Outcome);
        Assert.Equal(new(true, false, false), result.State);
        Assert.Equal(new[] { "root-id" }, result.Evidence!.RootInclusiveAncestorExactIds.ToArray());
        var read = Assert.IsType<ManagedBrowserReadTransferDirectoryRequest>(f.Requests[1].Payload);
        Assert.Equal("root-id", read.ExpectedParentExactId);
        Assert.Equal("/", read.ExpectedCanonicalPath);
        Assert.Same(f.Evidence.Snapshot,
            Assert.IsType<ManagedBrowserDirectoryMutationRequest>(f.Requests[0].Payload).ExpectedDirectory);
        Assert.Equal(3, f.Proofs.Count);
    }

    [Fact]
    public async Task ValidSameUidWrongRootRefreshIsOutcomeUnknownAndRetainsPicker()
    {
        var f = new Fixture { ReturnWrongFreshDirectory = true };
        var result = await f.OpenRoot();

        Assert.Equal(ManagedTransferDirectoryMutationOutcome.OutcomeUnknown, result.Outcome);
        Assert.True(result.State.PickerOpen);
        Assert.Null(result.Evidence);
    }

    [Fact]
    public async Task EnterBindsFreshReadToExactChildPathAndRootInclusiveTransition()
    {
        var f = new Fixture();
        var result = await f.Enter();

        Assert.Equal(ManagedTransferDirectoryMutationOutcome.DirectoryEntered, result.Outcome);
        Assert.Equal(new[] { "root-id", "child-parent", "child-id" },
            result.Evidence!.RootInclusiveAncestorExactIds.ToArray());
        var mutation = Assert.IsType<ManagedBrowserDirectoryMutationRequest>(f.Requests[0].Payload);
        Assert.Equal("child-id", mutation.ChildExactId);
        Assert.Equal("/one/child", mutation.ChildCanonicalPath);
        var read = Assert.IsType<ManagedBrowserReadTransferDirectoryRequest>(f.Requests[1].Payload);
        Assert.Equal("child-id", read.ExpectedParentExactId);
        Assert.Equal("/one/child", read.ExpectedCanonicalPath);
    }

    [Fact]
    public async Task ValidSameUidWrongChildRefreshIsOutcomeUnknown()
    {
        var f = new Fixture { ReturnWrongFreshDirectory = true };
        var result = await f.Enter();
        Assert.Equal(ManagedTransferDirectoryMutationOutcome.OutcomeUnknown, result.Outcome);
        Assert.True(result.State.PickerOpen);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("identity")]
    [InlineData("authorization-action")]
    [InlineData("snapshot-uid")]
    [InlineData("snapshot-fingerprint")]
    public async Task InvalidProvenanceCausesZeroProofPermitAndDispatch(string change)
    {
        var f = new Fixture();
        var binding = f.CandidateBinding;
        var identity = f.Identity;
        var evidence = f.Evidence;
        if (change == "expired") binding = binding with
        {
            Binding = binding.Binding with { ExpiresAtUtc = f.Clock.GetUtcNow().AddSeconds(-1) }
        };
        if (change == "identity") identity = identity with { AccountId = Guid.NewGuid() };
        if (change == "authorization-action") binding = binding with
        {
            Authorization = binding.Authorization with { Action = AdapterWriteAction.PrepareShare }
        };
        if (change == "snapshot-uid") evidence = f.EvidenceWith(uid: "other-user");
        if (change == "snapshot-fingerprint") evidence = f.EvidenceWith(fingerprint: new string('0', 64));

        var result = await f.Mutator.OpenRootAsync(identity, binding, f.Binding, evidence,
            new(true, false, false), default);

        Assert.Equal(ManagedTransferDirectoryMutationOutcome.PageNotValidated, result.Outcome);
        Assert.Equal(new(true, false, false), result.State);
        Assert.Empty(f.Proofs);
        Assert.Empty(f.Authorization.Requests);
        Assert.Empty(f.Requests);
    }

    [Fact]
    public async Task ProofReceivesExactImmutableIdentityBindingAndRuntimeSurface()
    {
        var f = new Fixture();
        await f.OpenRoot();

        Assert.All(f.Proofs, proof =>
        {
            Assert.Same(f.Identity, proof.Identity);
            Assert.Equal(f.Binding.BrowserId, proof.Binding.BrowserId);
            Assert.Equal(f.Binding.TargetId, proof.Binding.TargetId);
            Assert.Equal(f.Binding.DevToolsEndpoint, proof.Binding.DevToolsEndpoint);
            Assert.Equal(f.Binding.SourceUri, proof.Binding.SourceUri);
            Assert.Equal(f.Binding.ExpectedBaiduUserId, proof.Binding.ExpectedBaiduUserId);
            Assert.Null(proof.Runtime);
        });
    }

    [Fact]
    public async Task AuthorizationReceivesExactFiveActionMappingsAndRetainedReadBinding()
    {
        var f = new Fixture();
        _ = await f.OpenRoot();
        _ = await f.Enter();
        var create = await f.Begin();
        _ = await f.Confirm(create.State);
        _ = await f.Cancel(new(true, true, true));

        Assert.Equal([
            BaiduAutomationAction.EnterDirectory,
            BaiduAutomationAction.EnterDirectory,
            BaiduAutomationAction.CreateDirectory,
            BaiduAutomationAction.ConfirmDirectoryCreation,
            BaiduAutomationAction.CancelDestinationPicker],
            f.Authorization.Requests.Select(request => request.Action));
        Assert.Equal(["root-id", "child-id", null, null, null],
            f.Authorization.Requests.Select(request => request.TargetExactId));
        Assert.Equal([null, null, "missing", null, null],
            f.Authorization.Requests.Select(request => request.TypedValue));
        Assert.All(f.Authorization.Requests, request =>
        {
            Assert.Same(f.CandidateBinding.Authorization, request.Authorization);
            Assert.Same(f.CandidateBinding.Binding, request.Binding);
            Assert.Equal(new Uri(f.Binding.SourceUri), request.ReadBinding.OfficialUri);
            Assert.Equal(BaiduCandidatePageState.DestinationPicker, request.ReadBinding.ExpectedPageState);
            Assert.Equal(f.Binding.ExpectedBaiduUserId, request.ReadBinding.ExpectedBaiduUserId);
            Assert.Equal(f.Evidence.Snapshot.ParentExactId, request.ReadBinding.ExpectedParentExactId);
            Assert.Equal(f.Evidence.Snapshot.CanonicalPath, request.ReadBinding.ExpectedCanonicalPath!.Value);
            Assert.Equal(f.Evidence.Snapshot.Items.Select(item => item.ExactId).Order(), request.ReadBinding.ExpectedExactIds);
        });
    }

    [Fact]
    public async Task FailedPrePermitProofRetainsPickerAndUsesZeroPermitOrDispatch()
    {
        var f = new Fixture { ProofResult = false };
        var result = await f.OpenRoot();

        Assert.Equal(ManagedTransferDirectoryMutationOutcome.PageNotValidated, result.Outcome);
        Assert.True(result.State.PickerOpen);
        Assert.Single(f.Proofs);
        Assert.Empty(f.Authorization.Requests);
        Assert.Empty(f.Requests);
    }

    [Fact]
    public async Task FailedPostPermitProofRetainsPickerAndReleasesOnlyPermit()
    {
        var f = new Fixture { FailProofNumber = 2 };
        var result = await f.OpenRoot();

        Assert.Equal(ManagedTransferDirectoryMutationOutcome.PageNotValidated, result.Outcome);
        Assert.True(result.State.PickerOpen);
        Assert.Single(f.Authorization.Requests);
        Assert.Equal(1, f.Authorization.Disposed);
        Assert.Empty(f.Requests);
    }

    [Fact]
    public async Task CandidateBindingExpiryDuringBlockedPermitAcquisitionDispatchesZeroHelperMutation()
    {
        var f = new Fixture();
        var candidateBinding = f.CandidateBinding with
        {
            Binding = f.CandidateBinding.Binding with
            {
                ExpiresAtUtc = f.Clock.GetUtcNow().AddMilliseconds(500)
            }
        };
        f.Authorization.AcquisitionGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = f.Mutator.OpenRootAsync(f.Identity, candidateBinding, f.Binding, f.Evidence,
            new(true, false, false), default);
        await f.Authorization.AcquisitionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        f.Clock.Advance(TimeSpan.FromMilliseconds(600));
        f.Authorization.AcquisitionGate.SetResult();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(ManagedTransferDirectoryMutationOutcome.PageNotValidated, result.Outcome);
        Assert.Equal(new(true, false, false), result.State);
        Assert.Single(f.Authorization.Requests);
        Assert.Empty(f.Requests);
        Assert.Equal(1, f.Authorization.Disposed);
        await result.PendingSettlement.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task FutureIssuedCandidateBindingUsesZeroProofPermitOrDispatch()
    {
        var f = new Fixture();
        var candidateBinding = f.CandidateBinding with
        {
            Binding = f.CandidateBinding.Binding with
            {
                IssuedAtUtc = f.Clock.GetUtcNow().AddMinutes(1),
                ExpiresAtUtc = f.Clock.GetUtcNow().AddMinutes(2)
            }
        };

        var result = await f.Mutator.OpenRootAsync(f.Identity, candidateBinding, f.Binding, f.Evidence,
            new(true, false, false), default);

        Assert.Equal(ManagedTransferDirectoryMutationOutcome.PageNotValidated, result.Outcome);
        Assert.Equal(new(true, false, false), result.State);
        Assert.Empty(f.Proofs);
        Assert.Empty(f.Authorization.Requests);
        Assert.Empty(f.Requests);
    }

    [Fact]
    public async Task BeginPermitDenialRetainsStartingPickerState()
    {
        var f = new Fixture();
        f.Authorization.ReturnNull = true;

        var result = await f.Begin();

        Assert.Equal(ManagedTransferDirectoryMutationOutcome.PageNotValidated, result.Outcome);
        Assert.Equal(new(true, false, false), result.State);
        Assert.Empty(f.Requests);
    }

    [Fact]
    public async Task StateBoundaryMatrixPreservesPriorSafetyFacts()
    {
        var prior = new ManagedTransferDirectoryMutationState(true, true, true);

        var preDispatch = new Fixture();
        preDispatch.Authorization.ReturnNull = true;
        Assert.Equal(prior, (await preDispatch.Confirm(prior)).State);

        var cancelUnknown = new Fixture { MutationKindOverride = ManagedBrowserDirectoryMutationResultKind.OutcomeUnknown };
        var unknown = await cancelUnknown.Cancel(prior);
        Assert.Equal(ManagedTransferDirectoryMutationOutcome.QuarantineRequired, unknown.Outcome);
        Assert.Equal(prior, unknown.State);

        var observerDenied = new Fixture { ClosedKind = ManagedBrowserTransferPickerClosedResultKind.PageNotValidated };
        var inconclusive = await observerDenied.Cancel(prior);
        Assert.Equal(ManagedTransferDirectoryMutationOutcome.QuarantineRequired, inconclusive.Outcome);
        Assert.Equal(prior, inconclusive.State);

        var finalProofDenied = new Fixture { FailProofNumber = 3 };
        var finalDenied = await finalProofDenied.Cancel(prior);
        Assert.Equal(ManagedTransferDirectoryMutationOutcome.QuarantineRequired, finalDenied.Outcome);
        Assert.Equal(prior, finalDenied.State);

        var proven = await new Fixture().Cancel(prior);
        Assert.Equal(ManagedTransferDirectoryMutationOutcome.PickerClosed, proven.Outcome);
        Assert.Equal(new(false, false, true), proven.State);
    }

    [Fact]
    public async Task ConfirmEnterBeginConfirmSequenceNeverClearsPriorPossibleMutation()
    {
        var f = new Fixture();
        var initial = new ManagedTransferDirectoryMutationState(true, true, false);

        var firstConfirm = await f.Confirm(initial);
        var entered = await f.Enter(firstConfirm.State);
        var secondBegin = await f.Begin(entered.State);
        var secondConfirm = await f.Confirm(secondBegin.State);
        var rooted = await f.OpenRoot(secondConfirm.State);

        Assert.Equal(new(true, false, true), firstConfirm.State);
        Assert.Equal(new(true, false, true), entered.State);
        Assert.Equal(new(true, true, true), secondBegin.State);
        Assert.Equal(new(true, false, true), secondConfirm.State);
        Assert.Equal(new(true, false, true), rooted.State);
    }

    [Fact]
    public async Task SequentialFailureAndQuarantineRetainPriorPossibleMutation()
    {
        var prior = new ManagedTransferDirectoryMutationState(true, false, true);
        var denied = new Fixture { ProofResult = false };

        var enterFailure = await denied.Enter(prior);
        var beginFailure = await denied.Begin(prior);

        Assert.Equal(prior, enterFailure.State);
        Assert.Equal(prior, beginFailure.State);

        var quarantine = new Fixture { MutationKindOverride = ManagedBrowserDirectoryMutationResultKind.PageNotValidated };
        var confirm = await quarantine.Confirm(new(true, true, true));
        Assert.Equal(ManagedTransferDirectoryMutationOutcome.QuarantineRequired, confirm.Outcome);
        Assert.Equal(new(true, true, true), confirm.State);
    }

    [Fact]
    public async Task CancelPermitDisposalFailureRetainsPriorStateAndRequiresQuarantine()
    {
        var f = new Fixture();
        f.Authorization.ThrowOnDispose = true;
        var prior = new ManagedTransferDirectoryMutationState(true, true, true);

        var result = await f.Cancel(prior);

        Assert.Equal(ManagedTransferDirectoryMutationOutcome.QuarantineRequired, result.Outcome);
        Assert.Equal(prior, result.State);
        Assert.DoesNotContain(f.Requests, request => request.Action == ManagedBrowserAction.ObserveTransferPickerClosed);
    }

    [Fact]
    public async Task ConfirmFailureRetainsOpenCreateUiAndPriorPossibleMutation()
    {
        var f = new Fixture { MutationKindOverride = ManagedBrowserDirectoryMutationResultKind.PageNotValidated };
        var prior = new ManagedTransferDirectoryMutationState(true, true, true);
        var result = await f.Confirm(prior);

        Assert.Equal(ManagedTransferDirectoryMutationOutcome.QuarantineRequired, result.Outcome);
        Assert.Equal(prior, result.State);
    }

    [Fact]
    public async Task OneDeadlineBuildsEveryHelperBindingFromStrictlyDecreasingRemainingBudget()
    {
        var f = new Fixture();
        f.ProofStep = () => f.Clock.Advance(TimeSpan.FromMilliseconds(100));
        f.Authorization.AcquireStep = () => f.Clock.Advance(TimeSpan.FromMilliseconds(100));
        f.SendStep = _ => f.Clock.Advance(TimeSpan.FromMilliseconds(100));
        f.Authorization.DisposeStep = () => f.Clock.Advance(TimeSpan.FromMilliseconds(100));

        var result = await f.OpenRoot();

        Assert.Equal(ManagedTransferDirectoryMutationOutcome.RootOpened, result.Outcome);
        var timeouts = f.HelperBindings.Select(binding => binding.TimeoutMilliseconds).ToArray();
        Assert.True(timeouts.Length >= 4);
        Assert.All(timeouts, timeout => Assert.InRange(timeout, 1, f.Binding.TimeoutMilliseconds - 1));
        Assert.True(timeouts.Zip(timeouts.Skip(1), (left, right) => left > right).All(value => value));
    }

    [Fact]
    public async Task DeadlineBeforePermitIsTypedTimeoutWithNoPermitOrDispatch()
    {
        var f = new Fixture();
        f.ProofStep = () => f.Clock.Advance(TimeSpan.FromMilliseconds(f.Binding.TimeoutMilliseconds));

        var result = await f.OpenRoot();

        Assert.Equal(ManagedTransferDirectoryMutationOutcome.TimedOut, result.Outcome);
        Assert.True(result.State.PickerOpen);
        Assert.Empty(f.Authorization.Requests);
        Assert.Empty(f.Requests);
    }

    [Fact]
    public async Task InjectedClockExpiryCancelsPendingPermitWithoutWallClockDelay()
    {
        var f = new Fixture(5000);
        f.Authorization.AcquisitionGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = f.OpenRoot();
        await f.Authorization.AcquisitionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        f.Clock.Advance(TimeSpan.FromMilliseconds(f.Binding.TimeoutMilliseconds));
        _ = await Task.WhenAny(run, Task.Delay(100));
        var completedFromInjectedClock = run.IsCompleted;
        f.Authorization.AcquisitionGate.SetResult();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(completedFromInjectedClock);
        Assert.Equal(ManagedTransferDirectoryMutationOutcome.TimedOut, result.Outcome);
        Assert.Empty(f.Requests);
    }

    [Fact]
    public async Task DeadlineAfterPermitButBeforeDispatchIsTypedTimeoutAndDisposesPermit()
    {
        var f = new Fixture();
        f.Authorization.AcquireStep = () => f.Clock.Advance(TimeSpan.FromMilliseconds(f.Binding.TimeoutMilliseconds));

        var result = await f.OpenRoot();

        Assert.Equal(ManagedTransferDirectoryMutationOutcome.TimedOut, result.Outcome);
        Assert.True(result.State.PickerOpen);
        Assert.Single(f.Authorization.Requests);
        Assert.Equal(1, f.Authorization.Disposed);
        Assert.Empty(f.Requests);
    }

    [Fact]
    public async Task DeadlineAfterPossibleDispatchIsOutcomeUnknownAndDoesNotRead()
    {
        var f = new Fixture();
        f.SendStep = request =>
        {
            if (request.Action == ManagedBrowserAction.OpenTransferDirectoryRoot)
                f.Clock.Advance(TimeSpan.FromMilliseconds(f.Binding.TimeoutMilliseconds));
        };

        var result = await f.OpenRoot();

        Assert.Equal(ManagedTransferDirectoryMutationOutcome.OutcomeUnknown, result.Outcome);
        Assert.True(result.State.PickerOpen);
        Assert.Single(f.Requests);
    }

    [Fact]
    public async Task DeadlineReachedDuringPermitDisposalIsOutcomeUnknownAndDoesNotRead()
    {
        var f = new Fixture();
        f.Authorization.DisposeStep = () =>
            f.Clock.Advance(TimeSpan.FromMilliseconds(f.Binding.TimeoutMilliseconds));

        var result = await f.OpenRoot();

        Assert.Equal(ManagedTransferDirectoryMutationOutcome.OutcomeUnknown, result.Outcome);
        Assert.True(result.State.PickerOpen);
        Assert.Single(f.Requests);
        Assert.Equal(1, f.Authorization.Disposed);
    }

    [Fact]
    public async Task CallerCancellationBeforeDispatchIsTypedAndRetainsPicker()
    {
        var f = new Fixture();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var result = await f.OpenRoot(cancelled.Token);

        Assert.Equal(ManagedTransferDirectoryMutationOutcome.Cancelled, result.Outcome);
        Assert.True(result.State.PickerOpen);
        Assert.Empty(f.Authorization.Requests);
        Assert.Empty(f.Requests);
    }

    [Fact]
    public async Task CallerCancellationAfterSendStartsIsOutcomeUnknownAndLateFaultIsObserved()
    {
        var f = new Fixture();
        var pending = new TaskCompletionSource<ManagedBrowserResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.SendOverride = (request, _) =>
        {
            f.Requests.Add(request);
            started.TrySetResult();
            return pending.Task;
        };
        using var cancellation = new CancellationTokenSource();

        var running = f.OpenRoot(cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, f.Authorization.Disposed);
        Assert.False(result.PendingSettlement.IsCompleted);
        pending.SetException(new IOException("private late helper failure"));
        await f.Authorization.DisposedSignal.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await result.PendingSettlement.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(ManagedTransferDirectoryMutationOutcome.OutcomeUnknown, result.Outcome);
        Assert.True(result.State.PickerOpen);
        Assert.Single(f.Requests);
        Assert.Equal(1, f.Authorization.Disposed);
        Assert.True(result.PendingSettlement.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task CancelledPendingPermitAcquisitionReturnsBeforeLatePermitDisposalButExposesSettlement()
    {
        var f = new Fixture();
        f.Authorization.AcquisitionGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Authorization.IgnoreAcquisitionCancellation = true;
        f.Authorization.ReleaseGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();

        var running = f.OpenRoot(cancellation.Token);
        await f.Authorization.AcquisitionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(ManagedTransferDirectoryMutationOutcome.Cancelled, result.Outcome);
        Assert.Empty(f.Requests);
        Assert.False(result.PendingSettlement.IsCompleted);
        f.Authorization.AcquisitionGate.SetResult();
        await f.Authorization.ReleaseStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(result.PendingSettlement.IsCompleted);
        f.Authorization.ReleaseGate.SetResult();
        await result.PendingSettlement.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, f.Authorization.Disposed);
        Assert.True(result.PendingSettlement.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task TimedOutPendingPermitAcquisitionObservesLateAcquireAndDisposalFaults()
    {
        var f = new Fixture(timeoutMilliseconds: 25);
        f.Authorization.AcquisitionGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Authorization.IgnoreAcquisitionCancellation = true;
        f.Authorization.ReleaseGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Authorization.ThrowOnDispose = true;

        var running = f.OpenRoot();
        await f.Authorization.AcquisitionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        f.Clock.Advance(TimeSpan.FromMilliseconds(f.Binding.TimeoutMilliseconds));
        var result = await running.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(ManagedTransferDirectoryMutationOutcome.TimedOut, result.Outcome);
        Assert.Empty(f.Requests);
        Assert.False(result.PendingSettlement.IsCompleted);
        f.Authorization.AcquisitionGate.SetResult();
        await f.Authorization.ReleaseStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        f.Authorization.ReleaseGate.SetResult();
        await result.PendingSettlement.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(result.PendingSettlement.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task CancellationDuringAsyncPermitDisposalReturnsUnknownWithVisibleSettlement()
    {
        var f = new Fixture();
        f.Authorization.ReleaseGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();

        var running = f.OpenRoot(cancellation.Token);
        await f.Authorization.ReleaseStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(ManagedTransferDirectoryMutationOutcome.OutcomeUnknown, result.Outcome);
        Assert.False(result.PendingSettlement.IsCompleted);
        f.Authorization.ReleaseGate.SetResult();
        await result.PendingSettlement.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(result.PendingSettlement.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData("root")]
    [InlineData("enter")]
    [InlineData("begin")]
    [InlineData("confirm")]
    [InlineData("cancel")]
    [InlineData("denied")]
    public async Task EveryActionResultExposesNonFaultingSettlement(string action)
    {
        var f = new Fixture();
        ManagedTransferDirectoryMutationExecutionResult result = action switch
        {
            "root" => await f.OpenRoot(),
            "enter" => await f.Enter(),
            "begin" => await f.Begin(),
            "confirm" => await f.Confirm(new(true, true, false)),
            "cancel" => await f.Cancel(new(true, false, false)),
            _ => await f.Enter(new ManagedTransferDirectoryMutationState(false, false, true)),
        };

        await result.PendingSettlement.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(result.PendingSettlement.IsCompletedSuccessfully);
        Assert.DoesNotContain("synthetic-user", result.ToString());
    }

    [Fact]
    public async Task ConfirmSuccessRequiresExactFreshCurrentDirectoryAndPreservesPossibleMutation()
    {
        var f = new Fixture();
        var prior = new ManagedTransferDirectoryMutationState(true, true, true);
        var result = await f.Confirm(prior);

        Assert.Equal(ManagedTransferDirectoryMutationOutcome.DirectoryCreated, result.Outcome);
        Assert.Equal(new(true, false, true), result.State);
        var read = Assert.IsType<ManagedBrowserReadTransferDirectoryRequest>(f.Requests[1].Payload);
        Assert.Equal(f.Evidence.Snapshot.ParentExactId, read.ExpectedParentExactId);
        Assert.Equal(f.Evidence.Snapshot.CanonicalPath, read.ExpectedCanonicalPath);
    }

    [Fact]
    public async Task MalformedResponseAndDisposalFailureAfterDispatchAreNeverRetryable()
    {
        var malformed = new Fixture { CorruptResponse = true };
        Assert.Equal(ManagedTransferDirectoryMutationOutcome.OutcomeUnknown, (await malformed.OpenRoot()).Outcome);
        Assert.Single(malformed.Requests);

        var disposal = new Fixture();
        disposal.Authorization.ThrowOnDispose = true;
        Assert.Equal(ManagedTransferDirectoryMutationOutcome.OutcomeUnknown, (await disposal.OpenRoot()).Outcome);
        Assert.Single(disposal.Requests);
    }

    [Fact]
    public async Task ResultAndRequestTextRedactPrivateEvidence()
    {
        var f = new Fixture();
        var result = await f.OpenRoot();
        var text = string.Join("\n", new object[] { result, f.Authorization.Requests[0], f.Requests[0].Payload });

        Assert.DoesNotContain("synthetic-user", text);
        Assert.DoesNotContain("child-parent", text);
        Assert.DoesNotContain("child-id", text);
        Assert.DoesNotContain(f.Binding.SourceUri, text);
    }

    [Fact]
    public async Task ExactPersistedRuntimeAndHandlePermitTheBoundRootMutation()
    {
        using var f = await RuntimeFixture.CreateAsync();

        var result = await f.Run();

        Assert.Equal(ManagedTransferDirectoryMutationOutcome.RootOpened, result.Outcome);
        Assert.NotNull(result.Evidence);
        Assert.Single(f.Authorization.Requests);
        Assert.Equal(1, f.Authorization.Disposed);
        Assert.Equal(new[] { ManagedBrowserAction.OpenTransferDirectoryRoot, ManagedBrowserAction.ReadTransferDirectory },
            f.Requests.Select(request => request.Action));
    }

    [Theory]
    [InlineData("installation-revision")]
    [InlineData("session-revision")]
    [InlineData("stable-uid")]
    [InlineData("profile-id")]
    [InlineData("profile-ownership")]
    [InlineData("canonical-profile-path")]
    [InlineData("installation-id")]
    [InlineData("browser-family")]
    [InlineData("browser-id")]
    [InlineData("target-id")]
    [InlineData("endpoint")]
    [InlineData("source-uri")]
    [InlineData("current-directory-evidence")]
    public async Task RealRuntimeMismatchFailsClosedBeforeHelperMutation(string change)
    {
        using var f = await RuntimeFixture.CreateAsync();
        await f.ApplyMismatchAsync(change);

        var result = await f.Run();

        Assert.NotEqual(ManagedTransferDirectoryMutationOutcome.RootOpened, result.Outcome);
        Assert.Null(result.Evidence);
        Assert.DoesNotContain(f.Requests, request => request.Action == ManagedBrowserAction.OpenTransferDirectoryRoot);
        Assert.True(f.Authorization.Requests.Count == 0 || f.Requests.Count == 0);
    }

    [Theory]
    [InlineData("candidate-binding")]
    [InlineData("source-binding")]
    [InlineData("evidence")]
    [InlineData("runtime")]
    public async Task MissingRequiredProductionInputFailsClosedWithZeroPermitAndMutation(string missing)
    {
        using var f = await RuntimeFixture.CreateAsync();

        var result = await f.Mutator.OpenRootAsync(f.Identity,
            missing == "candidate-binding" ? null! : f.CandidateBinding,
            missing == "source-binding" ? null! : f.Binding,
            missing == "evidence" ? null! : f.Evidence,
            new(true, false, false),
            missing == "runtime" ? null : f.Runtime,
            default);

        Assert.Equal(ManagedTransferDirectoryMutationOutcome.PageNotValidated, result.Outcome);
        Assert.Empty(f.Authorization.Requests);
        Assert.DoesNotContain(f.Requests, request => request.Action == ManagedBrowserAction.OpenTransferDirectoryRoot);
    }

    [Theory]
    [InlineData(ManagedBrowserAction.BeginCreateTransferDirectory)]
    [InlineData(ManagedBrowserAction.ConfirmCreateTransferDirectory)]
    [InlineData(ManagedBrowserAction.CancelTransferPicker)]
    public async Task CreateConfirmAndCancelHelperPayloadsRetainExactWireSnapshot(ManagedBrowserAction action)
    {
        var f = new Fixture();
        _ = action switch
        {
            ManagedBrowserAction.BeginCreateTransferDirectory => await f.Begin(),
            ManagedBrowserAction.ConfirmCreateTransferDirectory => await f.Confirm(new(true, true, false)),
            _ => await f.Cancel(new(true, true, true)),
        };

        var command = Assert.Single(f.Requests, request => request.Action == action);
        var payload = Assert.IsType<ManagedBrowserDirectoryMutationRequest>(command.Payload);
        Assert.Equal(action, payload.Action);
        Assert.Same(f.Evidence.Snapshot, payload.ExpectedDirectory);
        Assert.Equal(f.Evidence.Snapshot.ObservationFingerprint, payload.ExpectedDirectory.ObservationFingerprint);
        Assert.Equal(f.Evidence.RootExactId, payload.ExpectedDirectory.AncestorExactIds[0]);
        Assert.Equal(f.Evidence.RootInclusiveAncestorExactIds, payload.ExpectedDirectory.AncestorExactIds);
        Assert.Equal(f.Evidence.Snapshot.ParentExactId, payload.ExpectedDirectory.ParentExactId);
        Assert.Equal(f.Evidence.Snapshot.CanonicalPath, payload.ExpectedDirectory.CanonicalPath);
        Assert.Equal(f.Evidence.Snapshot.BaiduUserId, payload.ExpectedDirectory.BaiduUserId);
        Assert.Equal(f.Evidence.Snapshot.GenerationToken, payload.ExpectedDirectory.GenerationToken);
    }

    private sealed class RuntimeFixture : IDisposable
    {
        internal const string SourceUri = "https://pan.baidu.com/s/runtime-synthetic";
        internal readonly string Root;
        internal readonly SqliteConnectionFactory Factory;
        internal readonly BrowserInstallationIdentity Installation;
        internal readonly ManagedBrowserSessionReference Session;
        internal readonly ManagedBrowserLifecycleCoordinator Lifecycle;
        internal readonly ManagedChromiumProcessManager Manager;
        internal readonly ManagedChromiumSessionHandle Handle;
        internal readonly Authorization Authorization = new();
        internal readonly List<ManagedBrowserRequest> Requests = [];
        internal readonly FakeTargets Targets;
        internal readonly WebAutomationRequestIdentity Identity;
        internal readonly BaiduCandidateSessionBinding CandidateBinding;
        internal ManagedBrowserOpenTransferSourceRequest Binding;
        internal ManagedTransferDirectoryEvidence Evidence;
        internal ManagedTransferDirectoryMutationRuntime Runtime;
        internal readonly ManagedTransferDirectoryMutator Mutator;

        private RuntimeFixture(string root, SqliteConnectionFactory factory,
            BrowserInstallationIdentity installation, ManagedBrowserSessionReference session,
            ManagedBrowserLifecycleCoordinator lifecycle, ManagedChromiumProcessManager manager,
            ManagedChromiumSessionHandle handle, FakeTargets targets,
            ManagedBrowserLifecycleSession captured)
        {
            Root = root;
            Factory = factory;
            Installation = installation;
            Session = session;
            Lifecycle = lifecycle;
            Manager = manager;
            Handle = handle;
            Targets = targets;
            Identity = new(Guid.NewGuid(), Guid.NewGuid(), session.AccountId, 7, WebAutomationOwnerKind.Transfer,
                ExecutionProfileSnapshot.Managed(session.BrowserFamily, session.ProfileId,
                    session.InstallationId, session.SessionId));
            var capability = new AdapterCapabilityVersion("BaiduTransfer", "protocol", "selectors", "synthetic-build");
            var authorization = new AdapterWriteAuthorizationRequest(capability, Identity.AccountId, Identity.OperationId,
                Identity.Generation, Identity.BatchId, AdapterWriteAction.EnsureDirectory);
            CandidateBinding = new(authorization, new(AdapterWriteAuthorizationSource.ConsumedLease, capability,
                Identity.AccountId, Identity.OperationId, Identity.Generation, Identity.BatchId,
                DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5), false, null),
                session.StableBaiduUserId);
            Binding = NewBinding(SourceUri);
            Evidence = NewEvidence(Binding);
            Runtime = new(captured, handle);
            Mutator = new(lifecycle, manager, Send, Authorization);
        }

        internal static async Task<RuntimeFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "directory-mutator-runtime-" + Guid.NewGuid().ToString("N"));
            var factory = new SqliteConnectionFactory(Path.Combine(root, "synthetic.db"));
            await new DatabaseMigrator(factory,
                new DatabaseBackupService(factory, Path.Combine(root, "backups")), "test").MigrateAsync();
            var installation = new BrowserInstallationIdentity(Guid.NewGuid(), BrowserFamily.Chrome,
                @"C:\SyntheticBrowser", @"C:\SyntheticBrowser\chrome.exe", "Google LLC", "Google Chrome", "synthetic-file");
            var session = new ManagedBrowserSessionReference(Guid.NewGuid(), installation.InstallationId, Guid.NewGuid(),
                "synthetic-user", BrowserFamily.Chrome, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
            var store = new SqliteManagedBrowserStateStore(factory);
            Assert.Equal(ManagedBrowserBindingStatus.Bound,
                await store.BindLoginAsync(true, installation, session, null, default));
            var profile = ManagedBrowserProfileDescriptor.FromStoredSession(session, root);
            var executable = new FakeExecutable(installation);
            var profileLease = new FakeProfile(profile);
            var processes = new FakeProcesses();
            var listeners = new FakeListeners();
            var targets = new FakeTargets { Page = new Uri("https://pan.baidu.com/") };
            var manager = new ManagedChromiumProcessManager(
                (_, _) => Task.FromResult<IBrowserExecutableLaunchLease>(executable),
                (_, _) => Task.FromResult<IManagedBrowserProfileLease>(profileLease),
                processes, listeners, targets, TimeSpan.FromMilliseconds(200));
            var lifecycle = new ManagedBrowserLifecycleCoordinator(store, manager, root);
            var handle = (await manager.LaunchAsync(installation, profile)).Session!;
            targets.Page = new Uri(SourceUri);
            var captured = (await lifecycle.LoadPersistedAsync(session.AccountId, default))!;
            return new(root, factory, installation, session, lifecycle, manager, handle, targets, captured);
        }

        internal Task<ManagedTransferDirectoryMutationExecutionResult> Run() =>
            Mutator.OpenRootAsync(Identity, CandidateBinding, Binding, Evidence,
                new(true, false, false), Runtime, default);

        internal async Task ApplyMismatchAsync(string change)
        {
            if (change == "installation-revision")
            {
                await Execute("UPDATE BrowserInstallations SET Revision=Revision+1;");
                return;
            }
            if (change == "session-revision")
            {
                await Execute("UPDATE ManagedBrowserSessions SET Revision=Revision+1;");
                return;
            }
            if (change == "browser-id")
            {
                Binding = new(Guid.NewGuid(), Binding.TargetId, Binding.DevToolsEndpoint,
                    Binding.TimeoutMilliseconds, Binding.SourceUri, Binding.ExpectedBaiduUserId);
                Evidence = NewEvidence(Binding);
                return;
            }
            if (change == "target-id")
            {
                Binding = new(Binding.BrowserId, "different-tab", Binding.DevToolsEndpoint,
                    Binding.TimeoutMilliseconds, Binding.SourceUri, Binding.ExpectedBaiduUserId);
                return;
            }
            if (change == "endpoint")
            {
                Binding = new(Binding.BrowserId, Binding.TargetId, new Uri("http://127.0.0.1:49153/"),
                    Binding.TimeoutMilliseconds, Binding.SourceUri, Binding.ExpectedBaiduUserId);
                return;
            }
            if (change == "source-uri")
            {
                Binding = NewBinding("https://pan.baidu.com/s/different-runtime");
                Evidence = NewEvidence(Binding);
                return;
            }
            if (change == "current-directory-evidence")
            {
                var raw = new ManagedBrowserDirectorySnapshot(Session.StableBaiduUserId, "other-generation",
                    "other-current", "/other", ["root-id", "other-current"], 0, true, true, [], new string('0', 64));
                var snapshot = new ManagedBrowserDirectorySnapshot(raw.BaiduUserId, raw.GenerationToken,
                    raw.ParentExactId, raw.CanonicalPath, raw.AncestorExactIds, raw.TotalCount, true, true, raw.Items,
                    new string('0', 64));
                Evidence = ManagedTransferDirectoryEvidence.CreateFromReaderValidated(snapshot);
                return;
            }

            var original = Runtime.CapturedSession.Session;
            var changed = change switch
            {
                "stable-uid" => NewSession(original, stableUid: "different-user"),
                "profile-id" => NewSession(original, profileId: Guid.NewGuid().ToString("N")),
                "profile-ownership" => NewSession(original, ownershipId: Guid.NewGuid().ToString("N")),
                "installation-id" => NewSession(original, installationId: Guid.NewGuid()),
                "browser-family" => NewSession(original, family: BrowserFamily.Edge),
                _ => original,
            };
            var canonicalPath = change == "canonical-profile-path"
                ? Path.Combine(Root, "different-profile")
                : Runtime.CapturedSession.CanonicalProfilePath;
            Runtime = new(new ManagedBrowserLifecycleSession(changed,
                Runtime.CapturedSession.InstallationRevision, Runtime.CapturedSession.SessionRevision, canonicalPath), Handle);
        }

        private static ManagedBrowserSessionReference NewSession(ManagedBrowserSessionReference source,
            string? stableUid = null, string? profileId = null, string? ownershipId = null,
            Guid? installationId = null, BrowserFamily? family = null) =>
            new(source.SessionId, installationId ?? source.InstallationId, source.AccountId,
                stableUid ?? source.StableBaiduUserId, family ?? source.BrowserFamily,
                profileId ?? source.ProfileId, ownershipId ?? source.ProfileOwnershipId);

        private ManagedBrowserOpenTransferSourceRequest NewBinding(string sourceUri) =>
            new(Handle.BrowserId, Handle.TargetId, Handle.Endpoint, 1000, sourceUri, Session.StableBaiduUserId);

        private ManagedTransferDirectoryEvidence NewEvidence(ManagedBrowserOpenTransferSourceRequest binding)
        {
            var item = new ManagedBrowserSourceItem("child-id", ManagedBrowserSourceItemKind.Folder, "child");
            var raw = new ManagedBrowserDirectorySnapshot(Session.StableBaiduUserId, "directory-generation",
                "child-parent", "/one", ["root-id", "child-parent"], 1, true, true, [item], new string('0', 64));
            var snapshot = new ManagedBrowserDirectorySnapshot(raw.BaiduUserId, raw.GenerationToken,
                raw.ParentExactId, raw.CanonicalPath, raw.AncestorExactIds, raw.TotalCount, true, true, raw.Items,
                raw.ComputeFingerprint(binding.SourceUri));
            return ManagedTransferDirectoryEvidence.CreateFromReaderValidated(snapshot);
        }

        private Task<ManagedBrowserResponse> Send(ManagedBrowserRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (request.Action == ManagedBrowserAction.ReadTransferDirectory)
            {
                var raw = new ManagedBrowserDirectorySnapshot(Session.StableBaiduUserId, "root-generation",
                    "root-id", "/", ["root-id"], 0, true, true, [], new string('0', 64));
                var snapshot = new ManagedBrowserDirectorySnapshot(raw.BaiduUserId, raw.GenerationToken,
                    raw.ParentExactId, raw.CanonicalPath, raw.AncestorExactIds, raw.TotalCount, true, true, raw.Items,
                    raw.ComputeFingerprint(Binding.SourceUri));
                return Task.FromResult(ManagedBrowserResponse.For(request,
                    new ManagedBrowserDirectoryListingResult(ManagedBrowserDirectoryListingResultKind.Observed, snapshot)));
            }
            return Task.FromResult(ManagedBrowserResponse.For(request,
                new ManagedBrowserDirectoryMutationResult(ManagedBrowserDirectoryMutationResultKind.RootOpened)));
        }

        private async Task Execute(string sql)
        {
            await using var connection = await Factory.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        public void Dispose() => Manager.Dispose();
    }

    private sealed class Fixture
    {
        internal readonly ManualTimeProvider Clock = new(new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero));
        internal readonly WebAutomationRequestIdentity Identity;
        internal BaiduCandidateSessionBinding CandidateBinding;
        internal readonly ManagedBrowserOpenTransferSourceRequest Binding;
        internal ManagedTransferDirectoryEvidence Evidence;
        internal readonly ManagedBrowserSourceSnapshot OriginalSource;
        internal readonly Authorization Authorization = new();
        internal readonly List<ManagedBrowserRequest> Requests = [];
        internal readonly List<ManagedTransferDirectoryMutationProof> Proofs = [];
        internal readonly List<ManagedBrowserOpenTransferSourceRequest> HelperBindings = [];
        internal bool ProofResult = true;
        internal int? FailProofNumber;
        internal Action? ProofStep;
        internal Action<ManagedBrowserRequest>? SendStep;
        internal Func<ManagedBrowserRequest, CancellationToken, Task<ManagedBrowserResponse>>? SendOverride;
        internal bool CorruptResponse;
        internal bool ReturnWrongFreshDirectory;
        internal ManagedBrowserDirectoryMutationResultKind? MutationKindOverride;
        internal ManagedBrowserTransferPickerClosedResultKind ClosedKind = ManagedBrowserTransferPickerClosedResultKind.PickerClosed;
        internal readonly ManagedTransferDirectoryMutator Mutator;
        private ManagedBrowserAction? lastMutation;

        internal Fixture(int timeoutMilliseconds = 1000)
        {
            var capability = new AdapterCapabilityVersion("BaiduTransfer", "protocol", "selectors", "synthetic-build");
            Identity = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 7, WebAutomationOwnerKind.Transfer);
            var authorization = new AdapterWriteAuthorizationRequest(capability, Identity.AccountId, Identity.OperationId,
                Identity.Generation, Identity.BatchId, AdapterWriteAction.EnsureDirectory);
            CandidateBinding = new(authorization, new(AdapterWriteAuthorizationSource.ConsumedLease, capability,
                Identity.AccountId, Identity.OperationId, Identity.Generation, Identity.BatchId,
                Clock.GetUtcNow().AddMinutes(-1), Clock.GetUtcNow().AddMinutes(5), false, null), "synthetic-user");
            Binding = new(Guid.NewGuid(), "synthetic-target", new Uri("http://127.0.0.1:9222/"), timeoutMilliseconds,
                "https://pan.baidu.com/s/synthetic", "synthetic-user");
            var sourceItem = new ManagedBrowserSourceItem("source-id", ManagedBrowserSourceItemKind.File, "source");
            var sourceRaw = new ManagedBrowserSourceSnapshot("synthetic-user", "source-generation", 1, true, true,
                [sourceItem], new string('0', 64));
            OriginalSource = new ManagedBrowserSourceSnapshot(sourceRaw.BaiduUserId, sourceRaw.GenerationToken,
                sourceRaw.TotalCount, true, true, sourceRaw.Items, sourceRaw.ComputeFingerprint(Binding.SourceUri));
            Evidence = EvidenceWith();
            Mutator = new(Send, Authorization, Prove, Clock);
        }

        internal Task<ManagedTransferDirectoryMutationExecutionResult> OpenRoot(CancellationToken token = default) =>
            OpenRoot(new(true, false, false), token);
        internal Task<ManagedTransferDirectoryMutationExecutionResult> OpenRoot(
            ManagedTransferDirectoryMutationState state, CancellationToken token = default) =>
            Mutator.OpenRootAsync(Identity, CandidateBinding, Binding, Evidence, state, token);
        internal Task<ManagedTransferDirectoryMutationExecutionResult> SelectionOpenRoot(
            CancellationToken token = default) =>
            Mutator.OpenRootForSelectionAsync(Identity, CandidateBinding, Binding, Evidence,
                new(true, false, false), token);
        internal Task<ManagedTransferDirectoryMutationExecutionResult> Enter(CancellationToken token = default) =>
            Enter(new(true, false, false), token);
        internal Task<ManagedTransferDirectoryMutationExecutionResult> Enter(
            ManagedTransferDirectoryMutationState state, CancellationToken token = default) =>
            Mutator.EnterDirectoryAsync(Identity, CandidateBinding, Binding, Evidence, "child-id", state, token);
        internal Task<ManagedTransferDirectoryMutationExecutionResult> SelectionEnter(
            CancellationToken token = default) =>
            Mutator.EnterDirectoryForSelectionAsync(Identity, CandidateBinding, Binding, Evidence,
                "child-id", new(true, false, false), token);
        internal void SetAuthorizationStage(AdapterWriteAction action)
        {
            CandidateBinding = new(CandidateBinding.Authorization with { Action = action },
                CandidateBinding.Binding, CandidateBinding.ExpectedBaiduUserId);
        }
        internal Task<ManagedTransferDirectoryMutationExecutionResult> Begin(CancellationToken token = default) =>
            Begin(new(true, false, false), token);
        internal Task<ManagedTransferDirectoryMutationExecutionResult> Begin(
            ManagedTransferDirectoryMutationState state, CancellationToken token = default) =>
            Mutator.BeginCreateAsync(Identity, CandidateBinding, Binding, Evidence, "missing", state, token);
        internal Task<ManagedTransferDirectoryMutationExecutionResult> Confirm(ManagedTransferDirectoryMutationState state,
            CancellationToken token = default) => Mutator.ConfirmCreateAsync(Identity, CandidateBinding, Binding,
                Evidence, "missing", state, token);
        internal Task<ManagedTransferDirectoryMutationExecutionResult> Cancel(ManagedTransferDirectoryMutationState state,
            CancellationToken token = default) => Mutator.CancelDestinationPickerAsync(Identity, CandidateBinding,
                Binding, Evidence, OriginalSource, state, token);

        internal ManagedTransferDirectoryEvidence EvidenceWith(string uid = "synthetic-user", string? fingerprint = null)
        {
            var item = new ManagedBrowserSourceItem("child-id", ManagedBrowserSourceItemKind.Folder, "child");
            var raw = new ManagedBrowserDirectorySnapshot(uid, "directory-generation", "child-parent", "/one",
                ["root-id", "child-parent"], 1, true, true, [item], new string('0', 64));
            var snapshot = new ManagedBrowserDirectorySnapshot(raw.BaiduUserId, raw.GenerationToken, raw.ParentExactId,
                raw.CanonicalPath, raw.AncestorExactIds, raw.TotalCount, true, true, raw.Items,
                fingerprint ?? raw.ComputeFingerprint(Binding.SourceUri));
            return ManagedTransferDirectoryEvidence.CreateFromReaderValidated(snapshot);
        }

        private Task<bool> Prove(ManagedTransferDirectoryMutationProof proof, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Proofs.Add(proof);
            HelperBindings.Add(proof.Binding);
            ProofStep?.Invoke();
            return Task.FromResult(ProofResult && FailProofNumber != Proofs.Count);
        }

        private Task<ManagedBrowserResponse> Send(ManagedBrowserRequest request, CancellationToken token)
        {
            if (SendOverride is not null) return SendOverride(request, token);
            token.ThrowIfCancellationRequested();
            Requests.Add(request);
            SendStep?.Invoke(request);
            if (request.Payload is ManagedBrowserDirectoryMutationRequest mutation) HelperBindings.Add(mutation.Binding);
            if (request.Payload is ManagedBrowserReadTransferDirectoryRequest read) HelperBindings.Add(read.Binding);
            if (request.Payload is ManagedBrowserObserveTransferPickerClosedRequest closed) HelperBindings.Add(closed.Binding);
            if (CorruptResponse)
                return Task.FromResult(new ManagedBrowserResponse(Guid.NewGuid(), request.OperationId, request.AccountId,
                    request.Generation, request.Action,
                    new ManagedBrowserDirectoryMutationResult(ManagedBrowserDirectoryMutationResultKind.RootOpened)));
            if (request.Action == ManagedBrowserAction.ReadTransferDirectory) return Task.FromResult(DirectoryResponse(request));
            if (request.Action == ManagedBrowserAction.ObserveTransferPickerClosed)
                return Task.FromResult(ManagedBrowserResponse.For(request, new ManagedBrowserTransferPickerClosedResult(ClosedKind)));
            lastMutation = request.Action;
            var kind = MutationKindOverride ?? request.Action switch
            {
                ManagedBrowserAction.OpenTransferDirectoryRoot => ManagedBrowserDirectoryMutationResultKind.RootOpened,
                ManagedBrowserAction.EnterTransferDirectory => ManagedBrowserDirectoryMutationResultKind.DirectoryEntered,
                ManagedBrowserAction.BeginCreateTransferDirectory => ManagedBrowserDirectoryMutationResultKind.CreateDialogOpened,
                ManagedBrowserAction.ConfirmCreateTransferDirectory => ManagedBrowserDirectoryMutationResultKind.DirectoryCreated,
                ManagedBrowserAction.CancelTransferPicker => ManagedBrowserDirectoryMutationResultKind.PickerCancelled,
                _ => ManagedBrowserDirectoryMutationResultKind.PageNotValidated,
            };
            return Task.FromResult(ManagedBrowserResponse.For(request, new ManagedBrowserDirectoryMutationResult(kind)));
        }

        private ManagedBrowserResponse DirectoryResponse(ManagedBrowserRequest request)
        {
            var (parent, path, ancestors) = ReturnWrongFreshDirectory
                ? ("wrong-parent", "/wrong", new[] { "root-id", "wrong-parent" })
                : lastMutation == ManagedBrowserAction.OpenTransferDirectoryRoot
                    ? ("root-id", "/", new[] { "root-id" })
                    : lastMutation == ManagedBrowserAction.EnterTransferDirectory
                        ? ("child-id", "/one/child", new[] { "root-id", "child-parent", "child-id" })
                        : ("child-parent", "/one", new[] { "root-id", "child-parent" });
            var raw = new ManagedBrowserDirectorySnapshot("synthetic-user", "next-generation", parent, path,
                ancestors, 0, true, true, [], new string('0', 64));
            var snapshot = new ManagedBrowserDirectorySnapshot(raw.BaiduUserId, raw.GenerationToken, raw.ParentExactId,
                raw.CanonicalPath, raw.AncestorExactIds, raw.TotalCount, true, true, raw.Items,
                raw.ComputeFingerprint(Binding.SourceUri));
            return ManagedBrowserResponse.For(request,
                new ManagedBrowserDirectoryListingResult(ManagedBrowserDirectoryListingResultKind.Observed, snapshot));
        }
    }

    private sealed class Authorization : IBaiduCandidateActionAuthorizationSource
    {
        internal readonly List<BaiduCandidateDomMutationRequest> Requests = [];
        internal readonly TaskCompletionSource DisposedSignal =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Disposed;
        internal int Active;
        internal int MaximumActive;
        internal bool ReturnNull;
        internal bool ThrowOnDispose;
        internal Action? AcquireStep;
        internal Action? DisposeStep;
        internal TaskCompletionSource? AcquisitionGate;
        internal TaskCompletionSource? ReleaseGate;
        internal bool IgnoreAcquisitionCancellation;
        internal readonly TaskCompletionSource AcquisitionStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<bool> IsAuthorizedAsync(BaiduCandidateDomRequest request, CancellationToken token) => ValueTask.FromResult(false);
        public ValueTask<IBaiduCandidateMutationPermit?> TryAcquireMutationPermitAsync(BaiduCandidateDomRequest request,
            CancellationToken token) => ValueTask.FromResult<IBaiduCandidateMutationPermit?>(null);
        public async ValueTask<IBaiduCandidateMutationPermit?> TryAcquireTransferMutationPermitAsync(
            BaiduCandidateDomMutationRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Requests.Add(request);
            AcquireStep?.Invoke();
            AcquisitionStarted.TrySetResult();
            if (AcquisitionGate is not null)
            {
                if (IgnoreAcquisitionCancellation) await AcquisitionGate.Task;
                else await AcquisitionGate.Task.WaitAsync(token);
            }
            if (ReturnNull) return null;
            Active++;
            MaximumActive = Math.Max(MaximumActive, Active);
            return new Permit(this);
        }
        private sealed class Permit(Authorization owner) : IBaiduCandidateMutationPermit
        {
            public async ValueTask DisposeAsync()
            {
                owner.Disposed++;
                owner.Active--;
                owner.DisposedSignal.TrySetResult();
                owner.DisposeStep?.Invoke();
                owner.ReleaseStarted.TrySetResult();
                if (owner.ReleaseGate is not null) await owner.ReleaseGate.Task;
                if (owner.ThrowOnDispose) throw new IOException("private permit failure");
            }
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private readonly object gate = new();
        private readonly List<ManualTimer> timers = [];
        private long timestamp;
        private DateTimeOffset utcNow = now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => utcNow;
        public override long GetTimestamp() => timestamp;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            lock (gate) timers.Add(timer);
            return timer;
        }

        internal void Advance(TimeSpan amount)
        {
            ManualTimer[] due;
            lock (gate)
            {
                utcNow += amount;
                timestamp += amount.Ticks;
                due = timers.Where(timer => timer.IsDue(timestamp)).ToArray();
            }
            foreach (var timer in due) timer.Fire(timestamp);
        }

        private void Remove(ManualTimer timer)
        {
            lock (gate) timers.Remove(timer);
        }

        private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            private long dueAt = long.MaxValue;
            private long periodTicks = Timeout.InfiniteTimeSpan.Ticks;
            private bool disposed;

            internal bool IsDue(long now) => !disposed && dueAt <= now;

            internal void Fire(long now)
            {
                if (disposed || dueAt > now) return;
                dueAt = periodTicks == Timeout.InfiniteTimeSpan.Ticks ? long.MaxValue : now + periodTicks;
                callback(state);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (disposed) return false;
                dueAt = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner.timestamp + dueTime.Ticks;
                periodTicks = period.Ticks;
                return true;
            }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                owner.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
