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

    public StagedRowPrecheckStatus? PrecheckStatus { get; private set; }

    public string? PrecheckErrorCode { get; private set; }

    public DateTimeOffset? PrecheckedAt { get; private set; }

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
        TransitionDisposition(
            StagedImportDisposition.Ready,
            validationMessage: null,
            duplicateOfStagedRowId: null,
            updatedAt);
    }

    public void MarkInvalid(string message, DateTimeOffset updatedAt) =>
        TransitionDisposition(StagedImportDisposition.Invalid, message, null, updatedAt);

    public void MarkConflict(string message, DateTimeOffset updatedAt) =>
        TransitionDisposition(StagedImportDisposition.Conflict, message, null, updatedAt);

    public void MarkNeedsResolution(string message, DateTimeOffset updatedAt) =>
        TransitionDisposition(StagedImportDisposition.NeedsResolution, message, null, updatedAt);

    public void MarkDuplicate(Guid duplicateOfStagedRowId, DateTimeOffset updatedAt)
    {
        TransitionDisposition(
            StagedImportDisposition.Duplicate,
            validationMessage: null,
            duplicateOfStagedRowId,
            updatedAt);
    }

    public void Exclude(string? reason, DateTimeOffset updatedAt) =>
        TransitionDisposition(StagedImportDisposition.Excluded, reason, null, updatedAt);

    public void RecordPrecheck(
        StagedRowPrecheckStatus status,
        string? errorCode,
        DateTimeOffset precheckedAt)
    {
        if (Disposition != StagedImportDisposition.Ready)
        {
            throw new InvalidOperationException("Only a ready staging row can be prechecked.");
        }

        if (status == StagedRowPrecheckStatus.Failed && string.IsNullOrWhiteSpace(errorCode))
        {
            throw new ArgumentException("A failed precheck requires an error code.", nameof(errorCode));
        }

        EnsureTimestamp(precheckedAt);
        PrecheckStatus = status;
        PrecheckErrorCode = status == StagedRowPrecheckStatus.Failed ? errorCode : null;
        PrecheckedAt = precheckedAt;
        UpdatedAt = precheckedAt;
    }

    public void MapAccount(Guid accountId, DateTimeOffset updatedAt)
    {
        EnsureMutable();
        EnsureTimestamp(updatedAt);
        MappedAccountId = accountId;
        ClearPrecheck();
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
        ClearPrecheck();
        UpdatedAt = updatedAt;
    }

    private void TransitionDisposition(
        StagedImportDisposition disposition,
        string? validationMessage,
        Guid? duplicateOfStagedRowId,
        DateTimeOffset updatedAt)
    {
        EnsureMutable();
        EnsureTimestamp(updatedAt);
        Disposition = disposition;
        DuplicateOfStagedRowId = disposition == StagedImportDisposition.Duplicate
            ? duplicateOfStagedRowId
            : null;
        ValidationMessage = disposition is
            StagedImportDisposition.Invalid or
            StagedImportDisposition.Conflict or
            StagedImportDisposition.NeedsResolution or
            StagedImportDisposition.Excluded
                ? validationMessage
                : null;
        ClearPrecheck();
        UpdatedAt = updatedAt;
    }

    private void ClearPrecheck()
    {
        PrecheckStatus = null;
        PrecheckErrorCode = null;
        PrecheckedAt = null;
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
