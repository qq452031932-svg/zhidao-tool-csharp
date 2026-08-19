namespace BaiduBatchTransfer.Core.Domain;

public enum ImportSessionStatus
{
    Parsing,
    Draft,
    Prechecking,
    ReadyToConfirm,
    BatchCreated,
    Cancelled,
}

public enum StagedImportDisposition
{
    NeedsResolution,
    Ready,
    Invalid,
    Duplicate,
    Conflict,
    Excluded,
}

public enum StagedRowPrecheckStatus
{
    Succeeded,
    Failed,
}

public enum AccountStatus
{
    Active,
    Invalid,
    Disabled,
    Deleted,
}

public enum CandidateStatus
{
    Unallocated,
    Reserved,
    Expanded,
    GloballyInvalid,
    Unused,
}

public readonly record struct ImportSessionCounters
{
    public ImportSessionCounters(
        int totalRows,
        int readyRows,
        int invalidRows,
        int duplicateRows,
        int excludedRows)
    {
        if (totalRows < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalRows));
        }

        if (readyRows < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(readyRows));
        }

        if (invalidRows < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(invalidRows));
        }

        if (duplicateRows < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(duplicateRows));
        }

        if (excludedRows < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(excludedRows));
        }

        var classifiedRows =
            (long)readyRows + invalidRows + duplicateRows + excludedRows;
        if (classifiedRows > totalRows)
        {
            throw new ArgumentOutOfRangeException(
                nameof(totalRows),
                "Classified row counters cannot exceed total rows.");
        }

        TotalRows = totalRows;
        ReadyRows = readyRows;
        InvalidRows = invalidRows;
        DuplicateRows = duplicateRows;
        ExcludedRows = excludedRows;
    }

    public int TotalRows { get; }

    public int ReadyRows { get; }

    public int InvalidRows { get; }

    public int DuplicateRows { get; }

    public int ExcludedRows { get; }
}
