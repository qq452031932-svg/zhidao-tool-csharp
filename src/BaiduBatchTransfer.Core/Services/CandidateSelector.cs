using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Core.Services;

public sealed class CandidateSelector
{
    public AllocationCandidate? Select(
        Guid accountId,
        IEnumerable<AllocationCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        AllocationCandidate? exclusive = null;
        AllocationCandidate? publicCandidate = null;
        foreach (var candidate in candidates)
        {
            if (candidate.Status is not (CandidateStatus.Unallocated or CandidateStatus.Unused))
            {
                continue;
            }

            if (candidate.CandidateConstraintAccountId == accountId &&
                (exclusive is null || candidate.CandidateSequence < exclusive.CandidateSequence))
            {
                exclusive = candidate;
            }
            else if (candidate.CandidateConstraintAccountId is null &&
                     (publicCandidate is null || candidate.CandidateSequence < publicCandidate.CandidateSequence))
            {
                publicCandidate = candidate;
            }
        }

        return exclusive ?? publicCandidate;
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
