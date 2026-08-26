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

    Task<bool> TryPersistTransferSourceEvidenceAsync(
        Guid operationId,
        Guid accountId,
        int generation,
        long expectedRevision,
        TransferSourceEvidence evidence,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The attempt store does not support transfer source evidence.");

    Task<bool> TryPersistTransferTargetPreparationAsync(
        Guid operationId,
        Guid accountId,
        int generation,
        long expectedRevision,
        TransferTargetPreparationEvidence evidence,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The attempt store does not support transfer target evidence.");

    Task<bool> TryPersistTransferSubmissionAsync(
        Guid operationId,
        Guid accountId,
        int generation,
        long expectedRevision,
        TransferSubmissionEvidence evidence,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The attempt store does not support transfer submission evidence.");

    Task<bool> TryPersistTransferAcceptanceAsync(
        Guid operationId,
        Guid accountId,
        int generation,
        long expectedRevision,
        string officialRequestId,
        TransferIdentityMapping mapping,
        string observationFingerprint,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The attempt store does not support exact transfer acceptance.");

    Task<bool> TryBeginDirectoryMutationAsync(
        TransferDirectoryMutationRecord mutation,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The attempt store does not support directory mutation evidence.");

    Task<bool> TryResolveDirectoryMutationAsync(
        TransferDirectoryMutationRecord expected,
        TransferDirectoryMutationState nextState,
        string? childExactId,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The attempt store does not support directory mutation evidence.");

    Task<IReadOnlyList<TransferDirectoryMutationRecord>> GetDirectoryMutationsAsync(
        Guid operationId,
        Guid accountId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TransferDirectoryMutationRecord>>([]);

    Task<TransferDirectoryMutationRecord?> GetUnresolvedDirectoryMutationAsync(
        Guid operationId,
        Guid accountId,
        string parentExactId,
        string canonicalSegment,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<TransferDirectoryMutationRecord?>(null);

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
