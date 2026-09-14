using System.Collections.ObjectModel;
using BaiduBatchTransfer.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SuperPan.Licensing.Client;

namespace BaiduBatchTransfer.App.Features.Jobs;

public sealed class JobResultsViewModel : ObservableObject
{
    public const int PageSize = 100;
    private readonly IJobResultsWorkflow workflow;
    private readonly IResultSaveDialog saveDialog;
    private readonly IJobResultCapabilityAvailability capabilities;
    private readonly IExistingShareDecisionInteraction? existingShareInteraction;
    private readonly ITextClipboard? clipboard;
    private readonly ILicenseAuthorizationGate? licenseGate;
    private readonly SemaphoreSlim lifecycleRefreshGate = new(1, 1);
    private Guid batchJobId;
    private long? nextCursor;
    private long generation;
    private bool isBusy;
    private bool isSaving;
    private bool isHistoryCleanupBusy;
    private long totalCount;
    private long resourceCount;
    private long operationCount;
    private long failedCount;
    private long pausedCount;
    private long completeSuccessCount;
    private long terminalProblemCount;
    private long exportedRows;
    private JobResultRow? selectedItem;
    private string statusMessage = "请选择一个任务查看结果。";
    private CancellationTokenSource? saveCancellation;
    private ProgressSnapshot? lastProgressSnapshot;
    private Guid progressBatchJobId;
    private bool refreshFailureActive;
    private readonly HashSet<Guid> loadedOperationIds = [];
    private readonly HashSet<Guid> autoPromptedExistingShareBatches = [];
    private readonly HashSet<Guid> finalizedLifecycleBatches = [];

    public JobResultsViewModel(
        IJobResultsWorkflow workflow,
        IResultSaveDialog saveDialog,
        IJobResultCapabilityAvailability capabilities,
        IExistingShareDecisionInteraction? existingShareInteraction = null,
        ITextClipboard? clipboard = null,
        ILicenseAuthorizationGate? licenseGate = null)
    {
        this.workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        this.saveDialog = saveDialog ?? throw new ArgumentNullException(nameof(saveDialog));
        this.capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        this.existingShareInteraction = existingShareInteraction;
        this.clipboard = clipboard;
        this.licenseGate = licenseGate;
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
        CopyGeneratedShareCommand = new AsyncRelayCommand<JobResultRow?>(
            CopyGeneratedShareAsync,
            row => CanCopyGeneratedShareRow(row ?? SelectedItem));
        CancelSaveCommand = new RelayCommand(CancelSave, () => IsSaving);
    }

    public ObservableCollection<JobResultRow> Items { get; } = [];
    public event EventHandler? ManualRefreshCompleted;
    public Guid BatchJobId => batchJobId;
    public long TotalCount { get => totalCount; private set => SetProperty(ref totalCount, value); }
    public long ResourceCount { get => resourceCount; private set => SetProperty(ref resourceCount, value); }
    public long OperationCount { get => operationCount; private set => SetProperty(ref operationCount, value); }
    public long FailedCount { get => failedCount; private set => SetProperty(ref failedCount, value); }
    public long PausedCount { get => pausedCount; private set => SetProperty(ref pausedCount, value); }
    public long CompleteSuccessCount { get => completeSuccessCount; private set => SetProperty(ref completeSuccessCount, value); }
    public long TerminalProblemCount { get => terminalProblemCount; private set => SetProperty(ref terminalProblemCount, value); }
    public long ExportedRows { get => exportedRows; private set => SetProperty(ref exportedRows, value); }
    public string StatusMessage { get => statusMessage; private set => SetProperty(ref statusMessage, value); }
    public string TransferCapabilityMessage => capabilities.TransferStatusMessage;
    public string ShareCapabilityMessage => capabilities.ShareStatusMessage;
    public bool IsHistoryCleanupBusy
    {
        get => isHistoryCleanupBusy;
        set
        {
            if (SetProperty(ref isHistoryCleanupBusy, value)) NotifyCommandState();
        }
    }
    private bool CanUseResultActions => !IsBusy && !IsSaving && !IsHistoryCleanupBusy;
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
    public bool CanLoadNextPage => batchJobId != Guid.Empty && nextCursor is not null && CanUseResultActions;
    public bool CanRetryShareOnly => CanUseResultActions && capabilities.CanUseShareActions &&
        SelectedItem is { ShareOperationId: not null, CanRetryShare: true };
    public bool CanReconcileTransfer => CanUseResultActions && capabilities.CanUseTransferActions &&
        SelectedItem is { CanReconcileTransfer: true };
    public bool CanReconcileShare => CanUseResultActions && capabilities.CanUseShareActions &&
        SelectedItem is { ShareOperationId: not null, CanReconcileShare: true };
    public bool CanResumeTransfer => CanUseResultActions && capabilities.CanUseTransferActions &&
        SelectedItem is { CanResumeTransfer: true };
    public bool CanResumeShare => CanUseResultActions && capabilities.CanUseShareActions &&
        SelectedItem is { ShareOperationId: not null, CanResumeShare: true };
    public bool CanSaveAs => batchJobId != Guid.Empty && CanUseResultActions;
    public bool CanCopyGeneratedShare => CanCopyGeneratedShareRow(SelectedItem);
    public bool CanRefresh => batchJobId != Guid.Empty && CanUseResultActions &&
        !finalizedLifecycleBatches.Contains(batchJobId);
    public bool CanResolveExistingShares => batchJobId != Guid.Empty && CanUseResultActions &&
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
    public IAsyncRelayCommand CopyGeneratedShareCommand { get; }
    public IRelayCommand CancelSaveCommand { get; }

