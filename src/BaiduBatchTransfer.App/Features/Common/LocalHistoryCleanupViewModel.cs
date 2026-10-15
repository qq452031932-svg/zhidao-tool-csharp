using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.Core.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BaiduBatchTransfer.App.Features.Common;

/// <summary>Coordinates local preview, confirmation and deletion without any remote capability.</summary>
public sealed class LocalHistoryCleanupViewModel : ObservableObject, IDisposable
{
    private readonly LocalTerminalHistoryService? service;
    private readonly ILocalHistoryCleanupInteraction? interaction;
    private readonly LocalHistoryKind kind;
    private readonly Func<bool> ownerAvailable;
    private readonly Func<CancellationToken, Task> reload;
    private readonly Action<string> report;
    private readonly IBackgroundOperationRunner backgroundRunner;
    private readonly IAsyncDelay conflictRetryDelay;
    // 只有转存一侧有：用来把还没结束的批次一并结束。为空时保持原来「只清已结束的」行为。
    private readonly EndBatchService? unfinishedBatches;
    private int unfinishedBatchCount;
    private readonly CancellationTokenSource lifetime = new();
    private long previewGeneration;
    private bool closed;
    private bool busy;
    private bool deferredPreviewRefresh;
    private LocalHistoryCleanupPreview? preview;
    private Task? previewRefresh;
    private CancellationTokenSource? conflictRetry;

    public LocalHistoryCleanupViewModel(LocalTerminalHistoryService? service,
        ILocalHistoryCleanupInteraction? interaction, LocalHistoryKind kind, Func<bool> ownerAvailable,
        Func<CancellationToken, Task> reload, Action<string> report,
        IBackgroundOperationRunner? backgroundRunner = null,
        IAsyncDelay? conflictRetryDelay = null,
        EndBatchService? unfinishedBatches = null)
    {
        this.service = service;
        this.interaction = interaction;
        this.kind = kind;
        this.ownerAvailable = ownerAvailable;
        this.reload = reload;
        this.report = report;
        this.backgroundRunner = backgroundRunner ?? new BackgroundOperationRunner();
        this.conflictRetryDelay = conflictRetryDelay ?? new TimeProviderDelay(TimeProvider.System);
        this.unfinishedBatches = kind == LocalHistoryKind.Transfer ? unfinishedBatches : null;
        ClearCommand = new AsyncRelayCommand(ClearAsync, () => CanClear);
    }

    public LocalHistoryCleanupPreview? Preview => preview;
    public bool IsBusy => busy;
    /// <summary>还没结束、清空时会先询问是否结束的批次数。</summary>
    public int UnfinishedBatchCount => unfinishedBatchCount;
    // 导入错了的批次也要能清掉：只有未结束的批次时按钮同样可用，点了会先问要不要结束它们。
    public bool CanClear => !closed && !busy && ownerAvailable() && interaction is not null &&
        preview is { HasConflicts: false } current && (current.TerminalBatchCount > 0 || unfinishedBatchCount > 0);
    public IAsyncRelayCommand ClearCommand { get; }

    // 按钮变灰时必须说出原因。此前提示语一律是"任务处理或保存期间不可清空"，
    // 而真实原因往往不是这个——比如残留的自动化记录会把清理永久挡住，用户
    // 看到的却是一个没有解释、也没有出路的灰按钮，只会以为软件坏了。
    public string ClearBlockedReason
    {
        get
        {
            if (interaction is null || service is null) return "当前环境不支持清空本地历史。";
            if (closed) return "工作区已关闭。";
            if (busy) return "正在清理，请稍候。";
            if (preview is null) return "正在检查可清理的历史……";
            if (!ownerAvailable()) return "任务处理或保存期间不可清空，请等待当前操作结束。";
            if (preview.HasConflicts)
                return "有未收尾的任务记录，暂时不能清空。请等待进行中的操作结束；" +
                       "若软件此前异常退出，重启后会自动收尾，仍无法清空请联系技术支持。";
            if (preview.TerminalBatchCount == 0)
                return unfinishedBatchCount > 0
                    ? $"有 {unfinishedBatchCount} 个批次还没结束。点击后可选择先停止它们再清空；仅删除本地记录，不影响百度网盘文件和分享链接。"
                    : "没有可清空的记录。";
            var unfinishedNote = unfinishedBatchCount > 0
                ? $"另有 {unfinishedBatchCount} 个批次还没结束，点击后可选择是否一并停止并清空。"
                : string.Empty;
            return unfinishedNote + (preview.RetryableFailureCount > 0
                ? $"可清空 {preview.TerminalBatchCount} 个已结束批次；其中 {preview.RetryableFailureCount} 条失败项仍可重试，" +
                  "清空后将无法再重试。仅删除本地记录，不影响百度网盘文件和分享链接。"
                : "仅删除本地记录，不影响百度网盘文件和分享链接。");
        }
    }

