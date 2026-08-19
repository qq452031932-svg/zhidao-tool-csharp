using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Tests.Jobs;

public sealed class ReassignRemainingWorkServiceTests
{
    [Fact]
    public async Task TaskQuotaPreviewIsReadOnlyAndMovesOnlyNeverSubmittedUnlockedOperations()
    {
        var batch = Guid.NewGuid();
        var failed = Guid.NewGuid();
        var replacement = Guid.NewGuid();
        var movable = Operation(failed, 1, "Waiting", "NotPrepared", false);
        var state = new ReassignmentState(
            batch,
            "version-1",
            QuotaBasis.TaskCount,
            [UnavailableAllocation(failed, 5), Allocation(replacement, 0)],
            [
                movable,
                Operation(failed, 2, "Submitting", "Submitting", false),
                Operation(failed, 3, "ResultPending", "PendingConfirmation", false),
                Operation(failed, 4, "Succeeded", "ResultReceived", false),
                Operation(failed, 5, "Waiting", "NotPrepared", true),
            ],
            []);
        var store = new RecordingReassignmentStore(state);
        var service = new ReassignRemainingWorkService(store);

        var preview = await service.PreviewAsync(new ReassignmentRequest(
            batch, failed, new Dictionary<Guid, int> { [failed] = 4, [replacement] = 1 }));

        var move = Assert.Single(preview.OperationMoves);
        Assert.Equal(movable.OperationId, move.OperationId);
        Assert.Equal(failed, move.OldAccountId);
        Assert.Equal(replacement, move.NewAccountId);
        Assert.Equal(1, preview.AffectedCount);
        Assert.Equal(-1, preview.AllocationDeltas.Single(x => x.AccountId == failed).Delta);
        Assert.Equal(1, preview.AllocationDeltas.Single(x => x.AccountId == replacement).Delta);
        Assert.Equal(0, store.ApplyCalls);
    }

    [Fact]
    public async Task TaskQuotaPreviewExcludesManualRetryAfterSubmissionBoundaryWhenAccountBecomesInvalid()
    {
        var batch = Guid.NewGuid();
        var failed = Guid.NewGuid();
        var replacement = Guid.NewGuid();
        var resultPending = Operation(failed, 1, "ResultPending", "PendingConfirmation", false) with
        {
            HasCrossedSubmissionBoundary = true,
        };
        var retriedOnSameAccount = resultPending with
        {
            Status = "Waiting",
            SubmitState = "NotPrepared",
        };
        var stateAfterAccountInvalidation = new ReassignmentState(
            batch,
            "version-after-retry",
            QuotaBasis.TaskCount,
            [UnavailableAllocation(failed, 1), Allocation(replacement, 0)],
            [retriedOnSameAccount],
            []);
        var store = new RecordingReassignmentStore(stateAfterAccountInvalidation);

        await Assert.ThrowsAsync<ReassignmentValidationException>(() =>
            new ReassignRemainingWorkService(store).PreviewAsync(new ReassignmentRequest(
                batch, failed, new Dictionary<Guid, int> { [failed] = 0, [replacement] = 1 })));

        Assert.Equal(0, store.ApplyCalls);
    }

    [Fact]
    public async Task SuccessTargetPreviewChangesTargetsButNeverReservedOrExclusiveCandidates()
    {
        var batch = Guid.NewGuid();
        var failed = Guid.NewGuid();
        var replacement = Guid.NewGuid();
        var state = new ReassignmentState(
            batch,
            "version-1",
            QuotaBasis.SuccessTarget,
            [UnavailableAllocation(failed, 3), Allocation(replacement, 1)],
            [Operation(failed, 1, "ResultPending", "PendingConfirmation", false)],
            [
                new ReassignmentCandidate(Guid.NewGuid(), 1, "Unallocated", null, null),
                new ReassignmentCandidate(Guid.NewGuid(), 2, "Reserved", failed, null),
                new ReassignmentCandidate(Guid.NewGuid(), 3, "Unallocated", null, failed),
            ]);
        var preview = await new ReassignRemainingWorkService(new RecordingReassignmentStore(state))
            .PreviewAsync(new ReassignmentRequest(
                batch, failed, new Dictionary<Guid, int> { [failed] = 1, [replacement] = 3 }));

        Assert.Empty(preview.OperationMoves);
        Assert.Empty(preview.CandidateMoves);
        Assert.Equal(2, preview.AffectedCount);
        Assert.All(preview.AllocationDeltas, delta => Assert.NotEqual(0, delta.Delta));
    }

