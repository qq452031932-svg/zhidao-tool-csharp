using System.Collections.ObjectModel;
using System.ComponentModel;
using BaiduBatchTransfer.App.Features.Jobs;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Application.Scheduling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SuperPan.Licensing.Client;

namespace BaiduBatchTransfer.App.Features.Common;

public sealed record WorkspaceBatchItem(
    Guid BatchJobId,
    string DisplayText,
    string BatchStatus = "Queued",
    DateTimeOffset? CreatedAt = null)
{
    public override string ToString() => DisplayText;
}

public sealed record WorkspaceBatchTerminalEvent(
    Guid BatchJobId,
    string BatchStatus,
    long? CompleteSuccessCount,
    long? TerminalProblemCount,
    long? CancelledCount = null);

public sealed class WorkspaceRunPanelViewModel : ObservableObject, IDisposable
{
    private readonly PauseBatchService pauseService;
    private readonly ResumeBatchService resumeService;
    private readonly RetryFailedTransferService retryService;
    private readonly IWorkspaceBatchHistorySource historySource;
    private readonly int maximumRecentBatches;
    private readonly IAsyncDelay automaticRefreshDelay;
    private readonly TimeSpan automaticRefreshInterval;
    private readonly ILicenseAuthorizationGate? licenseGate;
    private readonly EndBatchService? endService;
    private readonly IDialogService? dialogService;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? automaticRefresh;
    private CancellationTokenSource? endWatch;
    private CancellationTokenSource historyLifetime = new();
    private CancellationTokenSource? currentOperation;
    private WorkspaceBatchItem? selectedBatch;
    private string statusMessage = "尚未开始转存。";
    private bool isBusy;
    private bool isHistoryCleanupBusy;
    private bool isClosed;
    private readonly HashSet<Guid> notifiedTerminalBatches = [];
    private StuckBatchSnapshot? selectedStuck;
    private string? lastStuckHintKey;

    public WorkspaceRunPanelViewModel(
        JobResultsViewModel results,
        PauseBatchService pauseService,
        ResumeBatchService resumeService,
        RetryFailedTransferService retryService,
        int maximumRecentBatches = 20,
        IWorkspaceBatchHistorySource? historySource = null,
        IAsyncDelay? automaticRefreshDelay = null,
        TimeSpan? automaticRefreshInterval = null,
        ILicenseAuthorizationGate? licenseGate = null,
        EndBatchService? endService = null,
        IDialogService? dialogService = null)
    {
        if (maximumRecentBatches is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(maximumRecentBatches));
        Results = results ?? throw new ArgumentNullException(nameof(results));
        this.pauseService = pauseService ?? throw new ArgumentNullException(nameof(pauseService));
        this.resumeService = resumeService ?? throw new ArgumentNullException(nameof(resumeService));
        this.retryService = retryService ?? throw new ArgumentNullException(nameof(retryService));
        this.historySource = historySource ?? EmptyWorkspaceBatchHistorySource.Instance;
        this.maximumRecentBatches = maximumRecentBatches;
        this.automaticRefreshDelay = automaticRefreshDelay ?? new TimeProviderDelay(TimeProvider.System);
        this.automaticRefreshInterval = automaticRefreshInterval ?? TimeSpan.FromSeconds(5);
        this.licenseGate = licenseGate;
        this.endService = endService;
        this.dialogService = dialogService;
        if (this.automaticRefreshInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(automaticRefreshInterval));
        Batches = [];
        LoadSelectedBatchCommand = new AsyncRelayCommand(LoadSelectedBatchAsync, () => CanLoadSelectedBatch);
        PauseCommand = new AsyncRelayCommand(PauseAsync, () => CanPause);
        ResumeCommand = new AsyncRelayCommand(ResumeAsync, () => CanResume);
        PauseResumeCommand = new AsyncRelayCommand(
            () => CanResume ? ResumeAsync() : PauseAsync(), () => CanPause || CanResume);
        EndBatchCommand = new AsyncRelayCommand(EndBatchAsync, () => CanEndBatch);
        AbandonBatchCommand = new AsyncRelayCommand(AbandonBatchAsync, () => CanAbandonBatch);
        RetryFailedTransferCommand = new AsyncRelayCommand(
            RetryFailedTransferAsync,
            () => CanRetryFailedTransfer);
        Results.PropertyChanged += OnResultsPropertyChanged;
    }

