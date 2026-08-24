using System.Collections.ObjectModel;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BaiduBatchTransfer.App.Features.Shares;

public sealed record ShareAccountOption(Guid AccountId, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed class ShareSelectionGroup(
    Guid accountId,
    string accountDisplayName) : ObservableObject
{
    public Guid AccountId { get; } = accountId;
    public string AccountDisplayName { get; } = accountDisplayName;
    public ObservableCollection<OwnedRemoteSelection> Selections { get; } = [];
}

public sealed class ShareWorkspaceViewModel : ObservableObject, IDisposable
{
    private readonly IRemoteResourcePicker resourcePicker;
    private readonly IStandaloneShareStore store;
    private readonly IResultSaveDialog resultDialog;
    private readonly TimeProvider timeProvider;
    private readonly int historyPageSize;
    private readonly CancellationTokenSource lifetime = new();
    private ShareAccountOption? activeAccount;
    private string? resultDirectory;
    private StandaloneShareBatchRecord? selectedBatch;
    private StandaloneShareOperationRecord? selectedOperation;
    private string statusMessage = "请选择账号和网盘资源。";
    private bool isBusy;
    private bool isClosed;

    public ShareWorkspaceViewModel(
        IReadOnlyList<ShareAccountOption> accounts,
        IRemoteResourcePicker resourcePicker,
        IStandaloneShareStore store,
        IResultSaveDialog resultDialog,
        TimeProvider? timeProvider = null,
        int historyPageSize = 20)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        this.resourcePicker = resourcePicker ?? throw new ArgumentNullException(nameof(resourcePicker));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.resultDialog = resultDialog ?? throw new ArgumentNullException(nameof(resultDialog));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        if (historyPageSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(historyPageSize));
        this.historyPageSize = historyPageSize;

        Accounts = [];
        SelectionGroups = [];
        Batches = [];
        Operations = [];
        ReloadAccounts(accounts);

        PickResourcesCommand = new AsyncRelayCommand(PickResourcesAsync, () => CanPickResources);
        ChooseResultDirectoryCommand = new AsyncRelayCommand(
            ChooseResultDirectoryAsync,
            () => !IsBusy && !IsClosed);
        StartCommand = new AsyncRelayCommand(StartAsync, () => CanStart);
        RemoveSelectionCommand = new RelayCommand<OwnedRemoteSelection>(Remove, _ => !IsBusy && !IsClosed);
        LoadSelectedBatchCommand = new AsyncRelayCommand(LoadSelectedBatchAsync, () => CanUseBatchControls);
        PauseCommand = new AsyncRelayCommand(PauseAsync, () => CanPause);
        ResumeCommand = new AsyncRelayCommand(ResumeAsync, () => CanResume);
        RetryFailedCommand = new AsyncRelayCommand(RetryFailedAsync, () => CanRetryFailed);
    }

    public ObservableCollection<ShareAccountOption> Accounts { get; }
    public ObservableCollection<ShareSelectionGroup> SelectionGroups { get; }
    public ObservableCollection<StandaloneShareBatchRecord> Batches { get; }
    public ObservableCollection<StandaloneShareOperationRecord> Operations { get; }

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
            if (!SetProperty(ref resultDirectory, normalized)) return;
            NotifyCommandState();
        }
    }

    public StandaloneShareBatchRecord? SelectedBatch
    {
        get => selectedBatch;
        set
        {
            if (!SetProperty(ref selectedBatch, value)) return;
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
            NotifyCommandState();
        }
    }

    public string StatusMessage
    {
        get => statusMessage;
        private set => SetProperty(ref statusMessage, value);
    }

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
    public int SelectedResourceCount => SelectionGroups.Sum(group => group.Selections.Count);
    public bool CanPickResources => !IsClosed && !IsBusy && ActiveAccount is not null;
    public bool CanStart => !IsClosed && !IsBusy && ActiveAccount is not null &&
        SelectedResourceCount > 0 && !string.IsNullOrWhiteSpace(ResultDirectory) &&
        SelectionGroups.All(group => Accounts.Any(account => account.AccountId == group.AccountId));
    public bool CanUseBatchControls => !IsClosed && !IsBusy && SelectedBatch is not null;
    public bool CanPause => CanUseBatchControls &&
        SelectedBatch!.Status is StandaloneShareBatchStatus.Queued or StandaloneShareBatchStatus.Active;
    public bool CanResume => CanUseBatchControls &&
        SelectedBatch!.Status == StandaloneShareBatchStatus.Paused;
    public bool CanRetryFailed => CanUseBatchControls &&
        SelectedOperation is { CanSafelyRetry: true } operation &&
        operation.BatchId == SelectedBatch!.Id;

    public IAsyncRelayCommand PickResourcesCommand { get; }
    public IAsyncRelayCommand ChooseResultDirectoryCommand { get; }
    public IAsyncRelayCommand StartCommand { get; }
    public IRelayCommand<OwnedRemoteSelection> RemoveSelectionCommand { get; }
    public IAsyncRelayCommand LoadSelectedBatchCommand { get; }
    public IAsyncRelayCommand PauseCommand { get; }
    public IAsyncRelayCommand ResumeCommand { get; }
    public IAsyncRelayCommand RetryFailedCommand { get; }

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
        if (IsClosed || IsBusy) return;
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
        var selections = SelectionGroups.SelectMany(group => group.Selections).ToArray();
        var directory = ResultDirectory!;
        using var operation = CreateOperation(cancellationToken);
        IsBusy = true;
        try
        {
            var now = timeProvider.GetUtcNow();
            var batchId = Guid.NewGuid();
            var batch = StandaloneShareBatchRecord.Create(batchId, directory, selections.Length, now);
            var shareOperations = selections
                .Select((selection, index) => StandaloneShareOperationRecord.Create(
                    Guid.NewGuid(), batchId, selection, index + 1, now))
                .ToArray();
            await store.CreateBatchAsync(batch, shareOperations, operation.Token).ConfigureAwait(true);

            SelectionGroups.Clear();
            InsertOrReplaceBatch(batch, insertAtFront: true);
            SelectedBatch = batch;
            ReplaceOperations(shareOperations);
            StatusMessage = "分享任务已加入队列。";
            NotifySelectionState();
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            StatusMessage = "已取消开始分享。";
        }
        catch (Exception)
        {
            StatusMessage = "暂时无法开始分享，请稍后重试。";
        }
        finally
        {
            if (!IsClosed) IsBusy = false;
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (IsClosed || IsBusy) return;
        using var operation = CreateOperation(cancellationToken);
        IsBusy = true;
        try
        {
            var page = await store.GetBatchesAsync(new PageQuery(1, historyPageSize), operation.Token)
                .ConfigureAwait(true);
            operation.Token.ThrowIfCancellationRequested();
            Batches.Clear();
            foreach (var batch in page.Items.Take(historyPageSize)) Batches.Add(batch);
            SelectedBatch = Batches.FirstOrDefault();
            if (SelectedBatch is not null)
            {
                var batchOperations = await store.GetOperationsAsync(SelectedBatch.Id, operation.Token)
                    .ConfigureAwait(true);
                operation.Token.ThrowIfCancellationRequested();
                ReplaceOperations(batchOperations);
                StatusMessage = "已加载最近的分享任务。";
            }
            else
            {
                ReplaceOperations([]);
                StatusMessage = "尚未开始分享。";
            }
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
        catch (Exception)
        {
            StatusMessage = "暂时无法加载分享记录，请稍后重试。";
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

    public Task ResumeAsync(CancellationToken cancellationToken = default) =>
        !CanResume ? Task.CompletedTask : ChangeBatchAsync(
            (batch, token) => store.TryResumeBatchAsync(
                batch.Id, batch.Revision, timeProvider.GetUtcNow(), token),
            "分享任务已继续。",
            cancellationToken);

    public async Task RetryFailedAsync(CancellationToken cancellationToken = default)
    {
        if (!CanRetryFailed || SelectedOperation is not { } failed) return;
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
            if (result.Value is not null) ReplaceOperation(result.Value);
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
            StatusMessage = "重试失败，请稍后再试。";
        }
        finally
        {
            if (!IsClosed) IsBusy = false;
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
        lifetime.Dispose();
    }

    private async Task LoadBatchOperationsAsync(Guid batchId, CancellationToken cancellationToken)
    {
        if (IsClosed || IsBusy) return;
        using var operation = CreateOperation(cancellationToken);
        IsBusy = true;
        try
        {
            var items = await store.GetOperationsAsync(batchId, operation.Token).ConfigureAwait(true);
            operation.Token.ThrowIfCancellationRequested();
            if (SelectedBatch?.Id != batchId) return;
            ReplaceOperations(items);
            StatusMessage = "已加载所选分享任务。";
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
        catch (Exception)
        {
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
        CancellationToken cancellationToken)
    {
        if (SelectedBatch is not { } batch) return;
        using var operation = CreateOperation(cancellationToken);
        IsBusy = true;
        try
        {
            var result = await change(batch, operation.Token).ConfigureAwait(true);
            if (result.Value is not null) InsertOrReplaceBatch(result.Value, insertAtFront: false);
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
            SelectionGroups.Add(group);
        }
        if (group.Selections.Any(existing => existing.HasSameExecutionIdentity(selection))) return;
        group.Selections.Add(selection);
        NotifySelectionState();
    }

    private void Remove(OwnedRemoteSelection? selection)
    {
        if (selection is null || IsBusy || IsClosed) return;
        var group = SelectionGroups.FirstOrDefault(item => item.AccountId == selection.AccountId);
        if (group is null || !group.Selections.Remove(selection)) return;
        if (group.Selections.Count == 0) SelectionGroups.Remove(group);
        NotifySelectionState();
        StatusMessage = SelectedResourceCount == 0
            ? "请选择网盘资源。"
            : $"已选择 {SelectedResourceCount} 项资源。";
    }

    private void ReplaceOperations(IEnumerable<StandaloneShareOperationRecord> operations)
    {
        Operations.Clear();
        foreach (var operation in operations.OrderBy(item => item.SequenceNumber)) Operations.Add(operation);
        SelectedOperation = Operations.FirstOrDefault();
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

    private CancellationTokenSource CreateOperation(CancellationToken cancellationToken) =>
        CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);

    private void NotifySelectionState()
    {
        OnPropertyChanged(nameof(SelectedResourceCount));
        OnPropertyChanged(nameof(CanStart));
        StartCommand?.NotifyCanExecuteChanged();
    }

    private void NotifyCommandState()
    {
        OnPropertyChanged(nameof(CanPickResources));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanUseBatchControls));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(CanRetryFailed));
        PickResourcesCommand?.NotifyCanExecuteChanged();
        ChooseResultDirectoryCommand?.NotifyCanExecuteChanged();
        StartCommand?.NotifyCanExecuteChanged();
        RemoveSelectionCommand?.NotifyCanExecuteChanged();
        LoadSelectedBatchCommand?.NotifyCanExecuteChanged();
        PauseCommand?.NotifyCanExecuteChanged();
        ResumeCommand?.NotifyCanExecuteChanged();
        RetryFailedCommand?.NotifyCanExecuteChanged();
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
    public Task CreateBatchAsync(
        StandaloneShareBatchRecord batch,
        IReadOnlyList<StandaloneShareOperationRecord> operations,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("分享记录功能尚未准备好。");

    public Task<Page<StandaloneShareBatchRecord>> GetBatchesAsync(
        PageQuery query,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new Page<StandaloneShareBatchRecord>([], 0, query.PageNumber, query.PageSize));

    public Task<IReadOnlyList<StandaloneShareOperationRecord>> GetOperationsAsync(
        Guid batchId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<StandaloneShareOperationRecord>>([]);

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
