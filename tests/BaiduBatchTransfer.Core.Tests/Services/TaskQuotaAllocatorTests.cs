using BaiduBatchTransfer.Core.Services;

namespace BaiduBatchTransfer.Core.Tests.Services;

public sealed class TaskQuotaAllocatorTests
{
    [Fact]
    public void TwoHundredThousandResources_InterleaveOneToTwoToOneWithExactFinalCounts()
    {
        var a = Id(1);
        var b = Id(2);
        var c = Id(3);
        var accounts = new[]
        {
            new TaskQuotaAccount(a, 50_000, 0),
            new TaskQuotaAccount(b, 100_000, 1),
            new TaskQuotaAccount(c, 50_000, 2),
        };
        var candidates = Enumerable.Range(1, 200_000)
            .Select(sequence => new TaskQuotaCandidate(Id(sequence + 100), sequence, null))
            .ToArray();

        var assignments = new TaskQuotaAllocator().Allocate(accounts, candidates).ToArray();

        Assert.Equal(200_000, assignments.Length);
        Assert.Equal(50_000, assignments.Count(value => value.AccountId == a));
        Assert.Equal(100_000, assignments.Count(value => value.AccountId == b));
        Assert.Equal(50_000, assignments.Count(value => value.AccountId == c));
        Assert.Equal(new[] { a, b, b, c }, assignments.Take(4).Select(value => value.AccountId));
        Assert.All(
            assignments.Chunk(4).Take(1_000),
            chunk => Assert.Equal(new[] { 1, 2, 1 }, accounts.Select(account => chunk.Count(x => x.AccountId == account.AccountId))));
    }

    [Fact]
    public void LockedAssignments_CountTowardTargetsBeforePublicAllocation()
    {
        var a = Id(1);
        var b = Id(2);
        var candidates = new[]
        {
            Candidate(1, a),
            Candidate(2, null),
            Candidate(3, null),
        };

        var result = new TaskQuotaAllocator().Allocate(
            new[] { new TaskQuotaAccount(a, 1, 0), new TaskQuotaAccount(b, 2, 1) },
            candidates).ToArray();

        Assert.Equal(a, result.Single(value => value.CandidateSequence == 1).AccountId);
        Assert.Equal(2, result.Count(value => value.AccountId == b));
    }

    [Fact]
    public void LockedCountAboveTarget_IsRejected()
    {
        var a = Id(1);
        var candidates = new[] { Candidate(1, a), Candidate(2, a) };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new TaskQuotaAllocator().Allocate(
                new[] { new TaskQuotaAccount(a, 1, 0), new TaskQuotaAccount(Id(2), 1, 1) },
                candidates).ToArray());

        Assert.Contains("locked", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ZeroTarget_RemovesAccountFromAllocation()
    {
        var excluded = Id(1);
        var active = Id(2);

        var result = new TaskQuotaAllocator().Allocate(
            new[] { new TaskQuotaAccount(excluded, 0, 0), new TaskQuotaAccount(active, 2, 1) },
            new[] { Candidate(1, null), Candidate(2, null) }).ToArray();

        Assert.All(result, assignment => Assert.Equal(active, assignment.AccountId));
    }

    [Fact]
    public void SumOfTargets_MustEqualReadyResources()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new TaskQuotaAllocator().Allocate(
                new[] { new TaskQuotaAccount(Id(1), 1, 0) },
                new[] { Candidate(1, null), Candidate(2, null) }).ToArray());
    }

    [Fact]
    public void LazyAllocation_IsReplayableWithTheSameDeterministicOrder()
    {
        var assignments = new TaskQuotaAllocator().Allocate(
            new[]
            {
                new TaskQuotaAccount(Id(1), 1, 0),
                new TaskQuotaAccount(Id(2), 2, 1),
                new TaskQuotaAccount(Id(3), 1, 2),
            },
            Enumerable.Range(1, 4).Select(sequence => Candidate(sequence, null)).ToArray());

        var first = assignments.ToArray();
        var replay = assignments.ToArray();

        Assert.Equal(first, replay);
    }

    [Fact]
    public void MoreAccountsThanNominalCycleSize_StillAssignExactlyOncePerAccount()
    {
        var accounts = Enumerable.Range(1, 65)
            .Select(value => new TaskQuotaAccount(Id(value), 1, value - 1))
            .ToArray();
        var candidates = Enumerable.Range(1, 65)
            .Select(sequence => Candidate(sequence, null))
            .ToArray();

        var result = new TaskQuotaAllocator().Allocate(accounts, candidates).ToArray();

        Assert.Equal(65, result.Length);
        Assert.Equal(accounts.Select(account => account.AccountId), result.Select(value => value.AccountId));
    }

    private static TaskQuotaCandidate Candidate(int sequence, Guid? accountId) =>
        new(Id(sequence + 100), sequence, accountId);

    private static Guid Id(int value) => Guid.Parse($"00000000-0000-0000-0000-{value:D12}");
}