    public JobResultsViewModel Results { get; }
    public event EventHandler<WorkspaceBatchTerminalEvent>? BatchTerminal;
    public event EventHandler? ResultsRequested;
    public ObservableCollection<WorkspaceBatchItem> Batches { get; }
    public WorkspaceBatchItem? SelectedBatch
    {
        get => selectedBatch;
        set
        {
            if (!SetProperty(ref selectedBatch, value)) return;
            if (selectedStuck is { } stuck && stuck.BatchJobId != value?.BatchJobId) selectedStuck = null;
            NotifyCommandState();
            RestartAutomaticRefresh();
        }
    }
    public string StatusMessage { get => statusMessage; private set => SetProperty(ref statusMessage, value); }
    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (!SetProperty(ref isBusy, value)) return;
            NotifyCommandState();
        }
    }
    public bool IsClosed => isClosed;
    public bool IsHistoryCleanupBusy
    {
        get => isHistoryCleanupBusy;
        set
        {
            if (!SetProperty(ref isHistoryCleanupBusy, value)) return;
            Results.IsHistoryCleanupBusy = value;
            NotifyCommandState();
        }
    }
    public bool CanUseBatchActions => !isClosed && !IsBusy && !IsHistoryCleanupBusy && SelectedBatch is not null;
    public bool CanLoadSelectedBatch => CanUseBatchActions && !Results.IsBusy;
    public bool CanPause => CanUseBatchActions && SelectedBatch!.BatchStatus is "Queued" or "Active";
    public bool CanResume => CanUseBatchActions && SelectedBatch!.BatchStatus == "Paused";
    public bool CanRetryFailedTransfer => CanUseBatchActions &&
        Results.SelectedItem is { CanSafelyRetryTransfer: true };
    /// <summary>与采集页一致：一个按钮，运行时显示「暂停」，暂停后显示「继续」。</summary>
    public string PauseResumeText => CanResume ? "继续" : "暂停";
    public bool CanEndBatch => endService is not null && dialogService is not null && CanUseBatchActions &&
        !IsTerminal(SelectedBatch!.BatchStatus);
    /// <summary>已发出结束、正在等正在提交的几条收尾。</summary>
    public bool IsEndingBatch => endWatch is not null;
    /// <summary>
    /// 所选批次剩下的活走不下去（账号失效、结果核对不了），或者点了「停止」还在等收尾：
    /// 这时才显示「放弃收尾」，平时不出现，免得和「停止」混淆。
    /// </summary>
    public bool ShowAbandonBatch => endService is not null && dialogService is not null && SelectedBatch is { } batch &&
        !IsTerminal(batch.BatchStatus) &&
        (IsEndingBatch || selectedStuck is { IsBlocked: true } stuck && stuck.BatchJobId == batch.BatchJobId);
    public bool CanAbandonBatch => ShowAbandonBatch && CanUseBatchActions;
    public IAsyncRelayCommand AbandonBatchCommand { get; }
    public IAsyncRelayCommand LoadSelectedBatchCommand { get; }
    public IAsyncRelayCommand PauseCommand { get; }
    public IAsyncRelayCommand ResumeCommand { get; }
    public IAsyncRelayCommand RetryFailedTransferCommand { get; }
    public IAsyncRelayCommand PauseResumeCommand { get; }
    public IAsyncRelayCommand EndBatchCommand { get; }

    public Task ReloadHistoryAsync(CancellationToken cancellationToken = default)
    {
        if (isClosed) return Task.CompletedTask;
        automaticRefresh?.Cancel();
        historyLifetime.Cancel();
        historyLifetime.Dispose();
        historyLifetime = new();
        IsBusy = false;
        return InitializeCoreAsync(cancellationToken, propagateFailure: true);
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => InitializeCoreAsync(cancellationToken, propagateFailure: false);

    private async Task InitializeCoreAsync(CancellationToken cancellationToken, bool propagateFailure)
    {
        if (isClosed || IsBusy || (IsHistoryCleanupBusy && !propagateFailure)) return;
        var operation = BeginOperation(cancellationToken);
        if (operation is null) return;
        IsBusy = true;
        try
        {
            var recent = await historySource.LoadRecentAsync(maximumRecentBatches, operation.Token)
                .ConfigureAwait(true);
            if (!IsCurrent(operation)) return;
            if (propagateFailure) Results.Reset();
            Batches.Clear();
            foreach (var item in recent.Take(maximumRecentBatches)) Batches.Add(item);
            SelectedBatch = Batches.FirstOrDefault();
            if (SelectedBatch is { } selected)
            {
                if (propagateFailure)
                    await Results.InitializeForHandoffAsync(selected.BatchJobId, operation.Token).ConfigureAwait(true);
                else
                    await Results.InitializeAsync(selected.BatchJobId, operation.Token).ConfigureAwait(true);
                if (IsCurrent(operation)) StatusMessage = "已加载最近批次。";
            }
            else
            {
                Results.Reset();
                StatusMessage = "尚未开始转存。";
            }
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
        catch (Exception)
        {
            if (IsCurrent(operation)) StatusMessage = "无法加载最近批次，请稍后重试。";
            if (propagateFailure) throw;
        }
        finally
        {
            if (!isClosed && ReferenceEquals(currentOperation, operation)) IsBusy = false;
            operation.Dispose();
        }
    }

    public void PresentBatch(Guid batchJobId, string? displayText = null, string batchStatus = "Queued")
    {
        if (batchJobId == Guid.Empty) throw new ArgumentException("A batch is required.", nameof(batchJobId));
        if (isClosed) return;
        var existing = Batches.FirstOrDefault(item => item.BatchJobId == batchJobId);
        if (existing is null)
        {
            existing = new(batchJobId, string.IsNullOrWhiteSpace(displayText)
                ? $"批次 {batchJobId.ToString("N")[..8]}"
                : displayText.Trim(), batchStatus);
            Batches.Insert(0, existing);
            while (Batches.Count > maximumRecentBatches) Batches.RemoveAt(Batches.Count - 1);
        }
        SelectedBatch = existing;
        StatusMessage = "已显示所选批次的进度与结果。";
    }

    public async Task ActivateBatchAsync(
        Guid batchJobId,
        CancellationToken cancellationToken = default)
    {
        if (batchJobId == Guid.Empty) throw new ArgumentException("A batch is required.", nameof(batchJobId));
        if (isClosed) return;
        PresentBatch(batchJobId);
        await LoadBatchAsync(batchJobId, cancellationToken).ConfigureAwait(true);
    }

    public Task LoadSelectedBatchAsync(CancellationToken cancellationToken = default) =>
        CanLoadSelectedBatch && SelectedBatch is { } selected
            ? LoadBatchAsync(selected.BatchJobId, cancellationToken)
            : Task.CompletedTask;

    public Task PauseAsync(CancellationToken cancellationToken = default)
    {
        if (!CanPause) return Task.CompletedTask;
        // 结束后正在等收尾时用户又点了暂停：听用户的，不再自动结束（否则下一次结束会把它重新排队）。
        StopEndWatch();
        return RunBatchActionAsync(
            (batch, token) => pauseService.PauseAsync(batch, token),
            MapPause,
            value => value is PauseBatchDisposition.Paused or PauseBatchDisposition.AlreadyPaused ? "Paused" : null,
            cancellationToken);
    }

    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        if (!CanResume || !await IsRemoteOperationAuthorizedAsync(cancellationToken).ConfigureAwait(true)) return;
        // 用户改主意继续跑了：不再等着把它结束。
        StopEndWatch();
        var resumed = SelectedBatch?.BatchJobId;
        await RunBatchActionAsync(
            (batch, token) => resumeService.ResumeAsync(batch, token),
            MapResume,
            value => value is ResumeBatchDisposition.Queued or ResumeBatchDisposition.AlreadyResumed ? "Queued" : null,
            cancellationToken).ConfigureAwait(true);
        // 同一时间只跑一个批次。前面还有没跑完的批次时，点了「继续」也要排队，看起来就像没反应。
        if (resumed is { } id && SelectedBatch?.BatchJobId == id && SelectedBatch.BatchStatus == "Queued" &&
            Batches.FirstOrDefault(item => item.BatchJobId != id && item.BatchStatus == "Active") is { } running)
            StatusMessage = $"批次已恢复，正在排队：{running.DisplayText} 还没跑完，同一时间只运行一个批次，" +
                "它跑完后这一批会自动开始。不想等它可以先在列表里选中它，点「停止」。";
    }

    public async Task RetryFailedTransferAsync(CancellationToken cancellationToken = default)
    {
        if (!CanRetryFailedTransfer || Results.SelectedItem is not { } row) return;
        if (!await IsRemoteOperationAuthorizedAsync(cancellationToken).ConfigureAwait(true)) return;
        var operation = BeginOperation(cancellationToken);
        if (operation is null) return;
        IsBusy = true;
        try
        {
            var disposition = await retryService.RetryAsync(row.TransferOperationId, operation.Token)
                .ConfigureAwait(true);
            if (!IsCurrent(operation)) return;
            StatusMessage = disposition switch
            {
                RetryFailedTransferDisposition.Requeued => "失败的转存已安全重新排队。",
                RetryFailedTransferDisposition.NeedsReconciliation => "该操作需要先确认远端结果，不能直接重试。",
                RetryFailedTransferDisposition.NotFound => "未找到这条转存记录，请刷新结果。",
                _ => "当前记录不满足安全重试条件。",
            };
            if (disposition == RetryFailedTransferDisposition.Requeued)
                await Results.RefreshAsync(operation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
        catch (Exception)
        {
            if (IsCurrent(operation)) StatusMessage = "重试失败，请稍后再试。";
        }
        finally
        {
            if (!isClosed && ReferenceEquals(currentOperation, operation)) IsBusy = false;
            operation.Dispose();
        }
    }

    /// <summary>
    /// 结束所选批次。还没开始的标为已取消，已转存成功的不受影响；正在提交的不能中途打断，
    /// 等它们收尾后自动结束。结束后批次不能再继续，但可以用「清空转存历史」删掉。
    /// </summary>
    public async Task EndBatchAsync(CancellationToken cancellationToken = default)
    {
        if (!CanEndBatch || SelectedBatch is not { } selected) return;
        bool confirmed;
        try
        {
            confirmed = await dialogService!.ConfirmAsync(
                "停止转存",
                "确定停止这一批转存吗？\n\n" +
                "· 还没开始的记录会标为「已取消」，停止后不能再继续这一批；\n" +
                "· 已经转存成功的不受影响，网盘里的文件不会被删除；\n" +
                "· 正在提交的那几条不能中途打断，会等它们完成后再停下。\n\n" +
                "停止后可以用「清空转存历史」删除这批记录。只想暂时停一下，请用「暂停」，暂停后关掉软件下次也能继续。",
                cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        if (!confirmed || !CanEndBatch || SelectedBatch?.BatchJobId != selected.BatchJobId) return;

        var operation = BeginOperation(cancellationToken);
        if (operation is null) return;
        IsBusy = true;
        try
        {
            var result = await endService!.EndAsync(selected.BatchJobId, operation.Token).ConfigureAwait(true);
            if (!IsCurrent(operation)) return;
            await ApplyEndResultAsync(selected.BatchJobId, result, operation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
        catch (Exception)
        {
            if (IsCurrent(operation)) StatusMessage = "停止批次失败，请稍后重试。";
        }
        finally
        {
            if (!isClosed && ReferenceEquals(currentOperation, operation)) IsBusy = false;
            operation.Dispose();
        }
    }

    /// <summary>
    /// 放弃收尾：不等账号重新登录、不再核对，剩下的按「结果未确认」记为失败，这一批就此结束，
    /// 后面排队的批次马上开始。正在提交的那几条仍然不打断。
    /// </summary>
    public async Task AbandonBatchAsync(CancellationToken cancellationToken = default)
    {
        if (!CanAbandonBatch || SelectedBatch is not { } selected) return;
        var stuck = selectedStuck is { } known && known.BatchJobId == selected.BatchJobId ? known : null;
        var waitingFor = stuck is { UnavailableAccountNames.Count: > 0 }
            ? $"它们在等账号{StuckBatchText.AccountList(stuck.UnavailableAccountNames)}重新登录后核对结果。"
            : "它们的结果还没核对清楚。";
        bool confirmed;
        try
        {
            confirmed = await dialogService!.ConfirmAsync(
                "放弃收尾",
                $"这一批还有{(stuck is null ? "" : $" {stuck.UnfinishedCount:N0} 条")}记录没收尾，{waitingFor}\n\n" +
                "放弃后：\n" +
                "· 还没开始的标为「已取消」；\n" +
                "· 已经提交过的不再核对，记为失败（结果未确认）。它们可能其实已经转存或分享成功，请到网盘核对；\n" +
                "· 正在提交的那几条仍会做完；\n" +
                "· 这一批随即结束，后面排队的批次马上开始。\n\n" +
                "如果能重新登录账号，建议先在「账号管理」里点「重新登录」，结果能核对清楚。确定放弃收尾吗？",
                cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        if (!confirmed || !CanAbandonBatch || SelectedBatch?.BatchJobId != selected.BatchJobId) return;

        var operation = BeginOperation(cancellationToken);
        if (operation is null) return;
        IsBusy = true;
        try
        {
            var result = await endService!.AbandonAsync(selected.BatchJobId, operation.Token).ConfigureAwait(true);
            if (!IsCurrent(operation)) return;
            selectedStuck = null;
            lastStuckHintKey = null;
            switch (result.Disposition)
            {
                case AbandonBatchDisposition.Ended:
                case AbandonBatchDisposition.AlreadyEnded:
                    await ApplyEndResultAsync(selected.BatchJobId,
                        new(result.Disposition == AbandonBatchDisposition.Ended ? EndBatchDisposition.Ended : EndBatchDisposition.AlreadyEnded,
                            result.CancelledCount, 0),
                        operation.Token,
                        result.Disposition == AbandonBatchDisposition.Ended
                            ? $"已放弃收尾，这一批已结束：{StuckBatchText.Outcome(result)}。需要时可用「清空转存历史」删除这批记录。"
                            : null).ConfigureAwait(true);
                    break;
                case AbandonBatchDisposition.Stopping:
                    StatusMessage = $"已放弃收尾：{StuckBatchText.Outcome(result)}。";
                    if (Results.BatchJobId == selected.BatchJobId) await Results.RefreshAsync(operation.Token).ConfigureAwait(true);
                    StartEndWatch(selected.BatchJobId);
                    break;
                default:
                    StatusMessage = "未找到所选批次，请刷新后重试。";
                    break;
            }
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
        catch (Exception)
        {
            if (IsCurrent(operation)) StatusMessage = "放弃收尾失败，请稍后重试。";
        }
        finally
        {
            if (!isClosed && ReferenceEquals(currentOperation, operation)) IsBusy = false;
            operation.Dispose();
            NotifyCommandState();
        }
    }

    /// <summary>
    /// 自动刷新时看一眼卡没卡住：所选批次卡住了，说明在等什么、怎么办；所选批次在排队而前面那批卡住了，
    /// 告诉用户是被谁挡住的。同样的情况只说一次，免得刷屏。
    /// </summary>
    private async Task RefreshStuckHintAsync(WorkspaceBatchItem latest, CancellationToken cancellationToken)
    {
        if (endService is null) return;
        StuckBatchSnapshot? snapshot;
        try
        {
            snapshot = latest.BatchStatus switch
            {
                "Active" => await endService.GetStuckSnapshotAsync(latest.BatchJobId, cancellationToken).ConfigureAwait(true),
                "Queued" => await endService.GetActiveStuckSnapshotAsync(cancellationToken).ConfigureAwait(true),
                _ => null,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // 只是辅助提示，查不了就不提示，不影响进度刷新。
            return;
        }
        if (isClosed || cancellationToken.IsCancellationRequested || SelectedBatch?.BatchJobId != latest.BatchJobId) return;
        var own = snapshot is not null && snapshot.BatchJobId == latest.BatchJobId ? snapshot : null;
        var changed = !Equals(selectedStuck?.IsBlocked, own?.IsBlocked);
        selectedStuck = own;
        if (changed) NotifyCommandState();

        if (snapshot is not { IsBlocked: true })
        {
            lastStuckHintKey = null;
            return;
        }
        var key = $"{latest.BatchJobId:N}|{snapshot.BatchJobId:N}|{snapshot.UnfinishedCount}";
        if (key == lastStuckHintKey) return;
        lastStuckHintKey = key;
        var limit = snapshot.WasStopped
            ? StuckBatchWatchdogOptions.Default.StoppedBatchLimit
            : StuckBatchWatchdogOptions.Default.RunningBatchLimit;
        StatusMessage = own is not null
            ? StuckBatchText.BlockedHint(snapshot, limit)
            : "这一批在排队：前面正在运行的批次卡住了（" +
              (snapshot.UnavailableAccountNames.Count > 0
                  ? $"在等账号{StuckBatchText.AccountList(snapshot.UnavailableAccountNames)}重新登录"
                  : "结果一直核对不了") +
              $"），同一时间只运行一个批次。可以在列表里选中前面那一批，重新登录账号或点「放弃收尾」；" +
              $"一直没有进展的，约 {limit.TotalMinutes:0} 分钟后会自动放弃收尾，这一批随后开始。";
    }

    private async Task ApplyEndResultAsync(Guid batchJobId, EndBatchResult result, CancellationToken cancellationToken,
        string? endedMessage = null)
    {
        switch (result.Disposition)
        {
            case EndBatchDisposition.Ended:
            case EndBatchDisposition.AlreadyEnded:
                StatusMessage = endedMessage ?? (result.Disposition == EndBatchDisposition.AlreadyEnded
                    ? "这一批已经结束了。"
                    : result.CancelledCount > 0
                        ? $"批次已停止：{result.CancelledCount:N0} 条未开始的记录已取消。需要时可用「清空转存历史」删除这批记录。"
                        : "批次已停止。需要时可用「清空转存历史」删除这批记录。");
                if (SelectedBatch?.BatchJobId == batchJobId) UpdateSelectedBatchStatus("Cancelled");
                var statisticsRead = false;
                if (Results.BatchJobId == batchJobId)
                {
                    await Results.RefreshAsync(cancellationToken).ConfigureAwait(true);
                    statisticsRead = true;
                }
                if (notifiedTerminalBatches.Add(batchJobId))
                    BatchTerminal?.Invoke(this, new(batchJobId, "Cancelled",
                        statisticsRead ? Results.CompleteSuccessCount : null,
                        statisticsRead ? Results.TerminalProblemCount : null,
                        statisticsRead ? Results.CancelledCount : null));
                // 放在最后：自动结束时传进来的令牌就是它，先停会让上面的刷新被取消。
                StopEndWatch();
                break;
            case EndBatchDisposition.Stopping:
                StatusMessage = $"未开始的 {result.CancelledCount:N0} 条记录已取消，不会再开始新的转存；" +
                    $"还有 {result.InFlightCount:N0} 条已经提交、正在收尾或等待核对结果，不能中途打断，完成后这一批自动停止。" +
                    "分享结果核对不到的，约 2 分钟后按未创建处理；账号登录失效等原因一直收不了尾时，可以点「放弃收尾」。";
                // 批次照常运行（只剩收尾），让自动刷新继续显示进度。
                if (SelectedBatch?.BatchJobId == batchJobId) UpdateSelectedBatchStatus("Active");
                if (Results.BatchJobId == batchJobId) await Results.RefreshAsync(cancellationToken).ConfigureAwait(true);
                StartEndWatch(batchJobId);
                break;
            default:
                StopEndWatch();
                StatusMessage = "未找到所选批次，请刷新后重试。";
                break;
        }
    }

    // 正在提交的几条收尾之前，隔一会儿再结束一次；一旦结束（或用户点了「继续」）就停。
    private void StartEndWatch(Guid batchJobId)
    {
        if (endWatch is not null || isClosed) return;
        var watch = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        endWatch = watch;
        OnPropertyChanged(nameof(IsEndingBatch));
        NotifyCommandState();
        _ = RunEndWatchAsync(batchJobId, watch);
    }

    private void StopEndWatch()
    {
        if (endWatch is not { } watch) return;
        endWatch = null;
        watch.Cancel();
        OnPropertyChanged(nameof(IsEndingBatch));
        NotifyCommandState();
    }

    private async Task RunEndWatchAsync(Guid batchJobId, CancellationTokenSource watch)
    {
        try
        {
            while (!watch.IsCancellationRequested)
            {
                await automaticRefreshDelay.DelayAsync(automaticRefreshInterval, watch.Token).ConfigureAwait(true);
                if (isClosed || watch.IsCancellationRequested) return;
                if (IsBusy || IsHistoryCleanupBusy) continue;
                try
                {
                    var result = await endService!.EndAsync(batchJobId, watch.Token).ConfigureAwait(true);
                    if (isClosed || watch.IsCancellationRequested) return;
                    if (result.Disposition == EndBatchDisposition.Stopping) continue;
                    await ApplyEndResultAsync(batchJobId, result, watch.Token).ConfigureAwait(true);
                    return;
                }
                catch (OperationCanceledException) when (watch.IsCancellationRequested) { return; }
                catch (Exception)
                {
                    if (!isClosed) StatusMessage = "暂时无法停止批次，稍后将再次尝试。";
                }
            }
        }
        catch (OperationCanceledException) when (watch.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(endWatch, watch))
            {
                endWatch = null;
                OnPropertyChanged(nameof(IsEndingBatch));
                NotifyCommandState();
            }
            watch.Dispose();
        }
    }

    private async Task<bool> IsRemoteOperationAuthorizedAsync(CancellationToken cancellationToken)
    {
        if (licenseGate is null) return true;
        var result = await licenseGate
            .AuthorizeAsync(LicenseOperationKind.Transfer, cancellationToken)
            .ConfigureAwait(true);
        if (result.IsAuthorized) return true;
        StatusMessage = "软件尚未激活，操作未开始。请在「设置中心 → 软件授权」中激活后重试。";
        return false;
    }

    public void Close()
    {
        if (isClosed) return;
        isClosed = true;
        automaticRefresh?.Cancel();
        endWatch?.Cancel();
        lifetime.Cancel();
        OnPropertyChanged(nameof(IsClosed));
        NotifyCommandState();
    }

    public void Dispose()
    {
        Close();
        Results.PropertyChanged -= OnResultsPropertyChanged;
        historyLifetime.Dispose();
        lifetime.Dispose();
    }

    private async Task LoadBatchAsync(Guid batchJobId, CancellationToken cancellationToken)
    {
        if (isClosed || IsBusy || IsHistoryCleanupBusy || Results.IsBusy) return;
        var operation = BeginOperation(cancellationToken);
        if (operation is null) return;
        IsBusy = true;
        try
        {
            ResultsRequested?.Invoke(this, EventArgs.Empty);
            await Results.InitializeAsync(batchJobId, operation.Token).ConfigureAwait(true);
            if (IsCurrent(operation)) StatusMessage = "已加载所选批次。";
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
        catch (Exception)
        {
            if (IsCurrent(operation)) StatusMessage = "无法加载所选批次，请稍后重试。";
        }
        finally
        {
            if (!isClosed && ReferenceEquals(currentOperation, operation)) IsBusy = false;
            operation.Dispose();
        }
    }

    private async Task RunBatchActionAsync<TDisposition>(
        Func<Guid, CancellationToken, Task<TDisposition>> action,
        Func<TDisposition, string> mapStatus,
        Func<TDisposition, string?> mapBatchStatus,
        CancellationToken cancellationToken)
    {
        if (!CanUseBatchActions || SelectedBatch is not { } selected) return;
        var operation = BeginOperation(cancellationToken);
        if (operation is null) return;
        IsBusy = true;
        try
        {
            var disposition = await action(selected.BatchJobId, operation.Token).ConfigureAwait(true);
            if (!IsCurrent(operation)) return;
            StatusMessage = mapStatus(disposition);
            if (mapBatchStatus(disposition) is { } status) UpdateSelectedBatchStatus(status);
            await Results.RefreshAsync(operation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
        catch (Exception)
        {
            if (IsCurrent(operation)) StatusMessage = "批次操作失败，请稍后重试。";
        }
        finally
        {
            if (!isClosed && ReferenceEquals(currentOperation, operation)) IsBusy = false;
            operation.Dispose();
        }
    }

    private CancellationTokenSource? BeginOperation(CancellationToken cancellationToken)
    {
        if (isClosed) return null;
        currentOperation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, historyLifetime.Token, cancellationToken);
        return currentOperation;
    }

    private bool IsCurrent(CancellationTokenSource operation) =>
        !isClosed && !operation.IsCancellationRequested && !lifetime.IsCancellationRequested;

    private void OnResultsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(JobResultsViewModel.SelectedItem) or nameof(JobResultsViewModel.IsBusy))
            NotifyCommandState();
    }

    private void NotifyCommandState()
    {
        OnPropertyChanged(nameof(CanUseBatchActions));
        OnPropertyChanged(nameof(CanLoadSelectedBatch));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(CanRetryFailedTransfer));
        OnPropertyChanged(nameof(PauseResumeText));
        OnPropertyChanged(nameof(CanEndBatch));
        OnPropertyChanged(nameof(ShowAbandonBatch));
        OnPropertyChanged(nameof(CanAbandonBatch));
        PauseResumeCommand.NotifyCanExecuteChanged();
        AbandonBatchCommand.NotifyCanExecuteChanged();
        EndBatchCommand.NotifyCanExecuteChanged();
        LoadSelectedBatchCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        ResumeCommand.NotifyCanExecuteChanged();
        RetryFailedTransferCommand.NotifyCanExecuteChanged();
    }

    private void UpdateSelectedBatchStatus(string status)
    {
        if (SelectedBatch is not { } current) return;
        var index = Batches.IndexOf(current);
        if (index < 0) return;
        var updated = current with { BatchStatus = status };
        Batches[index] = updated;
        SelectedBatch = updated;
    }

    private void RestartAutomaticRefresh()
    {
        automaticRefresh?.Cancel();
        automaticRefresh = null;
        if (isClosed || SelectedBatch is not { BatchStatus: "Queued" or "Active" } selected) return;

        var refresh = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        automaticRefresh = refresh;
        _ = RunAutomaticRefreshAsync(selected.BatchJobId, refresh);
    }

    private async Task RunAutomaticRefreshAsync(
        Guid expectedBatchJobId,
        CancellationTokenSource refresh)
    {
        try
        {
            while (!refresh.IsCancellationRequested)
            {
                await automaticRefreshDelay.DelayAsync(automaticRefreshInterval, refresh.Token)
                    .ConfigureAwait(true);
                if (isClosed || SelectedBatch?.BatchJobId != expectedBatchJobId) return;
                if (IsHistoryCleanupBusy) continue;

                try
                {
                    var recent = await historySource.LoadRecentAsync(maximumRecentBatches, refresh.Token)
                        .ConfigureAwait(true);
                    if (isClosed || refresh.IsCancellationRequested || SelectedBatch?.BatchJobId != expectedBatchJobId) return;
                    if (IsHistoryCleanupBusy) continue;
                    var latest = recent.FirstOrDefault(item => item.BatchJobId == expectedBatchJobId);
                    var shouldFinalize = latest is not null && IsTerminal(latest.BatchStatus) &&
                        SelectedBatch is { } previous && !IsTerminal(previous.BatchStatus) &&
                        !notifiedTerminalBatches.Contains(expectedBatchJobId);
                    if (latest is not null) ApplyAutomaticBatchSnapshot(latest);

                    var statisticsRead = false;
                    if (shouldFinalize)
                    {
                        statisticsRead = await Results.FinalizeForTerminalAsync(
                                expectedBatchJobId,
                                () => !isClosed && !refresh.IsCancellationRequested &&
                                    SelectedBatch?.BatchJobId == expectedBatchJobId,
                                refresh.Token)
                            .ConfigureAwait(true);
                    }
                    else if (Results.BatchJobId == expectedBatchJobId)
                        statisticsRead = await Results.RefreshForLifecycleAsync(refresh.Token).ConfigureAwait(true);
                    if (shouldFinalize && !isClosed && !refresh.IsCancellationRequested &&
                        SelectedBatch?.BatchJobId == expectedBatchJobId &&
                        Results.IsFinalizedForLifecycle(expectedBatchJobId) &&
                        notifiedTerminalBatches.Add(expectedBatchJobId))
                    {
                        BatchTerminal?.Invoke(this, new(expectedBatchJobId, latest!.BatchStatus,
                            statisticsRead ? Results.CompleteSuccessCount : null,
                            statisticsRead ? Results.TerminalProblemCount : null,
                            statisticsRead ? Results.CancelledCount : null));
                    }

                    if (latest is null || latest.BatchStatus is not ("Queued" or "Active")) return;
                    if (!IsHistoryCleanupBusy) await RefreshStuckHintAsync(latest, refresh.Token).ConfigureAwait(true);
                }
                catch (OperationCanceledException) when (refresh.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception)
                {
                    if (!isClosed && !refresh.IsCancellationRequested) StatusMessage = "自动刷新失败，稍后将再次尝试。";
                }
            }
        }
        catch (OperationCanceledException) when (refresh.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(automaticRefresh, refresh)) automaticRefresh = null;
            refresh.Dispose();
        }
    }

    private void ApplyAutomaticBatchSnapshot(WorkspaceBatchItem latest)
    {
        if (SelectedBatch is not { } current) return;
        var index = Batches.IndexOf(current);
        if (index < 0) return;
        Batches[index] = latest;
        selectedBatch = latest;
        OnPropertyChanged(nameof(SelectedBatch));
        NotifyCommandState();
    }

    private static bool IsTerminal(string status) =>
        status is "Completed" or "CompletedWithShortfall" or "Cancelled";

    private static string MapPause(PauseBatchDisposition value) => value switch
    {
        PauseBatchDisposition.Paused => "批次已暂停，不再领取新的操作。",
        PauseBatchDisposition.AlreadyPaused => "批次已经处于暂停状态。",
        PauseBatchDisposition.NotFound => "未找到所选批次。",
        _ => "当前批次状态不能暂停。",
    };

    private static string MapResume(ResumeBatchDisposition value) => value switch
    {
        ResumeBatchDisposition.Queued => "批次已恢复并重新进入队列。",
        ResumeBatchDisposition.AlreadyResumed => "批次已经在运行队列中。",
        ResumeBatchDisposition.NotFound => "未找到所选批次。",
        _ => "当前批次状态不能继续。",
    };
}
