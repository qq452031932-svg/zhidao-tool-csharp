namespace BaiduBatchTransfer.Core.Domain;

public enum OperationStatus
{
    Waiting,
    Reserved,
    Preparing,
    Submitting,
    ResultPending,
    Succeeded,
    Failed,
    Paused,
    Cancelled,
}
