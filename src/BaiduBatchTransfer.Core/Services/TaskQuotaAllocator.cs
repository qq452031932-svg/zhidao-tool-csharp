namespace BaiduBatchTransfer.Core.Services;

public sealed class TaskQuotaAllocator
{
    public IEnumerable<TaskQuotaAssignment> Allocate(
        IReadOnlyList<TaskQuotaAccount> accounts,
        IReadOnlyCollection<TaskQuotaCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(candidates);

        var orderedAccounts = accounts.OrderBy(account => account.PriorityOrder).ToArray();
        ValidateAccounts(orderedAccounts);
        var candidateSnapshot = candidates.ToArray();
        ValidateCandidatesAndTotals(orderedAccounts, candidateSnapshot);

        var remainingTargets = orderedAccounts.ToDictionary(
            account => account.AccountId,
            account => account.TargetCount);
        foreach (var candidate in candidateSnapshot)
        {
            if (candidate.CandidateConstraintAccountId is not { } accountId)
            {
                continue;
            }

            if (!remainingTargets.TryGetValue(accountId, out var remaining))
            {
                throw new InvalidOperationException("A locked candidate refers to an account outside the confirmed targets.");
            }

            if (remaining == 0)
            {
                throw new InvalidOperationException("A locked candidate count exceeds its account target.");
            }

            remainingTargets[accountId] = remaining - 1;
        }

        return AllocateIterator(orderedAccounts, candidateSnapshot, remainingTargets);
    }

    private static IEnumerable<TaskQuotaAssignment> AllocateIterator(
        IReadOnlyList<TaskQuotaAccount> orderedAccounts,
        IReadOnlyCollection<TaskQuotaCandidate> candidates,
        Dictionary<Guid, int> initialRemainingTargets)
    {
        var remainingTargets = new Dictionary<Guid, int>(initialRemainingTargets);
        using var owners = InterleaveOwners(orderedAccounts, remainingTargets).GetEnumerator();
        foreach (var candidate in candidates.OrderBy(candidate => candidate.CandidateSequence))
        {
            if (candidate.CandidateConstraintAccountId is { } lockedAccountId)
            {
                yield return new TaskQuotaAssignment(
                    candidate.ResourceId,
                    candidate.CandidateSequence,
                    lockedAccountId,
                    true);
                continue;
            }

            if (!owners.MoveNext())
            {
                throw new InvalidOperationException("Public candidates exceed the remaining account targets.");
            }

            yield return new TaskQuotaAssignment(
                candidate.ResourceId,
                candidate.CandidateSequence,
                owners.Current,
                false);
        }

        if (owners.MoveNext())
        {
            throw new InvalidOperationException("Account targets exceed the available public candidates.");
        }
    }

    private static IEnumerable<Guid> InterleaveOwners(
        IReadOnlyList<TaskQuotaAccount> orderedAccounts,
        Dictionary<Guid, int> remainingTargets)
    {
        var positive = orderedAccounts
            .Where(account => remainingTargets[account.AccountId] > 0)
            .ToArray();
        if (IsOneTwoOne(positive, remainingTargets))
        {
            var first = positive[0].AccountId;
            var second = positive[1].AccountId;
            var third = positive[2].AccountId;
            while (remainingTargets[first] > 0)
            {
                remainingTargets[first]--;
                yield return first;
                remainingTargets[second]--;
                yield return second;
                remainingTargets[second]--;
                yield return second;
                remainingTargets[third]--;
                yield return third;
            }

            yield break;
        }

        foreach (var accountId in SmoothInterleave(positive, remainingTargets))
        {
            yield return accountId;
        }
    }

    private static bool IsOneTwoOne(
        IReadOnlyList<TaskQuotaAccount> accounts,
        IReadOnlyDictionary<Guid, int> remainingTargets) =>
        accounts.Count == 3 &&
        remainingTargets[accounts[0].AccountId] == remainingTargets[accounts[2].AccountId] &&
        (long)remainingTargets[accounts[1].AccountId] ==
            2L * remainingTargets[accounts[0].AccountId];

    private static IEnumerable<Guid> SmoothInterleave(
        IReadOnlyList<TaskQuotaAccount> accounts,
        Dictionary<Guid, int> remainingTargets)
    {
        var weights = accounts.ToDictionary(
            account => account.AccountId,
            account => remainingTargets[account.AccountId]);
        var scores = accounts.ToDictionary(account => account.AccountId, _ => 0L);
        var totalWeight = weights.Values.Aggregate(0L, (total, weight) => checked(total + weight));

        for (long allocation = 0; allocation < totalWeight; allocation++)
        {
            TaskQuotaAccount? selected = null;
            foreach (var account in accounts)
            {
                if (remainingTargets[account.AccountId] == 0)
                {
                    continue;
                }

                scores[account.AccountId] = checked(scores[account.AccountId] + weights[account.AccountId]);
                if (selected is null || scores[account.AccountId] > scores[selected.AccountId])
                {
                    selected = account;
                }
            }

            if (selected is null)
            {
                throw new InvalidOperationException("Unable to make progress while interleaving account targets.");
            }

            scores[selected.AccountId] = checked(scores[selected.AccountId] - totalWeight);
            remainingTargets[selected.AccountId]--;
            yield return selected.AccountId;
        }
    }

    private static void ValidateAccounts(IReadOnlyList<TaskQuotaAccount> accounts)
    {
        if (accounts.Select(account => account.AccountId).Distinct().Count() != accounts.Count)
        {
            throw new ArgumentException("Account targets must be unique.", nameof(accounts));
        }

        if (accounts.Select(account => account.PriorityOrder).Distinct().Count() != accounts.Count)
        {
            throw new ArgumentException("Account priorities must be unique.", nameof(accounts));
        }
    }

    private static void ValidateCandidatesAndTotals(
        IReadOnlyList<TaskQuotaAccount> accounts,
        IReadOnlyCollection<TaskQuotaCandidate> candidates)
    {
        if (candidates.Select(candidate => candidate.ResourceId).Distinct().Count() != candidates.Count ||
            candidates.Select(candidate => candidate.CandidateSequence).Distinct().Count() != candidates.Count)
        {
            throw new ArgumentException("Candidates must have unique resource IDs and sequences.", nameof(candidates));
        }

        var targetCount = accounts.Sum(account => (long)account.TargetCount);
        if (targetCount != candidates.Count)
        {
            throw new InvalidOperationException("The sum of account targets must equal the ready resource count.");
        }
    }
}

public sealed record TaskQuotaAccount
{
    public TaskQuotaAccount(Guid accountId, int targetCount, int priorityOrder)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(targetCount);
        ArgumentOutOfRangeException.ThrowIfNegative(priorityOrder);
        AccountId = accountId;
        TargetCount = targetCount;
        PriorityOrder = priorityOrder;
    }

    public Guid AccountId { get; }

    public int TargetCount { get; }

    public int PriorityOrder { get; }
}

public sealed record TaskQuotaCandidate
{
    public TaskQuotaCandidate(Guid resourceId, int candidateSequence, Guid? candidateConstraintAccountId)
    {
        if (candidateSequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(candidateSequence));
        }

        ResourceId = resourceId;
        CandidateSequence = candidateSequence;
        CandidateConstraintAccountId = candidateConstraintAccountId;
    }

    public Guid ResourceId { get; }

    public int CandidateSequence { get; }

    public Guid? CandidateConstraintAccountId { get; }
}

public sealed record TaskQuotaAssignment(
    Guid ResourceId,
    int CandidateSequence,
    Guid AccountId,
    bool IsLocked);
