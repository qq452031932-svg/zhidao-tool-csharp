using BaiduBatchTransfer.App;
using BaiduBatchTransfer.App.Services;

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

    private sealed class FakeLoginLauncher : IAccountLoginLauncher
    {
        public bool IsAvailableValue { get; init; }
        public bool Throw { get; init; }
        public bool IsAvailable => IsAvailableValue;
        public string StatusMessage => IsAvailable ? string.Empty : "百度登录适配器尚不可用。";
        public Task OpenAsync(CancellationToken cancellationToken = default) =>
            Throw ? throw new InvalidOperationException("secret") : Task.CompletedTask;
    }
}
