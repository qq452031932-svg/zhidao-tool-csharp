using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Application.Accounts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BaiduBatchTransfer.App;

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly object transferWorkspace;
    private readonly object shareWorkspace;
    private readonly IAccountLoginLauncher loginLauncher;
    private readonly IAccountManagementLauncher? accountManagementLauncher;
    private readonly Func<Action<int>, CancellationToken, Task>? refreshAccounts;
    private object currentContent;
    private string currentWorkspaceTitle = "转存";
    private string statusMessage = "就绪";
    private string accountStatusMessage;
    private bool isAccountOperationBusy;
    private bool isOtaAcceptanceBuild;
    private int updateAvailableReported;

    public MainWindowViewModel(
        object transferWorkspace,
        object shareWorkspace,
        IAccountLoginLauncher loginLauncher,
        IAccountManagementLauncher? accountManagementLauncher = null,
        Func<Action<int>, CancellationToken, Task>? refreshAccounts = null,
        int initialActiveAccountCount = 0)
    {
        this.transferWorkspace = transferWorkspace ?? throw new ArgumentNullException(nameof(transferWorkspace));
        this.shareWorkspace = shareWorkspace ?? throw new ArgumentNullException(nameof(shareWorkspace));
        this.loginLauncher = loginLauncher ?? throw new ArgumentNullException(nameof(loginLauncher));
        this.accountManagementLauncher = accountManagementLauncher;
        this.refreshAccounts = refreshAccounts;
        accountStatusMessage = FormatAccountStatus(initialActiveAccountCount);
        currentContent = transferWorkspace;
        ShowTransferCommand = new RelayCommand(ShowTransfer);
        ShowShareCommand = new RelayCommand(ShowShare);
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

    public string ApplicationTitle => FormatApplicationTitle(isOtaAcceptanceBuild);
    public object CurrentContent { get => currentContent; private set => SetProperty(ref currentContent, value); }
    public string CurrentWorkspaceTitle { get => currentWorkspaceTitle; private set => SetProperty(ref currentWorkspaceTitle, value); }
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
    public IRelayCommand ShowTransferCommand { get; }
    public IRelayCommand ShowShareCommand { get; }
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
                StatusMessage = "账号登录窗口已关闭，但列表刷新失败，请重试刷新。";
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
            if (result.RefreshRequired)
            {
                try
                {
                    await RefreshAccountsAsync(cancellationToken).ConfigureAwait(true);
                    StatusMessage = result.Status switch
                    {
                        AccountExitStatus.Removed => "账号已退出，账号列表已刷新。",
                        AccountExitStatus.CleanupPending => "账号已退出，本机登录数据待继续清理；账号列表已刷新。",
                        _ => "账号身份已核验，账号列表已刷新。",
                    };
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

    internal void ReportHandoffFailure() =>
        StatusMessage = "结果工作区操作失败，请稍后重试。";

    internal static string FormatApplicationTitle(bool isOtaAcceptanceBuild) =>
        isOtaAcceptanceBuild
            ? "百度批量转存 · OTA 本地验收版"
            : "百度批量转存";

    internal void ConfigureOtaAcceptanceBuild(bool enabled)
    {
        if (isOtaAcceptanceBuild == enabled)
            return;

        isOtaAcceptanceBuild = enabled;
        OnPropertyChanged(nameof(ApplicationTitle));
    }

    internal void ReportUpdateAvailable(string version)
    {
        if (string.IsNullOrWhiteSpace(version) ||
            Interlocked.CompareExchange(ref updateAvailableReported, 1, 0) != 0)
            return;

        StatusMessage = $"发现新版本 {version}，可到设置中心更新。";
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

    private void ShowTransfer()
    {
        CurrentContent = transferWorkspace;
        CurrentWorkspaceTitle = "转存";
    }

    private void ShowShare()
    {
        CurrentContent = shareWorkspace;
        CurrentWorkspaceTitle = "分享";
    }
}
