using BaiduBatchTransfer.App;
using BaiduBatchTransfer.App.Features.Imports;
using BaiduBatchTransfer.App.Features.Jobs;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace BaiduBatchTransfer.App.Tests;

public sealed class AppBootstrapTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "BaiduBatchTransfer-AppBootstrapTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void HostCompositionRegistersUiServicesButNeverAFakeBaiduAdapter()
    {
        using var host = AppBootstrap.CreateHost(root);

        Assert.Null(host.Services.GetService<IBaiduNetdiskAdapter>());
        Assert.IsType<UnavailableAdapterAvailability>(host.Services.GetRequiredService<IAdapterAvailability>());
        Assert.IsType<UnavailableAccountLoginLauncher>(host.Services.GetRequiredService<IAccountLoginLauncher>());
        Assert.IsType<UnavailableImportWorkflow>(host.Services.GetRequiredService<IImportWorkflow>());
        Assert.NotNull(host.Services.GetRequiredService<IImportPreviewPageSource>());
        Assert.NotNull(host.Services.GetRequiredService<IWizardBatchService>());
        Assert.NotNull(host.Services.GetRequiredService<IAppShellFactory>());
        Assert.IsType<StartupRecoveryRunner>(host.Services.GetRequiredService<IAppRecoveryRunner>());
        Assert.NotNull(host.Services.GetRequiredService<ISingleInstanceGuard>());
    }

    [Fact]
    public void BootstrapUsesOnlyTheSuppliedLocalDataRoot()
    {
        using var host = AppBootstrap.CreateHost(root);

        var paths = host.Services.GetRequiredService<AppDataPaths>();

        Assert.StartsWith(Path.GetFullPath(root), paths.DatabasePath, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(Path.GetFullPath(root), paths.BackupDirectory, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
