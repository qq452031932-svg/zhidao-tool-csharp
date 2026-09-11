using System.Collections.ObjectModel;
using BaiduBatchTransfer.Core.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BaiduBatchTransfer.App.Features.Transfers;

public sealed class TransferTargetDirectoryPickerViewModel : ObservableObject, IDisposable
{
    private readonly Guid accountId;
    private readonly ITransferTargetDirectorySource source;
    private readonly ITransferTargetDirectoryCreator? creator;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ObservableCollection<TransferTargetDirectoryEntry> directories = [];
    private CancellationTokenSource? navigationCancellation;
    private CancellationTokenSource? creationCancellation;
    private string currentPath;
    private string errorMessage = string.Empty;
    private bool isLoading;
    private bool isCreating;
    private bool isClosed;
    private long navigationGeneration;
    private long creationGeneration;
    private TransferTargetDirectoryEntry? selectedDirectory;

    public TransferTargetDirectoryPickerViewModel(
        Guid accountId,
        string initialPath,
        ITransferTargetDirectorySource source,
        ITransferTargetDirectoryCreator? creator = null)
    {
        if (accountId == Guid.Empty) throw new ArgumentException("必须指定账号。", nameof(accountId));
        this.accountId = accountId;
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.creator = creator;
        currentPath = new RemotePath(initialPath).Value;
        Directories = new ReadOnlyObservableCollection<TransferTargetDirectoryEntry>(directories);
        GoToParentCommand = new AsyncRelayCommand(GoToParentAsync, CanGoToParent);
        EnterSelectedDirectoryCommand = new AsyncRelayCommand(
            EnterSelectedDirectoryAsync,
            () => !IsBusy && !isClosed && SelectedDirectory is not null);
        SelectCurrentDirectoryCommand = new RelayCommand(
            () => Completed?.Invoke(this, CurrentPath),
            () => !IsBusy && !isClosed);
        CreateFolderCommand = new AsyncRelayCommand<string?>(
            name => CreateFolderAsync(name ?? string.Empty),
            _ => CanCreateFolder);
        CancelCommand = new RelayCommand(Cancel, () => !isClosed);
    }

    public event EventHandler<string?>? Completed;

