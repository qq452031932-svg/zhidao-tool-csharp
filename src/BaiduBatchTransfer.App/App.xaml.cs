using System.Windows;
using System.IO;
using BaiduBatchTransfer.Infrastructure.Database;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BaiduBatchTransfer.App;

public partial class App : System.Windows.Application
{
    private IHost? host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dataRoot = Path.Combine(localData, "BaiduBatchTransfer");
        host = AppBootstrap.CreateHost(dataRoot);
        try
        {
            await host.StartAsync().ConfigureAwait(true);
            _ = await host.Services.GetRequiredService<DatabaseMigrator>().MigrateAsync().ConfigureAwait(true);
            MainWindow = await host.Services.GetRequiredService<IAppShellFactory>().CreateAsync().ConfigureAwait(true);
            MainWindow.Show();
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
