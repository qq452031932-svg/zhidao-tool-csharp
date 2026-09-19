namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class SettingsViewStructureTests
{
    [Fact]
    public void SettingsViewContainsOnlyTheRealOtaUpdateCard()
    {
        var root = FindRepositoryRoot();
        var xamlPath = Path.Combine(
            root,
            "src",
            "BaiduBatchTransfer.App",
            "Features",
            "Settings",
            "SettingsView.xaml");
        var xaml = File.ReadAllText(xamlPath);

        // 标题里不再写 OTA：那是内部词，副标题已经说清楚这块是干什么的。
        Assert.Contains("Text=\"软件更新\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("OTA 在线更新", xaml, StringComparison.Ordinal);
        Assert.Contains("当前版本", xaml, StringComparison.Ordinal);
        Assert.Contains("更新状态", xaml, StringComparison.Ordinal);
        Assert.Contains("最新版本", xaml, StringComparison.Ordinal);
        Assert.Contains("更新说明", xaml, StringComparison.Ordinal);
        Assert.Contains("ProgressBar", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "Value=\"{Binding DownloadProgress, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("ScrollViewer", xaml, StringComparison.Ordinal);
        Assert.Contains("MaxHeight=", xaml, StringComparison.Ordinal);
        Assert.Contains("VerticalScrollBarVisibility=\"Auto\"", xaml, StringComparison.Ordinal);
        Assert.Contains("CheckForUpdatesCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("UpdateAndRestartCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding BuildLabel}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("检查更新", xaml, StringComparison.Ordinal);
        Assert.Contains("立即更新并重启", xaml, StringComparison.Ordinal);
        Assert.Contains("StaticResource BorderedPanelStyle", xaml, StringComparison.Ordinal);
        Assert.Contains("StaticResource SecondaryButtonStyle", xaml, StringComparison.Ordinal);
        Assert.Contains("StaticResource BrandBrush", xaml, StringComparison.Ordinal);
        Assert.Contains("StaticResource MutedBrush", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.Name=\"{Binding StatusMessage, StringFormat=更新状态：{0}}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"更新下载进度\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.HelpText=", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("功能建设中", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("服务器地址", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("CheckBox", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void CodeBehindContainsNoUpdateOrProcessShutdownBusinessLogic()
    {
        var root = FindRepositoryRoot();
        var codePath = Path.Combine(
            root,
            "src",
            "BaiduBatchTransfer.App",
            "Features",
            "Settings",
            "SettingsView.xaml.cs");
        var code = File.ReadAllText(codePath);

        Assert.Contains("InitializeComponent();", code, StringComparison.Ordinal);
        Assert.DoesNotContain("DownloadAsync", code, StringComparison.Ordinal);
        Assert.DoesNotContain("TryPrepareRestart", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Environment.Exit", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Application.Current", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Window.Close", code, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "BaiduBatchTransfer.sln")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
