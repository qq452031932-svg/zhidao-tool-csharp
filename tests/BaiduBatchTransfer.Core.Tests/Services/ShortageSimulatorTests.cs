using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Core.Services;

namespace BaiduBatchTransfer.Core.Tests.Services;

public sealed class ShortageSimulatorTests
{
    [Fact]
    public void ProportionalReduction_ReusesRatioAndCandidateSelectionWithDeterministicRounding()
    {
        var accounts = new[] { State(1, 2, 0), State(2, 3, 1), State(3, 2, 2) };
        var candidates = Enumerable.Range(1, 4).Select(sequence => Candidate(sequence, null)).ToArray();

        var result = new ShortageSimulator().Simulate(
            ShortagePolicy.ProportionalReduction,
            accounts,
            candidates);

        Assert.Equal(ShortageSimulationStatus.CompletedWithShortfall, result.Status);
        Assert.Equal(new[] { 1, 2, 1 }, accounts.Select(account => result.EffectiveTargets[account.AccountId]));
        Assert.Equal(new[] { Id(1), Id(2), Id(3), Id(2) }, result.Decisions.Select(value => value.AccountId));
        Assert.Equal(3, result.TotalShortfall);
    }

    [Fact]
    public void ProportionalReduction_UsesExclusiveCandidateBeforePublicForSelectedAccount()
    {
        var a = State(1, 1, 0);
        var b = State(2, 1, 1);
        var candidates = new[] { Candidate(1, null), Candidate(5, a.AccountId) };

        var result = new ShortageSimulator().Simulate(
            ShortagePolicy.ProportionalReduction,
            new[] { a, b },
            candidates);

        Assert.Equal(5, result.Decisions[0].CandidateSequence);
        Assert.Equal(1, result.Decisions[1].CandidateSequence);
    }

    [Fact]
    public void AccountPriority_FillsAccountsInConfirmedOrder()
    {
        var accounts = new[] { State(1, 2, 0), State(2, 2, 1) };
        var candidates = Enumerable.Range(1, 3).Select(sequence => Candidate(sequence, null)).ToArray();

        var result = new ShortageSimulator().Simulate(
            ShortagePolicy.AccountPriority,
            accounts,
            candidates);

        Assert.Equal(new[] { Id(1), Id(1), Id(2) }, result.Decisions.Select(value => value.AccountId));
        Assert.Equal(new[] { 2, 1 }, accounts.Select(account => result.EffectiveTargets[account.AccountId]));
    }

    [Fact]
    public void WaitForCandidates_PreservesTargetsAndReportsAwaitingShortfall()
    {
        var accounts = new[] { State(1, 2, 0), State(2, 2, 1) };

        var result = new ShortageSimulator().Simulate(
            ShortagePolicy.WaitForCandidates,
            accounts,
            new[] { Candidate(1, null) });

        Assert.Equal(ShortageSimulationStatus.AwaitingCandidates, result.Status);
        Assert.Equal(new[] { 2, 2 }, accounts.Select(account => result.EffectiveTargets[account.AccountId]));
        Assert.Equal(3, result.TotalShortfall);
    }

    [Fact]
    public void Exhaustion_IsNotFinalWhileResultPendingReservationRemains()
    {
        var pending = new SuccessTargetAccount(Id(1), 3, 3, 0, 1, 0);

        var result = new ShortageSimulator().Simulate(
            ShortagePolicy.ProportionalReduction,
            new[] { pending },
            Array.Empty<AllocationCandidate>());

        Assert.Equal(ShortageSimulationStatus.AwaitingCandidates, result.Status);
        Assert.Equal(3, result.EffectiveTargets[pending.AccountId]);
        Assert.Equal(2, result.TotalShortfall);
    }

    [Fact]
    public void FullyReservedTarget_IsStillNotFinalUntilPendingResultSettles()
    {
        var pending = new SuccessTargetAccount(Id(1), 1, 1, 0, 1, 0);

        var result = new ShortageSimulator().Simulate(
            ShortagePolicy.ProportionalReduction,
            new[] { pending },
            Array.Empty<AllocationCandidate>());

        Assert.Equal(ShortageSimulationStatus.AwaitingCandidates, result.Status);
    }

