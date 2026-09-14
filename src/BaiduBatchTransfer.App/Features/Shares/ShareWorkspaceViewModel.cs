using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using BaiduBatchTransfer.App.Features.Common;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SuperPan.Licensing.Client;

namespace BaiduBatchTransfer.App.Features.Shares;

public sealed record ShareAccountOption(Guid AccountId, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed record ShareExistingDecisionOption(ExistingShareDecision Decision, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed record SharePendingDisplayRow(
    OwnedRemoteSelection Selection,
    string Title,
    string ParentPath,
    string AccountDisplayName,
    string ExistingSharePolicyDisplay,
    string ReadinessText);

public sealed record ShareResultDisplayRow(
    StandaloneShareOperationRecord Operation,
    string Title,
    string AccountDisplayName,
    string StatusDisplay,
    string? NewShareUrl,
    string? NewExtractionCode,
    DateTimeOffset UpdatedAt,
    string ResultSaveStatus)
{
    public override string ToString() =>
        $"ShareResultDisplayRow {{ OperationId = {Operation.Id:D}, Status = {Operation.Status} }}";
}

public sealed class ShareSelectionGroup : ObservableObject
{
    private readonly ObservableCollection<OwnedRemoteSelection> selections = [];

    public Guid AccountId { get; }
    public string AccountDisplayName { get; }
    public ReadOnlyObservableCollection<OwnedRemoteSelection> Selections { get; }

    public ShareSelectionGroup(Guid accountId, string accountDisplayName)
    {
        if (accountId == Guid.Empty) throw new ArgumentException("An account is required.", nameof(accountId));
        ArgumentException.ThrowIfNullOrWhiteSpace(accountDisplayName);
        AccountId = accountId;
        AccountDisplayName = accountDisplayName.Trim();
        Selections = new ReadOnlyObservableCollection<OwnedRemoteSelection>(selections);
    }

    internal bool Contains(OwnedRemoteSelection selection) =>
        selections.Any(existing => existing.HasSameExecutionIdentity(selection));

    internal void Add(OwnedRemoteSelection selection) => selections.Add(selection);

    internal bool Remove(OwnedRemoteSelection selection) => selections.Remove(selection);
}

public sealed class ShareWorkspaceViewModel : ObservableObject, IDisposable
{
    private readonly IRemoteResourcePicker resourcePicker;
    private readonly IStandaloneShareStore store;
    private readonly IResultSaveDialog resultDialog;
    private readonly TimeProvider timeProvider;
    private readonly CreateStandaloneShareBatchService? batchService;
    private readonly ILocalResultExportCoordinator? localResultExports;
    private readonly IStandaloneShareResultProjectionRepository? resultProjectionRepository;
    private readonly ILocalResultExportStore? localResultExportStore;
    private readonly ILicenseAuthorizationGate? licenseGate;
    private readonly int historyPageSize;
    private readonly CancellationTokenSource lifetime = new();
    private readonly WorkspaceLogBuffer logBuffer = new();
    private readonly ObservableCollection<ShareSelectionGroup> selectionGroups = [];
    private readonly ObservableCollection<SharePendingDisplayRow> pendingRows = [];
    private ShareAccountOption? activeAccount;
    private string? resultDirectory;
    private StandaloneShareBatchRecord? selectedBatch;
    private StandaloneShareOperationRecord? selectedOperation;
    private Guid? loadedBatchId;
    private StandaloneSharePendingBatchEnvelope? pendingBatch;
    private string statusMessage = "请选择账号和网盘资源。";
    private bool isBusy;
    private bool isClosed;
    private long batchSelectionGeneration;
    private long operationSelectionGeneration;
    private bool useFixedExtractionCode;
    private CancellationTokenSource? saveResultsCancellation;
    private string? fixedExtractionCode;
    private ShareExistingDecisionOption existingShareDecision;
    private int historyPageNumber = 1;
    private long historyTotalCount;

    public ShareWorkspaceViewModel(
        IReadOnlyList<ShareAccountOption> accounts,
        IRemoteResourcePicker resourcePicker,
        IStandaloneShareStore store,
        IResultSaveDialog resultDialog,
        TimeProvider? timeProvider = null,
        int historyPageSize = 20,
        CreateStandaloneShareBatchService? batchService = null,
        ILocalResultExportCoordinator? localResultExports = null,
        IStandaloneShareResultProjectionRepository? resultProjectionRepository = null,
        ILocalResultExportStore? localResultExportStore = null,
        ILicenseAuthorizationGate? licenseGate = null)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        this.resourcePicker = resourcePicker ?? throw new ArgumentNullException(nameof(resourcePicker));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.resultDialog = resultDialog ?? throw new ArgumentNullException(nameof(resultDialog));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.batchService = batchService;
        this.localResultExports = localResultExports;
        this.resultProjectionRepository = resultProjectionRepository;
        this.localResultExportStore = localResultExportStore;
        this.licenseGate = licenseGate;
        if (historyPageSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(historyPageSize));
        this.historyPageSize = historyPageSize;

        Accounts = [];
        ExistingShareDecisionOptions =
        [
            new(ExistingShareDecision.Reuse, "复用完全匹配的已有分享"),
            new(ExistingShareDecision.Regenerate, "重新生成分享"),
            new(ExistingShareDecision.Cancel, "遇到已有分享则取消"),
        ];
        existingShareDecision = ExistingShareDecisionOptions[0];
        SelectionGroups = new ReadOnlyObservableCollection<ShareSelectionGroup>(selectionGroups);
        PendingRows = new ReadOnlyObservableCollection<SharePendingDisplayRow>(pendingRows);
        Batches = [];
        Operations = [];
        ResultRows = [];
        ReloadAccounts(accounts);

        PickResourcesCommand = new AsyncRelayCommand(PickResourcesAsync, () => CanPickResources);
        ChooseResultDirectoryCommand = new AsyncRelayCommand(
            ChooseResultDirectoryAsync,
            () => CanEditSelection);
        StartCommand = new AsyncRelayCommand(StartAsync, () => CanStart);
        ClearLogsCommand = new RelayCommand(ClearLogs, () => CanClearLogs);
        ResetPendingDraftCommand = new AsyncRelayCommand(
            ResetPendingDraftAsync,
            () => CanResetPendingDraft);
        RemoveSelectionCommand = new RelayCommand<OwnedRemoteSelection>(Remove, _ => CanEditSelection);
        LoadSelectedBatchCommand = new AsyncRelayCommand(LoadSelectedBatchAsync, () => CanUseBatchControls);
        PauseCommand = new AsyncRelayCommand(PauseAsync, () => CanPause);
        ResumeCommand = new AsyncRelayCommand(ResumeAsync, () => CanResume);
        RetryFailedCommand = new AsyncRelayCommand(RetryFailedAsync, () => CanRetryFailed);
        SaveResultsCommand = new AsyncRelayCommand(SaveResultsAsync, () => CanSaveResults);
        CancelSaveResultsCommand = new RelayCommand(CancelSaveResults, () => CanCancelSaveResults);
        PreviousHistoryPageCommand = new AsyncRelayCommand(
            PreviousHistoryPageAsync, () => CanLoadPreviousHistoryPage);
        NextHistoryPageCommand = new AsyncRelayCommand(
            NextHistoryPageAsync, () => CanLoadNextHistoryPage);
        ((INotifyCollectionChanged)Logs).CollectionChanged += OnLogsChanged;
        logBuffer.AppendSafe(StatusMessage);
    }

    public ObservableCollection<ShareAccountOption> Accounts { get; }
    public IReadOnlyList<ShareExistingDecisionOption> ExistingShareDecisionOptions { get; }
    public ReadOnlyObservableCollection<ShareSelectionGroup> SelectionGroups { get; }
    public ReadOnlyObservableCollection<SharePendingDisplayRow> PendingRows { get; }
    public ObservableCollection<StandaloneShareBatchRecord> Batches { get; }
    public ObservableCollection<StandaloneShareOperationRecord> Operations { get; }
    public ObservableCollection<ShareResultDisplayRow> ResultRows { get; }
    public ReadOnlyObservableCollection<WorkspaceLogEntry> Logs => logBuffer.Entries;

    public ShareAccountOption? ActiveAccount
    {
        get => activeAccount;
        set
        {
            if (value is not null)
            {
                value = Accounts.FirstOrDefault(account => account.AccountId == value.AccountId)
                    ?? throw new InvalidOperationException("该账号当前不可用于分享。");
            }
            if (!SetProperty(ref activeAccount, value)) return;
            NotifyCommandState();
        }
    }

    public string? ResultDirectory
    {
        get => resultDirectory;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (pendingBatch is not null && !string.Equals(resultDirectory, normalized, StringComparison.Ordinal))
                throw new InvalidOperationException("请先确认上一次是否已创建，再修改内容。");
            if (!SetProperty(ref resultDirectory, normalized)) return;
            NotifyCommandState();
        }
    }

    public bool UseFixedExtractionCode
    {
        get => useFixedExtractionCode;
        set
        {
            if (pendingBatch is not null && useFixedExtractionCode != value)
                throw new InvalidOperationException("请先确认上一次是否已创建，再修改内容。");
            if (!SetProperty(ref useFixedExtractionCode, value)) return;
            NotifyCommandState();
        }
    }

    public string? FixedExtractionCode
    {
        get => fixedExtractionCode;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (pendingBatch is not null && !string.Equals(fixedExtractionCode, normalized, StringComparison.Ordinal))
                throw new InvalidOperationException("请先确认上一次是否已创建，再修改内容。");
            if (!SetProperty(ref fixedExtractionCode, normalized)) return;
            NotifyCommandState();
        }
    }

    public ShareExistingDecisionOption SelectedExistingShareDecision
    {
        get => existingShareDecision;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            var canonical = ExistingShareDecisionOptions.SingleOrDefault(item => item.Decision == value.Decision)
                ?? throw new InvalidOperationException("不支持这个已有分享处理方式。");
            if (pendingBatch is not null && existingShareDecision.Decision != canonical.Decision)
                throw new InvalidOperationException("请先确认上一次是否已创建，再修改内容。");
            if (!SetProperty(ref existingShareDecision, canonical)) return;
            RebuildPendingRows();
        }
    }

    public StandaloneShareBatchRecord? SelectedBatch
    {
        get => selectedBatch;
        set
        {
            var previousBatchId = selectedBatch?.Id;
            if (!SetProperty(ref selectedBatch, value)) return;
            if (previousBatchId != value?.Id) ClearLoadedDetails();
            Interlocked.Increment(ref batchSelectionGeneration);
            if (SelectedOperation is not null && SelectedOperation.BatchId != value?.Id)
                SelectedOperation = null;
            NotifyCommandState();
        }
    }

    public StandaloneShareOperationRecord? SelectedOperation
    {
        get => selectedOperation;
        set
        {
            if (!SetProperty(ref selectedOperation, value)) return;
            Interlocked.Increment(ref operationSelectionGeneration);
            NotifyCommandState();
        }
    }

    public string StatusMessage
    {
        get => statusMessage;
        private set
        {
            if (!SetProperty(ref statusMessage, value)) return;
            OnPropertyChanged(nameof(UserFacingStatusMessage));
            if (!isClosed) logBuffer.AppendSafe(value);
        }
    }

    public string UserFacingStatusMessage => MapUserFacingStatus(StatusMessage);

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
    public bool HasPendingBatchConfirmation => pendingBatch is not null;
    public bool CanEditSelection => !IsClosed && !IsBusy && pendingBatch is null;
    public bool CanResetPendingDraft => !IsClosed && !IsBusy && pendingBatch is not null;
    public bool CanUseAdvancedSettings => CanEditSelection || CanResetPendingDraft;
    public bool CanSelectHistory => !IsClosed && !IsBusy;
    public int SelectedResourceCount => SelectionGroups.Sum(group => group.Selections.Count);
    public bool CanPickResources => CanEditSelection && ActiveAccount is not null;
    public bool CanStart => !IsClosed && !IsBusy && (pendingBatch is not null ||
        (ActiveAccount is not null && SelectedResourceCount > 0 &&
         !string.IsNullOrWhiteSpace(ResultDirectory) &&
         (!UseFixedExtractionCode || PermanentShareRequest.IsValidFixedExtractionCode(FixedExtractionCode)) &&
         SelectionGroups.All(group => Accounts.Any(account => account.AccountId == group.AccountId))));
    public bool CanUseBatchControls => !IsClosed && !IsBusy && SelectedBatch is not null;
    public bool CanPause => CanUseBatchControls &&
        SelectedBatch!.Status is StandaloneShareBatchStatus.Queued or StandaloneShareBatchStatus.Active;
    public bool CanResume => CanUseBatchControls &&
        SelectedBatch!.Status == StandaloneShareBatchStatus.Paused;
    public bool CanRetryFailed => CanUseBatchControls &&
        SelectedOperation is { CanSafelyRetry: true } operation &&
        operation.BatchId == SelectedBatch!.Id;
    public bool CanSaveResults => !IsClosed && !IsBusy && localResultExports is not null &&
        loadedBatchId == SelectedBatch?.Id &&
        SelectedBatch?.Status is StandaloneShareBatchStatus.Completed or
            StandaloneShareBatchStatus.CompletedWithFailures;
    public bool CanCancelSaveResults => !IsClosed && saveResultsCancellation is { IsCancellationRequested: false };
    public int HistoryPageNumber => historyPageNumber;
    public string HistoryPageText => $"第 {HistoryPageNumber} 页";
    public bool CanLoadPreviousHistoryPage => CanSelectHistory && HistoryPageNumber > 1;
    public bool CanLoadNextHistoryPage => CanSelectHistory &&
        (long)HistoryPageNumber * historyPageSize < historyTotalCount;
    public bool CanClearLogs => !IsClosed && !IsBusy && Logs.Count > 0;

    public IAsyncRelayCommand PickResourcesCommand { get; }
    public IAsyncRelayCommand ChooseResultDirectoryCommand { get; }
    public IAsyncRelayCommand StartCommand { get; }
    public IAsyncRelayCommand ResetPendingDraftCommand { get; }
    public IRelayCommand<OwnedRemoteSelection> RemoveSelectionCommand { get; }
    public IAsyncRelayCommand LoadSelectedBatchCommand { get; }
    public IAsyncRelayCommand PauseCommand { get; }
    public IAsyncRelayCommand ResumeCommand { get; }
    public IAsyncRelayCommand RetryFailedCommand { get; }
    public IAsyncRelayCommand SaveResultsCommand { get; }
    public IRelayCommand CancelSaveResultsCommand { get; }
    public IAsyncRelayCommand PreviousHistoryPageCommand { get; }
    public IAsyncRelayCommand NextHistoryPageCommand { get; }
    public IRelayCommand ClearLogsCommand { get; }

    public void ReloadAccounts(IReadOnlyList<ShareAccountOption> accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        if (accounts.Any(account => account.AccountId == Guid.Empty || string.IsNullOrWhiteSpace(account.DisplayName)))
            throw new ArgumentException("Every share account must have an id and display name.", nameof(accounts));
        if (accounts.Select(account => account.AccountId).Distinct().Count() != accounts.Count)
            throw new ArgumentException("Share accounts must be unique.", nameof(accounts));

        var previousId = ActiveAccount?.AccountId;
        Accounts.Clear();
        foreach (var account in accounts) Accounts.Add(account);
        ActiveAccount = Accounts.FirstOrDefault(account => account.AccountId == previousId)
            ?? Accounts.FirstOrDefault();
        NotifySelectionState();
    }

    public void Add(OwnedRemoteSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (!CanEditSelection)
            throw new InvalidOperationException("请先确认上一次是否已创建，再修改内容。");
        if (ActiveAccount is null)
            throw new InvalidOperationException("请先选择账号。");
        if (selection.AccountId != ActiveAccount.AccountId)
            throw new InvalidOperationException("所选资源不属于当前账号，请切回对应账号重新选择。");
        AddVerified(selection, ActiveAccount.DisplayName);
        StatusMessage = $"已选择 {SelectedResourceCount} 项资源。";
    }

    public async Task PickResourcesAsync(CancellationToken cancellationToken = default)
    {
        if (!CanPickResources || ActiveAccount is not { } account) return;
        using var operation = CreateOperation(cancellationToken);
        IsBusy = true;
        try
        {
            var picked = await resourcePicker.PickAsync(account.AccountId, operation.Token).ConfigureAwait(true);
            operation.Token.ThrowIfCancellationRequested();
            if (picked.Any(selection => selection.AccountId != account.AccountId))
            {
                StatusMessage = "选择结果与当前账号不一致，请重新选择。";
                return;
            }

            foreach (var selection in picked) AddVerified(selection, account.DisplayName);
            StatusMessage = picked.Count == 0
                ? "没有添加资源。"
                : $"已选择 {SelectedResourceCount} 项资源。";
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            StatusMessage = "已取消选择。";
        }
        catch (Exception)
        {
            StatusMessage = "暂时无法打开网盘资源，请稍后重试。";
        }
        finally
        {
            if (!IsClosed) IsBusy = false;
        }
    }

    public async Task ChooseResultDirectoryAsync(CancellationToken cancellationToken = default)
    {
        if (!CanEditSelection) return;
        using var operation = CreateOperation(cancellationToken);
        IsBusy = true;
        try
        {
            var selected = await resultDialog.PickDestinationDirectoryAsync(operation.Token).ConfigureAwait(true);
            if (!string.IsNullOrWhiteSpace(selected))
            {
                ResultDirectory = selected;
                StatusMessage = "已选择分享结果保存目录。";
            }
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            StatusMessage = "已取消选择目录。";
        }
        catch (Exception)
        {
            StatusMessage = "暂时无法选择目录，请稍后重试。";
        }
        finally
        {
            if (!IsClosed) IsBusy = false;
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (!CanStart) return;
        if (!await IsRemoteOperationAuthorizedAsync(cancellationToken).ConfigureAwait(true)) return;
        var envelope = pendingBatch;
        if (envelope is null)
        {
            var selections = SelectionGroups.SelectMany(group => group.Selections).ToArray();
            var allowedAccountIds = Accounts.Select(account => account.AccountId).ToHashSet();
            if (SelectionGroups.Any(group =>
                    !allowedAccountIds.Contains(group.AccountId) ||
                    group.Selections.Count == 0 ||
                    group.Selections.Any(selection => selection.AccountId != group.AccountId)))
            {
                StatusMessage = "所选资源与账号不一致，请重新选择。";
                return;
            }

            var now = timeProvider.GetUtcNow();
            var batchId = Guid.NewGuid();
            var operationIds = selections.Select(_ => Guid.NewGuid()).ToArray();
            var behavior = UseFixedExtractionCode
                ? ShareExtractionCodeBehavior.Fixed
                : ShareExtractionCodeBehavior.Random;
            envelope = batchService?.BuildDraft(
                batchId, operationIds, ResultDirectory!, selections, behavior,
                UseFixedExtractionCode ? FixedExtractionCode : null, now,
                SelectedExistingShareDecision.Decision);
            if (envelope is null)
            {
                var batch = StandaloneShareBatchRecord.Create(batchId, ResultDirectory!, selections.Length, now);
                var shareOperations = selections.Select((selection, index) =>
                    StandaloneShareOperationRecord.Create(
                        operationIds[index], batchId, selection, index + 1, now, behavior,
                        UseFixedExtractionCode ? FixedExtractionCode : null,
                        SelectedExistingShareDecision.Decision)).ToArray();
                envelope = StandaloneSharePendingBatchEnvelope.Create(batch, shareOperations);
            }
            pendingBatch = envelope;
            NotifyPendingBatchState();
        }

        using var operation = CreateOperation(cancellationToken);
        IsBusy = true;
        try
        {
            StandaloneShareBatchCreationReceipt? receipt;
            if (batchService is not null)
            {
                var allowedAccountIds = Accounts.Select(account => account.AccountId).ToHashSet();
                if (envelope.Operations.Any(item => !allowedAccountIds.Contains(item.AccountId)))
                {
                    StatusMessage = "所选账号当前不可用，请先检查账号。";
                    return;
                }
                receipt = await batchService.CreateAsync(envelope, operation.Token).ConfigureAwait(true);
            }
            else
            {
                receipt = await store.GetBatchCreationAsync(
                        envelope.Batch.Id, envelope.DraftFingerprint, operation.Token)
                    .ConfigureAwait(true);
                if (receipt is null)
                {
                    var allowedAccountIds = Accounts.Select(account => account.AccountId).ToHashSet();
                    if (envelope.Operations.Any(item => !allowedAccountIds.Contains(item.AccountId)))
                    {
                        StatusMessage = "所选账号当前不可用，请先检查账号。";
                        return;
                    }
                    receipt = await store.CreateOrGetBatchAsync(envelope, operation.Token).ConfigureAwait(true);
                }
            }
            await ApplyCreationReceiptAsync(envelope, receipt).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            StatusMessage = "创建结果需要确认，请再次点击开始分享。";
        }
        catch (Exception)
        {
            StatusMessage = "创建结果需要确认，请再次点击开始分享。";
        }
        finally
        {
            if (!IsClosed) IsBusy = false;
        }
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        LoadHistoryPageAsync(1, cancellationToken);

    public Task PreviousHistoryPageAsync(CancellationToken cancellationToken = default) =>
        !CanLoadPreviousHistoryPage
            ? Task.CompletedTask
            : LoadHistoryPageAsync(HistoryPageNumber - 1, cancellationToken);

    public Task NextHistoryPageAsync(CancellationToken cancellationToken = default) =>
        !CanLoadNextHistoryPage
            ? Task.CompletedTask
            : LoadHistoryPageAsync(HistoryPageNumber + 1, cancellationToken);

    private async Task LoadHistoryPageAsync(int pageNumber, CancellationToken cancellationToken)
    {
        if (IsClosed || IsBusy) return;
        if (pageNumber < 1) throw new ArgumentOutOfRangeException(nameof(pageNumber));
        var requestGeneration = Volatile.Read(ref batchSelectionGeneration);
        using var operation = CreateOperation(cancellationToken);
        IsBusy = true;
        try
        {
            var page = await store.GetBatchesAsync(new PageQuery(pageNumber, historyPageSize), operation.Token)
                .ConfigureAwait(true);
            operation.Token.ThrowIfCancellationRequested();
            if (!IsHistoryRequestCurrent(requestGeneration)) return;
            var target = page.Items.FirstOrDefault();
            var snapshot = target is null
                ? null
                : await store.GetBatchSnapshotAsync(target.Id, operation.Token).ConfigureAwait(true);
            operation.Token.ThrowIfCancellationRequested();
            if (!IsHistoryRequestCurrent(requestGeneration)) return;
            Batches.Clear();
            foreach (var batch in page.Items.Take(historyPageSize)) Batches.Add(batch);
            historyPageNumber = page.PageNumber;
            historyTotalCount = page.TotalCount;
            OnPropertyChanged(nameof(HistoryPageNumber));
            OnPropertyChanged(nameof(HistoryPageText));
            if (snapshot is not null)
            {
                var rows = await BuildResultRowsAsync(
                    snapshot.Batch.Id, snapshot.Operations, operation.Token).ConfigureAwait(true);
                operation.Token.ThrowIfCancellationRequested();
                if (!IsHistoryRequestCurrent(requestGeneration)) return;
                UpdateBatchInHistory(snapshot.Batch);
                SelectedBatch = snapshot.Batch;
                ReplaceOperations(snapshot.Batch.Id, snapshot.Operations);
                ReplaceResultRows(rows);
                StatusMessage = "已加载最近的分享任务。";
            }
            else
            {
                SelectedBatch = null;
                ReplaceOperations(null, []);
                StatusMessage = "尚未开始分享。";
            }
            NotifyCommandState();
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
        catch (Exception)
        {
            if (IsHistoryRequestCurrent(requestGeneration))
                StatusMessage = "暂时无法加载分享记录，请稍后重试。";
        }
        finally
        {
            if (!IsClosed) IsBusy = false;
        }
    }

    public async Task ResetPendingDraftAsync(CancellationToken cancellationToken = default)
    {
        if (!CanResetPendingDraft || pendingBatch is not { } envelope) return;
        using var operation = CreateOperation(cancellationToken);
        IsBusy = true;
        try
        {
            var receipt = await store.GetBatchCreationAsync(
                    envelope.Batch.Id, envelope.DraftFingerprint, operation.Token)
                .ConfigureAwait(true);
            if (receipt is not null)
            {
                await ApplyCreationReceiptAsync(envelope, receipt).ConfigureAwait(true);
                return;
            }

            if (!ReferenceEquals(pendingBatch, envelope)) return;
            pendingBatch = null;
            StatusMessage = "未发现已创建任务，可以修改后重新开始。";
            NotifyPendingBatchState();
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
        catch (Exception)
        {
            StatusMessage = "暂时无法确认上次结果，请稍后重试。";
        }
        finally
        {
            if (!IsClosed) IsBusy = false;
        }
    }

    public Task LoadSelectedBatchAsync(CancellationToken cancellationToken = default) =>
        SelectedBatch is null ? Task.CompletedTask : LoadBatchOperationsAsync(SelectedBatch.Id, cancellationToken);

    public Task PauseAsync(CancellationToken cancellationToken = default) =>
        !CanPause ? Task.CompletedTask : ChangeBatchAsync(
            (batch, token) => store.TryPauseBatchAsync(
                batch.Id, batch.Revision, timeProvider.GetUtcNow(), token),
            "分享任务已暂停。",
            cancellationToken);

    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        if (!CanResume || !await IsRemoteOperationAuthorizedAsync(cancellationToken).ConfigureAwait(true)) return;
        await ChangeBatchAsync(
            (batch, token) => store.TryResumeBatchAsync(
                batch.Id, batch.Revision, timeProvider.GetUtcNow(), token),
            "分享任务已继续。",
            cancellationToken,
            notifyExecution: true).ConfigureAwait(true);
    }

    public async Task RetryFailedAsync(CancellationToken cancellationToken = default)
    {
        if (!CanRetryFailed || SelectedOperation is not { } failed) return;
        if (!await IsRemoteOperationAuthorizedAsync(cancellationToken).ConfigureAwait(true)) return;
        var targetBatchId = SelectedBatch!.Id;
        var batchGeneration = Volatile.Read(ref batchSelectionGeneration);
        var selectedOperationGeneration = Volatile.Read(ref operationSelectionGeneration);
        using var operation = CreateOperation(cancellationToken);
        IsBusy = true;
        try
        {
            var result = await store.TryRetryFailedOperationAsync(
                failed.Id,
                failed.Generation,
                failed.Revision,
                timeProvider.GetUtcNow(),
                operation.Token).ConfigureAwait(true);
            if (!IsOperationRequestCurrent(
                    targetBatchId, failed.Id, batchGeneration, selectedOperationGeneration)) return;
            if (result.Disposition == StandaloneShareMutationDisposition.Applied)
            {
                var snapshot = await store.GetBatchSnapshotAsync(targetBatchId, operation.Token).ConfigureAwait(true);
                if (!IsOperationRequestCurrent(
                        targetBatchId, failed.Id, batchGeneration, selectedOperationGeneration)) return;
                if (snapshot is null)
                {
                    StatusMessage = "记录已变化，请刷新后再试。";
                    return;
                }
                UpdateBatchInHistory(snapshot.Batch);
                SelectedBatch = snapshot.Batch;
                ReplaceOperations(targetBatchId, snapshot.Operations);
                batchService?.NotifyExistingBatch(targetBatchId);
            }
            else if (result.Value is not null) ReplaceOperation(result.Value);
            StatusMessage = result.Disposition switch
            {
                StandaloneShareMutationDisposition.Applied => "失败项已安全重新排队。",
                StandaloneShareMutationDisposition.Conflict => "记录已变化，请刷新后再试。",
                StandaloneShareMutationDisposition.NotFound => "没有找到这条分享记录。",
                _ => "这条记录不能安全重试。",
            };
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
        catch (Exception)
        {
            if (IsOperationRequestCurrent(
                    targetBatchId, failed.Id, batchGeneration, selectedOperationGeneration))
                StatusMessage = "重试失败，请稍后再试。";
        }
        finally
        {
            if (!IsClosed) IsBusy = false;
        }
    }

    private async Task<bool> IsRemoteOperationAuthorizedAsync(CancellationToken cancellationToken)
    {
        if (licenseGate is null) return true;
        var result = await licenseGate
            .AuthorizeAsync(LicenseOperationKind.Share, cancellationToken)
            .ConfigureAwait(true);
        if (result.IsAuthorized) return true;
        StatusMessage = "软件尚未激活，联网功能暂不可用。";
        return false;
    }

    public async Task SaveResultsAsync(CancellationToken cancellationToken = default)
    {
        if (!CanSaveResults || SelectedBatch is not { } batch || localResultExports is null) return;
        var requestGeneration = Volatile.Read(ref batchSelectionGeneration);
        using var operation = CreateOperation(cancellationToken);
        saveResultsCancellation = operation;
        OnPropertyChanged(nameof(CanCancelSaveResults));
        CancelSaveResultsCommand.NotifyCanExecuteChanged();
        IsBusy = true;
        try
        {
            var result = await localResultExports.ExportStandaloneAsync(batch.Id, operation.Token)
                .ConfigureAwait(true);
            if (!IsBatchRequestCurrent(batch.Id, requestGeneration)) return;
            var rows = await BuildResultRowsAsync(batch.Id, Operations.ToArray(), operation.Token)
                .ConfigureAwait(true);
            operation.Token.ThrowIfCancellationRequested();
            if (!IsBatchRequestCurrent(batch.Id, requestGeneration)) return;
            ReplaceResultRows(rows);
            StatusMessage = result.Disposition switch
            {
                LocalResultExportDisposition.Exported or LocalResultExportDisposition.AlreadySucceeded =>
                    "分享结果已保存。",
                LocalResultExportDisposition.Busy => "分享结果正在保存，请稍后查看。",
                LocalResultExportDisposition.NotReady => "任务尚未完成，暂时不能保存结果。",
                LocalResultExportDisposition.Conflict => "保存状态已变化，请稍后重试。",
                _ => "保存结果失败，请稍后重试。",
            };
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            if (!IsClosed) StatusMessage = "已取消保存结果。";
        }
        catch (Exception)
        {
            if (IsBatchRequestCurrent(batch.Id, requestGeneration))
                StatusMessage = "保存结果失败，请稍后重试。";
        }
        finally
        {
            if (ReferenceEquals(saveResultsCancellation, operation))
            {
                saveResultsCancellation = null;
                OnPropertyChanged(nameof(CanCancelSaveResults));
                CancelSaveResultsCommand.NotifyCanExecuteChanged();
            }
            if (!IsClosed) IsBusy = false;
        }
    }

    private void CancelSaveResults() => saveResultsCancellation?.Cancel();

    public void Close()
    {
        if (isClosed) return;
        isClosed = true;
        lifetime.Cancel();
        ((INotifyCollectionChanged)Logs).CollectionChanged -= OnLogsChanged;
        OnPropertyChanged(nameof(IsClosed));
        NotifyCommandState();
    }

    public void Dispose()
    {
        Close();
        lifetime.Dispose();
    }

    private void ClearLogs()
    {
        if (!CanClearLogs) return;
        logBuffer.Clear();
        if (!SetProperty(ref statusMessage, "运行日志已清空。", nameof(StatusMessage)))
            OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(UserFacingStatusMessage));
    }

    private void OnLogsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(CanClearLogs));
        ClearLogsCommand.NotifyCanExecuteChanged();
    }

    private async Task LoadBatchOperationsAsync(Guid batchId, CancellationToken cancellationToken)
    {
        if (IsClosed || IsBusy) return;
        var requestGeneration = Volatile.Read(ref batchSelectionGeneration);
        using var operation = CreateOperation(cancellationToken);
        IsBusy = true;
        try
        {
            var snapshot = await store.GetBatchSnapshotAsync(batchId, operation.Token).ConfigureAwait(true);
            operation.Token.ThrowIfCancellationRequested();
            if (!IsBatchRequestCurrent(batchId, requestGeneration)) return;
            if (snapshot is null)
            {
                StatusMessage = "没有找到所选分享任务。";
                return;
            }
            var rows = await BuildResultRowsAsync(batchId, snapshot.Operations, operation.Token)
                .ConfigureAwait(true);
            operation.Token.ThrowIfCancellationRequested();
            if (!IsBatchRequestCurrent(batchId, requestGeneration)) return;
            UpdateBatchInHistory(snapshot.Batch);
            SelectedBatch = snapshot.Batch;
            ReplaceOperations(batchId, snapshot.Operations);
            ReplaceResultRows(rows);
            StatusMessage = "已加载所选分享任务。";
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
        catch (Exception)
        {
            if (IsBatchRequestCurrent(batchId, requestGeneration))
                StatusMessage = "暂时无法加载任务明细。";
        }
        finally
        {
            if (!IsClosed) IsBusy = false;
        }
    }

    private async Task ChangeBatchAsync(
        Func<StandaloneShareBatchRecord, CancellationToken,
            Task<StandaloneShareMutationResult<StandaloneShareBatchRecord>>> change,
        string successMessage,
        CancellationToken cancellationToken,
        bool notifyExecution = false)
    {
        if (SelectedBatch is not { } batch) return;
        var requestGeneration = Volatile.Read(ref batchSelectionGeneration);
        using var operation = CreateOperation(cancellationToken);
        IsBusy = true;
        try
        {
            var result = await change(batch, operation.Token).ConfigureAwait(true);
            if (!IsBatchRequestCurrent(batch.Id, requestGeneration)) return;
            if (result.Value is not null) InsertOrReplaceBatch(result.Value, insertAtFront: false);
            if (notifyExecution && result.Disposition == StandaloneShareMutationDisposition.Applied)
                batchService?.NotifyExistingBatch(batch.Id);
            StatusMessage = result.Disposition switch
            {
                StandaloneShareMutationDisposition.Applied => successMessage,
                StandaloneShareMutationDisposition.Conflict => "任务状态已变化，请刷新后再试。",
                StandaloneShareMutationDisposition.NotFound => "没有找到所选分享任务。",
                _ => "当前状态不能执行这个操作。",
            };
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
        catch (Exception)
        {
            if (IsBatchRequestCurrent(batch.Id, requestGeneration))
                StatusMessage = "操作失败，请稍后再试。";
        }
        finally
        {
            if (!IsClosed) IsBusy = false;
        }
    }

    private void AddVerified(OwnedRemoteSelection selection, string accountDisplayName)
    {
        var group = SelectionGroups.FirstOrDefault(item => item.AccountId == selection.AccountId);
        if (group is null)
        {
            group = new ShareSelectionGroup(selection.AccountId, accountDisplayName);
            selectionGroups.Add(group);
        }
        if (group.Contains(selection)) return;
        group.Add(selection);
        NotifySelectionState();
    }

    private void Remove(OwnedRemoteSelection? selection)
    {
        if (selection is null || !CanEditSelection) return;
        var group = SelectionGroups.FirstOrDefault(item => item.AccountId == selection.AccountId);
        if (group is null || !group.Remove(selection)) return;
        if (group.Selections.Count == 0) selectionGroups.Remove(group);
        NotifySelectionState();
        StatusMessage = SelectedResourceCount == 0
            ? "请选择网盘资源。"
            : $"已选择 {SelectedResourceCount} 项资源。";
    }

    private void ReplaceOperations(
        Guid? batchId,
        IEnumerable<StandaloneShareOperationRecord> operations)
    {
        loadedBatchId = batchId is { } id && SelectedBatch?.Id == id ? id : null;
        Operations.Clear();
        foreach (var operation in operations.OrderBy(item => item.SequenceNumber)) Operations.Add(operation);
        SelectedOperation = Operations.FirstOrDefault();
        ReplaceResultRows(BuildResultRows(Operations, [], string.Empty));
        OnPropertyChanged(nameof(CanSaveResults));
        SaveResultsCommand.NotifyCanExecuteChanged();
    }

    private void ClearLoadedDetails()
    {
        loadedBatchId = null;
        Operations.Clear();
        ResultRows.Clear();
        SelectedOperation = null;
        OnPropertyChanged(nameof(HasResultRows));
    }

    private void ReplaceResultRows(IEnumerable<ShareResultDisplayRow> rows)
    {
        ResultRows.Clear();
        foreach (var row in rows) ResultRows.Add(row);
        OnPropertyChanged(nameof(HasResultRows));
    }

    private void ReplaceOperation(StandaloneShareOperationRecord updated)
    {
        var index = Operations.IndexOf(Operations.FirstOrDefault(item => item.Id == updated.Id)!);
        if (index >= 0) Operations[index] = updated;
        SelectedOperation = updated;
    }

    private void InsertOrReplaceBatch(StandaloneShareBatchRecord updated, bool insertAtFront)
    {
        var existing = Batches.FirstOrDefault(item => item.Id == updated.Id);
        if (existing is not null)
        {
            var index = Batches.IndexOf(existing);
            Batches[index] = updated;
        }
        else if (insertAtFront)
        {
            Batches.Insert(0, updated);
            while (Batches.Count > historyPageSize) Batches.RemoveAt(Batches.Count - 1);
        }
        else
        {
            Batches.Add(updated);
        }
        SelectedBatch = updated;
    }

    private void UpdateBatchInHistory(StandaloneShareBatchRecord updated)
    {
        var existing = Batches.FirstOrDefault(item => item.Id == updated.Id);
        if (existing is null)
        {
            Batches.Add(updated);
            return;
        }
        Batches[Batches.IndexOf(existing)] = updated;
    }

    private bool IsHistoryRequestCurrent(long requestGeneration) =>
        !IsClosed && Volatile.Read(ref batchSelectionGeneration) == requestGeneration;

    private bool IsBatchRequestCurrent(Guid batchId, long requestGeneration) =>
        IsHistoryRequestCurrent(requestGeneration) && SelectedBatch?.Id == batchId;

    private bool IsOperationRequestCurrent(
        Guid batchId,
        Guid operationId,
        long batchGeneration,
        long selectedOperationGeneration) =>
        IsBatchRequestCurrent(batchId, batchGeneration) &&
        Volatile.Read(ref operationSelectionGeneration) == selectedOperationGeneration &&
        SelectedOperation?.Id == operationId;

    private CancellationTokenSource CreateOperation(CancellationToken cancellationToken) =>
        CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);

    private void NotifySelectionState()
    {
        RebuildPendingRows();
        OnPropertyChanged(nameof(SelectedResourceCount));
        OnPropertyChanged(nameof(CanStart));
        StartCommand?.NotifyCanExecuteChanged();
    }

    public bool HasPendingRows => PendingRows.Count > 0;
    public bool HasResultRows => ResultRows.Count > 0;

    private void RebuildPendingRows()
    {
        pendingRows.Clear();
        foreach (var group in SelectionGroups)
        {
            var currentAccount = Accounts.FirstOrDefault(account => account.AccountId == group.AccountId);
            foreach (var selection in group.Selections)
            {
                pendingRows.Add(new SharePendingDisplayRow(
                    selection,
                    selection.DisplayName,
                    selection.ParentPath.Value,
                    currentAccount?.DisplayName ?? group.AccountDisplayName,
                    SelectedExistingShareDecision.DisplayName,
                    currentAccount is not null ? "准备就绪" : "账号不可用"));
            }
        }
        OnPropertyChanged(nameof(HasPendingRows));
    }

    private async Task<IReadOnlyList<ShareResultDisplayRow>> BuildResultRowsAsync(
        Guid batchId,
        IReadOnlyList<StandaloneShareOperationRecord> operations,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<StandaloneShareResultProjection> projections = [];
        if (resultProjectionRepository is not null)
        {
            try
            {
                const int maximumResultRows = 10_000;
                if (operations.Count > maximumResultRows)
                    throw new InvalidDataException("The share result exceeds the display bound.");

                var orderedOperations = operations.OrderBy(item => item.SequenceNumber).ToArray();
                var operationIds = new HashSet<Guid>();
                for (var index = 0; index < orderedOperations.Length; index++)
                {
                    if (orderedOperations[index].SequenceNumber != index + 1 ||
                        !operationIds.Add(orderedOperations[index].Id))
                    {
                        throw new InvalidDataException("The loaded share operations are not contiguous.");
                    }
                }

                var loaded = new List<StandaloneShareResultProjection>(
                    Math.Min(operations.Count, maximumResultRows));
                var afterSequenceNumber = 0;
                var visitedCursors = new HashSet<int> { afterSequenceNumber };
                const int pageSize = 1_000;
                while (true)
                {
                    var page = await resultProjectionRepository.GetPageAsync(
                        batchId, afterSequenceNumber, pageSize, cancellationToken).ConfigureAwait(true);
                    ArgumentNullException.ThrowIfNull(page);
                    ArgumentNullException.ThrowIfNull(page.Items);
                    if (page.Items.Count == 0)
                    {
                        if (page.NextSequenceNumber is not null)
                            throw new InvalidDataException("The share result cursor advanced without rows.");
                        break;
                    }
                    if (page.Items.Count > pageSize || loaded.Count > maximumResultRows - page.Items.Count)
                        throw new InvalidDataException("The share result exceeds the display bound.");

                    var previousSequenceNumber = afterSequenceNumber;
                    foreach (var item in page.Items)
                    {
                        if (item.SequenceNumber != previousSequenceNumber + 1)
                            throw new InvalidDataException("The share result page is not contiguous.");
                        previousSequenceNumber = item.SequenceNumber;
                        loaded.Add(item);
                    }

                    if (page.NextSequenceNumber is not { } next) break;
                    if (next != previousSequenceNumber || !visitedCursors.Add(next))
                        throw new InvalidDataException("The share result cursor does not match its page.");
                    afterSequenceNumber = next;
                }

                if (loaded.Count != orderedOperations.Length)
                    throw new InvalidDataException("The share result projection is incomplete.");
                for (var index = 0; index < orderedOperations.Length; index++)
                {
                    var operation = orderedOperations[index];
                    var projection = loaded[index];
                    if (projection.OperationId != operation.Id ||
                        projection.SequenceNumber != operation.SequenceNumber ||
                        projection.Status != operation.Status)
                    {
                        throw new InvalidDataException("The share result projection does not match its operation.");
                    }
                }
                projections = loaded;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                projections = [];
                logBuffer.AppendSafe(
                    "分享结果暂时无法完整显示，请稍后刷新。",
                    WorkspaceLogLevel.Warning);
            }
        }

        var saveStatus = string.Empty;
        if (localResultExportStore is not null)
        {
            try
            {
                var record = await localResultExportStore.GetAsync(
                    new LocalResultExportKey(
                        LocalResultOwnerKind.StandaloneShareBatch,
                        batchId,
                        LocalResultExportKind.StandaloneShareWorkbook,
                        1),
                    cancellationToken).ConfigureAwait(true);
                saveStatus = MapExportStatus(record?.Status);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                saveStatus = string.Empty;
            }
        }
        return BuildResultRows(operations, projections, saveStatus);
    }

    private IReadOnlyList<ShareResultDisplayRow> BuildResultRows(
        IEnumerable<StandaloneShareOperationRecord> operations,
        IEnumerable<StandaloneShareResultProjection> projections,
        string saveStatus)
    {
        var byOperation = projections
            .GroupBy(item => (item.OperationId, item.SequenceNumber))
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single());
        return operations.OrderBy(item => item.SequenceNumber).Select(operation =>
        {
            byOperation.TryGetValue((operation.Id, operation.SequenceNumber), out var projection);
            var exposesCurrentResult = operation.Status == StandaloneShareOperationStatus.Succeeded &&
                projection?.Status == StandaloneShareOperationStatus.Succeeded;
            var accountName = Accounts.FirstOrDefault(account => account.AccountId == operation.AccountId)?.DisplayName
                ?? projection?.AccountDisplayName
                ?? "账号不可用";
            return new ShareResultDisplayRow(
                operation,
                operation.DisplayName,
                accountName,
                MapOperationStatus(operation.Status),
                exposesCurrentResult ? projection!.ShareUrl : null,
                exposesCurrentResult ? projection!.ExtractionCode : null,
                operation.UpdatedAt,
                saveStatus);
        }).ToArray();
    }

    private static string MapOperationStatus(StandaloneShareOperationStatus status) => status switch
    {
        StandaloneShareOperationStatus.Queued => "等待分享",
        StandaloneShareOperationStatus.Reserved or
        StandaloneShareOperationStatus.Preparing or
        StandaloneShareOperationStatus.Submitting => "正在分享",
        StandaloneShareOperationStatus.ResultPending => "等待确认",
        StandaloneShareOperationStatus.NeedsDecision => "等待选择",
        StandaloneShareOperationStatus.Succeeded => "已完成",
        StandaloneShareOperationStatus.Failed => "失败",
        StandaloneShareOperationStatus.Paused => "已暂停",
        StandaloneShareOperationStatus.Cancelled => "已取消",
        _ => string.Empty,
    };

    private static string MapExportStatus(LocalResultExportStatus? status) => status switch
    {
        null => "尚未保存",
        LocalResultExportStatus.Pending => "等待保存",
        LocalResultExportStatus.InProgress => "正在保存",
        LocalResultExportStatus.Succeeded => "已保存",
        LocalResultExportStatus.Failed => "保存失败",
        _ => string.Empty,
    };

    private static string MapUserFacingStatus(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return string.Empty;
        if (ContainsAny(message, "adapter", "helper", "ManagedChromium", "fingerprint", "指纹", "lease", "租约", "错误码"))
            return "分享状态已更新，请稍后查看。";
        return WorkspaceLogBuffer.SanitizeForEntry(message);
    }

    private static bool ContainsAny(string message, params string[] values) =>
        values.Any(value => message.Contains(value, StringComparison.OrdinalIgnoreCase));

    private void NotifyPendingBatchState()
    {
        OnPropertyChanged(nameof(HasPendingBatchConfirmation));
        OnPropertyChanged(nameof(CanEditSelection));
        OnPropertyChanged(nameof(CanResetPendingDraft));
        OnPropertyChanged(nameof(CanUseAdvancedSettings));
        OnPropertyChanged(nameof(CanSelectHistory));
        NotifyCommandState();
    }

    private async Task ApplyCreationReceiptAsync(
        StandaloneSharePendingBatchEnvelope expected,
        StandaloneShareBatchCreationReceipt receipt)
    {
        if (receipt.Batch.Id != expected.Batch.Id ||
            !string.Equals(receipt.DraftFingerprint, expected.DraftFingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The batch creation receipt does not match the pending draft.");
        }

        var page = await store.GetBatchesAsync(
            new PageQuery(1, historyPageSize), CancellationToken.None).ConfigureAwait(true);
        var authoritativeBatch = page.Items.FirstOrDefault(item => item.Id == receipt.Batch.Id)
            ?? throw new InvalidOperationException("The created batch is missing from the newest history page.");

        pendingBatch = null;
        selectionGroups.Clear();
        Batches.Clear();
        foreach (var batch in page.Items.Take(historyPageSize)) Batches.Add(batch);
        historyPageNumber = page.PageNumber;
        historyTotalCount = page.TotalCount;
        OnPropertyChanged(nameof(HistoryPageNumber));
        OnPropertyChanged(nameof(HistoryPageText));
        SelectedBatch = authoritativeBatch;
        ReplaceOperations(authoritativeBatch.Id, receipt.Operations);
        StatusMessage = "分享任务已加入队列。";
        NotifySelectionState();
        NotifyPendingBatchState();
    }

    private void NotifyCommandState()
    {
        OnPropertyChanged(nameof(CanPickResources));
        OnPropertyChanged(nameof(CanEditSelection));
        OnPropertyChanged(nameof(CanResetPendingDraft));
        OnPropertyChanged(nameof(CanUseAdvancedSettings));
        OnPropertyChanged(nameof(CanSelectHistory));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanUseBatchControls));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(CanRetryFailed));
        OnPropertyChanged(nameof(CanSaveResults));
        OnPropertyChanged(nameof(CanCancelSaveResults));
        OnPropertyChanged(nameof(CanLoadPreviousHistoryPage));
        OnPropertyChanged(nameof(CanLoadNextHistoryPage));
        OnPropertyChanged(nameof(CanClearLogs));
        PickResourcesCommand?.NotifyCanExecuteChanged();
        ChooseResultDirectoryCommand?.NotifyCanExecuteChanged();
        StartCommand?.NotifyCanExecuteChanged();
        ResetPendingDraftCommand?.NotifyCanExecuteChanged();
        RemoveSelectionCommand?.NotifyCanExecuteChanged();
        LoadSelectedBatchCommand?.NotifyCanExecuteChanged();
        PauseCommand?.NotifyCanExecuteChanged();
        ResumeCommand?.NotifyCanExecuteChanged();
        RetryFailedCommand?.NotifyCanExecuteChanged();
        SaveResultsCommand?.NotifyCanExecuteChanged();
        CancelSaveResultsCommand?.NotifyCanExecuteChanged();
        PreviousHistoryPageCommand?.NotifyCanExecuteChanged();
        NextHistoryPageCommand?.NotifyCanExecuteChanged();
        ClearLogsCommand?.NotifyCanExecuteChanged();
    }
}

