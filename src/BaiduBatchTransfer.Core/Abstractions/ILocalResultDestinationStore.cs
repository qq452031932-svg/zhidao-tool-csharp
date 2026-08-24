namespace BaiduBatchTransfer.Core.Abstractions;

public static class LocalResultDirectoryReference
{
    public static string Normalize(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
            throw new ArgumentException("A safe fully qualified local directory is required.", parameterName);

        var candidate = value.Trim();
        if (candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A safe fully qualified local directory is required.", parameterName);
        if (!Path.IsPathFullyQualified(candidate))
            throw new ArgumentException("A safe fully qualified local directory is required.", parameterName);

        string normalized;
        try
        {
            normalized = Path.GetFullPath(candidate);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("A safe fully qualified local directory is required.", parameterName);
        }

        var root = Path.GetPathRoot(normalized);
        if (string.IsNullOrEmpty(root) ||
            string.Equals(
                normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A non-root local directory is required.", parameterName);
        return normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}

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
        if (revision < 0) throw new ArgumentOutOfRangeException(nameof(revision));
        if (updatedAt < createdAt) throw new ArgumentOutOfRangeException(nameof(updatedAt));
        BatchJobId = batchJobId;
        DirectoryReference = LocalResultDirectoryReference.Normalize(directoryReference, nameof(directoryReference));
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