    [Fact]
    public async Task SuccessTargetPreviewRejectsMovingPublicPausedReservation()
    {
        var batch = Guid.NewGuid();
        var failed = Guid.NewGuid();
        var replacement = Guid.NewGuid();
        var resource = Guid.NewGuid();
        var operation = Operation(failed, 1, "Waiting", "NotPrepared", false);
        var state = new ReassignmentState(
            batch, "version-1", QuotaBasis.SuccessTarget,
            [UnavailableAllocation(failed, 1) with { MinimumRetainedTargetCount = 1 }, Allocation(replacement, 0)],
            [operation],
            [new ReassignmentCandidate(resource, 1, "Reserved", failed, null)]);

        await Assert.ThrowsAsync<ReassignmentValidationException>(() =>
            new ReassignRemainingWorkService(new RecordingReassignmentStore(state))
                .PreviewAsync(new ReassignmentRequest(
                    batch, failed, new Dictionary<Guid, int> { [failed] = 0, [replacement] = 1 })));
    }

    [Fact]
    public async Task ApplyRequiresExactConfirmationTokenAndCurrentBatchVersion()
    {
        var batch = Guid.NewGuid();
        var failed = Guid.NewGuid();
        var replacement = Guid.NewGuid();
        var state = new ReassignmentState(
            batch, "version-1", QuotaBasis.TaskCount,
            [UnavailableAllocation(failed, 1), Allocation(replacement, 0)],
            [Operation(failed, 1, "Waiting", "NotPrepared", false)], []);
        var store = new RecordingReassignmentStore(state);
        var service = new ReassignRemainingWorkService(store);
        var preview = await service.PreviewAsync(new ReassignmentRequest(
            batch, failed, new Dictionary<Guid, int> { [failed] = 0, [replacement] = 1 }));

        await Assert.ThrowsAsync<ReassignmentConfirmationException>(
            () => service.ApplyAsync(preview, "wrong-token"));
        store.CurrentVersion = "version-2";
        await Assert.ThrowsAsync<ReassignmentStaleStateException>(
            () => service.ApplyAsync(preview, preview.ConfirmationToken));
        Assert.Equal(0, store.ApplyCalls);
    }

    [Fact]
    public async Task ApplyRejectsCallerTamperingAndNeverTrustsPreviewMoves()
    {
        var batch = Guid.NewGuid();
        var failed = Guid.NewGuid();
        var replacement = Guid.NewGuid();
        var movable = Operation(failed, 1, "Waiting", "NotPrepared", false);
        var store = new RecordingReassignmentStore(new ReassignmentState(
            batch, "version-1", QuotaBasis.TaskCount,
            [UnavailableAllocation(failed, 1), Allocation(replacement, 0)], [movable], []));
        var service = new ReassignRemainingWorkService(store);
        var preview = await service.PreviewAsync(new ReassignmentRequest(batch, failed,
            new Dictionary<Guid, int> { [failed] = 0, [replacement] = 1 }));
        var tampered = preview with
        {
            OperationMoves = [new ReassignmentOperationMove(Guid.NewGuid(), failed, replacement)],
        };

        await Assert.ThrowsAsync<ReassignmentConfirmationException>(() =>
            service.ApplyAsync(tampered, preview.ConfirmationToken));

        Assert.Equal(0, store.ApplyCalls);
    }

    [Fact]
    public async Task ApplyRejectsVersionRaceInsideAtomicStoreMutation()
    {
        var batch = Guid.NewGuid();
        var failed = Guid.NewGuid();
        var replacement = Guid.NewGuid();
        var store = new RecordingReassignmentStore(new ReassignmentState(
            batch, "version-1", QuotaBasis.TaskCount,
            [UnavailableAllocation(failed, 1), Allocation(replacement, 0)],
            [Operation(failed, 1, "Waiting", "NotPrepared", false)], []));
        var service = new ReassignRemainingWorkService(store);
        var preview = await service.PreviewAsync(new ReassignmentRequest(batch, failed,
            new Dictionary<Guid, int> { [failed] = 0, [replacement] = 1 }));
        store.ChangeVersionInsideApply = true;

        await Assert.ThrowsAsync<ReassignmentStaleStateException>(() =>
            service.ApplyAsync(preview, preview.ConfirmationToken));

        Assert.Equal(1, store.ApplyCalls);
    }

    [Fact]
    public async Task ApplyReportsAccountHealthChangeAfterPreviewAsStaleState()
    {
        var batch = Guid.NewGuid();
        var failed = Guid.NewGuid();
        var replacement = Guid.NewGuid();
        var initial = new ReassignmentState(
            batch, "version-1", QuotaBasis.TaskCount,
            [UnavailableAllocation(failed, 1), Allocation(replacement, 0)],
            [Operation(failed, 1, "Waiting", "NotPrepared", false)], []);
        var store = new RecordingReassignmentStore(initial);
        var service = new ReassignRemainingWorkService(store);
        var preview = await service.PreviewAsync(new ReassignmentRequest(batch, failed,
            new Dictionary<Guid, int> { [failed] = 0, [replacement] = 1 }));
        store.CurrentState = initial with
        {
            BatchVersion = "version-2",
            Allocations =
            [
                UnavailableAllocation(failed, 1),
                new ReassignmentAllocation(replacement, 0, 0, 0, "Active", false, false),
            ],
        };

        await Assert.ThrowsAsync<ReassignmentStaleStateException>(() =>
            service.ApplyAsync(preview, preview.ConfirmationToken));

        Assert.Equal(0, store.ApplyCalls);
    }

