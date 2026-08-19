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
        var available = candidates
            .Where(candidate => candidate.Status is CandidateStatus.Unallocated or CandidateStatus.Unused)
            .ToList();
        var decisions = new List<AllocationDecision>(available.Count);

        while (true)
        {
            var eligibleAccountIds = current.Values
                .Where(account => account.AvailableSlots > 0)
                .Where(account => _candidateSelector.Select(account.AccountId, available) is not null)
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

            var candidate = _candidateSelector.Select(account.AccountId, available);
            if (candidate is null)
            {
                break;
            }

            decisions.Add(new AllocationDecision(
                candidate.ResourceId,
                candidate.CandidateSequence,
                account.AccountId,
                candidate.CandidateConstraintAccountId));
            available.Remove(candidate);
            current[account.AccountId] = account.AddSimulatedFulfillment();
        }

        var hasPendingReservations = accounts.Any(account => account.ReservedSlotCount > 0);
        var effectiveTargets = accounts.ToDictionary(
            account => account.AccountId,
            account => policy == ShortagePolicy.WaitForCandidates || hasPendingReservations
                ? account.EffectiveTarget
                : current[account.AccountId].FulfilledCount + current[account.AccountId].ReservedSlotCount);
        var totalShortfall = current.Values.Sum(account =>
            account.OriginalTarget - account.FulfilledCount - account.ReservedSlotCount);
        var status = hasPendingReservations
            ? ShortageSimulationStatus.AwaitingCandidates
            : totalShortfall == 0
                ? ShortageSimulationStatus.Completed
                : policy == ShortagePolicy.WaitForCandidates
                ? ShortageSimulationStatus.AwaitingCandidates
                : ShortageSimulationStatus.CompletedWithShortfall;

        return new ShortageSimulationResult(
            effectiveTargets,
            decisions,
            totalShortfall,
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

public sealed record ShortageSimulationResult(
    IReadOnlyDictionary<Guid, int> EffectiveTargets,
    IReadOnlyList<AllocationDecision> Decisions,
    int TotalShortfall,
    ShortageSimulationStatus Status);
