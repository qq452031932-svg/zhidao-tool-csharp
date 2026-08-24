using System.Windows;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
#if DEBUG
using BaiduBatchTransfer.App.Features.Acceptance;
#endif

namespace BaiduBatchTransfer.App;

public partial class App : System.Windows.Application
{
    private IHost? host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dataRoot = StartupDataRootSelector.Resolve(
            e.Args,
            Path.Combine(localData, "BaiduBatchTransfer"));
        host = AppBootstrap.CreateHost(dataRoot);
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
            MainWindow.Show();
#if DEBUG
            var probeOptions = BaiduAcceptanceProbeLaunchOptions.FromStartup(e.Args);
            if (probeOptions.IsEnabled)
            {
                var probe = await host.Services.GetRequiredService<IBaiduAcceptanceProbeLauncher>()
                    .OpenAsync(probeOptions).ConfigureAwait(true);
                if (!probe.Started || probe.SafeMessage == BaiduAcceptanceProbeResultCodes.CleanupDeferred)
                    MessageBox.Show(probe.SafeMessage, "百度现场验收", MessageBoxButton.OK, MessageBoxImage.Information);
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

    protected override void OnExit(ExitEventArgs e)
    {
        if (host is not null)
        {
            try { host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); }
            catch (Exception) { /* 退出时不覆盖原始关闭原因。 */ }
            host.Dispose();
        }
        base.OnExit(e);
    }
}
