namespace BaiduBatchTransfer.Core.Abstractions;

public enum WebAutomationBatchCancellationDisposition
{
    Cancelled,
    AlreadyCancelled,
    Terminal,
    OwnershipMismatch,
    StaleHandoff,
}

public interface IWebAutomationBatchCancellationStore
{
    Task<WebAutomationBatchCancellationDisposition> CancelAsync(
        Guid operationId,
        Guid batchId,
        Guid accountId,
        int generation,
        WebAutomationOwnerKind ownerKind,
        long expectedAttemptRevision,
        WebAutomationAttemptState expectedResumeStage,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    Task<bool> IsCancelledAsync(
        Guid batchId,
        WebAutomationOwnerKind ownerKind,
        CancellationToken cancellationToken = default);
}
