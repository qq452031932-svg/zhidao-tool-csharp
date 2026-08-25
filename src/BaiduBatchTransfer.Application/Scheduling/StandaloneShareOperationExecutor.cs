using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Scheduling;

public enum StandaloneShareExecutionDisposition
{
    Succeeded,
    DefinitelyNotAccepted,
    ResultPending,
    Paused,
    Stale,
}

public sealed record StandaloneShareExecutionResult(
    StandaloneShareExecutionDisposition Disposition,
    string SafeMessage);

public interface IStandaloneShareOperationRunner
{
    Task<StandaloneShareExecutionResult> ExecuteAsync(
        StandaloneShareOperationLease lease,
        CancellationToken cancellationToken = default);

    Task<StandaloneShareExecutionResult> ReconcileAsync(
        StandaloneShareOperationLease lease,
        CancellationToken cancellationToken = default);
}

public sealed class StandaloneShareOperationExecutor(
    IBaiduShareAdapter adapter,
    IBaiduSessionStore sessions,
    IStandaloneShareExecutionStore store,
    ShareSubmissionEngine submissionEngine,
    TimeProvider? timeProvider = null,
    RetryPolicy? retryPolicy = null,
    IAsyncDelay? delay = null) : IStandaloneShareOperationRunner
{
    private readonly IBaiduShareAdapter adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
    private readonly IBaiduSessionStore sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    private readonly IStandaloneShareExecutionStore store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly ShareSubmissionEngine submissionEngine = submissionEngine ?? throw new ArgumentNullException(nameof(submissionEngine));
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;
    private readonly RetryPolicy retryPolicy = retryPolicy ?? RetryPolicy.Default;
    private readonly IAsyncDelay delay = delay ?? new TimeProviderDelay(timeProvider ?? TimeProvider.System);

    public async Task<StandaloneShareExecutionResult> ExecuteAsync(
        StandaloneShareOperationLease lease,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var operation = await LoadCurrentAsync(lease, cancellationToken).ConfigureAwait(false);
        if (operation is null) return Stale();
        var session = await sessions.LoadAsync(operation.AccountId.ToString("D"), cancellationToken).ConfigureAwait(false);
        if (session?.LocalAccountId != operation.AccountId)
            return await FailBeforeSubmissionAsync(
                operation, lease, "ACCOUNT_SESSION_UNAVAILABLE", cancellationToken).ConfigureAwait(false);
        var request = Request(operation);

        var discovery = await submissionEngine.DiscoverAsync(
            adapter, session, request, retryPolicy, delay, cancellationToken).ConfigureAwait(false);
        if (!discovery.IsSuccess)
            return await FailBeforeSubmissionAsync(operation, lease, discovery.ErrorKind!.Value.ToString().ToUpperInvariant(), cancellationToken)
                .ConfigureAwait(false);
        var baseline = discovery.Value!.FullListing;
        if (!await store.TryCompleteBaselineAsync(
                operation.Id, operation.BatchId, operation.AccountId, operation.Generation,
                lease.LeaseGeneration, lease.LeaseOwnerId, operation.Revision,
                baseline.Items.Select(item => item.ExactId).ToArray(), baseline.Fingerprint,
                timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false))
            return Stale();
        operation = await LoadCurrentAsync(lease, cancellationToken).ConfigureAwait(false);
        if (operation is null) return Stale();

        var existing = submissionEngine.ResolveExistingShare(
            operation.ExistingShareDecision, request, discovery.Value.ExactMatches);
        if (existing.Action == ExistingShareAction.Reuse)
            return await ReuseExistingAsync(operation, lease, existing.ReusableShare!, cancellationToken)
                .ConfigureAwait(false);
        if (existing.Action is ExistingShareAction.Cancel or ExistingShareAction.RequiresDecision or ExistingShareAction.Incompatible)
            return await FailBeforeSubmissionAsync(
                operation, lease, $"EXISTING_SHARE_{existing.Action.ToString().ToUpperInvariant()}", cancellationToken)
                .ConfigureAwait(false);

        var context = new ShareOperationAutomationContext(
            operation.Id, operation.BatchId, operation.AccountId, operation.Generation,
            WebAutomationOwnerKind.StandaloneShare, operation.ExactRemoteItems).Validate();
        var submitted = await submissionEngine.ExecuteSubmissionAsync(
            request,
            new ShareSubmissionPorts(
                async token => adapter is IOperationBoundBaiduShareAdapter bound
                    ? await bound.PreparePermanentShareForOperationAsync(context, session, request, token).ConfigureAwait(false)
                    : BaiduAdapterResult<SharePreparationReceipt>.Success(
                        SharePreparationReceipt.Ready, SubmissionAcceptance.DefinitelyNotAccepted),
                async token =>
                {
                    if (!await store.TryMarkSubmittingAsync(
                            operation.Id, operation.BatchId, operation.AccountId, operation.Generation,
                            lease.LeaseGeneration, lease.LeaseOwnerId, operation.Revision,
                            timeProvider.GetUtcNow(), token).ConfigureAwait(false))
                        return false;
                    operation = await LoadCurrentAsync(lease, token).ConfigureAwait(false);
                    return operation is not null;
                },
                async (completedRetries, token) =>
                {
                    if (!await store.TryBeginSubmissionRetryAsync(
                            operation.Id, operation.BatchId, operation.AccountId, operation.Generation,
                            lease.LeaseGeneration, lease.LeaseOwnerId, operation.Revision,
                            completedRetries, timeProvider.GetUtcNow(), token).ConfigureAwait(false))
                        return false;
                    operation = await LoadCurrentAsync(lease, token).ConfigureAwait(false);
                    return operation is not null;
                },
                token => adapter is IOperationBoundBaiduShareAdapter bound
                    ? bound.SubmitPermanentShareForOperationAsync(context, session, request, token)
                    : adapter.CreatePermanentShareAsync(session, request, token),
                async (completedAttempts, token) =>
                {
                    if (!await store.TryRecordDefinitelyNotAcceptedAttemptAsync(
                            operation.Id, operation.BatchId, operation.AccountId, operation.Generation,
                            lease.LeaseGeneration, lease.LeaseOwnerId, operation.Revision,
                            completedAttempts, timeProvider.GetUtcNow(), token).ConfigureAwait(false))
                        return false;
                    operation = await LoadCurrentAsync(lease, token).ConfigureAwait(false);
                    return operation is not null;
                },
                token => store.TryMarkResultPendingAsync(
                    operation.Id, operation.BatchId, operation.AccountId, operation.Generation,
                    lease.LeaseGeneration, lease.LeaseOwnerId, operation.Revision,
                    timeProvider.GetUtcNow(), token)),
            retryPolicy,
            delay,
            cancellationToken).ConfigureAwait(false);
        if (submitted.Disposition == ShareSubmissionDisposition.Stale) return Stale();
        if (submitted.Disposition == ShareSubmissionDisposition.PreparationFailed)
            return await FailBeforeSubmissionAsync(
                operation, lease,
                submitted.ErrorKind?.ToString().ToUpperInvariant() ?? "PREPARATION_FAILED",
                cancellationToken).ConfigureAwait(false);
        if (submitted.Disposition == ShareSubmissionDisposition.ResultPending)
            return Pending("提交结果需要精确核对。");
        if (submitted.Disposition == ShareSubmissionDisposition.DefinitelyNotAccepted)
        {
            var failed = await store.TryFailAsync(
                operation.Id, operation.BatchId, operation.AccountId, operation.Generation,
                lease.LeaseGeneration, lease.LeaseOwnerId, operation.Revision,
                SubmissionAcceptance.DefinitelyNotAccepted,
                submitted.ErrorKind?.ToString().ToUpperInvariant() ?? "REMOTE_NOT_ACCEPTED",
                timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            return failed
                ? new(StandaloneShareExecutionDisposition.DefinitelyNotAccepted, "分享未被接受，可安全重试。")
                : Stale();
        }

        var receipt = submitted.Receipt!;
        var now = timeProvider.GetUtcNow();
        var result = new StandaloneShareResultRecord(
            Guid.NewGuid(), operation.Id, operation.BatchId, operation.AccountId, operation.Generation,
            operation.ExactRemoteItems.Fingerprint, operation.SettingsFingerprint,
            receipt.ShareId, receipt.ShareUrl, receipt.ExtractionCode!, true, true,
            receipt.SubmittedAtUtc, now);
        var accepted = await store.TryAcceptResultAsync(
            result, lease.LeaseGeneration, lease.LeaseOwnerId, operation.Revision, now, cancellationToken)
            .ConfigureAwait(false);
        return accepted
            ? new(StandaloneShareExecutionDisposition.Succeeded, "分享成功。")
            : Stale();
    }

    public async Task<StandaloneShareExecutionResult> ReconcileAsync(
        StandaloneShareOperationLease lease,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var operation = await LoadCurrentAsync(lease, cancellationToken).ConfigureAwait(false);
        if (operation is null || operation.Status != StandaloneShareOperationStatus.ResultPending ||
            operation.SubmissionState != StandaloneShareSubmissionState.ResultPending ||
            operation.BaselineFingerprint is null || operation.BaselineShareCount is null)
            return Stale();
        var session = await sessions.LoadAsync(operation.AccountId.ToString("D"), cancellationToken).ConfigureAwait(false);
        if (session?.LocalAccountId != operation.AccountId)
            return await ReleasePendingAsync(operation, lease, cancellationToken).ConfigureAwait(false);
        var request = Request(operation);
        var context = new ShareOperationAutomationContext(
            operation.Id, operation.BatchId, operation.AccountId, operation.Generation,
            WebAutomationOwnerKind.StandaloneShare, operation.ExactRemoteItems).Validate();
        BaiduAdapterResult<ShareCreationReceipt> confirmation;
        try
        {
            if (adapter is IOperationBoundBaiduShareAdapter bound)
            {
                confirmation = await bound.ConfirmExactShareForOperationAsync(
                    context, session, request, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var baseline = await LoadBaselineAsync(operation, cancellationToken).ConfigureAwait(false);
                if (baseline is null)
                    return await ReleasePendingAsync(operation, lease, cancellationToken).ConfigureAwait(false);
                var listing = await submissionEngine.DiscoverAsync(adapter, session, request, cancellationToken)
                    .ConfigureAwait(false);
                if (!listing.IsSuccess)
                    return await ReleasePendingAsync(operation, lease, cancellationToken).ConfigureAwait(false);
                var candidateReceipt = submissionEngine.ConfirmUniqueExact(
                    request, baseline, listing.Value!.AllItems);
                confirmation = candidateReceipt is null
                    ? BaiduAdapterResult<ShareCreationReceipt>.Failure(
                        BaiduErrorKind.UnknownResponse, adapterDiagnostic: null,
                        submissionAcceptance: SubmissionAcceptance.Unknown)
                    : BaiduAdapterResult<ShareCreationReceipt>.Success(candidateReceipt);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return await ReleasePendingAsync(operation, lease, CancellationToken.None).ConfigureAwait(false);
        }
        var evaluated = submissionEngine.EvaluateConfirmation(request, confirmation);
        if (evaluated.Disposition == ShareSubmissionDisposition.DefinitelyNotAccepted)
        {
            var failed = await store.TryFailAsync(
                operation.Id, operation.BatchId, operation.AccountId, operation.Generation,
                lease.LeaseGeneration, lease.LeaseOwnerId, operation.Revision,
                SubmissionAcceptance.DefinitelyNotAccepted,
                confirmation.ErrorKind!.Value.ToString().ToUpperInvariant(),
                timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            return failed
                ? new(StandaloneShareExecutionDisposition.DefinitelyNotAccepted, "分享未被接受，可安全重试。")
                : Stale();
        }
        if (evaluated.Disposition != ShareSubmissionDisposition.Accepted)
            return await ReleasePendingAsync(operation, lease, cancellationToken).ConfigureAwait(false);

        var receipt = evaluated.Receipt!;
        var now = timeProvider.GetUtcNow();
        var result = new StandaloneShareResultRecord(
            Guid.NewGuid(), operation.Id, operation.BatchId, operation.AccountId, operation.Generation,
            operation.ExactRemoteItems.Fingerprint, operation.SettingsFingerprint,
            receipt.ShareId, receipt.ShareUrl, receipt.ExtractionCode!, true, true,
            receipt.SubmittedAtUtc, now);
        var accepted = await store.TryAcceptResultAsync(
            result, lease.LeaseGeneration, lease.LeaseOwnerId, operation.Revision, now, cancellationToken)
            .ConfigureAwait(false);
        return accepted
            ? new(StandaloneShareExecutionDisposition.Succeeded, "分享成功。")
            : Stale();
    }

    private async Task<StandaloneShareExecutionResult> MarkResultPendingAsync(
        StandaloneShareOperationRecord operation,
        StandaloneShareOperationLease lease,
        CancellationToken cancellationToken)
    {
        var changed = await store.TryMarkResultPendingAsync(
            operation.Id, operation.BatchId, operation.AccountId, operation.Generation,
            lease.LeaseGeneration, lease.LeaseOwnerId, operation.Revision,
            timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        return changed ? Pending("提交结果需要精确核对。") : Stale();
    }

    private async Task<StandaloneShareExecutionResult> ReleasePendingAsync(
        StandaloneShareOperationRecord operation,
        StandaloneShareOperationLease lease,
        CancellationToken cancellationToken)
    {
        var released = await store.TryReleaseReconciliationAsync(
            operation.Id, operation.BatchId, operation.AccountId, operation.Generation,
            lease.LeaseGeneration, lease.LeaseOwnerId, operation.Revision,
            timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        return released ? Pending("尚未找到唯一分享结果，将继续核对。") : Stale();
    }

    private async Task<WebAutomationBaseline?> LoadBaselineAsync(
        StandaloneShareOperationRecord operation,
        CancellationToken cancellationToken)
    {
        var ids = new List<string>();
        string? cursor = null;
        do
        {
            var page = await store.GetBaselinePageAsync(
                operation.Id, operation.Generation, cursor, 1000, cancellationToken).ConfigureAwait(false);
            ids.AddRange(page.Items);
            cursor = page.NextCursor;
        } while (cursor is not null);
        if (ids.Count != operation.BaselineShareCount) return null;
        var baseline = WebAutomationBaseline.Create(ids);
        return string.Equals(baseline.Fingerprint, operation.BaselineFingerprint, StringComparison.Ordinal)
            ? baseline
            : null;
    }

    private async Task<StandaloneShareExecutionResult> FailBeforeSubmissionAsync(
        StandaloneShareOperationRecord operation,
        StandaloneShareOperationLease lease,
        string errorCode,
        CancellationToken cancellationToken)
    {
        var changed = await store.TryFailAsync(
            operation.Id, operation.BatchId, operation.AccountId, operation.Generation,
            lease.LeaseGeneration, lease.LeaseOwnerId, operation.Revision,
            SubmissionAcceptance.DefinitelyNotAccepted, errorCode,
            timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        return changed
            ? new(StandaloneShareExecutionDisposition.DefinitelyNotAccepted, "分享未被接受，可安全重试。")
            : Stale();
    }

    private async Task<StandaloneShareExecutionResult> ReuseExistingAsync(
        StandaloneShareOperationRecord operation,
        StandaloneShareOperationLease lease,
        BaiduShareRecord existing,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var result = new StandaloneShareResultRecord(
            Guid.NewGuid(), operation.Id, operation.BatchId, operation.AccountId, operation.Generation,
            operation.ExactRemoteItems.Fingerprint, operation.SettingsFingerprint,
            existing.ShareId, existing.ShareUrl, existing.ExtractionCode, true, true, now, now);
        var accepted = await store.TryReuseExistingResultAsync(
            result, lease.LeaseGeneration, lease.LeaseOwnerId, operation.Revision, now, cancellationToken)
            .ConfigureAwait(false);
        return accepted
            ? new(StandaloneShareExecutionDisposition.Succeeded, "已复用完全匹配的已有分享。")
            : Stale();
    }

    private async Task<StandaloneShareOperationRecord?> LoadCurrentAsync(
        StandaloneShareOperationLease lease,
        CancellationToken cancellationToken)
    {
        var operation = (await store.GetOperationsAsync(lease.BatchId, cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(item => item.Id == lease.OperationId);
        return operation is not null && operation.AccountId == lease.AccountId &&
               operation.Generation == lease.Generation && operation.LeaseGeneration == lease.LeaseGeneration &&
               operation.LeaseOwnerId == lease.LeaseOwnerId
            ? operation
            : null;
    }

    private static PermanentShareRequest Request(StandaloneShareOperationRecord operation) =>
        new(operation.ExactRemoteItems,
            operation.ExtractionCodeBehavior == ShareExtractionCodeBehavior.Fixed
                ? operation.FixedExtractionCode
                : null);

    private static StandaloneShareExecutionResult Pending(string message) =>
        new(StandaloneShareExecutionDisposition.ResultPending, message);
    private static StandaloneShareExecutionResult Stale() =>
        new(StandaloneShareExecutionDisposition.Stale, "任务状态已变化。");
}
