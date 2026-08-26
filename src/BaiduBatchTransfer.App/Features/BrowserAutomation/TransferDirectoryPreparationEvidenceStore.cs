using BaiduBatchTransfer.BaiduAdapter.WebAutomation;
using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.App.Features.BrowserAutomation;

internal sealed class TransferDirectoryPreparationEvidenceStore(
    IWebAutomationAttemptStore inner,
    ITransferRecoveryAuthorizationSource? recoveryAuthorization = null) : ITransferDirectoryPreparationEvidenceStore
{
    private readonly IWebAutomationAttemptStore inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly ITransferRecoveryAuthorizationSource? recoveryAuthorization = recoveryAuthorization;

    public Task<WebAutomationAttemptRecord?> GetAttemptAsync(
        TransferDirectoryPreparationRequest request,
        CancellationToken cancellationToken) =>
        inner.GetAsync(request.OperationId, request.AccountId, request.Generation, cancellationToken);

    public async Task<IReadOnlyList<TransferDirectoryMutationRecord>> GetUnresolvedMutationsAsync(
        Guid operationId,
        Guid accountId,
        CancellationToken cancellationToken) =>
        (await inner.GetDirectoryMutationsAsync(operationId, accountId, cancellationToken)
            .ConfigureAwait(false))
        .Where(item => item.State is TransferDirectoryMutationState.Intent or
            TransferDirectoryMutationState.OutcomeUnknown)
        .ToArray();

    public Task<TransferDirectoryRecoveryAuthorization?> AuthorizeRecoveryAsync(
        TransferDirectoryRecoveryAuthorizationRequest request,
        CancellationToken cancellationToken) =>
        recoveryAuthorization is null
            ? Task.FromResult<TransferDirectoryRecoveryAuthorization?>(null)
            : recoveryAuthorization.TryAuthorizeDirectoryMutationAsync(request, cancellationToken);

    public Task<bool> PersistSourceEvidenceAsync(
        TransferDirectoryPreparationRequest request,
        long expectedRevision,
        TransferSourceEvidence evidence,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken) =>
        inner.TryPersistTransferSourceEvidenceAsync(
            request.OperationId, request.AccountId, request.Generation, expectedRevision,
            evidence, updatedAt, cancellationToken);

    public Task<bool> BeginMutationAsync(
        TransferDirectoryMutationRecord mutation,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken) =>
        inner.TryBeginDirectoryMutationAsync(mutation, updatedAt, cancellationToken);

    public Task<bool> ResolveMutationAsync(
        TransferDirectoryMutationRecord expected,
        TransferDirectoryMutationState nextState,
        string? childExactId,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken) =>
        inner.TryResolveDirectoryMutationAsync(
            expected, nextState, childExactId, updatedAt, cancellationToken);
}
