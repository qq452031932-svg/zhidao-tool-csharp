using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Application.Accounts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BaiduBatchTransfer.App.Features.Accounts;

public static class AccountManagementUiText
{
    public const string WindowTitle = "账号管理";
    public const string Heading = "已添加账号";
    public const string KeepHistoryChoice = "仅退出账号，保留历史记录";
    public const string RemoveTerminalHistoryChoice = "退出账号，并清理该账号的终态任务历史";
    public const string Empty = "还没有已添加的账号。";
    public const string Loading = "正在加载账号列表……";
    public const string LoadingPreview = "正在生成该账号的退出预览……";
    public const string Applying = "正在退出所选账号……";
    public const string RetryingCleanup = "正在重试清理本机登录数据……";
    public const string LoadFailed = "账号列表加载失败，请稍后重试。";
    public const string OperationFailed = "账号操作失败，请稍后重试。";
    public const string CleanupFailed = "清理未完成，请稍后重试。";
    public const string ActiveWorkBlocked = "账号仍有未完成工作，暂时不能退出。";
    public const string StaleConfirmation = "确认信息已过期，请重新预览后再试。";
    public const string NotFound = "账号不存在或已被移除。";
    public const string AlreadyRemoved = "账号已经退出。";
    public const string Removed = "账号已退出并完成清理。";
    public const string CleanupPending = "账号已退出，仍需重试清理本机登录数据。";
    public const string Ready = "请核对影响范围后确认退出。";
    public const string Cancelled = "已取消退出操作。";
    public const string ConfirmTitle = "确认退出账号";
    public const string PreviousPage = "上一页";
    public const string NextPage = "下一页";
    public const string Cancel = "取消";
    public const string Confirm = "确认退出";
}

