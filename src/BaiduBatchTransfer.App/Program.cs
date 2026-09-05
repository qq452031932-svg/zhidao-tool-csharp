using BaiduBatchTransfer.App.Updates;
using Velopack;

namespace BaiduBatchTransfer.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        if (SignedUpdateGuard.TryRun(args, ApplicationBuildInfo.Current, out var guardExitCode))
        {
            Environment.ExitCode = guardExitCode;
            return;
        }
        if (SignedUpdateGuardFailureMarker.TryConsume(ApplicationBuildInfo.Current.SignedGuardFailureMarkerPath))
        {
            System.Windows.MessageBox.Show(
                UpdateAwareShutdownOrchestrator.UpdaterLaunchFailureMessage,
                "软件更新",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        }
        var updateRuntime = VelopackUpdateRuntime.Create(ApplicationBuildInfo.Current);
        try
        {
            var app = new App(updateRuntime);
            app.InitializeComponent();
            app.Run();
        }
        finally
        {
            updateRuntime.Dispose();
        }
    }
}
