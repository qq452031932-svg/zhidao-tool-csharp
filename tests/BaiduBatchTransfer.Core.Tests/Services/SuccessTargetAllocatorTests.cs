using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Core.Services;

namespace BaiduBatchTransfer.Core.Tests.Services;

public sealed class SuccessTargetAllocatorTests
{
    [Fact]
    public void MinimumProgressRatio_UsesFulfilledPlusReservedAndPriorityTieBreak()
    {
        var a = State(1, original: 4, effective: 4, fulfilled: 1, reserved: 1, priority: 0);
        var b = State(2, original: 2, effective: 2, fulfilled: 1, reserved: 0, priority: 1);

        Assert.Equal(a.AccountId, new SuccessTargetAllocator().SelectAccount(new[] { a, b })!.AccountId);
        Assert.Equal(a.AccountId, new SuccessTargetAllocator().SelectAccount(new[] { State(1, 2, 2, 1, 0, 0), b })!.AccountId);
    }

    [Fact]
    public void RatioComparison_IsOverflowSafe()
    {
        var a = State(1, int.MaxValue, int.MaxValue, int.MaxValue - 1, 0, 0);
        var b = State(2, int.MaxValue - 1, int.MaxValue - 1, int.MaxValue - 2, 0, 1);

        var selected = new SuccessTargetAllocator().SelectAccount(new[] { a, b });

        Assert.Equal(b.AccountId, selected!.AccountId);
    }

    [Fact]
    public void TargetZeroAndAccountsWithoutSlots_AreExcluded()
    {
        var zero = State(1, 0, 0, 0, 0, 0);
        var full = State(2, 1, 1, 1, 0, 1);
        var eligible = State(3, 2, 2, 0, 0, 2);

        Assert.Equal(eligible.AccountId, new SuccessTargetAllocator().SelectAccount(new[] { zero, full, eligible })!.AccountId);
    }

    [Fact]
    public void PendingReservation_PreventsReplacementBeyondTarget()
    {
        var state = State(1, original: 2, effective: 2, fulfilled: 1, reserved: 1, priority: 0);

        Assert.Equal(0, state.AvailableSlots);
        Assert.Null(new SuccessTargetAllocator().SelectAccount(new[] { state }));
    }

    [Fact]
    public void CandidateSelector_PrefersSmallestExclusiveThenSmallestPublic()
    {
        var account = Id(1);
        var other = Id(2);
        var candidates = new[]
        {
            Candidate(1, null),
            Candidate(5, account),
            Candidate(3, account),
            Candidate(2, other),
        };

        var result = new CandidateSelector().Select(account, candidates);

        Assert.Equal(3, result!.CandidateSequence);
        Assert.Equal(account, result.CandidateConstraintAccountId);
        Assert.Null(new CandidateSelector().Select(Id(3), new[] { Candidate(2, other) }));
    }

    [Fact]
    public void CandidateSelector_RepresentsFulfilledAndReusableUnusedExclusiveStates()
    {
        var account = Id(1);
        var result = new CandidateSelector().Select(account, new[]
        {
            Candidate(1, account, CandidateStatus.Expanded),
            Candidate(2, account, CandidateStatus.Unused),
            Candidate(3, null, CandidateStatus.Unallocated),
        });

        Assert.Equal(2, result!.CandidateSequence);
    }

    private static SuccessTargetAccount State(
        int id,
        int original,
        int effective,
        int fulfilled,
        int reserved,
        int priority) => new(Id(id), original, effective, fulfilled, reserved, priority);

    private static AllocationCandidate Candidate(
        int sequence,
        Guid? constraint,
        CandidateStatus status = CandidateStatus.Unallocated) =>
        new(Id(sequence + 100), sequence, constraint, status);

    private static Guid Id(int value) => Guid.Parse($"00000000-0000-0000-0000-{value:D12}");
}
