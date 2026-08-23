using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Application.Accounts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BaiduBatchTransfer.App;

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly object importPage;
    private readonly object createJobPage;
    private readonly object? resultsPage;
    private readonly IAccountLoginLauncher loginLauncher;
    private readonly IAccountManagementLauncher? accountManagementLauncher;
    private readonly Func<Action<int>, CancellationToken, Task>? refreshAccounts;
    private object currentContent;
    private string currentPageTitle = "导入与草稿";
    private string statusMessage = "就绪";
    private string accountStatusMessage;
    private bool isAccountOperationBusy;

    public MainWindowViewModel(
        object importPage,
        object createJobPage,
        IAccountLoginLauncher loginLauncher,
        IAccountManagementLauncher? accountManagementLauncher = null,
        Func<Action<int>, CancellationToken, Task>? refreshAccounts = null,
        int initialActiveAccountCount = 0,
        object? resultsPage = null)
    {
        this.importPage = importPage ?? throw new ArgumentNullException(nameof(importPage));
        this.createJobPage = createJobPage ?? throw new ArgumentNullException(nameof(createJobPage));
        this.resultsPage = resultsPage;
        this.loginLauncher = loginLauncher ?? throw new ArgumentNullException(nameof(loginLauncher));
        this.accountManagementLauncher = accountManagementLauncher;
        this.refreshAccounts = refreshAccounts;
        accountStatusMessage = FormatAccountStatus(initialActiveAccountCount);
        currentContent = importPage;
        ShowImportCommand = new RelayCommand(ShowImport);
        ShowCreateJobCommand = new RelayCommand(ShowCreateJob);
        ShowResultsCommand = new RelayCommand(ShowResults, () => this.resultsPage is not null);
        OpenAccountLoginCommand = new AsyncRelayCommand(
            OpenAccountLoginAsync,
            () => loginLauncher.IsAvailable && !IsAccountOperationBusy);
        OpenAccountManagementCommand = new AsyncRelayCommand(
            OpenAccountManagementAsync,
            () => accountManagementLauncher is not null && !IsAccountOperationBusy);
        RefreshAccountsCommand = new AsyncRelayCommand(
            RefreshAccountsManuallyAsync,
            () => refreshAccounts is not null && !IsAccountOperationBusy);
    }

    public string ApplicationTitle => "百度批量转存";
    public object CurrentContent { get => currentContent; private set => SetProperty(ref currentContent, value); }
    public string CurrentPageTitle { get => currentPageTitle; private set => SetProperty(ref currentPageTitle, value); }
    public string StatusMessage { get => statusMessage; private set => SetProperty(ref statusMessage, value); }
    public string AdapterStatusMessage => loginLauncher.StatusMessage;
    public string AccountStatusMessage { get => accountStatusMessage; private set => SetProperty(ref accountStatusMessage, value); }
    public string AccountManagementStatusMessage => accountManagementLauncher is null
        ? "账号管理尚不可用。"
        : IsAccountOperationBusy
            ? "正在处理账号操作，请稍候。"
            : "可管理已添加的百度账号。";
    public bool IsAccountOperationBusy
    {
        get => isAccountOperationBusy;
        private set
        {
            if (!SetProperty(ref isAccountOperationBusy, value)) return;
            OnPropertyChanged(nameof(AccountManagementStatusMessage));
            OpenAccountLoginCommand.NotifyCanExecuteChanged();
            OpenAccountManagementCommand.NotifyCanExecuteChanged();
            RefreshAccountsCommand.NotifyCanExecuteChanged();
        }
    }
    public IRelayCommand ShowImportCommand { get; }
    public IRelayCommand ShowCreateJobCommand { get; }
    public IRelayCommand ShowResultsCommand { get; }
    public IAsyncRelayCommand OpenAccountLoginCommand { get; }
    public IAsyncRelayCommand OpenAccountManagementCommand { get; }
    public IAsyncRelayCommand RefreshAccountsCommand { get; }

    public async Task OpenAccountLoginAsync(CancellationToken cancellationToken = default)
    {
        if (!loginLauncher.IsAvailable)
        {
            StatusMessage = loginLauncher.StatusMessage;
            return;
        }
        if (IsAccountOperationBusy) return;
        IsAccountOperationBusy = true;
        try
        {
            await loginLauncher.OpenAsync(cancellationToken).ConfigureAwait(true);
            try
            {
                await RefreshAccountsAsync(cancellationToken).ConfigureAwait(true);
                StatusMessage = "账号登录窗口已关闭，账号状态已刷新。";
            }
            catch (Exception)
            {
                StatusMessage = "账号登录已完成，但列表刷新失败，请重试刷新。";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusMessage = "已取消打开账号登录。";
        }
        catch (Exception)
        {
            StatusMessage = "无法打开账号登录，请稍后重试。";
        }
        finally
        {
            IsAccountOperationBusy = false;
        }
    }

    public async Task OpenAccountManagementAsync(CancellationToken cancellationToken = default)
    {
        if (accountManagementLauncher is null)
        {
            StatusMessage = "账号管理尚不可用。";
            return;
        }
        if (IsAccountOperationBusy) return;
        IsAccountOperationBusy = true;
        try
        {
            var result = await accountManagementLauncher.OpenAsync(cancellationToken).ConfigureAwait(true);
            if (result.RefreshRequired &&
                result.Status is AccountExitStatus.Removed or AccountExitStatus.CleanupPending)
            {
                try
                {
                    await RefreshAccountsAsync(cancellationToken).ConfigureAwait(true);
                    StatusMessage = result.Status == AccountExitStatus.Removed
                        ? "账号已退出，账号列表已刷新。"
                        : "账号已退出，本机登录数据待继续清理；账号列表已刷新。";
                }
                catch (Exception)
                {
                    StatusMessage = "账号操作已完成，但列表刷新失败，请重试刷新。";
                }
            }
            else
            {
                StatusMessage = "账号管理窗口已关闭。";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusMessage = "已取消打开账号管理。";
        }
        catch (Exception)
        {
            StatusMessage = "无法打开账号管理，请稍后重试。";
        }
        finally
        {
            IsAccountOperationBusy = false;
        }
    }

    public async Task RefreshAccountsManuallyAsync(CancellationToken cancellationToken = default)
    {
        if (refreshAccounts is null || IsAccountOperationBusy) return;

        IsAccountOperationBusy = true;
        try
        {
            await RefreshAccountsAsync(cancellationToken).ConfigureAwait(true);
            StatusMessage = "账号列表已刷新。";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusMessage = "账号列表刷新已取消。";
        }
        catch (Exception)
        {
            StatusMessage = "账号列表刷新失败，请稍后重试。";
        }
        finally
        {
            IsAccountOperationBusy = false;
        }
    }

    private async Task RefreshAccountsAsync(CancellationToken cancellationToken)
    {
        if (refreshAccounts is null) return;
        await refreshAccounts(
            count => AccountStatusMessage = FormatAccountStatus(count),
            cancellationToken).ConfigureAwait(true);
    }

    private static string FormatAccountStatus(int count) =>
        count > 0 ? $"已登录 {count:N0} 个百度账号" : "尚未登录百度账号";

    private void ShowImport()
    {
        CurrentContent = importPage;
        CurrentPageTitle = "导入与草稿";
    }

    private void ShowCreateJob()
    {
        CurrentContent = createJobPage;
        CurrentPageTitle = "创建任务";
    }

    public void ShowResults()
    {
        if (resultsPage is null) return;
        CurrentContent = resultsPage;
        CurrentPageTitle = "任务结果";
    }
}
