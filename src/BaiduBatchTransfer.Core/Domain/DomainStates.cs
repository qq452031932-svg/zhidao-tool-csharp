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
