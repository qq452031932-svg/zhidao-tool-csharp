namespace BaiduBatchTransfer.Core.Services;

public sealed class TaskQuotaAllocator
{
    private const int MaximumCycleSize = 64;

    public IEnumerable<TaskQuotaAssignment> Allocate(
        IReadOnlyList<TaskQuotaAccount> accounts,
        IReadOnlyCollection<TaskQuotaCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(candidates);

        var orderedAccounts = accounts.OrderBy(account => account.PriorityOrder).ToArray();
        ValidateInputs(orderedAccounts, candidates);

        var remainingTargets = orderedAccounts.ToDictionary(
            account => account.AccountId,
            account => account.TargetCount);
        foreach (var candidate in candidates)
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

        return AllocateIterator(orderedAccounts, candidates, remainingTargets);
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
        while (remainingTargets.Values.Any(count => count > 0))
        {
            var cycle = BuildCycle(orderedAccounts, remainingTargets);
            var emitted = false;
            foreach (var accountId in cycle)
            {
                if (remainingTargets[accountId] == 0)
                {
                    continue;
                }

                remainingTargets[accountId]--;
                emitted = true;
                yield return accountId;
            }

            if (!emitted)
            {
                throw new InvalidOperationException("Unable to make progress while interleaving account targets.");
            }
        }
    }

    private static IReadOnlyList<Guid> BuildCycle(
        IReadOnlyList<TaskQuotaAccount> orderedAccounts,
        IReadOnlyDictionary<Guid, int> remainingTargets)
    {
        var positive = orderedAccounts
            .Where(account => remainingTargets[account.AccountId] > 0)
            .ToArray();
        var gcd = positive
            .Select(account => remainingTargets[account.AccountId])
            .Aggregate(GreatestCommonDivisor);
        var exactCycleSize = positive.Sum(account => (long)remainingTargets[account.AccountId] / gcd);

        var cycleSize = Math.Max(MaximumCycleSize, positive.Length);
        IReadOnlyDictionary<Guid, int> cycleCounts = exactCycleSize <= cycleSize
            ? positive.ToDictionary(
                account => account.AccountId,
                account => remainingTargets[account.AccountId] / gcd)
            : ScaleCycle(positive, remainingTargets, cycleSize);

        return positive
            .SelectMany(account => Enumerable.Repeat(account.AccountId, cycleCounts[account.AccountId]))
            .ToArray();
    }

    private static IReadOnlyDictionary<Guid, int> ScaleCycle(
        IReadOnlyList<TaskQuotaAccount> accounts,
        IReadOnlyDictionary<Guid, int> remainingTargets,
        int cycleSize)
    {
        var total = accounts.Sum(account => (long)remainingTargets[account.AccountId]);
        var counts = accounts.ToDictionary(
            account => account.AccountId,
            account => Math.Max(1, (int)((long)remainingTargets[account.AccountId] * cycleSize / total)));

        while (counts.Values.Sum() > cycleSize)
        {
            var account = accounts
                .Where(value => counts[value.AccountId] > 1)
                .OrderBy(value =>
                    (decimal)remainingTargets[value.AccountId] / total -
                    (decimal)counts[value.AccountId] / cycleSize)
                .ThenByDescending(value => value.PriorityOrder)
                .First();
            counts[account.AccountId]--;
        }

        while (counts.Values.Sum() < cycleSize)
        {
            var account = accounts
                .OrderByDescending(value =>
                    (decimal)remainingTargets[value.AccountId] / total -
                    (decimal)counts[value.AccountId] / cycleSize)
                .ThenBy(value => value.PriorityOrder)
                .First();
            counts[account.AccountId]++;
        }

        return counts;
    }

    private static int GreatestCommonDivisor(int left, int right)
    {
        while (right != 0)
        {
            (left, right) = (right, left % right);
        }

        return left;
    }

    private static void ValidateInputs(
        IReadOnlyList<TaskQuotaAccount> accounts,
        IReadOnlyCollection<TaskQuotaCandidate> candidates)
    {
        if (accounts.Select(account => account.AccountId).Distinct().Count() != accounts.Count)
        {
            throw new ArgumentException("Account targets must be unique.", nameof(accounts));
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
