using System.Windows;
using System.IO;
using System.ComponentModel;
using BaiduBatchTransfer.App.Features.BrowserAutomation;
using BaiduBatchTransfer.App.Features.ManagedBrowser;
using BaiduBatchTransfer.App.Updates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
#if DEBUG
using BaiduBatchTransfer.App.Features.Acceptance;
#endif

namespace BaiduBatchTransfer.App;

public partial class App : System.Windows.Application
{
    private readonly IApplicationUpdateRuntime updateRuntime;
    private IHost? host;
    private ApplicationShutdownCoordinator? shutdownCoordinator;
    private UpdateAwareShutdownOrchestrator? updateShutdownOrchestrator;
    private int shutdownRequested;
    private bool allowShutdown;

    public App(IApplicationUpdateRuntime updateRuntime)
    {
        this.updateRuntime = updateRuntime ?? throw new ArgumentNullException(nameof(updateRuntime));
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dataRoot = StartupDataRootSelector.Resolve(
            e.Args,
            Path.Combine(localData, "BaiduBatchTransfer"));
        host = AppBootstrap.CreateHost(dataRoot, e.Args, updateRuntime);
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
                configuredHost,
                configuredHost.Services.GetRequiredService<ManagedChromiumProcessManager>());
            updateShutdownOrchestrator = new UpdateAwareShutdownOrchestrator(
                ShutdownSafelyAsync,
                updateRuntime.LaunchPreparedUpdateAndRestart,
                ResumeAfterFailedShutdown,
                ShowUpdaterLaunchFailure,
                CompleteApplicationExit);
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
        if (allowShutdown || updateShutdownOrchestrator is null) return;
        e.Cancel = true;
        if (Interlocked.Exchange(ref shutdownRequested, 1) != 0) return;

        if (MainWindow is not null) MainWindow.IsEnabled = false;
        var completed = await updateShutdownOrchestrator
            .ShutdownAsync(CancellationToken.None)
            .ConfigureAwait(true);
        if (!completed)
            Interlocked.Exchange(ref shutdownRequested, 0);
    }

    private async Task<bool> ShutdownSafelyAsync(CancellationToken cancellationToken)
    {
        var coordinator = shutdownCoordinator;
        if (coordinator is null)
            return false;

        try
        {
            var completed = await coordinator
                .ShutdownAsync(TimeSpan.FromSeconds(15), cancellationToken)
                .ConfigureAwait(true);
            if (!completed)
            {
                MessageBox.Show(
                    "浏览器任务仍在安全关闭，请稍候再试。账号和任务记录仍受保护。",
                    "百度批量转存",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return false;
            }

            host = null;
            return true;
        }
        catch (Exception)
        {
            MessageBox.Show(
                "应用尚未安全关闭，请稍后重试。账号和任务记录仍受保护。",
                "百度批量转存",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }
    }

    private void ResumeAfterFailedShutdown()
    {
        if (MainWindow is not null) MainWindow.IsEnabled = true;
    }

    private void ShowUpdaterLaunchFailure(string message) => MessageBox.Show(
        message,
        "软件更新",
        MessageBoxButton.OK,
        MessageBoxImage.Error);

    private void CompleteApplicationExit()
    {
        allowShutdown = true;
        if (MainWindow is not null) MainWindow.Closing -= OnMainWindowClosing;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (host is not null)
            {
                host.Dispose();
                host = null;
            }
        }
        finally
        {
            try
            {
                updateRuntime.Dispose();
            }
            finally
            {
                base.OnExit(e);
            }
        }
    }
}
