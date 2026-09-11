using System.Diagnostics;
using System.Reflection;
using BaiduBatchTransfer.App.Features.BrowserAutomation;
using BaiduBatchTransfer.App.Features.ManagedBrowser;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.BaiduAdapter.WebAutomation;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Infrastructure.Database;
using BaiduBatchTransfer.Infrastructure.Security;
using static BaiduBatchTransfer.App.Tests.Features.ManagedChromiumProcessManagerTests;

namespace BaiduBatchTransfer.App.Tests.Features;

[CollectionDefinition(ManagedTransferDirectoryPreparationSessionTests.CollectionName,
    DisableParallelization = true)]
public sealed class ManagedTransferDirectoryPreparationSessionTestCollection;

[Collection(CollectionName)]
public sealed class ManagedTransferDirectoryPreparationSessionTests
{
    public const string CollectionName = "Managed transfer directory preparation isolation";
    private static readonly DateTimeOffset Now = new(2026, 8, 29, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SessionIsLeaseNeutralAndImplementsTheExistingBusinessFlowContract()
    {
        Assert.Contains(typeof(ITransferDirectoryPreparationSession),
            typeof(ManagedTransferDirectoryPreparationSession).GetInterfaces());
        Assert.False(typeof(IDisposable).IsAssignableFrom(typeof(ManagedTransferDirectoryPreparationSession)));
        Assert.False(typeof(IAsyncDisposable).IsAssignableFrom(typeof(ManagedTransferDirectoryPreparationSession)));

        var constructors = typeof(ManagedTransferDirectoryPreparationSession)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.DoesNotContain(constructors.SelectMany(constructor => constructor.GetParameters()), parameter =>
            parameter.ParameterType == typeof(Func<Guid, CancellationToken, Task<IDisposable>>));
    }

    [Fact]
    public async Task PreparedResultCarriesExactFolderCandidateHandoffFromValidatedSessionState()
    {
        using var f = await Fixture.CreateAsync("/", ("target-id", "target-private-name"));
        f.Children["/target-private-name"] = [];

        var result = await f.Run("/target-private-name");

        var handoff = Assert.IsType<ManagedPreparedDestinationEvidence>(result.PreparedDestination);
        var sourceHandoff = Assert.IsType<ManagedBrowserSourceSnapshot>(result.ValidatedSourceSnapshot);
        Assert.Equal(TransferDirectoryPreparationStatus.Prepared, result.Outcome.Status);
        Assert.Equal(f.Identity.OperationId, handoff.OperationId);
        Assert.Equal(f.Identity.AccountId, handoff.AccountId);
        Assert.Equal(f.Identity.Generation, handoff.Generation);
        Assert.Equal(f.Store.Current.Revision, handoff.OwningAttemptRevision);
        Assert.Equal(BaiduExecutionEngine.ManagedChromiumV1, handoff.ExecutionEngine);
        Assert.Equal(f.Capability, handoff.Capability);
        Assert.Equal(f.Store.Current.SourceEvidence!.Items.Fingerprint, handoff.SourceFingerprint);
        Assert.Equal(f.Store.Current.SourceEvidence!.ObservationFingerprint,
            handoff.SourceObservationFingerprint);
        Assert.Equal(result.Outcome.Directory, handoff.Destination);
        Assert.Same(f.DirectoryEvidence[^1].Snapshot, handoff.FolderCandidatesSnapshot);
        Assert.Equal(new[] { "root-id", "target-id" },
            handoff.FolderCandidatesSnapshot.AncestorExactIds);
        Assert.Equal(TransferDirectoryListingScope.FolderCandidates, handoff.Scope);
        Assert.Equal(f.Store.Current.SourceEvidence!.ObservationFingerprint,
            sourceHandoff.ObservationFingerprint);
        Assert.Equal(f.Store.Current.SourceEvidence.BaiduUserId, sourceHandoff.BaiduUserId);
        Assert.Equal(f.Store.Current.SourceEvidence.Items.Items.Select(item => item.ExactId),
            sourceHandoff.Items.Select(item => item.ExactId));
        Assert.DoesNotContain(typeof(TransferTargetPreparationEvidence),
            typeof(ManagedPreparedDestinationEvidence).GetProperties(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Select(property => property.PropertyType));
        Assert.Empty(typeof(ManagedPreparedDestinationEvidence).GetConstructors(BindingFlags.Instance |
            BindingFlags.Public));

        var diagnostic = result + " " + handoff;
        Assert.DoesNotContain("target-id", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("target-private-name", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(f.Store.Current.SourceEvidence.ObservationFingerprint, diagnostic,
            StringComparison.Ordinal);
        Assert.DoesNotContain(f.Store.Current.SourceEvidence.Items.Fingerprint, diagnostic,
            StringComparison.Ordinal);
        Assert.DoesNotContain(f.ManagedIdentity.StableBaiduUserId, diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NeedsAttentionHasNoPreparedDestinationAndResultRejectsStatusHandoffMismatch()
    {
        using var f = await Fixture.CreateAsync("/", ("target-id", "target"));
        f.Children["/target"] = [];
        var prepared = await f.Run("/target");
        var handoff = Assert.IsType<ManagedPreparedDestinationEvidence>(prepared.PreparedDestination);
        var needsAttention = TransferDirectoryPreparationOutcome.NeedsAttention(
            new RemotePath("/"), BaiduErrorKind.NotValidated);

        Assert.Throws<ArgumentException>(() => new ManagedTransferDirectoryPreparationResult(needsAttention,
            preparedDestination: handoff, validatedSourceSnapshot: prepared.ValidatedSourceSnapshot));
        Assert.Throws<ArgumentException>(() => new ManagedTransferDirectoryPreparationResult(
            TransferDirectoryPreparationOutcome.Prepared(prepared.Outcome.Directory!)));

        f.Store.RejectBeginMutation = true;
        var denied = await f.Run("/missing");
        Assert.Equal(TransferDirectoryPreparationStatus.NeedsAttention, denied.Outcome.Status);
        Assert.Null(denied.PreparedDestination);
        Assert.Null(denied.ValidatedSourceSnapshot);
    }

    [Fact]
    public async Task PreparedResultCannotCarryDirectoryPreparationQuarantine()
    {
        using var preparedFixture = await Fixture.CreateAsync("/", ("target-id", "target"));
        preparedFixture.Children["/target"] = [];
        var prepared = await preparedFixture.Run("/target");
        var handoff = Assert.IsType<ManagedPreparedDestinationEvidence>(prepared.PreparedDestination);

        using var quarantinedFixture = await Fixture.CreateAsync("/");
        quarantinedFixture.Store.ThrowBeginMutation = true;
        quarantinedFixture.UnknownAction = ManagedBrowserAction.CancelTransferPicker;
        quarantinedFixture.UnknownActionClosesPicker = true;
        var quarantined = await quarantinedFixture.Run("/missing");
        var quarantine = Assert.IsType<ManagedTransferDirectoryPreparationQuarantine>(quarantined.Quarantine);

        Assert.Throws<ArgumentException>(() => new ManagedTransferDirectoryPreparationResult(
            prepared.Outcome, quarantine, handoff));
    }

    [Theory]
    [InlineData("outcome")]
    [InlineData("revision")]
    [InlineData("engine")]
    [InlineData("capability")]
    [InlineData("source")]
    [InlineData("source-name")]
    [InlineData("operation")]
    [InlineData("account")]
    [InlineData("generation")]
    [InlineData("destination")]
    public async Task PreparedDestinationFactoryRejectsMismatchedValidatedState(string mismatch)
    {
        using var f = await Fixture.CreateAsync("/", ("target-id", "target"));
        f.Children["/target"] = [];
        var result = await f.Run("/target");
        var identity = f.Identity;
        var execution = BaiduExecutionContext.Managed(f.ManagedIdentity);
        var request = f.Request("/target");
        var attempt = f.Store.Current;
        var validatedSource = f.SourceEvidence();
        var owningRevision = attempt.Revision;
        var outcome = result.Outcome;
        var finalEvidence = f.DirectoryEvidence[^1];

        switch (mismatch)
        {
            case "outcome":
                outcome = TransferDirectoryPreparationOutcome.NeedsAttention(
                    new RemotePath("/target"), BaiduErrorKind.NotValidated);
                break;
            case "revision":
                owningRevision++;
                break;
            case "engine":
                identity = identity with { PersistedExecutionProfile = ExecutionProfileSnapshot.Legacy("legacy") };
                break;
            case "capability":
                request = request with { Capability = new("BaiduTransfer", "wrong", "wrong", "wrong") };
                break;
            case "source":
                attempt = f.CopyAttemptWithSource(new(attempt.SourceEvidence!.Items,
                    attempt.SourceEvidence.TotalCount, true, true, attempt.SourceEvidence.GenerationToken,
                    attempt.SourceEvidence.BaiduUserId, attempt.SourceEvidence.Capability, new string('a', 64)));
                break;
            case "source-name":
                attempt = f.CopyAttemptWithSource(new(
                    new RemoteItemIdentitySet([new RemoteItemIdentity("source-id", "changed-source-name")]),
                    attempt.SourceEvidence!.TotalCount, true, true, attempt.SourceEvidence.GenerationToken,
                    attempt.SourceEvidence.BaiduUserId, attempt.SourceEvidence.Capability,
                    attempt.SourceEvidence.ObservationFingerprint));
                break;
            case "operation":
                request = request with { OperationId = Guid.NewGuid() };
                break;
            case "account":
                request = request with { AccountId = Guid.NewGuid() };
                break;
            case "generation":
                request = request with { Generation = request.Generation + 1 };
                break;
            case "destination":
                outcome = TransferDirectoryPreparationOutcome.Prepared(
                    new RemoteDirectory("other-id", new RemotePath("/target")));
                break;
        }

        Assert.Throws<InvalidOperationException>(() =>
            ManagedPreparedDestinationEvidence.CreateFromValidatedState(identity, execution, request,
                attempt, validatedSource, owningRevision, outcome, finalEvidence));
    }

    [Fact]
    public void IncompleteFolderCandidateWireSnapshotCannotBecomePreparedDestinationEvidence()
    {
        Assert.Throws<ArgumentException>(() => new ManagedBrowserDirectorySnapshot(
            "uid", "generation", "root-id", "/", ["root-id"], 0,
            reachedEnd: false, isStable: true, [], new string('0', 64)));
    }

    [Fact]
    public async Task ExistingNestedPathUsesExactIdsWithNoCreateUnderOneLease()
    {
        using var f = await Fixture.CreateAsync("/", ("alpha-id", "甲"));
        f.Children["/甲"] = [new("beta-id", "乙")];
        f.Children["/甲/乙"] = [];

        var result = await f.Run("/甲/乙");

        Assert.True(result.Outcome.Status == TransferDirectoryPreparationStatus.Prepared,
            $"Outcome={result.Outcome.Status}; Error={result.Outcome.ErrorKind}; GetAttempt={f.Store.GetAttemptCalls}; Permits={f.Authorization.Acquired}; Requests={string.Join(',', f.Requests.Select(request => request.Action))}");
        Assert.Equal("beta-id", result.Outcome.Directory!.RemoteId);
        Assert.Null(result.Quarantine);
        Assert.Equal(new[] { "alpha-id", "beta-id" }, f.EnteredIds);
        Assert.DoesNotContain(f.Requests, request => request.Action is
            ManagedBrowserAction.BeginCreateTransferDirectory or ManagedBrowserAction.ConfirmCreateTransferDirectory);
        Assert.All(f.DirectoryEvidence, evidence =>
            Assert.Equal(evidence.Snapshot.AncestorExactIds.Skip(1), evidence.FolderCandidates.AncestorExactIds));
        using var reacquired = await f.Lifecycle.AcquireAccountLeaseAsync(f.Identity.AccountId, default)
            .WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ExactIssuedBindingCanBeginHeldPreparationBeforeAnyHelperDispatch()
    {
        using var f = await Fixture.CreateAsync("/");

        var result = await new ManagedTransferPickerSession(f.Lifecycle, f.Manager,
            (_, _) => throw new InvalidOperationException("helper must not run"), f.Authorization, f.Clock)
            .BeginHeldPreparationAsync(f.Identity, f.CandidateBinding,
                BaiduExecutionContext.Managed(f.ManagedIdentity), f.Handle, Fixture.SourceUri, 3000, default);

        Assert.Equal(ManagedTransferPickerHeldPreparationKind.ReadyNotOpened, result.Kind);
        Assert.NotNull(result.Preparation);
        Assert.Empty(f.Requests);
        result.Preparation!.ReleaseIfUnconsumed();
    }

    [Fact]
    public async Task FixtureSourceSessionCanOpenAndReadSyntheticEvidence()
    {
        using var f = await Fixture.CreateAsync("/");

        var result = await new ManagedTransferSourceSession(f.Lifecycle, f.Manager, f.SendForTest)
            .OpenAndReadAsync(f.Identity.OperationId, f.Identity.Generation,
                BaiduExecutionContext.Managed(f.ManagedIdentity), f.Handle, Fixture.SourceUri, 3000);

        Assert.Equal(ManagedBrowserSourceListingResultKind.Observed, result.Kind);
    }

    [Fact]
    public async Task MissingSegmentsPersistIntentBeforeBeginAndConfirmThenResolveConfirmed()
    {
        using var f = await Fixture.CreateAsync("/");

        var result = await f.Run("/甲/乙");

        Assert.True(result.Outcome.Status == TransferDirectoryPreparationStatus.Prepared,
            $"Outcome={result.Outcome.Status}; Requests={string.Join(',', f.Requests.Select(request => request.Action))}");
        Assert.Equal(2, f.Store.Mutations.Count);
        Assert.All(f.Store.Mutations, mutation => Assert.Equal(TransferDirectoryMutationState.Confirmed, mutation.State));
        Assert.Equal(new[] { "甲", "乙" }, f.BegunSegments);
        Assert.Equal(new[] { "甲", "乙" }, f.ConfirmedSegments);
        Assert.True(f.IntentWasPresentBeforeEveryCreateAction);
        Assert.Null(result.Quarantine);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StoreFailureAfterPickerUsesProvenCancelBeforeLeaseRelease(bool throws)
    {
        using var f = await Fixture.CreateAsync("/");
        f.Store.ThrowBeginMutation = throws;
        f.Store.RejectBeginMutation = !throws;

        var result = await f.Run("/目标");

        Assert.Equal(TransferDirectoryPreparationStatus.NeedsAttention, result.Outcome.Status);
        Assert.Null(result.Quarantine);
        Assert.Equal(1, f.Requests.Count(request => request.Action == ManagedBrowserAction.CancelTransferPicker));
        Assert.Equal(1, f.Requests.Count(request => request.Action == ManagedBrowserAction.ObserveTransferPickerClosed));
        using var reacquired = await f.Lifecycle.AcquireAccountLeaseAsync(f.Identity.AccountId, default)
            .WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task StoreFailureAfterPickerWithUncertainCleanupReturnsOpaqueQuarantine()
    {
        using var f = await Fixture.CreateAsync("/");
        f.Store.ThrowBeginMutation = true;
        f.UnknownAction = ManagedBrowserAction.CancelTransferPicker;
        f.UnknownActionClosesPicker = true;

        var result = await f.Run("/目标");

        Assert.IsType<ManagedTransferDirectoryPreparationQuarantine>(result.Quarantine);
        Assert.Equal(1, f.Requests.Count(request => request.Action == ManagedBrowserAction.CancelTransferPicker));
        Assert.False(f.Lifecycle.AcquireAccountLeaseAsync(f.Identity.AccountId, default).IsCompleted);
    }

    [Fact]
    public async Task PersistedSourceStillOpensAndRevalidatesExactSourceBeforePicker()
    {
        using var f = await Fixture.CreateAsync("/", ("target-id", "目标"));
        f.Children["/目标"] = [];
        f.Store.PersistSource(f.SourceEvidence());

        var result = await f.Run("/目标");

        Assert.Equal(TransferDirectoryPreparationStatus.Prepared, result.Outcome.Status);
        var openSource = f.Requests.FindIndex(request => request.Action == ManagedBrowserAction.OpenTransferSource);
        var openPicker = f.Requests.FindIndex(request => request.Action == ManagedBrowserAction.OpenTransferPicker);
        Assert.InRange(openSource, 0, openPicker - 1);
        Assert.Equal(1, f.Requests.Count(request => request.Action == ManagedBrowserAction.OpenTransferSource));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("name")]
    [InlineData("generation")]
    [InlineData("fingerprint")]
    [InlineData("uid")]
    [InlineData("count")]
    [InlineData("incomplete")]
    [InlineData("unstable")]
    [InlineData("capability")]
    public async Task PersistedSourceExactMismatchDispatchesNoPickerMutation(string change)
    {
        using var f = await Fixture.CreateAsync("/", ("target-id", "目标"));
        f.Children["/目标"] = [];
        var persisted = f.SourceEvidence();
        if (change == "capability")
            persisted = new(persisted.Items, persisted.TotalCount, true, true, persisted.GenerationToken,
                persisted.BaiduUserId, new("BaiduTransfer", "different", "selectors", "synthetic-build"),
                persisted.ObservationFingerprint);
        f.Store.PersistSource(persisted);
        f.SourceChange = change;

        var result = await f.Run("/目标");

        Assert.Equal(TransferDirectoryPreparationStatus.NeedsAttention, result.Outcome.Status);
        Assert.DoesNotContain(f.Requests, request => request.Action == ManagedBrowserAction.OpenTransferPicker);
        Assert.Equal(0, f.Authorization.Acquired);
        Assert.Null(result.Quarantine);
    }

    [Fact]
    public async Task PersistedSourceAppearingAfterInitialReadIsStillExactlyRevalidated()
    {
        using var f = await Fixture.CreateAsync("/");
        f.Store.PersistSource(f.SourceEvidence());
        f.Store.FirstAttemptReadOmitsSource = true;
        f.SourceChange = "id";

        var result = await f.Run("/目标");

        Assert.Equal(TransferDirectoryPreparationStatus.NeedsAttention, result.Outcome.Status);
        Assert.True(f.Store.GetAttemptCalls >= 2);
        Assert.DoesNotContain(f.Requests, request => request.Action == ManagedBrowserAction.OpenTransferPicker);
        Assert.Equal(0, f.Authorization.Acquired);
    }

    [Theory]
    [InlineData("durable")]
    [InlineData("target")]
    public async Task PickerListingIsNotPublishedWhenFinalExactProofChanges(string failure)
    {
        using var f = await Fixture.CreateAsync("/", ("target-id", "目标"));
        f.Children["/目标"] = [];
        f.AfterPickerDirectoryRead = async () =>
        {
            if (failure == "durable")
                await f.Execute("UPDATE ManagedBrowserSessions SET Revision=Revision+1;");
            else
                f.TargetProbe.TargetId = "different-tab";
        };

        var result = await f.Run("/目标");

        Assert.Equal(TransferDirectoryPreparationStatus.NeedsAttention, result.Outcome.Status);
        Assert.NotNull(result.Quarantine);
        Assert.Equal(1, f.Requests.Count(request => request.Action == ManagedBrowserAction.OpenTransferPicker));
        Assert.DoesNotContain(f.Requests, request => request.Action == ManagedBrowserAction.EnterTransferDirectory);
    }

    [Fact]
    public async Task AccountLeaseWaitUsesTheOperationDeadlineAndDispatchesNothing()
    {
        using var f = await Fixture.CreateAsync("/");
        using var heldLease = await f.Lifecycle.AcquireAccountLeaseAsync(f.Identity.AccountId, default);

        var result = await f.Run("/目标", default, 100).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(TransferDirectoryPreparationStatus.NeedsAttention, result.Outcome.Status);
        Assert.Empty(f.Requests);
        Assert.Null(result.Quarantine);
    }

    [Fact]
    public async Task HeldPreparationReportsTypedTimeoutWhileWaitingForAccountLease()
    {
        using var f = await Fixture.CreateAsync("/");
        using var heldLease = await f.Lifecycle.AcquireAccountLeaseAsync(f.Identity.AccountId, default);

        var result = await new ManagedTransferPickerSession(f.Lifecycle, f.Manager, f.SendForTest, f.Authorization, f.Clock)
            .BeginHeldPreparationAsync(f.Identity, f.CandidateBinding,
                BaiduExecutionContext.Managed(f.ManagedIdentity), f.Handle, Fixture.SourceUri, 100, default)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(ManagedTransferPickerHeldPreparationKind.TimedOut, result.Kind);
        Assert.Null(result.Preparation);
        Assert.Empty(f.Requests);
    }

    [Fact]
    public async Task PrevalidationStoreWaitIsBoundedByTheSameOperationDeadline()
    {
        using var f = await Fixture.CreateAsync("/");
        f.Store.AttemptGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        var result = await f.Run("/目标", default, 100).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(TransferDirectoryPreparationStatus.NeedsAttention, result.Outcome.Status);
        Assert.Empty(f.Requests);
        Assert.Null(result.Quarantine);
        f.Store.AttemptGate.SetResult(f.Store.Current);
    }

    [Fact]
    public async Task CandidateBindingExpiryWhileWaitingForLeaseDispatchesNothing()
    {
        using var f = await Fixture.CreateAsync("/");
        f.CandidateBinding = f.NewCandidateBinding(f.Clock.GetUtcNow().AddMilliseconds(250));
        using var heldLease = await f.Lifecycle.AcquireAccountLeaseAsync(f.Identity.AccountId, default);

        var run = f.Run("/目标", default, 2000);
        f.Clock.Advance(TimeSpan.FromMilliseconds(350));
        heldLease.Dispose();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(TransferDirectoryPreparationStatus.NeedsAttention, result.Outcome.Status);
        Assert.Empty(f.Requests);
        Assert.Equal(0, f.Authorization.Acquired);
        Assert.Null(result.Quarantine);
    }

    [Fact]
    public async Task CandidateBindingExpiryDuringPermitWaitCausesZeroDirectoryDispatch()
    {
        using var f = await Fixture.CreateAsync("/", ("target-id", "目标"));
        f.Children["/目标"] = [];
        f.CandidateBinding = f.NewCandidateBinding(f.Clock.GetUtcNow().AddMilliseconds(500));
        f.Authorization.GatedAction = BaiduAutomationAction.EnterDirectory;
        f.Authorization.AcquisitionGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        var run = f.Run("/目标", default, 3000);
        await f.Authorization.AcquisitionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        f.Clock.Advance(TimeSpan.FromMilliseconds(800));
        f.Authorization.AcquisitionGate.SetResult();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.DoesNotContain(f.Requests, request => request.Action == ManagedBrowserAction.EnterTransferDirectory);
        Assert.NotNull(result.Quarantine);
        Assert.Equal(f.Authorization.Acquired, f.Authorization.Disposed);
    }

    [Fact]
    public async Task UnrelatedCurrentDirectoryReturnsThroughTheProvenRootAction()
    {
        using var f = await Fixture.CreateAsync("/别处", ("irrelevant", "其它"));
        f.Children["/"] = [new("outside-id", "别处"), new("target-id", "目标")];
        f.Children["/目标"] = [];

        var result = await f.Run("/目标");

        Assert.True(result.Outcome.Status == TransferDirectoryPreparationStatus.Prepared,
            $"Outcome={result.Outcome.Status}; Requests={string.Join(',', f.Requests.Select(request => request.Action))}");
        Assert.Contains(f.Requests, request => request.Action == ManagedBrowserAction.OpenTransferDirectoryRoot);
        var rootRequest = Assert.IsType<ManagedBrowserDirectoryMutationRequest>(f.Requests
            .Single(request => request.Action == ManagedBrowserAction.OpenTransferDirectoryRoot).Payload);
        Assert.Equal("root-id", rootRequest.RootBreadcrumbExactId);
    }

    [Fact]
    public async Task DuplicateSameNameFoldersFailClosedWithoutEntryOrCreate()
    {
        using var f = await Fixture.CreateAsync("/", ("one", "目标"), ("two", "目标"));

        var result = await f.Run("/目标");

        Assert.Equal(TransferDirectoryPreparationStatus.NeedsAttention, result.Outcome.Status);
        Assert.Empty(f.EnteredIds);
        Assert.Empty(f.BegunSegments);
        Assert.Null(result.Quarantine);
        using var reacquired = await f.Lifecycle.AcquireAccountLeaseAsync(f.Identity.AccountId, default)
            .WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task PossibleConfirmDispatchPersistsOutcomeUnknownAndQuarantinesWithoutRetry()
    {
        using var f = await Fixture.CreateAsync("/");
        f.UnknownAction = ManagedBrowserAction.ConfirmCreateTransferDirectory;

        var result = await f.Run("/目标");

        Assert.Equal(TransferDirectoryPreparationStatus.NeedsAttention, result.Outcome.Status);
        Assert.Equal(TransferDirectoryMutationState.OutcomeUnknown, Assert.Single(f.Store.Mutations).State);
        var quarantine = Assert.IsType<ManagedTransferDirectoryPreparationQuarantine>(result.Quarantine);
        Assert.Equal(1, f.Requests.Count(request => request.Action == ManagedBrowserAction.ConfirmCreateTransferDirectory));
        Assert.False(f.Lifecycle.AcquireAccountLeaseAsync(f.Identity.AccountId, default).IsCompleted);
        Assert.DoesNotContain("synthetic-user", result.ToString());
        Assert.DoesNotContain(Fixture.SourceUri, quarantine.ToString());
    }

    [Fact]
    public async Task InvalidIdentityOrExpiredCandidateBindingAcquiresNoAccountLeaseAndDispatchesNothing()
    {
        using var f = await Fixture.CreateAsync("/");
        f.CandidateBinding = f.NewCandidateBinding(f.Clock.GetUtcNow().AddMinutes(-1));

        var result = await f.Run("/目标");

        Assert.Equal(TransferDirectoryPreparationStatus.NeedsAttention, result.Outcome.Status);
        Assert.Empty(f.Requests);
        Assert.Equal(0, f.Store.GetAttemptCalls);
        using var reacquired = await f.Lifecycle.AcquireAccountLeaseAsync(f.Identity.AccountId, default)
            .WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData("root", ManagedBrowserAction.OpenTransferDirectoryRoot)]
    [InlineData("enter", ManagedBrowserAction.EnterTransferDirectory)]
    [InlineData("begin", ManagedBrowserAction.BeginCreateTransferDirectory)]
    [InlineData("confirm", ManagedBrowserAction.ConfirmCreateTransferDirectory)]
    [InlineData("cancel", ManagedBrowserAction.CancelTransferPicker)]
    public async Task PossibleDispatchAtEachMutationBoundaryRequiresActionSpecificQuarantine(
        string boundary, ManagedBrowserAction action)
    {
        using var f = boundary == "root"
            ? await Fixture.CreateAsync("/别处")
            : boundary == "enter"
                ? await Fixture.CreateAsync("/", ("target-id", "目标"))
                : await Fixture.CreateAsync("/");
        if (boundary == "root")
        {
            f.Children["/"] = [new("outside-id", "别处"), new("target-id", "目标")];
            f.Children["/目标"] = [];
        }
        if (boundary == "enter") f.Children["/目标"] = [];
        f.UnknownAction = action;
        f.UnknownActionClosesPicker = boundary == "cancel";

        var result = await f.Run("/目标");

        var quarantine = Assert.IsType<ManagedTransferDirectoryPreparationQuarantine>(result.Quarantine);
        Assert.Equal(1, f.Requests.Count(request => request.Action == action));
        var next = f.Lifecycle.AcquireAccountLeaseAsync(f.Identity.AccountId, default);
        Assert.False(next.IsCompleted);
        var mutationCount = f.Requests.Count(request => request.Action == action);
        var status = await quarantine.ReconcileAsync(2000, default);
        Assert.Equal(ManagedTransferDirectoryPreparationReconciliationStatus.Released, status);
        Assert.Equal(mutationCount, f.Requests.Count(request => request.Action == action));
        if (boundary is "begin" or "confirm")
            Assert.All(f.Store.Mutations, mutation =>
                Assert.Equal(TransferDirectoryMutationState.OutcomeUnknown, mutation.State));
        using var reacquired = await next.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData("picker", ManagedBrowserAction.OpenTransferPicker)]
    [InlineData("root", ManagedBrowserAction.OpenTransferDirectoryRoot)]
    [InlineData("enter", ManagedBrowserAction.EnterTransferDirectory)]
    [InlineData("begin", ManagedBrowserAction.BeginCreateTransferDirectory)]
    [InlineData("confirm", ManagedBrowserAction.ConfirmCreateTransferDirectory)]
    [InlineData("cancel", ManagedBrowserAction.CancelTransferPicker)]
    public async Task StoreFailureCapturingAnyUncertainActionIdentityRetainsOpaqueLease(
        string boundary, ManagedBrowserAction action)
    {
        using var f = boundary == "root"
            ? await Fixture.CreateAsync("/别处")
            : boundary == "enter"
                ? await Fixture.CreateAsync("/", ("target-id", "目标"))
                : await Fixture.CreateAsync("/");
        if (boundary == "root")
        {
            f.Children["/"] = [new("outside-id", "别处"), new("target-id", "目标")];
            f.Children["/目标"] = [];
        }
        if (boundary == "enter") f.Children["/目标"] = [];
        f.UnknownAction = action;
        f.UnknownActionClosesPicker = boundary == "cancel";
        f.ThrowStoreAfterUnknownAction = true;

        var result = await f.Run("/目标");

        var quarantine = Assert.IsType<ManagedTransferDirectoryPreparationQuarantine>(result.Quarantine);
        Assert.Equal(1, f.Requests.Count(request => request.Action == action));
        var next = f.Lifecycle.AcquireAccountLeaseAsync(f.Identity.AccountId, default);
        Assert.False(next.IsCompleted);
        Assert.Equal(ManagedTransferDirectoryPreparationReconciliationStatus.RetainedInconclusive,
            await quarantine.ReconcileAsync(2000, default));
        Assert.False(next.IsCompleted);
    }

    [Fact]
    public async Task ConfirmedCreateFollowedByKnownDuplicateFailureCancelsAndReleases()
    {
        using var f = await Fixture.CreateAsync("/");
        f.DuplicateNextSegmentAfterConfirmedCreate = true;

        var result = await f.Run("/甲/乙");

        Assert.Equal(TransferDirectoryPreparationStatus.NeedsAttention, result.Outcome.Status);
        Assert.Null(result.Quarantine);
        Assert.Equal(TransferDirectoryMutationState.Confirmed, Assert.Single(f.Store.Mutations).State);
        Assert.Equal(1, f.Requests.Count(request => request.Action == ManagedBrowserAction.CancelTransferPicker));
        Assert.Equal(1, f.Requests.Count(request => request.Action == ManagedBrowserAction.ObserveTransferPickerClosed));
        using var reacquired = await f.Lifecycle.AcquireAccountLeaseAsync(f.Identity.AccountId, default)
            .WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ConfirmedCreateFollowedByPreDispatchEnterDenialCancelsAndReleases()
    {
        using var f = await Fixture.CreateAsync("/");
        f.Authorization.DeniedAction = BaiduAutomationAction.EnterDirectory;

        var result = await f.Run("/甲");

        Assert.Equal(TransferDirectoryPreparationStatus.NeedsAttention, result.Outcome.Status);
        Assert.Null(result.Quarantine);
        Assert.Equal(TransferDirectoryMutationState.Confirmed, Assert.Single(f.Store.Mutations).State);
        Assert.DoesNotContain(f.Requests, request => request.Action == ManagedBrowserAction.EnterTransferDirectory);
        Assert.Equal(1, f.Requests.Count(request => request.Action == ManagedBrowserAction.CancelTransferPicker));
        Assert.Equal(1, f.Requests.Count(request => request.Action == ManagedBrowserAction.ObserveTransferPickerClosed));
        using var reacquired = await f.Lifecycle.AcquireAccountLeaseAsync(f.Identity.AccountId, default)
            .WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData("root", ManagedBrowserAction.OpenTransferDirectoryRoot)]
    [InlineData("enter", ManagedBrowserAction.EnterTransferDirectory)]
    public async Task ReconciliationAcceptsExactAppliedSideEffectAfterOutcomeUnknownWithoutRetry(
        string boundary, ManagedBrowserAction action)
    {
        using var f = boundary == "root"
            ? await Fixture.CreateAsync("/别处")
            : await Fixture.CreateAsync("/", ("target-id", "目标"));
        if (boundary == "root")
        {
            f.Children["/"] = [new("outside-id", "别处"), new("target-id", "目标")];
            f.Children["/目标"] = [];
        }
        else
        {
            f.Children["/目标"] = [];
        }
        f.UnknownAction = action;
        f.ApplyUnknownActionSideEffect = true;

        var result = await f.Run("/目标");
        var quarantine = Assert.IsType<ManagedTransferDirectoryPreparationQuarantine>(result.Quarantine);
        var dispatched = f.Requests.Count(request => request.Action == action);

        var status = await quarantine.ReconcileAsync(2000, default);

        Assert.Equal(ManagedTransferDirectoryPreparationReconciliationStatus.Released, status);
        Assert.Equal(1, dispatched);
        Assert.Equal(dispatched, f.Requests.Count(request => request.Action == action));
        using var reacquired = await f.Lifecycle.AcquireAccountLeaseAsync(f.Identity.AccountId, default)
            .WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData("root", BaiduAutomationAction.EnterDirectory, ManagedBrowserAction.OpenTransferDirectoryRoot)]
    [InlineData("enter", BaiduAutomationAction.EnterDirectory, ManagedBrowserAction.EnterTransferDirectory)]
    [InlineData("begin", BaiduAutomationAction.CreateDirectory, ManagedBrowserAction.BeginCreateTransferDirectory)]
    [InlineData("confirm", BaiduAutomationAction.ConfirmDirectoryCreation, ManagedBrowserAction.ConfirmCreateTransferDirectory)]
    [InlineData("cancel", BaiduAutomationAction.CancelDestinationPicker, ManagedBrowserAction.CancelTransferPicker)]
    public async Task ProofFailureImmediatelyBeforeClickDispatchesZeroForEveryBoundary(
        string boundary, BaiduAutomationAction permitAction, ManagedBrowserAction helperAction)
    {
        using var f = boundary == "root"
            ? await Fixture.CreateAsync("/别处")
            : boundary == "enter"
                ? await Fixture.CreateAsync("/", ("target-id", "目标"))
                : await Fixture.CreateAsync("/");
        if (boundary == "root")
        {
            f.Children["/"] = [new("outside-id", "别处"), new("target-id", "目标")];
            f.Children["/目标"] = [];
        }
        if (boundary == "enter") f.Children["/目标"] = [];
        f.Authorization.ProofChangeAction = permitAction;
        f.Authorization.AfterAcquisition = () =>
            f.Execute("UPDATE ManagedBrowserSessions SET Revision=Revision+1;");

        var result = await f.Run("/目标");

        Assert.DoesNotContain(f.Requests, request => request.Action == helperAction);
        Assert.NotNull(result.Quarantine);
        Assert.Equal(f.Authorization.Acquired, f.Authorization.Disposed);
    }

    [Fact]
    public async Task RefreshedReadFailureAfterConfirmQuarantinesAndNeverRetriesTheWrite()
    {
        using var f = await Fixture.CreateAsync("/");
        f.FailRefreshedReadAfterConfirm = true;

        var result = await f.Run("/目标");

        Assert.NotNull(result.Quarantine);
        Assert.Equal(TransferDirectoryMutationState.OutcomeUnknown, Assert.Single(f.Store.Mutations).State);
        Assert.Equal(1, f.Requests.Count(request => request.Action == ManagedBrowserAction.ConfirmCreateTransferDirectory));
    }

    [Fact]
    public async Task FailedDurableConfirmationRemainsQuarantinedAndCannotBeReleasedByPickerCancelAlone()
    {
        using var f = await Fixture.CreateAsync("/");
        f.Store.FailConfirmedResolution = true;

        var result = await f.Run("/目标");

        var quarantine = Assert.IsType<ManagedTransferDirectoryPreparationQuarantine>(result.Quarantine);
        Assert.Equal(TransferDirectoryMutationState.Intent, Assert.Single(f.Store.Mutations).State);
        Assert.Equal(ManagedTransferDirectoryPreparationReconciliationStatus.RetainedInconclusive,
            await quarantine.ReconcileAsync(2000, default));
        Assert.False(f.Lifecycle.AcquireAccountLeaseAsync(f.Identity.AccountId, default).IsCompleted);
    }

    [Theory]
    [InlineData(ManagedBrowserAction.OpenTransferSource)]
    [InlineData(ManagedBrowserAction.ReadTransferSource)]
    public async Task CancellationDuringNonCooperativeSourceSendRetainsLeaseUntilSettlement(
        ManagedBrowserAction pendingAction)
    {
        using var f = await Fixture.CreateAsync("/");
        f.PendingAction = pendingAction;
        f.PendingResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancelled = new CancellationTokenSource();

        var run = f.Run("/目标", cancelled.Token, 5000);
        await f.PendingActionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancelled.Cancel();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(2));
        var next = f.Lifecycle.AcquireAccountLeaseAsync(f.Identity.AccountId, default);

        Assert.Equal(TransferDirectoryPreparationStatus.NeedsAttention, result.Outcome.Status);
        Assert.Null(result.Quarantine);
        Assert.False(next.IsCompleted);
        f.PendingResponse.SetException(new IOException("private late source failure"));
        using var reacquired = await next.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task CancellationBeforeDirectoryDispatchSettlesPermitButRetainsOpenPickerQuarantine()
    {
        using var f = await Fixture.CreateAsync("/", ("target-id", "目标"));
        f.Children["/目标"] = [];
        f.Authorization.GatedAction = BaiduAutomationAction.EnterDirectory;
        f.Authorization.AcquisitionGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancelled = new CancellationTokenSource();

        var run = f.Run("/目标", cancelled.Token);
        await f.Authorization.AcquisitionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancelled.Cancel();
        var next = f.Lifecycle.AcquireAccountLeaseAsync(f.Identity.AccountId, default);
        Assert.False(next.IsCompleted);
        Assert.False(run.IsCompleted);

        f.Authorization.AcquisitionGate.SetResult();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(result.Quarantine);
        Assert.False(next.IsCompleted);
        for (var attempt = 0; attempt < 20 && f.Authorization.Acquired != f.Authorization.Disposed; attempt++)
            await Task.Delay(10);
        Assert.Equal(f.Authorization.Acquired, f.Authorization.Disposed);
        Assert.DoesNotContain(f.EnteredIds, id => id == "target-id");
    }

    [Fact]
    public async Task TimeoutBeforeDirectoryDispatchReturnsAtDeadlineAndRetainsOpenPickerQuarantine()
    {
        using var f = await Fixture.CreateAsync("/", ("target-id", "目标"));
        f.Children["/目标"] = [];
        f.Authorization.GatedAction = BaiduAutomationAction.EnterDirectory;
        f.Authorization.AcquisitionGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        var run = f.Run("/目标", default, 300);
        await f.Authorization.AcquisitionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(400);
        var next = f.Lifecycle.AcquireAccountLeaseAsync(f.Identity.AccountId, default);
        Assert.False(next.IsCompleted);
        Assert.True(run.IsCompleted);

        f.Authorization.AcquisitionGate.SetResult();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(result.Quarantine);
        Assert.False(next.IsCompleted);
        for (var attempt = 0; attempt < 20 && f.Authorization.Acquired != f.Authorization.Disposed; attempt++)
            await Task.Delay(10);
        Assert.Equal(f.Authorization.Acquired, f.Authorization.Disposed);
        Assert.Empty(f.EnteredIds);
    }

    [Fact]
    public async Task CancellationAfterPossibleDispatchPersistsOutcomeUnknownPastOuterDeadline()
    {
        using var f = await Fixture.CreateAsync("/");
        f.PendingAction = ManagedBrowserAction.ConfirmCreateTransferDirectory;
        f.PendingResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancelled = new CancellationTokenSource();

        var run = f.Run("/目标", cancelled.Token);
        await f.PendingActionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancelled.Cancel();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(2));
        var quarantine = Assert.IsType<ManagedTransferDirectoryPreparationQuarantine>(result.Quarantine);
        Assert.Equal(TransferDirectoryMutationState.OutcomeUnknown, Assert.Single(f.Store.Mutations).State);
        var next = f.Lifecycle.AcquireAccountLeaseAsync(f.Identity.AccountId, default);
        Assert.False(next.IsCompleted);

        f.PendingResponse.SetException(new IOException("private late synthetic failure"));
        Assert.Equal(ManagedTransferDirectoryPreparationReconciliationStatus.RetainedInconclusive,
            await quarantine.ReconcileAsync(2000, default));
        Assert.False(next.IsCompleted);
        Assert.Equal(1, f.Requests.Count(request => request.Action == ManagedBrowserAction.ConfirmCreateTransferDirectory));
        Assert.DoesNotContain("private", result.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReconciliationTimeoutIncludesPendingSettlementWait()
    {
        using var f = await Fixture.CreateAsync("/");
        f.PendingAction = ManagedBrowserAction.ConfirmCreateTransferDirectory;
        f.PendingResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancelled = new CancellationTokenSource();
        var run = f.Run("/目标", cancelled.Token);
        await f.PendingActionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancelled.Cancel();
        var quarantine = Assert.IsType<ManagedTransferDirectoryPreparationQuarantine>((await run).Quarantine);

        var status = await quarantine.ReconcileAsync(100, default).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(ManagedTransferDirectoryPreparationReconciliationStatus.RetainedTimedOut, status);
        Assert.False(f.Lifecycle.AcquireAccountLeaseAsync(f.Identity.AccountId, default).IsCompleted);
        f.PendingResponse.SetException(new IOException("synthetic late settlement"));
    }

    [Fact]
    public async Task ReconciliationTimeoutBoundsANonCooperativeStoreReadAndRetainsLease()
    {
        using var f = await Fixture.CreateAsync("/");
        f.UnknownAction = ManagedBrowserAction.ConfirmCreateTransferDirectory;
        var quarantine = Assert.IsType<ManagedTransferDirectoryPreparationQuarantine>((await f.Run("/目标")).Quarantine);
        var hanging = new TaskCompletionSource<IReadOnlyList<TransferDirectoryMutationRecord>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        f.Store.UnresolvedGate = hanging;
        var started = Stopwatch.StartNew();

        var reconcile = quarantine.ReconcileAsync(100, default);
        var completed = await Task.WhenAny(reconcile, Task.Delay(1000));
        var bounded = ReferenceEquals(completed, reconcile);
        hanging.TrySetResult(f.Store.Unresolved());
        var status = await reconcile.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(bounded);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(1));
        Assert.Equal(ManagedTransferDirectoryPreparationReconciliationStatus.RetainedTimedOut, status);
        Assert.False(f.Lifecycle.AcquireAccountLeaseAsync(f.Identity.AccountId, default).IsCompleted);
    }

    [Fact]
    public async Task ReconciliationDeadlineUsesInjectedClockInsteadOfWallClock()
    {
        using var f = await Fixture.CreateAsync("/");
        f.UnknownAction = ManagedBrowserAction.ConfirmCreateTransferDirectory;
        var quarantine = Assert.IsType<ManagedTransferDirectoryPreparationQuarantine>((await f.Run("/目标")).Quarantine);
        f.AfterPickerDirectoryRead = () =>
        {
            f.Clock.Advance(TimeSpan.FromMilliseconds(5000));
            return Task.CompletedTask;
        };

        var status = await quarantine.ReconcileAsync(5000, default);

        Assert.Equal(ManagedTransferDirectoryPreparationReconciliationStatus.RetainedTimedOut, status);
        Assert.False(f.Lifecycle.AcquireAccountLeaseAsync(f.Identity.AccountId, default).IsCompleted);
    }

    [Theory]
    [InlineData("picker", TransferDirectoryMutationState.Intent)]
    [InlineData("cancel", TransferDirectoryMutationState.OutcomeUnknown)]
    public async Task FinalReleaseReadRejectsMutationInsertedAfterQuarantineCapture(
        string boundary, TransferDirectoryMutationState insertedState)
    {
        using var f = boundary == "cancel"
            ? await Fixture.CreateAsync("/", ("target-id", "目标"))
            : await Fixture.CreateAsync("/");
        if (boundary == "cancel") f.Children["/目标"] = [];
        f.UnknownAction = boundary == "cancel"
            ? ManagedBrowserAction.CancelTransferPicker
            : ManagedBrowserAction.OpenTransferPicker;
        f.UnknownActionClosesPicker = boundary == "cancel";
        var quarantine = Assert.IsType<ManagedTransferDirectoryPreparationQuarantine>(
            (await f.Run("/目标")).Quarantine);
        f.AddUnresolvedMutation(insertedState);

        var status = await quarantine.ReconcileAsync(2000, default);

        Assert.Equal(ManagedTransferDirectoryPreparationReconciliationStatus.RetainedInconclusive, status);
        Assert.False(f.Lifecycle.AcquireAccountLeaseAsync(f.Identity.AccountId, default).IsCompleted);
    }

    [Theory]
    [InlineData("picker")]
    [InlineData("cancel")]
    public async Task FinalReleaseReadIsBoundedAndBlockedReadRetainsLease(string boundary)
    {
        using var f = boundary == "cancel"
            ? await Fixture.CreateAsync("/", ("target-id", "目标"))
            : await Fixture.CreateAsync("/");
        if (boundary == "cancel") f.Children["/目标"] = [];
        f.UnknownAction = boundary == "cancel"
            ? ManagedBrowserAction.CancelTransferPicker
            : ManagedBrowserAction.OpenTransferPicker;
        f.UnknownActionClosesPicker = boundary == "cancel";
        var quarantine = Assert.IsType<ManagedTransferDirectoryPreparationQuarantine>(
            (await f.Run("/目标")).Quarantine);
        var hanging = new TaskCompletionSource<IReadOnlyList<TransferDirectoryMutationRecord>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        f.Store.UnresolvedGate = hanging;

        var status = await quarantine.ReconcileAsync(100, default).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(ManagedTransferDirectoryPreparationReconciliationStatus.RetainedTimedOut, status);
        Assert.False(f.Lifecycle.AcquireAccountLeaseAsync(f.Identity.AccountId, default).IsCompleted);
        hanging.SetResult(f.Store.Unresolved());
    }

    [Theory]
    [InlineData("root", ManagedBrowserAction.OpenTransferDirectoryRoot)]
    [InlineData("create", ManagedBrowserAction.ConfirmCreateTransferDirectory)]
    [InlineData("picker", ManagedBrowserAction.OpenTransferPicker)]
    public async Task ReconciliationDeadlineBoundsNonCooperativeDirectoryReadAndSettlesBeforeRetry(
        string boundary, ManagedBrowserAction uncertainAction)
    {
        using var f = boundary == "root" ? await Fixture.CreateAsync("/别处") : await Fixture.CreateAsync("/");
        if (boundary == "root")
        {
            f.Children["/"] = [new("outside-id", "别处"), new("target-id", "目标")];
            f.Children["/目标"] = [];
        }
        f.UnknownAction = uncertainAction;
        var quarantine = Assert.IsType<ManagedTransferDirectoryPreparationQuarantine>((await f.Run("/目标")).Quarantine);
        var hanging = new TaskCompletionSource<ManagedBrowserResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.PendingAction = ManagedBrowserAction.ReadTransferDirectory;
        f.PendingResponse = hanging;
        var mutationDispatches = f.Requests.Count(request => request.Action == uncertainAction);
        var started = Stopwatch.StartNew();

        var first = quarantine.ReconcileAsync(100, default);
        var completed = await Task.WhenAny(first, Task.Delay(1000));
        var bounded = ReferenceEquals(first, completed);
        if (!bounded)
        {
            hanging.TrySetException(new IOException("synthetic late reconciliation read failure"));
            _ = await first.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(bounded);
        }
        Assert.Equal(ManagedTransferDirectoryPreparationReconciliationStatus.RetainedTimedOut, await first);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(1));
        var readsWhilePending = f.Requests.Count(request => request.Action == ManagedBrowserAction.ReadTransferDirectory);
        var next = f.Lifecycle.AcquireAccountLeaseAsync(f.Identity.AccountId, default);
        Assert.False(next.IsCompleted);

        Assert.Equal(ManagedTransferDirectoryPreparationReconciliationStatus.RetainedTimedOut,
            await quarantine.ReconcileAsync(100, default).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(readsWhilePending,
            f.Requests.Count(request => request.Action == ManagedBrowserAction.ReadTransferDirectory));
        Assert.False(next.IsCompleted);

        hanging.TrySetException(new IOException("synthetic observed late reconciliation read failure"));
        f.PendingAction = null;
        f.PendingResponse = null;
        Assert.Equal(ManagedTransferDirectoryPreparationReconciliationStatus.Released,
            await quarantine.ReconcileAsync(2000, default));
        Assert.Equal(mutationDispatches, f.Requests.Count(request => request.Action == uncertainAction));
        using var reacquired = await next.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task CreateQuarantineRejectsAnAdditionalUnresolvedMutationForTheOperation()
    {
        using var f = await Fixture.CreateAsync("/");
        f.UnknownAction = ManagedBrowserAction.ConfirmCreateTransferDirectory;
        var result = await f.Run("/目标");
        var quarantine = Assert.IsType<ManagedTransferDirectoryPreparationQuarantine>(result.Quarantine);
        var expected = Assert.Single(f.Store.Mutations);
        f.Store.Mutations.Add(new(expected.OperationId, expected.AccountId, expected.Generation,
            expected.SegmentOrdinal + 1, expected.ParentExactId, null, new RemotePath("/其它"),
            expected.ListingGenerationToken, expected.BaiduUserId, expected.Capability,
            TransferDirectoryMutationState.OutcomeUnknown, expected.OwningAttemptRevision));

        Assert.Equal(ManagedTransferDirectoryPreparationReconciliationStatus.RetainedInconclusive,
            await quarantine.ReconcileAsync(2000, default));
        Assert.False(f.Lifecycle.AcquireAccountLeaseAsync(f.Identity.AccountId, default).IsCompleted);
    }

    [Theory]
    [InlineData("ordinal")]
    [InlineData("parent")]
    [InlineData("path")]
    [InlineData("generation")]
    [InlineData("capability")]
    [InlineData("revision")]
    [InlineData("state")]
    public async Task CreateQuarantineRequiresTheExactCapturedMutationIdentity(string change)
    {
        using var f = await Fixture.CreateAsync("/");
        f.UnknownAction = ManagedBrowserAction.ConfirmCreateTransferDirectory;
        var quarantine = Assert.IsType<ManagedTransferDirectoryPreparationQuarantine>((await f.Run("/目标")).Quarantine);
        var expected = Assert.Single(f.Store.Mutations);
        f.Store.Mutations[0] = new(expected.OperationId, expected.AccountId, expected.Generation,
            change == "ordinal" ? expected.SegmentOrdinal + 1 : expected.SegmentOrdinal,
            change == "parent" ? "different-parent" : expected.ParentExactId, expected.ChildExactId,
            change == "path" ? new RemotePath("/其它") : expected.CanonicalChildPath,
            change == "generation" ? "different-generation" : expected.ListingGenerationToken,
            expected.BaiduUserId,
            change == "capability" ? new("BaiduTransfer", "different", "selectors", "synthetic-build") : expected.Capability,
            change == "state" ? TransferDirectoryMutationState.Intent : expected.State,
            change == "revision" ? expected.OwningAttemptRevision + 1 : expected.OwningAttemptRevision);

        Assert.Equal(ManagedTransferDirectoryPreparationReconciliationStatus.RetainedInconclusive,
            await quarantine.ReconcileAsync(2000, default));
        Assert.False(f.Lifecycle.AcquireAccountLeaseAsync(f.Identity.AccountId, default).IsCompleted);
    }

    private sealed class Fixture : IDisposable
    {
        internal const string SourceUri = "https://pan.baidu.com/s/task4synthetic?pwd=aB12";
        internal readonly Dictionary<string, List<(string Id, string Name)>> Children = new(StringComparer.Ordinal);
        internal readonly List<ManagedBrowserRequest> Requests = [];
        internal readonly List<string> EnteredIds = [];
        internal readonly List<string> BegunSegments = [];
        internal readonly List<string> ConfirmedSegments = [];
        internal readonly List<ManagedTransferDirectoryEvidence> DirectoryEvidence = [];
        internal readonly Authorization Authorization = new();
        internal readonly FakeProcesses Processes = new();
        internal readonly FakeListeners Listeners = new();
        internal readonly Targets TargetProbe = new();
        internal readonly ManualTimeProvider Clock = new(Now);
        internal required string Root;
        internal required SqliteConnectionFactory Factory;
        internal required BrowserInstallationIdentity Installation;
        internal required ManagedBrowserSessionReference ManagedIdentity;
        internal required ManagedBrowserLifecycleCoordinator Lifecycle;
        internal required ManagedChromiumProcessManager Manager;
        internal required ManagedChromiumSessionHandle Handle;
        internal required WebAutomationRequestIdentity Identity;
        internal required AdapterCapabilityVersion Capability;
        internal required MemoryStore Store;
        internal BaiduCandidateSessionBinding CandidateBinding = null!;
        internal ManagedBrowserAction? UnknownAction;
        internal bool UnknownActionClosesPicker;
        internal bool FailRefreshedReadAfterConfirm;
        internal bool ThrowStoreAfterUnknownAction;
        internal bool DuplicateNextSegmentAfterConfirmedCreate;
        internal bool ApplyUnknownActionSideEffect;
        internal ManagedBrowserAction? PendingAction;
        internal TaskCompletionSource<ManagedBrowserResponse>? PendingResponse;
        internal readonly TaskCompletionSource PendingActionStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool IntentWasPresentBeforeEveryCreateAction = true;
        internal string? SourceChange;
        internal Func<Task>? AfterPickerDirectoryRead;
        private string currentPath = "/";
        private string? pendingSegment;
        private bool pickerOpen;
        private bool confirmSucceeded;

        internal static async Task<Fixture> CreateAsync(string currentPath,
            params (string Id, string Name)[] initialChildren)
        {
            var root = Path.Combine(Path.GetTempPath(), "directory-preparation-session-" + Guid.NewGuid().ToString("N"));
            var factory = new SqliteConnectionFactory(Path.Combine(root, "synthetic.db"));
            await new DatabaseMigrator(factory,
                new DatabaseBackupService(factory, Path.Combine(root, "backups")), "test").MigrateAsync();
            var installation = new BrowserInstallationIdentity(Guid.NewGuid(), BrowserFamily.Chrome,
                @"C:\SyntheticBrowser", @"C:\SyntheticBrowser\chrome.exe", "Google LLC", "Google Chrome", "synthetic-file");
            var managed = new ManagedBrowserSessionReference(Guid.NewGuid(), installation.InstallationId, Guid.NewGuid(),
                "synthetic-user", BrowserFamily.Chrome, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
            var stateStore = new SqliteManagedBrowserStateStore(factory);
            Assert.Equal(ManagedBrowserBindingStatus.Bound,
                await stateStore.BindLoginAsync(true, installation, managed, null, default));
            var profile = ManagedBrowserProfileDescriptor.FromStoredSession(managed, root);
            var executable = new FakeExecutable(installation);
            var profileLease = new FakeProfile(profile);
            var f = new Fixture
            {
                Root = root,
                Factory = factory,
                Installation = installation,
                ManagedIdentity = managed,
                Capability = new("BaiduTransfer", "protocol", "selectors", "synthetic-build"),
                Store = null!,
                Manager = null!,
                Lifecycle = null!,
                Handle = null!,
                Identity = null!,
                currentPath = currentPath,
            };
            f.Manager = new((_, _) => Task.FromResult<IBrowserExecutableLaunchLease>(executable),
                (_, _) => Task.FromResult<IManagedBrowserProfileLease>(profileLease), f.Processes, f.Listeners,
                f.TargetProbe, TimeSpan.FromMilliseconds(200));
            f.Lifecycle = new(stateStore, f.Manager, root);
            f.Handle = (await f.Manager.LaunchAsync(installation, profile)).Session!;
            f.Identity = new(Guid.NewGuid(), Guid.NewGuid(), managed.AccountId, 7, WebAutomationOwnerKind.Transfer,
                ExecutionProfileSnapshot.Managed(managed.BrowserFamily, managed.ProfileId,
                    managed.InstallationId, managed.SessionId));
            f.CandidateBinding = f.NewCandidateBinding(f.Clock.GetUtcNow().AddMinutes(5));
            f.Store = new MemoryStore(WebAutomationAttemptRecord.Create(f.Identity.OperationId, f.Identity.AccountId,
                f.Identity.Generation, WebAutomationOwnerKind.Transfer, Now.AddMinutes(-1)));
            f.Children[currentPath] = [.. initialChildren];
            if (currentPath != "/" && !f.Children.ContainsKey("/")) f.Children["/"] = [];
            return f;
        }

        internal BaiduCandidateSessionBinding NewCandidateBinding(DateTimeOffset expires)
        {
            var authorization = new AdapterWriteAuthorizationRequest(Capability, Identity.AccountId,
                Identity.OperationId, Identity.Generation, Identity.BatchId, AdapterWriteAction.EnsureDirectory);
            return new(authorization, new(AdapterWriteAuthorizationSource.ConsumedLease, Capability,
                Identity.AccountId, Identity.OperationId, Identity.Generation, Identity.BatchId,
                Clock.GetUtcNow().AddMinutes(-1), expires, false, null), ManagedIdentity.StableBaiduUserId);
        }

        internal async Task<ManagedTransferDirectoryPreparationResult> Run(string target,
            CancellationToken cancellationToken = default, int timeoutMilliseconds = 3000)
        {
            var request = Request(target);
            Identity.Validate();
            CandidateBinding.ValidateIdentity(Identity);
            Assert.Equal(Identity.OperationId, request.OperationId);
            Assert.Equal(Identity.BatchId, request.BatchId);
            Assert.Equal(Identity.AccountId, request.AccountId);
            Assert.Equal(Identity.Generation, request.Generation);
            Assert.Equal(CandidateBinding.Binding.Capability, request.Capability);
            Assert.Equal(SourceUri, request.SourceShare.OriginalUrl);
            Assert.Equal(request.OperationId, Store.Current.OperationId);
            Assert.Equal(request.AccountId, Store.Current.AccountId);
            Assert.Equal(request.Generation, Store.Current.Generation);
            Assert.Equal(WebAutomationOwnerKind.Transfer, Store.Current.OwnerKind);
            Assert.Equal(WebAutomationAttemptState.Preparing, Store.Current.State);
            return await new ManagedTransferDirectoryPreparationRunner(Lifecycle, Manager, Send, Authorization, Clock)
                .RunAsync(Identity, CandidateBinding, BaiduExecutionContext.Managed(ManagedIdentity), Handle,
                    SourceUri, timeoutMilliseconds, Store, request, cancellationToken);
        }

        internal TransferDirectoryPreparationRequest Request(string target) => new(
            Identity.OperationId, Identity.BatchId, Identity.AccountId, Identity.Generation,
            new("synthetic", SourceUri), new(target), Capability);

        internal WebAutomationAttemptRecord CopyAttemptWithSource(TransferSourceEvidence? source) => new(
            Store.Current.OperationId, Store.Current.AccountId, Store.Current.Generation,
            Store.Current.OwnerKind, Store.Current.State, Store.Current.ResumeStage, Store.Current.Baseline,
            source?.ObservationFingerprint, Store.Current.IsWaitingForUser, Store.Current.Revision,
            Store.Current.CreatedAt, Store.Current.UpdatedAt, Store.Current.AcceptedRemoteItems, source,
            Store.Current.TargetPreparationEvidence, Store.Current.SubmissionEvidence,
            Store.Current.OfficialRequestId, Store.Current.TransferIdentityMapping, Store.Current.ExecutionProfile);

        internal TransferSourceEvidence SourceEvidence()
        {
            var item = new RemoteItemIdentity("source-id", "source-name");
            var snapshot = SourceSnapshot();
            return new(new([item]), 1, true, true, snapshot.GenerationToken,
                snapshot.BaiduUserId, Capability, snapshot.ObservationFingerprint);
        }

        internal void AddUnresolvedMutation(TransferDirectoryMutationState state) => Store.Mutations.Add(new(
            Identity.OperationId, Identity.AccountId, Identity.Generation, 0, "root-id", null,
            new RemotePath("/raced"), "raced-generation", ManagedIdentity.StableBaiduUserId,
            Capability, state, Store.Current.Revision));

        private Task<ManagedBrowserResponse> Send(ManagedBrowserRequest request, CancellationToken token)
        {
            Requests.Add(request);
            token.ThrowIfCancellationRequested();
            if (request.Action == PendingAction && PendingResponse is not null)
            {
                PendingActionStarted.TrySetResult();
                return PendingResponse.Task;
            }
            if (request.Action == ManagedBrowserAction.OpenTransferSource)
            {
                TargetProbe.Page = new(SourceUri);
                return Reply(new ManagedBrowserTransferSourceResult(ManagedBrowserTransferSourceResultKind.SourceOpened));
            }
            if (request.Action == ManagedBrowserAction.ReadTransferSource)
                return Reply(new ManagedBrowserSourceListingResult(ManagedBrowserSourceListingResultKind.Observed,
                    SourceSnapshot()));
            if (request.Action == UnknownAction)
            {
                if (UnknownActionClosesPicker) pickerOpen = false;
                if (ApplyUnknownActionSideEffect && request.Action == ManagedBrowserAction.OpenTransferDirectoryRoot)
                    currentPath = "/";
                if (ApplyUnknownActionSideEffect && request.Action == ManagedBrowserAction.EnterTransferDirectory)
                {
                    var enter = Assert.IsType<ManagedBrowserDirectoryMutationRequest>(request.Payload);
                    currentPath = enter.ChildCanonicalPath!;
                    Children.TryAdd(currentPath, []);
                }
                if (ThrowStoreAfterUnknownAction) Store.ThrowOnNextUnresolved = true;
                return request.Action == ManagedBrowserAction.OpenTransferPicker
                    ? Reply(new ManagedBrowserTransferPickerResult(ManagedBrowserTransferPickerResultKind.OutcomeUnknown))
                    : Reply(new ManagedBrowserDirectoryMutationResult(ManagedBrowserDirectoryMutationResultKind.OutcomeUnknown));
            }
            if (request.Action == ManagedBrowserAction.OpenTransferPicker)
            {
                pickerOpen = true;
                return Reply(new ManagedBrowserTransferPickerResult(ManagedBrowserTransferPickerResultKind.PickerOpened));
            }
            if (request.Action == ManagedBrowserAction.ReadTransferDirectory)
            {
                if (FailRefreshedReadAfterConfirm && confirmSucceeded)
                {
                    FailRefreshedReadAfterConfirm = false;
                    return Reply(new ManagedBrowserDirectoryListingResult(
                        ManagedBrowserDirectoryListingResultKind.PageNotValidated));
                }
                var evidence = DirectorySnapshot();
                DirectoryEvidence.Add(ManagedTransferDirectoryEvidence.CreateFromReaderValidated(evidence));
                if (pickerOpen && AfterPickerDirectoryRead is not null)
                {
                    var after = AfterPickerDirectoryRead;
                    AfterPickerDirectoryRead = null;
                    return ReplyAfterAsync(after, new ManagedBrowserDirectoryListingResult(
                        ManagedBrowserDirectoryListingResultKind.Observed, evidence));
                }
                return Reply(new ManagedBrowserDirectoryListingResult(ManagedBrowserDirectoryListingResultKind.Observed,
                    evidence));
            }
            if (request.Action == ManagedBrowserAction.ObserveTransferPickerClosed)
                return Reply(new ManagedBrowserTransferPickerClosedResult(pickerOpen
                    ? ManagedBrowserTransferPickerClosedResultKind.PageNotValidated
                    : ManagedBrowserTransferPickerClosedResultKind.PickerClosed));

            var payload = Assert.IsType<ManagedBrowserDirectoryMutationRequest>(request.Payload);
            if (request.Action == ManagedBrowserAction.OpenTransferDirectoryRoot)
            {
                currentPath = "/";
                return Reply(new ManagedBrowserDirectoryMutationResult(ManagedBrowserDirectoryMutationResultKind.RootOpened));
            }
            if (request.Action == ManagedBrowserAction.EnterTransferDirectory)
            {
                EnteredIds.Add(payload.ChildExactId!);
                currentPath = payload.ChildCanonicalPath!;
                Children.TryAdd(currentPath, []);
                return Reply(new ManagedBrowserDirectoryMutationResult(ManagedBrowserDirectoryMutationResultKind.DirectoryEntered));
            }
            if (request.Action == ManagedBrowserAction.BeginCreateTransferDirectory)
            {
                pendingSegment = payload.NewSegment;
                BegunSegments.Add(payload.NewSegment!);
                AssertIntent(payload.NewSegment!);
                return Reply(new ManagedBrowserDirectoryMutationResult(ManagedBrowserDirectoryMutationResultKind.CreateDialogOpened));
            }
            if (request.Action == ManagedBrowserAction.ConfirmCreateTransferDirectory)
            {
                ConfirmedSegments.Add(payload.NewSegment!);
                AssertIntent(payload.NewSegment!);
                var childPath = currentPath == "/" ? "/" + pendingSegment : currentPath + "/" + pendingSegment;
                Children[currentPath].Add(("created-" + ConfirmedSegments.Count, pendingSegment!));
                Children.TryAdd(childPath, []);
                if (DuplicateNextSegmentAfterConfirmedCreate)
                    Children[childPath] = [new("duplicate-1", "乙"), new("duplicate-2", "乙")];
                pendingSegment = null;
                confirmSucceeded = true;
                return Reply(new ManagedBrowserDirectoryMutationResult(ManagedBrowserDirectoryMutationResultKind.DirectoryCreated));
            }
            pickerOpen = false;
            return Reply(new ManagedBrowserDirectoryMutationResult(ManagedBrowserDirectoryMutationResultKind.PickerCancelled));

            Task<ManagedBrowserResponse> Reply(ManagedBrowserResult result) =>
                Task.FromResult(ManagedBrowserResponse.For(request, result));
            async Task<ManagedBrowserResponse> ReplyAfterAsync(Func<Task> after, ManagedBrowserResult result)
            {
                await after();
                return ManagedBrowserResponse.For(request, result);
            }
        }

        internal Task<ManagedBrowserResponse> SendForTest(ManagedBrowserRequest request, CancellationToken token) =>
            Send(request, token);

        private void AssertIntent(string segment)
        {
            if (!Store.Mutations.Any(mutation => mutation.State == TransferDirectoryMutationState.Intent &&
                    mutation.CanonicalChildPath.Value.EndsWith('/' + segment, StringComparison.Ordinal)))
                IntentWasPresentBeforeEveryCreateAction = false;
        }

        private ManagedBrowserSourceSnapshot SourceSnapshot()
        {
            var id = SourceChange == "id" ? "changed-source-id" : "source-id";
            var name = SourceChange == "name" ? "changed-source-name" : "source-name";
            var uid = SourceChange == "uid" ? "changed-synthetic-user" : ManagedIdentity.StableBaiduUserId;
            var generation = SourceChange == "generation" ? "changed-source-generation" : "source-generation";
            ManagedBrowserSourceItem[] items = SourceChange == "count"
                ? [new(id, ManagedBrowserSourceItemKind.File, name),
                    new("source-id-2", ManagedBrowserSourceItemKind.File, "source-name-2")]
                : [new(id, ManagedBrowserSourceItemKind.File, name)];
            var reachedEnd = SourceChange != "incomplete";
            var stable = SourceChange != "unstable";
            var raw = new ManagedBrowserSourceSnapshot(uid, generation, items.Length,
                reachedEnd, stable, items, new string('0', 64));
            var fingerprintSource = SourceChange == "fingerprint" ? SourceUri + "#changed" : SourceUri;
            return new(raw.BaiduUserId, raw.GenerationToken, raw.TotalCount, reachedEnd, stable, raw.Items,
                raw.ComputeFingerprint(fingerprintSource));
        }

        internal async Task Execute(string sql)
        {
            await using var connection = await Factory.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        private ManagedBrowserDirectorySnapshot DirectorySnapshot()
        {
            var ancestors = new List<string> { "root-id" };
            var segments = currentPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var cursor = "/";
            foreach (var segment in segments)
            {
                var child = Children[cursor].Single(item => item.Name == segment);
                ancestors.Add(child.Id);
                cursor = cursor == "/" ? "/" + segment : cursor + "/" + segment;
            }
            var items = Children.GetValueOrDefault(currentPath, []).Select(item =>
                new ManagedBrowserSourceItem(item.Id, ManagedBrowserSourceItemKind.Folder, item.Name)).ToArray();
            var raw = new ManagedBrowserDirectorySnapshot(ManagedIdentity.StableBaiduUserId,
                "directory-generation-" + Requests.Count, ancestors[^1], currentPath, ancestors, items.Length,
                true, true, items, new string('0', 64));
            return new(raw.BaiduUserId, raw.GenerationToken, raw.ParentExactId, raw.CanonicalPath,
                raw.AncestorExactIds, raw.TotalCount, true, true, raw.Items, raw.ComputeFingerprint(SourceUri));
        }

        public void Dispose() => Manager.Dispose();
    }

    private sealed class Targets : IManagedBrowserTargetProbe
    {
        internal Uri Page = new("https://pan.baidu.com/");
        internal string TargetId = "fixed-tab";
        public Task<ManagedBrowserTargetSnapshot?> ReadAsync(DevToolsActivePort port, string? exactTargetId,
            CancellationToken token, Action<string>? targetDiscovered = null) =>
            Task.FromResult<ManagedBrowserTargetSnapshot?>(new(port.BrowserId, TargetId, Page));
    }

    private sealed class Authorization : IBaiduCandidateActionAuthorizationSource
    {
        internal int Acquired;
        internal int Disposed;
        internal BaiduAutomationAction? GatedAction;
        internal BaiduAutomationAction? DeniedAction;
        internal BaiduAutomationAction? ProofChangeAction;
        internal Func<Task>? AfterAcquisition;
        internal TaskCompletionSource? AcquisitionGate;
        internal readonly TaskCompletionSource AcquisitionStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<bool> IsAuthorizedAsync(BaiduCandidateDomRequest request, CancellationToken token) =>
            ValueTask.FromResult(false);
        public ValueTask<IBaiduCandidateMutationPermit?> TryAcquireMutationPermitAsync(
            BaiduCandidateDomRequest request, CancellationToken token) =>
            ValueTask.FromResult<IBaiduCandidateMutationPermit?>(null);
        public async ValueTask<IBaiduCandidateMutationPermit?> TryAcquireTransferMutationPermitAsync(
            BaiduCandidateDomMutationRequest request, CancellationToken token)
        {
            Acquired++;
            if (request.Action == GatedAction && AcquisitionGate is not null)
            {
                AcquisitionStarted.TrySetResult();
                await AcquisitionGate.Task.ConfigureAwait(false);
            }
            if (request.Action == ProofChangeAction && AfterAcquisition is not null)
                await AfterAcquisition();
            if (request.Action == DeniedAction) return null;
            return new Permit(this);
        }
        private sealed class Permit(Authorization owner) : IBaiduCandidateMutationPermit
        {
            public ValueTask DisposeAsync()
            {
                owner.Disposed++;
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class MemoryStore(WebAutomationAttemptRecord current) : ITransferDirectoryPreparationEvidenceStore
    {
        internal WebAutomationAttemptRecord Current = current;
        internal readonly List<TransferDirectoryMutationRecord> Mutations = [];
        internal int GetAttemptCalls;
        internal TaskCompletionSource<WebAutomationAttemptRecord?>? AttemptGate;
        internal TaskCompletionSource<IReadOnlyList<TransferDirectoryMutationRecord>>? UnresolvedGate;
        internal bool FirstAttemptReadOmitsSource;
        internal bool ThrowOnNextUnresolved;
        internal bool ThrowBeginMutation;
        internal bool RejectBeginMutation;
        internal bool FailConfirmedResolution;
        internal void PersistSource(TransferSourceEvidence source) => Current = Copy(source, Current.Revision + 1);
        public Task<WebAutomationAttemptRecord?> GetAttemptAsync(TransferDirectoryPreparationRequest request,
            CancellationToken cancellationToken)
        {
            GetAttemptCalls++;
            if (AttemptGate is not null) return AttemptGate.Task;
            if (FirstAttemptReadOmitsSource && GetAttemptCalls == 1)
                return Task.FromResult<WebAutomationAttemptRecord?>(WithoutSource());
            return Task.FromResult<WebAutomationAttemptRecord?>(Current);
        }
        public Task<IReadOnlyList<TransferDirectoryMutationRecord>> GetUnresolvedMutationsAsync(Guid operationId,
            Guid accountId, CancellationToken cancellationToken)
        {
            if (ThrowOnNextUnresolved)
            {
                ThrowOnNextUnresolved = false;
                throw new IOException("synthetic store identity failure");
            }
            if (UnresolvedGate is not null) return UnresolvedGate.Task;
            return Task.FromResult(Unresolved());
        }
        internal IReadOnlyList<TransferDirectoryMutationRecord> Unresolved() =>
            Mutations.Where(mutation => mutation.State != TransferDirectoryMutationState.Confirmed).ToArray();
        public Task<TransferDirectoryRecoveryAuthorization?> AuthorizeRecoveryAsync(
            TransferDirectoryRecoveryAuthorizationRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<TransferDirectoryRecoveryAuthorization?>(null);
        public Task<bool> PersistSourceEvidenceAsync(TransferDirectoryPreparationRequest request, long expectedRevision,
            TransferSourceEvidence evidence, DateTimeOffset updatedAt, CancellationToken cancellationToken)
        {
            if (expectedRevision != Current.Revision) return Task.FromResult(false);
            Current = Copy(evidence, Current.Revision + 1);
            return Task.FromResult(true);
        }
        public Task<bool> BeginMutationAsync(TransferDirectoryMutationRecord mutation, DateTimeOffset updatedAt,
            CancellationToken cancellationToken)
        {
            if (ThrowBeginMutation) throw new IOException("synthetic begin mutation store failure");
            if (RejectBeginMutation) return Task.FromResult(false);
            Mutations.Add(mutation);
            return Task.FromResult(true);
        }
        public Task<bool> ResolveMutationAsync(TransferDirectoryMutationRecord expected,
            TransferDirectoryMutationState nextState, string? childExactId, DateTimeOffset updatedAt,
            CancellationToken cancellationToken)
        {
            if (FailConfirmedResolution && nextState == TransferDirectoryMutationState.Confirmed)
                return Task.FromResult(false);
            var index = Mutations.IndexOf(expected);
            if (index < 0) return Task.FromResult(false);
            Mutations[index] = new(expected.OperationId, expected.AccountId, expected.Generation,
                expected.SegmentOrdinal, expected.ParentExactId, childExactId, expected.CanonicalChildPath,
                expected.ListingGenerationToken, expected.BaiduUserId, expected.Capability, nextState,
                expected.OwningAttemptRevision);
            return Task.FromResult(true);
        }
        private WebAutomationAttemptRecord Copy(TransferSourceEvidence source, long revision) => new(
            Current.OperationId, Current.AccountId, Current.Generation, Current.OwnerKind, Current.State,
            Current.ResumeStage, Current.Baseline, source.ObservationFingerprint, Current.IsWaitingForUser,
            revision, Current.CreatedAt, Now, sourceEvidence: source);
        private WebAutomationAttemptRecord WithoutSource() => new(
            Current.OperationId, Current.AccountId, Current.Generation, Current.OwnerKind, Current.State,
            Current.ResumeStage, Current.Baseline, null, Current.IsWaitingForUser,
            Current.Revision - 1, Current.CreatedAt, Now);
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private long timestamp;
        private DateTimeOffset current = now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => current;
        public override long GetTimestamp() => timestamp;
        internal void Advance(TimeSpan duration)
        {
            current += duration;
            timestamp += duration.Ticks;
        }
    }
}