public sealed class UnavailableRemoteResourcePicker : IRemoteResourcePicker
{
    public Task<IReadOnlyList<OwnedRemoteSelection>> PickAsync(
        Guid accountId,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("网盘资源选择功能尚未准备好。");
}

public sealed class UnavailableStandaloneShareStore : IStandaloneShareStore
{
    public Task<StandaloneShareBatchCreationReceipt> CreateOrGetBatchAsync(
        StandaloneSharePendingBatchEnvelope envelope,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("分享记录功能尚未准备好。");

    public Task<StandaloneShareBatchCreationReceipt?> GetBatchCreationAsync(
        Guid batchId,
        string draftFingerprint,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<StandaloneShareBatchCreationReceipt?>(null);

    public Task<Page<StandaloneShareBatchRecord>> GetBatchesAsync(
        PageQuery query,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new Page<StandaloneShareBatchRecord>([], 0, query.PageNumber, query.PageSize));

    public Task<IReadOnlyList<StandaloneShareOperationRecord>> GetOperationsAsync(
        Guid batchId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<StandaloneShareOperationRecord>>([]);

    public Task<StandaloneShareBatchSnapshot?> GetBatchSnapshotAsync(
        Guid batchId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<StandaloneShareBatchSnapshot?>(null);

    public Task<StandaloneShareMutationResult<StandaloneShareBatchRecord>> TryPauseBatchAsync(
        Guid batchId, long expectedRevision, DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default) => BatchUnavailable();

    public Task<StandaloneShareMutationResult<StandaloneShareBatchRecord>> TryResumeBatchAsync(
        Guid batchId, long expectedRevision, DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default) => BatchUnavailable();

    public Task<StandaloneShareMutationResult<StandaloneShareOperationRecord>> TryRetryFailedOperationAsync(
        Guid operationId, int expectedGeneration, long expectedRevision, DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new StandaloneShareMutationResult<StandaloneShareOperationRecord>(
            StandaloneShareMutationDisposition.InvalidState, null));

    private static Task<StandaloneShareMutationResult<StandaloneShareBatchRecord>> BatchUnavailable() =>
        Task.FromResult(new StandaloneShareMutationResult<StandaloneShareBatchRecord>(
            StandaloneShareMutationDisposition.InvalidState, null));
}