    public void NotifyAvailabilityChanged()
    {
        OnPropertyChanged(nameof(CanClear));
        OnPropertyChanged(nameof(ClearBlockedReason));
        ClearCommand.NotifyCanExecuteChanged();
    }

    public Task RefreshPreviewAsync(CancellationToken cancellationToken = default)
    {
        if (closed || cancellationToken.IsCancellationRequested) return Task.CompletedTask;
        if (!busy) return StartOrJoinPreviewAsync(cancellationToken);
        // Do not scan during confirmation/deletion, but preserve terminal events for the final scan.
        deferredPreviewRefresh = true;
        return Task.CompletedTask;
    }

    private Task StartOrJoinPreviewAsync(CancellationToken cancellationToken, bool reportFailure = true)
    {
        if (closed || service is null || cancellationToken.IsCancellationRequested) return Task.CompletedTask;
        if (previewRefresh is { IsCompleted: false })
        {
            // Coalesce all intervening events into one fresh scan after the current scan.
            ++previewGeneration;
            return JoinPreviewAsync(previewRefresh, cancellationToken);
        }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        previewRefresh = completion.Task;
        var generation = ++previewGeneration;
        preview = null;
        OnPropertyChanged(nameof(Preview));
        OnPropertyChanged(nameof(ClearBlockedReason));
        NotifyAvailabilityChanged();
        _ = RunPreviewAsync(completion, generation, reportFailure);
        return JoinPreviewAsync(completion.Task, cancellationToken);
    }

