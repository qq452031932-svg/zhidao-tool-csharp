using System.Windows;
using System.IO;
using System.ComponentModel;
using BaiduBatchTransfer.App.Features.BrowserAutomation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
#if DEBUG
using BaiduBatchTransfer.App.Features.Acceptance;
#endif

namespace BaiduBatchTransfer.App;

public partial class App : System.Windows.Application
{
    private IHost? host;
    private ApplicationShutdownCoordinator? shutdownCoordinator;
    private int shutdownRequested;
    private bool allowShutdown;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dataRoot = StartupDataRootSelector.Resolve(
            e.Args,
            Path.Combine(localData, "BaiduBatchTransfer"));
        host = AppBootstrap.CreateHost(dataRoot, e.Args);
        try
        {
            var startup = await host.Services.GetRequiredService<AppStartupController>()
                .StartAsync(token => host.StartAsync(token)).ConfigureAwait(true);
            if (!startup.CoordinatorMayStart)
            {
                MessageBox.Show(startup.StatusMessage, "百度批量转存", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }
            MainWindow = await host.Services.GetRequiredService<IAppShellFactory>().CreateAsync().ConfigureAwait(true);
            var configuredHost = host;
            shutdownCoordinator = new ApplicationShutdownCoordinator(
                configuredHost.Services.GetRequiredService<VisibleWebAutomationQueue>(),
                _ => configuredHost.StopAsync(TimeSpan.FromSeconds(5)),
                configuredHost);
            MainWindow.Closing += OnMainWindowClosing;
            MainWindow.Show();
#if DEBUG
            var probeOptions = BaiduAcceptanceProbeLaunchOptions.FromStartup(e.Args);
            if (probeOptions.IsEnabled)
            {
                var probe = await host.Services.GetRequiredService<IBaiduAcceptanceProbeLauncher>()
                    .OpenAsync(probeOptions).ConfigureAwait(true);
                if (!probe.Started)
                    MessageBox.Show(probe.SafeMessage, "百度现场验收", MessageBoxButton.OK, MessageBoxImage.Information);
                else if (probe.CleanupOutcome == BaiduAcceptanceProbeCleanupOutcome.Deferred)
                    MessageBox.Show("验收窗口已关闭，但隔离浏览器目录尚待清理。", "百度现场验收", MessageBoxButton.OK, MessageBoxImage.Information);
            }
#endif
        }
        catch (Exception)
        {
            MessageBox.Show(
                "应用启动失败，本机数据没有被删除。请关闭后重试；如果问题持续，请导出诊断信息。",
                "百度批量转存",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    private async void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        if (allowShutdown || shutdownCoordinator is null) return;
        e.Cancel = true;
        if (Interlocked.Exchange(ref shutdownRequested, 1) != 0) return;

        try
        {
            var completed = await shutdownCoordinator
                .ShutdownAsync(TimeSpan.FromSeconds(15))
                .ConfigureAwait(true);
            if (!completed)
            {
                Interlocked.Exchange(ref shutdownRequested, 0);
                MessageBox.Show(
                    "浏览器任务仍在安全关闭，请稍候再试。账号和任务记录仍受保护。",
                    "百度批量转存",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            allowShutdown = true;
            host = null;
            if (MainWindow is not null) MainWindow.Closing -= OnMainWindowClosing;
            Shutdown();
        }
        catch (Exception)
        {
            Interlocked.Exchange(ref shutdownRequested, 0);
            MessageBox.Show(
                "应用尚未安全关闭，请稍后重试。账号和任务记录仍受保护。",
                "百度批量转存",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (host is not null)
        {
            host.Dispose();
            host = null;
        }
        base.OnExit(e);
    }
}