    [Fact]
    public async Task PreviewRejectsChangesThatDoNotRedistributeOnlyTheFailedAccountTarget()
    {
        var batch = Guid.NewGuid();
        var failed = Guid.NewGuid();
        var healthyA = Guid.NewGuid();
        var healthyB = Guid.NewGuid();
        var state = new ReassignmentState(batch, "version-1", QuotaBasis.SuccessTarget,
            [UnavailableAllocation(failed, 2), Allocation(healthyA, 2), Allocation(healthyB, 0)], [], []);
        var service = new ReassignRemainingWorkService(new RecordingReassignmentStore(state));

        await Assert.ThrowsAsync<ReassignmentValidationException>(() => service.PreviewAsync(
            new ReassignmentRequest(batch, failed,
                new Dictionary<Guid, int> { [failed] = 2, [healthyA] = 0, [healthyB] = 2 })));
    }

    [Fact]
    public async Task SuccessTargetCannotDropBelowAlreadyReservedOrFulfilledWork()
    {
        var batch = Guid.NewGuid();
        var failed = Guid.NewGuid();
        var replacement = Guid.NewGuid();
        var state = new ReassignmentState(batch, "version-1", QuotaBasis.SuccessTarget,
            [new ReassignmentAllocation(failed, 3, 3, 2, "Invalid", true, false), Allocation(replacement, 0)], [], []);
        var service = new ReassignRemainingWorkService(new RecordingReassignmentStore(state));

        await Assert.ThrowsAsync<ReassignmentValidationException>(() => service.PreviewAsync(
            new ReassignmentRequest(batch, failed,
                new Dictionary<Guid, int> { [failed] = 1, [replacement] = 2 })));
    }

    [Fact]
    public async Task PreviewRejectsHealthySourceAndUnavailableReplacementTarget()
    {
        var batch = Guid.NewGuid();
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        var operation = Operation(source, 1, "Waiting", "NotPrepared", false);

        var healthySource = new ReassignRemainingWorkService(new RecordingReassignmentStore(
            new ReassignmentState(batch, "v1", QuotaBasis.TaskCount,
                [Allocation(source, 1), Allocation(target, 0)], [operation], [])));
        await Assert.ThrowsAsync<ReassignmentValidationException>(() => healthySource.PreviewAsync(
            new ReassignmentRequest(batch, source, new Dictionary<Guid, int> { [source] = 0, [target] = 1 })));

        var disabledTarget = new ReassignRemainingWorkService(new RecordingReassignmentStore(
            new ReassignmentState(batch, "v2", QuotaBasis.TaskCount,
                [UnavailableAllocation(source, 1), new ReassignmentAllocation(target, 0, 0, 0, "Active", false, false)],
                [operation], [])));
        await Assert.ThrowsAsync<ReassignmentValidationException>(() => disabledTarget.PreviewAsync(
            new ReassignmentRequest(batch, source, new Dictionary<Guid, int> { [source] = 0, [target] = 1 })));
    }

    private static ReassignmentAllocation Allocation(Guid account, int target) =>
        new(account, target, target, 0, "Active", true, false);

    private static ReassignmentAllocation UnavailableAllocation(Guid account, int target) =>
        new(account, target, target, 0, "Invalid", true, false);

    private static ReassignmentOperation Operation(
        Guid account,
        long sequence,
        string status,
        string submitState,
        bool exclusive) =>
        new(Guid.NewGuid(), sequence, account, status, submitState, exclusive);

    private sealed class RecordingReassignmentStore(ReassignmentState state) : IReassignmentStore
    {
        public ReassignmentState CurrentState { get; set; } = state;
        public string CurrentVersion
        {
            get => CurrentState.BatchVersion;
            set => CurrentState = CurrentState with { BatchVersion = value };
        }
        public bool ChangeVersionInsideApply { get; set; }
        public int ApplyCalls { get; private set; }
        public Task<ReassignmentState?> LoadAsync(Guid batchJobId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ReassignmentState?>(CurrentState);

        public Task<bool> ApplyAsync(ReassignmentMutation mutation, string expectedBatchVersion, CancellationToken cancellationToken = default)
        {
            ApplyCalls++;
            if (ChangeVersionInsideApply) CurrentVersion = "version-raced";
            return Task.FromResult(string.Equals(CurrentVersion, expectedBatchVersion, StringComparison.Ordinal));
        }
    }
}
