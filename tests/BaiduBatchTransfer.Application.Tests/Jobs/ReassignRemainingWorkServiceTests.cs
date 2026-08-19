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
            [Allocation(failed, 5), Allocation(replacement, 0)],
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
    public async Task SuccessTargetPreviewChangesTargetsButNeverReservedOrExclusiveCandidates()
    {
        var batch = Guid.NewGuid();
        var failed = Guid.NewGuid();
        var replacement = Guid.NewGuid();
        var state = new ReassignmentState(
            batch,
            "version-1",
            QuotaBasis.SuccessTarget,
            [Allocation(failed, 3), Allocation(replacement, 1)],
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
    public async Task ApplyRequiresExactConfirmationTokenAndCurrentBatchVersion()
    {
        var batch = Guid.NewGuid();
        var failed = Guid.NewGuid();
        var replacement = Guid.NewGuid();
        var state = new ReassignmentState(
            batch, "version-1", QuotaBasis.TaskCount,
            [Allocation(failed, 1), Allocation(replacement, 0)],
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
        Assert.Equal(1, store.ApplyCalls);
    }

    [Fact]
    public async Task PreviewRejectsChangesThatDoNotRedistributeOnlyTheFailedAccountTarget()
    {
        var batch = Guid.NewGuid();
        var failed = Guid.NewGuid();
        var healthyA = Guid.NewGuid();
        var healthyB = Guid.NewGuid();
        var state = new ReassignmentState(batch, "version-1", QuotaBasis.SuccessTarget,
            [Allocation(failed, 2), Allocation(healthyA, 2), Allocation(healthyB, 0)], [], []);
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
            [new ReassignmentAllocation(failed, 3, 3, 2), Allocation(replacement, 0)], [], []);
        var service = new ReassignRemainingWorkService(new RecordingReassignmentStore(state));

        await Assert.ThrowsAsync<ReassignmentValidationException>(() => service.PreviewAsync(
            new ReassignmentRequest(batch, failed,
                new Dictionary<Guid, int> { [failed] = 1, [replacement] = 2 })));
    }

    private static ReassignmentAllocation Allocation(Guid account, int target) =>
        new(account, target, target);

    private static ReassignmentOperation Operation(
        Guid account,
        long sequence,
        string status,
        string submitState,
        bool exclusive) =>
        new(Guid.NewGuid(), sequence, account, status, submitState, exclusive);

    private sealed class RecordingReassignmentStore(ReassignmentState state) : IReassignmentStore
    {
        public string CurrentVersion { get; set; } = state.BatchVersion;
        public int ApplyCalls { get; private set; }
        public Task<ReassignmentState?> LoadAsync(Guid batchJobId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ReassignmentState?>(state with { BatchVersion = CurrentVersion });

        public Task<bool> ApplyAsync(ReassignmentMutation mutation, string expectedBatchVersion, CancellationToken cancellationToken = default)
        {
            ApplyCalls++;
            return Task.FromResult(string.Equals(CurrentVersion, expectedBatchVersion, StringComparison.Ordinal));
        }
    }
}
