namespace BaiduBatchTransfer.Core.Domain;

public enum BatchStatus
{
    Queued,
    Active,
    Paused,
    AwaitingCandidates,
    PausedReady,
    Completed,
    CompletedWithShortfall,
    Cancelled,
}