    public ReadOnlyObservableCollection<TransferTargetDirectoryEntry> Directories { get; }
    public string CurrentPath
    {
        get => currentPath;
        private set
        {
            if (!SetProperty(ref currentPath, value)) return;
            OnPropertyChanged(nameof(IsAtRoot));
            GoToParentCommand.NotifyCanExecuteChanged();
        }
    }
    public bool IsAtRoot => string.Equals(CurrentPath, "/", StringComparison.Ordinal);
    public string ErrorMessage
    {
        get => errorMessage;
        private set => SetProperty(ref errorMessage, value);
    }
    public bool IsLoading
    {
        get => isLoading;
        private set
        {
            if (!SetProperty(ref isLoading, value)) return;
            GoToParentCommand.NotifyCanExecuteChanged();
            EnterSelectedDirectoryCommand.NotifyCanExecuteChanged();
            SelectCurrentDirectoryCommand.NotifyCanExecuteChanged();
            CreateFolderCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CanCreateFolder));
            OnPropertyChanged(nameof(CanBrowse));
        }
    }
    public bool IsCreating
    {
        get => isCreating;
        private set
        {
            if (!SetProperty(ref isCreating, value)) return;
            GoToParentCommand.NotifyCanExecuteChanged();
            EnterSelectedDirectoryCommand.NotifyCanExecuteChanged();
            SelectCurrentDirectoryCommand.NotifyCanExecuteChanged();
            CreateFolderCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CanCreateFolder));
            OnPropertyChanged(nameof(CanBrowse));
        }
    }
    public bool CanCreateFolder => creator is not null && !IsBusy && !isClosed;
    public bool CanBrowse => !IsBusy && !isClosed;
    public TransferTargetDirectoryEntry? SelectedDirectory
    {
        get => selectedDirectory;
        set
        {
            if (!SetProperty(ref selectedDirectory, value)) return;
            EnterSelectedDirectoryCommand.NotifyCanExecuteChanged();
        }
    }
    public IAsyncRelayCommand GoToParentCommand { get; }
    public IAsyncRelayCommand EnterSelectedDirectoryCommand { get; }
    public IRelayCommand SelectCurrentDirectoryCommand { get; }
    public IAsyncRelayCommand<string?> CreateFolderCommand { get; }
    public IRelayCommand CancelCommand { get; }

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        NavigateToAsync(CurrentPath, cancellationToken);

    public Task EnterChildAsync(
        TransferTargetDirectoryEntry directory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(directory);
        return NavigateToAsync(directory.Path, cancellationToken);
    }

    public Task GoToParentAsync(CancellationToken cancellationToken = default)
    {
        if (!CanGoToParent()) return Task.CompletedTask;
        var separator = CurrentPath.LastIndexOf('/');
        var parent = separator <= 0 ? "/" : CurrentPath[..separator];
        return NavigateToAsync(parent, cancellationToken);
    }

    public async Task NavigateToAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (isClosed) return;
        var canonicalPath = new RemotePath(path).Value;
        var generation = Interlocked.Increment(ref navigationGeneration);
        creationCancellation?.Cancel();
        var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        var previous = Interlocked.Exchange(ref navigationCancellation, request);
        previous?.Cancel();
        IsLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            TransferTargetDirectoryLoadResult result;
            try
            {
                result = await source.LoadAsync(accountId, canonicalPath, request.Token)
                    .ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                if (!isClosed && generation == Volatile.Read(ref navigationGeneration))
                    ErrorMessage = "目录读取失败，请稍后重试。";
                return;
            }

            if (request.IsCancellationRequested || isClosed ||
                generation != Volatile.Read(ref navigationGeneration))
                return;

            if (!result.IsSuccess)
            {
                ErrorMessage = result.ErrorMessage ?? "目录读取失败，请稍后重试。";
                return;
            }

            CurrentPath = canonicalPath;
            directories.Clear();
            foreach (var directory in result.Directories) directories.Add(directory);
            SelectedDirectory = null;
        }
        finally
        {
            if (generation == Volatile.Read(ref navigationGeneration)) IsLoading = false;
            Interlocked.CompareExchange(ref navigationCancellation, null, request);
            request.Dispose();
        }
    }

    public async Task CreateFolderAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        if (!CanCreateFolder || creator is null) return;

        var parentPath = CurrentPath;
        var capturedNavigationGeneration = Volatile.Read(ref navigationGeneration);
        var generation = Interlocked.Increment(ref creationGeneration);
        var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        var previous = Interlocked.Exchange(ref creationCancellation, request);
        previous?.Cancel();
        IsCreating = true;
        ErrorMessage = string.Empty;
        try
        {
            TransferTargetDirectoryCreateResult createResult;
            try
            {
                createResult = await creator.CreateAsync(
                        accountId, parentPath, name, request.Token)
                    .ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                if (IsCurrentCreate(request, generation, parentPath, capturedNavigationGeneration))
                    ErrorMessage = "文件夹创建失败，请稍后重试。";
                return;
            }

            if (!IsCurrentCreate(request, generation, parentPath, capturedNavigationGeneration))
                return;
            if (!createResult.IsSuccess)
            {
                ErrorMessage = createResult.ErrorMessage ?? "文件夹创建失败，请稍后重试。";
                return;
            }

            string childPath;
            try
            {
                childPath = new RemotePath(createResult.CreatedPath!).Value;
            }
            catch (ArgumentException)
            {
                ErrorMessage = "文件夹创建失败，请稍后重试。";
                return;
            }

            TransferTargetDirectoryLoadResult openResult;
            try
            {
                openResult = await source.LoadAsync(accountId, childPath, request.Token)
                    .ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                if (IsCurrentCreate(request, generation, parentPath, capturedNavigationGeneration))
                    ErrorMessage = "文件夹已创建，但暂时无法打开，请刷新后重试。";
                return;
            }

            if (!IsCurrentCreate(request, generation, parentPath, capturedNavigationGeneration))
                return;
            if (!openResult.IsSuccess)
            {
                ErrorMessage = "文件夹已创建，但暂时无法打开，请刷新后重试。";
                return;
            }

            CurrentPath = childPath;
            directories.Clear();
            foreach (var directory in openResult.Directories) directories.Add(directory);
            SelectedDirectory = null;
        }
        finally
        {
            if (generation == Volatile.Read(ref creationGeneration)) IsCreating = false;
            Interlocked.CompareExchange(ref creationCancellation, null, request);
            request.Dispose();
        }
    }

    public void Close()
    {
        if (isClosed) return;
        isClosed = true;
        lifetime.Cancel();
        navigationCancellation?.Cancel();
        creationCancellation?.Cancel();
        GoToParentCommand.NotifyCanExecuteChanged();
        EnterSelectedDirectoryCommand.NotifyCanExecuteChanged();
        SelectCurrentDirectoryCommand.NotifyCanExecuteChanged();
        CreateFolderCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanCreateFolder));
        OnPropertyChanged(nameof(CanBrowse));
        CancelCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        Close();
        navigationCancellation?.Dispose();
        creationCancellation?.Dispose();
        lifetime.Dispose();
    }

    private bool IsBusy => IsLoading || IsCreating;

    private bool CanGoToParent() => !IsBusy && !isClosed && !IsAtRoot;

    private bool IsCurrentCreate(
        CancellationTokenSource request,
        long generation,
        string parentPath,
        long capturedNavigationGeneration) =>
        !request.IsCancellationRequested &&
        !isClosed &&
        generation == Volatile.Read(ref creationGeneration) &&
        capturedNavigationGeneration == Volatile.Read(ref navigationGeneration) &&
        string.Equals(CurrentPath, parentPath, StringComparison.Ordinal);

    private void Cancel()
    {
        if (isClosed) return;
        Completed?.Invoke(this, null);
        Close();
    }

    private Task EnterSelectedDirectoryAsync(CancellationToken cancellationToken) =>
        SelectedDirectory is null
            ? Task.CompletedTask
            : EnterChildAsync(SelectedDirectory, cancellationToken);
}