internal static class AccountManagementPresentation
{
    internal static string SanitizeDisplayName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "未命名账号";
        var builder = new StringBuilder(Math.Min(value.Length, 80));
        foreach (var rune in value.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or
                UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                continue;
            builder.Append(rune.ToString());
            if (builder.Length >= 80) break;
        }
        var sanitized = builder.ToString().Trim();
        if (sanitized.Length == 0 || LooksSensitiveOrPathLike(sanitized))
            return "已选择的账号";
        return sanitized;
    }

    private static bool LooksSensitiveOrPathLike(string value)
    {
        if (value.Contains('/') || value.Contains('\\') ||
            value.Contains("cookie", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("token", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("profile", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("exception", StringComparison.OrdinalIgnoreCase))
            return true;
        for (var index = 0; index + 36 <= value.Length; index++)
        {
            if (Guid.TryParse(value.AsSpan(index, 36), out _)) return true;
        }
        return false;
    }
}

public interface IAccountRemovalOperations
{
    Task<AccountPage> GetAccountsPageAsync(int pageNumber, int pageSize, CancellationToken cancellationToken);
    Task<AccountExitPreview> PreviewAsync(
        Guid accountId,
        AccountExitHistoryMode mode,
        CancellationToken cancellationToken);
    Task<AccountExitPreview> ApplyAsync(AccountExitRequest request, CancellationToken cancellationToken);
    Task<AccountExitPreview> ResumeCleanupAsync(
        Guid accountId,
        DateTimeOffset removedAt,
        CancellationToken cancellationToken);
}

public interface IAccountManagementConfirmation
{
    Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken);
}

public sealed class AccountRemovalOperations(AccountRemovalCoordinator coordinator) : IAccountRemovalOperations
{
    private readonly AccountRemovalCoordinator coordinator =
        coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    public Task<AccountPage> GetAccountsPageAsync(
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken) =>
        coordinator.GetAccountsPageAsync(pageNumber, pageSize, cancellationToken);

    public Task<AccountExitPreview> PreviewAsync(
        Guid accountId,
        AccountExitHistoryMode mode,
        CancellationToken cancellationToken) =>
        coordinator.PreviewAsync(accountId, mode, cancellationToken);

    public Task<AccountExitPreview> ApplyAsync(
        AccountExitRequest request,
        CancellationToken cancellationToken) =>
        coordinator.ApplyAsync(request, cancellationToken);

    public Task<AccountExitPreview> ResumeCleanupAsync(
        Guid accountId,
        DateTimeOffset removedAt,
        CancellationToken cancellationToken) =>
        coordinator.ResumeCleanupAsync(accountId, removedAt, cancellationToken);
}

public sealed class AccountManagementRowViewModel
{
    private readonly Func<AccountManagementRowViewModel, CancellationToken, Task> action;
    private readonly Func<bool> canExecute;

    internal AccountManagementRowViewModel(
        AccountListItem account,
        Func<AccountManagementRowViewModel, CancellationToken, Task> action,
        Func<bool> canExecute)
    {
        ArgumentNullException.ThrowIfNull(account);
        this.action = action ?? throw new ArgumentNullException(nameof(action));
        this.canExecute = canExecute ?? throw new ArgumentNullException(nameof(canExecute));
        AccountId = account.AccountId;
        DisplayName = AccountManagementPresentation.SanitizeDisplayName(account.DisplayName);
        LoginStatusText = MapLoginStatus(account.Status);
        EnabledStateText = account.IsEnabled ? "已启用" : "已禁用";
        IsCleanupPending = account.IsCleanupPending;
        CanAct = IsCleanupPending || account.IsEnabled;
        ActionText = IsCleanupPending ? "重试清理" : CanAct ? "退出账号" : "不可操作";
        ActionCommand = new AsyncRelayCommand(ExecuteActionAsync, () => CanAct && this.canExecute());
    }

    internal Guid AccountId { get; }
    internal bool IsCleanupPending { get; }
    internal bool CanAct { get; }
    public string DisplayName { get; }
    public string LoginStatusText { get; }
    public string EnabledStateText { get; }
    public string ActionText { get; }
    public string ActionAutomationName => $"{ActionText}：{DisplayName}";
    public IAsyncRelayCommand ActionCommand { get; }

    public override string ToString() =>
        $"账号行：{DisplayName}，{LoginStatusText}，{EnabledStateText}，操作为{ActionText}";

    private Task ExecuteActionAsync(CancellationToken cancellationToken) =>
        action(this, cancellationToken);

    internal void NotifyCanExecuteChanged() => ActionCommand.NotifyCanExecuteChanged();

    private static string MapLoginStatus(string status) => status switch
    {
        "Active" => "已登录",
        "Invalid" => "登录已失效",
        "Disabled" => "已退出",
        _ => "状态未知",
    };
}

public sealed class AccountManagementViewModel : ObservableObject
{
    public const int PageSize = 100;

    private readonly IAccountRemovalOperations operations;
    private readonly IAccountManagementConfirmation confirmation;
    private readonly TimeProvider timeProvider;
    private CancellationToken lifetimeToken;
    private CancellationTokenRegistration lifetimeRegistration;
    private bool lifetimeAttached;
    private AccountManagementRowViewModel? selectedAccount;
    private AccountExitPreview? preview;
    private Guid previewAccountId;
    private AccountExitHistoryMode previewMode;
    private AccountExitHistoryMode historyMode = AccountExitHistoryMode.KeepHistory;
    private long previewVersion;
    private bool isBusy;
    private bool isConfirmationVisible;
    private bool needsPreviewRefresh;
    private bool isLifetimeEnded;
    private int pageNumber = 1;
    private long totalCount;
    private int terminalOperationCount;
    private int terminalBatchCount;
    private int sharedBatchCount;
    private string confirmationMessage = string.Empty;
    private string statusMessage = AccountManagementUiText.Loading;

    public AccountManagementViewModel(
        IAccountRemovalOperations operations,
        IAccountManagementConfirmation confirmation,
        TimeProvider? timeProvider = null)
    {
        this.operations = operations ?? throw new ArgumentNullException(nameof(operations));
        this.confirmation = confirmation ?? throw new ArgumentNullException(nameof(confirmation));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        LoadFirstPageCommand = new AsyncRelayCommand(LoadFirstPageAsync, () => !IsBusy && !IsLifetimeEnded);
        LoadNextPageCommand = new AsyncRelayCommand(LoadNextPageAsync, () => CanLoadNextPage && !IsBusy && !IsLifetimeEnded);
        LoadPreviousPageCommand = new AsyncRelayCommand(LoadPreviousPageAsync, () => PageNumber > 1 && !IsBusy && !IsLifetimeEnded);
        ReloadPreviewCommand = new AsyncRelayCommand(ReloadPreviewAsync, () => NeedsPreviewRefresh && selectedAccount is not null && !IsBusy && !IsLifetimeEnded);
        CancelConfirmationCommand = new RelayCommand(CancelConfirmation, () => IsConfirmationVisible && !IsBusy && !IsLifetimeEnded);
        ConfirmRemovalCommand = new AsyncRelayCommand(ConfirmRemovalAsync, () => CanConfirm);
    }

    public event EventHandler<AccountManagementRefreshEventArgs>? RefreshRequested;

    public ObservableCollection<AccountManagementRowViewModel> Accounts { get; } = [];
    public string WindowTitle => AccountManagementUiText.WindowTitle;
    public string Heading => AccountManagementUiText.Heading;
    public string KeepHistoryChoice => AccountManagementUiText.KeepHistoryChoice;
    public string RemoveTerminalHistoryChoice => AccountManagementUiText.RemoveTerminalHistoryChoice;
    public IAsyncRelayCommand LoadFirstPageCommand { get; }
    public IAsyncRelayCommand LoadNextPageCommand { get; }
    public IAsyncRelayCommand LoadPreviousPageCommand { get; }
    public IAsyncRelayCommand ReloadPreviewCommand { get; }
    public IRelayCommand CancelConfirmationCommand { get; }
    public IAsyncRelayCommand ConfirmRemovalCommand { get; }

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (!SetProperty(ref isBusy, value)) return;
            OnPropertyChanged(nameof(IsLoading));
            NotifyCommands();
        }
    }

    public bool IsLoading => IsBusy;
    public bool IsLifetimeEnded
    {
        get => isLifetimeEnded;
        private set
        {
            if (!SetProperty(ref isLifetimeEnded, value)) return;
            NotifyCommands();
        }
    }
    public bool HasAccounts => Accounts.Count > 0;
    public bool IsEmpty => !IsBusy && Accounts.Count == 0;
    public AccountManagementRowViewModel? SelectedAccount
    {
        get => selectedAccount;
        set
        {
            if (!SetProperty(ref selectedAccount, value)) return;
            InvalidatePreview(clearSelection: false);
        }
    }
    public int PageNumber { get => pageNumber; private set => SetProperty(ref pageNumber, value); }
    public long TotalCount { get => totalCount; private set => SetProperty(ref totalCount, value); }
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
    public string PageSummary => $"第 {PageNumber} 页，共 {TotalPages} 页";
    public bool CanLoadNextPage => PageNumber < TotalPages;

    public bool IsConfirmationVisible
    {
        get => isConfirmationVisible;
        private set
        {
            if (!SetProperty(ref isConfirmationVisible, value)) return;
            NotifyCommands();
        }
    }

    public bool NeedsPreviewRefresh
    {
        get => needsPreviewRefresh;
        private set
        {
            if (!SetProperty(ref needsPreviewRefresh, value)) return;
            NotifyCommands();
        }
    }
    public bool CanConfirm => IsConfirmationVisible && preview is not null && !NeedsPreviewRefresh && !IsBusy && !IsLifetimeEnded;
    public int TerminalOperationCount
    {
        get => terminalOperationCount;
        private set => SetProperty(ref terminalOperationCount, value);
    }
    public int TerminalBatchCount
    {
        get => terminalBatchCount;
        private set => SetProperty(ref terminalBatchCount, value);
    }
    public int SharedBatchCount
    {
        get => sharedBatchCount;
        private set => SetProperty(ref sharedBatchCount, value);
    }
    public string ConfirmationMessage
    {
        get => confirmationMessage;
        private set => SetProperty(ref confirmationMessage, value);
    }
    public string StatusMessage
    {
        get => statusMessage;
        private set => SetProperty(ref statusMessage, value);
    }

    public bool KeepHistorySelected
    {
        get => historyMode == AccountExitHistoryMode.KeepHistory;
        set { if (value) SetHistoryMode(AccountExitHistoryMode.KeepHistory); }
    }

    public bool RemoveTerminalHistorySelected
    {
        get => historyMode == AccountExitHistoryMode.RemoveTerminalHistory;
        set { if (value) SetHistoryMode(AccountExitHistoryMode.RemoveTerminalHistory); }
    }

    public Task LoadFirstPageAsync(CancellationToken cancellationToken = default) =>
        LoadPageAsync(1, cancellationToken);

    public Task LoadNextPageAsync(CancellationToken cancellationToken = default) =>
        CanLoadNextPage ? LoadPageAsync(PageNumber + 1, cancellationToken) : Task.CompletedTask;

    public Task LoadPreviousPageAsync(CancellationToken cancellationToken = default) =>
        PageNumber > 1 ? LoadPageAsync(PageNumber - 1, cancellationToken) : Task.CompletedTask;

    public Task ReloadPreviewAsync(CancellationToken cancellationToken = default) =>
        selectedAccount is { } row && NeedsPreviewRefresh
            ? BeginAccountActionAsync(row, cancellationToken)
            : Task.CompletedTask;

    public void AttachLifetime(CancellationToken cancellationToken)
    {
        if (lifetimeAttached) throw new InvalidOperationException("账号管理窗口生命周期已绑定。");
        lifetimeAttached = true;
        lifetimeToken = cancellationToken;
        lifetimeRegistration = cancellationToken.Register(EndLifetime);
        if (cancellationToken.IsCancellationRequested) EndLifetime();
    }

    public void EndLifetime()
    {
        if (IsLifetimeEnded) return;
        IsLifetimeEnded = true;
        InvalidatePreview(clearSelection: true);
    }

    public void DetachLifetime()
    {
        EndLifetime();
        lifetimeRegistration.Dispose();
    }

    public async Task BeginAccountActionAsync(
        AccountManagementRowViewModel row,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (IsBusy || IsLifetimeEnded || !row.CanAct || !Accounts.Contains(row)) return;
        InvalidatePreview(clearSelection: true);
        SelectedAccount = row;
        if (row.IsCleanupPending)
        {
            await ResumeCleanupAsync(row, cancellationToken).ConfigureAwait(true);
            return;
        }

        var version = previewVersion;
        var accountId = row.AccountId;
        var mode = historyMode;
        IsConfirmationVisible = true;
        NeedsPreviewRefresh = true;
        IsBusy = true;
        StatusMessage = AccountManagementUiText.LoadingPreview;
        using var linked = CreateOperationTokenSource(cancellationToken);
        try
        {
            var result = await operations.PreviewAsync(accountId, mode, linked.Token).ConfigureAwait(true);
            if (version != previewVersion || selectedAccount != row || historyMode != mode) return;
            if (result.Status != AccountExitStatus.Ready)
            {
                StatusMessage = MapPreviewStatus(result.Status);
                IsConfirmationVisible = false;
                NeedsPreviewRefresh = false;
                if (result.Status == AccountExitStatus.CleanupPending)
                    RaiseRefresh(result.Status);
                return;
            }

            preview = result;
            previewAccountId = accountId;
            previewMode = mode;
            TerminalOperationCount = Math.Max(0, result.TerminalOperationCount);
            TerminalBatchCount = Math.Max(0, result.TerminalBatchCount);
            SharedBatchCount = Math.Max(0, result.SharedBatchCount);
            ConfirmationMessage = BuildConfirmationMessage(
                AccountManagementPresentation.SanitizeDisplayName(result.DisplayName),
                mode);
            NeedsPreviewRefresh = false;
            StatusMessage = AccountManagementUiText.Ready;
        }
        catch (OperationCanceledException)
        {
            if (!IsLifetimeEnded) StatusMessage = AccountManagementUiText.Cancelled;
        }
        catch (Exception)
        {
            StatusMessage = AccountManagementUiText.OperationFailed;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ConfirmRemovalAsync(CancellationToken cancellationToken = default)
    {
        if (!CanConfirm || selectedAccount is null || preview is null) return;
        var version = previewVersion;
        var accountId = previewAccountId;
        var mode = previewMode;
        var confirmationToken = preview.ConfirmationToken;
        var message = ConfirmationMessage;
        IsBusy = true;
        using var linked = CreateOperationTokenSource(cancellationToken);
        try
        {
            var confirmed = await confirmation
                .ConfirmAsync(AccountManagementUiText.ConfirmTitle, message, linked.Token)
                .ConfigureAwait(true);
            if (!confirmed) return;
            if (version != previewVersion || selectedAccount.AccountId != accountId || historyMode != mode) return;
            StatusMessage = AccountManagementUiText.Applying;
            var request = new AccountExitRequest(
                accountId,
                mode,
                confirmationToken,
                timeProvider.GetUtcNow());
            var result = await operations.ApplyAsync(request, linked.Token).ConfigureAwait(true);
            HandleApplyResult(result.Status);
        }
        catch (OperationCanceledException)
        {
            if (!IsLifetimeEnded) StatusMessage = AccountManagementUiText.Cancelled;
        }
        catch (Exception)
        {
            StatusMessage = AccountManagementUiText.OperationFailed;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadPageAsync(int requestedPage, CancellationToken cancellationToken)
    {
        if (IsBusy || requestedPage < 1) return;
        InvalidatePreview(clearSelection: true);
        IsBusy = true;
        StatusMessage = AccountManagementUiText.Loading;
        using var linked = CreateOperationTokenSource(cancellationToken);
        try
        {
            var page = await operations
                .GetAccountsPageAsync(requestedPage, PageSize, linked.Token)
                .ConfigureAwait(true);
            Accounts.Clear();
            foreach (var account in page.Items.Take(PageSize))
                Accounts.Add(new AccountManagementRowViewModel(
                    account,
                    (row, token) => BeginAccountActionAsync(row, token),
                    () => !IsBusy && !IsLifetimeEnded));
            PageNumber = Math.Max(1, page.PageNumber);
            TotalCount = Math.Max(0, page.TotalCount);
            StatusMessage = Accounts.Count == 0 ? AccountManagementUiText.Empty : string.Empty;
            OnPropertyChanged(nameof(HasAccounts));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(TotalPages));
            OnPropertyChanged(nameof(PageSummary));
            OnPropertyChanged(nameof(CanLoadNextPage));
        }
        catch (OperationCanceledException)
        {
            if (!IsLifetimeEnded) StatusMessage = AccountManagementUiText.Cancelled;
        }
        catch (Exception)
        {
            StatusMessage = AccountManagementUiText.LoadFailed;
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    private async Task ResumeCleanupAsync(
        AccountManagementRowViewModel row,
        CancellationToken cancellationToken)
    {
        IsBusy = true;
        StatusMessage = AccountManagementUiText.RetryingCleanup;
        using var linked = CreateOperationTokenSource(cancellationToken);
        try
        {
            var result = await operations
                .ResumeCleanupAsync(row.AccountId, timeProvider.GetUtcNow(), linked.Token)
                .ConfigureAwait(true);
            if (result.Status == AccountExitStatus.Removed)
            {
                StatusMessage = AccountManagementUiText.Removed;
                RaiseRefresh(result.Status);
            }
            else if (result.Status == AccountExitStatus.CleanupPending)
            {
                StatusMessage = AccountManagementUiText.CleanupPending;
                RaiseRefresh(result.Status);
            }
            else
            {
                StatusMessage = AccountManagementUiText.CleanupFailed;
            }
        }
        catch (OperationCanceledException)
        {
            if (!IsLifetimeEnded) StatusMessage = AccountManagementUiText.Cancelled;
        }
        catch (Exception)
        {
            StatusMessage = AccountManagementUiText.CleanupFailed;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void HandleApplyResult(AccountExitStatus status)
    {
        InvalidatePreview(clearSelection: false);
        StatusMessage = status switch
        {
            AccountExitStatus.Removed => AccountManagementUiText.Removed,
            AccountExitStatus.CleanupPending => AccountManagementUiText.CleanupPending,
            AccountExitStatus.ActiveWorkBlocked => AccountManagementUiText.ActiveWorkBlocked,
            AccountExitStatus.StaleConfirmation => AccountManagementUiText.StaleConfirmation,
            AccountExitStatus.AlreadyRemoved => AccountManagementUiText.AlreadyRemoved,
            AccountExitStatus.NotFound => AccountManagementUiText.NotFound,
            _ => AccountManagementUiText.OperationFailed,
        };
        if (status is AccountExitStatus.Removed or AccountExitStatus.CleanupPending)
            RaiseRefresh(status);
    }

    private void SetHistoryMode(AccountExitHistoryMode value)
    {
        if (historyMode == value) return;
        historyMode = value;
        InvalidatePreview(clearSelection: false, preservePanel: selectedAccount is not null);
        if (selectedAccount is not null)
        {
            NeedsPreviewRefresh = true;
            StatusMessage = "退出方式已更改，请重新加载确认信息。";
        }
        OnPropertyChanged(nameof(KeepHistorySelected));
        OnPropertyChanged(nameof(RemoveTerminalHistorySelected));
    }

    private void CancelConfirmation()
    {
        InvalidatePreview(clearSelection: false);
        StatusMessage = AccountManagementUiText.Cancelled;
    }

    private void InvalidatePreview(bool clearSelection, bool preservePanel = false)
    {
        previewVersion++;
        preview = null;
        previewAccountId = Guid.Empty;
        IsConfirmationVisible = preservePanel;
        NeedsPreviewRefresh = preservePanel;
        TerminalOperationCount = 0;
        TerminalBatchCount = 0;
        SharedBatchCount = 0;
        ConfirmationMessage = string.Empty;
        if (clearSelection && selectedAccount is not null)
        {
            selectedAccount = null;
            OnPropertyChanged(nameof(SelectedAccount));
        }
        NotifyCommands();
    }

    private string BuildConfirmationMessage(string displayName, AccountExitHistoryMode mode)
    {
        var history = mode == AccountExitHistoryMode.KeepHistory
            ? "将保留该账号的历史记录。"
            : "将清理该账号的终态任务历史；共享批次的公共上下文将保留。";
        return $"将退出账号“{displayName}”，其他账号不受影响。" +
            $"终态任务 {TerminalOperationCount} 项，终态批次 {TerminalBatchCount} 个，共享批次 {SharedBatchCount} 个。{history}";
    }

    private static string MapPreviewStatus(AccountExitStatus status) => status switch
    {
        AccountExitStatus.ActiveWorkBlocked => AccountManagementUiText.ActiveWorkBlocked,
        AccountExitStatus.StaleConfirmation => AccountManagementUiText.StaleConfirmation,
        AccountExitStatus.AlreadyRemoved => AccountManagementUiText.AlreadyRemoved,
        AccountExitStatus.NotFound => AccountManagementUiText.NotFound,
        AccountExitStatus.CleanupPending => AccountManagementUiText.CleanupPending,
        _ => AccountManagementUiText.OperationFailed,
    };

    private void RaiseRefresh(AccountExitStatus status) =>
        RefreshRequested?.Invoke(this, new AccountManagementRefreshEventArgs(status));

    private void NotifyCommands()
    {
        LoadFirstPageCommand.NotifyCanExecuteChanged();
        LoadNextPageCommand.NotifyCanExecuteChanged();
        LoadPreviousPageCommand.NotifyCanExecuteChanged();
        CancelConfirmationCommand.NotifyCanExecuteChanged();
        ConfirmRemovalCommand.NotifyCanExecuteChanged();
        ReloadPreviewCommand.NotifyCanExecuteChanged();
        foreach (var account in Accounts) account.NotifyCanExecuteChanged();
    }

    private CancellationTokenSource CreateOperationTokenSource(CancellationToken commandToken) =>
        CancellationTokenSource.CreateLinkedTokenSource(commandToken, lifetimeToken);

}
