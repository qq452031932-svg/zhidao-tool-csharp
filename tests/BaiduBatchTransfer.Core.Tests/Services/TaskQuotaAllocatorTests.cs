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

    [Fact]
    public void CoprimeTargets_AreWeightedFairAcrossEveryPrefix()
    {
        var a = Id(1);
        var b = Id(2);
        var assignments = new TaskQuotaAllocator().Allocate(
            new[] { new TaskQuotaAccount(a, 100, 0), new TaskQuotaAccount(b, 101, 1) },
            Enumerable.Range(1, 201).Select(sequence => Candidate(sequence, null)).ToArray())
            .ToArray();

        Assert.Equal(100, assignments.Count(value => value.AccountId == a));
        Assert.Equal(101, assignments.Count(value => value.AccountId == b));
        Assert.True(MaximumRunLength(assignments.Select(value => value.AccountId)) <= 2);

        var assignedToA = 0;
        for (var prefixLength = 1; prefixLength <= assignments.Length; prefixLength++)
        {
            assignedToA += assignments[prefixLength - 1].AccountId == a ? 1 : 0;
            var ideal = (decimal)prefixLength * 100 / 201;
            Assert.InRange(Math.Abs(assignedToA - ideal), 0m, 1m);
        }
    }

    [Fact]
    public void SmallCoprimeCycle_IsAlsoWeightedFairInsteadOfGrouped()
    {
        var a = Id(1);
        var b = Id(2);

        var assignments = new TaskQuotaAllocator().Allocate(
            new[] { new TaskQuotaAccount(a, 31, 0), new TaskQuotaAccount(b, 32, 1) },
            Enumerable.Range(1, 63).Select(sequence => Candidate(sequence, null)).ToArray())
            .ToArray();

        Assert.True(MaximumRunLength(assignments.Select(value => value.AccountId)) <= 2);
    }

    [Fact]
    public void CallTimeSnapshot_PreventsMutationFromChangingDeferredAllocation()
    {
        var a = Id(1);
        var b = Id(2);
        var accounts = new List<TaskQuotaAccount>
        {
            new(a, 1, 0),
            new(b, 1, 1),
        };
        var candidates = new List<TaskQuotaCandidate>
        {
            Candidate(1, null),
            Candidate(2, null),
        };

        var allocation = new TaskQuotaAllocator().Allocate(accounts, candidates);
        accounts.Clear();
        accounts.Add(new TaskQuotaAccount(Id(3), 2, 0));
        candidates.Clear();
        candidates.Add(Candidate(3, null));

        var result = allocation.ToArray();
        Assert.Equal(2, result.Length);
        Assert.Equal(new[] { a, b }, result.Select(value => value.AccountId));
        Assert.Equal(new[] { 1, 2 }, result.Select(value => value.CandidateSequence));
    }

    [Fact]
    public void AggregateTargetValidation_UsesLongWithoutOverflow()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            new TaskQuotaAllocator().Allocate(
                new[]
                {
                    new TaskQuotaAccount(Id(1), int.MaxValue, 0),
                    new TaskQuotaAccount(Id(2), int.MaxValue, 1),
                },
                Array.Empty<TaskQuotaCandidate>()).ToArray());

        Assert.Contains("sum", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InvalidAccounts_AreRejectedBeforeCandidateEnumeration()
    {
        var duplicateAccount = Id(1);
        var accounts = new[]
        {
            new TaskQuotaAccount(duplicateAccount, 0, 0),
            new TaskQuotaAccount(duplicateAccount, 0, 1),
        };

        Assert.Throws<ArgumentException>(() =>
            new TaskQuotaAllocator().Allocate(accounts, new ThrowingCandidates()));
    }

    private static int MaximumRunLength(IEnumerable<Guid> owners)
    {
        var maximum = 0;
        var current = 0;
        Guid? previous = null;
        foreach (var owner in owners)
        {
            current = owner == previous ? current + 1 : 1;
            maximum = Math.Max(maximum, current);
            previous = owner;
        }

        return maximum;
    }

    private static TaskQuotaCandidate Candidate(int sequence, Guid? accountId) =>
        new(Id(sequence + 100), sequence, accountId);

    private static Guid Id(int value) => Guid.Parse($"00000000-0000-0000-0000-{value:D12}");

    private sealed class ThrowingCandidates : IReadOnlyCollection<TaskQuotaCandidate>
    {
        public int Count => 0;

        public IEnumerator<TaskQuotaCandidate> GetEnumerator() =>
            throw new InvalidOperationException("Candidates must not be enumerated.");

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
