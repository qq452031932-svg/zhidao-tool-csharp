namespace BaiduBatchTransfer.Core.Abstractions;

public enum LocalResultOwnerKind
{
    TransferBatch,
    StandaloneShareBatch,
}

public enum LocalResultExportKind
{
    ReplacementWorkbook,
    StandaloneShareWorkbook,
    OfficialShareDetail,
}

public enum LocalResultExportStatus
{
    Pending,
    InProgress,
    Succeeded,
    Failed,
}

public readonly record struct LocalResultExportKey(
    LocalResultOwnerKind OwnerKind,
    Guid OwnerId,
    LocalResultExportKind ExportKind,
    int Generation)
{
    public void Validate()
    {
        if (!Enum.IsDefined(OwnerKind)) throw new ArgumentOutOfRangeException(nameof(OwnerKind));
        if (OwnerId == Guid.Empty) throw new ArgumentException("An export owner is required.", nameof(OwnerId));
        if (!Enum.IsDefined(ExportKind)) throw new ArgumentOutOfRangeException(nameof(ExportKind));
        if (Generation < 1) throw new ArgumentOutOfRangeException(nameof(Generation));
    }
}

public sealed record LocalResultExportRequest
{
    private LocalResultExportRequest(LocalResultExportKey key, string targetFileName, DateTimeOffset createdAt)
    {
        Key = key;
        TargetFileName = LocalResultExportValidation.SafeFileName(targetFileName, nameof(targetFileName));
        CreatedAt = createdAt;
    }

    public LocalResultExportKey Key { get; }
    public string TargetFileName { get; }
    public DateTimeOffset CreatedAt { get; }

    public static LocalResultExportRequest Create(
        LocalResultOwnerKind ownerKind,
        Guid ownerId,
        LocalResultExportKind exportKind,
        int generation,
        string targetFileName,
        DateTimeOffset createdAt)
    {
        var key = new LocalResultExportKey(ownerKind, ownerId, exportKind, generation);
        key.Validate();
        return new(key, targetFileName, createdAt);
    }

    public override string ToString() =>
        $"LocalResultExportRequest {{ Key = {Key}, TargetFileName = [REDACTED] }}";
}

public sealed record LocalResultExportRecord(
    Guid Id,
    LocalResultExportKey Key,
    LocalResultExportStatus Status,
    string TargetFileName,
    string? FinalFileName,
    string? SanitizedErrorCode,
    long LeaseGeneration,
    DateTimeOffset? LeaseExpiresAt,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public override string ToString() =>
        $"LocalResultExportRecord {{ Id = {Id}, Key = {Key}, Status = {Status}, TargetFileName = [REDACTED], FinalFileName = [REDACTED], Error = [REDACTED], LeaseGeneration = {LeaseGeneration}, Revision = {Revision} }}";
}

public sealed record LocalResultExportLease(
    Guid ExportId,
    LocalResultExportKey Key,
    long LeaseGeneration,
    Guid LeaseOwnerId,
    DateTimeOffset LeaseExpiresAt,
    long Revision)
{
    public override string ToString() =>
        $"LocalResultExportLease {{ ExportId = {ExportId}, Key = {Key}, LeaseGeneration = {LeaseGeneration}, LeaseOwnerId = [REDACTED], LeaseExpiresAt = [REDACTED], Revision = {Revision} }}";
}

public interface ILocalResultExportStore
{
    Task<LocalResultExportRecord> CreateOrGetAsync(
        LocalResultExportRequest request,
        CancellationToken cancellationToken = default);

    Task<LocalResultExportRecord?> GetAsync(
        LocalResultExportKey key,
        CancellationToken cancellationToken = default);

    Task<Page<LocalResultExportRecord>> GetPageAsync(
        PageQuery query,
        CancellationToken cancellationToken = default);

    Task<LocalResultExportLease?> TryAcquireAsync(
        LocalResultExportKey key,
        long expectedRevision,
        Guid leaseOwnerId,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default);

    Task<bool> TryCompleteAsync(
        Guid exportId,
        int expectedGeneration,
        long expectedLeaseGeneration,
        Guid expectedLeaseOwnerId,
        long expectedRevision,
        DateTimeOffset expectedLeaseExpiresAt,
        string finalFileName,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    Task<bool> TryFailAsync(
        Guid exportId,
        int expectedGeneration,
        long expectedLeaseGeneration,
        Guid expectedLeaseOwnerId,
        long expectedRevision,
        DateTimeOffset expectedLeaseExpiresAt,
        string sanitizedErrorCode,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    Task<int> RecoverExpiredAsync(
        DateTimeOffset now,
        int maximumCount,
        CancellationToken cancellationToken = default);
}

public static class LocalResultExportValidation
{
    public static string SafeFileName(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 200 || Path.IsPathFullyQualified(value) ||
            value is "." or ".." || value.Contains("..", StringComparison.Ordinal) ||
            value.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/', '\\', ':']) >= 0 ||
            !string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal))
        {
            throw new ArgumentException("A safe local artifact file name is required.", parameterName);
        }
        return value.Trim();
    }

    public static string SanitizedErrorCode(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64 ||
            value.Any(character => character is not (>= 'A' and <= 'Z') and not (>= '0' and <= '9') and not '_'))
        {
            throw new ArgumentException("A sanitized error code is required.", parameterName);
        }
        return value;
    }
}