    public void Reset()
    {
        Interlocked.Increment(ref generation);
        var previousSave = saveCancellation;
        saveCancellation = null;
        batchJobId = Guid.Empty;
        nextCursor = null;
        selectedItem = null;
        isBusy = false;
        isSaving = false;
        totalCount = resourceCount = operationCount = failedCount = pausedCount = 0;
        completeSuccessCount = terminalProblemCount = exportedRows = 0;
        progressBatchJobId = Guid.Empty;
        lastProgressSnapshot = null;
        refreshFailureActive = false;
        loadedOperationIds.Clear();
        autoPromptedExistingShareBatches.Clear();
        finalizedLifecycleBatches.Clear();
        statusMessage = "请选择一个任务查看结果。";
        previousSave?.Cancel();
        Items.Clear();
        OnPropertyChanged(string.Empty);
        NotifyCommandState();
    }

    public Task InitializeAsync(Guid requestedBatchJobId, CancellationToken cancellationToken = default) =>
        InitializeCoreAsync(requestedBatchJobId, propagateFailure: false, cancellationToken);

    public Task InitializeForHandoffAsync(Guid requestedBatchJobId, CancellationToken cancellationToken = default) =>
        InitializeCoreAsync(requestedBatchJobId, propagateFailure: true, cancellationToken);

    private async Task InitializeCoreAsync(
        Guid requestedBatchJobId,
        bool propagateFailure,
        CancellationToken cancellationToken)
    {
        if (requestedBatchJobId == Guid.Empty) throw new ArgumentException("A batch is required.", nameof(requestedBatchJobId));
        if (IsHistoryCleanupBusy && !propagateFailure) return;
        var capturedGeneration = Interlocked.Increment(ref generation);
        if (requestedBatchJobId != progressBatchJobId)
        {
            progressBatchJobId = requestedBatchJobId;
            lastProgressSnapshot = null;
            refreshFailureActive = false;
        }
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
            if (propagateFailure) throw;
        }
        catch (Exception)
        {
            if (capturedGeneration == generation) StatusMessage = "任务结果读取失败，请稍后重试。";
            if (propagateFailure) throw;
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
        var capturedGeneration = generation;
        await RefreshForLifecycleAsync(cancellationToken).ConfigureAwait(true);
        if (capturedGeneration == generation && !IsHistoryCleanupBusy && !cancellationToken.IsCancellationRequested)
            ManualRefreshCompleted?.Invoke(this, EventArgs.Empty);
    }

