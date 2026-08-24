using System.Collections.ObjectModel;
using System.ComponentModel;
using BaiduBatchTransfer.App.Features.Jobs;
using BaiduBatchTransfer.Application.Jobs;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BaiduBatchTransfer.App.Features.Common;

public sealed record WorkspaceBatchItem(Guid BatchJobId, string DisplayText)
{
    public override string ToString() => DisplayText;
}

public sealed class WorkspaceRunPanelViewModel : ObservableObject, IDisposable
{
    private readonly PauseBatchService pauseService;
    private readonly ResumeBatchService resumeService;
    private readonly RetryFailedTransferService retryService;
    private readonly int maximumRecentBatches;
    private readonly CancellationTokenSource lifetime = new();
    private WorkspaceBatchItem? selectedBatch;
    private string statusMessage = "尚未开始转存。";
    private bool isBusy;
    private bool isClosed;

    public WorkspaceRunPanelViewModel(
        JobResultsViewModel results,
        PauseBatchService pauseService,
        ResumeBatchService resumeService,
        RetryFailedTransferService retryService,
        int maximumRecentBatches = 20)
    {
        if (maximumRecentBatches is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(maximumRecentBatches));
        Results = results ?? throw new ArgumentNullException(nameof(results));
        this.pauseService = pauseService ?? throw new ArgumentNullException(nameof(pauseService));
        this.resumeService = resumeService ?? throw new ArgumentNullException(nameof(resumeService));
        this.retryService = retryService ?? throw new ArgumentNullException(nameof(retryService));
        this.maximumRecentBatches = maximumRecentBatches;
        Batches = [];
        LoadSelectedBatchCommand = new AsyncRelayCommand(LoadSelectedBatchAsync, () => CanUseBatchActions);
        PauseCommand = new AsyncRelayCommand(PauseAsync, () => CanUseBatchActions);
        ResumeCommand = new AsyncRelayCommand(ResumeAsync, () => CanUseBatchActions);
        RetryFailedTransferCommand = new AsyncRelayCommand(
            RetryFailedTransferAsync,
            () => CanRetryFailedTransfer);
        Results.PropertyChanged += OnResultsPropertyChanged;
    }

    public JobResultsViewModel Results { get; }
    public ObservableCollection<WorkspaceBatchItem> Batches { get; }
    public WorkspaceBatchItem? SelectedBatch
    {
        get => selectedBatch;
        set
        {
            if (!SetProperty(ref selectedBatch, value)) return;
            NotifyCommandState();
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
    public bool CanUseBatchActions => !isClosed && !IsBusy && SelectedBatch is not null;
    public bool CanRetryFailedTransfer => CanUseBatchActions &&
        Results.SelectedItem is { TransferStatus: "Failed" };
    public IAsyncRelayCommand LoadSelectedBatchCommand { get; }
    public IAsyncRelayCommand PauseCommand { get; }
    public IAsyncRelayCommand ResumeCommand { get; }
    public IAsyncRelayCommand RetryFailedTransferCommand { get; }

    public void PresentBatch(Guid batchJobId, string? displayText = null)
    {
        if (batchJobId == Guid.Empty) throw new ArgumentException("A batch is required.", nameof(batchJobId));
        if (isClosed) return;
        var existing = Batches.FirstOrDefault(item => item.BatchJobId == batchJobId);
        if (existing is null)
        {
            existing = new(batchJobId, string.IsNullOrWhiteSpace(displayText)
                ? $"批次 {batchJobId.ToString("N")[..8]}"
                : displayText.Trim());
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
        SelectedBatch is { } selected
            ? LoadBatchAsync(selected.BatchJobId, cancellationToken)
            : Task.CompletedTask;

    public Task PauseAsync(CancellationToken cancellationToken = default) =>
        RunBatchActionAsync(
            (batch, token) => pauseService.PauseAsync(batch, token),
            MapPause,
            cancellationToken);

    public Task ResumeAsync(CancellationToken cancellationToken = default) =>
        RunBatchActionAsync(
            (batch, token) => resumeService.ResumeAsync(batch, token),
            MapResume,
            cancellationToken);

    public async Task RetryFailedTransferAsync(CancellationToken cancellationToken = default)
    {
        if (!CanRetryFailedTransfer || Results.SelectedItem is not { } row) return;
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
            if (!isClosed) IsBusy = false;
            operation.Dispose();
        }
    }

    public void Close()
    {
        if (isClosed) return;
        isClosed = true;
        lifetime.Cancel();
        OnPropertyChanged(nameof(IsClosed));
        NotifyCommandState();
    }

    public void Dispose()
    {
        Close();
        Results.PropertyChanged -= OnResultsPropertyChanged;
        lifetime.Dispose();
    }

    private async Task LoadBatchAsync(Guid batchJobId, CancellationToken cancellationToken)
    {
        if (isClosed || IsBusy) return;
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
            if (!isClosed) IsBusy = false;
            operation.Dispose();
        }
    }

    private async Task RunBatchActionAsync<TDisposition>(
        Func<Guid, CancellationToken, Task<TDisposition>> action,
        Func<TDisposition, string> mapStatus,
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
            await Results.RefreshAsync(operation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
        catch (Exception)
        {
            if (IsCurrent(operation)) StatusMessage = "批次操作失败，请稍后重试。";
        }
        finally
        {
            if (!isClosed) IsBusy = false;
            operation.Dispose();
        }
    }

    private CancellationTokenSource? BeginOperation(CancellationToken cancellationToken)
    {
        if (isClosed) return null;
        return CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
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
        OnPropertyChanged(nameof(CanRetryFailedTransfer));
        LoadSelectedBatchCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        ResumeCommand.NotifyCanExecuteChanged();
        RetryFailedTransferCommand.NotifyCanExecuteChanged();
    }

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
