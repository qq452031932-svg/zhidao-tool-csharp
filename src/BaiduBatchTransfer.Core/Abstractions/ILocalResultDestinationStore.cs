namespace BaiduBatchTransfer.Core.Abstractions;

public sealed record LocalResultDestinationRecord
{
    public LocalResultDestinationRecord(
        Guid batchJobId,
        string directoryReference,
        long revision,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        if (batchJobId == Guid.Empty) throw new ArgumentException("A batch is required.", nameof(batchJobId));
        if (string.IsNullOrWhiteSpace(directoryReference) || !Path.IsPathFullyQualified(directoryReference))
            throw new ArgumentException("A fully qualified local directory is required.", nameof(directoryReference));
        if (revision < 0) throw new ArgumentOutOfRangeException(nameof(revision));
        if (updatedAt < createdAt) throw new ArgumentOutOfRangeException(nameof(updatedAt));
        BatchJobId = batchJobId;
        DirectoryReference = Path.GetFullPath(directoryReference);
        Revision = revision;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    public Guid BatchJobId { get; }
    public string DirectoryReference { get; }
    public long Revision { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; }

    public static LocalResultDestinationRecord Create(
        Guid batchJobId,
        string directoryReference,
        DateTimeOffset createdAt) =>
        new(batchJobId, directoryReference, 0, createdAt, createdAt);

    public override string ToString() =>
        $"LocalResultDestinationRecord {{ BatchJobId = {BatchJobId}, DirectoryReference = [REDACTED], Revision = {Revision} }}";
}

public interface ILocalResultDestinationStore
{
    Task SetAsync(LocalResultDestinationRecord destination, CancellationToken cancellationToken = default);
    Task<LocalResultDestinationRecord?> GetAsync(Guid batchJobId, CancellationToken cancellationToken = default);
}
