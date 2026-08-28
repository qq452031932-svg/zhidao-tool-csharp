using BaiduBatchTransfer.App.Features.ManagedBrowser;
using BaiduBatchTransfer.Application.Accounts;
using BaiduBatchTransfer.Core.Domain;
using System.IO;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class ManagedBrowserLoginUiTests
{
    [Fact]
    public void ManagedWindowBindsRecoveryCommandsAndFolderPickerUsesActiveOwner()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "src"))) root = root.Parent;
        Assert.NotNull(root);
        var features = Path.Combine(root.FullName, "src", "BaiduBatchTransfer.App", "Features", "ManagedBrowser");
        var xaml = File.ReadAllText(Path.Combine(features, "ManagedBrowserLoginWindow.xaml"));
        Assert.Contains("{Binding CheckLoginCommand}", xaml);
        Assert.Contains("{Binding ReselectDirectoryCommand}", xaml);
        Assert.Contains("{Binding CancelPendingCommand}", xaml);
        Assert.Contains("{Binding SelectedFamily}", xaml);
        Assert.Contains("{Binding CapabilityNotice}", xaml);
        var code = File.ReadAllText(Path.Combine(features, "ManagedBrowserLoginWindow.xaml.cs"));
        Assert.Contains("Dispatcher.BeginInvoke", code);
        Assert.DoesNotContain("Cookie", code);
        var picker = File.ReadAllText(Path.Combine(features, "BrowserDirectoryDialog.cs"));
        Assert.Contains("FirstOrDefault(window => window.IsActive)", picker);
    }
    [Fact]
    public void ManagedLoginHasDedicatedViewModelRatherThanLegacyCookieWindow()
    {
        Assert.NotNull(typeof(ManagedBrowserLoginLauncher).Assembly.GetType(
            "BaiduBatchTransfer.App.Features.ManagedBrowser.ManagedBrowserLoginViewModel"));
    }

    [Fact]
    public async Task RetryAndFamilyChangeKeepOneReservedAccountAndReselectIsExplicit()
    {
        var operations = new Operations();
        var request = ManagedBrowserLoginRequest.AddNew(Guid.NewGuid());
        var vm = new ManagedBrowserLoginViewModel(request, operations);
        Assert.Contains("首次使用请选择包含 chrome.exe 或 msedge.exe 的安装目录。", vm.Instructions);
        Assert.Contains("浏览器打开后，请在专用窗口中登录百度网盘。", vm.Instructions);
        Assert.Contains("尚未完成真实扫码识别验收", vm.CapabilityNotice);
        Assert.Null(vm.SelectedFamily);
        await vm.CheckLoginAsync();
        vm.SelectedFamily = BrowserFamily.Edge;
        await vm.CheckLoginAsync();
        await vm.ReselectDirectoryAsync();
        Assert.Equal(3, operations.Calls.Count);
        Assert.All(operations.Calls, call => Assert.Same(request, call.Request));
        Assert.Equal(new BrowserFamily?[] { null, BrowserFamily.Edge, BrowserFamily.Edge }, operations.Calls.Select(x => x.Family));
        Assert.Equal(new[] { false, false, true }, operations.Calls.Select(x => x.Reselect));
    }

    [Theory]
    [InlineData(ManagedBrowserLoginStatus.Pending)]
    [InlineData(ManagedBrowserLoginStatus.LoginRequired)]
    [InlineData(ManagedBrowserLoginStatus.BrowserUnavailable)]
    [InlineData(ManagedBrowserLoginStatus.AccountIdentityMismatch)]
    [InlineData(ManagedBrowserLoginStatus.TimedOut)]
    [InlineData(ManagedBrowserLoginStatus.Cancelled)]
    [InlineData(ManagedBrowserLoginStatus.BrowserFamilyChoiceRequired)]
    [InlineData(ManagedBrowserLoginStatus.SetupUnavailable)]
    public async Task TypedOutcomeIsVisibleAndNeverAutomaticallyRetriedOrBound(ManagedBrowserLoginStatus status)
    {
        var operations = new Operations { Status = status };
        var vm = new ManagedBrowserLoginViewModel(ManagedBrowserLoginRequest.ReloginExisting(Guid.NewGuid()), operations, BrowserFamily.Chrome);
        await vm.CheckLoginAsync();
        Assert.Equal(status, vm.Result.Status);
        Assert.Equal(new ManagedBrowserLoginResult(status).SafeMessage, vm.StatusMessage);
        Assert.False(vm.IsBusy);
        Assert.False(vm.IsBound);
        Assert.Equal(BrowserFamily.Chrome, operations.Calls.Single().Family);
    }

    [Fact]
    public async Task CancelPendingWaitLeavesWindowRetryableWithoutConcurrentAttempt()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operations = new Operations
        {
            Handler = async token => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return new(ManagedBrowserLoginStatus.Bound); }
        };
        var vm = new ManagedBrowserLoginViewModel(ManagedBrowserLoginRequest.AddNew(Guid.NewGuid()), operations);
        var waiting = vm.CheckLoginAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(vm.IsBusy);
        await vm.CheckLoginAsync();
        Assert.Single(operations.Calls);
        vm.CancelPending();
        await waiting.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(ManagedBrowserLoginStatus.Cancelled, vm.Result.Status);
        Assert.False(vm.IsBusy);
        operations.Handler = null;
        await vm.CheckLoginAsync();
        Assert.Equal(2, operations.Calls.Count);
    }

    [Fact]
    public async Task OnlyBoundResultDisablesFurtherChecksAndReselection()
    {
        var operations = new Operations { Status = ManagedBrowserLoginStatus.Bound };
        var vm = new ManagedBrowserLoginViewModel(ManagedBrowserLoginRequest.AddNew(Guid.NewGuid()), operations);
        await vm.CheckLoginAsync();
        Assert.True(vm.IsBound);
        await vm.CheckLoginAsync();
        await vm.ReselectDirectoryAsync();
        Assert.Single(operations.Calls);
    }

    [Fact]
    public async Task ClosedWindowDoesNotStartAgainAndUnexpectedFailureIsSanitized()
    {
        var operations = new Operations { Handler = _ => throw new InvalidOperationException("private UID path cookie") };
        var vm = new ManagedBrowserLoginViewModel(ManagedBrowserLoginRequest.AddNew(Guid.NewGuid()), operations);
        await vm.CheckLoginAsync();
        Assert.Equal(ManagedBrowserLoginStatus.BrowserUnavailable, vm.Result.Status);
        Assert.DoesNotContain("private", vm.StatusMessage);
        vm.EndLifetime();
        await vm.CheckLoginAsync();
        Assert.Single(operations.Calls);
    }

    private sealed class Operations : IManagedBrowserLoginOperations
    {
        public List<(ManagedBrowserLoginRequest Request, BrowserFamily? Family, bool Reselect)> Calls { get; } = [];
        public ManagedBrowserLoginStatus Status { get; set; } = ManagedBrowserLoginStatus.LoginRequired;
        public Func<CancellationToken, Task<ManagedBrowserLoginResult>>? Handler { get; set; }
        public Task<ManagedBrowserLoginResult> LaunchAsync(ManagedBrowserLoginRequest request, BrowserFamily? family = null,
            bool reselect = false, CancellationToken cancellationToken = default)
        {
            Calls.Add((request, family, reselect));
            return Handler?.Invoke(cancellationToken) ?? Task.FromResult(new ManagedBrowserLoginResult(Status));
        }
    }
}