    public async Task<bool> RefreshForLifecycleAsync(CancellationToken cancellationToken = default)
    {
        if (!CanRefresh) return false;
        var capturedBatch = batchJobId;
        await lifecycleRefreshGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            if (capturedBatch != batchJobId || !CanRefresh) return false;
            return await RefreshSnapshotCoreAsync(capturedBatch, finalize: false, cancellationToken)
                .ConfigureAwait(true);
        }
        finally
        {
            lifecycleRefreshGate.Release();
        }
    }

    internal async Task<bool> FinalizeForTerminalAsync(
        Guid requestedBatch,
        Func<bool> isCurrent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(isCurrent);
        await lifecycleRefreshGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            if (IsHistoryCleanupBusy || requestedBatch == Guid.Empty || finalizedLifecycleBatches.Contains(requestedBatch) ||
                cancellationToken.IsCancellationRequested || !isCurrent())
                return false;
            if (requestedBatch != batchJobId)
                return await LoadAndFinalizeTerminalSnapshotAsync(requestedBatch, isCurrent, cancellationToken)
                    .ConfigureAwait(true);
            return await RefreshSnapshotCoreAsync(requestedBatch, finalize: true, cancellationToken)
                .ConfigureAwait(true);
        }
        finally
        {
            lifecycleRefreshGate.Release();
        }
    }

    internal bool IsFinalizedForLifecycle(Guid requestedBatch) =>
        finalizedLifecycleBatches.Contains(requestedBatch);

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
        var capturedGeneration = generation;
        var capturedBatch = batchJobId;
        var pendingBatch = await FindPendingExistingShareBatchAsync(cancellationToken).ConfigureAwait(true);
        if (capturedGeneration != generation || capturedBatch != batchJobId ||
            cancellationToken.IsCancellationRequested || pendingBatch is not { } requestedBatch) return null;
        // Initialize starts exactly one new results generation. A Reset or another load invalidates it.
        var initializedGeneration = unchecked(capturedGeneration + 1);
        await InitializeAsync(requestedBatch, cancellationToken).ConfigureAwait(true);
        if (initializedGeneration != generation || requestedBatch != batchJobId ||
            cancellationToken.IsCancellationRequested) return null;
        await ResolvePendingExistingSharesAsync(requestedBatch, cancellationToken).ConfigureAwait(true);
        return initializedGeneration == generation && requestedBatch == batchJobId &&
            !cancellationToken.IsCancellationRequested ? requestedBatch : null;
    }

    internal async Task<Guid?> FindPendingExistingShareBatchAsync(
        CancellationToken cancellationToken = default)
    {
        if (!capabilities.CanUseShareActions || existingShareInteraction is null || !CanUseResultActions) return null;
        var capturedGeneration = generation;
        var capturedBatch = batchJobId;
        try
        {
            var pending = await existingShareInteraction.GetPendingBatchIdsAsync(100, cancellationToken).ConfigureAwait(true);
            if (capturedGeneration != generation || capturedBatch != batchJobId ||
                cancellationToken.IsCancellationRequested) return null;
            var pendingBatch = pending.FirstOrDefault(id => !autoPromptedExistingShareBatches.Contains(id));
            return pendingBatch == Guid.Empty ? null : pendingBatch;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (capturedGeneration == generation && capturedBatch == batchJobId)
                StatusMessage = "恢复已有分享确认已取消。";
            return null;
        }
        catch (Exception)
        {
            if (capturedGeneration == generation && capturedBatch == batchJobId)
                StatusMessage = "无法恢复待确认的已有分享，请稍后重试。";
            return null;
        }
    }

    internal async Task ResolvePendingExistingSharesAsync(
        Guid expectedBatchJobId,
        CancellationToken cancellationToken = default)
    {
        if (expectedBatchJobId == Guid.Empty || batchJobId != expectedBatchJobId || cancellationToken.IsCancellationRequested) return;
        var capturedGeneration = generation;
        var outcome = await ResolveExistingSharesCoreAsync(cancellationToken).ConfigureAwait(true);
        if (capturedGeneration != generation || batchJobId != expectedBatchJobId || cancellationToken.IsCancellationRequested) return;
        if (outcome?.UserClosed == true) autoPromptedExistingShareBatches.Add(expectedBatchJobId);
        else if (outcome?.Completed == true) autoPromptedExistingShareBatches.Remove(expectedBatchJobId);
    }

    public async Task SaveAsAsync(CancellationToken cancellationToken = default)
    {
        if (!CanSaveAs) return;
        var capturedGeneration = generation;
        var capturedBatch = batchJobId;
        var destination = await saveDialog.PickDestinationDirectoryAsync(cancellationToken).ConfigureAwait(true);
        if (capturedGeneration != generation || capturedBatch != batchJobId || cancellationToken.IsCancellationRequested || !CanSaveAs) return;
        if (string.IsNullOrWhiteSpace(destination))
        {
            StatusMessage = "未选择保存目录。";
            return;
        }
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

    private async Task CopyGeneratedShareAsync(
        JobResultRow? commandRow,
        CancellationToken cancellationToken)
    {
        var selected = commandRow ?? SelectedItem;
        if (!CanCopyGeneratedShareRow(selected) || clipboard is null || selected is null) return;
        var capturedGeneration = generation;
        try
        {
            await clipboard.SetTextAsync(
                $"分享链接：{selected.GeneratedShareUrl}\r\n提取码：{selected.GeneratedExtractionCode}",
                cancellationToken).ConfigureAwait(true);
            if (capturedGeneration == generation) StatusMessage = "分享链接和提取码已复制。";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (capturedGeneration == generation) StatusMessage = "复制已取消。";
        }
        catch (Exception)
        {
            if (capturedGeneration == generation) StatusMessage = "复制失败，请稍后重试。";
        }
    }

    private Task RetryShareOnlyAsync(CancellationToken cancellationToken) =>
        RunAuthorizedRowActionAsync(
            LicenseOperationKind.Share,
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
        RunAuthorizedRowActionAsync(
            LicenseOperationKind.Transfer,
            () => workflow.ResumeTransferAsync(SelectedItem!, cancellationToken),
            "正在恢复转存任务……", cancellationToken);

    private Task ResumeShareAsync(CancellationToken cancellationToken) =>
        RunAuthorizedRowActionAsync(
            LicenseOperationKind.Share,
            () => workflow.ResumeShareAsync(SelectedItem!, cancellationToken),
            "正在恢复分享任务……", cancellationToken);

    private async Task RunAuthorizedRowActionAsync(
        LicenseOperationKind operation,
        Func<Task<JobResultActionOutcome>> action,
        string pendingText,
        CancellationToken cancellationToken)
    {
        if (!CanUseResultActions) return;
        var capturedGeneration = generation;
        if (licenseGate is not null)
        {
            var authorization = await licenseGate.AuthorizeAsync(operation, cancellationToken).ConfigureAwait(true);
            if (capturedGeneration != generation || !CanUseResultActions) return;
            if (!authorization.IsAuthorized)
            {
                StatusMessage = "软件尚未激活，联网功能暂不可用。";
                return;
            }
        }
        await RunRowActionAsync(action, pendingText, cancellationToken).ConfigureAwait(true);
    }

    private async Task RunRowActionAsync(
        Func<Task<JobResultActionOutcome>> action,
        string pendingText,
        CancellationToken cancellationToken)
    {
        if (!CanUseResultActions) return;
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
        CompleteSuccessCount = summary.CompleteSuccessCount;
        TerminalProblemCount = summary.TerminalProblemCount;
    }

    private void PublishProgress(Guid requestedBatch)
    {
        var snapshot = new ProgressSnapshot(OperationCount, CompleteSuccessCount, TerminalProblemCount);
        var mustPublish = requestedBatch != progressBatchJobId || lastProgressSnapshot != snapshot || refreshFailureActive;
        progressBatchJobId = requestedBatch;
        lastProgressSnapshot = snapshot;
        refreshFailureActive = false;
        if (!mustPublish) return;

        var processed = snapshot.Success + snapshot.Failed;
        var remaining = Math.Max(0, snapshot.Total - processed);
        StatusMessage = $"进度 {processed:N0}/{snapshot.Total:N0}：成功 {snapshot.Success:N0}，失败 {snapshot.Failed:N0}，剩余 {remaining:N0}。";
    }

    private async Task<bool> RefreshSnapshotCoreAsync(
        Guid capturedBatch,
        bool finalize,
        CancellationToken cancellationToken)
    {
        var capturedGeneration = generation;
        IsBusy = true;
        try
        {
            await ReloadSnapshotAsync(capturedBatch, capturedGeneration, cancellationToken).ConfigureAwait(true);
            if (capturedGeneration != generation || capturedBatch != batchJobId || cancellationToken.IsCancellationRequested)
                return false;
            PublishProgress(capturedBatch);
            if (finalize) FinalizeLifecycle(capturedBatch);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (capturedGeneration == generation && !finalizedLifecycleBatches.Contains(capturedBatch))
                StatusMessage = "刷新任务结果已取消。";
        }
        catch (Exception)
        {
            if (capturedGeneration == generation && capturedBatch == batchJobId)
            {
                if (!refreshFailureActive)
                {
                    refreshFailureActive = true;
                    StatusMessage = "任务结果刷新失败，请稍后重试。";
                }
                if (finalize) FinalizeLifecycle(capturedBatch);
            }
        }
        finally
        {
            if (capturedGeneration == generation) IsBusy = false;
        }
        return false;
    }

    private void FinalizeLifecycle(Guid requestedBatch)
    {
        if (!finalizedLifecycleBatches.Add(requestedBatch)) return;
        NotifyCommandState();
    }

    private async Task<bool> LoadAndFinalizeTerminalSnapshotAsync(
        Guid requestedBatch,
        Func<bool> isCurrent,
        CancellationToken cancellationToken)
    {
        var capturedGeneration = generation;
        IsBusy = true;
        try
        {
            var summaryTask = workflow.LoadSummaryAsync(requestedBatch, cancellationToken);
            var pageTask = workflow.LoadPageAsync(requestedBatch, null, PageSize, cancellationToken);
            await Task.WhenAll(summaryTask, pageTask).ConfigureAwait(true);
            if (capturedGeneration != generation || cancellationToken.IsCancellationRequested || !isCurrent())
                return false;

            var previousSave = saveCancellation;
            saveCancellation = null;
            previousSave?.Cancel();
            IsSaving = false;
            batchJobId = requestedBatch;
            OnPropertyChanged(nameof(BatchJobId));
            Items.Clear();
            loadedOperationIds.Clear();
            nextCursor = null;
            SelectedItem = null;
            if (requestedBatch != progressBatchJobId)
            {
                progressBatchJobId = requestedBatch;
                lastProgressSnapshot = null;
                refreshFailureActive = false;
            }
            ApplySummary(await summaryTask.ConfigureAwait(true));
            ApplyPage(await pageTask.ConfigureAwait(true), replace: true);
            PublishProgress(requestedBatch);
            FinalizeLifecycle(requestedBatch);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            if (capturedGeneration == generation && !cancellationToken.IsCancellationRequested && isCurrent())
                FinalizeLifecycle(requestedBatch);
        }
        finally
        {
            if (capturedGeneration == generation) IsBusy = false;
        }
        return false;
    }

    private async Task ReloadSnapshotAsync(
        Guid requestedBatch,
        long expectedGeneration,
        CancellationToken cancellationToken)
    {
        var summaryTask = workflow.LoadSummaryAsync(requestedBatch, cancellationToken);
        var pageTask = workflow.LoadPageAsync(requestedBatch, null, PageSize, cancellationToken);
        await Task.WhenAll(summaryTask, pageTask).ConfigureAwait(true);
        if (expectedGeneration != generation || requestedBatch != batchJobId || cancellationToken.IsCancellationRequested)
            return;
        ApplySummary(await summaryTask.ConfigureAwait(true));
        ApplyPage(await pageTask.ConfigureAwait(true), replace: true);
    }

    private void ApplyPage(JobResultsPage page, bool replace)
    {
        var selectedOperationId = SelectedItem?.TransferOperationId;
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
                TransferDetailMessage = SafeDetail(item.TransferDetailMessage),
            });
        }
        if (replace && Items.Count > 0)
            SelectedItem = selectedOperationId is { } operationId
                ? Items.FirstOrDefault(item => item.TransferOperationId == operationId) ?? Items[0]
                : Items[0];
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

    private static string? SafeDetail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= 1_024 && !trimmed.Any(char.IsControl) ? trimmed : null;
    }

    private void CancelSave() => saveCancellation?.Cancel();

    private bool CanCopyGeneratedShareRow(JobResultRow? row) =>
        batchJobId != Guid.Empty && CanUseResultActions && clipboard is not null &&
        row is { GeneratedShareUrl: { Length: > 0 }, GeneratedExtractionCode: { Length: > 0 } };

    private void NotifyCommandState()
    {
        OnPropertyChanged(nameof(CanLoadNextPage));
        OnPropertyChanged(nameof(CanRetryShareOnly));
        OnPropertyChanged(nameof(CanReconcileTransfer));
        OnPropertyChanged(nameof(CanReconcileShare));
        OnPropertyChanged(nameof(CanResumeTransfer));
        OnPropertyChanged(nameof(CanResumeShare));
        OnPropertyChanged(nameof(CanSaveAs));
        OnPropertyChanged(nameof(CanCopyGeneratedShare));
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
        CopyGeneratedShareCommand.NotifyCanExecuteChanged();
        CancelSaveCommand.NotifyCanExecuteChanged();
    }

    private readonly record struct ProgressSnapshot(long Total, long Success, long Failed);
}
