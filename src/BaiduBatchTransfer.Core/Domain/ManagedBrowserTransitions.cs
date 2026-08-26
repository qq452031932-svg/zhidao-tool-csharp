namespace BaiduBatchTransfer.Core.Domain;

public enum ManagedBrowserTransitionStatus
{
    Applied = 1,
    Conflict = 2,
    NotFound = 3
}

public sealed record ManagedBrowserTransitionRequest
{
    public ManagedBrowserTransitionRequest(Guid identityId, long expectedRevision)
    {
        if (identityId == Guid.Empty)
            throw new ArgumentException("An identity ID is required.", nameof(identityId));
        if (expectedRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(expectedRevision), expectedRevision, "A non-negative revision is required.");

        IdentityId = identityId;
        ExpectedRevision = expectedRevision;
    }

    public Guid IdentityId { get; }

    public long ExpectedRevision { get; }

    public override string ToString() =>
        "ManagedBrowserTransitionRequest { IdentityId = [REDACTED], ExpectedRevision = " +
        $"{ExpectedRevision} }}";
}

public sealed record ManagedBrowserTransitionResult
{
    public ManagedBrowserTransitionResult(
        ManagedBrowserTransitionStatus status,
        long? currentRevision)
    {
        if (status is not (ManagedBrowserTransitionStatus.Applied or
                           ManagedBrowserTransitionStatus.Conflict or
                           ManagedBrowserTransitionStatus.NotFound))
            throw new ArgumentOutOfRangeException(nameof(status), status, "A transition status is required.");
        if (currentRevision is < 0)
            throw new ArgumentOutOfRangeException(nameof(currentRevision), currentRevision, "A non-negative revision is required.");
        if (status == ManagedBrowserTransitionStatus.Applied && !currentRevision.HasValue)
            throw new ArgumentException("An applied transition must report its current revision.", nameof(currentRevision));
        if (status == ManagedBrowserTransitionStatus.NotFound && currentRevision.HasValue)
            throw new ArgumentException("A missing identity cannot report a current revision.", nameof(currentRevision));

        Status = status;
        CurrentRevision = currentRevision;
    }

    public ManagedBrowserTransitionStatus Status { get; }

    public long? CurrentRevision { get; }

    public static ManagedBrowserTransitionResult Applied(long currentRevision) =>
        new(ManagedBrowserTransitionStatus.Applied, currentRevision);

    public static ManagedBrowserTransitionResult Conflict(long? currentRevision) =>
        new(ManagedBrowserTransitionStatus.Conflict, currentRevision);

    public static ManagedBrowserTransitionResult NotFound() =>
        new(ManagedBrowserTransitionStatus.NotFound, null);
}
