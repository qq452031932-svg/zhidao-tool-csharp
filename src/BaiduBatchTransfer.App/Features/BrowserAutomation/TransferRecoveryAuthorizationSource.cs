using BaiduBatchTransfer.BaiduAdapter.WebAutomation;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.App.Features.BrowserAutomation;

public interface ITransferRecoveryAuthorizationSource
{
    Task<TransferDirectoryRecoveryAuthorization?> TryAuthorizeDirectoryMutationAsync(
        TransferDirectoryRecoveryAuthorizationRequest request,
        CancellationToken cancellationToken = default);

    Task<ReadOnlyTransferRecoveryBinding?> TryAuthorizeSubmissionRecoveryAsync(
        TransferSubmissionRecoveryAuthorizationRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<ReadOnlyTransferRecoveryBinding?>(null);
}

public sealed record TransferSubmissionRecoveryAuthorizationRequest(
    Guid OperationId,
    Guid BatchId,
    Guid AccountId,
    int Generation,
    RemoteDirectory Target,
    string BaiduUserId,
    AdapterCapabilityVersion Capability);

public sealed class TransferRecoveryAuthorizationSource(
    IWebAutomationAttemptStore attempts,
    IWebAutomationRequestOwnershipStore ownership,
    TimeProvider? timeProvider = null) : ITransferRecoveryAuthorizationSource
{
    private static readonly TimeSpan AuthorizationLifetime = TimeSpan.FromMinutes(2);
    private readonly IWebAutomationAttemptStore attempts = attempts ?? throw new ArgumentNullException(nameof(attempts));
    private readonly IWebAutomationRequestOwnershipStore ownership = ownership ?? throw new ArgumentNullException(nameof(ownership));
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<TransferDirectoryRecoveryAuthorization?> TryAuthorizeDirectoryMutationAsync(
        TransferDirectoryRecoveryAuthorizationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var mutation = request.Mutation ?? throw new ArgumentNullException(nameof(request.Mutation));
        if (request.BatchId == Guid.Empty || request.CurrentGeneration < 1 ||
            request.CurrentGeneration < mutation.Generation ||
            mutation.State is not (TransferDirectoryMutationState.Intent or
                TransferDirectoryMutationState.OutcomeUnknown))
            return null;
        var segment = LastSegment(mutation.CanonicalChildPath);
        var unresolved = await attempts.GetUnresolvedDirectoryMutationAsync(
            mutation.OperationId, mutation.AccountId, mutation.ParentExactId, segment,
            cancellationToken).ConfigureAwait(false);
        if (unresolved != mutation) return null;

        var origin = await attempts.GetAsync(
            mutation.OperationId, mutation.AccountId, mutation.Generation, cancellationToken)
            .ConfigureAwait(false);
        if (origin is null || origin.OwnerKind != WebAutomationOwnerKind.Transfer ||
            origin.State != WebAutomationAttemptState.Preparing &&
            !(origin.State == WebAutomationAttemptState.UserHandoff &&
              origin.ResumeStage == WebAutomationAttemptState.Preparing) ||
            origin.Revision < mutation.OwningAttemptRevision || origin.SourceEvidence is null ||
            origin.SourceEvidence.BaiduUserId != mutation.BaiduUserId ||
            origin.SourceEvidence.Capability != mutation.Capability)
            return null;
        if (!await ownership.IsBoundAsync(
                mutation.OperationId, request.BatchId, mutation.AccountId, request.CurrentGeneration,
                WebAutomationOwnerKind.Transfer, cancellationToken).ConfigureAwait(false))
            return null;

        return new TransferDirectoryRecoveryAuthorization(
            mutation.OperationId, request.BatchId, mutation.AccountId, mutation.Generation,
            mutation.OwningAttemptRevision, mutation.ParentExactId,
            Parent(mutation.CanonicalChildPath), mutation.CanonicalChildPath,
            mutation.BaiduUserId, mutation.Capability,
            timeProvider.GetUtcNow().Add(AuthorizationLifetime));
    }

    public async Task<ReadOnlyTransferRecoveryBinding?> TryAuthorizeSubmissionRecoveryAsync(
        TransferSubmissionRecoveryAuthorizationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.OperationId == Guid.Empty || request.BatchId == Guid.Empty ||
            request.AccountId == Guid.Empty || request.Generation < 1 || request.Target is null ||
            string.IsNullOrWhiteSpace(request.BaiduUserId) || request.Capability is null)
            return null;
        var attempt = await attempts.GetAsync(
            request.OperationId, request.AccountId, request.Generation, cancellationToken)
            .ConfigureAwait(false);
        var effectiveState = attempt?.State == WebAutomationAttemptState.UserHandoff
            ? attempt.ResumeStage
            : attempt?.State;
        if (attempt is null || attempt.OwnerKind != WebAutomationOwnerKind.Transfer ||
            effectiveState != WebAutomationAttemptState.ResultPending ||
            attempt.SourceEvidence is null || attempt.TargetPreparationEvidence is null ||
            attempt.SubmissionEvidence is null ||
            attempt.TargetPreparationEvidence.TargetPath != request.Target.Path ||
            !string.Equals(attempt.TargetPreparationEvidence.TargetExactId,
                request.Target.RemoteId, StringComparison.Ordinal) ||
            !string.Equals(attempt.SourceEvidence.BaiduUserId,
                request.BaiduUserId, StringComparison.Ordinal) ||
            !string.Equals(attempt.TargetPreparationEvidence.BaiduUserId,
                request.BaiduUserId, StringComparison.Ordinal) ||
            attempt.SourceEvidence.Capability != request.Capability ||
            attempt.SubmissionEvidence.Capability != request.Capability ||
            !string.Equals(attempt.SubmissionEvidence.SourceFingerprint,
                attempt.SourceEvidence.Items.Fingerprint, StringComparison.Ordinal) ||
            !string.Equals(attempt.SubmissionEvidence.TargetBaselineFingerprint,
                attempt.TargetPreparationEvidence.Baseline.Fingerprint, StringComparison.Ordinal) ||
            !string.Equals(attempt.SubmissionEvidence.TargetExactId,
                request.Target.RemoteId, StringComparison.Ordinal))
            return null;
        if (!await ownership.IsBoundAsync(
                request.OperationId, request.BatchId, request.AccountId, request.Generation,
                WebAutomationOwnerKind.Transfer, cancellationToken).ConfigureAwait(false))
            return null;
        return new ReadOnlyTransferRecoveryBinding(
            request.OperationId,
            request.BatchId,
            request.AccountId,
            request.Generation,
            request.Target,
            request.BaiduUserId,
            attempt.SourceEvidence.Items.Fingerprint,
            attempt.TargetPreparationEvidence.Baseline.Fingerprint,
            attempt.SubmissionEvidence.MarkedSubmittingAtUtc,
            attempt.TransferIdentityMapping?.Fingerprint,
            request.Capability,
            timeProvider.GetUtcNow().Add(AuthorizationLifetime));
    }

    private static string LastSegment(RemotePath path) =>
        path.Value.Split('/', StringSplitOptions.RemoveEmptyEntries)[^1];

    private static RemotePath Parent(RemotePath child)
    {
        var segments = child.Value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length <= 1 ? new RemotePath("/") :
            new RemotePath('/' + string.Join('/', segments.Take(segments.Length - 1)));
    }
}
