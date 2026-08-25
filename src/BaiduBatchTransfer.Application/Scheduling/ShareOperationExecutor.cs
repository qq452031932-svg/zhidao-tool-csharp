using System.Security.Cryptography;
using System.Text.Json;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Scheduling;

public sealed class ShareOperationExecutor
{
    private static readonly TimeSpan ReconciliationDelay = TimeSpan.FromMinutes(1);
    private readonly IBaiduShareAdapter adapter;
    private readonly IBaiduSessionStore sessionStore;
    private readonly IShareExecutionStore store;
    private readonly RetryPolicy retryPolicy;
    private readonly IAsyncDelay delay;
    private readonly TimeProvider timeProvider;
    private readonly ShareSubmissionEngine submissionEngine;

    public ShareOperationExecutor(
        IBaiduShareAdapter adapter,
        IBaiduSessionStore sessionStore,
        IShareExecutionStore store,
        RetryPolicy retryPolicy,
        IAsyncDelay delay,
        TimeProvider? timeProvider = null,
        ShareSubmissionEngine? submissionEngine = null)
    {
        this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        this.sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.retryPolicy = retryPolicy ?? throw new ArgumentNullException(nameof(retryPolicy));
        this.delay = delay ?? throw new ArgumentNullException(nameof(delay));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.submissionEngine = submissionEngine ?? new ShareSubmissionEngine();
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

        var discovery = await submissionEngine.DiscoverAsync(
            adapter, session, request, retryPolicy, delay, cancellationToken).ConfigureAwait(false);
        if (!discovery.IsSuccess)
        {
            if (IsAccountPausingFailure(discovery.ErrorKind!.Value))
                return await PauseAsync(operation, PauseReason(discovery.ErrorKind.Value), cancellationToken).ConfigureAwait(false);
            return await FailAsync(
                operation, discovery.ErrorKind.Value.ToString(), discovery.SafeMessage!, cancellationToken).ConfigureAwait(false);
        }

        foreach (var chunk in discovery.Value!.ExactMatches.Chunk(500))
        {
            var records = chunk.Select(value => new ShareDiscoveryBaselineRecord(
                operation.Id, value.ShareId, operation.Generation, value.ShareUrl, value.ExtractionCode,
                value.IsPermanent, value.IsAccessUnlimited)).ToArray();
            await store.AppendDiscoveryBaselinePageAsync(
                operation.Id, operation.LeaseGeneration, records, cancellationToken).ConfigureAwait(false);
        }

        var evidence = await ReadBaselineEvidenceAsync(context, request, cancellationToken).ConfigureAwait(false);
        if (!await store.TryCompleteDiscoveryBaselineAsync(
                operation.Id, operation.Generation, operation.LeaseGeneration,
                evidence.Fingerprint, evidence.Count, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false))
            return Stale();

        var existing = submissionEngine.ResolveExistingShare(
            context.ExistingShareDecision, request, evidence.Matches, context.ConfirmedExistingShare,
            requireExistingForReuse: true);
        switch (existing.Action)
        {
            case ExistingShareAction.Cancel:
                return await PauseAsync(operation, SharePauseReason.CancelledByUser, cancellationToken).ConfigureAwait(false);
            case ExistingShareAction.Reuse:
                return await ReuseAsync(context, existing.ReusableShare!, cancellationToken).ConfigureAwait(false);
            case ExistingShareAction.Incompatible:
                return await PauseAsync(operation, SharePauseReason.IncompatibleExistingShare, cancellationToken).ConfigureAwait(false);
            case ExistingShareAction.RequiresDecision:
                return await PauseAsync(operation, SharePauseReason.AwaitingExistingShareDecision, cancellationToken).ConfigureAwait(false);
            case ExistingShareAction.SubmitNew:
                break;
            default:
                throw new InvalidOperationException("The persisted existing-share decision is invalid.");
        }

        ShareOperationAutomationContext? automationContext = null;
        if (adapter is IOperationBoundBaiduShareAdapter operationBound)
            automationContext = new ShareOperationAutomationContext(
                operation.Id, operation.BatchJobId, operation.AccountId, operation.Generation,
                WebAutomationOwnerKind.TransferShare, context.ExactRemoteItems).Validate();
        var submission = await submissionEngine.ExecuteSubmissionAsync(
            request,
            new ShareSubmissionPorts(
                token => adapter is IOperationBoundBaiduShareAdapter bound
                    ? bound.PreparePermanentShareForOperationAsync(automationContext!, session, request, token)
                    : Task.FromResult(BaiduAdapterResult<SharePreparationReceipt>.Success(
                        SharePreparationReceipt.Ready, SubmissionAcceptance.DefinitelyNotAccepted)),
                token =>
                {
                    var submittedAt = timeProvider.GetUtcNow();
                    var descriptor = CreateDescriptor(operation, submittedAt);
                    return store.TryMarkSubmittingAsync(
                        operation.Id, operation.Generation, operation.LeaseGeneration,
                        descriptor, submittedAt, token);
                },
                (completedRetries, token) => store.TryBeginSubmissionAttemptAsync(
                    operation.Id, operation.Generation, operation.LeaseGeneration,
                    checked(operation.AttemptCount + completedRetries), timeProvider.GetUtcNow(), token),
                token => adapter is IOperationBoundBaiduShareAdapter bound
                    ? bound.SubmitPermanentShareForOperationAsync(automationContext!, session, request, token)
                    : adapter.CreatePermanentShareAsync(session, request, token),
                (completedAttempts, token) => store.TryRecordDefinitelyNotAcceptedRetryAsync(
                    operation.Id, operation.Generation, operation.LeaseGeneration,
                    checked(operation.AttemptCount + completedAttempts), timeProvider.GetUtcNow(), token),
                token =>
                {
                    var now = timeProvider.GetUtcNow();
                    return store.TryMarkResultPendingAsync(
                        operation.Id, operation.Generation, operation.LeaseGeneration,
                        now + ReconciliationDelay, now, token);
                }),
            retryPolicy,
            delay,
            cancellationToken).ConfigureAwait(false);

        if (submission.Disposition == ShareSubmissionDisposition.Stale) return Stale();
        if (submission.Disposition == ShareSubmissionDisposition.Accepted)
            return await AcceptReceiptAsync(context, request, submission.Receipt!, cancellationToken).ConfigureAwait(false);
        if (submission.Disposition == ShareSubmissionDisposition.ResultPending)
            return new(ShareExecutionDisposition.ResultPending, "分享结果待确认");
        if (submission.Disposition == ShareSubmissionDisposition.PreparationFailed)
        {
            if (submission.ErrorKind is { } preparationError && IsAccountPausingFailure(preparationError))
                return await PauseAsync(operation, PauseReason(preparationError), cancellationToken).ConfigureAwait(false);
            return await FailAsync(
                operation,
                submission.ErrorKind?.ToString() ?? "UnknownResponse",
                submission.SafeMessage ?? "The service returned an unrecognized response.",
                cancellationToken).ConfigureAwait(false);
        }
        if (submission.ErrorKind is { } error && IsAccountPausingFailure(error))
            return await PauseAsync(operation, PauseReason(error), cancellationToken).ConfigureAwait(false);
        return await FailAsync(
            operation,
            submission.ErrorKind?.ToString() ?? "UnknownResponse",
            submission.SafeMessage ?? "The service returned an unrecognized response.",
            cancellationToken).ConfigureAwait(false);
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

        if (!submissionEngine.IsExactAcceptedReceipt(request, receipt))
            return await MarkPendingAsync(operation, cancellationToken).ConfigureAwait(false);

        var now = timeProvider.GetUtcNow();
        var accepted = await store.AcceptResultAsync(
            operation.Id, ShareOperationStatus.Submitting, operation.LeaseGeneration,
            Result(context, receipt.ShareId, receipt.ShareUrl, receipt.ExtractionCode!, receipt.SubmittedAtUtc, now),
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

    private async Task<BaselineEvidence> ReadBaselineEvidenceAsync(
        ShareExecutionContext context,
        PermanentShareRequest request,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        ShareReconciliationPolicy.Append(hash,"share-baseline-v1");
        string? cursor=null;
        var count=0;
        var matches = new List<BaiduShareRecord>();
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
                matches.Add(share);
                count++;
            }
            cursor=page.NextCursor;
        }while(cursor is not null);
        return new(Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),count,matches);
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

    private sealed record BaselineEvidence(
        string Fingerprint,int Count,IReadOnlyList<BaiduShareRecord> Matches);
}
