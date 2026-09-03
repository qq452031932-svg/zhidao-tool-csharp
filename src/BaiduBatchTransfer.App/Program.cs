using BaiduBatchTransfer.App.Updates;
using Velopack;

namespace BaiduBatchTransfer.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        var updateRuntime = VelopackUpdateRuntime.Create(ApplicationBuildInfo.Current);
        var app = new App(updateRuntime);
        app.InitializeComponent();
        app.Run();
    }
}
