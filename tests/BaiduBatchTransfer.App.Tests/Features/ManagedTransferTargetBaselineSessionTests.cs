using BaiduBatchTransfer.App.Features.BrowserAutomation;
using BaiduBatchTransfer.App.Features.ManagedBrowser;
using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.BaiduAdapter.WebAutomation;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Infrastructure.Security;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class ManagedTransferTargetBaselineSessionTests
{
    public static TheoryData<WebAutomationAttemptState> NonPreparingStates => new()
    {
        WebAutomationAttemptState.BaselinePersisted,
        WebAutomationAttemptState.UserHandoff,
        WebAutomationAttemptState.Submitting,
        WebAutomationAttemptState.ResultPending,
        WebAutomationAttemptState.Succeeded,
        WebAutomationAttemptState.DefinitelyNotSubmitted,
        WebAutomationAttemptState.NeedsDecision,
    };

    public static TheoryData<string> DownstreamEvidenceKinds => new()
    {
        "submission-evidence",
        "official-request-id",
        "transfer-identity-mapping",
        "accepted-remote-items",
    };

    public static TheoryData<string> CandidateIdentityMismatchKinds => new()
    {
        "capability",
        "operation",
        "account",
        "generation",
    };

    public static TheoryData<string> CandidateTimeBoundaryKinds => new()
    {
        "issued-one-tick-in-the-future",
        "expires-exactly-now",
        "expired-one-tick-before-now",
        "issued-equals-expiry",
    };

    public static TheoryData<string, int> FixedActionIdentityDriftCases
    {
        get
        {
            var data = new TheoryData<string, int>();
            foreach (var drift in new[]
                     {
                         "endpoint", "target-id", "process", "profile", "baidu-uid", "source-evidence",
                     })
            for (var completedAction = 0; completedAction <= 6; completedAction++)
                data.Add(drift, completedAction);
            return data;
        }
    }

    public static TheoryData<int> FixedActionExpiryBoundaries => new()
    {
        1, 2, 3, 4, 5, 6, 7,
    };

    public static TheoryData<string, string, ManagedBrowserTargetNavigationState>
        PostNavigationLoginSecurityCases => new()
        {
            { "open-target-root", "login", ManagedBrowserTargetNavigationState.RouteOutcomeUnknown },
            { "open-target-root", "security", ManagedBrowserTargetNavigationState.RouteOutcomeUnknown },
            { "enter-target-child", "login", ManagedBrowserTargetNavigationState.KnownNotDispatched },
            { "enter-target-child", "security", ManagedBrowserTargetNavigationState.RouteOutcomeUnknown },
            { "restore-source", "login", ManagedBrowserTargetNavigationState.KnownNotDispatched },
            { "restore-source", "security", ManagedBrowserTargetNavigationState.RouteOutcomeUnknown },
        };

    public static TheoryData<string, string> NonCooperativeReadAndRestoreCases => new()
    {
        { "root-target-inventory", "caller-cancel" },
        { "root-target-inventory", "operation-timeout" },
        { "child-target-inventory", "caller-cancel" },
        { "child-target-inventory", "operation-timeout" },
        { "source-restore", "caller-cancel" },
        { "source-restore", "operation-timeout" },
    };

    [Theory]
    [MemberData(nameof(NonPreparingStates))]
    public async Task NonPreparingAttemptStateIsAZeroDispatchGate(WebAutomationAttemptState state)
    {
        var fixture = Fixture.Create();
        fixture.Store.Current = fixture.Attempt(state);

        var result = await fixture.Run();

        Assert.Equal(ManagedTransferTargetBaselineResultKind.PageNotValidated, result.Kind);
        fixture.AssertZeroDispatch();
    }

    [Fact]
    public async Task WrongOwnerRevisionMissingSourceOrExistingTargetAreZeroDispatchGates()
    {
        foreach (var invalid in new[] { "owner", "revision", "source", "target" })
        {
            var fixture = Fixture.Create();
            fixture.Store.Current = invalid switch
            {
                "owner" => fixture.CopyAttempt(owner: WebAutomationOwnerKind.TransferShare),
                "revision" => fixture.CopyAttempt(revision: fixture.ExpectedRevision + 1),
                "source" => fixture.CopyAttempt(source: null, replaceSource: true),
                "target" => fixture.CopyAttempt(target: fixture.TargetEvidence(), replaceTarget: true),
                _ => throw new InvalidOperationException(),
            };

            var result = await fixture.Run();

            Assert.Equal(ManagedTransferTargetBaselineResultKind.PageNotValidated, result.Kind);
            fixture.AssertZeroDispatch();
        }
    }

    [Theory]
    [MemberData(nameof(DownstreamEvidenceKinds))]
    public async Task PreparingAttemptWithAnyDownstreamEvidenceIsAZeroDispatchGate(string evidenceKind)
    {
        var fixture = Fixture.Create();
        fixture.Store.Current = fixture.AttemptWithDownstreamEvidence(evidenceKind);

        var result = await fixture.Run();

        Assert.Equal(ManagedTransferTargetBaselineResultKind.PageNotValidated, result.Kind);
        fixture.AssertZeroDispatch();
        Assert.Equal(0, fixture.Lease.Acquisitions);
    }

    [Fact]
    public async Task HandoffFromAnotherOperationIsAZeroDispatchGate()
    {
        var fixture = Fixture.Create();
        var other = Fixture.Create();

        var result = await fixture.Run(preparedDestination: other.PreparedDestination);

        Assert.Equal(ManagedTransferTargetBaselineResultKind.PageNotValidated, result.Kind);
        fixture.AssertZeroDispatch();
    }

    [Fact]
    public async Task AttemptAdvancedWhileWaitingForLeaseIsRecheckedBeforeAnyDispatch()
    {
        var fixture = Fixture.Create();
        fixture.AdvanceAttemptOnLease = true;

        var result = await fixture.Run();

        Assert.Equal(ManagedTransferTargetBaselineResultKind.PageNotValidated, result.Kind);
        fixture.AssertZeroDispatch();
        Assert.Equal(1, fixture.Lease.Disposals);
    }

    [Fact]
    public async Task CandidateCapabilityAndIdentityMismatchAreZeroDispatchGates()
    {
        foreach (var invalid in new[] { "capability", "account", "generation" })
        {
            var fixture = Fixture.Create();
            var binding = invalid switch
            {
                "capability" => fixture.Candidate(new AdapterCapabilityVersion(
                    "BaiduTransfer", "other-protocol", "selectors", "synthetic-build")),
                "account" => fixture.Candidate(accountId: Guid.NewGuid()),
                "generation" => fixture.Candidate(generation: fixture.Identity.Generation + 1),
                _ => throw new InvalidOperationException(),
            };

            var result = await fixture.Run(candidateBinding: binding);

            Assert.Equal(ManagedTransferTargetBaselineResultKind.PageNotValidated, result.Kind);
            fixture.AssertZeroDispatch();
        }
    }

    [Theory]
    [MemberData(nameof(CandidateIdentityMismatchKinds))]
    public async Task CandidateCapabilityOperationAccountAndGenerationMismatchAreNamedZeroDispatchGates(
        string mismatchKind)
    {
        var fixture = Fixture.Create();
        var binding = mismatchKind switch
        {
            "capability" => fixture.Candidate(new AdapterCapabilityVersion(
                "BaiduTransfer", "other-protocol", "selectors", "synthetic-build")),
            "operation" => fixture.Candidate(operationId: Guid.NewGuid()),
            "account" => fixture.Candidate(accountId: Guid.NewGuid()),
            "generation" => fixture.Candidate(generation: fixture.Identity.Generation + 1),
            _ => throw new InvalidOperationException(),
        };

        var result = await fixture.Run(candidateBinding: binding);

        Assert.Equal(ManagedTransferTargetBaselineResultKind.PageNotValidated, result.Kind);
        fixture.AssertZeroDispatch();
        Assert.Equal(0, fixture.Lease.Acquisitions);
    }

    [Fact]
    public async Task SuccessUsesOneLeaseAndPersistsOnlyAfterExactTargetTraversalAndSourceRestoration()
    {
        var fixture = Fixture.Create();
        fixture.SuccessMode = true;
        fixture.StrictRouteProof = true;
        fixture.Store.PersistSucceeds = true;

        var result = await fixture.Run();

        Assert.Equal(ManagedTransferTargetBaselineResultKind.BaselinePersisted, result.Kind);
        var evidence = Assert.IsType<TransferTargetPreparationEvidence>(result.Evidence);
        Assert.Equal("target-id", evidence.TargetExactId);
        Assert.Equal("/target", evidence.TargetPath.Value);
        Assert.Equal(["existing-file"], evidence.Baseline.Items.Select(item => item.ExactId));
        Assert.Equal(1, fixture.Lease.Acquisitions);
        Assert.Equal(1, fixture.Lease.Disposals);
        Assert.Equal(1, fixture.Store.PersistCalls);
        Assert.Equal(
            [
                "runtime", "route:OriginalSource", "helper:ReadTransferSource",
                "runtime", "route:OriginalSource", "helper:OpenTransferTargetRoot",
                "runtime", "route:OfficialTargetRoot", "helper:ReadTransferTargetDirectory",
                "route:ExactTargetDirectory:/", "runtime", "route:ExactTargetDirectory:/",
                "helper:EnterTransferTargetDirectory", "runtime",
                "route:ExactTargetDirectory:/target", "helper:ReadTransferTargetDirectory",
                "route:ExactTargetDirectory:/target", "runtime",
                "route:ExactTargetDirectory:/target", "helper:OpenTransferSourceAfterTargetRead",
                "runtime", "route:OriginalSource", "helper:ReadTransferSource", "persist"
            ],
            fixture.Events.TakeWhile(value => value != "cleanup:get1").ToArray());
        Assert.DoesNotContain(fixture.Requests, request => request.Action is
            ManagedBrowserAction.BeginCreateTransferDirectory or
            ManagedBrowserAction.ConfirmCreateTransferDirectory);
    }

    [Fact]
    public async Task FutureIssuedAndExpiredCandidatesDispatchNothing()
    {
        foreach (var invalid in new[] { "future", "expired" })
        {
            var fixture = Fixture.Create();
            var binding = invalid == "future"
                ? fixture.Candidate(issuedAt: fixture.Clock.GetUtcNow().AddMinutes(1))
                : fixture.Candidate(expiresAt: fixture.Clock.GetUtcNow());
            var result = await fixture.Run(candidateBinding: binding);
            Assert.Equal(ManagedTransferTargetBaselineResultKind.PageNotValidated, result.Kind);
            fixture.AssertZeroDispatch();
        }
    }

    [Theory]
    [MemberData(nameof(CandidateTimeBoundaryKinds))]
    public async Task CandidateFutureIssueAndEveryExpiryBoundaryAreNamedZeroDispatchGates(string boundary)
    {
        var fixture = Fixture.Create();
        var now = fixture.Clock.GetUtcNow();
        var binding = boundary switch
        {
            "issued-one-tick-in-the-future" => fixture.Candidate(issuedAt: now.AddTicks(1)),
            "expires-exactly-now" => fixture.Candidate(expiresAt: now),
            "expired-one-tick-before-now" => fixture.Candidate(expiresAt: now.AddTicks(-1)),
            "issued-equals-expiry" => fixture.Candidate(
                issuedAt: now.AddMinutes(-1), expiresAt: now.AddMinutes(-1)),
            _ => throw new InvalidOperationException(),
        };

        var result = await fixture.Run(candidateBinding: binding);

        Assert.Equal(ManagedTransferTargetBaselineResultKind.PageNotValidated, result.Kind);
        fixture.AssertZeroDispatch();
        Assert.Equal(0, fixture.Lease.Acquisitions);
    }

    [Fact]
    public async Task CandidateExpiringAfterTargetOpenQuarantinesTheRoute()
    {
        var fixture = Fixture.Create();
        fixture.SuccessMode = true;
        fixture.StrictRouteProof = true;
        fixture.AfterAction = action =>
        {
            if (action == ManagedBrowserAction.OpenTransferTargetRoot)
                fixture.Clock.Advance(TimeSpan.FromMinutes(6));
        };

        var result = await fixture.Run();

        Assert.Equal(ManagedTransferTargetBaselineResultKind.RouteQuarantined, result.Kind);
        Assert.NotNull(result.Quarantine);
        Assert.Equal(0, fixture.Lease.Disposals);
        Assert.Equal(0, fixture.Store.PersistCalls);
    }

    [Theory]
    [MemberData(nameof(FixedActionIdentityDriftCases))]
    public async Task EndpointTargetIdProcessProfileUidAndSourceDriftAtEveryFixedActionBoundaryPreventsPersistence(
        string driftKind, int afterCompletedAction)
    {
        var fixture = Fixture.Create();
        fixture.SuccessMode = true;
        fixture.StrictRouteProof = true;
        fixture.Store.PersistSucceeds = true;
        if (afterCompletedAction == 0) fixture.ApplyDrift(driftKind);
        fixture.AfterFixedAction = (ordinal, _) =>
        {
            if (ordinal == afterCompletedAction) fixture.ApplyDrift(driftKind);
        };

        var result = await fixture.Run();

        Assert.NotEqual(ManagedTransferTargetBaselineResultKind.BaselinePersisted, result.Kind);
        Assert.Equal(0, fixture.Store.PersistCalls);
        Assert.Equal(afterCompletedAction, fixture.Requests.Count);
        Assert.Equal(afterCompletedAction is 0 or 1 ? 1 : 0, fixture.Lease.Disposals);
    }

    [Theory]
    [MemberData(nameof(FixedActionExpiryBoundaries))]
    public async Task CandidateExpiryAfterEveryFixedActionBoundaryPreventsTheNextDispatchOrPersistence(
        int afterCompletedAction)
    {
        var fixture = Fixture.Create();
        fixture.SuccessMode = true;
        fixture.StrictRouteProof = true;
        fixture.Store.PersistSucceeds = true;
        fixture.AfterFixedAction = (ordinal, _) =>
        {
            if (ordinal == afterCompletedAction) fixture.Clock.Advance(TimeSpan.FromMinutes(6));
        };

        var result = await fixture.Run();

        Assert.NotEqual(ManagedTransferTargetBaselineResultKind.BaselinePersisted, result.Kind);
        Assert.Equal(0, fixture.Store.PersistCalls);
        Assert.Equal(afterCompletedAction, fixture.Requests.Count);
        Assert.Equal(afterCompletedAction == 1 ? 1 : 0, fixture.Lease.Disposals);
    }

    [Fact]
    public async Task NonCooperativeNavigationKeepsLeaseUntilSettlementAndCleanup()
    {
        var fixture = Fixture.Create();
        fixture.SuccessMode = true;
        fixture.StrictRouteProof = true;
        fixture.PendingAction = ManagedBrowserAction.OpenTransferTargetRoot;
        using var cancellation = new CancellationTokenSource();

        var running = fixture.Run(cancellationToken: cancellation.Token);
        await fixture.PendingActionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(2));

        var quarantine = Assert.IsType<ManagedTransferTargetBaselineQuarantine>(result.Quarantine);
        Assert.Equal(0, fixture.Lease.Disposals);
        fixture.PendingResponse.SetResult(fixture.ReplyPending(
            new ManagedBrowserOpenTransferTargetRootResult(
                ManagedBrowserOpenTransferTargetRootResultKind.RootOpened,
                ManagedBrowserTargetNavigationState.RouteOpened)));
        Assert.Equal(ManagedTransferTargetBaselineReconciliationStatus.Released,
            await quarantine.ReconcileAsync(3000));
        Assert.Equal(1, fixture.Lease.Disposals);
        Assert.Single(fixture.Requests, request =>
            request.Action == ManagedBrowserAction.OpenTransferTargetRoot);
        Assert.Single(fixture.Requests, request =>
            request.Action == ManagedBrowserAction.OpenTransferSourceAfterTargetRead);
    }

    [Theory]
    [MemberData(nameof(NonCooperativeReadAndRestoreCases))]
    public async Task TargetInventoryAndSourceRestoreCancellationOrTimeoutKeepLeaseUntilNonCooperativeSettlement(
        string fixedAction, string interruption)
    {
        var fixture = Fixture.Create();
        fixture.SuccessMode = true;
        fixture.StrictRouteProof = true;
        fixture.PendingAction = fixedAction.EndsWith("target-inventory", StringComparison.Ordinal)
            ? ManagedBrowserAction.ReadTransferTargetDirectory
            : ManagedBrowserAction.OpenTransferSourceAfterTargetRead;
        fixture.PendingActionOccurrence = fixedAction == "child-target-inventory" ? 2 : 1;
        using var cancellation = new CancellationTokenSource();

        var running = fixture.Run(cancellationToken: cancellation.Token,
            timeoutMilliseconds: interruption == "operation-timeout" ? 30 : 3000);
        await fixture.PendingActionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (interruption == "caller-cancel") cancellation.Cancel();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(2));

        var quarantine = Assert.IsType<ManagedTransferTargetBaselineQuarantine>(result.Quarantine);
        Assert.Equal(0, fixture.Lease.Disposals);
        Assert.Equal(0, fixture.Store.PersistCalls);
        fixture.PendingResponse.SetResult(fixture.ReplyPendingSuccess());
        Assert.Equal(ManagedTransferTargetBaselineReconciliationStatus.Released,
            await quarantine.ReconcileAsync(3000));
        Assert.Equal(1, fixture.Lease.Disposals);
        if (fixedAction == "source-restore")
            Assert.Single(fixture.Requests, request =>
                request.Action == ManagedBrowserAction.OpenTransferSourceAfterTargetRead);
    }

    [Fact]
    public async Task LateInvalidRootInventoryFallsBackToOfficialRootProofBeforeSourceRestore()
    {
        var fixture = Fixture.Create();
        fixture.SuccessMode = true;
        fixture.StrictRouteProof = true;
        fixture.PendingAction = ManagedBrowserAction.ReadTransferTargetDirectory;
        using var cancellation = new CancellationTokenSource();

        var running = fixture.Run(cancellationToken: cancellation.Token);
        await fixture.PendingActionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(2));

        var quarantine = Assert.IsType<ManagedTransferTargetBaselineQuarantine>(result.Quarantine);
        fixture.PendingResponse.SetResult(fixture.ReplyPending(
            new ManagedBrowserReadTransferTargetDirectoryResult(
                ManagedBrowserReadTransferTargetDirectoryResultKind.PageNotValidated)));
        Assert.Equal(ManagedTransferTargetBaselineReconciliationStatus.Released,
            await quarantine.ReconcileAsync(3000));
        Assert.Contains("route:ExactTargetDirectory:/", fixture.Events);
        Assert.Contains("route:OfficialTargetRoot", fixture.Events);
        Assert.Single(fixture.Requests, request =>
            request.Action == ManagedBrowserAction.OpenTransferSourceAfterTargetRead);
        Assert.Equal(1, fixture.Lease.Disposals);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LatePersistenceSettlementIsReconciledWithoutRepersisting(bool writeSucceeded)
    {
        var fixture = Fixture.Create();
        fixture.SuccessMode = true;
        fixture.Store.PersistGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();

        var running = fixture.Run(cancellationToken: cancellation.Token);
        await fixture.Store.PersistStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(2));

        var quarantine = Assert.IsType<ManagedTransferTargetBaselineQuarantine>(result.Quarantine);
        Assert.Equal(0, fixture.Lease.Disposals);
        fixture.Store.PersistGate.SetResult(writeSucceeded);
        Assert.Equal(ManagedTransferTargetBaselineReconciliationStatus.Released,
            await quarantine.ReconcileAsync(3000));
        Assert.Equal(1, fixture.Store.PersistCalls);
        Assert.Equal(1, fixture.Lease.Disposals);
    }

    [Theory]
    [InlineData("noncooperative-persist")]
    [InlineData("noncooperative-source-restore")]
    public async Task ExpiredCandidateAfterLateSettlementAtExactSourceReleasesByPureObservationOnly(
        string lateOperation)
    {
        var fixture = Fixture.Create();
        fixture.SuccessMode = true;
        fixture.StrictRouteProof = true;
        if (lateOperation == "noncooperative-persist")
            fixture.Store.PersistGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        else
            fixture.PendingAction = ManagedBrowserAction.OpenTransferSourceAfterTargetRead;
        using var cancellation = new CancellationTokenSource();

        var running = fixture.Run(cancellationToken: cancellation.Token);
        if (lateOperation == "noncooperative-persist")
            await fixture.Store.PersistStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        else
            await fixture.PendingActionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(2));

        var quarantine = Assert.IsType<ManagedTransferTargetBaselineQuarantine>(result.Quarantine);
        fixture.Clock.Advance(TimeSpan.FromMinutes(6));
        if (lateOperation == "noncooperative-persist")
            fixture.Store.PersistGate!.SetResult(false);
        else
            fixture.PendingResponse.SetResult(fixture.ReplyPendingSuccess());
        var restoreRequestsBeforeCleanup = fixture.Requests.Count(request =>
            request.Action == ManagedBrowserAction.OpenTransferSourceAfterTargetRead);
        var routeProofsBeforeCleanup = fixture.RouteProof.Calls;

        Assert.Equal(ManagedTransferTargetBaselineReconciliationStatus.Released,
            await quarantine.ReconcileAsync(3000));
        Assert.True(fixture.RouteProof.Calls > routeProofsBeforeCleanup);
        Assert.Equal(restoreRequestsBeforeCleanup, fixture.Requests.Count(request =>
            request.Action == ManagedBrowserAction.OpenTransferSourceAfterTargetRead));
        Assert.Equal(1, fixture.Lease.Disposals);
    }

    [Fact]
    public async Task ExpiredCandidateOffSourceIsObservedButNeverUsedForRecoveryNavigation()
    {
        var fixture = Fixture.Create();
        fixture.SuccessMode = true;
        fixture.StrictRouteProof = true;
        fixture.PendingAction = ManagedBrowserAction.OpenTransferTargetRoot;
        using var cancellation = new CancellationTokenSource();

        var running = fixture.Run(cancellationToken: cancellation.Token);
        await fixture.PendingActionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(2));

        var quarantine = Assert.IsType<ManagedTransferTargetBaselineQuarantine>(result.Quarantine);
        fixture.Clock.Advance(TimeSpan.FromMinutes(6));
        fixture.PendingResponse.SetResult(fixture.ReplyPending(
            new ManagedBrowserOpenTransferTargetRootResult(
                ManagedBrowserOpenTransferTargetRootResultKind.RootOpened,
                ManagedBrowserTargetNavigationState.RouteOpened)));
        var routeProofsBeforeCleanup = fixture.RouteProof.Calls;

        Assert.Equal(ManagedTransferTargetBaselineReconciliationStatus.RetainedInconclusive,
            await quarantine.ReconcileAsync(3000));
        Assert.True(fixture.RouteProof.Calls > routeProofsBeforeCleanup);
        Assert.DoesNotContain(fixture.Requests, request =>
            request.Action == ManagedBrowserAction.OpenTransferSourceAfterTargetRead);
        Assert.Equal(0, fixture.Lease.Disposals);
    }

    [Fact]
    public async Task CleanupAttemptDriftRetainsOpaqueNonDisposableQuarantine()
    {
        var fixture = Fixture.Create();
        fixture.SuccessMode = true;
        fixture.Store.PersistSucceeds = true;
        fixture.Store.DriftOnCleanupSecondRead = true;

        var result = await fixture.Run();

        var quarantine = Assert.IsType<ManagedTransferTargetBaselineQuarantine>(result.Quarantine);
        Assert.Equal(ManagedTransferTargetBaselineResultKind.RouteQuarantined, result.Kind);
        Assert.DoesNotContain(typeof(IDisposable), quarantine.GetType().GetInterfaces());
        Assert.Equal(0, fixture.Lease.Disposals);
        Assert.Equal(ManagedTransferTargetBaselineReconciliationStatus.RetainedInconclusive,
            await quarantine.ReconcileAsync(3000));
        Assert.Equal(0, fixture.Lease.Disposals);
    }

    [Fact]
    public async Task TimedOutCleanupTaskMustSettleBeforeAReconcileCanReleaseTheLease()
    {
        var fixture = Fixture.Create();
        fixture.SuccessMode = true;
        fixture.Store.PersistSucceeds = true;
        fixture.Store.CleanupFirstReadGate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        var result = await fixture.Run().WaitAsync(TimeSpan.FromSeconds(7));

        var quarantine = Assert.IsType<ManagedTransferTargetBaselineQuarantine>(result.Quarantine);
        var reconciling = quarantine.ReconcileAsync(3000);
        await Task.Delay(50);
        Assert.False(reconciling.IsCompleted);
        Assert.Equal(0, fixture.Lease.Disposals);

        fixture.Store.CleanupFirstReadGate.SetResult(fixture.Store.Current);
        Assert.Equal(ManagedTransferTargetBaselineReconciliationStatus.Released,
            await reconciling.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, fixture.Lease.Disposals);
    }

    [Theory]
    [InlineData("execution-profile")]
    [InlineData("attempt-observation")]
    [InlineData("source-capability")]
    public async Task CleanupRejectsAConsistentButNotExactPersistedAttempt(string corruption)
    {
        var fixture = Fixture.Create();
        fixture.SuccessMode = true;
        fixture.Store.PersistSucceeds = true;
        fixture.Store.PersistedCorruption = corruption;

        var result = await fixture.Run();

        Assert.Equal(ManagedTransferTargetBaselineResultKind.RouteQuarantined, result.Kind);
        Assert.Equal(0, fixture.Lease.Disposals);
        Assert.Equal(ManagedTransferTargetBaselineReconciliationStatus.RetainedInconclusive,
            await Assert.IsType<ManagedTransferTargetBaselineQuarantine>(result.Quarantine)
                .ReconcileAsync(3000));
        Assert.Equal(0, fixture.Lease.Disposals);
    }

    [Fact]
    public async Task LateEnteredChildIsReadAndProvedBeforeTheSoleSourceRestore()
    {
        var fixture = Fixture.Create();
        fixture.SuccessMode = true;
        fixture.StrictRouteProof = true;
        fixture.PendingAction = ManagedBrowserAction.EnterTransferTargetDirectory;
        using var cancellation = new CancellationTokenSource();

        var running = fixture.Run(cancellationToken: cancellation.Token);
        await fixture.PendingActionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(2));

        var quarantine = Assert.IsType<ManagedTransferTargetBaselineQuarantine>(result.Quarantine);
        fixture.PendingResponse.SetResult(fixture.ReplyPending(
            new ManagedBrowserEnterTransferTargetDirectoryResult(
                ManagedBrowserEnterTransferTargetDirectoryResultKind.DirectoryEntered,
                ManagedBrowserTargetNavigationState.RouteOpened)));
        Assert.Equal(ManagedTransferTargetBaselineReconciliationStatus.Released,
            await quarantine.ReconcileAsync(3000));
        Assert.Contains("route:ExactTargetDirectory:/target", fixture.Events);
        Assert.Contains(fixture.Requests, request =>
            request.Action == ManagedBrowserAction.ReadTransferTargetDirectory &&
            Assert.IsType<ManagedBrowserReadTransferTargetDirectoryRequest>(request.Payload)
                .ExpectedCanonicalPath == "/target");
        Assert.Single(fixture.Requests, request =>
            request.Action == ManagedBrowserAction.OpenTransferSourceAfterTargetRead);
        Assert.Equal(1, fixture.Lease.Disposals);
    }

    [Theory]
    [InlineData(ManagedBrowserTargetNavigationState.KnownNotDispatched)]
    [InlineData(ManagedBrowserTargetNavigationState.RouteOutcomeUnknown)]
    public async Task LateEnterThatLeftParentFallsBackToExactParentProofBeforeSourceRestore(
        ManagedBrowserTargetNavigationState lateNavigation)
    {
        var fixture = Fixture.Create();
        fixture.SuccessMode = true;
        fixture.StrictRouteProof = true;
        fixture.PendingAction = ManagedBrowserAction.EnterTransferTargetDirectory;
        using var cancellation = new CancellationTokenSource();

        var running = fixture.Run(cancellationToken: cancellation.Token);
        await fixture.PendingActionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(2));

        var quarantine = Assert.IsType<ManagedTransferTargetBaselineQuarantine>(result.Quarantine);
        fixture.PendingResponse.SetResult(fixture.ReplyPending(
            new ManagedBrowserEnterTransferTargetDirectoryResult(
                ManagedBrowserEnterTransferTargetDirectoryResultKind.Cancelled, lateNavigation)));
        Assert.Equal(ManagedTransferTargetBaselineReconciliationStatus.Released,
            await quarantine.ReconcileAsync(3000));
        Assert.Contains("route:ExactTargetDirectory:/target", fixture.Events);
        Assert.Contains("route:ExactTargetDirectory:/", fixture.Events);
        Assert.Single(fixture.Requests, request =>
            request.Action == ManagedBrowserAction.OpenTransferSourceAfterTargetRead);
        Assert.Equal(1, fixture.Lease.Disposals);
    }

    [Fact]
    public async Task OperationDeadlineCoversNonCooperativeInitialAttemptRead()
    {
        var fixture = Fixture.Create();
        fixture.Store.InitialReadGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        var result = await fixture.Run(timeoutMilliseconds: 30).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(ManagedTransferTargetBaselineResultKind.TimedOut, result.Kind);
        Assert.Equal(0, fixture.Lease.Acquisitions);
        fixture.AssertZeroDispatch();
        fixture.Store.InitialReadGate.SetResult(fixture.Store.Current);
    }

    [Theory]
    [InlineData("caller-cancel")]
    [InlineData("operation-timeout")]
    public async Task CancelledOrTimedOutNonCooperativeLeaseAcquisitionDisposesItsLateLeaseExactlyOnceWithoutDispatch(
        string interruption)
    {
        var fixture = Fixture.Create();
        fixture.LeaseGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();

        var running = fixture.Run(cancellationToken: cancellation.Token,
            timeoutMilliseconds: interruption == "operation-timeout" ? 30 : 3000);
        await fixture.LeaseStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (interruption == "caller-cancel") cancellation.Cancel();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(interruption == "caller-cancel"
            ? ManagedTransferTargetBaselineResultKind.Cancelled
            : ManagedTransferTargetBaselineResultKind.TimedOut, result.Kind);
        Assert.Equal(0, fixture.Lease.Disposals);
        fixture.AssertZeroDispatch();
        Assert.Equal(1, fixture.Store.GetCalls);

        fixture.LeaseGate.SetResult(fixture.Lease);
        await fixture.LeaseDisposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, fixture.Lease.Disposals);
        fixture.AssertZeroDispatch();
    }

    [Fact]
    public async Task RuntimeDriftBeforeNextFixedActionQuarantinesWithoutPersistence()
    {
        var fixture = Fixture.Create();
        fixture.SuccessMode = true;
        fixture.AfterAction = action =>
        {
            if (action == ManagedBrowserAction.OpenTransferTargetRoot)
                fixture.RuntimeValid = false;
        };

        var result = await fixture.Run();

        Assert.Equal(ManagedTransferTargetBaselineResultKind.RouteQuarantined, result.Kind);
        Assert.Equal(0, fixture.Store.PersistCalls);
        Assert.Equal(0, fixture.Lease.Disposals);
    }

    [Fact]
    public async Task PreNavigationLoginRequiredIsTypedAndReleasesAfterSettlement()
    {
        var fixture = Fixture.Create();
        fixture.SuccessMode = true;
        fixture.ForcedInitialSourceKind = ManagedBrowserSourceListingResultKind.LoginRequired;

        var result = await fixture.Run();

        Assert.Equal(ManagedTransferTargetBaselineResultKind.LoginRequired, result.Kind);
        Assert.Null(result.Quarantine);
        Assert.Equal(1, fixture.Lease.Disposals);
        Assert.DoesNotContain(fixture.Requests, request =>
            request.Action == ManagedBrowserAction.OpenTransferTargetRoot);
    }

    [Theory]
    [InlineData("login")]
    [InlineData("security")]
    public async Task LoginOrSecurityKnownNotDispatchedAtFirstNavigationReleasesTheLease(string status)
    {
        var fixture = Fixture.Create();
        fixture.SuccessMode = true;
        fixture.ForcedResult = request => request.Action == ManagedBrowserAction.OpenTransferTargetRoot
            ? fixture.NavigationFailure(request.Action, status,
                ManagedBrowserTargetNavigationState.KnownNotDispatched)
            : null;

        var result = await fixture.Run();

        Assert.Equal(status == "login"
            ? ManagedTransferTargetBaselineResultKind.LoginRequired
            : ManagedTransferTargetBaselineResultKind.VerificationRequired, result.Kind);
        Assert.Null(result.Quarantine);
        Assert.Equal(1, fixture.Lease.Disposals);
        Assert.Equal(0, fixture.Store.PersistCalls);
    }

    [Theory]
    [MemberData(nameof(PostNavigationLoginSecurityCases))]
    public async Task LoginOrSecurityAfterNavigationWasPossibleAlwaysQuarantines(
        string actionName, string status, ManagedBrowserTargetNavigationState navigationState)
    {
        var fixture = Fixture.Create();
        fixture.SuccessMode = true;
        var action = actionName switch
        {
            "open-target-root" => ManagedBrowserAction.OpenTransferTargetRoot,
            "enter-target-child" => ManagedBrowserAction.EnterTransferTargetDirectory,
            "restore-source" => ManagedBrowserAction.OpenTransferSourceAfterTargetRead,
            _ => throw new InvalidOperationException(),
        };
        fixture.ForcedResult = request => request.Action == action
            ? fixture.NavigationFailure(action, status, navigationState)
            : null;

        var result = await fixture.Run();

        Assert.Equal(ManagedTransferTargetBaselineResultKind.RouteQuarantined, result.Kind);
        Assert.NotNull(result.Quarantine);
        Assert.Equal(0, fixture.Lease.Disposals);
        Assert.Equal(0, fixture.Store.PersistCalls);
    }

    [Fact]
    public async Task FreshSourceDriftAfterRestoreQuarantinesAndNeverPersists()
    {
        var fixture = Fixture.Create();
        fixture.SuccessMode = true;
        fixture.DriftSecondSourceRead = true;

        var result = await fixture.Run();

        Assert.Equal(ManagedTransferTargetBaselineResultKind.RouteQuarantined, result.Kind);
        Assert.Equal(0, fixture.Store.PersistCalls);
        Assert.Equal(0, fixture.Lease.Disposals);
    }

    private sealed class Fixture
    {
        private static readonly DateTimeOffset Now = new(2026, 8, 29, 8, 0, 0, TimeSpan.Zero);
        internal const string SourceUri = "https://pan.baidu.com/s/synthetic-source";
        internal readonly MutableTimeProvider Clock = new(Now);
        internal readonly AdapterCapabilityVersion Capability =
            new("BaiduTransfer", "protocol-v2", "selectors-v2", "synthetic-build");
        internal readonly List<ManagedBrowserRequest> Requests = [];
        internal readonly List<string> Events = [];
        internal readonly CountingRouteProof RouteProof;
        internal readonly CountingLease Lease = new();
        internal TaskCompletionSource<IDisposable>? LeaseGate;
        internal readonly TaskCompletionSource LeaseStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource LeaseDisposed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly MemoryAttemptStore Store;
        internal readonly WebAutomationRequestIdentity Identity;
        internal readonly ManagedBrowserSessionReference ManagedIdentity;
        internal readonly ManagedChromiumSessionHandle Handle;
        internal readonly ManagedBrowserLifecycleSession LifecycleSnapshot;
        internal readonly ManagedBrowserSourceSnapshot SourceSnapshot;
        internal readonly TransferSourceEvidence SourceEvidence;
        internal readonly ManagedPreparedDestinationEvidence PreparedDestination;
        internal readonly BaiduCandidateSessionBinding CandidateBinding;
        internal readonly long ExpectedRevision = 4;
        internal bool SuccessMode;
        internal bool StrictRouteProof;
        internal bool RuntimeValid = true;
        internal bool EndpointValid = true;
        internal bool TargetIdValid = true;
        internal bool UidValid = true;
        internal bool SourceBindingValid = true;
        internal ManagedBrowserLifecycleSession LifecycleCurrent;
        internal ManagedBrowserSourceListingResultKind? ForcedInitialSourceKind;
        internal bool DriftSecondSourceRead;
        internal int SourceReads;
        internal ManagedTransferRouteState CurrentRoute = ManagedTransferRouteState.OriginalSource;
        internal string? CurrentRoutePath;
        internal bool AdvanceAttemptOnLease;
        internal ManagedBrowserAction? PendingAction;
        internal bool PendingActionConsumed;
        internal int PendingActionOccurrence = 1;
        internal int PendingActionSeen;
        internal readonly TaskCompletionSource PendingActionStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<ManagedBrowserResponse> PendingResponse =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManagedBrowserRequest? PendingRequest;
        internal Action<ManagedBrowserAction>? AfterAction;
        internal Action<int, ManagedBrowserAction>? AfterFixedAction;
        internal Func<ManagedBrowserRequest, ManagedBrowserResult?>? ForcedResult;
        internal int CompletedFixedActions;

        private Fixture()
        {
            Lease.OnDisposed = () => LeaseDisposed.TrySetResult();
            var installation = new BrowserInstallationIdentity(Guid.NewGuid(), BrowserFamily.Chrome,
                @"C:\SyntheticBrowser", @"C:\SyntheticBrowser\chrome.exe",
                "Google LLC", "Google Chrome", "synthetic-file");
            ManagedIdentity = new(Guid.NewGuid(), installation.InstallationId, Guid.NewGuid(),
                "synthetic-user", BrowserFamily.Chrome, Guid.NewGuid().ToString("N"),
                Guid.NewGuid().ToString("N"));
            Identity = new(Guid.NewGuid(), Guid.NewGuid(), ManagedIdentity.AccountId, 7,
                WebAutomationOwnerKind.Transfer, ExecutionProfileSnapshot.Managed(
                    ManagedIdentity.BrowserFamily, ManagedIdentity.ProfileId,
                    ManagedIdentity.InstallationId, ManagedIdentity.SessionId));
            var profile = ManagedBrowserProfileDescriptor.FromStoredSession(
                ManagedIdentity, @"C:\SyntheticAppData");
            var port = new DevToolsActivePort(9222, Guid.NewGuid(), new Uri("http://127.0.0.1:9222/"),
                new Uri("ws://127.0.0.1:9222/devtools/browser/synthetic"));
            Handle = new ManagedChromiumSessionHandle(31415, installation, profile, port, "fixed-tab");
            LifecycleSnapshot = new(ManagedIdentity, 11, 13, profile.CanonicalSourcePath);
            LifecycleCurrent = LifecycleSnapshot;
            SourceSnapshot = CreateSourceSnapshot(ManagedIdentity.StableBaiduUserId);
            SourceEvidence = new(new([new RemoteItemIdentity("source-id", "source-name")]),
                1, true, true, SourceSnapshot.GenerationToken, SourceSnapshot.BaiduUserId,
                Capability, SourceSnapshot.ObservationFingerprint);
            RouteProof = new(Events, request => !StrictRouteProof ||
                EndpointValid && TargetIdValid && UidValid && SourceBindingValid &&
                request.ExpectedRoute.State == CurrentRoute &&
                (request.ExpectedRoute.State != ManagedTransferRouteState.ExactTargetDirectory ||
                 string.Equals(request.ExpectedRoute.CanonicalPath, CurrentRoutePath,
                     StringComparison.Ordinal)));
            Store = new(Attempt(WebAutomationAttemptState.Preparing), Events);
            CandidateBinding = Candidate();
            PreparedDestination = CreatePreparedDestination();
        }

        internal static Fixture Create() => new();

        internal async Task<ManagedTransferTargetBaselineResult> Run(
            BaiduCandidateSessionBinding? candidateBinding = null,
            ManagedPreparedDestinationEvidence? preparedDestination = null,
            CancellationToken cancellationToken = default,
            int timeoutMilliseconds = 3000) =>
            await new ManagedTransferTargetBaselineSession(
                (_, _) =>
                {
                    Lease.Acquisitions++;
                    LeaseStarted.TrySetResult();
                    if (AdvanceAttemptOnLease)
                        Store.Current = Attempt(WebAutomationAttemptState.BaselinePersisted);
                    return LeaseGate?.Task ?? Task.FromResult<IDisposable>(Lease);
                },
                (_, _) => Task.FromResult<ManagedBrowserLifecycleSession?>(LifecycleCurrent),
                (_, _, _) =>
                {
                    Events.Add("runtime");
                    return Task.FromResult(RuntimeValid);
                }, Send, RouteProof, Clock)
                .RunAsync(Identity, candidateBinding ?? CandidateBinding,
                    BaiduExecutionContext.Managed(ManagedIdentity), Handle, ManagedIdentity,
                SourceUri, SourceSnapshot, preparedDestination ?? PreparedDestination,
                    ExpectedRevision, timeoutMilliseconds, Store, cancellationToken);

        internal void ApplyDrift(string driftKind)
        {
            switch (driftKind)
            {
                case "endpoint": EndpointValid = false; break;
                case "target-id": TargetIdValid = false; break;
                case "process": RuntimeValid = false; break;
                case "profile":
                    LifecycleCurrent = new(ManagedIdentity,
                        LifecycleSnapshot.InstallationRevision,
                        LifecycleSnapshot.SessionRevision,
                        LifecycleSnapshot.CanonicalProfilePath + "-drift");
                    break;
                case "baidu-uid": UidValid = false; break;
                case "source-evidence": SourceBindingValid = false; break;
                default: throw new InvalidOperationException();
            }
        }

        internal ManagedBrowserResult NavigationFailure(ManagedBrowserAction action, string status,
            ManagedBrowserTargetNavigationState navigationState) => action switch
        {
            ManagedBrowserAction.OpenTransferTargetRoot =>
                new ManagedBrowserOpenTransferTargetRootResult(status == "login"
                        ? ManagedBrowserOpenTransferTargetRootResultKind.LoginRequired
                        : ManagedBrowserOpenTransferTargetRootResultKind.VerificationRequired,
                    navigationState),
            ManagedBrowserAction.EnterTransferTargetDirectory =>
                new ManagedBrowserEnterTransferTargetDirectoryResult(status == "login"
                        ? ManagedBrowserEnterTransferTargetDirectoryResultKind.LoginRequired
                        : ManagedBrowserEnterTransferTargetDirectoryResultKind.VerificationRequired,
                    navigationState),
            ManagedBrowserAction.OpenTransferSourceAfterTargetRead =>
                new ManagedBrowserOpenTransferSourceAfterTargetReadResult(status == "login"
                        ? ManagedBrowserOpenTransferSourceAfterTargetReadResultKind.LoginRequired
                        : ManagedBrowserOpenTransferSourceAfterTargetReadResultKind.VerificationRequired,
                    navigationState),
            _ => throw new InvalidOperationException(),
        };

        internal BaiduCandidateSessionBinding Candidate(
            AdapterCapabilityVersion? capability = null, Guid? accountId = null, int? generation = null,
            DateTimeOffset? issuedAt = null, DateTimeOffset? expiresAt = null,
            Guid? operationId = null)
        {
            var selected = capability ?? Capability;
            var account = accountId ?? Identity.AccountId;
            var selectedGeneration = generation ?? Identity.Generation;
            var operation = operationId ?? Identity.OperationId;
            var authorization = new AdapterWriteAuthorizationRequest(selected, account,
                operation, selectedGeneration, Identity.BatchId, AdapterWriteAction.EnsureDirectory);
            return new(authorization, new(AdapterWriteAuthorizationSource.ConsumedLease, selected,
                account, operation, selectedGeneration, Identity.BatchId,
                issuedAt ?? Now.AddMinutes(-1), expiresAt ?? Now.AddMinutes(5), false, null),
                ManagedIdentity.StableBaiduUserId);
        }

        internal WebAutomationAttemptRecord Attempt(WebAutomationAttemptState state)
        {
            var baseline = state is WebAutomationAttemptState.BaselinePersisted or
                WebAutomationAttemptState.Submitting or WebAutomationAttemptState.ResultPending or
                WebAutomationAttemptState.Succeeded
                ? WebAutomationBaseline.Create(["legacy-id"])
                : null;
            return new(Identity.OperationId, Identity.AccountId, Identity.Generation,
                WebAutomationOwnerKind.Transfer, state,
                state == WebAutomationAttemptState.UserHandoff ? WebAutomationAttemptState.Preparing : null,
                baseline, baseline?.Fingerprint ?? SourceEvidence.ObservationFingerprint,
                state == WebAutomationAttemptState.UserHandoff, ExpectedRevision, Now.AddMinutes(-2), Now,
                sourceEvidence: SourceEvidence, executionProfile: Identity.ExecutionProfile);
        }

        internal WebAutomationAttemptRecord AttemptWithDownstreamEvidence(string evidenceKind)
        {
            var target = TargetEvidence();
            var accepted = new RemoteItemIdentitySet([new RemoteItemIdentity("accepted-id", null)]);
            var mapping = new TransferIdentityMapping(
                [new TransferIdentityMappingEntry("source-id", "accepted-id")]);
            var submission = new TransferSubmissionEvidence(SourceEvidence.Items.Fingerprint,
                target.TargetExactId, target.Baseline.Fingerprint, SourceEvidence.BaiduUserId,
                SourceEvidence.Capability, Now);
            return evidenceKind switch
            {
                "submission-evidence" => new(Identity.OperationId, Identity.AccountId,
                    Identity.Generation, WebAutomationOwnerKind.Transfer,
                    WebAutomationAttemptState.Preparing, null, target.Baseline,
                    SourceEvidence.ObservationFingerprint, false, ExpectedRevision,
                    Now.AddMinutes(-2), Now, sourceEvidence: SourceEvidence,
                    targetPreparationEvidence: target, submissionEvidence: submission,
                    executionProfile: Identity.ExecutionProfile),
                "official-request-id" => new(Identity.OperationId, Identity.AccountId,
                    Identity.Generation, WebAutomationOwnerKind.Transfer,
                    WebAutomationAttemptState.Preparing, null, null,
                    SourceEvidence.ObservationFingerprint, false, ExpectedRevision,
                    Now.AddMinutes(-2), Now, sourceEvidence: SourceEvidence,
                    officialRequestId: "official-request", executionProfile: Identity.ExecutionProfile),
                "transfer-identity-mapping" => new(Identity.OperationId, Identity.AccountId,
                    Identity.Generation, WebAutomationOwnerKind.Transfer,
                    WebAutomationAttemptState.Preparing, null, null,
                    SourceEvidence.ObservationFingerprint, false, ExpectedRevision,
                    Now.AddMinutes(-2), Now, acceptedRemoteItems: accepted,
                    sourceEvidence: SourceEvidence, officialRequestId: "official-request",
                    transferIdentityMapping: mapping, executionProfile: Identity.ExecutionProfile),
                "accepted-remote-items" => new(Identity.OperationId, Identity.AccountId,
                    Identity.Generation, WebAutomationOwnerKind.Transfer,
                    WebAutomationAttemptState.Preparing, null, null,
                    SourceEvidence.ObservationFingerprint, false, ExpectedRevision,
                    Now.AddMinutes(-2), Now, acceptedRemoteItems: accepted,
                    sourceEvidence: SourceEvidence, executionProfile: Identity.ExecutionProfile),
                _ => throw new InvalidOperationException(),
            };
        }

        internal WebAutomationAttemptRecord CopyAttempt(
            WebAutomationOwnerKind? owner = null, long? revision = null,
            TransferSourceEvidence? source = null, bool replaceSource = false,
            TransferTargetPreparationEvidence? target = null, bool replaceTarget = false)
        {
            var current = Store.Current;
            var selectedSource = replaceSource ? source : current.SourceEvidence;
            var selectedTarget = replaceTarget ? target : current.TargetPreparationEvidence;
            return new(current.OperationId, current.AccountId, current.Generation, owner ?? current.OwnerKind,
                current.State, current.ResumeStage, selectedTarget?.Baseline, selectedSource?.ObservationFingerprint,
                current.IsWaitingForUser, revision ?? current.Revision, current.CreatedAt, current.UpdatedAt,
                current.AcceptedRemoteItems, selectedSource, selectedTarget, current.SubmissionEvidence,
                current.OfficialRequestId, current.TransferIdentityMapping, current.ExecutionProfile);
        }

        internal TransferTargetPreparationEvidence TargetEvidence()
        {
            var baseline = WebAutomationBaseline.Create(["old-target"]);
            var path = new RemotePath("/target");
            var fingerprint = TransferTargetPreparationEvidence.ComputeObservationFingerprint(
                "target-id", path, 1, true, true, "target-generation", ManagedIdentity.StableBaiduUserId,
                TransferTargetInventoryScope.AllItems, baseline);
            return new("target-id", path, 1, true, true, "target-generation",
                ManagedIdentity.StableBaiduUserId, TransferTargetInventoryScope.AllItems, baseline, fingerprint);
        }

        internal void AssertZeroDispatch()
        {
            Assert.Empty(Requests);
            Assert.Equal(0, RouteProof.Calls);
            Assert.Equal(0, Store.PersistCalls);
        }

        private ManagedPreparedDestinationEvidence CreatePreparedDestination()
        {
            var destination = new RemoteDirectory("target-id", new RemotePath("/target"));
            var raw = new ManagedBrowserDirectorySnapshot(ManagedIdentity.StableBaiduUserId,
                "target-generation", destination.RemoteId, destination.Path.Value,
                ["root-id", destination.RemoteId], 0, true, true, [], new string('0', 64));
            var snapshot = new ManagedBrowserDirectorySnapshot(raw.BaiduUserId, raw.GenerationToken,
                raw.ParentExactId, raw.CanonicalPath, raw.AncestorExactIds, raw.TotalCount,
                true, true, raw.Items, raw.ComputeFingerprint(SourceUri));
            var finalEvidence = ManagedTransferDirectoryEvidence.CreateFromReaderValidated(snapshot);
            var request = new TransferDirectoryPreparationRequest(Identity.OperationId, Identity.BatchId,
                Identity.AccountId, Identity.Generation, new ShareReference("synthetic", SourceUri),
                destination.Path, Capability);
            return ManagedPreparedDestinationEvidence.CreateFromValidatedState(Identity,
                BaiduExecutionContext.Managed(ManagedIdentity), request, Store.Current, SourceEvidence,
                ExpectedRevision, TransferDirectoryPreparationOutcome.Prepared(destination), finalEvidence);
        }

        private static ManagedBrowserSourceSnapshot CreateSourceSnapshot(string uid)
        {
            var raw = new ManagedBrowserSourceSnapshot(uid, "source-generation", 1, true, true,
                [new ManagedBrowserSourceItem("source-id", ManagedBrowserSourceItemKind.File, "source-name")],
                new string('0', 64));
            return new(raw.BaiduUserId, raw.GenerationToken, raw.TotalCount, true, true, raw.Items,
                raw.ComputeFingerprint(SourceUri));
        }

        private Task<ManagedBrowserResponse> Send(ManagedBrowserRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (!SuccessMode)
                throw new InvalidOperationException("A zero-dispatch test reached the helper.");
            Events.Add("helper:" + request.Action);
            if (request.Action == PendingAction) PendingActionSeen++;
            if (request.Action == PendingAction && !PendingActionConsumed &&
                PendingActionSeen == PendingActionOccurrence)
            {
                PendingActionConsumed = true;
                PendingRequest = request;
                PendingActionStarted.TrySetResult();
                return PendingResponse.Task;
            }
            ManagedBrowserResult result = ForcedResult?.Invoke(request) ?? (request.Action switch
            {
                ManagedBrowserAction.ReadTransferSource =>
                    SourceResult(),
                ManagedBrowserAction.OpenTransferTargetRoot =>
                    new ManagedBrowserOpenTransferTargetRootResult(
                        ManagedBrowserOpenTransferTargetRootResultKind.RootOpened,
                        ManagedBrowserTargetNavigationState.RouteOpened),
                ManagedBrowserAction.EnterTransferTargetDirectory =>
                    new ManagedBrowserEnterTransferTargetDirectoryResult(
                        ManagedBrowserEnterTransferTargetDirectoryResultKind.DirectoryEntered,
                        ManagedBrowserTargetNavigationState.RouteOpened),
                ManagedBrowserAction.ReadTransferTargetDirectory =>
                    new ManagedBrowserReadTransferTargetDirectoryResult(
                        ManagedBrowserReadTransferTargetDirectoryResultKind.Observed,
                        TargetSnapshot(Assert.IsType<ManagedBrowserReadTransferTargetDirectoryRequest>(
                            request.Payload).ExpectedCanonicalPath)),
                ManagedBrowserAction.OpenTransferSourceAfterTargetRead =>
                    new ManagedBrowserOpenTransferSourceAfterTargetReadResult(
                        ManagedBrowserOpenTransferSourceAfterTargetReadResultKind.SourceRestored,
                        ManagedBrowserTargetNavigationState.RouteOpened),
                _ => throw new InvalidOperationException("Unexpected fixed helper action."),
            });
            if (ResultOpenedRoute(result)) ApplyRoute(request.Action, request.Payload);
            CompletedFixedActions++;
            AfterFixedAction?.Invoke(CompletedFixedActions, request.Action);
            AfterAction?.Invoke(request.Action);
            return Task.FromResult(ManagedBrowserResponse.For(request, result));

            ManagedBrowserSourceListingResult SourceResult()
            {
                SourceReads++;
                if (SourceReads == 1 && ForcedInitialSourceKind is { } forced)
                    return new(forced);
                if (DriftSecondSourceRead && SourceReads >= 2)
                {
                    var raw = new ManagedBrowserSourceSnapshot(ManagedIdentity.StableBaiduUserId,
                        "changed-generation", 1, true, true,
                        [new ManagedBrowserSourceItem("changed-id",
                            ManagedBrowserSourceItemKind.File, "changed-name")], new string('0', 64));
                    return new(ManagedBrowserSourceListingResultKind.Observed,
                        new(raw.BaiduUserId, raw.GenerationToken, raw.TotalCount, true, true,
                            raw.Items, raw.ComputeFingerprint(SourceUri)));
                }
                return new(ManagedBrowserSourceListingResultKind.Observed, SourceSnapshot);
            }
        }

        internal ManagedBrowserResponse ReplyPending(ManagedBrowserResult result)
        {
            var request = Assert.IsType<ManagedBrowserRequest>(PendingRequest);
            if (ResultOpenedRoute(result)) ApplyRoute(request.Action, request.Payload);
            CompletedFixedActions++;
            AfterFixedAction?.Invoke(CompletedFixedActions, request.Action);
            AfterAction?.Invoke(request.Action);
            return ManagedBrowserResponse.For(request, result);
        }

        internal ManagedBrowserResponse ReplyPendingSuccess()
        {
            var request = Assert.IsType<ManagedBrowserRequest>(PendingRequest);
            ManagedBrowserResult result = request.Action switch
            {
                ManagedBrowserAction.ReadTransferTargetDirectory =>
                    new ManagedBrowserReadTransferTargetDirectoryResult(
                        ManagedBrowserReadTransferTargetDirectoryResultKind.Observed,
                        TargetSnapshot(Assert.IsType<ManagedBrowserReadTransferTargetDirectoryRequest>(
                            request.Payload).ExpectedCanonicalPath)),
                ManagedBrowserAction.OpenTransferSourceAfterTargetRead =>
                    new ManagedBrowserOpenTransferSourceAfterTargetReadResult(
                        ManagedBrowserOpenTransferSourceAfterTargetReadResultKind.SourceRestored,
                        ManagedBrowserTargetNavigationState.RouteOpened),
                _ => throw new InvalidOperationException(),
            };
            return ReplyPending(result);
        }

        private static bool ResultOpenedRoute(ManagedBrowserResult result) => result switch
        {
            ManagedBrowserOpenTransferTargetRootResult root =>
                root.NavigationState == ManagedBrowserTargetNavigationState.RouteOpened,
            ManagedBrowserEnterTransferTargetDirectoryResult enter =>
                enter.NavigationState == ManagedBrowserTargetNavigationState.RouteOpened,
            ManagedBrowserOpenTransferSourceAfterTargetReadResult source =>
                source.NavigationState == ManagedBrowserTargetNavigationState.RouteOpened,
            ManagedBrowserReadTransferTargetDirectoryResult inventory =>
                inventory.Kind == ManagedBrowserReadTransferTargetDirectoryResultKind.Observed,
            _ => true,
        };

        private void ApplyRoute(ManagedBrowserAction action, ManagedBrowserRequestPayload payload)
        {
            if (action == ManagedBrowserAction.OpenTransferTargetRoot)
            {
                CurrentRoute = ManagedTransferRouteState.OfficialTargetRoot;
                CurrentRoutePath = null;
            }
            else if (action == ManagedBrowserAction.EnterTransferTargetDirectory)
            {
                CurrentRoute = ManagedTransferRouteState.ExactTargetDirectory;
                CurrentRoutePath = Assert.IsType<ManagedBrowserEnterTransferTargetDirectoryRequest>(payload)
                    .ChildCanonicalPath;
            }
            else if (action == ManagedBrowserAction.ReadTransferTargetDirectory)
            {
                CurrentRoute = ManagedTransferRouteState.ExactTargetDirectory;
                CurrentRoutePath = Assert.IsType<ManagedBrowserReadTransferTargetDirectoryRequest>(payload)
                    .ExpectedCanonicalPath ?? "/";
            }
            else if (action == ManagedBrowserAction.OpenTransferSourceAfterTargetRead)
            {
                CurrentRoute = ManagedTransferRouteState.OriginalSource;
                CurrentRoutePath = null;
            }
        }

        private ManagedBrowserTargetInventorySnapshot TargetSnapshot(string? expectedPath)
        {
            if (expectedPath is null or "/")
                return new(Handle.BrowserId, Handle.TargetId, Handle.Endpoint,
                    ManagedIdentity.StableBaiduUserId, "target-generation", "root-id", "/", ["root-id"],
                    1, true, true, 2,
                    [new ManagedBrowserTargetInventoryItem("target-id",
                        ManagedBrowserTargetInventoryItemKind.Folder, "target")]);
            Assert.Equal("/target", expectedPath);
            return new(Handle.BrowserId, Handle.TargetId, Handle.Endpoint,
                ManagedIdentity.StableBaiduUserId, "target-generation", "target-id", "/target",
                ["root-id", "target-id"], 1, true, true, 2,
                [new ManagedBrowserTargetInventoryItem("existing-file",
                    ManagedBrowserTargetInventoryItemKind.File, "existing.txt")]);
        }
    }

    private sealed class CountingRouteProof(List<string> events,
        Func<ManagedTransferRouteProofRequest, bool> verify) : IManagedTransferRouteProof
    {
        internal int Calls;
        public Task<bool> VerifyAsync(ManagedTransferRouteProofRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            events.Add("route:" + request.ExpectedRoute.State +
                (request.ExpectedRoute.CanonicalPath is null ? string.Empty : ":" + request.ExpectedRoute.CanonicalPath));
            return Task.FromResult(verify(request));
        }
    }

    private sealed class CountingLease : IDisposable
    {
        internal int Acquisitions;
        internal int Disposals;
        internal Action? OnDisposed;
        public void Dispose()
        {
            Disposals++;
            OnDisposed?.Invoke();
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance(TimeSpan by) => now += by;
    }

    private sealed class MemoryAttemptStore(WebAutomationAttemptRecord current, List<string> events) : IWebAutomationAttemptStore
    {
        internal WebAutomationAttemptRecord Current = current;
        internal int PersistCalls;
        internal bool PersistSucceeds;
        internal TaskCompletionSource<bool>? PersistGate;
        internal readonly TaskCompletionSource PersistStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool DriftOnCleanupSecondRead;
        internal string? PersistedCorruption;
        internal int GetCalls;
        internal TaskCompletionSource<WebAutomationAttemptRecord?>? InitialReadGate;
        internal TaskCompletionSource<WebAutomationAttemptRecord?>? CleanupFirstReadGate;
        public Task<WebAutomationAttemptRecord?> GetAsync(Guid operationId, Guid accountId, int generation,
            CancellationToken cancellationToken = default)
        {
            GetCalls++;
            if (GetCalls == 1 && InitialReadGate is not null) return InitialReadGate.Task;
            if (GetCalls == 3 && CleanupFirstReadGate is not null) return CleanupFirstReadGate.Task;
            if (GetCalls > 2) events.Add(GetCalls == 3 ? "cleanup:get1" : "cleanup:get2");
            if (DriftOnCleanupSecondRead && GetCalls >= 4)
                Current = new(Current.OperationId, Current.AccountId, Current.Generation, Current.OwnerKind,
                    Current.State, Current.ResumeStage, Current.Baseline, Current.ObservationFingerprint,
                    Current.IsWaitingForUser, Current.Revision + 1, Current.CreatedAt, Current.UpdatedAt,
                    Current.AcceptedRemoteItems, Current.SourceEvidence, Current.TargetPreparationEvidence,
                    Current.SubmissionEvidence, Current.OfficialRequestId, Current.TransferIdentityMapping,
                    Current.ExecutionProfile);
            return Task.FromResult<WebAutomationAttemptRecord?>(Current);
        }
        public async Task<bool> TryPersistTransferTargetPreparationAsync(Guid operationId, Guid accountId, int generation,
            long expectedRevision, TransferTargetPreparationEvidence evidence, DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            PersistCalls++;
            events.Add("persist");
            if (PersistGate is not null)
            {
                PersistStarted.TrySetResult();
                PersistSucceeds = await PersistGate.Task.ConfigureAwait(false);
            }
            if (!PersistSucceeds || Current.Revision != expectedRevision ||
                Current.State != WebAutomationAttemptState.Preparing)
                return false;
            Current = new(Current.OperationId, Current.AccountId, Current.Generation, Current.OwnerKind,
                WebAutomationAttemptState.BaselinePersisted, null, evidence.Baseline,
                evidence.ObservationFingerprint, false, Current.Revision + 1, Current.CreatedAt, updatedAt,
                sourceEvidence: Current.SourceEvidence, targetPreparationEvidence: evidence,
                executionProfile: Current.ExecutionProfile);
            Current = PersistedCorruption switch
            {
                "execution-profile" => new(Current.OperationId, Current.AccountId, Current.Generation,
                    Current.OwnerKind, Current.State, Current.ResumeStage, Current.Baseline,
                    Current.ObservationFingerprint, Current.IsWaitingForUser, Current.Revision,
                    Current.CreatedAt, Current.UpdatedAt, Current.AcceptedRemoteItems,
                    Current.SourceEvidence, Current.TargetPreparationEvidence, Current.SubmissionEvidence,
                    Current.OfficialRequestId, Current.TransferIdentityMapping,
                    ExecutionProfileSnapshot.LegacyForAccount(Current.AccountId)),
                "attempt-observation" => new(Current.OperationId, Current.AccountId, Current.Generation,
                    Current.OwnerKind, Current.State, Current.ResumeStage, Current.Baseline,
                    new string('f', 64), Current.IsWaitingForUser, Current.Revision,
                    Current.CreatedAt, Current.UpdatedAt, Current.AcceptedRemoteItems,
                    Current.SourceEvidence, Current.TargetPreparationEvidence, Current.SubmissionEvidence,
                    Current.OfficialRequestId, Current.TransferIdentityMapping, Current.ExecutionProfile),
                "source-capability" => new(Current.OperationId, Current.AccountId, Current.Generation,
                    Current.OwnerKind, Current.State, Current.ResumeStage, Current.Baseline,
                    Current.ObservationFingerprint, Current.IsWaitingForUser, Current.Revision,
                    Current.CreatedAt, Current.UpdatedAt, Current.AcceptedRemoteItems,
                    new TransferSourceEvidence(Current.SourceEvidence!.Items,
                        Current.SourceEvidence.TotalCount, Current.SourceEvidence.ReachedEnd,
                        Current.SourceEvidence.IsStable, Current.SourceEvidence.GenerationToken,
                        Current.SourceEvidence.BaiduUserId,
                        new AdapterCapabilityVersion("BaiduTransfer", "other-protocol",
                            "selectors-v2", "synthetic-build"),
                        Current.SourceEvidence.ObservationFingerprint),
                    Current.TargetPreparationEvidence, Current.SubmissionEvidence,
                    Current.OfficialRequestId, Current.TransferIdentityMapping, Current.ExecutionProfile),
                _ => Current,
            };
            return true;
        }
        public Task<bool> BeginAsync(WebAutomationAttemptRecord attempt, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<Page<WebAutomationAttemptRecord>> GetRecoveryPageAsync(PageQuery query,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> TryPersistBaselineAsync(Guid operationId, Guid accountId, int generation,
            long expectedRevision, WebAutomationBaseline baseline, string observationFingerprint,
            DateTimeOffset updatedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> TryAdvanceAsync(Guid operationId, Guid accountId, int generation,
            WebAutomationAttemptState expectedState, long expectedRevision, WebAutomationAttemptState nextState,
            string? observationFingerprint, bool isWaitingForUser, DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> TryResumeFromUserHandoffAsync(Guid operationId, Guid accountId, int generation,
            long expectedRevision, WebAutomationAttemptState resumeStage, string? observationFingerprint,
            DateTimeOffset updatedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> TryResolveUserHandoffAsync(Guid operationId, Guid accountId, int generation,
            long expectedRevision, WebAutomationAttemptState resumeStage, WebAutomationAttemptState terminalState,
            string? observationFingerprint, DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
