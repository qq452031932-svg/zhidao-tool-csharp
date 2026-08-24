using System.Collections.ObjectModel;
using BaiduBatchTransfer.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BaiduBatchTransfer.App.Features.Jobs;

public sealed class JobResultsViewModel : ObservableObject
{
    public const int PageSize = 100;
    private readonly IJobResultsWorkflow workflow;
    private readonly IResultSaveDialog saveDialog;
    private readonly IJobResultCapabilityAvailability capabilities;
    private readonly IExistingShareDecisionInteraction? existingShareInteraction;
    private Guid batchJobId;
    private long? nextCursor;
    private long generation;
    private bool isBusy;
    private bool isSaving;
    private long totalCount;
    private long resourceCount;
    private long operationCount;
    private long failedCount;
    private long pausedCount;
    private long exportedRows;
    private JobResultRow? selectedItem;
    private string statusMessage = "请选择一个任务查看结果。";
    private CancellationTokenSource? saveCancellation;
    private readonly HashSet<Guid> loadedOperationIds = [];
    private readonly HashSet<Guid> autoPromptedExistingShareBatches = [];

    public JobResultsViewModel(
        IJobResultsWorkflow workflow,
        IResultSaveDialog saveDialog,
        IJobResultCapabilityAvailability capabilities,
        IExistingShareDecisionInteraction? existingShareInteraction = null)
    {
        this.workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        this.saveDialog = saveDialog ?? throw new ArgumentNullException(nameof(saveDialog));
        this.capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        this.existingShareInteraction = existingShareInteraction;
        LoadNextPageCommand = new AsyncRelayCommand(LoadNextPageAsync, () => CanLoadNextPage);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => CanRefresh);
        RetryShareOnlyCommand = new AsyncRelayCommand(RetryShareOnlyAsync, () => CanRetryShareOnly);
        ReconcileTransferCommand = new AsyncRelayCommand(ReconcileTransferAsync, () => CanReconcileTransfer);
        ReconcileShareCommand = new AsyncRelayCommand(ReconcileShareAsync, () => CanReconcileShare);
        ResumeTransferCommand = new AsyncRelayCommand(ResumeTransferAsync, () => CanResumeTransfer);
        ResumeShareCommand = new AsyncRelayCommand(ResumeShareAsync, () => CanResumeShare);
        ResolveExistingSharesCommand = new AsyncRelayCommand(
            ResolveExistingSharesAsync,
            () => CanResolveExistingShares);
        SaveAsCommand = new AsyncRelayCommand(SaveAsAsync, () => CanSaveAs);
        CancelSaveCommand = new RelayCommand(CancelSave, () => IsSaving);
    }

    public ObservableCollection<JobResultRow> Items { get; } = [];
    public Guid BatchJobId => batchJobId;
    public long TotalCount { get => totalCount; private set => SetProperty(ref totalCount, value); }
    public long ResourceCount { get => resourceCount; private set => SetProperty(ref resourceCount, value); }
    public long OperationCount { get => operationCount; private set => SetProperty(ref operationCount, value); }
    public long FailedCount { get => failedCount; private set => SetProperty(ref failedCount, value); }
    public long PausedCount { get => pausedCount; private set => SetProperty(ref pausedCount, value); }
    public long ExportedRows { get => exportedRows; private set => SetProperty(ref exportedRows, value); }
    public string StatusMessage { get => statusMessage; private set => SetProperty(ref statusMessage, value); }
    public string TransferCapabilityMessage => capabilities.TransferStatusMessage;
    public string ShareCapabilityMessage => capabilities.ShareStatusMessage;
    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (!SetProperty(ref isBusy, value)) return;
            NotifyCommandState();
        }
    }
    public bool IsSaving
    {
        get => isSaving;
        private set
        {
            if (!SetProperty(ref isSaving, value)) return;
            NotifyCommandState();
        }
    }
    public JobResultRow? SelectedItem
    {
        get => selectedItem;
        set
        {
            if (!SetProperty(ref selectedItem, value)) return;
            NotifyCommandState();
        }
    }
    public bool CanLoadNextPage => batchJobId != Guid.Empty && nextCursor is not null && !IsBusy && !IsSaving;
    public bool CanRetryShareOnly => !IsBusy && !IsSaving && capabilities.CanUseShareActions &&
        SelectedItem is { ShareOperationId: not null, CanRetryShare: true };
    public bool CanReconcileTransfer => !IsBusy && !IsSaving && capabilities.CanUseTransferActions &&
        SelectedItem is { CanReconcileTransfer: true };
    public bool CanReconcileShare => !IsBusy && !IsSaving && capabilities.CanUseShareActions &&
        SelectedItem is { ShareOperationId: not null, CanReconcileShare: true };
    public bool CanResumeTransfer => !IsBusy && !IsSaving && capabilities.CanUseTransferActions &&
        SelectedItem is { CanResumeTransfer: true };
    public bool CanResumeShare => !IsBusy && !IsSaving && capabilities.CanUseShareActions &&
        SelectedItem is { ShareOperationId: not null, CanResumeShare: true };
    public bool CanSaveAs => batchJobId != Guid.Empty && !IsBusy && !IsSaving;
    public bool CanRefresh => batchJobId != Guid.Empty && !IsBusy && !IsSaving;
    public bool CanResolveExistingShares => batchJobId != Guid.Empty && !IsBusy && !IsSaving &&
        capabilities.CanUseShareActions && existingShareInteraction is not null;
    public IAsyncRelayCommand LoadNextPageCommand { get; }
    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand RetryShareOnlyCommand { get; }
    public IAsyncRelayCommand ReconcileTransferCommand { get; }
    public IAsyncRelayCommand ReconcileShareCommand { get; }
    public IAsyncRelayCommand ResumeTransferCommand { get; }
    public IAsyncRelayCommand ResumeShareCommand { get; }
    public IAsyncRelayCommand ResolveExistingSharesCommand { get; }
    public IAsyncRelayCommand SaveAsCommand { get; }
    public IRelayCommand CancelSaveCommand { get; }

    public async Task InitializeAsync(Guid requestedBatchJobId, CancellationToken cancellationToken = default)
    {
        if (requestedBatchJobId == Guid.Empty) throw new ArgumentException("A batch is required.", nameof(requestedBatchJobId));
        var capturedGeneration = Interlocked.Increment(ref generation);
        var previousSave = saveCancellation;
        saveCancellation = null;
        previousSave?.Cancel();
        IsSaving = false;
        batchJobId = requestedBatchJobId;
        OnPropertyChanged(nameof(BatchJobId));
        Items.Clear();
        loadedOperationIds.Clear();
        nextCursor = null;
        SelectedItem = null;
        IsBusy = true;
        StatusMessage = "正在读取任务结果……";
        try
        {
            var summaryTask = workflow.LoadSummaryAsync(requestedBatchJobId, cancellationToken);
            var pageTask = workflow.LoadPageAsync(requestedBatchJobId, null, PageSize, cancellationToken);
            await Task.WhenAll(summaryTask, pageTask).ConfigureAwait(true);
            if (capturedGeneration != generation || requestedBatchJobId != batchJobId) return;
            ApplySummary(await summaryTask.ConfigureAwait(true));
            ApplyPage(await pageTask.ConfigureAwait(true), replace: true);
            StatusMessage = $"已读取 {Items.Count:N0} / {TotalCount:N0} 条操作结果。";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (capturedGeneration == generation) StatusMessage = "读取任务结果已取消。";
        }
        catch (Exception)
        {
            if (capturedGeneration == generation) StatusMessage = "任务结果读取失败，请稍后重试。";
        }
        finally
        {
            if (capturedGeneration == generation) IsBusy = false;
        }
    }

    public async Task LoadNextPageAsync(CancellationToken cancellationToken = default)
    {
        if (!CanLoadNextPage) return;
        var capturedGeneration = generation;
        var capturedBatch = batchJobId;
        var cursor = nextCursor;
        IsBusy = true;
        try
        {
            var page = await workflow.LoadPageAsync(capturedBatch, cursor, PageSize, cancellationToken).ConfigureAwait(true);
            if (capturedGeneration != generation || capturedBatch != batchJobId) return;
            ApplyPage(page, replace: false);
            StatusMessage = $"已读取 {Items.Count:N0} / {TotalCount:N0} 条操作结果。";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (capturedGeneration == generation) StatusMessage = "读取下一页已取消。";
        }
        catch (Exception)
        {
            if (capturedGeneration == generation) StatusMessage = "下一页读取失败，请稍后重试。";
        }
        finally
        {
            if (capturedGeneration == generation) IsBusy = false;
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!CanRefresh) return;
        var capturedGeneration = generation;
        var capturedBatch = batchJobId;
        IsBusy = true;
        StatusMessage = "正在刷新任务结果……";
        try
        {
            await ReloadSnapshotAsync(capturedBatch, capturedGeneration, cancellationToken).ConfigureAwait(true);
            if (capturedGeneration == generation)
                StatusMessage = $"任务结果已刷新，共 {TotalCount:N0} 条操作。";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (capturedGeneration == generation) StatusMessage = "刷新任务结果已取消。";
        }
        catch (Exception)
        {
            if (capturedGeneration == generation) StatusMessage = "任务结果刷新失败，请稍后重试。";
        }
        finally
        {
            if (capturedGeneration == generation) IsBusy = false;
        }
    }

    public async Task ResolveExistingSharesAsync(CancellationToken cancellationToken = default)
    {
        await ResolveExistingSharesCoreAsync(cancellationToken).ConfigureAwait(true);
    }

    private async Task<ExistingShareInteractionOutcome?> ResolveExistingSharesCoreAsync(
        CancellationToken cancellationToken)
    {
        if (!CanResolveExistingShares || existingShareInteraction is null) return null;
        var capturedGeneration = generation;
        var capturedBatch = batchJobId;
        IsBusy = true;
        StatusMessage = "正在读取已有分享……";
        try
        {
            var outcome = await existingShareInteraction.ResolveAsync(capturedBatch, cancellationToken).ConfigureAwait(true);
            if (capturedGeneration != generation || capturedBatch != batchJobId) return null;
            if (outcome.Completed)
                await ReloadSnapshotAsync(capturedBatch, capturedGeneration, cancellationToken).ConfigureAwait(true);
            if (capturedGeneration == generation) StatusMessage = outcome.StatusText;
            return outcome;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (capturedGeneration == generation) StatusMessage = "已有分享确认已取消。";
            return null;
        }
        catch (Exception)
        {
            if (capturedGeneration == generation)
                StatusMessage = "已有分享读取失败，请确认账号登录状态后重试。";
            return null;
        }
        finally
        {
            if (capturedGeneration == generation) IsBusy = false;
        }
    }

    public async Task<Guid?> RestorePendingExistingShareDecisionAsync(
        CancellationToken cancellationToken = default)
    {
        var pendingBatch = await FindPendingExistingShareBatchAsync(cancellationToken).ConfigureAwait(true);
        if (pendingBatch is not { } batchJobId) return null;
        await InitializeAsync(batchJobId, cancellationToken).ConfigureAwait(true);
        await ResolvePendingExistingSharesAsync(batchJobId, cancellationToken).ConfigureAwait(true);
        return batchJobId;
    }

    internal async Task<Guid?> FindPendingExistingShareBatchAsync(
        CancellationToken cancellationToken = default)
    {
        if (!capabilities.CanUseShareActions || existingShareInteraction is null || IsBusy || IsSaving) return null;
        try
        {
            var pending = await existingShareInteraction.GetPendingBatchIdsAsync(100, cancellationToken).ConfigureAwait(true);
            var pendingBatch = pending.FirstOrDefault(id => !autoPromptedExistingShareBatches.Contains(id));
            return pendingBatch == Guid.Empty ? null : pendingBatch;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusMessage = "恢复已有分享确认已取消。";
            return null;
        }
        catch (Exception)
        {
            StatusMessage = "无法恢复待确认的已有分享，请稍后重试。";
            return null;
        }
    }

    internal async Task ResolvePendingExistingSharesAsync(
        Guid expectedBatchJobId,
        CancellationToken cancellationToken = default)
    {
        if (expectedBatchJobId == Guid.Empty || batchJobId != expectedBatchJobId) return;
        var outcome = await ResolveExistingSharesCoreAsync(cancellationToken).ConfigureAwait(true);
        if (batchJobId != expectedBatchJobId) return;
        if (outcome?.UserClosed == true) autoPromptedExistingShareBatches.Add(expectedBatchJobId);
        else if (outcome?.Completed == true) autoPromptedExistingShareBatches.Remove(expectedBatchJobId);
    }

    public async Task SaveAsAsync(CancellationToken cancellationToken = default)
    {
        if (!CanSaveAs) return;
        var destination = await saveDialog.PickDestinationDirectoryAsync(cancellationToken).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(destination))
        {
            StatusMessage = "未选择保存目录。";
            return;
        }
        var capturedGeneration = generation;
        var capturedBatch = batchJobId;
        var currentSave = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        saveCancellation = currentSave;
        IsSaving = true;
        ExportedRows = 0;
        StatusMessage = "正在另存结果表格……";
        var progress = new Progress<long>(value =>
        {
            if (!IsSaving || generation != capturedGeneration || batchJobId != capturedBatch ||
                !ReferenceEquals(saveCancellation, currentSave) || currentSave.IsCancellationRequested) return;
            ExportedRows = value;
            StatusMessage = $"正在另存结果表格，已写入 {value:N0} 行……";
        });
        try
        {
            var written = await workflow.ExportAsync(capturedBatch, destination, progress, currentSave.Token)
                .ConfigureAwait(true);
            if (generation != capturedGeneration || batchJobId != capturedBatch ||
                !ReferenceEquals(saveCancellation, currentSave)) return;
            ExportedRows = written;
            StatusMessage = $"结果表格已保存，共 {written:N0} 行。";
        }
        catch (OperationCanceledException) when (currentSave.IsCancellationRequested)
        {
            if (generation == capturedGeneration && batchJobId == capturedBatch &&
                ReferenceEquals(saveCancellation, currentSave))
                StatusMessage = "另存结果表格已取消；临时文件会尽力清理。";
        }
        catch (Exception)
        {
            if (generation == capturedGeneration && batchJobId == capturedBatch &&
                ReferenceEquals(saveCancellation, currentSave))
                StatusMessage = "结果表格保存失败，请检查目录后重试。";
        }
        finally
        {
            if (ReferenceEquals(saveCancellation, currentSave))
            {
                saveCancellation = null;
                IsSaving = false;
            }
            currentSave.Dispose();
        }
    }

    private Task RetryShareOnlyAsync(CancellationToken cancellationToken) =>
        RunRowActionAsync(
            () => workflow.RetryShareOnlyAsync(SelectedItem!, cancellationToken),
            "正在重新排队分享……", cancellationToken);

    private Task ReconcileTransferAsync(CancellationToken cancellationToken) =>
        RunRowActionAsync(
            () => workflow.ReconcileTransferAsync(SelectedItem!.TransferOperationId, cancellationToken),
            "正在确认转存结果……", cancellationToken);

    private Task ReconcileShareAsync(CancellationToken cancellationToken) =>
        RunRowActionAsync(
            () => workflow.ReconcileShareAsync(SelectedItem!.ShareOperationId!.Value, cancellationToken),
            "正在确认分享结果……", cancellationToken);

    private Task ResumeTransferAsync(CancellationToken cancellationToken) =>
        RunRowActionAsync(
            () => workflow.ResumeTransferAsync(SelectedItem!, cancellationToken),
            "正在恢复转存任务……", cancellationToken);

    private Task ResumeShareAsync(CancellationToken cancellationToken) =>
        RunRowActionAsync(
            () => workflow.ResumeShareAsync(SelectedItem!, cancellationToken),
            "正在恢复分享任务……", cancellationToken);

    private async Task RunRowActionAsync(
        Func<Task<JobResultActionOutcome>> action,
        string pendingText,
        CancellationToken cancellationToken)
    {
        if (IsBusy || IsSaving) return;
        var capturedGeneration = generation;
        IsBusy = true;
        StatusMessage = pendingText;
        try
        {
            var outcome = await action().ConfigureAwait(true);
            if (capturedGeneration != generation) return;
            if (outcome.Accepted && !outcome.IsStale)
                await ReloadSnapshotAsync(batchJobId, capturedGeneration, cancellationToken).ConfigureAwait(true);
            if (capturedGeneration != generation) return;
            StatusMessage = outcome.IsStale
                ? "记录状态已经变化，请刷新结果后重试。"
                : outcome.Accepted ? SafeStatus(outcome.StatusText) : "当前状态不允许执行此操作。";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (capturedGeneration == generation) StatusMessage = "操作已取消。";
        }
        catch (Exception)
        {
            if (capturedGeneration == generation) StatusMessage = "操作失败，请稍后重试。";
        }
        finally
        {
            if (capturedGeneration == generation) IsBusy = false;
        }
    }

    private void ApplySummary(JobResultsSummary summary)
    {
        ResourceCount = summary.ResourceCount;
        OperationCount = summary.OperationCount;
        FailedCount = summary.FailedCount;
        PausedCount = summary.PausedCount;
    }

    private async Task ReloadSnapshotAsync(
        Guid requestedBatch,
        long expectedGeneration,
        CancellationToken cancellationToken)
    {
        var summaryTask = workflow.LoadSummaryAsync(requestedBatch, cancellationToken);
        var pageTask = workflow.LoadPageAsync(requestedBatch, null, PageSize, cancellationToken);
        await Task.WhenAll(summaryTask, pageTask).ConfigureAwait(true);
        if (expectedGeneration != generation || requestedBatch != batchJobId) return;
        ApplySummary(await summaryTask.ConfigureAwait(true));
        ApplyPage(await pageTask.ConfigureAwait(true), replace: true);
    }

    private void ApplyPage(JobResultsPage page, bool replace)
    {
        var operationIds = page.Items.Select(item => item.TransferOperationId).ToArray();
        if (operationIds.Any(id => id == Guid.Empty) || operationIds.Distinct().Count() != operationIds.Length ||
            (!replace && operationIds.Any(loadedOperationIds.Contains)) ||
            (page.Items.Count == 0 && page.NextCursor is not null))
            throw new InvalidOperationException("The result page did not form a strict keyset page.");
        if (replace)
        {
            SelectedItem = null;
            Items.Clear();
            loadedOperationIds.Clear();
        }
        foreach (var item in page.Items)
        {
            loadedOperationIds.Add(item.TransferOperationId);
            Items.Add(item with
            {
                TransferStatusText = SafeStatus(item.TransferStatusText),
                ShareStatusText = SafeStatus(item.ShareStatusText),
            });
        }
        TotalCount = page.TotalCount;
        nextCursor = page.NextCursor;
        NotifyCommandState();
    }

    private static string SafeStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return "状态未知";
        return status.Any(character => character is >= '\u3400' and <= '\u9fff')
            ? status
            : "状态未知";
    }

    private void CancelSave() => saveCancellation?.Cancel();

    private void NotifyCommandState()
    {
        OnPropertyChanged(nameof(CanLoadNextPage));
        OnPropertyChanged(nameof(CanRetryShareOnly));
        OnPropertyChanged(nameof(CanReconcileTransfer));
        OnPropertyChanged(nameof(CanReconcileShare));
        OnPropertyChanged(nameof(CanResumeTransfer));
        OnPropertyChanged(nameof(CanResumeShare));
        OnPropertyChanged(nameof(CanSaveAs));
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(CanResolveExistingShares));
        LoadNextPageCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
        RetryShareOnlyCommand.NotifyCanExecuteChanged();
        ReconcileTransferCommand.NotifyCanExecuteChanged();
        ReconcileShareCommand.NotifyCanExecuteChanged();
        ResumeTransferCommand.NotifyCanExecuteChanged();
        ResumeShareCommand.NotifyCanExecuteChanged();
        ResolveExistingSharesCommand.NotifyCanExecuteChanged();
        SaveAsCommand.NotifyCanExecuteChanged();
        CancelSaveCommand.NotifyCanExecuteChanged();
    }
}
