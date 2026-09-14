using System.Collections.ObjectModel;
using System.ComponentModel;
using BaiduBatchTransfer.App.Features.Jobs;
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
    long? TerminalProblemCount);

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
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? automaticRefresh;
    private CancellationTokenSource historyLifetime = new();
    private CancellationTokenSource? currentOperation;
    private WorkspaceBatchItem? selectedBatch;
    private string statusMessage = "尚未开始转存。";
    private bool isBusy;
    private bool isHistoryCleanupBusy;
    private bool isClosed;
    private readonly HashSet<Guid> notifiedTerminalBatches = [];

    public WorkspaceRunPanelViewModel(
        JobResultsViewModel results,
        PauseBatchService pauseService,
        ResumeBatchService resumeService,
        RetryFailedTransferService retryService,
        int maximumRecentBatches = 20,
        IWorkspaceBatchHistorySource? historySource = null,
        IAsyncDelay? automaticRefreshDelay = null,
        TimeSpan? automaticRefreshInterval = null,
        ILicenseAuthorizationGate? licenseGate = null)
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
        if (this.automaticRefreshInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(automaticRefreshInterval));
        Batches = [];
        LoadSelectedBatchCommand = new AsyncRelayCommand(LoadSelectedBatchAsync, () => CanLoadSelectedBatch);
        PauseCommand = new AsyncRelayCommand(PauseAsync, () => CanPause);
        ResumeCommand = new AsyncRelayCommand(ResumeAsync, () => CanResume);
        RetryFailedTransferCommand = new AsyncRelayCommand(
            RetryFailedTransferAsync,
            () => CanRetryFailedTransfer);
        Results.PropertyChanged += OnResultsPropertyChanged;
    }

    public JobResultsViewModel Results { get; }
    public event EventHandler<WorkspaceBatchTerminalEvent>? BatchTerminal;
    public ObservableCollection<WorkspaceBatchItem> Batches { get; }
    public WorkspaceBatchItem? SelectedBatch
    {
        get => selectedBatch;
        set
        {
            if (!SetProperty(ref selectedBatch, value)) return;
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
    public IAsyncRelayCommand LoadSelectedBatchCommand { get; }
    public IAsyncRelayCommand PauseCommand { get; }
    public IAsyncRelayCommand ResumeCommand { get; }
    public IAsyncRelayCommand RetryFailedTransferCommand { get; }

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

    public Task PauseAsync(CancellationToken cancellationToken = default) =>
        !CanPause ? Task.CompletedTask :
        RunBatchActionAsync(
            (batch, token) => pauseService.PauseAsync(batch, token),
            MapPause,
            value => value is PauseBatchDisposition.Paused or PauseBatchDisposition.AlreadyPaused ? "Paused" : null,
            cancellationToken);

    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        if (!CanResume || !await IsRemoteOperationAuthorizedAsync(cancellationToken).ConfigureAwait(true)) return;
        await RunBatchActionAsync(
            (batch, token) => resumeService.ResumeAsync(batch, token),
            MapResume,
            value => value is ResumeBatchDisposition.Queued or ResumeBatchDisposition.AlreadyResumed ? "Queued" : null,
            cancellationToken).ConfigureAwait(true);
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

    private async Task<bool> IsRemoteOperationAuthorizedAsync(CancellationToken cancellationToken)
    {
        if (licenseGate is null) return true;
        var result = await licenseGate
            .AuthorizeAsync(LicenseOperationKind.Transfer, cancellationToken)
            .ConfigureAwait(true);
        if (result.IsAuthorized) return true;
        StatusMessage = "软件尚未激活，联网功能暂不可用。";
        return false;
    }

    public void Close()
    {
        if (isClosed) return;
        isClosed = true;
        automaticRefresh?.Cancel();
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
                            statisticsRead ? Results.TerminalProblemCount : null));
                    }

                    if (latest is null || latest.BatchStatus is not ("Queued" or "Active")) return;
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
