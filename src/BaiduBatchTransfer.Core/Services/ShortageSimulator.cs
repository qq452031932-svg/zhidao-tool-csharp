using System.Collections.Immutable;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Core.Services;

public sealed class ShortageSimulator
{
    private readonly SuccessTargetAllocator _accountSelector = new();
    private readonly CandidateSelector _candidateSelector = new();

    public ShortageSimulationResult Simulate(
        ShortagePolicy policy,
        IReadOnlyList<SuccessTargetAccount> accounts,
        IReadOnlyCollection<AllocationCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(candidates);
        Validate(accounts, candidates);

        var current = accounts.ToDictionary(account => account.AccountId);
        var candidatePool = _candidateSelector.Prepare(candidates);
        var decisions = new List<AllocationDecision>(candidatePool.AvailableCount);

        while (true)
        {
            var eligibleAccountIds = current.Values
                .Where(account => account.AvailableSlots > 0)
                .Where(account => candidatePool.HasCandidate(account.AccountId))
                .Select(account => account.AccountId)
                .ToHashSet();
            if (eligibleAccountIds.Count == 0)
            {
                break;
            }

            var account = policy == ShortagePolicy.AccountPriority
                ? current.Values
                    .Where(value => eligibleAccountIds.Contains(value.AccountId))
                    .OrderBy(value => value.PriorityOrder)
                    .FirstOrDefault()
                : _accountSelector.SelectAccount(current.Values, eligibleAccountIds);
            if (account is null)
            {
                break;
            }

            var candidate = candidatePool.Take(account.AccountId);
            if (candidate is null)
            {
                break;
            }

            decisions.Add(new AllocationDecision(
                candidate.ResourceId,
                candidate.CandidateSequence,
                account.AccountId,
                candidate.CandidateConstraintAccountId));
            current[account.AccountId] = account.AddSimulatedFulfillment();
        }

        var hasPendingReservations = accounts.Any(account => account.ReservedSlotCount > 0);
        var effectiveTargets = accounts.ToImmutableDictionary(
            account => account.AccountId,
            account => policy == ShortagePolicy.WaitForCandidates || hasPendingReservations
                ? account.EffectiveTarget
                : current[account.AccountId].FulfilledCount + current[account.AccountId].ReservedSlotCount);
        long totalEffectiveTarget = 0;
        long totalShortfall = 0;
        checked
        {
            foreach (var account in current.Values)
            {
                totalEffectiveTarget += effectiveTargets[account.AccountId];
                totalShortfall += (long)account.OriginalTarget -
                    account.FulfilledCount -
                    account.ReservedSlotCount;
            }
        }
        var status = hasPendingReservations
            ? ShortageSimulationStatus.AwaitingCandidates
            : totalShortfall == 0
                ? ShortageSimulationStatus.Completed
                : policy == ShortagePolicy.WaitForCandidates
                ? ShortageSimulationStatus.AwaitingCandidates
                : ShortageSimulationStatus.CompletedWithShortfall;

        return new ShortageSimulationResult(
            effectiveTargets,
            decisions.ToImmutableArray(),
            totalEffectiveTarget,
            totalShortfall,
            candidatePool.CandidateInspectionCount,
            candidatePool.CandidateOperationCount,
            status);
    }

    private static void Validate(
        IReadOnlyList<SuccessTargetAccount> accounts,
        IReadOnlyCollection<AllocationCandidate> candidates)
    {
        if (accounts.Select(account => account.AccountId).Distinct().Count() != accounts.Count)
        {
            throw new ArgumentException("Accounts must be unique.", nameof(accounts));
        }

        if (accounts.Select(account => account.PriorityOrder).Distinct().Count() != accounts.Count)
        {
            throw new ArgumentException("Account priorities must be unique.", nameof(accounts));
        }

        if (candidates.Select(candidate => candidate.ResourceId).Distinct().Count() != candidates.Count ||
            candidates.Select(candidate => candidate.CandidateSequence).Distinct().Count() != candidates.Count)
        {
            throw new ArgumentException("Candidates must have unique resource IDs and sequences.", nameof(candidates));
        }
    }
}

public enum ShortageSimulationStatus
{
    Completed,
    CompletedWithShortfall,
    AwaitingCandidates,
}

public sealed record ShortageSimulationResult
{
    private ImmutableDictionary<Guid, int> _effectiveTargets = ImmutableDictionary<Guid, int>.Empty;
    private ImmutableList<AllocationDecision> _decisions = ImmutableList<AllocationDecision>.Empty;

    public ShortageSimulationResult(
        IReadOnlyDictionary<Guid, int> effectiveTargets,
        IReadOnlyList<AllocationDecision> decisions,
        long totalEffectiveTarget,
        long totalShortfall,
        int candidateInspectionCount,
        long candidateOperationCount,
        ShortageSimulationStatus status)
    {
        ArgumentNullException.ThrowIfNull(effectiveTargets);
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentOutOfRangeException.ThrowIfNegative(totalEffectiveTarget);
        ArgumentOutOfRangeException.ThrowIfNegative(totalShortfall);
        ArgumentOutOfRangeException.ThrowIfNegative(candidateInspectionCount);
        ArgumentOutOfRangeException.ThrowIfNegative(candidateOperationCount);

        EffectiveTargets = effectiveTargets.ToImmutableDictionary();
        Decisions = decisions.ToImmutableList();
        TotalEffectiveTarget = totalEffectiveTarget;
        TotalShortfall = totalShortfall;
        CandidateInspectionCount = candidateInspectionCount;
        CandidateOperationCount = candidateOperationCount;
        Status = status;
    }

    public ShortageSimulationResult(
        IReadOnlyDictionary<Guid, int> effectiveTargets,
        IReadOnlyList<AllocationDecision> decisions,
        int totalShortfall,
        ShortageSimulationStatus status)
        : this(
            effectiveTargets,
            decisions,
            SumTargets(effectiveTargets),
            totalShortfall,
            0,
            0,
            status)
    {
    }

    public IReadOnlyDictionary<Guid, int> EffectiveTargets
    {
        get => _effectiveTargets;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _effectiveTargets = value.ToImmutableDictionary();
        }
    }

    public IReadOnlyList<AllocationDecision> Decisions
    {
        get => _decisions;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _decisions = value.ToImmutableList();
        }
    }

    public long TotalEffectiveTarget { get; init; }

    public long TotalShortfall { get; init; }

    public int CandidateInspectionCount { get; init; }

    public long CandidateOperationCount { get; init; }

    public ShortageSimulationStatus Status { get; init; }

    public void Deconstruct(
        out IReadOnlyDictionary<Guid, int> effectiveTargets,
        out IReadOnlyList<AllocationDecision> decisions,
        out int totalShortfall,
        out ShortageSimulationStatus status)
    {
        effectiveTargets = EffectiveTargets;
        decisions = Decisions;
        totalShortfall = checked((int)TotalShortfall);
        status = Status;
    }

    private static long SumTargets(IReadOnlyDictionary<Guid, int> effectiveTargets)
    {
        ArgumentNullException.ThrowIfNull(effectiveTargets);
        return effectiveTargets.Values.Aggregate(
            0L,
            (total, target) => checked(total + target));
    }
}
