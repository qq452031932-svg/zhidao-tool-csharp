using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Core.Abstractions;

public interface IWebAutomationAttemptStore
{
    Task<bool> BeginAsync(
        WebAutomationAttemptRecord attempt,
        CancellationToken cancellationToken = default);

    Task<WebAutomationAttemptRecord?> GetAsync(
        Guid operationId,
        Guid accountId,
        int generation,
        CancellationToken cancellationToken = default);

    Task<Page<WebAutomationAttemptRecord>> GetRecoveryPageAsync(
        PageQuery query,
        CancellationToken cancellationToken = default);

    Task<bool> TryPersistBaselineAsync(
        Guid operationId,
        Guid accountId,
        int generation,
        long expectedRevision,
        WebAutomationBaseline baseline,
        string observationFingerprint,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    Task<bool> TryAdvanceAsync(
        Guid operationId,
        Guid accountId,
        int generation,
        WebAutomationAttemptState expectedState,
        long expectedRevision,
        WebAutomationAttemptState nextState,
        string? observationFingerprint,
        bool isWaitingForUser,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    Task<bool> TryPersistOfficialAcceptanceAsync(
        Guid operationId,
        Guid accountId,
        int generation,
        long expectedRevision,
        RemoteItemIdentitySet acceptedRemoteItems,
        string observationFingerprint,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The attempt store does not support official acceptance evidence.");

    Task<bool> TryResumeFromUserHandoffAsync(
        Guid operationId,
        Guid accountId,
        int generation,
        long expectedRevision,
        WebAutomationAttemptState resumeStage,
        string? observationFingerprint,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    Task<bool> TryResolveUserHandoffAsync(
        Guid operationId,
        Guid accountId,
        int generation,
        long expectedRevision,
        WebAutomationAttemptState resumeStage,
        WebAutomationAttemptState terminalState,
        string? observationFingerprint,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);
}
