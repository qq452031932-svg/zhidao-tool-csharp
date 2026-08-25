using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Scheduling;

public enum ShareReconciliationDisposition
{
    Accepted,
    DefinitelyNotAccepted,
    ResultPending,
    Ambiguous,
    RequiresUser,
    Stale,
}

public enum ShareReconciliationIssue
{
    ReconcileTimeout,
    MultipleCompatibleShares,
    AccountSessionUnavailable,
    AccountVerificationRequired,
    ListingUnavailable,
}

public sealed record ShareReconciliationResult(
    ShareReconciliationDisposition Disposition,
    string StableStatus)
{
    public override string ToString() =>
        $"ShareReconciliationResult {{ Disposition = {Disposition}, StableStatus = {StableStatus}, Evidence = [REDACTED] }}";
}

public readonly record struct ShareReconciliationWriteSnapshot(
    DateTimeOffset UpdatedAt,
    DateTimeOffset? NextReconcileAt);

public readonly record struct ShareReconciliationFailureSnapshot(
    Guid BatchJobId,
    Guid ResourceItemId,
    Guid AccountId,
    int OutputOrdinal,
    string ExactItemSetFingerprint,
    string SettingsFingerprint,
    int AttemptCount,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? NextReconcileAt);

public sealed record ShareReconciliationOptions(
    int MaximumPolls,
    int MaximumPagesPerPoll,
    TimeSpan PollDelay,
    TimeSpan RetryDelay)
{
    public static ShareReconciliationOptions Default { get; } =
        new(3, 10_000, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1));

    public void Validate()
    {
        if (MaximumPolls is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(MaximumPolls));
        if (MaximumPagesPerPoll is < 1 or > 100_000) throw new ArgumentOutOfRangeException(nameof(MaximumPagesPerPoll));
        if (PollDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(PollDelay));
        if (RetryDelay <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(RetryDelay));
    }
}

public interface IShareReconciliationStore
{
    Task<ShareExecutionContext?> LoadPendingAsync(Guid operationId, CancellationToken cancellationToken = default);
    Task<StringKeysetPage<ShareDiscoveryBaselineRecord>> GetBaselinePageAsync(
        Guid operationId, string? afterShareId, int pageSize, CancellationToken cancellationToken = default);
    Task<bool> TryAcceptAsync(
        Guid operationId, int generation, long leaseGeneration, ShareReconciliationWriteSnapshot expected,
        BaiduShareResultRecord result,
        DateTimeOffset updatedAt, CancellationToken cancellationToken = default);
    Task<bool> TryKeepPendingAsync(
        Guid operationId, int generation, long leaseGeneration, ShareReconciliationWriteSnapshot expected,
        ShareReconciliationIssue issue, DateTimeOffset? observedSessionCapturedAtUtc,
        DateTimeOffset nextReconcileAt, DateTimeOffset updatedAt, CancellationToken cancellationToken = default);
    Task<bool> TryFailDefinitelyNotAcceptedAsync(
        Guid operationId, int generation, long leaseGeneration, ShareReconciliationFailureSnapshot expected,
        DateTimeOffset updatedAt, CancellationToken cancellationToken = default);
}

public interface IShareResultReconciler
{
    Task<ShareReconciliationResult> ReconcileAsync(
        Guid operationId,
        CancellationToken cancellationToken = default);
}

public sealed class ShareOperationReconciliationService : IShareResultReconciler
{
    private const int BaselinePageSize = 500;
    private readonly IBaiduShareAdapter adapter;
    private readonly IBaiduSessionStore sessionStore;
    private readonly IShareReconciliationStore store;
    private readonly IAsyncDelay delay;
    private readonly ShareReconciliationOptions options;
    private readonly TimeProvider timeProvider;
    private readonly ShareSubmissionEngine submissionEngine;

