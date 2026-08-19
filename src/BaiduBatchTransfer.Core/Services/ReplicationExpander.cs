namespace BaiduBatchTransfer.Core.Services;

public sealed class ReplicationExpander
{
    public IEnumerable<ReplicationIntent> Expand(
        IEnumerable<ReplicationResource> resources,
        IReadOnlyList<Guid> selectedAccountIds)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(selectedAccountIds);

        var accountSnapshot = selectedAccountIds.ToArray();
        if (accountSnapshot.Length == 0)
        {
            throw new ArgumentException("At least one account must be selected.", nameof(selectedAccountIds));
        }

        if (accountSnapshot.Length != accountSnapshot.Distinct().Count())
        {
            throw new ArgumentException("Selected accounts must be unique.", nameof(selectedAccountIds));
        }

        var resourceSnapshot = resources.ToArray();
        return ExpandIterator(resourceSnapshot, accountSnapshot);
    }

    public static long EstimateIntentCount(long resourceCount, long accountCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(resourceCount);
        ArgumentOutOfRangeException.ThrowIfNegative(accountCount);
        return checked(resourceCount * accountCount);
    }

    private static IEnumerable<ReplicationIntent> ExpandIterator(
        IEnumerable<ReplicationResource> resources,
        IReadOnlyList<Guid> accountIds)
    {
        long ordinal = 0;
        foreach (var resource in resources)
        {
            foreach (var accountId in accountIds)
            {
                ordinal = checked(ordinal + 1);
                yield return new ReplicationIntent(
                    resource.ResourceId,
                    resource.CandidateSequence,
                    accountId,
                    ordinal);
            }
        }
    }
}

public sealed record ReplicationResource(Guid ResourceId, int CandidateSequence)
{
    public int CandidateSequence { get; } = CandidateSequence > 0
        ? CandidateSequence
        : throw new ArgumentOutOfRangeException(nameof(CandidateSequence));
}

public sealed record ReplicationIntent(
    Guid ResourceId,
    int CandidateSequence,
    Guid AccountId,
    long AllocationOrdinal);
