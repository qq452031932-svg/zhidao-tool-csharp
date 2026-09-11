using BaiduBatchTransfer.App.Features.BrowserAutomation;
using BaiduBatchTransfer.App.Features.Acceptance;
using BaiduBatchTransfer.App.Features.ManagedBrowser;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.BaiduAdapter.WebAutomation;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Infrastructure.Security;
using BaiduBatchTransfer.Infrastructure.Database;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class ManagedTransferFinalSubmissionSessionTests
{
    [Fact]
    public async Task ConcreteFactoryScopesKeepInterleavedAccountsAndFixedHelperBindingsIndependent()
    {
        var a = new Fixture(WebAutomationAttemptState.BaselinePersisted);
        var b = new Fixture(WebAutomationAttemptState.BaselinePersisted);
        var requestA = WithCurrentCandidateWindow(a.Request);
        var requestB = WithCurrentCandidateWindow(b.Request);
        var requests = new List<ManagedBrowserRequest>();
        var byOperation = new[] { requestA, requestB }
            .ToDictionary(request => request.Identity.OperationId);
        var authorization = new ConcreteAuthorization();

        Task<ManagedBrowserResponse> Send(ManagedBrowserRequest request, CancellationToken _)
        {
            requests.Add(request);
            var owner = byOperation[request.OperationId];
            ManagedBrowserResult result = request.Action switch
            {
                ManagedBrowserAction.OpenTransferPicker => new ManagedBrowserTransferPickerResult(
                    ManagedBrowserTransferPickerResultKind.PickerOpened),
                ManagedBrowserAction.ReadTransferDirectory => DirectoryResult(owner),
                ManagedBrowserAction.ObserveTransferSubmissionReady =>
                    new ManagedBrowserTransferSubmissionReadinessResult(
                        ManagedBrowserTransferSubmissionReadinessResultKind.Ready,
                        new ManagedBrowserTransferSubmissionReadiness(
                            ((ManagedBrowserObserveTransferSubmissionReadyRequest)request.Payload).Context, 1)),
                ManagedBrowserAction.SubmitTransferOnce => new ManagedBrowserSubmitTransferOnceResult(
                    ManagedBrowserTransferSubmitResultKind.ClickAttempted,
                    ManagedBrowserTransferSubmitDispatchState.ClickDispatched),
                ManagedBrowserAction.ObserveTransferSubmissionOutcome =>
                    new ManagedBrowserTransferSubmissionOutcomeResult(
                        ManagedBrowserTransferOutcomeKind.UnboundAcknowledgement),
                _ => throw new InvalidOperationException($"Unexpected action {request.Action}."),
            };
            return Task.FromResult(ManagedBrowserResponse.For(request, result));
        }

        var factory = new ManagedTransferFinalSubmissionConcreteBoundaries(
            (ManagedBrowserLifecycleCoordinator)RuntimeHelpers.GetUninitializedObject(
                typeof(ManagedBrowserLifecycleCoordinator)),
            new AlwaysExactRouteProof(),
            new ManagedTransferPickerOpener(Send, authorization),
            new ManagedTransferDirectoryReader(Send),
            new ManagedTransferDirectoryMutator(Send, authorization,
                static (_, _) => Task.FromResult(true)),
            new ManagedTransferSubmissionOperator(Send),
            new ManagedTransferTargetInventoryReader(Send),
            (ManagedBrowserHelperClient)RuntimeHelpers.GetUninitializedObject(
                typeof(ManagedBrowserHelperClient)),
            authorization,
            new ExactProcessExitObserver());
        requestA = WithFactory(requestA, factory).CreateBoundaryScope();
        requestB = WithFactory(requestB, factory).CreateBoundaryScope();
        var scopeA = requestA.Boundaries;
        var scopeB = requestB.Boundaries;
        Assert.NotSame(scopeA, scopeB);
        var contextA = SubmissionContext(requestA);
        var contextB = SubmissionContext(requestB);

        Assert.Equal(ManagedBrowserTransferPickerResultKind.PickerOpened,
            (await scopeA.OpenPickerAsync(requestA, default)).Kind);
        Assert.Equal(ManagedBrowserTransferPickerResultKind.PickerOpened,
            (await scopeB.OpenPickerAsync(requestB, default)).Kind);
        var readyA = await scopeA.ObserveReadinessAsync(contextA, default);
        var readyB = await scopeB.ObserveReadinessAsync(contextB, default);
        Assert.NotNull(readyA.Readiness);
        Assert.NotNull(readyB.Readiness);
        _ = await scopeA.SubmitOnceAsync(readyA.Readiness!, default);
        _ = await scopeB.SubmitOnceAsync(readyB.Readiness!, default);
        _ = await scopeB.ObserveOutcomeAsync(readyB.Readiness!, default);
        _ = await scopeA.ObserveOutcomeAsync(readyA.Readiness!, default);

        Assert.Equal(2, requests.Count(request =>
            request.Action == ManagedBrowserAction.SubmitTransferOnce));
        Assert.All(requests, request =>
        {
            var owner = byOperation[request.OperationId];
            Assert.Equal(owner.Identity.AccountId, request.AccountId);
            Assert.Equal(owner.Identity.Generation, request.Generation);
            switch (request.Payload)
            {
                case ManagedBrowserOpenTransferPickerRequest picker:
                    Assert.Equal(owner.ExactHandle.BrowserId, picker.Binding.BrowserId);
                    Assert.Equal(owner.ExactHandle.TargetId, picker.Binding.TargetId);
                    break;
                case ManagedBrowserReadTransferDirectoryRequest directory:
                    Assert.Equal(owner.ExactHandle.BrowserId, directory.Binding.BrowserId);
                    Assert.Equal(owner.ExactHandle.TargetId, directory.Binding.TargetId);
                    break;
                case ManagedBrowserObserveTransferSubmissionReadyRequest readiness:
                    Assert.Equal(owner.ExactHandle.BrowserId, readiness.Context.BrowserId);
                    Assert.Equal(owner.ExactHandle.TargetId, readiness.Context.TargetId);
                    Assert.Equal(owner.PreparedDestination.Destination.RemoteId,
                        readiness.Context.SelectedTargetExactId);
                    break;
                case ManagedBrowserSubmitTransferOnceRequest submit:
                    Assert.Equal(owner.ExactHandle.BrowserId, submit.Context.BrowserId);
                    Assert.Equal(owner.ExactHandle.TargetId, submit.Context.TargetId);
                    break;
                case ManagedBrowserObserveTransferSubmissionOutcomeRequest outcome:
                    Assert.Equal(owner.ExactHandle.BrowserId, outcome.Context.BrowserId);
                    Assert.Equal(owner.ExactHandle.TargetId, outcome.Context.TargetId);
                    break;
            }
        });

        static ManagedBrowserDirectoryListingResult DirectoryResult(
            ManagedTransferFinalSubmissionRequest owner)
        {
            var root = "root-" + owner.Identity.OperationId.ToString("N");
            var raw = new ManagedBrowserDirectorySnapshot(
                owner.ExactManagedSession.StableBaiduUserId, "picker-generation", root, "/",
                [root], 0, true, true, [], new string('0', 64));
            var exact = new ManagedBrowserDirectorySnapshot(
                raw.BaiduUserId, raw.GenerationToken, raw.ParentExactId, raw.CanonicalPath,
                raw.AncestorExactIds, raw.TotalCount, true, true, raw.Items,
                raw.ComputeFingerprint(owner.CanonicalSourceUri));
            return new ManagedBrowserDirectoryListingResult(
                ManagedBrowserDirectoryListingResultKind.Observed, exact);
        }

        static ManagedBrowserTransferSubmissionContext SubmissionContext(
            ManagedTransferFinalSubmissionRequest owner)
        {
            var source = owner.ImmutableSource.Items.Single();
            return new ManagedBrowserTransferSubmissionContext(
                owner.ExactHandle.BrowserId, owner.ExactHandle.TargetId,
                owner.ExactHandle.Endpoint, owner.TimeoutMilliseconds,
                owner.CanonicalSourceUri, owner.ExactManagedSession.StableBaiduUserId,
                owner.ImmutableSource.GenerationToken,
                owner.ImmutableSource.ObservationFingerprint,
                owner.PreparedDestination.SourceFingerprint,
                source.ExactId, source.Kind,
                owner.PreparedDestination.Destination.RemoteId,
                owner.PreparedDestination.Destination.Path.Value,
                ((MemoryAttemptStore)owner.AttemptStore).Current
                    .TargetPreparationEvidence!.Baseline.Fingerprint,
                owner.DuplicatePolicy);
        }

        static ManagedTransferFinalSubmissionRequest WithCurrentCandidateWindow(
            ManagedTransferFinalSubmissionRequest owner)
        {
            var now = DateTimeOffset.UtcNow;
            var current = owner.CandidateBinding;
            var binding = current.Binding with
            {
                IssuedAtUtc = now.AddMinutes(-1),
                ExpiresAtUtc = now.AddMinutes(5),
            };
            return new ManagedTransferFinalSubmissionRequest(
                owner.Identity, new BaiduCandidateSessionBinding(
                    current.Authorization, binding, current.ExpectedBaiduUserId),
                owner.ExecutionContext, owner.ExactHandle, owner.LifecycleSession,
                owner.ExactManagedSession, owner.CanonicalSourceUri, owner.ImmutableSource,
                owner.PreparedDestination, owner.DuplicatePolicy, owner.ExpectedAttemptRevision,
                owner.TimeoutMilliseconds, owner.AttemptStore, owner.Boundaries);
        }

        static ManagedTransferFinalSubmissionRequest WithFactory(
            ManagedTransferFinalSubmissionRequest owner,
            IManagedTransferFinalSubmissionBoundaryFactory factory) =>
            new(owner.Identity, owner.CandidateBinding, owner.ExecutionContext,
                owner.ExactHandle, owner.LifecycleSession, owner.ExactManagedSession,
                owner.CanonicalSourceUri, owner.ImmutableSource, owner.PreparedDestination,
                owner.DuplicatePolicy, owner.ExpectedAttemptRevision, owner.TimeoutMilliseconds,
                owner.AttemptStore, factory);
    }
    [Theory]
    [InlineData(WebAutomationAttemptState.Preparing)]
    [InlineData(WebAutomationAttemptState.Submitting)]
    [InlineData(WebAutomationAttemptState.ResultPending)]
    [InlineData(WebAutomationAttemptState.Succeeded)]
    [InlineData(WebAutomationAttemptState.DefinitelyNotSubmitted)]
    [InlineData(WebAutomationAttemptState.NeedsDecision)]
    public async Task NonBaselinePersistedAttemptDispatchesNothing(WebAutomationAttemptState state)
    {
        var fixture = new Fixture(state);

        var result = await fixture.RunAsync();

        Assert.Equal(ManagedTransferFinalSubmissionResultKind.NotEligible, result.Kind);
        Assert.Equal(["load"], fixture.Events);
        Assert.Equal(0, fixture.Store.MutationCalls);
        Assert.Equal(0, fixture.SubmitCalls);
    }

    [Theory]
    [InlineData(ManagedBrowserSourceItemKind.File)]
    [InlineData(ManagedBrowserSourceItemKind.Folder)]
    public async Task ExactHappyPathUsesOneLeaseAndPersistsBothEvidenceStagesInOrder(
        ManagedBrowserSourceItemKind sourceKind)
    {
        var fixture = new Fixture(WebAutomationAttemptState.BaselinePersisted, sourceKind);

        var result = await fixture.RunAsync();

        Assert.True(result.Kind == ManagedTransferFinalSubmissionResultKind.ExactSuccess,
            $"Actual={result.Kind}; Events={string.Join(",", fixture.Events)}");
        Assert.Equal("destination-1", result.DestinationExactId);
        Assert.Null(result.Quarantine);
        Assert.Equal(1, fixture.SubmitCalls);
        Assert.Equal(1, fixture.Lease.Disposals);
        Assert.Equal(new[]
        {
            "load", "lease", "load", "proof:OriginalSource", "picker", "enter:target-id",
            "readiness", "permit", "persist:submission", "load", "proof:DestinationPicker",
            "load", "submit", "process-exit", "official", "persist:official", "target-root",
            "target:root-id", "target-enter:target-id", "target",
            "persist:confirmation", "load:no-dispatch-1", "load:no-dispatch-2",
            "proof:ExactTargetDirectory",
            "permit:dispose", "lease:dispose",
        }, fixture.Events);
    }

    [Fact]
    public async Task SubmissionEvidenceAndSubmissionCasUseTheSameCapturedTimestamp()
    {
        var fixture = new Fixture(WebAutomationAttemptState.BaselinePersisted);
        fixture.Clock.AdvanceOnEveryRead = TimeSpan.FromMilliseconds(1);
        fixture.Store.RequireExactSubmissionTimestamp = true;

        var result = await fixture.RunAsync();

        Assert.Equal(ManagedTransferFinalSubmissionResultKind.ExactSuccess, result.Kind);
        Assert.Equal(1, fixture.SubmitCalls);
    }

    [Fact]
    public async Task AllDuplicatePoliciesRemainFrozenThroughReadinessAndProduceDistinctDigests()
    {
        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        foreach (var policy in new[]
        {
            DuplicatePolicy.Skip,
            DuplicatePolicy.Overwrite,
            DuplicatePolicy.Rename,
        })
        {
            var fixture = new Fixture(
                WebAutomationAttemptState.BaselinePersisted,
                ManagedBrowserSourceItemKind.File, policy);

            var result = await fixture.RunAsync();

            Assert.Equal(ManagedTransferFinalSubmissionResultKind.ExactSuccess, result.Kind);
            Assert.Equal(policy, fixture.Request.DuplicatePolicy);
            Assert.Equal(policy, fixture.ObservedReadinessContext!.DuplicatePolicy);
            Assert.Equal(policy, fixture.ObservedSubmitReadiness!.Context.DuplicatePolicy);
            Assert.True(fingerprints.Add(
                fixture.Store.Current.SubmissionEvidence!.ReadinessFingerprint!));
        }
        Assert.Equal(3, fingerprints.Count);
    }

    [Fact]
    public async Task ReadinessResponseDuplicatePolicyDriftFailsClosedBeforePermitAndSubmit()
    {
        var fixture = new Fixture(
            WebAutomationAttemptState.BaselinePersisted,
            ManagedBrowserSourceItemKind.File, DuplicatePolicy.Skip)
        {
            ReadinessPolicyOverride = DuplicatePolicy.Overwrite,
        };

        var result = await fixture.RunAsync();

        Assert.Equal(ManagedTransferFinalSubmissionResultKind.NotEligible, result.Kind);
        Assert.DoesNotContain("permit", fixture.Events);
        Assert.Equal(0, fixture.SubmitCalls);
        Assert.Equal(0, fixture.Store.MutationCalls);
    }

    [Fact]
    public async Task RecoveryWithDifferentDuplicatePolicyThanStoredReadinessFailsClosed()
    {
        var fixture = new Fixture(
            WebAutomationAttemptState.BaselinePersisted,
            ManagedBrowserSourceItemKind.File, DuplicatePolicy.Skip);
        Assert.Equal(ManagedTransferFinalSubmissionResultKind.ExactSuccess,
            (await fixture.RunAsync()).Kind);
        var original = fixture.Request;
        var changed = new ManagedTransferFinalSubmissionRequest(
            original.Identity, original.CandidateBinding, original.ExecutionContext,
            original.ExactHandle, original.LifecycleSession, original.ExactManagedSession,
            original.CanonicalSourceUri, original.ImmutableSource,
            original.PreparedDestination, DuplicatePolicy.Rename,
            original.ExpectedAttemptRevision, original.TimeoutMilliseconds,
            original.AttemptStore, original.Boundaries);

        var recovered = await new ManagedTransferFinalSubmissionSession(fixture.Clock)
            .RecoverAsync(changed);

        Assert.Equal(ManagedTransferFinalSubmissionResultKind.NotEligible, recovered.Kind);
        Assert.Equal(1, fixture.SubmitCalls);
        Assert.Equal(1, fixture.Events.Count(value => value == "official"));
    }

    [Fact]
    public void UndefinedDuplicatePolicyIsRejectedByFrozenSubmissionRequest()
    {
        var fixture = new Fixture(WebAutomationAttemptState.BaselinePersisted);
        var original = fixture.Request;

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ManagedTransferFinalSubmissionRequest(
                original.Identity, original.CandidateBinding, original.ExecutionContext,
                original.ExactHandle, original.LifecycleSession, original.ExactManagedSession,
                original.CanonicalSourceUri, original.ImmutableSource,
                original.PreparedDestination, (DuplicatePolicy)999,
                original.ExpectedAttemptRevision, original.TimeoutMilliseconds,
                original.AttemptStore, original.Boundaries));
    }

    [Fact]
    public async Task PickerOutcomeUnknownQuarantinesBeforeSubmissionBoundaryAndDispatchesNothingElse()
    {
        var fixture = new Fixture(WebAutomationAttemptState.BaselinePersisted)
        {
            PickerResult = ManagedBrowserTransferPickerResultKind.OutcomeUnknown,
        };

        var result = await fixture.RunAsync();

        Assert.Equal(ManagedTransferFinalSubmissionResultKind.ResultPending, result.Kind);
        Assert.NotNull(result.Quarantine);
        Assert.Equal(["load", "lease", "load", "proof:OriginalSource", "picker"], fixture.Events);
        Assert.Equal(0, fixture.Store.MutationCalls);
        Assert.Equal(0, fixture.SubmitCalls);
        Assert.Equal(0, fixture.Lease.Disposals);
    }

    [Fact]
    public async Task RejectedSubmissionPersistenceSettlesTwoReadsAndReleasesWithoutClick()
    {
        var fixture = new Fixture(WebAutomationAttemptState.BaselinePersisted);
        fixture.Store.PersistSubmissionSucceeds = false;

        var result = await fixture.RunAsync();

        Assert.Equal(ManagedTransferFinalSubmissionResultKind.NotEligible, result.Kind);
        Assert.Equal(0, fixture.SubmitCalls);
        Assert.Equal(1, fixture.Lease.Disposals);
        Assert.Equal(new[]
        {
            "load", "lease", "load", "proof:OriginalSource", "picker", "enter:target-id",
            "readiness", "permit", "persist:submission", "load", "load",
            "proof:DestinationPicker", "permit:dispose", "lease:dispose",
        }, fixture.Events);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public async Task RejectedSubmissionPersistenceLateDurableReadKeepsPermitAndLeaseUntilFreshReconcile(
        int delayedRead)
    {
        var fixture = new Fixture(WebAutomationAttemptState.BaselinePersisted);
        fixture.Store.PersistSubmissionSucceeds = false;
        var (started, gate) = fixture.Store.GateLoad(delayedRead);
        using var cancelled = new CancellationTokenSource();

        var pending = fixture.RunAsync(cancelled.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancelled.Cancel();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(ManagedTransferFinalSubmissionResultKind.ResultPending, result.Kind);
        Assert.NotNull(result.Quarantine);
        Assert.DoesNotContain("permit:dispose", fixture.Events);
        Assert.Equal(0, fixture.Lease.Disposals);
        Assert.Equal(0, fixture.SubmitCalls);
        Assert.Equal(0, fixture.Store.KnownNotDispatchedCalls);

        gate.TrySetResult();
        var reconciled = await result.Quarantine!.ReconcileAsync(1_000);

        Assert.Equal(ManagedTransferFinalSubmissionReconciliationStatus.Released, reconciled);
        Assert.Equal(WebAutomationAttemptState.BaselinePersisted, fixture.Store.Current.State);
        Assert.Equal(1, fixture.Lease.Disposals);
        Assert.Equal(1, fixture.Events.Count(value => value == "permit:dispose"));
        Assert.True(fixture.Events.IndexOf("proof:DestinationPicker") <
            fixture.Events.IndexOf("permit:dispose"));
        Assert.True(fixture.Events.IndexOf("permit:dispose") <
            fixture.Events.IndexOf("lease:dispose"));
        Assert.Equal(0, fixture.Store.KnownNotDispatchedCalls);
        Assert.Equal(0, fixture.SubmitCalls);
        Assert.Equal(1, result.Quarantine.MaximumConcurrentReconciliations);
    }

    [Fact]
    public async Task RejectedSubmissionPersistenceReadFaultCanUseFreshExactProofToRelease()
    {
        var fixture = new Fixture(WebAutomationAttemptState.BaselinePersisted);
        fixture.Store.PersistSubmissionSucceeds = false;
        var (started, gate) = fixture.Store.GateLoad(3);
        using var cancelled = new CancellationTokenSource();

        var pending = fixture.RunAsync(cancelled.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancelled.Cancel();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        gate.TrySetException(new InvalidOperationException("synthetic durable read fault"));

        Assert.Equal(ManagedTransferFinalSubmissionReconciliationStatus.Released,
            await result.Quarantine!.ReconcileAsync(1_000));
        Assert.Equal(1, fixture.Events.Count(value => value == "permit:dispose"));
        Assert.Equal(1, fixture.Lease.Disposals);
        Assert.Equal(0, fixture.Store.KnownNotDispatchedCalls);
        Assert.Equal(0, fixture.SubmitCalls);
    }

    [Theory]
    [InlineData("after-lease-read")]
    [InlineData("readiness")]
    public async Task CooperativeReadOnlyCancellationCanUseFreshExactProofToReleaseLease(
        string boundary)
    {
        var fixture = new Fixture(WebAutomationAttemptState.BaselinePersisted);
        if (boundary == "after-lease-read") fixture.Store.CancelledLoadCalls.Add(2);
        else fixture.CancelReadiness = true;

        var result = await fixture.RunAsync();

        Assert.Equal(ManagedTransferFinalSubmissionResultKind.ResultPending, result.Kind);
        Assert.Equal(ManagedTransferFinalSubmissionReconciliationStatus.Released,
            await result.Quarantine!.ReconcileAsync(1_000));
        Assert.Equal(1, fixture.Lease.Disposals);
        Assert.Equal(0, fixture.Store.MutationCalls);
        Assert.Equal(0, fixture.SubmitCalls);
    }

    [Fact]
    public async Task DurableSubmissionFaultRequiresExactStoreReconcileBeforeRelease()
    {
        var fixture = new Fixture(WebAutomationAttemptState.BaselinePersisted);
        fixture.Store.PersistSubmissionThrows = true;

        var result = await fixture.RunAsync();

        Assert.Equal(ManagedTransferFinalSubmissionResultKind.ResultPending, result.Kind);
        Assert.Equal(0, fixture.Lease.Disposals);
        Assert.Equal(ManagedTransferFinalSubmissionReconciliationStatus.Released,
            await result.Quarantine!.ReconcileAsync(1_000));
        Assert.Equal(WebAutomationAttemptState.BaselinePersisted, fixture.Store.Current.State);
        Assert.Equal(1, fixture.Lease.Disposals);
        Assert.Equal(0, fixture.SubmitCalls);
    }

    [Fact]
    public async Task PossibleDispatchFaultUsesOfficialReconcileAndNeverNegativeCasOrSecondSubmit()
    {
        var fixture = new Fixture(WebAutomationAttemptState.BaselinePersisted)
        {
            SubmitThrows = true,
        };

        var result = await fixture.RunAsync();

        Assert.Equal(ManagedTransferFinalSubmissionResultKind.ResultPending, result.Kind);
        Assert.Equal(ManagedTransferFinalSubmissionReconciliationStatus.Released,
            await result.Quarantine!.ReconcileAsync(1_000));
        Assert.Equal(WebAutomationAttemptState.Succeeded, fixture.Store.Current.State);
        Assert.Equal(0, fixture.Store.KnownNotDispatchedCalls);
        Assert.Equal(1, fixture.SubmitCalls);
        Assert.Equal(1, fixture.Lease.Disposals);
    }

    [Fact]
    public async Task ProvenKnownNotDispatchedUsesOnlyDedicatedCasAfterSettlementAndTwoReads()
    {
        var fixture = new Fixture(WebAutomationAttemptState.BaselinePersisted)
        {
            SubmitDispatchState = ManagedBrowserTransferSubmitDispatchState.KnownNotDispatched,
        };

        var result = await fixture.RunAsync();

        Assert.Equal(ManagedTransferFinalSubmissionResultKind.DefinitelyNotDispatched, result.Kind);
        Assert.Equal(1, fixture.SubmitCalls);
        Assert.Equal(1, fixture.Store.KnownNotDispatchedCalls);
        Assert.True(fixture.Events.IndexOf("process-exit") < fixture.Events.IndexOf("load:no-dispatch-1"));
        Assert.True(fixture.Events.IndexOf("load:no-dispatch-2") < fixture.Events.IndexOf("permit:dispose"));
        Assert.True(fixture.Events.IndexOf("permit:dispose") < fixture.Events.IndexOf("persist:no-dispatch"));
        Assert.Equal("lease:dispose", fixture.Events[^1]);
    }

    [Fact]
    public async Task KnownNotDispatchedPermitDisposalFaultNeverRunsNegativeCasOrReleasesLease()
    {
        var fixture = new Fixture(WebAutomationAttemptState.BaselinePersisted)
        {
            SubmitDispatchState = ManagedBrowserTransferSubmitDispatchState.KnownNotDispatched,
            PermitDisposeThrows = true,
        };

        var result = await fixture.RunAsync();

        Assert.Equal(ManagedTransferFinalSubmissionResultKind.ResultPending, result.Kind);
        Assert.Equal(WebAutomationAttemptState.Submitting, fixture.Store.Current.State);
        Assert.Equal(0, fixture.Store.KnownNotDispatchedCalls);
        Assert.Equal(0, fixture.Lease.Disposals);
        Assert.Contains("permit:dispose", fixture.Events);
        Assert.DoesNotContain("persist:no-dispatch", fixture.Events);
    }

    [Fact]
    public async Task PossibleDispatchPersistsResultPendingAndQuarantineNeverSubmitsAgain()
    {
        var fixture = new Fixture(WebAutomationAttemptState.BaselinePersisted)
        {
            SubmitDispatchState = ManagedBrowserTransferSubmitDispatchState.DispatchOutcomeUnknown,
            EnforceExclusiveLease = true,
        };

        var result = await fixture.RunAsync();
        var reconciled = await result.Quarantine!.ReconcileAsync(1_000);

        Assert.Equal(ManagedTransferFinalSubmissionResultKind.ResultPending, result.Kind);
        Assert.True(reconciled == ManagedTransferFinalSubmissionReconciliationStatus.Released,
            $"Actual={reconciled}; Events={string.Join(',', fixture.Events)}");
        Assert.Equal(1, fixture.SubmitCalls);
        Assert.Equal(WebAutomationAttemptState.Succeeded, fixture.Store.Current.State);
        Assert.Equal(1, fixture.Lease.Disposals);
        Assert.Equal(1, fixture.Events.Count(value => value == "submit"));
        Assert.Equal(1, fixture.LeaseAcquireCalls);
        Assert.Equal(0, fixture.ReacquireAttempts);
        Assert.Contains("official", fixture.Events);
        Assert.Contains("target", fixture.Events);
    }

    [Fact]
    public async Task RouteDriftAfterBoundPersistenceNeverSubmitsAndRetainsSubmittingQuarantine()
    {
        var fixture = new Fixture(WebAutomationAttemptState.BaselinePersisted)
        {
            FailProofRoute = ManagedTransferFinalSubmissionProofRoute.DestinationPicker,
        };

        var result = await fixture.RunAsync();

        Assert.Equal(ManagedTransferFinalSubmissionResultKind.ResultPending, result.Kind);
        Assert.Equal(0, fixture.SubmitCalls);
        Assert.Equal(WebAutomationAttemptState.Submitting, fixture.Store.Current.State);
        Assert.Equal(0, fixture.Store.KnownNotDispatchedCalls);
        Assert.Equal(0, fixture.Lease.Disposals);
    }

    [Fact]
    public async Task CandidateExpiryDuringFinalRouteProofNeverSubmits()
    {
        var fixture = new Fixture(WebAutomationAttemptState.BaselinePersisted)
        {
            FinalRouteProofGate = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };

        var pending = fixture.RunAsync();
        await fixture.FinalRouteProofStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.Clock.Advance(TimeSpan.FromMinutes(6));
        fixture.FinalRouteProofGate.SetResult();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(ManagedTransferFinalSubmissionResultKind.ResultPending, result.Kind);
        Assert.Equal(0, fixture.SubmitCalls);
        Assert.Equal(WebAutomationAttemptState.Submitting, fixture.Store.Current.State);
        Assert.Equal(0, fixture.Lease.Disposals);
    }

    [Fact]
    public async Task PermitRevocationDuringFinalRouteProofNeverSubmits()
    {
        var fixture = new Fixture(WebAutomationAttemptState.BaselinePersisted)
        {
            FinalRouteProofGate = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };

        var pending = fixture.RunAsync();
        await fixture.FinalRouteProofStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.FinalPermitOwned = false;
        fixture.FinalRouteProofGate.SetResult();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(ManagedTransferFinalSubmissionResultKind.ResultPending, result.Kind);
        Assert.Equal(0, fixture.SubmitCalls);
        Assert.Equal(WebAutomationAttemptState.Submitting, fixture.Store.Current.State);
        Assert.Equal(0, fixture.Lease.Disposals);
    }

    [Fact]
    public async Task AttemptRevisionDriftDuringFinalRouteProofNeverSubmits()
    {
        var fixture = new Fixture(WebAutomationAttemptState.BaselinePersisted)
        {
            FinalRouteProofGate = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };

        var pending = fixture.RunAsync();
        await fixture.FinalRouteProofStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.Store.BumpBaselineRevision();
        fixture.FinalRouteProofGate.SetResult();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(ManagedTransferFinalSubmissionResultKind.ResultPending, result.Kind);
        Assert.Equal(0, fixture.SubmitCalls);
        Assert.Equal(WebAutomationAttemptState.Submitting, fixture.Store.Current.State);
        Assert.Equal(0, fixture.Lease.Disposals);
    }

    [Fact]
    public async Task OfficialDestinationMismatchWithSoleAdditionRemainsPending()
    {
        var fixture = new Fixture(WebAutomationAttemptState.BaselinePersisted)
        {
            OfficialDestinationExactId = "different-destination",
        };

        var result = await fixture.RunAsync();

        Assert.Equal(ManagedTransferFinalSubmissionResultKind.ResultPending, result.Kind);
        Assert.Equal(WebAutomationAttemptState.ResultPending, fixture.Store.Current.State);
        Assert.Equal(0, fixture.Store.ConfirmationCalls);
        Assert.Equal(0, fixture.Lease.Disposals);
    }

    [Fact]
    public async Task LostFinalPermitOwnershipAfterPersistenceNeverDispatches()
    {
        var fixture = new Fixture(WebAutomationAttemptState.BaselinePersisted)
        {
            FinalPermitOwned = false,
        };

        var result = await fixture.RunAsync();

        Assert.Equal(ManagedTransferFinalSubmissionResultKind.ResultPending, result.Kind);
        Assert.Equal(0, fixture.SubmitCalls);
        Assert.Equal(WebAutomationAttemptState.Submitting, fixture.Store.Current.State);
        Assert.Equal(0, fixture.Lease.Disposals);
    }

    [Fact]
    public async Task ConcretePermitAttestationRejectsDisposedForeignWrongBindingPickerAndRevokingPermits()
    {
        var exact = await CreateAttestedPermitScenarioAsync(
            BaiduAutomationAction.ClickConfirmTransfer);
        Assert.Equal(ManagedTransferPermitOwnershipKind.Owned,
            (await exact.Scope.ObservePermitOwnershipAsync(
                exact.Request, exact.Permit, default)).Kind);

        var disposed = await CreateAttestedPermitScenarioAsync(
            BaiduAutomationAction.ClickConfirmTransfer);
        await disposed.Permit.DisposeAsync();
        Assert.Equal(ManagedTransferPermitOwnershipKind.Lost,
            (await disposed.Scope.ObservePermitOwnershipAsync(
                disposed.Request, disposed.Permit, default)).Kind);

        var foreign = await CreateAttestedPermitScenarioAsync(
            BaiduAutomationAction.ClickConfirmTransfer);
        Assert.Equal(ManagedTransferPermitOwnershipKind.Lost,
            (await exact.Scope.ObservePermitOwnershipAsync(
                exact.Request, foreign.Permit, default)).Kind);

        var wrongBatch = Guid.NewGuid();
        var wrongBinding = new ManagedTransferFinalSubmissionRequest(
            exact.Request.Identity,
            new BaiduCandidateSessionBinding(
                exact.Request.CandidateBinding.Authorization with { BatchId = wrongBatch },
                exact.Request.CandidateBinding.Binding with { BatchId = wrongBatch },
                exact.Request.CandidateBinding.ExpectedBaiduUserId),
            exact.Request.ExecutionContext, exact.Request.ExactHandle,
            exact.Request.LifecycleSession, exact.Request.ExactManagedSession,
            exact.Request.CanonicalSourceUri, exact.Request.ImmutableSource,
            exact.Request.PreparedDestination, exact.Request.DuplicatePolicy,
            exact.Request.ExpectedAttemptRevision,
            exact.Request.TimeoutMilliseconds, exact.Request.AttemptStore,
            exact.Request.Boundaries);
        Assert.Equal(ManagedTransferPermitOwnershipKind.Lost,
            (await exact.Scope.ObservePermitOwnershipAsync(
                wrongBinding, exact.Permit, default)).Kind);

        var picker = await CreateAttestedPermitScenarioAsync(
            BaiduAutomationAction.OpenDestinationPicker);
        Assert.Equal(ManagedTransferPermitOwnershipKind.Lost,
            (await picker.Scope.ObservePermitOwnershipAsync(
                picker.Request, picker.Permit, default)).Kind);

        var revoking = await CreateAttestedPermitScenarioAsync(
            BaiduAutomationAction.ClickConfirmTransfer);
        var release = revoking.Coordinator
            .ReleaseBindingAsync(revoking.Request.CandidateBinding.Binding).AsTask();
        Assert.False(release.IsCompleted);
        Assert.Equal(ManagedTransferPermitOwnershipKind.Lost,
            (await revoking.Scope.ObservePermitOwnershipAsync(
                revoking.Request, revoking.Permit, default)).Kind);

        await exact.Permit.DisposeAsync();
        await foreign.Permit.DisposeAsync();
        await picker.Permit.DisposeAsync();
        await revoking.Permit.DisposeAsync();
        await release;
    }

    [Fact]
    public async Task SessionWithDisposedRealCoordinatorPermitNeverCallsSubmitOnce()
    {
        var scenario = await CreateAttestedPermitScenarioAsync(
            BaiduAutomationAction.ClickConfirmTransfer);
        await scenario.Permit.DisposeAsync();
        var fixture = scenario.Fixture;
        fixture.Request = scenario.Request;
        fixture.FinalPermitAcquireOverride = _ =>
            ValueTask.FromResult<IBaiduCandidateMutationPermit?>(scenario.Permit);
        fixture.PermitOwnershipOverride = (request, permit, token) =>
            scenario.Scope.ObservePermitOwnershipAsync(request, permit, token);

        var result = await fixture.RunAsync();

        Assert.Equal(ManagedTransferFinalSubmissionResultKind.ResultPending, result.Kind);
        Assert.Equal(0, fixture.SubmitCalls);
        Assert.Equal(WebAutomationAttemptState.Submitting, fixture.Store.Current.State);
    }

    [Fact]
    public async Task CandidateExpiryAfterPersistenceNeverDispatchesOrPublishesRetryableState()
    {
        var fixture = new Fixture(WebAutomationAttemptState.BaselinePersisted);
        fixture.Store.AfterSubmissionPersisted = () => fixture.Clock.Advance(TimeSpan.FromMinutes(10));

        var result = await fixture.RunAsync();

        Assert.Equal(ManagedTransferFinalSubmissionResultKind.ResultPending, result.Kind);
        Assert.Equal(0, fixture.SubmitCalls);
        Assert.Equal(WebAutomationAttemptState.Submitting, fixture.Store.Current.State);
        Assert.Equal(0, fixture.Store.KnownNotDispatchedCalls);
    }

    [Fact]
    public async Task RecoveryFromOfficialResultPendingReadsTargetOnlyAndNeverSubmitsAgain()
    {
        var fixture = new Fixture(WebAutomationAttemptState.BaselinePersisted)
        {
            TargetReadReturnsNull = true,
        };
        var first = await fixture.RunAsync();
        Assert.Equal(ManagedTransferFinalSubmissionResultKind.ResultPending, first.Kind);
        Assert.Equal(WebAutomationAttemptState.ResultPending, fixture.Store.Current.State);
        fixture.TargetReadReturnsNull = false;
        fixture.Events.Clear();

        var recovered = await new ManagedTransferFinalSubmissionSession(fixture.Clock)
            .RecoverAsync(fixture.Request);

        Assert.Equal(ManagedTransferFinalSubmissionResultKind.ExactSuccess, recovered.Kind);
        Assert.Equal("destination-1", recovered.DestinationExactId);
        Assert.Equal(1, fixture.SubmitCalls);
        Assert.DoesNotContain("picker", fixture.Events);
        Assert.DoesNotContain("permit", fixture.Events);
        Assert.DoesNotContain("submit", fixture.Events);
        Assert.Contains("target", fixture.Events);
    }

    [Fact]
    public async Task RecoveryFromEmptyOfficialResultPendingUsesLateAcceptanceOnlyAndNeverMutatesPicker()
    {
        var fixture = new Fixture(WebAutomationAttemptState.BaselinePersisted)
        {
            SubmitDispatchState = ManagedBrowserTransferSubmitDispatchState.DispatchOutcomeUnknown,
        };
        var first = await fixture.RunAsync();
        Assert.Equal(ManagedTransferFinalSubmissionResultKind.ResultPending, first.Kind);
        Assert.Equal(WebAutomationAttemptState.ResultPending, fixture.Store.Current.State);
        Assert.Null(fixture.Store.Current.OfficialRequestId);
        fixture.Events.Clear();

        var recovered = await new ManagedTransferFinalSubmissionSession(fixture.Clock)
            .RecoverAsync(fixture.Request);

        Assert.Equal(ManagedTransferFinalSubmissionResultKind.ExactSuccess, recovered.Kind);
        Assert.Equal("destination-1", recovered.DestinationExactId);
        Assert.Equal(1, fixture.Store.LateAcceptanceCalls);
        Assert.DoesNotContain("picker", fixture.Events);
        Assert.DoesNotContain("permit", fixture.Events);
        Assert.DoesNotContain("submit", fixture.Events);
        Assert.Contains("official", fixture.Events);
        Assert.Contains("persist:late-official", fixture.Events);
    }

    [Fact]
    public async Task RecoveryFromEmptyOfficialResultPendingWithoutBoundOfficialStaysReadOnlyPending()
    {
        var fixture = new Fixture(WebAutomationAttemptState.BaselinePersisted)
        {
            SubmitDispatchState = ManagedBrowserTransferSubmitDispatchState.DispatchOutcomeUnknown,
        };
        _ = await fixture.RunAsync();
        fixture.OfficialKind = ManagedBrowserTransferOutcomeKind.UnboundAcknowledgement;
        fixture.Events.Clear();

        var recovered = await new ManagedTransferFinalSubmissionSession(fixture.Clock)
            .RecoverAsync(fixture.Request);

        Assert.Equal(ManagedTransferFinalSubmissionResultKind.ResultPending, recovered.Kind);
        Assert.Equal(WebAutomationAttemptState.ResultPending, fixture.Store.Current.State);
        Assert.Null(fixture.Store.Current.OfficialRequestId);
        Assert.Equal(0, fixture.Store.LateAcceptanceCalls);
        Assert.DoesNotContain("picker", fixture.Events);
        Assert.DoesNotContain("permit", fixture.Events);
        Assert.DoesNotContain("submit", fixture.Events);
    }

    [Theory]
    [InlineData(ManagedBrowserTransferOutcomeKind.UnboundAcknowledgement)]
    [InlineData(ManagedBrowserTransferOutcomeKind.TimedOut)]
    public async Task HeldLeaseReconcileMovesUnboundSubmittingToEmptyResultPending(
        ManagedBrowserTransferOutcomeKind outcome)
    {
        var fixture = new Fixture(WebAutomationAttemptState.BaselinePersisted)
        {
            OfficialKind = outcome,
        };
        var result = await fixture.RunAsync();
        Assert.Equal(WebAutomationAttemptState.Submitting, fixture.Store.Current.State);

        var reconciled = await result.Quarantine!.ReconcileAsync(1_000);

        Assert.Equal(ManagedTransferFinalSubmissionReconciliationStatus.RetainedInconclusive,
            reconciled);
        Assert.Equal(WebAutomationAttemptState.ResultPending, fixture.Store.Current.State);
        Assert.Null(fixture.Store.Current.OfficialRequestId);
        Assert.Equal(0, fixture.Store.KnownNotDispatchedCalls);
        Assert.Equal(0, fixture.Lease.Disposals);
        Assert.Equal(1, fixture.Events.Count(value => value == "submit"));
    }

    [Fact]
    public async Task CancelAfterSubmitSendRetainsLeaseAndPermitUntilLateTransportSettles()
    {
        var fixture = new Fixture(WebAutomationAttemptState.BaselinePersisted);
        fixture.SubmitGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancelled = new CancellationTokenSource();

        var pending = fixture.RunAsync(cancelled.Token);
        await fixture.SubmitStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancelled.Cancel();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(ManagedTransferFinalSubmissionResultKind.ResultPending, result.Kind);
        Assert.Equal(0, fixture.Lease.Disposals);
        Assert.DoesNotContain("permit:dispose", fixture.Events);

        fixture.SubmitGate.SetResult(new ManagedTransferSubmitResult(
            ManagedBrowserTransferSubmitResultKind.ClickAttempted,
            ManagedBrowserTransferSubmitDispatchState.DispatchOutcomeUnknown));
        Assert.Equal(ManagedTransferFinalSubmissionReconciliationStatus.Released,
            await result.Quarantine!.ReconcileAsync(1_000));
        await fixture.PermitDisposed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, fixture.Lease.Disposals);
        Assert.Equal(1, fixture.SubmitCalls);
    }

    [Fact]
    public async Task CancelDuringPermitAcquisitionDisposesLatePermitAndNeverPersistsOrClicks()
    {
        var fixture = new Fixture(WebAutomationAttemptState.BaselinePersisted);
        fixture.PermitGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancelled = new CancellationTokenSource();

        var pending = fixture.RunAsync(cancelled.Token);
        await fixture.PermitAcquireStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancelled.Cancel();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(ManagedTransferFinalSubmissionResultKind.ResultPending, result.Kind);
        Assert.Equal(0, fixture.Store.MutationCalls);
        Assert.Equal(0, fixture.SubmitCalls);
        Assert.Equal(0, fixture.Lease.Disposals);

        fixture.PermitGate.SetResult(new CountingPermit(fixture.Events, fixture.PermitDisposed));
        await fixture.PermitDisposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ManagedTransferFinalSubmissionReconciliationStatus.Released,
            await result.Quarantine!.ReconcileAsync(1_000));
        Assert.Equal(1, fixture.Lease.Disposals);
    }

    [Fact]
    public async Task RecoveryFromBoundSubmittingObservesOnlyAndNeverSubmitsAgain()
    {
        var fixture = new Fixture(WebAutomationAttemptState.BaselinePersisted)
        {
            FailProofRoute = ManagedTransferFinalSubmissionProofRoute.DestinationPicker,
        };
        var first = await fixture.RunAsync();
        Assert.Equal(ManagedTransferFinalSubmissionResultKind.ResultPending, first.Kind);
        Assert.Equal(WebAutomationAttemptState.Submitting, fixture.Store.Current.State);
        fixture.FailProofRoute = null;
        fixture.Events.Clear();

        var recovered = await new ManagedTransferFinalSubmissionSession(fixture.Clock)
            .RecoverAsync(fixture.Request);

        Assert.Equal(ManagedTransferFinalSubmissionResultKind.ExactSuccess, recovered.Kind);
        Assert.Equal(0, fixture.Events.Count(value => value == "submit"));
        Assert.DoesNotContain("picker", fixture.Events);
        Assert.DoesNotContain("permit", fixture.Events);
        Assert.Contains("official", fixture.Events);
        Assert.Contains("target", fixture.Events);
        Assert.Equal(0, fixture.SubmitCalls);
    }

    [Fact]
    public async Task RecoveryAfterConfirmationPersistedReturnsExactIdentityWithoutSubmit()
    {
        var fixture = new Fixture(WebAutomationAttemptState.BaselinePersisted);
        var first = await fixture.RunAsync();
        Assert.Equal(ManagedTransferFinalSubmissionResultKind.ExactSuccess, first.Kind);
        fixture.Events.Clear();

        var recovered = await new ManagedTransferFinalSubmissionSession(fixture.Clock)
            .RecoverAsync(fixture.Request);

        Assert.Equal(ManagedTransferFinalSubmissionResultKind.ExactSuccess, recovered.Kind);
        Assert.Equal("destination-1", recovered.DestinationExactId);
        Assert.Equal(1, fixture.SubmitCalls);
        Assert.DoesNotContain("submit", fixture.Events);
        Assert.DoesNotContain("picker", fixture.Events);
        Assert.DoesNotContain("permit", fixture.Events);
    }

    [Fact]
    public async Task RecoveryAfterNoDispatchCasNeverAcquiresMutationOrSubmits()
    {
        var fixture = new Fixture(WebAutomationAttemptState.BaselinePersisted)
        {
            SubmitDispatchState = ManagedBrowserTransferSubmitDispatchState.KnownNotDispatched,
        };
        var first = await fixture.RunAsync();
        Assert.Equal(ManagedTransferFinalSubmissionResultKind.DefinitelyNotDispatched, first.Kind);
        fixture.Events.Clear();

        var recovered = await new ManagedTransferFinalSubmissionSession(fixture.Clock)
            .RecoverAsync(fixture.Request);

        Assert.Equal(ManagedTransferFinalSubmissionResultKind.DefinitelyNotDispatched, recovered.Kind);
        Assert.Equal(["load"], fixture.Events);
        Assert.Equal(1, fixture.SubmitCalls);
    }

    [Fact]
    public async Task AmbiguousSubmissionPersistenceRetainsPermitUntilStoreSettlesThenReconcilesTwoReads()
    {
        var fixture = new Fixture(WebAutomationAttemptState.BaselinePersisted);
        fixture.Store.PersistSubmissionGate = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancelled = new CancellationTokenSource();

        var pending = fixture.RunAsync(cancelled.Token);
        await fixture.Store.PersistSubmissionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancelled.Cancel();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(ManagedTransferFinalSubmissionResultKind.ResultPending, result.Kind);
        Assert.DoesNotContain("permit:dispose", fixture.Events);
        Assert.Equal(0, fixture.SubmitCalls);
        Assert.Equal(0, fixture.Lease.Disposals);

        fixture.Store.PersistSubmissionGate.SetResult(false);
        var reconciled = await result.Quarantine!.ReconcileAsync(1_000);
        await fixture.PermitDisposed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(ManagedTransferFinalSubmissionReconciliationStatus.Released, reconciled);
        Assert.Equal(1, fixture.Lease.Disposals);
        Assert.Equal(0, fixture.SubmitCalls);
        Assert.Contains("proof:DestinationPicker", fixture.Events);
    }

    [Fact]
    public async Task LateSuccessfulSubmissionPersistenceUsesDedicatedNoDispatchCasWithoutClick()
    {
        var fixture = new Fixture(WebAutomationAttemptState.BaselinePersisted);
        fixture.Store.PersistSubmissionGate = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancelled = new CancellationTokenSource();

        var pending = fixture.RunAsync(cancelled.Token);
        await fixture.Store.PersistSubmissionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancelled.Cancel();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.Store.PersistSubmissionGate.SetResult(true);

        var reconciled = await result.Quarantine!.ReconcileAsync(1_000);
        await fixture.PermitDisposed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(ManagedTransferFinalSubmissionReconciliationStatus.Released, reconciled);
        Assert.Equal(WebAutomationAttemptState.DefinitelyNotSubmitted, fixture.Store.Current.State);
        Assert.Equal(0, fixture.SubmitCalls);
        Assert.Equal(1, fixture.Store.KnownNotDispatchedCalls);
        Assert.Equal(1, fixture.Lease.Disposals);
        Assert.True(fixture.Events.IndexOf("load:no-dispatch-2") <
            fixture.Events.IndexOf("permit:dispose"));
        Assert.True(fixture.Events.IndexOf("permit:dispose") <
            fixture.Events.IndexOf("persist:no-dispatch"));
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("state")]
    [InlineData("source")]
    [InlineData("target")]
    [InlineData("capability")]
    [InlineData("uid")]
    [InlineData("generation")]
    [InlineData("profile")]
    [InlineData("readiness")]
    public async Task EveryDurableBindingDriftAfterSubmittingPersistPreventsSubmit(
        string drift)
    {
        var fixture = new Fixture(WebAutomationAttemptState.BaselinePersisted);
        fixture.Store.AfterSubmissionPersisted = () => fixture.Store.ApplyPostPersistDrift(drift);

        var result = await fixture.RunAsync();

        Assert.Equal(ManagedTransferFinalSubmissionResultKind.ResultPending, result.Kind);
        Assert.Equal(0, fixture.SubmitCalls);
        Assert.Equal(0, fixture.Store.KnownNotDispatchedCalls);
        Assert.Equal(0, fixture.Lease.Disposals);
    }

    [Theory]
    [InlineData("zero-additions")]
    [InlineData("multiple-additions")]
    [InlineData("missing-baseline")]
    [InlineData("uid")]
    [InlineData("path")]
    [InlineData("generation")]
    [InlineData("browser")]
    [InlineData("target")]
    [InlineData("endpoint")]
    public async Task TargetEvidenceMustExactlyAgreeWithOfficialMappingAndFixedRoute(
        string drift)
    {
        var fixture = new Fixture(WebAutomationAttemptState.BaselinePersisted);
        fixture.DriftTargetRead(drift);

        var result = await fixture.RunAsync();

        Assert.Equal(ManagedTransferFinalSubmissionResultKind.ResultPending, result.Kind);
        Assert.Equal(WebAutomationAttemptState.ResultPending, fixture.Store.Current.State);
        Assert.Equal(0, fixture.Store.ConfirmationCalls);
        Assert.Equal(1, fixture.SubmitCalls);
        Assert.Equal(0, fixture.Lease.Disposals);
    }

    [Fact]
    public async Task TargetConfirmationStoreRaceLeavesOfficialResultPendingWithoutShareHandoff()
    {
        var fixture = new Fixture(WebAutomationAttemptState.BaselinePersisted);
        fixture.Store.PersistConfirmationSucceeds = false;

        var result = await fixture.RunAsync();

        Assert.Equal(ManagedTransferFinalSubmissionResultKind.ResultPending, result.Kind);
        Assert.Equal(WebAutomationAttemptState.ResultPending, fixture.Store.Current.State);
        Assert.Equal(1, fixture.Store.ConfirmationCalls);
        Assert.Equal(1, fixture.SubmitCalls);
        Assert.Null(result.DestinationExactId);
    }

    [Fact]
    public async Task NonCooperativePickerKeepsLeaseUntilItsLateCompletionSettles()
    {
        var fixture = new Fixture(WebAutomationAttemptState.BaselinePersisted);
        fixture.PickerGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancelled = new CancellationTokenSource();

        var pending = fixture.RunAsync(cancelled.Token);
        await fixture.PickerStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancelled.Cancel();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(ManagedTransferFinalSubmissionResultKind.ResultPending, result.Kind);
        Assert.Equal(0, fixture.Store.MutationCalls);
        Assert.Equal(0, fixture.SubmitCalls);
        Assert.Equal(0, fixture.Lease.Disposals);
        var beforeSettle = await result.Quarantine!.ReconcileAsync(1);
        Assert.Equal(ManagedTransferFinalSubmissionReconciliationStatus.RetainedTimedOut,
            beforeSettle);
        Assert.Equal(ManagedTransferFinalSubmissionReconciliationStatus.RetainedTimedOut,
            await result.Quarantine.ReconcileAsync(1));

        fixture.PickerGate.SetResult(ManagedBrowserTransferPickerResultKind.PickerOpened);
        var afterSettle = await result.Quarantine.ReconcileAsync(1_000);
        Assert.Equal(ManagedTransferFinalSubmissionReconciliationStatus.Released,
            afterSettle);
        Assert.Equal(1, fixture.Lease.Disposals);
        Assert.Equal(1, fixture.Events.Count(value => value == "proof:DestinationPicker"));
        Assert.Equal(1, result.Quarantine.MaximumConcurrentReconciliations);
    }

    [Fact]
    public async Task CancelledLeaseAcquisitionDisposesLeaseIfItArrivesLate()
    {
        var fixture = new Fixture(WebAutomationAttemptState.BaselinePersisted);
        fixture.LeaseGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancelled = new CancellationTokenSource();

        var pending = fixture.RunAsync(cancelled.Token);
        await fixture.LeaseAcquireStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancelled.Cancel();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(ManagedTransferFinalSubmissionResultKind.Cancelled, result.Kind);
        Assert.Equal(0, fixture.Lease.Disposals);
        fixture.LeaseGate.SetResult(fixture.Lease);
        await fixture.LeaseDisposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, fixture.Lease.Disposals);
        Assert.Equal(0, fixture.Store.MutationCalls);
        Assert.Equal(0, fixture.SubmitCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoverDisposesLeaseThatArrivesAfterWaitStops(bool cancel)
    {
        var fixture = new Fixture(WebAutomationAttemptState.Submitting,
            timeoutMilliseconds: cancel ? 3_000 : 10);
        fixture.LeaseGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancelled = new CancellationTokenSource();

        var pending = new ManagedTransferFinalSubmissionSession(fixture.Clock)
            .RecoverAsync(fixture.Request, cancelled.Token);
        await fixture.LeaseAcquireStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (cancel) cancelled.Cancel();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(cancel
            ? ManagedTransferFinalSubmissionResultKind.Cancelled
            : ManagedTransferFinalSubmissionResultKind.TimedOut, result.Kind);
        Assert.Equal(0, fixture.Lease.Disposals);
        fixture.LeaseGate.SetResult(fixture.Lease);
        await fixture.LeaseDisposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, fixture.Lease.Disposals);
        Assert.DoesNotContain("picker", fixture.Events);
        Assert.DoesNotContain("permit", fixture.Events);
        Assert.DoesNotContain("submit", fixture.Events);
    }

    [Fact]
    public async Task RecoverLateDurableReadUsesHeldLeaseTrackerAndSingleReconcile()
    {
        var fixture = new Fixture(WebAutomationAttemptState.BaselinePersisted);
        Assert.Equal(ManagedTransferFinalSubmissionResultKind.ExactSuccess,
            (await fixture.RunAsync()).Kind);
        fixture.Events.Clear();
        fixture.Store.LoadCalls = 0;
        var (started, gate) = fixture.Store.GateLoad(2);
        using var cancelled = new CancellationTokenSource();

        var pending = new ManagedTransferFinalSubmissionSession(fixture.Clock)
            .RecoverAsync(fixture.Request, cancelled.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancelled.Cancel();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ManagedTransferFinalSubmissionResultKind.ResultPending, result.Kind);
        Assert.Equal(ManagedTransferFinalSubmissionReconciliationStatus.RetainedTimedOut,
            await result.Quarantine!.ReconcileAsync(1));

        gate.TrySetResult();
        Assert.Equal(ManagedTransferFinalSubmissionReconciliationStatus.Released,
            await result.Quarantine.ReconcileAsync(1_000));
        Assert.Equal(1, result.Quarantine.MaximumConcurrentReconciliations);
        Assert.Equal(2, fixture.Lease.Disposals);
        Assert.DoesNotContain("picker", fixture.Events);
        Assert.DoesNotContain("permit", fixture.Events);
        Assert.DoesNotContain("submit", fixture.Events);
    }

    [Fact]
    public async Task ReadOnlyTargetOutcomeUnknownStopsWithoutConfirmationOrFinalRouteProof()
    {
        var fixture = new Fixture(WebAutomationAttemptState.BaselinePersisted)
        {
            TargetReadReturnsNull = true,
        };

        var result = await fixture.RunAsync();

        Assert.Equal(ManagedTransferFinalSubmissionResultKind.ResultPending, result.Kind);
        Assert.DoesNotContain("permit:dispose", fixture.Events);
        Assert.Equal(0, fixture.Lease.Disposals);
        Assert.Contains("target:root-id", fixture.Events);
        Assert.DoesNotContain("target-enter:target-id", fixture.Events);
        Assert.DoesNotContain("proof:ExactTargetDirectory", fixture.Events);
        Assert.Equal(0, fixture.Store.ConfirmationCalls);
        Assert.Equal(1, fixture.SubmitCalls);
    }

    [Theory]
    [InlineData("submit-fault")]
    [InlineData("official-fault")]
    [InlineData("permit-dispose-fault")]
    public async Task EveryPossibleDispatchFaultRemainsResultPendingAndNeverRetryable(
        string fault)
    {
        var fixture = new Fixture(WebAutomationAttemptState.BaselinePersisted)
        {
            SubmitThrows = fault == "submit-fault",
            OfficialThrows = fault == "official-fault",
            PermitDisposeThrows = fault == "permit-dispose-fault",
        };

        var result = await fixture.RunAsync();

        Assert.Equal(ManagedTransferFinalSubmissionResultKind.ResultPending, result.Kind);
        Assert.Equal(fault == "permit-dispose-fault"
            ? WebAutomationAttemptState.Succeeded
            : WebAutomationAttemptState.Submitting, fixture.Store.Current.State);
        Assert.Equal(1, fixture.SubmitCalls);
        Assert.Equal(0, fixture.Store.KnownNotDispatchedCalls);
        Assert.Equal(0, fixture.Lease.Disposals);
        if (fault == "permit-dispose-fault")
        {
            Assert.Equal(ManagedTransferFinalSubmissionReconciliationStatus.RetainedInconclusive,
                await result.Quarantine!.ReconcileAsync(1_000));
            Assert.Equal(0, fixture.Lease.Disposals);
            Assert.Equal(1, fixture.Events.Count(value => value == "permit:dispose"));
        }
    }

    [Fact]
    public async Task RunAndRecoverEachRequestANewBoundaryScopeFromFactory()
    {
        var fixture = new Fixture(WebAutomationAttemptState.BaselinePersisted);
        var original = fixture.Request;
        var factory = new CountingBoundaryFactory(original.Boundaries);
        fixture.Request = new ManagedTransferFinalSubmissionRequest(
            original.Identity, original.CandidateBinding, original.ExecutionContext,
            original.ExactHandle, original.LifecycleSession, original.ExactManagedSession,
            original.CanonicalSourceUri, original.ImmutableSource, original.PreparedDestination,
            original.DuplicatePolicy, original.ExpectedAttemptRevision, original.TimeoutMilliseconds,
            original.AttemptStore, factory);
        var session = new ManagedTransferFinalSubmissionSession(fixture.Clock);

        Assert.Equal(ManagedTransferFinalSubmissionResultKind.ExactSuccess,
            (await session.RunAsync(fixture.Request)).Kind);
        Assert.Equal(ManagedTransferFinalSubmissionResultKind.ExactSuccess,
            (await session.RecoverAsync(fixture.Request)).Kind);

        Assert.Equal(2, factory.CreateCalls);
    }

    [Fact]
    public async Task NonCooperativeReadinessKeepsLeaseUntilReadSettlesWithoutPermitOrStore()
    {
        var fixture = new Fixture(WebAutomationAttemptState.BaselinePersisted);
        fixture.ReadinessGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancelled = new CancellationTokenSource();

        var pending = fixture.RunAsync(cancelled.Token);
        await fixture.ReadinessStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancelled.Cancel();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(ManagedTransferFinalSubmissionResultKind.ResultPending, result.Kind);
        Assert.Equal(0, fixture.Store.MutationCalls);
        Assert.Equal(0, fixture.SubmitCalls);
        Assert.DoesNotContain("permit", fixture.Events);
        Assert.Equal(ManagedTransferFinalSubmissionReconciliationStatus.RetainedTimedOut,
            await result.Quarantine!.ReconcileAsync(1));

        fixture.ReadinessGate.SetResult(new ManagedTransferReadinessResult(
            ManagedBrowserTransferSubmissionReadinessResultKind.PageNotValidated));
        Assert.Equal(ManagedTransferFinalSubmissionReconciliationStatus.Released,
            await result.Quarantine.ReconcileAsync(1_000));
        Assert.Equal(1, fixture.Lease.Disposals);
        Assert.Equal(1, fixture.Events.Count(value => value == "proof:DestinationPicker"));
        Assert.Equal(1, result.Quarantine.MaximumConcurrentReconciliations);
    }

    [Fact]
    public async Task LateAfterLeaseReloadDrainsThenReconcilesWithoutStartingASecondOperation()
    {
        var fixture = new Fixture(WebAutomationAttemptState.BaselinePersisted);
        var (started, gate) = fixture.Store.GateLoad(2);
        using var cancelled = new CancellationTokenSource();

        var pending = fixture.RunAsync(cancelled.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancelled.Cancel();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ManagedTransferFinalSubmissionResultKind.ResultPending, result.Kind);
        Assert.Equal(ManagedTransferFinalSubmissionReconciliationStatus.RetainedTimedOut,
            await result.Quarantine!.ReconcileAsync(1));

        gate.TrySetResult();
        Assert.Equal(ManagedTransferFinalSubmissionReconciliationStatus.Released,
            await result.Quarantine.ReconcileAsync(1_000));
        Assert.Equal(1, result.Quarantine.MaximumConcurrentReconciliations);
        Assert.Equal(1, fixture.Lease.Disposals);
        Assert.Equal(0, fixture.SubmitCalls);
    }

    [Fact]
    public async Task LateAfterLeaseReloadWithDurableDriftRemainsQuarantined()
    {
        var fixture = new Fixture(WebAutomationAttemptState.BaselinePersisted);
        var (started, gate) = fixture.Store.GateLoad(2);
        using var cancelled = new CancellationTokenSource();

        var pending = fixture.RunAsync(cancelled.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancelled.Cancel();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.Store.BumpBaselineRevision();
        gate.TrySetResult();

        Assert.Equal(ManagedTransferFinalSubmissionReconciliationStatus.RetainedInconclusive,
            await result.Quarantine!.ReconcileAsync(1_000));
        Assert.Equal(0, fixture.Lease.Disposals);
        Assert.Equal(0, fixture.SubmitCalls);
    }

    [Theory]
    [InlineData("bound-read")]
    [InlineData("permit-ownership")]
    [InlineData("negative-read")]
    public async Task LatePreDispatchBoundarySettlesBeforePermitDisposalAndNoDispatchCas(
        string boundary)
    {
        var fixture = new Fixture(WebAutomationAttemptState.BaselinePersisted)
        {
            SubmitDispatchState = ManagedBrowserTransferSubmitDispatchState.KnownNotDispatched,
        };
        Task started;
        Action complete;
        if (boundary == "permit-ownership")
        {
            fixture.PermitOwnershipGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            started = fixture.PermitOwnershipStarted.Task;
            complete = () => fixture.PermitOwnershipGate.SetResult(
                new ManagedTransferPermitOwnershipResult(
                    ManagedTransferPermitOwnershipKind.Owned,
                    fixture.Request.Identity.OperationId,
                    fixture.Request.Identity.AccountId,
                    fixture.Request.Identity.Generation,
                    AdapterWriteAction.SubmitTransfer));
        }
        else
        {
            var load = fixture.Store.GateLoad(boundary == "bound-read" ? 3 : 5);
            started = load.Started.Task;
            complete = () => { load.Gate.TrySetResult(); };
        }
        using var cancelled = new CancellationTokenSource();

        var pending = fixture.RunAsync(cancelled.Token);
        await started.WaitAsync(TimeSpan.FromSeconds(2));
        cancelled.Cancel();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ManagedTransferFinalSubmissionResultKind.ResultPending, result.Kind);
        Assert.DoesNotContain("persist:no-dispatch", fixture.Events);
        Assert.DoesNotContain("permit:dispose", fixture.Events);

        complete();
        Assert.Equal(ManagedTransferFinalSubmissionReconciliationStatus.Released,
            await result.Quarantine!.ReconcileAsync(1_000));
        Assert.Equal(WebAutomationAttemptState.DefinitelyNotSubmitted, fixture.Store.Current.State);
        Assert.Equal(1, fixture.Store.KnownNotDispatchedCalls);
        Assert.True(fixture.Events.IndexOf("permit:dispose") <
            fixture.Events.IndexOf("persist:no-dispatch"));
        Assert.Equal(boundary == "negative-read" ? 1 : 0, fixture.SubmitCalls);
        Assert.Equal(1, fixture.Lease.Disposals);
    }

    [Fact]
    public async Task LateFinalDurableReadDrainsThenUsesTwoFreshReadsAndFinalRouteProof()
    {
        var fixture = new Fixture(WebAutomationAttemptState.BaselinePersisted);
        var (started, gate) = fixture.Store.GateLoad(5);
        using var cancelled = new CancellationTokenSource();

        var pending = fixture.RunAsync(cancelled.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancelled.Cancel();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ManagedTransferFinalSubmissionResultKind.ResultPending, result.Kind);
        Assert.Equal(0, fixture.Lease.Disposals);

        gate.TrySetResult();
        Assert.Equal(ManagedTransferFinalSubmissionReconciliationStatus.Released,
            await result.Quarantine!.ReconcileAsync(1_000));
        Assert.Equal(WebAutomationAttemptState.Succeeded, fixture.Store.Current.State);
        Assert.Equal(1, fixture.Events.Count(value => value == "submit"));
        Assert.Equal(1, fixture.Lease.Disposals);
    }

    [Fact]
    public async Task LateNoDispatchCasSettlesThenUsesTwoReadsAndRouteProofBeforeLeaseRelease()
    {
        var fixture = new Fixture(WebAutomationAttemptState.BaselinePersisted)
        {
            SubmitDispatchState = ManagedBrowserTransferSubmitDispatchState.KnownNotDispatched,
        };
        fixture.Store.KnownNotDispatchedGate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancelled = new CancellationTokenSource();

        var pending = fixture.RunAsync(cancelled.Token);
        await fixture.Store.KnownNotDispatchedStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancelled.Cancel();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ManagedTransferFinalSubmissionResultKind.ResultPending, result.Kind);
        Assert.Equal(0, fixture.Lease.Disposals);

        fixture.Store.KnownNotDispatchedGate.SetResult(true);
        Assert.Equal(ManagedTransferFinalSubmissionReconciliationStatus.Released,
            await result.Quarantine!.ReconcileAsync(1_000));
        Assert.Equal(WebAutomationAttemptState.DefinitelyNotSubmitted, fixture.Store.Current.State);
        Assert.Equal(1, fixture.Store.KnownNotDispatchedCalls);
        Assert.Equal(1, fixture.Events.Count(value => value == "submit"));
        Assert.Equal(1, fixture.Lease.Disposals);
    }

    [Fact]
    public async Task AttemptIsReloadedImmediatelyAfterLeaseBeforeAnyBrowserOrPermitAction()
    {
        var fixture = new Fixture(WebAutomationAttemptState.BaselinePersisted);
        fixture.AfterLeaseAcquired = fixture.Store.BumpBaselineRevision;

        var result = await fixture.RunAsync();

        Assert.Equal(ManagedTransferFinalSubmissionResultKind.NotEligible, result.Kind);
        Assert.Equal(["load", "lease", "load", "lease:dispose"], fixture.Events);
        Assert.Equal(0, fixture.Store.MutationCalls);
        Assert.Equal(0, fixture.SubmitCalls);
    }

    [Theory]
    [InlineData("Cancelled", "Cancelled")]
    [InlineData("TimedOut", "TimedOut")]
    [InlineData("OutcomeUnknown", "ResultPending")]
    public async Task EveryPreBoundaryDirectoryOutcomeDispatchesNoPermitStoreOrSubmit(
        string outcomeName,
        string expectedName)
    {
        var outcome = Enum.Parse<ManagedTransferDirectoryMutationOutcome>(outcomeName);
        var expected = Enum.Parse<ManagedTransferFinalSubmissionResultKind>(expectedName);
        var fixture = new Fixture(WebAutomationAttemptState.BaselinePersisted)
        {
            EnterResult = outcome,
        };

        var result = await fixture.RunAsync();

        Assert.Equal(expected, result.Kind);
        Assert.Equal(0, fixture.Store.MutationCalls);
        Assert.Equal(0, fixture.SubmitCalls);
        Assert.DoesNotContain("permit", fixture.Events);
    }

    [Theory]
    [InlineData("lifecycle")]
    [InlineData("process")]
    [InlineData("endpoint")]
    [InlineData("browser")]
    [InlineData("target")]
    [InlineData("source")]
    public async Task EveryTypedRuntimeDriftAfterPersistPreventsSubmit(string drift)
    {
        var fixture = new Fixture(WebAutomationAttemptState.BaselinePersisted);
        fixture.Store.AfterSubmissionPersisted = () => fixture.RuntimeDrift = drift;

        var result = await fixture.RunAsync();

        Assert.Equal(ManagedTransferFinalSubmissionResultKind.ResultPending, result.Kind);
        Assert.Equal(0, fixture.SubmitCalls);
        Assert.Equal(WebAutomationAttemptState.Submitting, fixture.Store.Current.State);
    }

    [Fact]
    public async Task CandidateIssuedTimeDriftAfterPersistPreventsSubmit()
    {
        var fixture = new Fixture(WebAutomationAttemptState.BaselinePersisted);
        fixture.Store.AfterSubmissionPersisted = () =>
            fixture.Clock.Advance(TimeSpan.FromMinutes(-10));

        var result = await fixture.RunAsync();

        Assert.Equal(ManagedTransferFinalSubmissionResultKind.ResultPending, result.Kind);
        Assert.Equal(0, fixture.SubmitCalls);
    }

    [Theory]
    [InlineData("root")]
    [InlineData("entry")]
    public async Task FixedTargetRouteOutcomeUnknownStopsSequentialReadOnlyTraversal(string stage)
    {
        var fixture = new Fixture(WebAutomationAttemptState.BaselinePersisted)
        {
            TargetRootRouteUnknown = stage == "root",
            TargetEntryRouteUnknown = stage == "entry",
        };

        var result = await fixture.RunAsync();

        Assert.Equal(ManagedTransferFinalSubmissionResultKind.ResultPending, result.Kind);
        Assert.Equal(0, fixture.Store.ConfirmationCalls);
        Assert.DoesNotContain("proof:ExactTargetDirectory", fixture.Events);
        if (stage == "root")
        {
            Assert.DoesNotContain("target:root-id", fixture.Events);
            Assert.DoesNotContain("target-enter:target-id", fixture.Events);
        }
        else
        {
            Assert.Contains("target:root-id", fixture.Events);
            Assert.DoesNotContain("target", fixture.Events);
        }
    }

    [Theory]
    [InlineData("unbound")]
    [InlineData("target-mismatch")]
    public async Task ToastOrWrongOfficialTargetWithExternalAdditionNeverConfirms(string drift)
    {
        var fixture = new Fixture(WebAutomationAttemptState.BaselinePersisted)
        {
            OfficialKind = drift == "unbound"
                ? ManagedBrowserTransferOutcomeKind.UnboundAcknowledgement
                : ManagedBrowserTransferOutcomeKind.AcceptedBound,
            OfficialTargetExactId = drift == "target-mismatch" ? "other-target" : null,
        };

        var result = await fixture.RunAsync();

        Assert.Equal(ManagedTransferFinalSubmissionResultKind.ResultPending, result.Kind);
        Assert.Equal(0, fixture.Store.ConfirmationCalls);
        Assert.Null(result.DestinationExactId);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("incomplete")]
    [InlineData("unstable")]
    public async Task StructurallyInvalidTargetInventoryIsRejectedBeforeConfirmation(
        string rejection)
    {
        var fixture = new Fixture(WebAutomationAttemptState.BaselinePersisted)
        {
            TargetReadKindOverride =
                ManagedBrowserReadTransferTargetDirectoryResultKind.PageNotValidated,
        };

        var sample = fixture.TargetSnapshot.Items[0];
        Assert.Throws<ArgumentException>(() => new ManagedBrowserTargetInventorySnapshot(
            fixture.TargetSnapshot.BrowserId, fixture.TargetSnapshot.TargetId,
            fixture.TargetSnapshot.DevToolsEndpoint, fixture.TargetSnapshot.BaiduUserId,
            fixture.TargetSnapshot.GenerationToken, fixture.TargetSnapshot.DirectoryExactId,
            fixture.TargetSnapshot.CanonicalPath, fixture.TargetSnapshot.AncestorExactIds,
            rejection == "duplicate" ? 2 : 1,
            reachedEnd: rejection != "incomplete", isStable: rejection != "unstable",
            enumerationStepCount: 1,
            rejection == "duplicate" ? [sample, sample] : [sample]));

        var result = await fixture.RunAsync();

        Assert.Equal(ManagedTransferFinalSubmissionResultKind.ResultPending, result.Kind);
        Assert.Equal(0, fixture.Store.ConfirmationCalls);
        Assert.Null(result.DestinationExactId);
    }

    [Theory]
    [InlineData("wire-vs-allitems")]
    [InlineData("wire-vs-durable")]
    [InlineData("noncanonical-allitems")]
    [InlineData("wire-duplicate")]
    [InlineData("allitems-duplicate")]
    [InlineData("projection-fingerprint")]
    [InlineData("wire-fingerprint")]
    [InlineData("allitems-top-name")]
    public async Task EveryTargetProjectionMustCarryTheSameCanonicalExactItems(string drift)
    {
        var fixture = new Fixture(WebAutomationAttemptState.BaselinePersisted);
        fixture.DriftTargetRead(drift);

        var result = await fixture.RunAsync();

        Assert.Equal(ManagedTransferFinalSubmissionResultKind.ResultPending, result.Kind);
        Assert.Equal(WebAutomationAttemptState.ResultPending, fixture.Store.Current.State);
        Assert.Equal(0, fixture.Store.ConfirmationCalls);
        Assert.Null(result.DestinationExactId);
    }

    [Fact]
    public async Task NonCooperativePendingStoreAndProcessMustSettleBeforePermitAndLeaseRelease()
    {
        var fixture = new Fixture(WebAutomationAttemptState.BaselinePersisted)
        {
            SubmitDispatchState = ManagedBrowserTransferSubmitDispatchState.DispatchOutcomeUnknown,
        };
        fixture.Store.AdvanceGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancelled = new CancellationTokenSource();

        var running = fixture.RunAsync(cancelled.Token);
        await fixture.Store.AdvanceStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancelled.Cancel();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, fixture.Lease.Disposals);
        Assert.DoesNotContain("permit:dispose", fixture.Events);

        fixture.Store.AdvanceGate.SetResult(true);
        Assert.Equal(ManagedTransferFinalSubmissionReconciliationStatus.Released,
            await result.Quarantine!.ReconcileAsync(1_000));
        Assert.True(fixture.Events.IndexOf("persist:confirmation") <
            fixture.Events.IndexOf("permit:dispose"));
        Assert.Equal("lease:dispose", fixture.Events[^1]);
    }

    [Theory]
    [InlineData("Submitting", "ExactSuccess", 1)]
    [InlineData("ResultPending", "ExactSuccess", 1)]
    [InlineData("EmptyResultPending", "ExactSuccess", 1)]
    [InlineData("Succeeded", "ExactSuccess", 1)]
    [InlineData("DefinitelyNotSubmitted", "DefinitelyNotDispatched", 0)]
    public async Task RestartRecoveryUsesReopenedSqliteAndNeverPickerPermitOrSubmit(
        string boundaryName, string expectedName, int expectedLeaseAcquisitions)
    {
        var fixture = new Fixture(WebAutomationAttemptState.BaselinePersisted)
        {
            EnforceExclusiveLease = true,
        };
        var root = Path.Combine(Path.GetTempPath(), $"bbt-final-restart-{Guid.NewGuid():N}");
        try
        {
            var request = await CreateReopenedSqliteRequestAsync(fixture, root,
                boundaryName);
            fixture.Clock.Advance(TimeSpan.FromMinutes(1));
            fixture.Events.Clear();
            var durable = await request.AttemptStore.GetAsync(request.Identity.OperationId,
                request.Identity.AccountId, request.Identity.Generation);
            Assert.True(durable is not null &&
                durable.ExecutionProfile.Equals(request.Identity.ExecutionProfile) &&
                durable.SourceEvidence is not null &&
                durable.TargetPreparationEvidence is not null &&
                durable.SubmissionEvidence is not null,
                $"State={durable?.State}; Profile={durable?.ExecutionProfile.Equals(request.Identity.ExecutionProfile)}; Source={durable?.SourceEvidence is not null}; Target={durable?.TargetPreparationEvidence is not null}; Submission={durable?.SubmissionEvidence is not null}");

            var result = await new ManagedTransferFinalSubmissionSession(fixture.Clock)
                .RecoverAsync(request);

            Assert.True(result.Kind ==
                Enum.Parse<ManagedTransferFinalSubmissionResultKind>(expectedName),
                $"Actual={result.Kind}; Events={string.Join(',', fixture.Events)}");
            Assert.Equal(0, fixture.SubmitCalls);
            Assert.DoesNotContain("picker", fixture.Events);
            Assert.DoesNotContain("permit", fixture.Events);
            Assert.DoesNotContain("submit", fixture.Events);
            Assert.Equal(expectedLeaseAcquisitions, fixture.LeaseAcquireCalls);
            Assert.Equal(0, fixture.ReacquireAttempts);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("authorization-operation")]
    [InlineData("authorization-account")]
    [InlineData("authorization-generation")]
    [InlineData("authorization-batch")]
    [InlineData("authorization-action")]
    [InlineData("binding-operation")]
    [InlineData("binding-account")]
    [InlineData("binding-generation")]
    [InlineData("binding-batch")]
    [InlineData("binding-capability")]
    [InlineData("issued-future")]
    [InlineData("expired")]
    [InlineData("recovery")]
    [InlineData("uid")]
    public async Task EveryCandidateBindingMismatchStopsBeforeLeasePickerPermitStoreAndSubmit(
        string drift)
    {
        var fixture = new Fixture(WebAutomationAttemptState.BaselinePersisted);
        fixture.DriftCandidate(drift);

        var result = await fixture.RunAsync();

        Assert.Equal(ManagedTransferFinalSubmissionResultKind.NotEligible, result.Kind);
        Assert.Equal(["load"], fixture.Events);
        Assert.Equal(0, fixture.LeaseAcquireCalls);
        Assert.Equal(0, fixture.Store.MutationCalls);
        Assert.Equal(0, fixture.SubmitCalls);
    }

    private static async Task<ManagedTransferFinalSubmissionRequest>
        CreateReopenedSqliteRequestAsync(
            Fixture fixture, string root, string boundaryName)
    {
        var emptyOfficialPending = boundaryName == "EmptyResultPending";
        var boundary = emptyOfficialPending
            ? WebAutomationAttemptState.ResultPending
            : Enum.Parse<WebAutomationAttemptState>(boundaryName);
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "attempts.db");
        var factory = new SqliteConnectionFactory(databasePath);
        await new DatabaseMigrator(factory,
            new DatabaseBackupService(factory, Path.Combine(root, "backups")), "test")
            .MigrateAsync();
        await SeedRestartOwnerAsync(factory, fixture.Request);
        var store = new SqliteWebAutomationAttemptStore(factory);
        var request = fixture.Request;
        var identity = request.Identity;
        Assert.True(await store.BeginAsync(WebAutomationAttemptRecord.Create(
            identity.OperationId, identity.AccountId, identity.Generation,
            WebAutomationOwnerKind.Transfer, fixture.Clock.GetUtcNow(),
            identity.ExecutionProfile)));
        var initial = (await store.GetAsync(identity.OperationId, identity.AccountId,
            identity.Generation))!;
        var source = fixture.Store.Current.SourceEvidence!;
        var target = fixture.Store.Current.TargetPreparationEvidence!;
        Assert.True(await store.TryPersistTransferSourceEvidenceAsync(
            identity.OperationId, identity.AccountId, identity.Generation, initial.Revision,
            source, fixture.Clock.GetUtcNow().AddSeconds(1)));
        var withSource = (await store.GetAsync(identity.OperationId, identity.AccountId,
            identity.Generation))!;
        Assert.True(await store.TryPersistTransferTargetPreparationAsync(
            identity.OperationId, identity.AccountId, identity.Generation, withSource.Revision,
            target, fixture.Clock.GetUtcNow().AddSeconds(2)));
        var prepared = (await store.GetAsync(identity.OperationId, identity.AccountId,
            identity.Generation))!;
        var sourceItem = request.ImmutableSource.Items.Single();
        var context = new ManagedBrowserTransferSubmissionContext(
            request.ExactHandle.BrowserId, request.ExactHandle.TargetId,
            request.ExactHandle.Endpoint, request.TimeoutMilliseconds - 1,
            request.CanonicalSourceUri, request.ExactManagedSession.StableBaiduUserId,
            request.ImmutableSource.GenerationToken,
            request.ImmutableSource.ObservationFingerprint,
            request.PreparedDestination.SourceFingerprint,
            sourceItem.ExactId, sourceItem.Kind,
            request.PreparedDestination.Destination.RemoteId,
            request.PreparedDestination.Destination.Path.Value,
            target.Baseline.Fingerprint, request.DuplicatePolicy);
        var readiness = new ManagedBrowserTransferSubmissionReadiness(context, 1);
        var submission = TransferSubmissionEvidence.CreateBound(
            Guid.NewGuid(), readiness.ReadinessFingerprint, source.Items.Fingerprint,
            target.TargetExactId, target.Baseline.Fingerprint, source.BaiduUserId,
            source.Capability, fixture.Clock.GetUtcNow().AddSeconds(3));
        Assert.True(await store.TryPersistTransferSubmissionAsync(
            identity.OperationId, identity.AccountId, identity.Generation, prepared.Revision,
            submission, fixture.Clock.GetUtcNow().AddSeconds(3)));
        var current = (await store.GetAsync(identity.OperationId, identity.AccountId,
            identity.Generation))!;
        if (boundary == WebAutomationAttemptState.DefinitelyNotSubmitted)
        {
            Assert.True(await store.TryResolveTransferKnownNotDispatchedAsync(
                identity.OperationId, identity.AccountId, identity.Generation, current.Revision,
                submission.SubmissionId!.Value, submission.ReadinessFingerprint!,
                fixture.Clock.GetUtcNow().AddSeconds(4)));
        }
        else if (emptyOfficialPending)
        {
            Assert.True(await store.TryAdvanceAsync(
                identity.OperationId, identity.AccountId, identity.Generation,
                WebAutomationAttemptState.Submitting, current.Revision,
                WebAutomationAttemptState.ResultPending, observationFingerprint: null,
                isWaitingForUser: false, fixture.Clock.GetUtcNow().AddSeconds(4)));
        }
        else if (boundary is WebAutomationAttemptState.ResultPending or
                 WebAutomationAttemptState.Succeeded)
        {
            var mapping = new TransferIdentityMapping([new("source-id", "destination-1")]);
            Assert.True(await store.TryPersistTransferAcceptanceAsync(
                identity.OperationId, identity.AccountId, identity.Generation, current.Revision,
                "official-1", mapping, new string('a', 64),
                fixture.Clock.GetUtcNow().AddSeconds(4)));
            current = (await store.GetAsync(identity.OperationId, identity.AccountId,
                identity.Generation))!;
            if (boundary == WebAutomationAttemptState.Succeeded)
            {
                var targetEvidence = ManagedTransferTargetInventoryReader.Project(
                    fixture.TargetSnapshot);
                var confirmation = TransferTargetConfirmationEvidence.Create(
                    submission, "official-1", mapping, target,
                    targetEvidence.DurableEvidence.Baseline,
                    targetEvidence.WireSnapshot.ObservationFingerprint,
                    fixture.Clock.GetUtcNow().AddSeconds(5));
                Assert.True(await store.TryPersistTransferTargetConfirmationAsync(
                    identity.OperationId, identity.AccountId, identity.Generation,
                    current.Revision, confirmation));
            }
        }

        var reopenedFactory = new SqliteConnectionFactory(databasePath);
        var reopenedStore = new SqliteWebAutomationAttemptStore(reopenedFactory);
        return new ManagedTransferFinalSubmissionRequest(
            request.Identity, request.CandidateBinding, request.ExecutionContext,
            request.ExactHandle, request.LifecycleSession, request.ExactManagedSession,
            request.CanonicalSourceUri, request.ImmutableSource,
            request.PreparedDestination, request.DuplicatePolicy, request.ExpectedAttemptRevision,
            request.TimeoutMilliseconds, reopenedStore, request.Boundaries);
    }

    private static async Task SeedRestartOwnerAsync(
        SqliteConnectionFactory factory, ManagedTransferFinalSubmissionRequest request)
    {
        var identity = request.Identity;
        var batch = identity.BatchId;
        var resource = Guid.NewGuid();
        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Accounts(Id,BaiduUserId,DisplayName,LoginMethod,Status,DailyLimit,
              DailyExecutedCount,SortOrder,IsEnabled,DailyCountDateKey,IsDeleted)
            VALUES($account,$uid,'owner','WebView2','Active',0,0,0,1,'2026-08-30',0);
            INSERT INTO BrowserInstallations(InstallationId,BrowserFamily,InstallationDirectory,
              ExecutablePath,Publisher,ProductName,FileIdentity,State,Revision,CreatedAt,UpdatedAt)
            VALUES($installation,$family,$directory,$executable,'Google LLC','Google Chrome',
              'restart-file','Active',1,$now,$now);
            INSERT INTO ManagedBrowserSessions(SessionId,AccountId,BaiduUserId,InstallationId,
              BrowserFamily,ProfileId,ProfileOwnershipId,State,Revision,CreatedAt,UpdatedAt)
            VALUES($session,$account,$uid,$installation,$family,$profile,$ownership,
              'Active',1,$now,$now);
            INSERT INTO BatchJobs(Id,Name,SourceType,TransferMode,QuotaBasis,ShortagePolicy,
              ResourceCount,OperationCount,DefaultTargetPath,FolderCreationMode,DuplicatePolicy,
              ValidationMode,Status,CreatedAt,QueueOrder)
            VALUES($batch,'restart','Text','Distribution','TaskCount','ProportionalReduction',
              1,1,'/','None','Skip','Immediate','Queued',$now,1);
            INSERT INTO ResourceItems(Id,BatchJobId,CandidateSequence,ShareUrl,NormalizedShareId,
              ExtractionCode,FolderCreationModeOverride,GlobalValidityStatus,CandidateStatus,
              CreatedAt,UpdatedAt)
            VALUES($resource,$batch,1,'https://pan.baidu.com/s/restart','restart','1111','None',
              'Valid','Expanded',$now,$now);
            INSERT INTO TransferOperations(Id,ResourceItemId,BatchJobId,SequenceNumber,
              AllocationOrdinal,TargetPath,NormalizedTargetPath,CreateFolderName,
              AssignedAccountId,AllocationSource,Status,AttemptCount,OperationFingerprint,
              SubmitState,TargetSlotState,CreatedAt,UpdatedAt,ExecutionEngineVersion,
              BrowserFamily,ProfileId,InstallationId,SessionId)
            VALUES($operation,$resource,$batch,1,1,'/','/','',$account,'Automatic','Waiting',
              $generation,'restart-fp','NotPrepared','Unoccupied',$now,$now,'ManagedChromium-v1',
              $family,$profile,$installation,$session);
            """;
        command.Parameters.AddWithValue("$account", identity.AccountId.ToString("D"));
        command.Parameters.AddWithValue("$uid", request.ExactManagedSession.StableBaiduUserId);
        command.Parameters.AddWithValue("$installation",
            request.ExactManagedSession.InstallationId.ToString("D"));
        command.Parameters.AddWithValue("$family",
            request.ExactManagedSession.BrowserFamily.ToString());
        command.Parameters.AddWithValue("$directory",
            request.ExactHandle.Installation.InstallationDirectory);
        command.Parameters.AddWithValue("$executable",
            request.ExactHandle.Installation.ExecutablePath);
        command.Parameters.AddWithValue("$session",
            request.ExactManagedSession.SessionId.ToString("D"));
        command.Parameters.AddWithValue("$profile", request.ExactManagedSession.ProfileId);
        command.Parameters.AddWithValue("$ownership",
            request.ExactManagedSession.ProfileOwnershipId);
        command.Parameters.AddWithValue("$batch", batch.ToString("D"));
        command.Parameters.AddWithValue("$resource", resource.ToString("D"));
        command.Parameters.AddWithValue("$operation", identity.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$generation", identity.Generation);
        command.Parameters.AddWithValue("$now",
            new DateTimeOffset(2026, 8, 30, 8, 0, 0, TimeSpan.Zero).ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<AttestedPermitScenario> CreateAttestedPermitScenarioAsync(
        BaiduAutomationAction action)
    {
        var fixture = new Fixture(WebAutomationAttemptState.BaselinePersisted);
        var current = fixture.Request;
        var coordinator = new AcceptanceWriteLeaseCoordinator(
            new AcceptanceWriteLeaseOptions(true, ["--webview-acceptance"]),
            new EmptyCapabilityStore(), fixture.Clock);
        var ensure = current.CandidateBinding.Authorization with
        {
            Action = AdapterWriteAction.EnsureDirectory,
        };
        Assert.True(coordinator.TryCreateLease(
            ensure, new AcceptanceWriteLeaseConfirmation(true, true, true)));
        var binding = Assert.IsType<AdapterWriteAuthorizationBinding>(
            await coordinator.TryCreateBindingAsync(ensure));
        Assert.True(await coordinator.TryValidateBindingAsync(
            ensure with { Action = AdapterWriteAction.PrepareTransfer }, binding));
        var submit = ensure with { Action = AdapterWriteAction.SubmitTransfer };
        Assert.True(await coordinator.TryValidateBindingAsync(submit, binding));
        var candidate = new BaiduCandidateSessionBinding(
            submit, binding, current.ExactManagedSession.StableBaiduUserId);
        var request = new ManagedTransferFinalSubmissionRequest(
            current.Identity, candidate, current.ExecutionContext,
            current.ExactHandle, current.LifecycleSession, current.ExactManagedSession,
            current.CanonicalSourceUri, current.ImmutableSource,
            current.PreparedDestination, current.DuplicatePolicy, current.ExpectedAttemptRevision,
            current.TimeoutMilliseconds, current.AttemptStore, current.Boundaries);
        var factory = CreateConcreteFactory(coordinator);
        var scope = factory.CreateScope(request);
        var read = new BaiduCandidateDomReadRequest(
            new Uri(request.CanonicalSourceUri), BaiduCandidatePageState.ExternalShare,
            request.ExactManagedSession.StableBaiduUserId,
            request.ImmutableSource.Items.Select(item => item.ExactId), null, null);
        var permit = Assert.IsAssignableFrom<IBaiduCandidateMutationPermit>(
            await coordinator.TryAcquireTransferMutationPermitAsync(
                new BaiduCandidateDomMutationRequest(
                    submit, binding, action, read, null, null)));
        return new(fixture, request, coordinator, scope, permit);
    }

    private static ManagedTransferFinalSubmissionConcreteBoundaries CreateConcreteFactory(
        IBaiduCandidateActionAuthorizationSource authorization)
    {
        static Task<ManagedBrowserResponse> NoSend(
            ManagedBrowserRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No helper operation is expected.");
        return new ManagedTransferFinalSubmissionConcreteBoundaries(
            (ManagedBrowserLifecycleCoordinator)RuntimeHelpers.GetUninitializedObject(
                typeof(ManagedBrowserLifecycleCoordinator)),
            new AlwaysExactRouteProof(),
            new ManagedTransferPickerOpener(NoSend, authorization),
            new ManagedTransferDirectoryReader(NoSend),
            new ManagedTransferDirectoryMutator(NoSend, authorization,
                static (_, _) => Task.FromResult(true)),
            new ManagedTransferSubmissionOperator(NoSend),
            new ManagedTransferTargetInventoryReader(NoSend),
            (ManagedBrowserHelperClient)RuntimeHelpers.GetUninitializedObject(
                typeof(ManagedBrowserHelperClient)),
            authorization,
            new ExactProcessExitObserver());
    }

    private sealed record AttestedPermitScenario(
        Fixture Fixture,
        ManagedTransferFinalSubmissionRequest Request,
        AcceptanceWriteLeaseCoordinator Coordinator,
        IManagedTransferFinalSubmissionBoundaries Scope,
        IBaiduCandidateMutationPermit Permit);

    private sealed class EmptyCapabilityStore : IAdapterCapabilityStore
    {
        public Task AcceptAsync(AdapterCapabilityAcceptanceRecord acceptance,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AdapterCapabilityAcceptanceRecord?> GetAsync(
            string adapterKind, string protocolVersion, string selectorVersion,
            string adapterBuildVersion, CancellationToken cancellationToken = default) =>
            Task.FromResult<AdapterCapabilityAcceptanceRecord?>(null);
    }

    private sealed class Fixture
    {
        private static readonly DateTimeOffset Now = new(2026, 8, 30, 8, 0, 0, TimeSpan.Zero);
        private const string SourceUri = "https://pan.baidu.com/s/synthetic-source";
        internal readonly List<string> Events = [];
        internal readonly CountingLease Lease;
        internal readonly MemoryAttemptStore Store;
        internal ManagedTransferFinalSubmissionRequest Request;
        internal readonly MutableTimeProvider Clock = new(Now);
        internal int LeaseHeld;
        internal int LeaseAcquireCalls;
        internal int ReacquireAttempts;
        internal bool EnforceExclusiveLease;
        internal int SubmitCalls;
        internal readonly TaskCompletionSource SubmitStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource PermitDisposed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource PermitAcquireStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource PickerStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource LeaseAcquireStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource LeaseDisposed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReadinessStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource PermitOwnershipStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource FinalRouteProofStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<ManagedTransferSubmitResult>? SubmitGate;
        internal TaskCompletionSource<IBaiduCandidateMutationPermit?>? PermitGate;
        internal TaskCompletionSource<ManagedBrowserTransferPickerResultKind>? PickerGate;
        internal TaskCompletionSource<IDisposable>? LeaseGate;
        internal TaskCompletionSource<ManagedTransferReadinessResult>? ReadinessGate;
        internal TaskCompletionSource<ManagedTransferPermitOwnershipResult>? PermitOwnershipGate;
        internal TaskCompletionSource? FinalRouteProofGate;
        internal Func<ManagedTransferFinalSubmissionRequest,
            ValueTask<IBaiduCandidateMutationPermit?>>? FinalPermitAcquireOverride;
        internal Func<ManagedTransferFinalSubmissionRequest, IBaiduCandidateMutationPermit,
            CancellationToken, Task<ManagedTransferPermitOwnershipResult>>? PermitOwnershipOverride;
        internal ManagedBrowserTransferPickerResultKind PickerResult =
            ManagedBrowserTransferPickerResultKind.PickerOpened;
        internal ManagedTransferDirectoryMutationOutcome EnterResult =
            ManagedTransferDirectoryMutationOutcome.DirectoryEntered;
        internal ManagedBrowserTransferSubmitDispatchState SubmitDispatchState =
            ManagedBrowserTransferSubmitDispatchState.ClickDispatched;
        internal ManagedTransferFinalSubmissionProofRoute? FailProofRoute;
        internal string OfficialDestinationExactId = "destination-1";
        internal bool FinalPermitOwned = true;
        internal bool TargetReadReturnsNull;
        internal ManagedBrowserReadTransferTargetDirectoryResultKind? TargetReadKindOverride;
        internal bool SubmitThrows;
        internal bool CancelReadiness;
        internal bool OfficialThrows;
        internal bool PermitDisposeThrows;
        internal string? RuntimeDrift;
        internal bool TargetRootRouteUnknown;
        internal bool TargetEntryRouteUnknown;
        internal ManagedBrowserTransferOutcomeKind OfficialKind =
            ManagedBrowserTransferOutcomeKind.AcceptedBound;
        internal string? OfficialTargetExactId;
        internal Action? AfterLeaseAcquired;
        internal ManagedBrowserTargetInventorySnapshot TargetSnapshot;
        internal ManagedTransferTargetInventoryEvidence? TargetEvidenceOverride;
        internal ManagedBrowserTransferSubmissionContext? ObservedReadinessContext;
        internal ManagedBrowserTransferSubmissionReadiness? ObservedSubmitReadiness;
        internal DuplicatePolicy? ReadinessPolicyOverride;

        internal Fixture(WebAutomationAttemptState state,
            ManagedBrowserSourceItemKind sourceKind = ManagedBrowserSourceItemKind.File,
            DuplicatePolicy duplicatePolicy = DuplicatePolicy.Skip,
            int timeoutMilliseconds = 3_000)
        {
            Lease = new(Events, LeaseDisposed, () => Volatile.Write(ref LeaseHeld, 0));
            var capability = new AdapterCapabilityVersion(
                "BaiduTransfer", "protocol-v3", "selectors-v3", "synthetic-build");
            var installation = new BrowserInstallationIdentity(Guid.NewGuid(), BrowserFamily.Chrome,
                @"C:\SyntheticBrowser", @"C:\SyntheticBrowser\chrome.exe",
                "Google LLC", "Google Chrome", "synthetic-file");
            var managed = new ManagedBrowserSessionReference(Guid.NewGuid(), installation.InstallationId,
                Guid.NewGuid(), "synthetic-user", BrowserFamily.Chrome,
                Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
            var identity = new WebAutomationRequestIdentity(Guid.NewGuid(), Guid.NewGuid(),
                managed.AccountId, 7, WebAutomationOwnerKind.Transfer,
                ExecutionProfileSnapshot.Managed(managed.BrowserFamily, managed.ProfileId,
                    managed.InstallationId, managed.SessionId));
            var profile = ManagedBrowserProfileDescriptor.FromStoredSession(managed, @"C:\SyntheticAppData");
            var port = new DevToolsActivePort(9222, Guid.NewGuid(),
                new Uri("http://127.0.0.1:9222/"),
                new Uri("ws://127.0.0.1:9222/devtools/browser/synthetic"));
            var handle = new ManagedChromiumSessionHandle(31415, installation, profile, port, "fixed-tab");
            var lifecycle = new ManagedBrowserLifecycleSession(managed, 11, 13, profile.CanonicalSourcePath);
            var rawSource = new ManagedBrowserSourceSnapshot(managed.StableBaiduUserId,
                "source-generation", 1, true, true,
                [new ManagedBrowserSourceItem("source-id", sourceKind,
                    sourceKind == ManagedBrowserSourceItemKind.File ? "app.apk" : "folder")],
                new string('0', 64));
            var sourceSnapshot = new ManagedBrowserSourceSnapshot(rawSource.BaiduUserId,
                rawSource.GenerationToken, rawSource.TotalCount, true, true, rawSource.Items,
                rawSource.ComputeFingerprint(SourceUri));
            var source = new TransferSourceEvidence(
                new RemoteItemIdentitySet([new RemoteItemIdentity("source-id", null)]),
                1, true, true, sourceSnapshot.GenerationToken, sourceSnapshot.BaiduUserId,
                capability, sourceSnapshot.ObservationFingerprint);
            const long preparingRevision = 4;
            var preparing = new WebAutomationAttemptRecord(identity.OperationId, identity.AccountId,
                identity.Generation, WebAutomationOwnerKind.Transfer, WebAutomationAttemptState.Preparing,
                null, null, source.ObservationFingerprint, false, preparingRevision,
                Now.AddMinutes(-2), Now.AddMinutes(-1), sourceEvidence: source,
                executionProfile: identity.ExecutionProfile);
            var destination = new RemoteDirectory("target-id", new RemotePath("/target"));
            var rawDirectory = new ManagedBrowserDirectorySnapshot(managed.StableBaiduUserId,
                "target-generation", destination.RemoteId, destination.Path.Value,
                ["root-id", destination.RemoteId], 0, true, true, [], new string('0', 64));
            var directory = new ManagedBrowserDirectorySnapshot(rawDirectory.BaiduUserId,
                rawDirectory.GenerationToken, rawDirectory.ParentExactId, rawDirectory.CanonicalPath,
                rawDirectory.AncestorExactIds, rawDirectory.TotalCount, true, true,
                rawDirectory.Items, rawDirectory.ComputeFingerprint(SourceUri));
            var prepared = ManagedPreparedDestinationEvidence.CreateFromValidatedState(identity,
                BaiduExecutionContext.Managed(managed),
                new TransferDirectoryPreparationRequest(identity.OperationId, identity.BatchId,
                    identity.AccountId, identity.Generation,
                    new ShareReference("synthetic", SourceUri), destination.Path, capability),
                preparing, source, preparingRevision,
                TransferDirectoryPreparationOutcome.Prepared(destination),
                ManagedTransferDirectoryEvidence.CreateFromReaderValidated(directory));
            var baseline = WebAutomationBaseline.Create(["existing-1"]);
            var targetFingerprint = TransferTargetPreparationEvidence.ComputeObservationFingerprint(
                destination.RemoteId, destination.Path, 1, true, true, "target-generation",
                managed.StableBaiduUserId, TransferTargetInventoryScope.AllItems, baseline);
            var target = new TransferTargetPreparationEvidence(destination.RemoteId, destination.Path,
                1, true, true, "target-generation", managed.StableBaiduUserId,
                TransferTargetInventoryScope.AllItems, baseline, targetFingerprint);
            var recoveryContext = new ManagedBrowserTransferSubmissionContext(
                handle.BrowserId, handle.TargetId, handle.Endpoint,
                timeoutMilliseconds == 1 ? 1 : timeoutMilliseconds - 1, SourceUri,
                managed.StableBaiduUserId, sourceSnapshot.GenerationToken,
                sourceSnapshot.ObservationFingerprint, prepared.SourceFingerprint,
                sourceSnapshot.Items.Single().ExactId, sourceSnapshot.Items.Single().Kind,
                destination.RemoteId, destination.Path.Value, target.Baseline.Fingerprint,
                duplicatePolicy);
            var recoveryReadinessFingerprint =
                new ManagedBrowserTransferSubmissionReadiness(recoveryContext, 1)
                    .ReadinessFingerprint;
            var attempt = BuildAttempt(state, identity, source, target, Now,
                recoveryReadinessFingerprint);
            Store = new MemoryAttemptStore(attempt, Events);
            var authorization = new AdapterWriteAuthorizationRequest(capability, identity.AccountId,
                identity.OperationId, identity.Generation, identity.BatchId,
                AdapterWriteAction.SubmitTransfer);
            var binding = new AdapterWriteAuthorizationBinding(
                AdapterWriteAuthorizationSource.ConsumedLease, capability, identity.AccountId,
                identity.OperationId, identity.Generation, identity.BatchId,
                Now.AddMinutes(-1), Now.AddMinutes(5), false, null);
            var candidate = new BaiduCandidateSessionBinding(
                authorization, binding, managed.StableBaiduUserId);
            TargetSnapshot = new ManagedBrowserTargetInventorySnapshot(
                handle.BrowserId, handle.TargetId, handle.Endpoint, managed.StableBaiduUserId,
                "target-generation-2", destination.RemoteId, destination.Path.Value,
                ["root-id", destination.RemoteId], 2, true, true, 2,
                [
                    new ManagedBrowserTargetInventoryItem("existing-1",
                        ManagedBrowserTargetInventoryItemKind.File, "existing.txt"),
                    new ManagedBrowserTargetInventoryItem("destination-1",
                        sourceKind == ManagedBrowserSourceItemKind.File
                            ? ManagedBrowserTargetInventoryItemKind.File
                            : ManagedBrowserTargetInventoryItemKind.Folder,
                        sourceKind == ManagedBrowserSourceItemKind.File ? "app.apk" : "folder"),
                ]);

            var boundaries = new FakeBoundaries(this, identity, handle, lifecycle, sourceSnapshot,
                destination.RemoteId);
            Request = new ManagedTransferFinalSubmissionRequest(identity, candidate,
                BaiduExecutionContext.Managed(managed), handle, lifecycle, managed, SourceUri,
                sourceSnapshot, prepared, duplicatePolicy,
                expectedAttemptRevision: 5, timeoutMilliseconds,
                Store, boundaries);
        }

        internal Task<ManagedTransferFinalSubmissionResult> RunAsync(
            CancellationToken cancellationToken = default) =>
            new ManagedTransferFinalSubmissionSession(Clock).RunAsync(Request, cancellationToken);

        internal void SetTimeout(int timeoutMilliseconds) =>
            Request = new ManagedTransferFinalSubmissionRequest(
                Request.Identity, Request.CandidateBinding, Request.ExecutionContext,
                Request.ExactHandle, Request.LifecycleSession, Request.ExactManagedSession,
                Request.CanonicalSourceUri, Request.ImmutableSource, Request.PreparedDestination,
                Request.DuplicatePolicy, Request.ExpectedAttemptRevision, timeoutMilliseconds,
                Request.AttemptStore, Request.Boundaries);

        internal void DriftCandidate(string drift)
        {
            var current = Request.CandidateBinding;
            var authorization = current.Authorization;
            var binding = current.Binding;
            var capability = new AdapterCapabilityVersion(
                "BaiduTransfer", "drift-protocol", "drift-selectors", "drift-build");
            authorization = drift switch
            {
                "authorization-operation" => authorization with { OperationId = Guid.NewGuid() },
                "authorization-account" => authorization with { AccountId = Guid.NewGuid() },
                "authorization-generation" => authorization with { Generation = authorization.Generation + 1 },
                "authorization-batch" => authorization with { BatchId = Guid.NewGuid() },
                "authorization-action" => authorization with { Action = AdapterWriteAction.PrepareTransfer },
                "recovery" => authorization with { IsRecovery = true },
                _ => authorization,
            };
            binding = drift switch
            {
                "binding-operation" => binding with { OperationId = Guid.NewGuid() },
                "binding-account" => binding with { AccountId = Guid.NewGuid() },
                "binding-generation" => binding with { Generation = binding.Generation + 1 },
                "binding-batch" => binding with { BatchId = Guid.NewGuid() },
                "binding-capability" => binding with { Capability = capability },
                "issued-future" => binding with { IssuedAtUtc = Clock.GetUtcNow().AddMinutes(1) },
                "expired" => binding with { ExpiresAtUtc = Clock.GetUtcNow() },
                "recovery" => binding with { IsRecovery = true },
                _ => binding,
            };
            var candidate = new BaiduCandidateSessionBinding(
                authorization, binding,
                drift == "uid" ? "different-user" : current.ExpectedBaiduUserId);
            Request = new ManagedTransferFinalSubmissionRequest(
                Request.Identity, candidate, Request.ExecutionContext, Request.ExactHandle,
                Request.LifecycleSession, Request.ExactManagedSession,
                Request.CanonicalSourceUri, Request.ImmutableSource,
                Request.PreparedDestination, Request.DuplicatePolicy,
                Request.ExpectedAttemptRevision,
                Request.TimeoutMilliseconds, Request.AttemptStore, Request.Boundaries);
        }

        internal void DriftTargetRead(string drift)
        {
            var original = TargetSnapshot;
            var items = drift switch
            {
                "zero-additions" => original.Items.Where(item => item.ExactId == "existing-1"),
                "multiple-additions" => original.Items.Append(
                    new ManagedBrowserTargetInventoryItem("destination-2",
                        ManagedBrowserTargetInventoryItemKind.File, "other.apk")),
                "missing-baseline" => original.Items.Where(item => item.ExactId != "existing-1"),
                _ => original.Items,
            };
            var materialized = items.ToArray();
            var browserId = drift == "browser" ? Guid.NewGuid() : original.BrowserId;
            var targetId = drift == "target" ? "replacement-tab" : original.TargetId;
            var endpoint = drift == "endpoint" ? new Uri("http://127.0.0.1:9333/") : original.DevToolsEndpoint;
            var uid = drift == "uid" ? "other-user" : original.BaiduUserId;
            var path = drift == "path" ? "/other-target" : original.CanonicalPath;
            var replacement = new ManagedBrowserTargetInventorySnapshot(
                browserId, targetId, endpoint, uid, original.GenerationToken,
                original.DirectoryExactId, path, original.AncestorExactIds,
                materialized.Length, true, true, original.EnumerationStepCount, materialized);
            if (drift == "generation")
            {
                var changedGeneration = new ManagedBrowserTargetInventorySnapshot(
                    original.BrowserId, original.TargetId, original.DevToolsEndpoint,
                    original.BaiduUserId, "drifted-generation", original.DirectoryExactId,
                    original.CanonicalPath, original.AncestorExactIds, original.TotalCount,
                    true, true, original.EnumerationStepCount, original.Items);
                var originalEvidence = ManagedTransferTargetInventoryReader.Project(original);
                var changedEvidence = ManagedTransferTargetInventoryReader.Project(changedGeneration);
                TargetEvidenceOverride = new ManagedTransferTargetInventoryEvidence(
                    original, originalEvidence.AllItems, changedEvidence.DurableEvidence);
                return;
            }
            if (drift is "wire-vs-allitems" or "wire-vs-durable" or
                "noncanonical-allitems" or "wire-duplicate" or
                "allitems-duplicate" or "projection-fingerprint" or
                "wire-fingerprint" or "allitems-top-name")
            {
                var originalEvidence = ManagedTransferTargetInventoryReader.Project(original);
                if (drift == "wire-duplicate")
                {
                    SetBackingField(original, nameof(ManagedBrowserTargetInventorySnapshot.Items),
                        Array.AsReadOnly(new[] { original.Items[0], original.Items[0] }));
                    TargetEvidenceOverride = new ManagedTransferTargetInventoryEvidence(
                        original, originalEvidence.AllItems, originalEvidence.DurableEvidence);
                    return;
                }
                if (drift == "allitems-duplicate")
                {
                    var duplicate = originalEvidence.AllItems.DetailedItems[0];
                    SetBackingField(originalEvidence.AllItems,
                        nameof(TransferDirectoryListing.DetailedItems),
                        Array.AsReadOnly(new[] { duplicate, duplicate }));
                    TargetEvidenceOverride = originalEvidence;
                    return;
                }
                if (drift == "projection-fingerprint")
                {
                    SetBackingField(originalEvidence.AllItems,
                        nameof(TransferDirectoryListing.ObservationFingerprint),
                        new string('f', 64));
                    TargetEvidenceOverride = originalEvidence;
                    return;
                }
                if (drift == "wire-fingerprint")
                {
                    SetBackingField(original,
                        nameof(ManagedBrowserTargetInventorySnapshot.ObservationFingerprint),
                        new string('f', 64));
                    SetBackingField(originalEvidence.AllItems,
                        nameof(TransferDirectoryListing.ObservationFingerprint),
                        new string('f', 64));
                    TargetEvidenceOverride = new ManagedTransferTargetInventoryEvidence(
                        original, originalEvidence.AllItems, originalEvidence.DurableEvidence);
                    return;
                }
                if (drift == "allitems-top-name")
                {
                    var forgedTop = originalEvidence.AllItems.TopLevelItems
                        .Select(item => new RemoteItemIdentity(item.ExactId, "wrong-name"))
                        .ToArray();
                    SetBackingField(originalEvidence.AllItems,
                        nameof(TransferDirectoryListing.TopLevelItems),
                        Array.AsReadOnly(forgedTop));
                    TargetEvidenceOverride = originalEvidence;
                    return;
                }
                var changedSnapshot = new ManagedBrowserTargetInventorySnapshot(
                    original.BrowserId, original.TargetId, original.DevToolsEndpoint,
                    original.BaiduUserId, original.GenerationToken, original.DirectoryExactId,
                    original.CanonicalPath, original.AncestorExactIds, 2, true, true,
                    original.EnumerationStepCount,
                    [
                        new ManagedBrowserTargetInventoryItem("different-existing",
                            ManagedBrowserTargetInventoryItemKind.File, "different.txt"),
                        new ManagedBrowserTargetInventoryItem("different-destination",
                            ManagedBrowserTargetInventoryItemKind.File, "different.apk"),
                    ]);
                var changedEvidence = ManagedTransferTargetInventoryReader.Project(changedSnapshot);
                if (drift == "wire-vs-allitems")
                    TargetEvidenceOverride = new ManagedTransferTargetInventoryEvidence(
                        original, changedEvidence.AllItems, originalEvidence.DurableEvidence);
                else if (drift == "wire-vs-durable")
                    TargetEvidenceOverride = new ManagedTransferTargetInventoryEvidence(
                        original, originalEvidence.AllItems, changedEvidence.DurableEvidence);
                else
                {
                    var reversed = TransferDirectoryListing.Exact(
                        originalEvidence.AllItems.Directory!,
                        originalEvidence.AllItems.AncestorExactIds,
                        originalEvidence.AllItems.DetailedItems.Reverse().ToArray(),
                        originalEvidence.AllItems.TotalCount, true, true,
                        originalEvidence.AllItems.GenerationToken!,
                        originalEvidence.AllItems.BaiduUserId!,
                        originalEvidence.AllItems.ObservationFingerprint,
                        TransferDirectoryListingScope.AllItems);
                    TargetEvidenceOverride = new ManagedTransferTargetInventoryEvidence(
                        original, reversed, originalEvidence.DurableEvidence);
                }
                return;
            }
            TargetSnapshot = replacement;
        }

        private static void SetBackingField<T>(object target, string property, T value)
        {
            var field = target.GetType().GetField($"<{property}>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            field!.SetValue(target, value);
        }

        private static WebAutomationAttemptRecord BuildAttempt(WebAutomationAttemptState state,
            WebAutomationRequestIdentity identity, TransferSourceEvidence source,
            TransferTargetPreparationEvidence target, DateTimeOffset now,
            string readinessFingerprint)
        {
            if (state == WebAutomationAttemptState.BaselinePersisted)
                return new(identity.OperationId, identity.AccountId, identity.Generation,
                    WebAutomationOwnerKind.Transfer, state, null, target.Baseline,
                    target.ObservationFingerprint, false, 5, now.AddMinutes(-2), now,
                    sourceEvidence: source, targetPreparationEvidence: target,
                    executionProfile: identity.ExecutionProfile);
            var needsBaseline = state is WebAutomationAttemptState.Submitting or
                WebAutomationAttemptState.ResultPending or WebAutomationAttemptState.Succeeded;
            TransferSubmissionEvidence? submission = null;
            if (needsBaseline)
                submission = TransferSubmissionEvidence.CreateBound(Guid.NewGuid(), readinessFingerprint,
                    source.Items.Fingerprint, target.TargetExactId, target.Baseline.Fingerprint,
                    source.BaiduUserId, source.Capability, now);
            return new(identity.OperationId, identity.AccountId, identity.Generation,
                WebAutomationOwnerKind.Transfer, state, null, needsBaseline ? target.Baseline : null,
                needsBaseline ? target.ObservationFingerprint : source.ObservationFingerprint,
                false, 5, now.AddMinutes(-2), now, sourceEvidence: source,
                targetPreparationEvidence: target, submissionEvidence: submission,
                executionProfile: identity.ExecutionProfile);
        }
    }

    private sealed class FakeBoundaries(
        Fixture fixture,
        WebAutomationRequestIdentity identity,
        ManagedChromiumSessionHandle handle,
        ManagedBrowserLifecycleSession lifecycle,
        ManagedBrowserSourceSnapshot source,
        string officialTargetExactId) : IManagedTransferFinalSubmissionBoundaries
    {
        public async Task<IDisposable> AcquireAccountLeaseAsync(
            Guid accountId, CancellationToken cancellationToken)
        {
            fixture.Events.Add("lease");
            fixture.LeaseAcquireCalls++;
            if (fixture.EnforceExclusiveLease &&
                Interlocked.Exchange(ref fixture.LeaseHeld, 1) != 0)
            {
                fixture.ReacquireAttempts++;
                throw new InvalidOperationException("synthetic mutually-exclusive lease is held");
            }
            fixture.LeaseAcquireStarted.TrySetResult();
            fixture.AfterLeaseAcquired?.Invoke();
            return fixture.LeaseGate is null
                ? fixture.Lease
                : await fixture.LeaseGate.Task.ConfigureAwait(false);
        }

        public async Task<ManagedTransferRuntimeProofResult> ObserveRuntimeAsync(
            ManagedTransferFinalSubmissionProofRequest request,
            CancellationToken cancellationToken)
        {
            fixture.Events.Add("proof:" + request.ExpectedRoute.State);
            if (request.ExpectedRoute.State ==
                    ManagedTransferFinalSubmissionProofRoute.DestinationPicker &&
                fixture.FinalRouteProofGate is not null)
            {
                fixture.FinalRouteProofStarted.TrySetResult();
                await fixture.FinalRouteProofGate.Task.ConfigureAwait(false);
            }
            var drift = fixture.RuntimeDrift;
            var provenLifecycle = drift == "lifecycle"
                ? new ManagedBrowserLifecycleSession(lifecycle.Session,
                    lifecycle.InstallationRevision, lifecycle.SessionRevision + 1,
                    lifecycle.CanonicalProfilePath)
                : lifecycle;
            return new ManagedTransferRuntimeProofResult(
                request.ExpectedRoute.State == fixture.FailProofRoute
                    ? ManagedTransferRuntimeProofKind.NotExact
                    : ManagedTransferRuntimeProofKind.Exact,
                request.ExpectedRoute.State, provenLifecycle,
                drift == "process" ? handle.RootProcessId + 1 : handle.RootProcessId,
                drift == "browser" ? Guid.NewGuid() : handle.BrowserId,
                drift == "target" ? "replacement-target" : handle.TargetId,
                drift == "endpoint" ? new Uri("http://127.0.0.1:9333/") : handle.Endpoint,
                drift == "source" ? new string('f', 64) : source.ObservationFingerprint);
        }

        public async Task<ManagedTransferPickerOpenResult> OpenPickerAsync(
            ManagedTransferFinalSubmissionRequest request,
            CancellationToken cancellationToken)
        {
            fixture.Events.Add("picker");
            fixture.PickerStarted.TrySetResult();
            var kind = fixture.PickerGate is null
                ? fixture.PickerResult
                : await fixture.PickerGate.Task.ConfigureAwait(false);
            return new ManagedTransferPickerOpenResult(kind);
        }

        public Task<ManagedTransferDirectoryMutationExecutionResult> EnterDirectoryForSelectionAsync(
            ManagedTransferFinalSubmissionRequest request, string childExactId,
            CancellationToken cancellationToken)
        {
            fixture.Events.Add("enter:" + childExactId);
            return Task.FromResult(new ManagedTransferDirectoryMutationExecutionResult(
                fixture.EnterResult,
                new ManagedTransferDirectoryMutationState(true, false, false)));
        }

        public async Task<ManagedTransferReadinessResult> ObserveReadinessAsync(
            ManagedBrowserTransferSubmissionContext context,
            CancellationToken cancellationToken)
        {
            fixture.Events.Add("readiness");
            fixture.ObservedReadinessContext = context;
            fixture.ReadinessStarted.TrySetResult();
            if (fixture.CancelReadiness)
                return await Task.FromCanceled<ManagedTransferReadinessResult>(
                    new CancellationToken(true));
            if (fixture.ReadinessGate is not null)
                return await fixture.ReadinessGate.Task.ConfigureAwait(false);
            if (fixture.ReadinessPolicyOverride is { } driftedPolicy)
                context = new ManagedBrowserTransferSubmissionContext(
                    context.BrowserId, context.TargetId, context.DevToolsEndpoint,
                    context.TimeoutMilliseconds, context.SourceUri, context.BaiduUserId,
                    context.SourceGenerationToken, context.SourceObservationFingerprint,
                    context.SourceFingerprint, context.SourceExactId, context.SourceItemKind,
                    context.SelectedTargetExactId, context.SelectedTargetCanonicalPath,
                    context.TargetBaselineFingerprint, driftedPolicy);
            return new ManagedTransferReadinessResult(
                ManagedBrowserTransferSubmissionReadinessResultKind.Ready,
                new ManagedBrowserTransferSubmissionReadiness(context, 1));
        }

        public async ValueTask<IBaiduCandidateMutationPermit?> AcquireFinalPermitAsync(
            ManagedTransferFinalSubmissionRequest request,
            CancellationToken cancellationToken)
        {
            fixture.Events.Add("permit");
            fixture.PermitAcquireStarted.TrySetResult();
            if (fixture.FinalPermitAcquireOverride is not null)
                return await fixture.FinalPermitAcquireOverride(request).ConfigureAwait(false);
            if (fixture.PermitGate is not null)
                return await fixture.PermitGate.Task.ConfigureAwait(false);
            return new CountingPermit(fixture.Events, fixture.PermitDisposed,
                () => fixture.PermitDisposeThrows);
        }

        public async Task<ManagedTransferPermitOwnershipResult> ObservePermitOwnershipAsync(
            ManagedTransferFinalSubmissionRequest request,
            IBaiduCandidateMutationPermit permit,
            CancellationToken cancellationToken)
        {
            fixture.PermitOwnershipStarted.TrySetResult();
            if (fixture.PermitOwnershipOverride is not null)
                return await fixture.PermitOwnershipOverride(
                    request, permit, cancellationToken).ConfigureAwait(false);
            if (fixture.PermitOwnershipGate is not null)
                return await fixture.PermitOwnershipGate.Task.ConfigureAwait(false);
            return new ManagedTransferPermitOwnershipResult(
                fixture.FinalPermitOwned
                    ? ManagedTransferPermitOwnershipKind.Owned
                    : ManagedTransferPermitOwnershipKind.Lost,
                identity.OperationId, identity.AccountId, identity.Generation,
                AdapterWriteAction.SubmitTransfer);
        }

        public async Task<ManagedTransferSubmitResult> SubmitOnceAsync(
            ManagedBrowserTransferSubmissionReadiness readiness,
            CancellationToken cancellationToken)
        {
            fixture.SubmitCalls++;
            fixture.ObservedSubmitReadiness = readiness;
            fixture.Events.Add("submit");
            fixture.SubmitStarted.TrySetResult();
            if (fixture.SubmitThrows)
                throw new InvalidOperationException("synthetic submit fault");
            if (fixture.SubmitGate is not null)
                return await fixture.SubmitGate.Task.ConfigureAwait(false);
            return new ManagedTransferSubmitResult(
                ManagedBrowserTransferSubmitResultKind.ClickAttempted,
                fixture.SubmitDispatchState);
        }

        public Task<ManagedTransferOfficialOutcome> ObserveOutcomeAsync(
            ManagedBrowserTransferSubmissionReadiness readiness,
            CancellationToken cancellationToken)
        {
            fixture.Events.Add("official");
            if (fixture.OfficialThrows)
                throw new InvalidOperationException("synthetic official fault");
            if (fixture.OfficialKind != ManagedBrowserTransferOutcomeKind.AcceptedBound)
                return Task.FromResult(new ManagedTransferOfficialOutcome(fixture.OfficialKind));
            return Task.FromResult(new ManagedTransferOfficialOutcome(
                ManagedBrowserTransferOutcomeKind.AcceptedBound,
                ManagedBrowserTransferSubmissionOutcomeResult.CreateAcceptedBound(
                    "official-1", fixture.OfficialTargetExactId ?? officialTargetExactId, "source-id",
                    fixture.OfficialDestinationExactId)));
        }

        public Task<ManagedBrowserOpenTransferTargetRootResult> OpenFixedTargetRootAsync(
            ManagedTransferFinalSubmissionRequest request,
            CancellationToken cancellationToken)
        {
            fixture.Events.Add("target-root");
            return Task.FromResult(new ManagedBrowserOpenTransferTargetRootResult(
                ManagedBrowserOpenTransferTargetRootResultKind.RootOpened,
                fixture.TargetRootRouteUnknown
                    ? ManagedBrowserTargetNavigationState.RouteOutcomeUnknown
                    : ManagedBrowserTargetNavigationState.RouteOpened));
        }

        public Task<ManagedBrowserEnterTransferTargetDirectoryResult> EnterFixedTargetDirectoryAsync(
            ManagedTransferFinalSubmissionRequest request,
            ManagedBrowserTargetDirectoryHeader expectedParent,
            string childExactId,
            string childServerName,
            string childCanonicalPath,
            CancellationToken cancellationToken)
        {
            fixture.Events.Add("target-enter:" + childExactId);
            return Task.FromResult(new ManagedBrowserEnterTransferTargetDirectoryResult(
                ManagedBrowserEnterTransferTargetDirectoryResultKind.DirectoryEntered,
                fixture.TargetEntryRouteUnknown
                    ? ManagedBrowserTargetNavigationState.RouteOutcomeUnknown
                    : ManagedBrowserTargetNavigationState.RouteOpened));
        }

        public Task<ManagedTransferTargetInventoryReadResult> ReadFixedTargetAsync(
            ManagedTransferFinalSubmissionRequest request,
            string expectedDirectoryExactId,
            string expectedCanonicalPath,
            IReadOnlyList<string> expectedAncestorExactIds,
            CancellationToken cancellationToken)
        {
            var isFinal = string.Equals(expectedDirectoryExactId,
                fixture.TargetSnapshot.DirectoryExactId, StringComparison.Ordinal);
            fixture.Events.Add(isFinal ? "target" : "target:" + expectedDirectoryExactId);
            if (fixture.TargetReadKindOverride is { } rejected)
                return Task.FromResult(new ManagedTransferTargetInventoryReadResult(rejected));
            ManagedTransferTargetInventoryEvidence? evidence = null;
            if (!fixture.TargetReadReturnsNull)
            {
                if (isFinal)
                    evidence = fixture.TargetEvidenceOverride ??
                        ManagedTransferTargetInventoryReader.Project(fixture.TargetSnapshot);
                else
                {
                    var allAncestors = fixture.TargetSnapshot.AncestorExactIds;
                    var depth = expectedAncestorExactIds.Count;
                    var nextId = allAncestors[depth];
                    var segments = fixture.TargetSnapshot.CanonicalPath[1..].Split('/');
                    var nextName = segments[depth - 1];
                    var snapshot = new ManagedBrowserTargetInventorySnapshot(
                        handle.BrowserId, handle.TargetId, handle.Endpoint,
                        fixture.TargetSnapshot.BaiduUserId, "route-generation-" + depth,
                        expectedDirectoryExactId, expectedCanonicalPath,
                        expectedAncestorExactIds, 1, true, true, 1,
                        [new ManagedBrowserTargetInventoryItem(nextId,
                            ManagedBrowserTargetInventoryItemKind.Folder, nextName)]);
                    evidence = ManagedTransferTargetInventoryReader.Project(snapshot);
                }
            }
            return Task.FromResult(evidence is null
                ? new ManagedTransferTargetInventoryReadResult(
                    ManagedBrowserReadTransferTargetDirectoryResultKind.PageNotValidated)
                : new ManagedTransferTargetInventoryReadResult(
                    ManagedBrowserReadTransferTargetDirectoryResultKind.Observed, evidence));
        }

        public Task<ManagedTransferProcessExitObservation> ObserveProcessExitAsync(
            ManagedTransferFinalSubmissionRequest request,
            CancellationToken cancellationToken)
        {
            fixture.Events.Add("process-exit");
            return Task.FromResult(new ManagedTransferProcessExitObservation(
                ManagedTransferProcessExitObservationKind.Observed,
                handle.RootProcessId, handle.BrowserId, handle.TargetId));
        }
    }

    private sealed class ConcreteAuthorization : IBaiduCandidateActionAuthorizationSource
    {
        public ValueTask<bool> IsAuthorizedAsync(BaiduCandidateDomRequest request,
            CancellationToken cancellationToken) => ValueTask.FromResult(false);

        public ValueTask<IBaiduCandidateMutationPermit?> TryAcquireMutationPermitAsync(
            BaiduCandidateDomRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IBaiduCandidateMutationPermit?>(null);

        public ValueTask<IBaiduCandidateMutationPermit?> TryAcquireTransferMutationPermitAsync(
            BaiduCandidateDomMutationRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IBaiduCandidateMutationPermit?>(new ConcretePermit());

        private sealed class ConcretePermit : IBaiduCandidateMutationPermit
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class CountingBoundaryFactory(
        IManagedTransferFinalSubmissionBoundaries boundaries)
        : IManagedTransferFinalSubmissionBoundaryFactory
    {
        internal int CreateCalls;

        public IManagedTransferFinalSubmissionBoundaries CreateScope(
            ManagedTransferFinalSubmissionRequest request)
        {
            CreateCalls++;
            return boundaries;
        }
    }

    private sealed class AlwaysExactRouteProof : IManagedTransferRouteProof
    {
        public Task<bool> VerifyAsync(ManagedTransferRouteProofRequest request,
            CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class ExactProcessExitObserver : IManagedTransferProcessExitObserver
    {
        public Task<ManagedTransferProcessExitObservation> ObserveAsync(
            ManagedChromiumSessionHandle exactHandle, CancellationToken cancellationToken) =>
            Task.FromResult(new ManagedTransferProcessExitObservation(
                ManagedTransferProcessExitObservationKind.Observed,
                exactHandle.RootProcessId, exactHandle.BrowserId, exactHandle.TargetId));
    }

    private sealed class CountingLease(
        List<string> events,
        TaskCompletionSource disposed,
        Action? released = null) : IDisposable
    {
        internal int Disposals;
        public void Dispose()
        {
            Disposals++;
            events.Add("lease:dispose");
            released?.Invoke();
            disposed.TrySetResult();
        }
    }

    private sealed class CountingPermit(
        List<string> events,
        TaskCompletionSource disposed,
        Func<bool>? shouldThrow = null) : IBaiduCandidateMutationPermit
    {
        public ValueTask DisposeAsync()
        {
            events.Add("permit:dispose");
            disposed.TrySetResult();
            return shouldThrow?.Invoke() == true
                ? ValueTask.FromException(new InvalidOperationException("synthetic dispose fault"))
                : ValueTask.CompletedTask;
        }
    }

    internal sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        internal TimeSpan AdvanceOnEveryRead;

        public override DateTimeOffset GetUtcNow()
        {
            var current = now;
            now += AdvanceOnEveryRead;
            return current;
        }
        internal void Advance(TimeSpan by) => now += by;
    }

    private sealed class MemoryAttemptStore(WebAutomationAttemptRecord current, List<string> events)
        : IWebAutomationAttemptStore
    {
        internal WebAutomationAttemptRecord Current = current;
        internal int MutationCalls;
        internal int KnownNotDispatchedCalls;
        internal int ConfirmationCalls;
        internal int LateAcceptanceCalls;
        internal bool PersistSubmissionSucceeds = true;
        internal bool PersistSubmissionThrows;
        internal bool RequireExactSubmissionTimestamp;
        internal bool PersistConfirmationSucceeds = true;
        internal Action? AfterSubmissionPersisted;
        internal TaskCompletionSource<bool>? PersistSubmissionGate;
        internal TaskCompletionSource<bool>? AdvanceGate;
        internal TaskCompletionSource<bool>? KnownNotDispatchedGate;
        internal readonly TaskCompletionSource KnownNotDispatchedStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource AdvanceStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource PersistSubmissionStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly Dictionary<int, TaskCompletionSource> LoadGates = [];
        internal readonly Dictionary<int, TaskCompletionSource> LoadStarted = [];
        internal readonly HashSet<int> CancelledLoadCalls = [];
        internal int LoadCalls;

        public async Task<WebAutomationAttemptRecord?> GetAsync(Guid operationId, Guid accountId,
            int generation, CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref LoadCalls);
            var noDispatchReads = events.Contains("process-exit") &&
                !events.Contains("persist:no-dispatch");
            events.Add(noDispatchReads
                ? events.Contains("load:no-dispatch-1") ? "load:no-dispatch-2" : "load:no-dispatch-1"
                : "load");
            if (LoadStarted.TryGetValue(call, out var started)) started.TrySetResult();
            if (CancelledLoadCalls.Contains(call))
                return await Task.FromCanceled<WebAutomationAttemptRecord?>(
                    new CancellationToken(true));
            if (LoadGates.TryGetValue(call, out var gate))
                await gate.Task.ConfigureAwait(false);
            return Current;
        }

        internal (TaskCompletionSource Started, TaskCompletionSource Gate) GateLoad(int call)
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            LoadStarted.Add(call, started);
            LoadGates.Add(call, gate);
            return (started, gate);
        }

        public async Task<bool> TryPersistTransferSubmissionAsync(Guid operationId, Guid accountId,
            int generation, long expectedRevision, TransferSubmissionEvidence evidence,
            DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
        {
            MutationCalls++;
            events.Add("persist:submission");
            if (PersistSubmissionThrows)
                throw new InvalidOperationException("synthetic submission persistence fault");
            if (PersistSubmissionGate is not null)
            {
                PersistSubmissionStarted.TrySetResult();
                PersistSubmissionSucceeds = await PersistSubmissionGate.Task.ConfigureAwait(false);
            }
            if (!PersistSubmissionSucceeds ||
                (RequireExactSubmissionTimestamp &&
                    evidence.MarkedSubmittingAtUtc != updatedAt) ||
                Current.State != WebAutomationAttemptState.BaselinePersisted ||
                Current.Revision != expectedRevision) return false;
            Current = Copy(WebAutomationAttemptState.Submitting, Current.Revision + 1, updatedAt,
                submission: evidence);
            AfterSubmissionPersisted?.Invoke();
            return true;
        }

        public Task<bool> TryPersistTransferAcceptanceAsync(Guid operationId, Guid accountId,
            int generation, long expectedRevision, string officialRequestId,
            TransferIdentityMapping mapping, string observationFingerprint, DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            MutationCalls++;
            events.Add("persist:official");
            if (Current.State != WebAutomationAttemptState.Submitting ||
                Current.Revision != expectedRevision) return Task.FromResult(false);
            var accepted = new RemoteItemIdentitySet(mapping.Entries.Select(entry =>
                new RemoteItemIdentity(entry.DestinationExactId, null)));
            Current = Copy(WebAutomationAttemptState.ResultPending, Current.Revision + 1, updatedAt,
                accepted: accepted, requestId: officialRequestId, mapping: mapping,
                officialFingerprint: observationFingerprint);
            return Task.FromResult(true);
        }

        public Task<bool> TryPersistTransferLateAcceptanceAsync(Guid operationId, Guid accountId,
            int generation, long expectedRevision, Guid submissionId,
            string readinessFingerprint, string targetBaselineFingerprint,
            string officialRequestId, TransferIdentityMapping mapping,
            string observationFingerprint, DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            MutationCalls++;
            LateAcceptanceCalls++;
            events.Add("persist:late-official");
            if (Current.State != WebAutomationAttemptState.ResultPending ||
                Current.Revision != expectedRevision ||
                Current.SubmissionEvidence?.SubmissionId != submissionId ||
                Current.SubmissionEvidence.ReadinessFingerprint != readinessFingerprint ||
                Current.SubmissionEvidence.TargetBaselineFingerprint != targetBaselineFingerprint ||
                Current.OfficialRequestId is not null || Current.TransferIdentityMapping is not null ||
                Current.AcceptedRemoteItems is not null ||
                Current.OfficialObservationFingerprint is not null)
                return Task.FromResult(false);
            var accepted = new RemoteItemIdentitySet(mapping.Entries.Select(entry =>
                new RemoteItemIdentity(entry.DestinationExactId, null)));
            Current = Copy(WebAutomationAttemptState.ResultPending, Current.Revision + 1, updatedAt,
                accepted: accepted, requestId: officialRequestId, mapping: mapping,
                officialFingerprint: observationFingerprint);
            return Task.FromResult(true);
        }

        public async Task<bool> TryResolveTransferKnownNotDispatchedAsync(Guid operationId,
            Guid accountId, int generation, long expectedRevision, Guid submissionId,
            string readinessFingerprint, DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            MutationCalls++;
            KnownNotDispatchedCalls++;
            events.Add("persist:no-dispatch");
            if (KnownNotDispatchedGate is not null)
            {
                KnownNotDispatchedStarted.TrySetResult();
                _ = await KnownNotDispatchedGate.Task.ConfigureAwait(false);
            }
            if (Current.State != WebAutomationAttemptState.Submitting ||
                Current.Revision != expectedRevision ||
                Current.SubmissionEvidence?.SubmissionId != submissionId ||
                Current.SubmissionEvidence.ReadinessFingerprint != readinessFingerprint)
                return false;
            Current = Copy(WebAutomationAttemptState.DefinitelyNotSubmitted,
                Current.Revision + 1, updatedAt);
            return true;
        }

        public Task<bool> TryPersistTransferTargetConfirmationAsync(Guid operationId, Guid accountId,
            int generation, long expectedRevision, TransferTargetConfirmationEvidence evidence,
            CancellationToken cancellationToken = default)
        {
            MutationCalls++;
            ConfirmationCalls++;
            events.Add("persist:confirmation");
            if (!PersistConfirmationSucceeds ||
                Current.State != WebAutomationAttemptState.ResultPending ||
                Current.Revision != expectedRevision) return Task.FromResult(false);
            Current = Copy(WebAutomationAttemptState.Succeeded, Current.Revision + 1,
                evidence.ConfirmedAtUtc, confirmation: evidence,
                observationFingerprint: evidence.TargetObservationFingerprint);
            return Task.FromResult(true);
        }

        private WebAutomationAttemptRecord Copy(WebAutomationAttemptState state, long revision,
            DateTimeOffset updatedAt, TransferSubmissionEvidence? submission = null,
            RemoteItemIdentitySet? accepted = null, string? requestId = null,
            TransferIdentityMapping? mapping = null, string? officialFingerprint = null,
            TransferTargetConfirmationEvidence? confirmation = null,
            string? observationFingerprint = null) =>
            new(Current.OperationId, Current.AccountId, Current.Generation, Current.OwnerKind,
                state, null, Current.Baseline, observationFingerprint ?? Current.ObservationFingerprint,
                false, revision, Current.CreatedAt, updatedAt,
                accepted ?? Current.AcceptedRemoteItems, Current.SourceEvidence,
                Current.TargetPreparationEvidence, submission ?? Current.SubmissionEvidence,
                requestId ?? Current.OfficialRequestId, mapping ?? Current.TransferIdentityMapping,
                Current.ExecutionProfile, officialFingerprint ?? Current.OfficialObservationFingerprint,
                confirmation ?? Current.TargetConfirmationEvidence);

        internal void ApplyPostPersistDrift(string drift)
        {
            var source = Current.SourceEvidence!;
            var target = Current.TargetPreparationEvidence!;
            var submission = Current.SubmissionEvidence!;
            var revision = Current.Revision;
            var state = Current.State;
            var profile = Current.ExecutionProfile;
            if (drift == "revision") revision++;
            if (drift == "state") state = WebAutomationAttemptState.ResultPending;
            if (drift == "profile") profile = ExecutionProfileSnapshot.Legacy("drifted-profile");
            if (drift == "readiness")
                submission = TransferSubmissionEvidence.CreateBound(
                    submission.SubmissionId!.Value, new string('e', 64),
                    submission.SourceFingerprint, submission.TargetExactId,
                    submission.TargetBaselineFingerprint, submission.BaiduUserId,
                    submission.Capability, submission.MarkedSubmittingAtUtc);
            if (drift is "source" or "capability" or "uid")
            {
                var capability = drift == "capability"
                    ? new AdapterCapabilityVersion("BaiduTransfer", "protocol-drift",
                        "selectors-drift", "build-drift")
                    : source.Capability;
                var uid = drift == "uid" ? "drifted-user" : source.BaiduUserId;
                var items = drift == "source"
                    ? new RemoteItemIdentitySet([new RemoteItemIdentity("source-id-2", null)])
                    : source.Items;
                source = new TransferSourceEvidence(items, source.TotalCount,
                    source.ReachedEnd, source.IsStable, source.GenerationToken, uid,
                    capability, source.ObservationFingerprint);
                if (drift == "uid")
                    target = new TransferTargetPreparationEvidence(target.TargetExactId,
                        target.TargetPath, target.TotalCount, target.ReachedEnd, target.IsStable,
                        target.TargetGenerationToken, uid, target.Scope, target.Baseline,
                        TransferTargetPreparationEvidence.ComputeObservationFingerprint(
                            target.TargetExactId, target.TargetPath, target.TotalCount,
                            target.ReachedEnd, target.IsStable, target.TargetGenerationToken,
                            uid, target.Scope, target.Baseline));
                submission = TransferSubmissionEvidence.CreateBound(
                    submission.SubmissionId!.Value, submission.ReadinessFingerprint!,
                    items.Fingerprint, target.TargetExactId, target.Baseline.Fingerprint,
                    uid, capability, submission.MarkedSubmittingAtUtc);
            }
            if (drift == "target")
            {
                var baseline = WebAutomationBaseline.Create(["other-existing"]);
                var path = new RemotePath("/other-target");
                target = new TransferTargetPreparationEvidence("other-target", path, 1, true,
                    true, target.TargetGenerationToken, target.BaiduUserId, target.Scope, baseline,
                    TransferTargetPreparationEvidence.ComputeObservationFingerprint(
                        "other-target", path, 1, true, true, target.TargetGenerationToken,
                        target.BaiduUserId, target.Scope, baseline));
                submission = TransferSubmissionEvidence.CreateBound(
                    submission.SubmissionId!.Value, submission.ReadinessFingerprint!,
                    source.Items.Fingerprint, target.TargetExactId, baseline.Fingerprint,
                    source.BaiduUserId, source.Capability, submission.MarkedSubmittingAtUtc);
            }
            Current = new WebAutomationAttemptRecord(Current.OperationId, Current.AccountId,
                drift == "generation" ? Current.Generation + 1 : Current.Generation,
                Current.OwnerKind, state, null, target.Baseline,
                target.ObservationFingerprint, false, revision, Current.CreatedAt,
                Current.UpdatedAt, sourceEvidence: source, targetPreparationEvidence: target,
                submissionEvidence: submission, executionProfile: profile);
        }

        internal void BumpBaselineRevision() => Current = new WebAutomationAttemptRecord(
            Current.OperationId, Current.AccountId, Current.Generation, Current.OwnerKind,
            Current.State, Current.ResumeStage, Current.Baseline, Current.ObservationFingerprint,
            Current.IsWaitingForUser, Current.Revision + 1, Current.CreatedAt, Current.UpdatedAt,
            Current.AcceptedRemoteItems, Current.SourceEvidence, Current.TargetPreparationEvidence,
            Current.SubmissionEvidence, Current.OfficialRequestId, Current.TransferIdentityMapping,
            Current.ExecutionProfile, Current.OfficialObservationFingerprint,
            Current.TargetConfirmationEvidence);

        public Task<bool> BeginAsync(WebAutomationAttemptRecord attempt,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Page<WebAutomationAttemptRecord>> GetRecoveryPageAsync(PageQuery query,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> TryPersistBaselineAsync(Guid operationId, Guid accountId, int generation,
            long expectedRevision, WebAutomationBaseline baseline, string observationFingerprint,
            DateTimeOffset updatedAt, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public async Task<bool> TryAdvanceAsync(Guid operationId, Guid accountId, int generation,
            WebAutomationAttemptState expectedState, long expectedRevision,
            WebAutomationAttemptState nextState, string? observationFingerprint,
            bool isWaitingForUser, DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            MutationCalls++;
            events.Add("persist:pending");
            if (AdvanceGate is not null)
            {
                AdvanceStarted.TrySetResult();
                if (!await AdvanceGate.Task.ConfigureAwait(false)) return false;
            }
            if (Current.State != expectedState || Current.Revision != expectedRevision ||
                nextState != WebAutomationAttemptState.ResultPending) return false;
            Current = Copy(nextState, Current.Revision + 1, updatedAt,
                observationFingerprint: observationFingerprint);
            return true;
        }
        public Task<bool> TryResumeFromUserHandoffAsync(Guid operationId, Guid accountId,
            int generation, long expectedRevision, WebAutomationAttemptState resumeStage,
            string? observationFingerprint, DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> TryResolveUserHandoffAsync(Guid operationId, Guid accountId,
            int generation, long expectedRevision, WebAutomationAttemptState resumeStage,
            WebAutomationAttemptState terminalState, string? observationFingerprint,
            DateTimeOffset updatedAt, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
