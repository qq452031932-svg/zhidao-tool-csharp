namespace BaiduBatchTransfer.Core.Domain;

public enum ShareOperationStatus
{
    WaitingTransfer,
    WaitingShare,
    Reserved,
    Preparing,
    Submitting,
    ResultPending,
    Succeeded,
    Failed,
    Paused,
    ReusedExisting,
    Cancelled,
}
