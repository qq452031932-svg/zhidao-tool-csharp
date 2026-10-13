using System.Windows;
using System.IO;
using System.ComponentModel;
using BaiduBatchTransfer.App.Features.BrowserAutomation;
using BaiduBatchTransfer.App.Features.ManagedBrowser;
using BaiduBatchTransfer.App.Features.Licensing;
using BaiduBatchTransfer.App.Services;
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
    private ApplicationStoppingShutdownCoordinator? applicationStoppingShutdownCoordinator;
    private UpdateAwareShutdownOrchestrator? updateShutdownOrchestrator;
    private int shutdownRequested;
    private bool allowShutdown;
    private IRunLog? runLog;

    public App(IApplicationUpdateRuntime updateRuntime)
    {
        this.updateRuntime = updateRuntime ?? throw new ArgumentNullException(nameof(updateRuntime));
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (LicenseAcceptanceSmokeRunner.IsRequested(e.Args))
        {
            var smoke = await LicenseAcceptanceSmokeRunner
                .RunAsync(e.Args, LicenseRuntimeBuildInfo.Current)
                .ConfigureAwait(true);
            Environment.ExitCode = smoke.ExitCode;
            Shutdown(smoke.ExitCode);
            return;
        }
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dataRoot = StartupDataRootSelector.Resolve(
            e.Args,
            Path.Combine(localData, "BaiduBatchTransfer"));
        host = AppBootstrap.CreateHost(dataRoot, e.Args, updateRuntime);
        runLog = host.Services.GetService<IRunLog>();
        runLog?.Write(RunLogSource, $"软件启动，版本 {ApplicationBuildInfo.Current.ProductVersion}。");
        // 只记录，不接管：未处理异常原来怎么表现现在还怎么表现，日志里多一份现场。
        DispatcherUnhandledException += (_, args) => LogUnhandled("界面线程出现未处理的异常", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogUnhandled("出现未处理的异常，软件即将退出", args.ExceptionObject as Exception);
        try
        {
            var startup = await host.Services.GetRequiredService<AppStartupController>()
                .StartAsync(token => host.StartAsync(token)).ConfigureAwait(true);
            if (!startup.CoordinatorMayStart)
            {
                runLog?.Write(RunLogSource, $"启动未继续：{startup.StatusMessage}");
                MessageBox.Show(startup.StatusMessage, "知道综合工具", MessageBoxButton.OK, MessageBoxImage.Information);
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
            applicationStoppingShutdownCoordinator = new ApplicationStoppingShutdownCoordinator(
                configuredHost.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping,
                () => Interlocked.Exchange(ref shutdownRequested, 1) == 0,
                operation => Dispatcher.InvokeAsync(operation).Task.Unwrap(),
                () => ShutdownSafelyAsync(CancellationToken.None),
                CompleteApplicationExit,
                FailClosedApplicationStop);
            updateShutdownOrchestrator = new UpdateAwareShutdownOrchestrator(
                ShutdownSafelyAsync,
                updateRuntime.LaunchPreparedUpdateAndRestart,
                ResumeAfterFailedShutdown,
                ShowUpdaterLaunchFailure,
                CompleteApplicationExit);
            MainWindow.Closing += OnMainWindowClosing;
            MainWindow.Show();
            runLog?.Write(RunLogSource, "主窗口已打开。");
            // 只有服务器明确说"这台机器需要卡密"时才弹激活窗。连不上服务器时弹窗
            // 等于告诉付费用户"你没激活"，而他什么都没做错——网络恢复后会自动通过。
            if (startup.ActivationInputRequired)
            {
                await host.Services.GetRequiredService<ILicenseActivationPresenter>()
                    .PresentAsync().ConfigureAwait(true);
            }
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
        catch (Exception exception)
        {
            try
            {
                var logDirectory = Path.Combine(dataRoot, "logs");
                Directory.CreateDirectory(logDirectory);
                File.WriteAllText(
                    Path.Combine(logDirectory, "startup-error.txt"),
                    exception.ToString());
            }
            catch
            {
                // Startup diagnostics must never hide the original failure prompt.
            }
            LogUnhandled("启动失败", exception);
            MessageBox.Show(
                "应用启动失败，本机数据没有被删除。请关闭后重试；如果问题持续，请导出诊断信息。",
                "知道综合工具",
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
            if (MainWindow?.DataContext is MainWindowViewModel mainViewModel)
            {
                var preferenceFlushed = await mainViewModel
                    .SealAndFlushShareAnalyticsPreferenceAsync(TimeSpan.FromSeconds(2))
                    .ConfigureAwait(true);
                if (!preferenceFlushed)
                {
                    MessageBox.Show(
                        "账号查看偏好尚未保存，请稍候后重试关闭。",
                        "知道综合工具",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return false;
                }
            }

            var completed = await coordinator
                .ShutdownAsync(TimeSpan.FromSeconds(15), cancellationToken)
                .ConfigureAwait(true);
            if (!completed)
            {
                MessageBox.Show(
                    "浏览器任务仍在安全关闭，请稍候再试。账号和任务记录仍受保护。",
                    "知道综合工具",
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
                "知道综合工具",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }
    }

    private void ResumeAfterFailedShutdown()
    {
        if (MainWindow?.DataContext is MainWindowViewModel mainViewModel)
            mainViewModel.ResumeShareAnalyticsPreferenceAfterFailedShutdown();
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

    private void FailClosedApplicationStop()
    {
        Environment.ExitCode = -1;
        allowShutdown = true;
        if (MainWindow is not null)
        {
            MainWindow.Closing -= OnMainWindowClosing;
            MainWindow.IsEnabled = false;
        }
        Shutdown(-1);
    }

    private const string RunLogSource = "软件";

    private void LogUnhandled(string what, Exception? exception) =>
        runLog?.Write(RunLogSource, exception is null ? $"{what}。" : $"{what}：{exception}");

    protected override void OnExit(ExitEventArgs e)
    {
        runLog?.Write(RunLogSource, $"软件关闭，退出码 {e.ApplicationExitCode}。");
        try
        {
            applicationStoppingShutdownCoordinator?.Dispose();
            applicationStoppingShutdownCoordinator = null;
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
