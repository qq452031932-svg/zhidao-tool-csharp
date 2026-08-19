using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Core.Services;

public sealed class CandidateSelector
{
    public AllocationCandidate? Select(
        Guid accountId,
        IEnumerable<AllocationCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return Prepare(candidates).Take(accountId);
    }

    public CandidatePool Prepare(IEnumerable<AllocationCandidate> candidates) => new(candidates);
}

public sealed class CandidatePool
{
    private readonly Dictionary<Guid, Queue<AllocationCandidate>> _exclusive = new();
    private readonly Queue<AllocationCandidate> _public = new();

    internal CandidatePool(IEnumerable<AllocationCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var available = new List<AllocationCandidate>();
        foreach (var candidate in candidates)
        {
            CandidateInspectionCount = checked(CandidateInspectionCount + 1);
            CandidateOperationCount = checked(CandidateOperationCount + 1);
            if (candidate.Status is CandidateStatus.Unallocated or CandidateStatus.Unused)
            {
                available.Add(candidate);
            }
        }

        foreach (var candidate in available.OrderBy(candidate => candidate.CandidateSequence))
        {
            if (candidate.CandidateConstraintAccountId is not { } accountId)
            {
                _public.Enqueue(candidate);
                continue;
            }

            if (!_exclusive.TryGetValue(accountId, out var queue))
            {
                queue = new Queue<AllocationCandidate>();
                _exclusive.Add(accountId, queue);
            }

            queue.Enqueue(candidate);
        }

        AvailableCount = available.Count;
    }

    public int CandidateInspectionCount { get; }

    public long CandidateOperationCount { get; private set; }

    public int AvailableCount { get; private set; }

    public bool HasCandidate(Guid accountId)
    {
        CandidateOperationCount = checked(CandidateOperationCount + 1);
        return (_exclusive.TryGetValue(accountId, out var queue) && queue.Count > 0) ||
            _public.Count > 0;
    }

    public AllocationCandidate? Take(Guid accountId)
    {
        CandidateOperationCount = checked(CandidateOperationCount + 1);
        AllocationCandidate? candidate = null;
        if (_exclusive.TryGetValue(accountId, out var queue) && queue.Count > 0)
        {
            candidate = queue.Dequeue();
        }
        else if (_public.Count > 0)
        {
            candidate = _public.Dequeue();
        }

        if (candidate is not null)
        {
            AvailableCount--;
        }

        return candidate;
    }
}

public sealed record AllocationCandidate
{
    public AllocationCandidate(
        Guid resourceId,
        int candidateSequence,
        Guid? candidateConstraintAccountId,
        CandidateStatus status)
    {
        if (candidateSequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(candidateSequence));
        }

        ResourceId = resourceId;
        CandidateSequence = candidateSequence;
        CandidateConstraintAccountId = candidateConstraintAccountId;
        Status = status;
    }

    public Guid ResourceId { get; }

    public int CandidateSequence { get; }

    public Guid? CandidateConstraintAccountId { get; }

    public CandidateStatus Status { get; }
}

public sealed record AllocationDecision(
    Guid ResourceId,
    int CandidateSequence,
    Guid AccountId,
    Guid? CandidateConstraintAccountId);
