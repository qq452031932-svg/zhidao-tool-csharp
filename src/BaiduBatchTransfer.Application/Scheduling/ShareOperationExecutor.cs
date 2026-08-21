using System.Security.Cryptography;
using System.Text.Json;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Scheduling;

public sealed class ShareOperationExecutor
{
    private const int MaximumDiscoveryPages = 10_000;
    private static readonly TimeSpan ReconciliationDelay = TimeSpan.FromMinutes(1);
    private readonly IBaiduShareAdapter adapter;
    private readonly IBaiduSessionStore sessionStore;
    private readonly IShareExecutionStore store;
    private readonly RetryPolicy retryPolicy;
    private readonly IAsyncDelay delay;
    private readonly TimeProvider timeProvider;
    private readonly ShareReconciliationPolicy reconciliation = new();

    public ShareOperationExecutor(
        IBaiduShareAdapter adapter,
        IBaiduSessionStore sessionStore,
        IShareExecutionStore store,
        RetryPolicy retryPolicy,
        IAsyncDelay delay,
        TimeProvider? timeProvider = null)
    {
        this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        this.sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.retryPolicy = retryPolicy ?? throw new ArgumentNullException(nameof(retryPolicy));
        this.delay = delay ?? throw new ArgumentNullException(nameof(delay));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ShareExecutionResult> ExecuteAsync(
        ShareExecutionLease lease,
        CancellationToken cancellationToken = default)
    {
        if (lease.OperationId == Guid.Empty || lease.Generation < 1 || lease.LeaseGeneration < 1)
            throw new ArgumentException("A valid share execution lease is required.", nameof(lease));
        var context = await store.LoadAsync(lease.OperationId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The share operation is no longer available.");
        if (context.Operation.Generation != lease.Generation ||
            context.Operation.LeaseGeneration != lease.LeaseGeneration)
            return Stale();
        if (!context.Selected || !context.ShareEnabled)
            return ShareExecutionResult.NotEnabled;

        ValidateContext(context);
        var operation = context.Operation;
        var request = BuildRequest(context);
        var session = await sessionStore.LoadAsync(operation.AccountId.ToString("D"), cancellationToken)
            .ConfigureAwait(false);
        if (session is null)
            return await PauseAsync(operation, SharePauseReason.AccountSessionUnavailable, cancellationToken).ConfigureAwait(false);

        if (!await store.TryMarkPreparingAsync(
                operation.Id, operation.Generation, operation.LeaseGeneration,
                timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false))
            return Stale();

        var discovery = await DiscoverExactMatchesAsync(operation, session, request, cancellationToken).ConfigureAwait(false);
        if (!discovery.IsSuccess)
        {
            if (discovery.PauseReason is { } reason)
                return await PauseAsync(operation, reason, cancellationToken).ConfigureAwait(false);
            return await FailAsync(operation, discovery.ErrorCode!, discovery.SafeMessage!, cancellationToken).ConfigureAwait(false);
        }

        var evidence = await ReadBaselineEvidenceAsync(context, request, cancellationToken).ConfigureAwait(false);
        if (!await store.TryCompleteDiscoveryBaselineAsync(
                operation.Id, operation.Generation, operation.LeaseGeneration,
                evidence.Fingerprint, evidence.Count, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false))
            return Stale();

        switch (context.ExistingShareDecision)
        {
            case ExistingShareDecision.Cancel:
                return await PauseAsync(operation, SharePauseReason.CancelledByUser, cancellationToken).ConfigureAwait(false);
            case ExistingShareDecision.Reuse when evidence.ConfirmedMatch is not null:
                return await ReuseAsync(context, evidence.ConfirmedMatch, cancellationToken).ConfigureAwait(false);
            case ExistingShareDecision.Reuse when context.ConfirmedExistingShare is not null:
                return await PauseAsync(operation, SharePauseReason.AwaitingExistingShareDecision, cancellationToken).ConfigureAwait(false);
            case ExistingShareDecision.Reuse when evidence.FirstCompatible is not null:
                return await ReuseAsync(context, evidence.FirstCompatible, cancellationToken).ConfigureAwait(false);
            case ExistingShareDecision.Reuse when evidence.Count > 0:
                return await PauseAsync(operation, SharePauseReason.IncompatibleExistingShare, cancellationToken).ConfigureAwait(false);
            case ExistingShareDecision.Reuse:
                return await PauseAsync(operation, SharePauseReason.AwaitingExistingShareDecision, cancellationToken).ConfigureAwait(false);
            case ExistingShareDecision.Regenerate:
                break;
            case null when evidence.Count > 0:
                return await PauseAsync(operation, SharePauseReason.AwaitingExistingShareDecision, cancellationToken).ConfigureAwait(false);
            case null:
                break;
            default:
                throw new InvalidOperationException("The persisted existing-share decision is invalid.");
        }

        var submittedAt = timeProvider.GetUtcNow();
        var descriptor = CreateDescriptor(operation, submittedAt);
        if (!await store.TryMarkSubmittingAsync(
                operation.Id, operation.Generation, operation.LeaseGeneration,
                descriptor, submittedAt, cancellationToken).ConfigureAwait(false))
            return Stale();

        var completedRetries = 0;
        while (true)
        {
            if (completedRetries > 0)
            {
                var expectedAttempt = checked(operation.AttemptCount + completedRetries);
                if (!await store.TryBeginSubmissionAttemptAsync(
                        operation.Id, operation.Generation, operation.LeaseGeneration,
                        expectedAttempt, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false))
                    return Stale();
            }
            BaiduAdapterResult<ShareCreationReceipt> result;
            try
            {
                result = await adapter.CreatePermanentShareAsync(session, request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await MarkPendingAsync(operation, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            catch (Exception)
            {
                return await MarkPendingAsync(operation, CancellationToken.None).ConfigureAwait(false);
            }

            if (result.IsSuccess)
                return await AcceptReceiptAsync(context, request, result.Value!, cancellationToken).ConfigureAwait(false);

            if (result.SubmissionAcceptance != SubmissionAcceptance.DefinitelyNotAccepted)
                return await MarkPendingAsync(operation, cancellationToken).ConfigureAwait(false);

            var expectedCompletedAttempt = checked(operation.AttemptCount + completedRetries + 1);
            if (!await store.TryRecordDefinitelyNotAcceptedRetryAsync(
                    operation.Id, operation.Generation, operation.LeaseGeneration,
                    expectedCompletedAttempt, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false))
                return Stale();

            var decision = retryPolicy.Decide(result.ErrorKind!.Value, completedRetries, result.RetryAfter);
            if (!decision.ShouldRetry)
            {
                if (IsAccountPausingFailure(result.ErrorKind.Value))
                    return await PauseAsync(operation, PauseReason(result.ErrorKind.Value), cancellationToken).ConfigureAwait(false);
                return await FailAsync(operation, result.ErrorKind.Value.ToString(), result.SafeMessage!, cancellationToken).ConfigureAwait(false);
            }

            completedRetries++;
            await delay.DelayAsync(decision.Delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ShareExecutionResult> AcceptReceiptAsync(
        ShareExecutionContext context,
        PermanentShareRequest request,
        ShareCreationReceipt receipt,
        CancellationToken cancellationToken)
    {
        var operation = context.Operation;
        if (!await store.TryRecordAdapterRequestAsync(
                operation.Id, operation.Generation, operation.LeaseGeneration,
                receipt.AdapterRequestId, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false))
            return Stale();

        if (receipt.Request.RemoteItems != request.RemoteItems ||
            receipt.Request.UsesRandomExtractionCode != request.UsesRandomExtractionCode ||
            !string.Equals(receipt.Request.FixedExtractionCode, request.FixedExtractionCode, StringComparison.Ordinal) ||
            !receipt.IsFinalAcceptance || string.IsNullOrWhiteSpace(receipt.ExtractionCode))
            return await MarkPendingAsync(operation, cancellationToken).ConfigureAwait(false);

        var now = timeProvider.GetUtcNow();
        var accepted = await store.AcceptResultAsync(
            operation.Id, ShareOperationStatus.Submitting, operation.LeaseGeneration,
            Result(context, receipt.ShareId, receipt.ShareUrl, receipt.ExtractionCode, receipt.SubmittedAtUtc, now),
            now, cancellationToken).ConfigureAwait(false);
        return accepted
            ? new(ShareExecutionDisposition.Succeeded, "分享成功")
            : Stale();
    }

    private async Task<ShareExecutionResult> ReuseAsync(
        ShareExecutionContext context,
        BaiduShareRecord existing,
        CancellationToken cancellationToken)
    {
        var operation = context.Operation;
        var now = timeProvider.GetUtcNow();
        var accepted = await store.ReuseExistingResultAsync(
            operation.Id, operation.LeaseGeneration,
            Result(context, existing.ShareId, existing.ShareUrl, existing.ExtractionCode, now, now),
            now, cancellationToken).ConfigureAwait(false);
        return accepted
            ? new(ShareExecutionDisposition.ReusedExisting, "复用已有分享")
            : Stale();
    }

    private static BaiduShareResultRecord Result(
        ShareExecutionContext context,
        string shareId,
        string shareUrl,
        string extractionCode,
        DateTimeOffset sharedAt,
        DateTimeOffset updatedAt)
    {
        var operation = context.Operation;
        return new(
            Guid.NewGuid(), operation.Id, operation.BatchJobId, operation.ResourceItemId, operation.AccountId,
            operation.ExactItemSetFingerprint, operation.SettingsFingerprint, operation.Generation,
            shareId, shareUrl, extractionCode, true, true, sharedAt, updatedAt);
    }

    private async Task<DiscoveryResult> DiscoverExactMatchesAsync(
        ShareOperationRecord operation,
        AccountSession session,
        PermanentShareRequest request,
        CancellationToken cancellationToken)
    {
        var cursor = (string?)null;
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        for (var pageNumber = 0; pageNumber < MaximumDiscoveryPages; pageNumber++)
        {
            BaiduAdapterResult<BaiduSharePage> page;
            var completedRetries = 0;
            while (true)
            {
                try
                {
                    page = await adapter.ListSharesAsync(session, cursor, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception)
                {
                    return DiscoveryResult.Failure("UnknownResponse", "The service returned an unrecognized response.");
                }
                if (page.IsSuccess) break;
                var retry = retryPolicy.Decide(page.ErrorKind!.Value, completedRetries, page.RetryAfter);
                if (!retry.ShouldRetry)
                {
                    return IsAccountPausingFailure(page.ErrorKind.Value)
                        ? DiscoveryResult.Paused(PauseReason(page.ErrorKind.Value))
                        : DiscoveryResult.Failure(page.ErrorKind.Value.ToString(), page.SafeMessage!);
                }
                completedRetries++;
                await delay.DelayAsync(retry.Delay, cancellationToken).ConfigureAwait(false);
            }

            var matches = page.Value!.Items.Where(value => value.RemoteItems == request.RemoteItems)
                .Select(value => new ShareDiscoveryBaselineRecord(
                    operation.Id,value.ShareId,operation.Generation,value.ShareUrl,value.ExtractionCode,
                    value.IsPermanent,value.IsAccessUnlimited)).ToArray();
            if (matches.Length > 0)
                await store.AppendDiscoveryBaselinePageAsync(
                    operation.Id,operation.LeaseGeneration,matches,cancellationToken).ConfigureAwait(false);
            var next = page.Value.NextCursor;
            if (next is null) return DiscoveryResult.Success();
            if (string.IsNullOrWhiteSpace(next) || !seenCursors.Add(next))
                return DiscoveryResult.Failure("InvalidCursor", "The service returned an unrecognized response.");
            cursor = next;
        }
        return DiscoveryResult.Failure("PageLimit", "The service returned an unrecognized response.");
    }

    private async Task<BaselineEvidence> ReadBaselineEvidenceAsync(
        ShareExecutionContext context,
        PermanentShareRequest request,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        ShareReconciliationPolicy.Append(hash,"share-baseline-v1");
        string? cursor=null;
        var count=0;
        BaiduShareRecord? firstCompatible=null;
        BaiduShareRecord? confirmedMatch=null;
        do
        {
            var page=await store.GetDiscoveryBaselineKeysetPageAsync(
                context.Operation.Id,cursor,500,cancellationToken).ConfigureAwait(false);
            foreach(var entry in page.Items)
            {
                if(entry.ShareUrl is null||entry.ExtractionCode is null||entry.IsPermanent is null||entry.IsAccessUnlimited is null)
                    throw new InvalidOperationException("The discovery evidence is incomplete.");
                ShareReconciliationPolicy.Append(hash,entry.ShareId);
                ShareReconciliationPolicy.Append(hash,entry.IsPermanent.Value?"1":"0");
                ShareReconciliationPolicy.Append(hash,entry.IsAccessUnlimited.Value?"1":"0");
                ShareReconciliationPolicy.Append(hash,entry.ExtractionCode);
                var share=new BaiduShareRecord(entry.ShareId,context.ExactRemoteItems,entry.ShareUrl,entry.ExtractionCode,
                    entry.IsPermanent.Value,entry.IsAccessUnlimited.Value);
                if(firstCompatible is null&&reconciliation.IsCompatible(share,request))firstCompatible=share;
                if(context.ConfirmedExistingShare is { } confirmed&&SameShare(share,confirmed)&&
                   reconciliation.IsCompatible(share,request))confirmedMatch=share;
                count++;
            }
            cursor=page.NextCursor;
        }while(cursor is not null);
        return new(Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),count,firstCompatible,confirmedMatch);
    }

    private async Task<ShareExecutionResult> MarkPendingAsync(
        ShareOperationRecord operation,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var changed = await store.TryMarkResultPendingAsync(
            operation.Id, operation.Generation, operation.LeaseGeneration,
            now + ReconciliationDelay, now, cancellationToken).ConfigureAwait(false);
        return changed ? new(ShareExecutionDisposition.ResultPending, "分享结果待确认") : Stale();
    }

    private async Task<ShareExecutionResult> PauseAsync(
        ShareOperationRecord operation,
        SharePauseReason reason,
        CancellationToken cancellationToken)
    {
        var changed = await store.TryPauseAsync(
            operation.Id, operation.Generation, operation.LeaseGeneration,
            reason, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        return changed ? new(ShareExecutionDisposition.Paused, StablePauseStatus(reason)) : Stale();
    }

    private async Task<ShareExecutionResult> FailAsync(
        ShareOperationRecord operation,
        string errorCode,
        string safeMessage,
        CancellationToken cancellationToken)
    {
        var changed = await store.TryFailAsync(
            operation.Id, operation.Generation, operation.LeaseGeneration,
            errorCode, safeMessage, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        return changed ? new(ShareExecutionDisposition.Failed, "分享失败") : Stale();
    }

    private static PermanentShareRequest BuildRequest(ShareExecutionContext context) =>
        string.Equals(context.Settings.ExtractionCodeMode, "Random", StringComparison.Ordinal)
            ? new(context.ExactRemoteItems)
            : new(context.ExactRemoteItems, context.Settings.FixedExtractionCode);

    private static void ValidateContext(ShareExecutionContext context)
    {
        var operation = context.Operation;
        var settings = context.Settings;
        if (operation.Status != ShareOperationStatus.Reserved.ToString() ||
            operation.Id == Guid.Empty || operation.Generation < 1 || operation.LeaseGeneration < 1 ||
            operation.BatchJobId != settings.BatchJobId ||
            !string.Equals(operation.ExactItemSetFingerprint, context.ExactRemoteItems.Fingerprint, StringComparison.Ordinal) ||
            !settings.IsPermanent || !settings.IsAccessUnlimited ||
            !string.Equals(operation.SettingsFingerprint, settings.SettingsFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("The confirmed share execution boundary is invalid.");

        ShareExtractionCodeBehavior behavior;
        if (string.Equals(settings.ExtractionCodeMode, "Random", StringComparison.Ordinal))
        {
            if (settings.FixedExtractionCode is not null)
                throw new InvalidOperationException("The confirmed share settings are invalid.");
            behavior = ShareExtractionCodeBehavior.Random;
        }
        else if (string.Equals(settings.ExtractionCodeMode, "Fixed", StringComparison.Ordinal))
        {
            if (!PermanentShareRequest.IsValidFixedExtractionCode(settings.FixedExtractionCode))
                throw new InvalidOperationException("The confirmed fixed extraction code is invalid.");
            behavior = ShareExtractionCodeBehavior.Fixed;
        }
        else throw new InvalidOperationException("The confirmed share settings are invalid.");

        var fingerprint = ShareSettingsFingerprint.Compute(true, true, behavior, settings.FixedExtractionCode);
        if (!string.Equals(fingerprint, settings.SettingsFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("The confirmed share settings fingerprint is stale.");
    }

    private static string CreateDescriptor(ShareOperationRecord operation, DateTimeOffset submittedAt) =>
        JsonSerializer.Serialize(new
        {
            Version = 1,
            ClientRequestId = Guid.NewGuid().ToString("N"),
            operation.Generation,
            ExactSet = operation.ExactItemSetFingerprint,
            Settings = operation.SettingsFingerprint,
            SubmittedAtUtc = submittedAt.ToUniversalTime(),
        });

    private static bool SameShare(BaiduShareRecord left, BaiduShareRecord right) =>
        string.Equals(left.ShareId,right.ShareId,StringComparison.Ordinal) &&
        left.RemoteItems == right.RemoteItems &&
        string.Equals(left.ShareUrl,right.ShareUrl,StringComparison.Ordinal) &&
        string.Equals(left.ExtractionCode,right.ExtractionCode,StringComparison.Ordinal) &&
        left.IsPermanent == right.IsPermanent && left.IsAccessUnlimited == right.IsAccessUnlimited;

    private static SharePauseReason PauseReason(BaiduErrorKind errorKind) => errorKind switch
    {
        BaiduErrorKind.ExpiredLogin => SharePauseReason.AccountSessionUnavailable,
        BaiduErrorKind.VerificationRequired => SharePauseReason.AccountVerificationRequired,
        _ => SharePauseReason.ShareSettingsRejected,
    };

    private static bool IsAccountPausingFailure(BaiduErrorKind errorKind) =>
        errorKind is BaiduErrorKind.ExpiredLogin or BaiduErrorKind.VerificationRequired or BaiduErrorKind.NotValidated;

    private static string StablePauseStatus(SharePauseReason reason) => reason switch
    {
        SharePauseReason.AwaitingExistingShareDecision => "等待确认已有分享",
        SharePauseReason.IncompatibleExistingShare => "已有分享设置不兼容",
        SharePauseReason.CancelledByUser => "用户取消分享",
        SharePauseReason.AccountSessionUnavailable => "账号登录已失效",
        SharePauseReason.AccountVerificationRequired => "账号需要安全验证",
        SharePauseReason.ShareSettingsRejected => "分享设置不可用",
        _ => "分享已暂停",
    };

    private static ShareExecutionResult Stale() => new(ShareExecutionDisposition.Stale, "分享任务状态已变化");

    private sealed record DiscoveryResult(
        bool IsSuccess,
        SharePauseReason? PauseReason,
        string? ErrorCode,
        string? SafeMessage)
    {
        public static DiscoveryResult Success() => new(true, null, null, null);
        public static DiscoveryResult Paused(SharePauseReason reason) => new(false, reason, null, null);
        public static DiscoveryResult Failure(string code, string message) => new(false, null, code, message);
    }

    private sealed record BaselineEvidence(
        string Fingerprint,int Count,BaiduShareRecord? FirstCompatible,BaiduShareRecord? ConfirmedMatch);
}