    private static async Task JoinPreviewAsync(Task task, CancellationToken cancellationToken)
    {
        try { await task.WaitAsync(cancellationToken).ConfigureAwait(true); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task RunPreviewAsync(TaskCompletionSource completion, long generation, bool reportFailure)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        try
        {
            do
            {
                generation = previewGeneration;
                try
                {
                    var value = await backgroundRunner.RunAsync(token => service!.PreviewAsync(kind, token), operation.Token).ConfigureAwait(true);
                    if (closed || operation.IsCancellationRequested) return;
                    var unfinished = await CountUnfinishedAsync(operation.Token).ConfigureAwait(true);
                    if (closed || operation.IsCancellationRequested) return;
                    if (generation == previewGeneration)
                    {
                        preview = value;
                        unfinishedBatchCount = unfinished;
                    }
                }
                catch (OperationCanceledException) when (operation.IsCancellationRequested) { return; }
                catch (Exception)
                {
                    if (reportFailure && !closed && generation == previewGeneration)
                        report("暂时无法读取可清空的历史，请稍后重试。");
                }
            }
            while (!closed && generation != previewGeneration);
        }
        finally
        {
            if (!closed && generation == previewGeneration)
            {
                OnPropertyChanged(nameof(Preview));
        OnPropertyChanged(nameof(ClearBlockedReason));
                NotifyAvailabilityChanged();
            }
            completion.TrySetResult();
            ScheduleConflictRetry();
        }
    }

    private async Task<int> CountUnfinishedAsync(CancellationToken cancellationToken)
    {
        if (unfinishedBatches is null) return 0;
        try
        {
            return await backgroundRunner.RunAsync(token => unfinishedBatches.CountUnfinishedAsync(token), cancellationToken)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // 数不出来就按没有处理：清空仍按原来的方式只清已结束的。
            return 0;
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        if (closed || busy || !ownerAvailable() || service is null || interaction is null) return;
        busy = true;
        OnPropertyChanged(nameof(IsBusy));
        NotifyAvailabilityChanged();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        var deletedCount = 0;
        // 结束未结束批次时，有的批次还有正在提交的记录，暂时清不掉：在最后的提示里说明。
        var stillRunningNote = string.Empty;
        try
        {
            if (previewRefresh is { IsCompleted: false })
                await JoinPreviewAsync(previewRefresh, operation.Token).ConfigureAwait(true);
            await StartOrJoinPreviewAsync(operation.Token).ConfigureAwait(true);
            if (closed || operation.IsCancellationRequested || preview is not { } confirmedPreview) return;
            if (confirmedPreview.HasConflicts)
            {
                report("仍有任务在处理，暂时无法清空历史，请稍后重试。");
                return;
            }
            if (unfinishedBatches is not null && unfinishedBatchCount > 0)
            {
                var choice = await interaction.ChooseUnfinishedAsync(
                    unfinishedBatchCount, confirmedPreview.TerminalBatchCount, operation.Token).ConfigureAwait(true);
                if (closed || operation.IsCancellationRequested) return;
                if (choice == UnfinishedBatchChoice.Cancel)
                {
                    report("已取消清空历史。");
                    return;
                }
                if (choice == UnfinishedBatchChoice.EndThenClear)
                {
                    if (!ownerAvailable())
                    {
                        report("仍有任务在处理，暂时无法清空历史，请稍后重试。");
                        return;
                    }
                    var ended = await backgroundRunner.RunAsync(
                        token => unfinishedBatches.EndAllUnfinishedAsync(token), operation.Token).ConfigureAwait(true);
                    if (closed || operation.IsCancellationRequested) return;
                    if (ended.StillRunning > 0)
                        stillRunningNote = $"另有 {ended.StillRunning} 个批次还有正在提交的记录，已停止领取新的操作，" +
                            "等它们完成后再点一次「清空转存历史」即可。";
                    await reload(operation.Token).ConfigureAwait(true);
                    if (closed || operation.IsCancellationRequested) return;
                    // 刚结束的批次现在也算已结束，重新取一次要确认的内容。
                    await StartOrJoinPreviewAsync(operation.Token).ConfigureAwait(true);
                    if (closed || operation.IsCancellationRequested || preview is not { } refreshed) return;
                    confirmedPreview = refreshed;
                    if (confirmedPreview.HasConflicts)
                    {
                        report("批次已停止，但仍有任务在收尾，暂时无法清空历史，请稍后重试。" + stillRunningNote);
                        return;
                    }
                }
            }
            if (confirmedPreview.TerminalBatchCount == 0)
            {
                report(stillRunningNote.Length > 0 ? stillRunningNote : "没有可清空的已结束历史。");
                return;
            }
            var confirmed = await interaction.ConfirmAsync(confirmedPreview, operation.Token).ConfigureAwait(true);
            if (closed || operation.IsCancellationRequested) return;
            if (!confirmed)
            {
                report("已取消清空历史。");
                return;
            }
            if (!ownerAvailable())
            {
                report("仍有任务在处理，暂时无法清空历史，请稍后重试。");
                return;
            }
            // This exact object is what the user confirmed. Never replace its token after confirmation.
            var result = await backgroundRunner.RunAsync(token => service.DeleteAsync(confirmedPreview, token), operation.Token).ConfigureAwait(true);
            if (closed || operation.IsCancellationRequested) return;
            if (result.Disposition == LocalHistoryCleanupDisposition.Conflict)
                report("历史记录已变化或仍有任务在处理，未清空任何历史，请稍后重试。");
            else if (result.Disposition == LocalHistoryCleanupDisposition.NothingToDelete)
                report("没有可清空的已结束历史。");
            else
            {
                deletedCount = result.DeletedBatchCount;
                await reload(operation.Token).ConfigureAwait(true);
                if (closed || operation.IsCancellationRequested) return;
                // 按钮写着"清空历史"，清完却还剩记录，不说明就像是没清干净。
                // 未结束的批次（进行中/已暂停/排队中）是有意保留的——删了就没法再恢复。
                report($"已清空 {result.DeletedBatchCount} 个已结束的{(kind == LocalHistoryKind.Transfer ? "转存" : "分享")}批次历史。" +
                    (stillRunningNote.Length > 0
                        ? stillRunningNote
                        : unfinishedBatchCount > 0 || unfinishedBatches is null
                            ? "未结束的批次（进行中、已暂停、排队中）予以保留，结束后可再清空。"
                            : string.Empty) +
                    "仅删除本地记录，不影响百度网盘文件和分享链接。");
            }
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!closed) report(deletedCount > 0
                ? $"已清空 {deletedCount} 个批次。本地历史已删除，但界面刷新失败，请稍后重试。"
                : "清空历史未能完成，请稍后重试。");
        }
        finally
        {
            if (!closed)
            {
                do
                {
                    deferredPreviewRefresh = false;
                    // Keep the deletion outcome visible even when this follow-up preview fails.
                    await StartOrJoinPreviewAsync(operation.Token, reportFailure: false).ConfigureAwait(true);
                }
                while (!closed && !operation.IsCancellationRequested && deferredPreviewRefresh);
                busy = false;
                OnPropertyChanged(nameof(IsBusy));
                NotifyAvailabilityChanged();
                ScheduleConflictRetry();
            }
        }
    }

    private void ScheduleConflictRetry()
    {
        if (closed || busy || preview is not { HasConflicts: true } || conflictRetry is not null) return;
        var retry = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        conflictRetry = retry;
        _ = RetryConflictAsync(retry);
    }

    private async Task RetryConflictAsync(CancellationTokenSource retry)
    {
        try
        {
            while (!closed && !retry.IsCancellationRequested && preview is { HasConflicts: true })
            {
                await conflictRetryDelay.DelayAsync(TimeSpan.FromSeconds(5), retry.Token).ConfigureAwait(true);
                if (closed || retry.IsCancellationRequested) return;
                if (busy || !ownerAvailable()) continue;
                await RefreshPreviewAsync(retry.Token).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) when (retry.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!closed) report("暂时无法重查历史状态，请稍后刷新重试。");
        }
        finally
        {
            if (ReferenceEquals(conflictRetry, retry)) conflictRetry = null;
            retry.Dispose();
        }
    }

    public void Close()
    {
        if (closed) return;
        closed = true;
        ++previewGeneration;
        preview = null;
        lifetime.Cancel();
        NotifyAvailabilityChanged();
    }

    public void Dispose()
    {
        Close();
        lifetime.Dispose();
    }
}
