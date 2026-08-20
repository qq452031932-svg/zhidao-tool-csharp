using BaiduBatchTransfer.App;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Application.Accounts;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BaiduBatchTransfer.App.Tests;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public void NavigationUsesChineseLabelsAndDoesNotExposeUnavailableFeaturesAsRunnable()
    {
        var login = new FakeLoginLauncher();
        var vm = new MainWindowViewModel(new object(), new object(), login);

        Assert.Equal("导入与草稿", vm.CurrentPageTitle);
        Assert.Equal("百度批量转存", vm.ApplicationTitle);
        Assert.False(vm.OpenAccountLoginCommand.CanExecute(null));
        Assert.Contains("尚不可用", vm.AdapterStatusMessage);
        Assert.False(vm.OpenAccountManagementCommand.CanExecute(null));
        Assert.Contains("尚不可用", vm.AccountManagementStatusMessage);

        vm.ShowCreateJobCommand.Execute(null);

        Assert.Equal("创建任务", vm.CurrentPageTitle);
    }

    [Fact]
    public async Task LoginLauncherFailureIsMappedToChineseWithoutLeakingException()
    {
        var login = new FakeLoginLauncher { IsAvailableValue = true, Throw = true };
        var vm = new MainWindowViewModel(new object(), new object(), login);

        await vm.OpenAccountLoginAsync();

        Assert.Equal("无法打开账号登录，请稍后重试。", vm.StatusMessage);
        Assert.DoesNotContain("InvalidOperationException", vm.StatusMessage);
    }

    [Fact]
    public async Task SuccessfulLoginRefreshesVisibleAccountCount()
    {
        var login = new FakeLoginLauncher { IsAvailableValue = true };
        var refreshCalls = 0;
        var vm = new MainWindowViewModel(
            new object(), new object(), login,
            refreshAccounts: (applyCount, _) =>
            {
                refreshCalls++;
                applyCount(1);
                return Task.CompletedTask;
            },
            initialActiveAccountCount: 0);

        Assert.Equal("尚未登录百度账号", vm.AccountStatusMessage);

        await vm.OpenAccountLoginAsync();

        Assert.Equal("已登录 1 个百度账号", vm.AccountStatusMessage);
        Assert.Equal(1, refreshCalls);
    }

    [Theory]
    [InlineData(AccountExitStatus.Removed)]
    [InlineData(AccountExitStatus.CleanupPending)]
    public async Task CommittedManagementCloseRefreshesCountAndDisablesCommandsWhileBusy(
        AccountExitStatus status)
    {
        var management = new FakeManagementLauncher
        {
            Result = new AccountManagementRefreshResult(true, status),
            Delay = true,
        };
        var refreshCalls = 0;
        var vm = new MainWindowViewModel(
            new object(), new object(), new FakeLoginLauncher { IsAvailableValue = true },
            management,
            (applyCount, _) =>
            {
                refreshCalls++;
                applyCount(2);
                return Task.CompletedTask;
            },
            initialActiveAccountCount: 3);

        var opening = vm.OpenAccountManagementAsync();
        await management.Started.Task;

        Assert.False(vm.OpenAccountManagementCommand.CanExecute(null));
        Assert.False(vm.OpenAccountLoginCommand.CanExecute(null));
        Assert.Contains("正在", vm.AccountManagementStatusMessage);

        management.Complete();
        await opening;

        Assert.Equal(1, refreshCalls);
        Assert.Equal("已登录 2 个百度账号", vm.AccountStatusMessage);
        Assert.True(vm.OpenAccountManagementCommand.CanExecute(null));
    }

    [Fact]
    public async Task ManagementFailureUsesSafeChineseText()
    {
        var management = new FakeManagementLauncher { Throw = true };
        var vm = new MainWindowViewModel(
            new object(), new object(), new FakeLoginLauncher { IsAvailableValue = true }, management);

        await vm.OpenAccountManagementAsync();

        Assert.Equal("无法打开账号管理，请稍后重试。", vm.StatusMessage);
        Assert.DoesNotContain("secret-account-id", vm.StatusMessage);
        Assert.DoesNotContain("InvalidOperationException", vm.StatusMessage);
    }

    [Theory]
    [InlineData(AccountExitStatus.Removed)]
    [InlineData(AccountExitStatus.CleanupPending)]
    public async Task CommittedManagementActionReportsRefreshFailureAndRetriesOnlyTheRefresh(
        AccountExitStatus status)
    {
        var management = new FakeManagementLauncher
        {
            Result = new AccountManagementRefreshResult(true, status),
        };
        var refreshCalls = 0;
        var vm = new MainWindowViewModel(
            new object(),
            new object(),
            new FakeLoginLauncher { IsAvailableValue = true },
            management,
            (applyCount, _) =>
            {
                refreshCalls++;
                if (refreshCalls == 1)
                    throw new InvalidOperationException("secret-account-id C:\\private\\profile token");
                applyCount(4);
                return Task.CompletedTask;
            },
            initialActiveAccountCount: 3);

        await vm.OpenAccountManagementAsync();

        Assert.Equal("账号操作已完成，但列表刷新失败，请重试刷新。", vm.StatusMessage);
        Assert.Equal(1, management.OpenCalls);
        Assert.True(vm.OpenAccountManagementCommand.CanExecute(null));
        Assert.True(vm.RefreshAccountsCommand.CanExecute(null));

        await vm.RefreshAccountsCommand.ExecuteAsync(null);

        Assert.Equal(1, management.OpenCalls);
        Assert.Equal(2, refreshCalls);
        Assert.Equal("已登录 4 个百度账号", vm.AccountStatusMessage);
        Assert.Equal("账号列表已刷新。", vm.StatusMessage);
    }

    [Fact]
    public async Task CompletedLoginReportsRefreshFailureWithoutOpeningLoginAgainOnRetry()
    {
        var login = new FakeLoginLauncher { IsAvailableValue = true };
        var refreshCalls = 0;
        var vm = new MainWindowViewModel(
            new object(),
            new object(),
            login,
            refreshAccounts: (applyCount, _) =>
            {
                refreshCalls++;
                if (refreshCalls == 1)
                    throw new InvalidOperationException("private-profile token");
                applyCount(2);
                return Task.CompletedTask;
            });

        await vm.OpenAccountLoginAsync();

        Assert.Equal("账号登录已完成，但列表刷新失败，请重试刷新。", vm.StatusMessage);
        Assert.Equal(1, login.OpenCalls);
        Assert.True(vm.RefreshAccountsCommand.CanExecute(null));

        await vm.RefreshAccountsCommand.ExecuteAsync(null);

        Assert.Equal(1, login.OpenCalls);
        Assert.Equal(2, refreshCalls);
        Assert.Equal("已登录 2 个百度账号", vm.AccountStatusMessage);
    }

    [Fact]
    public void HeaderExposesAccountRefreshAndPolitelyAnnouncesAccountState()
    {
        var xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "BaiduBatchTransfer.App",
            "MainWindow.xaml"));

        Assert.Contains("Content=\"刷新账号\" Command=\"{Binding RefreshAccountsCommand}\"", xaml);
        Assert.Contains("x:Name=\"AccountStatusLiveRegion\"", xaml);
        Assert.Contains("AutomationProperties.Name=\"{Binding AccountStatusMessage}\"", xaml);
        Assert.Contains("x:Name=\"OperationStatusLiveRegion\"", xaml);
        Assert.Contains("AutomationProperties.Name=\"{Binding StatusMessage}\"", xaml);
        Assert.Contains("AutomationProperties.Name=\"{Binding AccountManagementStatusMessage}\"", xaml);
        Assert.Equal(2, xaml.Split("AutomationProperties.LiveSetting=\"Polite\"").Length - 1);

        var codeBehind = File.ReadAllText(Path.ChangeExtension(
            Path.Combine(FindRepositoryRoot(), "src", "BaiduBatchTransfer.App", "MainWindow.xaml"),
            ".xaml.cs"));
        Assert.Contains("DataContextChanged += OnDataContextChanged", codeBehind);
        Assert.Contains("Closed += OnClosed", codeBehind);
        Assert.Contains("liveRegionNotifier?.Dispose()", codeBehind);
        Assert.Contains("AutomationEvents.LiveRegionChanged", codeBehind);
    }

    [Fact]
    public void LiveRegionNotifierDispatchesTrackedChangesAndStopsAfterDispose()
    {
        var source = new FakePropertyChanges();
        var dispatched = new List<Action>();
        var announcements = new List<string>();
        var notifier = new PropertyChangeLiveRegionNotifier(
            source,
            new Dictionary<string, Action>
            {
                [nameof(MainWindowViewModel.AccountStatusMessage)] = () => announcements.Add("account"),
                [nameof(MainWindowViewModel.StatusMessage)] = () => announcements.Add("operation"),
            },
            action => dispatched.Add(action));

        source.Raise(nameof(MainWindowViewModel.CurrentPageTitle));
        Assert.Empty(dispatched);

        source.Raise(nameof(MainWindowViewModel.AccountStatusMessage));
        var accountAnnouncement = Assert.Single(dispatched);
        Assert.Empty(announcements);
        accountAnnouncement();
        Assert.Equal(["account"], announcements);

        source.Raise(nameof(MainWindowViewModel.StatusMessage));
        Assert.Equal(2, dispatched.Count);
        notifier.Dispose();
        dispatched[1]();
        Assert.Equal(["account"], announcements);
        source.Raise(nameof(MainWindowViewModel.StatusMessage));
        Assert.Equal(2, dispatched.Count);
    }

    private static string FindRepositoryRoot([CallerFilePath] string sourceFilePath = "")
    {
        var current = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "BaiduBatchTransfer.sln")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("未找到测试仓库目录。");
    }

    private sealed class FakeLoginLauncher : IAccountLoginLauncher
    {
        public bool IsAvailableValue { get; init; }
        public bool Throw { get; init; }
        public int OpenCalls { get; private set; }
        public bool IsAvailable => IsAvailableValue;
        public string StatusMessage => IsAvailable ? string.Empty : "百度登录适配器尚不可用。";
        public Task OpenAsync(CancellationToken cancellationToken = default)
        {
            OpenCalls++;
            return Throw ? throw new InvalidOperationException("secret") : Task.CompletedTask;
        }
    }

    private sealed class FakePropertyChanges : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public void Raise(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private sealed class FakeManagementLauncher : IAccountManagementLauncher
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event EventHandler<AccountManagementRefreshEventArgs>? RefreshRequested
        {
            add { }
            remove { }
        }
        public AccountManagementRefreshResult Result { get; init; } = AccountManagementRefreshResult.None;
        public bool Delay { get; init; }
        public bool Throw { get; init; }
        public int OpenCalls { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<AccountManagementRefreshResult> OpenAsync(CancellationToken cancellationToken = default)
        {
            OpenCalls++;
            Started.TrySetResult();
            if (Throw) throw new InvalidOperationException("secret-account-id C:\\private\\profile token");
            if (Delay) await release.Task.WaitAsync(cancellationToken);
            return Result;
        }

        public void Complete() => release.TrySetResult();
    }
}
