namespace BaiduBatchTransfer.Core.Domain;

public sealed class ImportSession
{
    public ImportSession(
        Guid id,
        string sourceType,
        string sourceName,
        DateTimeOffset createdAt,
        Guid? appendToBatchJobId = null,
        ImportSessionCounters counters = default)
    {
        Id = id;
        SourceType = RequireText(sourceType, nameof(sourceType));
        SourceName = RequireText(sourceName, nameof(sourceName));
        AppendToBatchJobId = appendToBatchJobId;
        Status = ImportSessionStatus.Parsing;
        TotalRows = counters.TotalRows;
        ReadyRows = counters.ReadyRows;
        InvalidRows = counters.InvalidRows;
        DuplicateRows = counters.DuplicateRows;
        ExcludedRows = counters.ExcludedRows;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public Guid Id { get; }

    public string SourceType { get; }

    public string SourceName { get; }

    public ImportSessionStatus Status { get; private set; }

    public int TotalRows { get; private set; }

    public int ReadyRows { get; private set; }

    public int InvalidRows { get; private set; }

    public int DuplicateRows { get; private set; }

    public int ExcludedRows { get; private set; }

    public Guid? PrecheckAccountId { get; private set; }

    public Guid? CreatedBatchJobId { get; private set; }

    public Guid? AppendToBatchJobId { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public void RecordRow(StagedImportDisposition disposition, DateTimeOffset updatedAt)
    {
        EnsureStatus(ImportSessionStatus.Parsing);
        EnsureTimestamp(updatedAt);
        var totalRows = checked(TotalRows + 1);
        var readyRows = ReadyRows;
        var invalidRows = InvalidRows;
        var duplicateRows = DuplicateRows;
        var excludedRows = ExcludedRows;
        switch (disposition)
        {
            case StagedImportDisposition.Ready:
                readyRows = checked(readyRows + 1);
                break;
            case StagedImportDisposition.Invalid:
            case StagedImportDisposition.Conflict:
                invalidRows = checked(invalidRows + 1);
                break;
            case StagedImportDisposition.Duplicate:
                duplicateRows = checked(duplicateRows + 1);
                break;
            case StagedImportDisposition.Excluded:
                excludedRows = checked(excludedRows + 1);
                break;
        }

        TotalRows = totalRows;
        ReadyRows = readyRows;
        InvalidRows = invalidRows;
        DuplicateRows = duplicateRows;
        ExcludedRows = excludedRows;
        UpdatedAt = updatedAt;
    }

    public void MarkDraft(DateTimeOffset updatedAt) => Transition(
        ImportSessionStatus.Parsing,
        ImportSessionStatus.Draft,
        updatedAt);

    public void BeginPrecheck(Guid accountId, DateTimeOffset updatedAt)
    {
        Transition(ImportSessionStatus.Draft, ImportSessionStatus.Prechecking, updatedAt);
        PrecheckAccountId = accountId;
    }

    public void MarkReadyToConfirm(DateTimeOffset updatedAt) => Transition(
        ImportSessionStatus.Prechecking,
        ImportSessionStatus.ReadyToConfirm,
        updatedAt);

    public void MarkBatchCreated(Guid batchJobId, DateTimeOffset updatedAt)
    {
        Transition(ImportSessionStatus.ReadyToConfirm, ImportSessionStatus.BatchCreated, updatedAt);
        CreatedBatchJobId = batchJobId;
    }

    public void Cancel(DateTimeOffset updatedAt)
    {
        if (Status is ImportSessionStatus.BatchCreated or ImportSessionStatus.Cancelled)
        {
            throw new InvalidOperationException($"Cannot cancel an import session in {Status} status.");
        }

        EnsureTimestamp(updatedAt);
        Status = ImportSessionStatus.Cancelled;
        UpdatedAt = updatedAt;
    }

    private void Transition(
        ImportSessionStatus expected,
        ImportSessionStatus next,
        DateTimeOffset updatedAt)
    {
        EnsureStatus(expected);
        EnsureTimestamp(updatedAt);
        Status = next;
        UpdatedAt = updatedAt;
    }

    private void EnsureStatus(ImportSessionStatus expected)
    {
        if (Status != expected)
        {
            throw new InvalidOperationException($"Expected import session status {expected}, but was {Status}.");
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
