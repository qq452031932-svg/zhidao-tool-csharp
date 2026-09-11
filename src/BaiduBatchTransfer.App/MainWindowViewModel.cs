using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.App.Features.Jobs;
using BaiduBatchTransfer.Application.Accounts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace BaiduBatchTransfer.App;

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly object homeWorkspace;
    private readonly object shareDataWorkspace;
    private readonly object batchTransferWorkspace;
    private readonly object adInsertionWorkspace;
    private readonly object fileExclusionWorkspace;
    private readonly object statisticsWorkspace;
    private readonly object settingsWorkspace;
    private readonly IAccountLoginLauncher loginLauncher;
    private readonly IAccountManagementLauncher? accountManagementLauncher;
    private readonly Func<Action<int>, CancellationToken, Task>? refreshAccounts;
    private readonly Func<Guid, CancellationToken, Task<AccountCapacityRefreshResult>>? refreshAccountCapacity;
    private readonly Action<Guid?, string?>? selectShareAnalyticsAccount;
    private readonly ShareAnalyticsPreferencePersistenceCoordinator? shareAnalyticsPreferencePersistence;
    private readonly Func<CancellationToken, Task>? enterShareData;
    private readonly Action? leaveShareData;
    private readonly HashSet<AccountAllocationRowViewModel> subscribedAccountRows = [];
    private readonly HashSet<Guid> capacityRefreshesInFlight = [];
    private readonly object capacityRefreshGate = new();
    private readonly CancellationTokenSource capacityLifetime = new();
    private object currentContent;
    private string currentWorkspaceTitle = "分享数据";
    private string statusMessage = "就绪";
    private string accountStatusMessage;
    private bool isAccountOperationBusy;
    private bool isClosed;
    private bool isRefreshingAccounts;
    private bool suppressAccountSelectionHandling;
    private bool analyticsAccountResolved;
    private int initialWorkspaceActivated;
    private int updateAvailableReported;
    private bool isOtaAcceptanceBuild;
    private Guid? analyticsAccountId;

    public MainWindowViewModel(
        object homeWorkspace,
        object shareDataWorkspace,
        object batchTransferWorkspace,
        object adInsertionWorkspace,
        object fileExclusionWorkspace,
        object statisticsWorkspace,
        object settingsWorkspace,
        IAccountLoginLauncher loginLauncher,
        IAccountManagementLauncher? accountManagementLauncher = null,
        Func<Action<int>, CancellationToken, Task>? refreshAccounts = null,
        int initialActiveAccountCount = 0,
        ObservableCollection<AccountAllocationRowViewModel>? accounts = null,
        Func<Guid, CancellationToken, Task<AccountCapacityRefreshResult>>? refreshAccountCapacity = null,
        Action<Guid?, string?>? selectShareAnalyticsAccount = null,
        Guid? initialShareAnalyticsAccountId = null,
        Func<Guid?, CancellationToken, Task>? saveShareAnalyticsAccount = null,
        bool shareAnalyticsPreferenceLoadFailed = false,
        Func<CancellationToken, Task>? enterShareData = null,
        Action? leaveShareData = null)
    {
        this.homeWorkspace = homeWorkspace ?? throw new ArgumentNullException(nameof(homeWorkspace));
        this.shareDataWorkspace = shareDataWorkspace ?? throw new ArgumentNullException(nameof(shareDataWorkspace));
        this.batchTransferWorkspace = batchTransferWorkspace ?? throw new ArgumentNullException(nameof(batchTransferWorkspace));
        this.adInsertionWorkspace = adInsertionWorkspace ?? throw new ArgumentNullException(nameof(adInsertionWorkspace));
        this.fileExclusionWorkspace = fileExclusionWorkspace ?? throw new ArgumentNullException(nameof(fileExclusionWorkspace));
        this.statisticsWorkspace = statisticsWorkspace ?? throw new ArgumentNullException(nameof(statisticsWorkspace));
        this.settingsWorkspace = settingsWorkspace ?? throw new ArgumentNullException(nameof(settingsWorkspace));
        this.loginLauncher = loginLauncher ?? throw new ArgumentNullException(nameof(loginLauncher));
        this.accountManagementLauncher = accountManagementLauncher;
        this.refreshAccounts = refreshAccounts;
        this.refreshAccountCapacity = refreshAccountCapacity;
        this.selectShareAnalyticsAccount = selectShareAnalyticsAccount;
        var uiContext = SynchronizationContext.Current;
        shareAnalyticsPreferencePersistence = saveShareAnalyticsAccount is null
            ? null
            : new ShareAnalyticsPreferencePersistenceCoordinator(
                saveShareAnalyticsAccount,
                () => ReportShareAnalyticsPreferenceFailure(uiContext));
        this.enterShareData = enterShareData;
        this.leaveShareData = leaveShareData;
        Accounts = accounts ?? [];
        Accounts.CollectionChanged += OnAccountsCollectionChanged;
        SynchronizeAccountSubscriptions(resolveMissingCurrent: false);
        ResolveInitialAnalyticsAccount(initialShareAnalyticsAccountId);
        accountStatusMessage = FormatAccountStatus(initialActiveAccountCount);
        if (shareAnalyticsPreferenceLoadFailed)
            statusMessage = "账号查看偏好未能恢复。";
        currentContent = shareDataWorkspace;
        ShowHomeCommand = new RelayCommand(() => ShowWorkspace(this.homeWorkspace, "首页"));
        ShowShareDataCommand = new RelayCommand(() => ShowWorkspace(this.shareDataWorkspace, "分享数据"));
        ShowBatchTransferCommand = new RelayCommand(() => ShowWorkspace(this.batchTransferWorkspace, "批量转存"));
        ShowAdInsertionCommand = new RelayCommand(() => ShowWorkspace(this.adInsertionWorkspace, "广告插入"));
        ShowFileExclusionCommand = new RelayCommand(() => ShowWorkspace(this.fileExclusionWorkspace, "文件排除"));
        ShowStatisticsCommand = new RelayCommand(() => ShowWorkspace(this.statisticsWorkspace, "数据统计"));
        ShowSettingsCommand = new RelayCommand(() => ShowWorkspace(this.settingsWorkspace, "设置中心"));
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
    public object BatchTransferWorkspace => batchTransferWorkspace;
    public object CurrentContent { get => currentContent; private set => SetProperty(ref currentContent, value); }
    public string CurrentWorkspaceTitle { get => currentWorkspaceTitle; private set => SetProperty(ref currentWorkspaceTitle, value); }
    public string StatusMessage { get => statusMessage; private set => SetProperty(ref statusMessage, value); }
    public ObservableCollection<AccountAllocationRowViewModel> Accounts { get; }
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
    public IRelayCommand ShowHomeCommand { get; }
    public IRelayCommand ShowShareDataCommand { get; }
    public IRelayCommand ShowBatchTransferCommand { get; }
    public IRelayCommand ShowAdInsertionCommand { get; }
    public IRelayCommand ShowFileExclusionCommand { get; }
    public IRelayCommand ShowStatisticsCommand { get; }
    public IRelayCommand ShowSettingsCommand { get; }
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
            var existingAccountIds = Accounts.Select(account => account.AccountId).ToHashSet();
            await loginLauncher.OpenAsync(cancellationToken).ConfigureAwait(true);
            try
            {
                await RefreshAccountsAsync(cancellationToken).ConfigureAwait(true);
                StatusMessage = "账号登录窗口已关闭，账号状态已刷新。";
                await RefreshNewAccountsAfterLoginAsync(existingAccountIds, cancellationToken)
                    .ConfigureAwait(true);
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

    public Task ActivateInitialWorkspaceAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref initialWorkspaceActivated, 1, 0) != 0 || enterShareData is null)
            return Task.CompletedTask;
        return enterShareData(cancellationToken);
    }

    public Task<bool> FlushShareAnalyticsPreferenceAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        shareAnalyticsPreferencePersistence?.FlushAsync(timeout, cancellationToken) ??
        Task.FromResult(true);

    public Task<bool> SealAndFlushShareAnalyticsPreferenceAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        shareAnalyticsPreferencePersistence?.SealAndFlushAsync(timeout, cancellationToken) ??
        Task.FromResult(true);

    public void ResumeShareAnalyticsPreferenceAfterFailedShutdown() =>
        shareAnalyticsPreferencePersistence?.ResumeAfterFailedShutdown();

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
        if (isClosed || string.IsNullOrWhiteSpace(version) ||
            Interlocked.CompareExchange(ref updateAvailableReported, 1, 0) != 0)
            return;

        StatusMessage = $"发现新版本 {version}，可到设置中心更新。";
    }

    public void Close()
    {
        if (isClosed) return;
        isClosed = true;
        leaveShareData?.Invoke();
        shareAnalyticsPreferencePersistence?.Dispose();
        capacityLifetime.Cancel();
        Accounts.CollectionChanged -= OnAccountsCollectionChanged;
        foreach (var row in subscribedAccountRows) row.PropertyChanged -= OnAccountPropertyChanged;
        subscribedAccountRows.Clear();
        capacityLifetime.Dispose();
    }

    private async Task RefreshAccountsAsync(CancellationToken cancellationToken)
    {
        if (refreshAccounts is null) return;
        isRefreshingAccounts = true;
        try
        {
            await refreshAccounts(
                count => AccountStatusMessage = FormatAccountStatus(count),
                cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            isRefreshingAccounts = false;
            SynchronizeAccountSubscriptions(resolveMissingCurrent: true);
        }
    }

    private static string FormatAccountStatus(int count) =>
        count > 0 ? $"已登录 {count:N0} 个百度账号" : "尚未登录百度账号";

    private void ShowWorkspace(object workspace, string title)
    {
        if (!ReferenceEquals(workspace, shareDataWorkspace)) leaveShareData?.Invoke();
        CurrentContent = workspace;
        CurrentWorkspaceTitle = title;
        if (ReferenceEquals(workspace, shareDataWorkspace) && enterShareData is not null)
            ObserveWorkspaceActivation(enterShareData(capacityLifetime.Token));
    }

    private void OnAccountsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        SynchronizeAccountSubscriptions(resolveMissingCurrent: !isRefreshingAccounts);

    private void SynchronizeAccountSubscriptions(bool resolveMissingCurrent)
    {
        var current = Accounts.ToHashSet();
        foreach (var removed in subscribedAccountRows.Where(row => !current.Contains(row)).ToArray())
        {
            removed.PropertyChanged -= OnAccountPropertyChanged;
            subscribedAccountRows.Remove(removed);
        }
        foreach (var added in current.Where(row => subscribedAccountRows.Add(row)))
            added.PropertyChanged += OnAccountPropertyChanged;
        if (resolveMissingCurrent &&
            (analyticsAccountId is null || current.All(row => row.AccountId != analyticsAccountId)))
            ApplyFallbackAnalyticsAccount(persist: true);
    }

    private void OnAccountPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (isClosed || suppressAccountSelectionHandling ||
            e.PropertyName != nameof(AccountAllocationRowViewModel.IsSelected) ||
            sender is not AccountAllocationRowViewModel row)
            return;
        if (row.IsSelected) ApplyAnalyticsAccount(row, persist: true);
        else if (analyticsAccountId == row.AccountId)
            ApplyAnalyticsAccount(Accounts.LastOrDefault(account => account.IsSelected), persist: true);
        if (refreshAccountCapacity is null || !row.IsSelected) return;
        ObserveCapacityRefresh(RefreshCapacityAsync(row.AccountId, capacityLifetime.Token));
    }

    private void ResolveInitialAnalyticsAccount(Guid? preferredAccountId)
    {
        var preferred = preferredAccountId is { } id
            ? Accounts.FirstOrDefault(account => account.AccountId == id)
            : null;
        ApplyAnalyticsAccount(
            preferred ?? (Accounts.Count == 1 ? Accounts[0] : null),
            persist: false);
    }

    private void ApplyFallbackAnalyticsAccount(bool persist) =>
        ApplyAnalyticsAccount(Accounts.Count == 1 ? Accounts[0] : null, persist);

    private void ApplyAnalyticsAccount(AccountAllocationRowViewModel? row, bool persist)
    {
        if (row is not null && !row.IsSelected)
        {
            suppressAccountSelectionHandling = true;
            try { row.IsSelected = true; }
            finally { suppressAccountSelectionHandling = false; }
        }
        var changed = !analyticsAccountResolved || analyticsAccountId != row?.AccountId;
        analyticsAccountResolved = true;
        analyticsAccountId = row?.AccountId;
        if (!changed) return;
        selectShareAnalyticsAccount?.Invoke(analyticsAccountId, row?.DisplayName);
        if (persist) PersistAnalyticsAccount(analyticsAccountId);
    }

    private void PersistAnalyticsAccount(Guid? accountId)
    {
        shareAnalyticsPreferencePersistence?.RequestSave(accountId);
    }

    private void ReportShareAnalyticsPreferenceFailure(SynchronizationContext? uiContext)
    {
        void ApplySafeStatus()
        {
            if (!isClosed) StatusMessage = "账号查看偏好未能保存。";
        }

        if (uiContext is null || ReferenceEquals(SynchronizationContext.Current, uiContext))
            ApplySafeStatus();
        else
            uiContext.Post(_ => ApplySafeStatus(), null);
    }

    private async Task RefreshNewAccountsAfterLoginAsync(
        IReadOnlySet<Guid> existingAccountIds,
        CancellationToken cancellationToken)
    {
        if (refreshAccountCapacity is null) return;
        foreach (var row in Accounts.Where(account => !existingAccountIds.Contains(account.AccountId)).ToArray())
            await RefreshCapacityAsync(row.AccountId, cancellationToken).ConfigureAwait(true);
    }

    private async Task RefreshCapacityAsync(Guid accountId, CancellationToken cancellationToken)
    {
        lock (capacityRefreshGate)
            if (!capacityRefreshesInFlight.Add(accountId)) return;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                capacityLifetime.Token,
                cancellationToken);
            var result = await refreshAccountCapacity!(accountId, linked.Token).ConfigureAwait(true);
            if (isClosed || linked.IsCancellationRequested) return;
            if (result.Disposition == AccountCapacityRefreshDisposition.Updated &&
                result.Capacity is { } capacity)
            {
                var account = Accounts.FirstOrDefault(candidate => candidate.AccountId == accountId);
                account?.ApplyCapacity(capacity);
                if (result.AvatarUri is { } avatar) account?.ApplyAvatar(avatar);
            }
            else
            {
                StatusMessage = result.SafeMessage;
            }
        }
        catch (OperationCanceledException) when (capacityLifetime.IsCancellationRequested || cancellationToken.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!isClosed) StatusMessage = "容量获取失败，请稍后重试。";
        }
        finally
        {
            lock (capacityRefreshGate) capacityRefreshesInFlight.Remove(accountId);
        }
    }

    private static async void ObserveCapacityRefresh(Task operation)
    {
        try { await operation.ConfigureAwait(true); }
        catch { }
    }

    private static async void ObserveWorkspaceActivation(Task operation)
    {
        try { await operation.ConfigureAwait(true); }
        catch { }
    }

}