    [Fact]
    public void TwoHundredThousandCandidates_ArePreprocessedAndConsumedOnce()
    {
        const int candidateCount = 200_000;
        var accounts = new[] { State(1, 100_000, 0), State(2, 100_000, 1) };
        var candidates = Enumerable.Range(1, candidateCount)
            .Select(sequence => Candidate(sequence, null))
            .ToArray();

        var result = new ShortageSimulator().Simulate(
            ShortagePolicy.ProportionalReduction,
            accounts,
            candidates);

        Assert.Equal(candidateCount, result.Decisions.Count());
        Assert.Equal(candidateCount, result.CandidateInspectionCount);
        Assert.InRange(result.CandidateOperationCount, candidateCount, 4L * candidateCount + 2);
    }

    [Fact]
    public void ResultCollections_AreImmutableSnapshots()
    {
        var result = new ShortageSimulator().Simulate(
            ShortagePolicy.ProportionalReduction,
            new[] { State(1, 1, 0) },
            new[] { Candidate(1, null) });

        Assert.Equal(
            typeof(IReadOnlyDictionary<Guid, int>),
            typeof(ShortageSimulationResult).GetProperty(nameof(result.EffectiveTargets))!.PropertyType);
        Assert.Equal(
            typeof(IReadOnlyList<AllocationDecision>),
            typeof(ShortageSimulationResult).GetProperty(nameof(result.Decisions))!.PropertyType);

        var mutableTargets = Assert.IsAssignableFrom<IDictionary<Guid, int>>(result.EffectiveTargets);
        Assert.Throws<NotSupportedException>(() => mutableTargets.Add(Id(2), 1));
        var mutableDecisions = Assert.IsAssignableFrom<IList<AllocationDecision>>(result.Decisions);
        Assert.Throws<NotSupportedException>(() => mutableDecisions.Add(result.Decisions[0]));

        var legacyConstructor = typeof(ShortageSimulationResult).GetConstructor(
            new[]
            {
                typeof(IReadOnlyDictionary<Guid, int>),
                typeof(IReadOnlyList<AllocationDecision>),
                typeof(int),
                typeof(ShortageSimulationStatus),
            });
        Assert.NotNull(legacyConstructor);
        var legacyDeconstruct = typeof(ShortageSimulationResult).GetMethod(
            "Deconstruct",
            new[]
            {
                typeof(IReadOnlyDictionary<Guid, int>).MakeByRefType(),
                typeof(IReadOnlyList<AllocationDecision>).MakeByRefType(),
                typeof(int).MakeByRefType(),
                typeof(ShortageSimulationStatus).MakeByRefType(),
            });
        Assert.NotNull(legacyDeconstruct);
    }

    [Fact]
    public void ResultRecord_PreservesInitAndWithSemanticsWhileSnapshottingCollections()
    {
        var original = new ShortageSimulator().Simulate(
            ShortagePolicy.ProportionalReduction,
            new[] { State(1, 1, 0) },
            new[] { Candidate(1, null) });
        var replacementTargets = new Dictionary<Guid, int> { [Id(2)] = 2 };
        var replacementDecisions = new List<AllocationDecision>
        {
            new(Id(200), 100, Id(2), null),
        };

        var changed = original with
        {
            EffectiveTargets = replacementTargets,
            Decisions = replacementDecisions,
            Status = ShortageSimulationStatus.AwaitingCandidates,
        };
        replacementTargets.Clear();
        replacementDecisions.Clear();

        Assert.Equal(2, changed.EffectiveTargets[Id(2)]);
        Assert.Single(changed.Decisions);
        Assert.Equal(ShortageSimulationStatus.AwaitingCandidates, changed.Status);
    }

    [Fact]
    public void AggregateTargetsAndShortfall_UseCheckedLongArithmetic()
    {
        var accounts = new[]
        {
            new SuccessTargetAccount(Id(1), int.MaxValue, int.MaxValue, 0, 0, 0),
            new SuccessTargetAccount(Id(2), int.MaxValue, int.MaxValue, 0, 0, 1),
        };

        var result = new ShortageSimulator().Simulate(
            ShortagePolicy.WaitForCandidates,
            accounts,
            Array.Empty<AllocationCandidate>());

        Assert.Equal(4_294_967_294L, result.TotalEffectiveTarget);
        Assert.Equal(4_294_967_294L, result.TotalShortfall);
    }

    private static SuccessTargetAccount State(int id, int target, int priority) =>
        new(Id(id), target, target, 0, 0, priority);

    private static AllocationCandidate Candidate(int sequence, Guid? constraint) =>
        new(Id(sequence + 100), sequence, constraint, CandidateStatus.Unallocated);

    private static Guid Id(int value) => Guid.Parse($"00000000-0000-0000-0000-{value:D12}");
}
