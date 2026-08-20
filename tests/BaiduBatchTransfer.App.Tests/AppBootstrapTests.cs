using BaiduBatchTransfer.App;
using BaiduBatchTransfer.App.Features.Accounts;
using BaiduBatchTransfer.App.Features.Imports;
using BaiduBatchTransfer.App.Features.Jobs;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Application.Accounts;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Infrastructure.Database;
using Microsoft.Extensions.DependencyInjection;

namespace BaiduBatchTransfer.App.Tests;

public sealed class AppBootstrapTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "BaiduBatchTransfer-AppBootstrapTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void HostCompositionRegistersUiServicesButNeverAFakeBaiduAdapter()
    {
        using var host = AppBootstrap.CreateHost(root);

        Assert.IsType<BaiduBatchTransfer.BaiduAdapter.Http.LiveBaiduNetdiskAdapter>(
            host.Services.GetRequiredService<IBaiduNetdiskAdapter>());
        Assert.IsType<UnavailableAdapterAvailability>(host.Services.GetRequiredService<IAdapterAvailability>());
        Assert.IsType<BaiduAccountLoginLauncher>(host.Services.GetRequiredService<IAccountLoginLauncher>());
        Assert.IsType<LocalImportWorkflow>(host.Services.GetRequiredService<IImportWorkflow>());
        Assert.NotNull(host.Services.GetRequiredService<IImportPreviewPageSource>());
        Assert.NotNull(host.Services.GetRequiredService<IWizardBatchService>());
        Assert.NotNull(host.Services.GetRequiredService<IAppShellFactory>());
        Assert.IsType<StartupRecoveryRunner>(host.Services.GetRequiredService<IAppRecoveryRunner>());
        Assert.NotNull(host.Services.GetRequiredService<ISingleInstanceGuard>());
    }

    [Fact]
    public async Task LocalTextImportWorksWhileLiveTransfersRemainDisabled()
    {
        using var host = AppBootstrap.CreateHost(root);
        await host.Services.GetRequiredService<DatabaseMigrator>().MigrateAsync();
        var workflow = host.Services.GetRequiredService<IImportWorkflow>();

        var result = await workflow.ImportTextAsync(
            Guid.NewGuid(),
            "https://pan.baidu.com/s/abc123 提取码: a1b2",
            CancellationToken.None);

        Assert.Equal(1, result.Statistics.Total);
        Assert.Equal(1, result.Statistics.Ready);
        Assert.False(host.Services.GetRequiredService<IAdapterAvailability>().CanStartTransfers);
    }

    [Fact]
    public async Task MigratedHostResolvesTheRealAccountManagementComposition()
    {
        using var host = AppBootstrap.CreateHost(root);
        await host.Services.GetRequiredService<DatabaseMigrator>().MigrateAsync();

        Assert.IsType<SqliteAccountRemovalStore>(
            host.Services.GetRequiredService<IAccountRemovalStore>());
        Assert.NotNull(host.Services.GetRequiredService<RemoveAccountService>());
        Assert.NotNull(host.Services.GetRequiredService<AccountRemovalCoordinator>());
        Assert.IsType<AccountRemovalOperations>(
            host.Services.GetRequiredService<IAccountRemovalOperations>());
        Assert.IsType<WpfAccountManagementOwnedPrompt>(
            host.Services.GetRequiredService<IAccountManagementOwnedPrompt>());
        Assert.NotNull(host.Services.GetRequiredService<
            Func<IAccountManagementConfirmation, AccountManagementWindow>>());
        Assert.IsType<AccountManagementWindowPresenter>(
            host.Services.GetRequiredService<IAccountManagementWindowPresenter>());
        Assert.IsType<AccountManagementLauncher>(
            host.Services.GetRequiredService<IAccountManagementLauncher>());
    }

    [Fact]
    public void BootstrapUsesOnlyTheSuppliedLocalDataRoot()
    {
        using var host = AppBootstrap.CreateHost(root);

        var paths = host.Services.GetRequiredService<AppDataPaths>();

        Assert.StartsWith(Path.GetFullPath(root), paths.DatabasePath, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(Path.GetFullPath(root), paths.BackupDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(Path.GetFullPath(root), paths.ProfileDirectory, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
