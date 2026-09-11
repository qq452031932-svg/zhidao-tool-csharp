using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Jobs;

public enum JobResultActionDisposition
{
    Accepted,
    Stale,
    ConfirmedPresent,
    ConfirmedAbsent,
    Ambiguous,
    ResultPending,
    RequiresUser,
}

public sealed record JobResultActionResult(
    JobResultActionDisposition Disposition,
    string StatusText)
{
    public bool Accepted => Disposition is not JobResultActionDisposition.Stale;
    public bool IsStale => Disposition == JobResultActionDisposition.Stale;

    public override string ToString() =>
        $"JobResultActionResult {{ Disposition = {Disposition}, StatusText = {StatusText}, UserContent = [REDACTED] }}";
}

public interface IJobResultShareActionStore
{
    Task<bool> TryQueueFailedShareAsync(
        Guid operationId,
        int expectedGeneration,
        DateTimeOffset expectedUpdatedAt,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    Task<bool> TryResumePausedShareAsync(
        Guid operationId,
        int expectedGeneration,
        DateTimeOffset expectedUpdatedAt,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);
}

public interface IJobResultTransferActionStore
{
    Task<bool> ResumePausedAsync(
        Guid operationId,
        int expectedAttemptCount,
        DateTimeOffset expectedUpdatedAt,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);
}

public interface IJobResultActionCapability
{
    bool CanUseTransferActions { get; }
    bool CanUseShareActions { get; }
}

public sealed class UnavailableJobResultActionCapability : IJobResultActionCapability
{
    public bool CanUseTransferActions => false;
    public bool CanUseShareActions => false;
}

public sealed class DirectBackgroundJobResultActionCapability : IJobResultActionCapability
{
    public bool CanUseTransferActions => true;
    public bool CanUseShareActions => true;
}

public interface IJobResultActions
{
    Task<JobResultActionResult> RetryShareOnlyAsync(Guid shareOperationId, int expectedGeneration, DateTimeOffset expectedUpdatedAt, CancellationToken cancellationToken = default);
    Task<JobResultActionResult> ResumeShareAsync(Guid shareOperationId, int expectedGeneration, DateTimeOffset expectedUpdatedAt, CancellationToken cancellationToken = default);
    Task<JobResultActionResult> ResumeTransferAsync(Guid transferOperationId, int expectedAttemptCount, DateTimeOffset expectedUpdatedAt, CancellationToken cancellationToken = default);
    Task<JobResultActionResult> ReconcileTransferAsync(Guid transferOperationId, CancellationToken cancellationToken = default);
    Task<JobResultActionResult> ReconcileShareAsync(Guid shareOperationId, CancellationToken cancellationToken = default);
}

public sealed class JobResultActions(
    IJobResultShareActionStore shareStore,
    IJobResultTransferActionStore transferStore,
    ITransferResultReconciler transferReconciler,
    IShareResultReconciler shareReconciler,
    TimeProvider? timeProvider = null,
    IJobResultActionCapability? capability = null) : IJobResultActions
{
    private readonly IJobResultShareActionStore shareStore =
        shareStore ?? throw new ArgumentNullException(nameof(shareStore));
    private readonly IJobResultTransferActionStore transferStore =
        transferStore ?? throw new ArgumentNullException(nameof(transferStore));
    private readonly ITransferResultReconciler transferReconciler =
        transferReconciler ?? throw new ArgumentNullException(nameof(transferReconciler));
    private readonly IShareResultReconciler shareReconciler =
        shareReconciler ?? throw new ArgumentNullException(nameof(shareReconciler));
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;
    private readonly IJobResultActionCapability capability = capability ?? new UnavailableJobResultActionCapability();

    public async Task<JobResultActionResult> RetryShareOnlyAsync(
        Guid shareOperationId,
        int expectedGeneration,
        DateTimeOffset expectedUpdatedAt,
        CancellationToken cancellationToken = default)
    {
        ValidateId(shareOperationId, nameof(shareOperationId));
        if (!capability.CanUseShareActions) return ShareUnavailable();
        ValidateSnapshot(expectedGeneration, expectedUpdatedAt);
        var changed = await shareStore.TryQueueFailedShareAsync(
            shareOperationId, expectedGeneration, expectedUpdatedAt,
            timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        return changed
            ? new(JobResultActionDisposition.Accepted, "分享任务已重新加入队列")
            : Stale();
    }

    public async Task<JobResultActionResult> ResumeShareAsync(
        Guid shareOperationId,
        int expectedGeneration,
        DateTimeOffset expectedUpdatedAt,
        CancellationToken cancellationToken = default)
    {
        ValidateId(shareOperationId, nameof(shareOperationId));
        if (!capability.CanUseShareActions) return ShareUnavailable();
        ValidateSnapshot(expectedGeneration, expectedUpdatedAt);
        var changed = await shareStore.TryResumePausedShareAsync(
            shareOperationId, expectedGeneration, expectedUpdatedAt,
            timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        return changed
            ? new(JobResultActionDisposition.Accepted, "分享任务已恢复")
            : Stale();
    }

    public async Task<JobResultActionResult> ResumeTransferAsync(
        Guid transferOperationId,
        int expectedAttemptCount,
        DateTimeOffset expectedUpdatedAt,
        CancellationToken cancellationToken = default)
    {
        ValidateId(transferOperationId, nameof(transferOperationId));
        if (!capability.CanUseTransferActions) return TransferUnavailable();
        if (expectedAttemptCount < 0) throw new ArgumentOutOfRangeException(nameof(expectedAttemptCount));
        if (expectedUpdatedAt == default) throw new ArgumentException("A transfer snapshot is required.", nameof(expectedUpdatedAt));
        var changed = await transferStore.ResumePausedAsync(
            transferOperationId, expectedAttemptCount, expectedUpdatedAt,
            timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        return changed
            ? new(JobResultActionDisposition.Accepted, "转存任务已恢复")
            : Stale();
    }

    public async Task<JobResultActionResult> ReconcileTransferAsync(
        Guid transferOperationId,
        CancellationToken cancellationToken = default)
    {
        ValidateId(transferOperationId, nameof(transferOperationId));
        if (!capability.CanUseTransferActions) return TransferUnavailable();
        var result = await transferReconciler.ReconcileAsync(transferOperationId, cancellationToken)
            .ConfigureAwait(false);
        return result switch
        {
            TransferConfirmation.ConfirmedPresent => new(JobResultActionDisposition.ConfirmedPresent, "已确认转存成功"),
            TransferConfirmation.ConfirmedAbsent => new(JobResultActionDisposition.ConfirmedAbsent, "已确认未完成转存"),
            TransferConfirmation.Ambiguous => new(JobResultActionDisposition.Ambiguous, "转存结果仍待确认"),
            _ => throw new InvalidOperationException("The transfer reconciler returned an unsupported result."),
        };
    }

    public async Task<JobResultActionResult> ReconcileShareAsync(
        Guid shareOperationId,
        CancellationToken cancellationToken = default)
    {
        ValidateId(shareOperationId, nameof(shareOperationId));
        if (!capability.CanUseShareActions) return ShareUnavailable();
        var result = await shareReconciler.ReconcileAsync(shareOperationId, cancellationToken).ConfigureAwait(false);
        return new(Map(result.Disposition), result.StableStatus);
    }

    private static JobResultActionDisposition Map(ShareReconciliationDisposition disposition) => disposition switch
    {
        ShareReconciliationDisposition.Accepted => JobResultActionDisposition.ConfirmedPresent,
        ShareReconciliationDisposition.DefinitelyNotAccepted => JobResultActionDisposition.ConfirmedAbsent,
        ShareReconciliationDisposition.ResultPending => JobResultActionDisposition.ResultPending,
        ShareReconciliationDisposition.Ambiguous => JobResultActionDisposition.Ambiguous,
        ShareReconciliationDisposition.RequiresUser => JobResultActionDisposition.RequiresUser,
        ShareReconciliationDisposition.Stale => JobResultActionDisposition.Stale,
        _ => throw new ArgumentOutOfRangeException(nameof(disposition)),
    };

    private static JobResultActionResult Stale() =>
        new(JobResultActionDisposition.Stale, "任务状态已变化，请刷新后重试");

    private static JobResultActionResult TransferUnavailable() =>
        new(JobResultActionDisposition.RequiresUser, "转存功能尚未通过现场验证");

    private static JobResultActionResult ShareUnavailable() =>
        new(JobResultActionDisposition.RequiresUser, "分享功能尚未通过现场验证");

    private static void ValidateId(Guid id, string name)
    {
        if (id == Guid.Empty) throw new ArgumentException("An operation is required.", name);
    }

    private static void ValidateSnapshot(int expectedGeneration, DateTimeOffset expectedUpdatedAt)
    {
        if (expectedGeneration < 0) throw new ArgumentOutOfRangeException(nameof(expectedGeneration));
        if (expectedUpdatedAt == default)
            throw new ArgumentException("A share snapshot is required.", nameof(expectedUpdatedAt));
    }
}
