namespace BaiduBatchTransfer.Core.Domain;

public sealed class StagedImportRow
{
    public StagedImportRow(
        Guid id,
        Guid importSessionId,
        int sourceRowNumber,
        string shareUrl,
        DateTimeOffset updatedAt)
    {
        if (sourceRowNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceRowNumber));
        }

        Id = id;
        ImportSessionId = importSessionId;
        SourceRowNumber = sourceRowNumber;
        ShareUrl = RequireText(shareUrl, nameof(shareUrl));
        Disposition = StagedImportDisposition.NeedsResolution;
        UpdatedAt = updatedAt;
    }

    public Guid Id { get; }

    public Guid ImportSessionId { get; }

    public int SourceRowNumber { get; }

    public string ShareUrl { get; }

    public string? NormalizedShareId { get; private set; }

    public string? ExtractionCode { get; private set; }

    public string? TargetPath { get; private set; }

    public string? CreateFolderName { get; private set; }

    public string? RequestedAccountName { get; private set; }

    public Guid? MappedAccountId { get; private set; }

    public string? Note { get; private set; }

    public StagedImportDisposition Disposition { get; private set; }

    public Guid? DuplicateOfStagedRowId { get; private set; }

    public string? ValidationMessage { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public void MarkReady(
        string normalizedShareId,
        string? extractionCode,
        DateTimeOffset updatedAt)
    {
        EnsureMutable();
        EnsureTimestamp(updatedAt);
        NormalizedShareId = RequireText(normalizedShareId, nameof(normalizedShareId));
        ExtractionCode = extractionCode;
        Disposition = StagedImportDisposition.Ready;
        ValidationMessage = null;
        UpdatedAt = updatedAt;
    }

    public void MarkInvalid(string message, DateTimeOffset updatedAt) =>
        SetDisposition(StagedImportDisposition.Invalid, message, updatedAt);

    public void MarkConflict(string message, DateTimeOffset updatedAt) =>
        SetDisposition(StagedImportDisposition.Conflict, message, updatedAt);

    public void MarkDuplicate(Guid duplicateOfStagedRowId, DateTimeOffset updatedAt)
    {
        EnsureMutable();
        EnsureTimestamp(updatedAt);
        DuplicateOfStagedRowId = duplicateOfStagedRowId;
        Disposition = StagedImportDisposition.Duplicate;
        UpdatedAt = updatedAt;
    }

    public void Exclude(string? reason, DateTimeOffset updatedAt) =>
        SetDisposition(StagedImportDisposition.Excluded, reason, updatedAt);

    public void MapAccount(Guid accountId, DateTimeOffset updatedAt)
    {
        EnsureMutable();
        EnsureTimestamp(updatedAt);
        MappedAccountId = accountId;
        UpdatedAt = updatedAt;
    }

    public void SetOverrides(
        string? targetPath,
        string? createFolderName,
        string? requestedAccountName,
        string? note,
        DateTimeOffset updatedAt)
    {
        EnsureMutable();
        EnsureTimestamp(updatedAt);
        TargetPath = targetPath;
        CreateFolderName = createFolderName;
        RequestedAccountName = requestedAccountName;
        Note = note;
        UpdatedAt = updatedAt;
    }

    private void SetDisposition(
        StagedImportDisposition disposition,
        string? message,
        DateTimeOffset updatedAt)
    {
        EnsureMutable();
        EnsureTimestamp(updatedAt);
        Disposition = disposition;
        ValidationMessage = message;
        UpdatedAt = updatedAt;
    }

    private void EnsureMutable()
    {
        if (Disposition == StagedImportDisposition.Excluded)
        {
            throw new InvalidOperationException("An excluded staging row cannot be changed.");
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