    public ShareOperationReconciliationService(
        IBaiduShareAdapter adapter,
        IBaiduSessionStore sessionStore,
        IShareReconciliationStore store,
        IAsyncDelay delay,
        ShareReconciliationOptions? options = null,
        TimeProvider? timeProvider = null,
        ShareSubmissionEngine? submissionEngine = null)
    {
        this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        this.sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.delay = delay ?? throw new ArgumentNullException(nameof(delay));
        this.options = options ?? ShareReconciliationOptions.Default;
        this.options.Validate();
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.submissionEngine = submissionEngine ?? new ShareSubmissionEngine();
    }

    public async Task<ShareReconciliationResult> ReconcileAsync(
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An operation is required.", nameof(operationId));
        var context = await store.LoadPendingAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (context is null || !string.Equals(context.Operation.Status, "ResultPending", StringComparison.Ordinal) ||
            context.Operation.RequestDescriptorJson is null || context.Operation.SubmitStartedAt is null)
            return new(ShareReconciliationDisposition.Stale, "任务状态已变化");

        var session = await sessionStore.LoadAsync(
            context.Operation.AccountId.ToString("D"), cancellationToken).ConfigureAwait(false);
        if (session is null)
            return await KeepPendingAsync(context, ShareReconciliationIssue.AccountSessionUnavailable,
                ShareReconciliationDisposition.RequiresUser, "等待账号登录", cancellationToken).ConfigureAwait(false);

        if (adapter is IOperationBoundBaiduShareAdapter operationBound)
            return await ReconcileOperationBoundAsync(context, session, operationBound, cancellationToken)
                .ConfigureAwait(false);

        var baseline = await LoadBaselineAsync(operationId, cancellationToken).ConfigureAwait(false);
        for (var poll = 0; poll < options.MaximumPolls; poll++)
        {
            if (poll > 0)
                await delay.DelayAsync(options.PollDelay, cancellationToken).ConfigureAwait(false);

            var candidates = new Dictionary<string, BaiduShareRecord>(StringComparer.Ordinal);
            string? cursor = null;
            var pages = 0;
            do
            {
                if (++pages > options.MaximumPagesPerPoll)
                    return await KeepPendingAsync(context, ShareReconciliationIssue.ListingUnavailable,
                        ShareReconciliationDisposition.ResultPending, "分享结果待确认", cancellationToken).ConfigureAwait(false);

                BaiduAdapterResult<BaiduSharePage> response;
                try
                {
                    response = await adapter.ListSharesAsync(session, cursor, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch
                {
                    return await KeepPendingAsync(context, ShareReconciliationIssue.ListingUnavailable,
                        ShareReconciliationDisposition.ResultPending, "分享结果待确认", CancellationToken.None).ConfigureAwait(false);
                }

                if (!response.IsSuccess && response.ErrorKind == BaiduErrorKind.ExpiredLogin)
                    return await KeepPendingAsync(context, ShareReconciliationIssue.AccountSessionUnavailable,
                        ShareReconciliationDisposition.RequiresUser, "等待账号登录", cancellationToken,
                        session.CapturedAtUtc).ConfigureAwait(false);
                if (!response.IsSuccess && response.ErrorKind == BaiduErrorKind.VerificationRequired)
                    return await KeepPendingAsync(context, ShareReconciliationIssue.AccountVerificationRequired,
                        ShareReconciliationDisposition.RequiresUser, "账号需要安全验证", cancellationToken,
                        session.CapturedAtUtc).ConfigureAwait(false);
                if (!response.IsSuccess)
                    return await KeepPendingAsync(context, ShareReconciliationIssue.ListingUnavailable,
                        ShareReconciliationDisposition.ResultPending, "分享结果待确认", cancellationToken).ConfigureAwait(false);

                foreach (var candidate in response.Value!.Items)
                {
                    if (baseline.Contains(candidate.ShareId) || !IsCompatible(context, candidate)) continue;
                    candidates.TryAdd(candidate.ShareId, candidate);
                }
                cursor = response.Value.NextCursor;
            } while (cursor is not null);

            if (candidates.Count > 1)
                return await KeepPendingAsync(context, ShareReconciliationIssue.MultipleCompatibleShares,
                    ShareReconciliationDisposition.Ambiguous, "存在多个匹配分享，需人工确认", cancellationToken).ConfigureAwait(false);
            if (candidates.Count == 1)
                return await AcceptAsync(context, candidates.Values.Single(), cancellationToken).ConfigureAwait(false);
        }

        return await KeepPendingAsync(context, ShareReconciliationIssue.ReconcileTimeout,
            ShareReconciliationDisposition.ResultPending, "分享结果待确认", cancellationToken).ConfigureAwait(false);
    }

    private async Task<ShareReconciliationResult> ReconcileOperationBoundAsync(
        ShareExecutionContext context,
        AccountSession session,
        IOperationBoundBaiduShareAdapter operationBound,
        CancellationToken cancellationToken)
    {
        var operation = context.Operation;
        var request = string.Equals(context.Settings.ExtractionCodeMode, "Random", StringComparison.Ordinal)
            ? new PermanentShareRequest(context.ExactRemoteItems)
            : new PermanentShareRequest(context.ExactRemoteItems, context.Settings.FixedExtractionCode);
        var automationContext = new ShareOperationAutomationContext(
            operation.Id, operation.BatchJobId, operation.AccountId, operation.Generation,
            WebAutomationOwnerKind.TransferShare, context.ExactRemoteItems).Validate();
        BaiduAdapterResult<ShareCreationReceipt> confirmation;
        try
        {
            confirmation = await operationBound.ConfirmExactShareForOperationAsync(
                automationContext, session, request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            return await KeepPendingAsync(context, ShareReconciliationIssue.ListingUnavailable,
                ShareReconciliationDisposition.ResultPending, "分享结果待确认", CancellationToken.None).ConfigureAwait(false);
        }

        var evaluated = submissionEngine.EvaluateConfirmation(request, confirmation);
        if (evaluated.Disposition != ShareSubmissionDisposition.Accepted)
        {
            if (evaluated.Disposition == ShareSubmissionDisposition.DefinitelyNotAccepted)
                return await FailDefinitelyNotAcceptedAsync(context, cancellationToken).ConfigureAwait(false);
            if (evaluated.ErrorKind == BaiduErrorKind.ExpiredLogin)
                return await KeepPendingAsync(context, ShareReconciliationIssue.AccountSessionUnavailable,
                    ShareReconciliationDisposition.RequiresUser, "等待账号登录", cancellationToken,
                    session.CapturedAtUtc).ConfigureAwait(false);
            if (evaluated.ErrorKind == BaiduErrorKind.VerificationRequired)
                return await KeepPendingAsync(context, ShareReconciliationIssue.AccountVerificationRequired,
                    ShareReconciliationDisposition.RequiresUser, "账号需要安全验证", cancellationToken,
                    session.CapturedAtUtc).ConfigureAwait(false);
            return await KeepPendingAsync(context, ShareReconciliationIssue.ListingUnavailable,
                ShareReconciliationDisposition.ResultPending, "分享结果待确认", cancellationToken).ConfigureAwait(false);
        }
        return await AcceptReceiptAsync(context, evaluated.Receipt!, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ShareReconciliationResult> AcceptReceiptAsync(
        ShareExecutionContext context,
        ShareCreationReceipt receipt,
        CancellationToken cancellationToken)
    {
        var operation = context.Operation;
        var now = timeProvider.GetUtcNow();
        var result = new BaiduShareResultRecord(
            Guid.NewGuid(), operation.Id, operation.BatchJobId, operation.ResourceItemId, operation.AccountId,
            operation.ExactItemSetFingerprint, operation.SettingsFingerprint, operation.Generation,
            receipt.ShareId, receipt.ShareUrl, receipt.ExtractionCode!, true, true, receipt.SubmittedAtUtc, now);
        var accepted = await store.TryAcceptAsync(
            operation.Id, operation.Generation, operation.LeaseGeneration, Snapshot(operation), result, now,
            cancellationToken).ConfigureAwait(false);
        return accepted
            ? new(ShareReconciliationDisposition.Accepted, "分享成功")
            : new(ShareReconciliationDisposition.Stale, "任务状态已变化");
    }

    private async Task<HashSet<string>> LoadBaselineAsync(Guid operationId, CancellationToken cancellationToken)
    {
        var baseline = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var page = await store.GetBaselinePageAsync(operationId, cursor, BaselinePageSize, cancellationToken)
                .ConfigureAwait(false);
            foreach (var item in page.Items) baseline.Add(item.ShareId);
            cursor = page.NextCursor;
        } while (cursor is not null);
        return baseline;
    }

    private static bool IsCompatible(ShareExecutionContext context, BaiduShareRecord candidate)
    {
        if (candidate.RemoteItems != context.ExactRemoteItems ||
            candidate.IsPermanent != context.Settings.IsPermanent ||
            candidate.IsAccessUnlimited != context.Settings.IsAccessUnlimited)
            return false;
        return string.Equals(context.Settings.ExtractionCodeMode, "Random", StringComparison.Ordinal)
            ? !string.IsNullOrWhiteSpace(candidate.ExtractionCode)
            : string.Equals(candidate.ExtractionCode, context.Settings.FixedExtractionCode, StringComparison.Ordinal);
    }

    private async Task<ShareReconciliationResult> AcceptAsync(
        ShareExecutionContext context,
        BaiduShareRecord candidate,
        CancellationToken cancellationToken)
    {
        var operation = context.Operation;
        var now = timeProvider.GetUtcNow();
        var result = new BaiduShareResultRecord(
            Guid.NewGuid(), operation.Id, operation.BatchJobId, operation.ResourceItemId, operation.AccountId,
            operation.ExactItemSetFingerprint, operation.SettingsFingerprint, operation.Generation,
            candidate.ShareId, candidate.ShareUrl, candidate.ExtractionCode, true, true, now, now);
        var accepted = await store.TryAcceptAsync(
            operation.Id, operation.Generation, operation.LeaseGeneration, Snapshot(operation), result, now,
            cancellationToken).ConfigureAwait(false);
        return accepted
            ? new(ShareReconciliationDisposition.Accepted, "分享成功")
            : new(ShareReconciliationDisposition.Stale, "任务状态已变化");
    }

    private async Task<ShareReconciliationResult> KeepPendingAsync(
        ShareExecutionContext context,
        ShareReconciliationIssue issue,
        ShareReconciliationDisposition disposition,
        string stableStatus,
        CancellationToken cancellationToken,
        DateTimeOffset? observedSessionCapturedAtUtc = null)
    {
        var now = timeProvider.GetUtcNow();
        var changed = await store.TryKeepPendingAsync(
            context.Operation.Id, context.Operation.Generation, context.Operation.LeaseGeneration,
            Snapshot(context.Operation), issue, observedSessionCapturedAtUtc,
            now.Add(options.RetryDelay), now, cancellationToken).ConfigureAwait(false);
        return changed
            ? new(disposition, stableStatus)
            : new(ShareReconciliationDisposition.Stale, "任务状态已变化");
    }

    private async Task<ShareReconciliationResult> FailDefinitelyNotAcceptedAsync(
        ShareExecutionContext context,
        CancellationToken cancellationToken)
    {
        var operation = context.Operation;
        var now = timeProvider.GetUtcNow();
        var changed = await store.TryFailDefinitelyNotAcceptedAsync(
            operation.Id, operation.Generation, operation.LeaseGeneration,
            new(operation.BatchJobId, operation.ResourceItemId, operation.AccountId, operation.OutputOrdinal,
                operation.ExactItemSetFingerprint, operation.SettingsFingerprint, operation.AttemptCount,
                operation.UpdatedAt, operation.NextReconcileAt),
            now, cancellationToken).ConfigureAwait(false);
        return changed
            ? new(ShareReconciliationDisposition.DefinitelyNotAccepted, "分享未被接受，可安全重试")
            : new(ShareReconciliationDisposition.Stale, "任务状态已变化");
    }

    private static ShareReconciliationWriteSnapshot Snapshot(ShareOperationRecord operation) =>
        new(operation.UpdatedAt, operation.NextReconcileAt);
}
