namespace BaiduBatchTransfer.Core.Domain;

public sealed class ResourceItem
{
    public ResourceItem(
        Guid id,
        Guid batchJobId,
        Guid stagedImportRowId,
        int candidateSequence,
        string shareUrl,
        string normalizedShareId,
        DateTimeOffset createdAt)
    {
        if (candidateSequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(candidateSequence));
        }

        Id = id;
        BatchJobId = batchJobId;
        StagedImportRowId = stagedImportRowId;
        CandidateSequence = candidateSequence;
        ShareUrl = RequireText(shareUrl, nameof(shareUrl));
        NormalizedShareId = RequireText(normalizedShareId, nameof(normalizedShareId));
        CandidateStatus = CandidateStatus.Unallocated;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public Guid Id { get; }

    public Guid BatchJobId { get; }

    public Guid StagedImportRowId { get; }

    public int CandidateSequence { get; }

    public string ShareUrl { get; }

    public string NormalizedShareId { get; }

    public CandidateStatus CandidateStatus { get; private set; }

    public Guid? ReservedAccountId { get; private set; }

    public DateTimeOffset? ReservationExpiresAt { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public void Reserve(Guid accountId, DateTimeOffset expiresAt, DateTimeOffset updatedAt)
    {
        if (CandidateStatus is not (CandidateStatus.Unallocated or CandidateStatus.Unused))
        {
            throw new InvalidOperationException($"Cannot reserve a candidate in {CandidateStatus} status.");
        }

        EnsureTimestamp(updatedAt);
        if (expiresAt <= updatedAt)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresAt), "Reservation must expire in the future.");
        }

        CandidateStatus = CandidateStatus.Reserved;
        ReservedAccountId = accountId;
        ReservationExpiresAt = expiresAt;
        UpdatedAt = updatedAt;
    }

    public void MarkExpanded(DateTimeOffset updatedAt)
    {
        EnsureStatus(CandidateStatus.Reserved);
        EnsureTimestamp(updatedAt);
        CandidateStatus = CandidateStatus.Expanded;
        ReservationExpiresAt = null;
        UpdatedAt = updatedAt;
    }

    public void ReleaseAsUnused(DateTimeOffset updatedAt)
    {
        EnsureStatus(CandidateStatus.Reserved);
        EnsureTimestamp(updatedAt);
        CandidateStatus = CandidateStatus.Unused;
        ReservedAccountId = null;
        ReservationExpiresAt = null;
        UpdatedAt = updatedAt;
    }

    public void MarkGloballyInvalid(DateTimeOffset updatedAt)
    {
        if (CandidateStatus is not (
            CandidateStatus.Unallocated or
            CandidateStatus.Reserved or
            CandidateStatus.Unused))
        {
            throw new InvalidOperationException($"Cannot invalidate a candidate in {CandidateStatus} status.");
        }

        EnsureTimestamp(updatedAt);
        CandidateStatus = CandidateStatus.GloballyInvalid;
        ReservedAccountId = null;
        ReservationExpiresAt = null;
        UpdatedAt = updatedAt;
    }

    private void EnsureStatus(CandidateStatus expected)
    {
        if (CandidateStatus != expected)
        {
            throw new InvalidOperationException($"Expected candidate status {expected}, but was {CandidateStatus}.");
        }
    }

    private void EnsureTimestamp(DateTimeOffset timestamp)
    {
        if (timestamp < UpdatedAt)
        {
            throw new ArgumentOutOfRangeException(nameof(timestamp), "Timestamp cannot move backwards.");
        }
    }

    private static string RequireText(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A non-empty value is required.", parameterName)
            : value;
}
