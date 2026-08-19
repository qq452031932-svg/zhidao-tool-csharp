using System.Reflection;
using System.IO;
using BaiduBatchTransfer.App.Features.Imports;
using BaiduBatchTransfer.App.Features.Jobs;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Application.Imports;
using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Infrastructure.Database;
using BaiduBatchTransfer.Infrastructure.SystemIntegration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BaiduBatchTransfer.App;

public sealed record AppDataPaths
{
    public AppDataPaths(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.GetFullPath(rootDirectory);
        DatabasePath = EnsureChild(Path.Combine(RootDirectory, "data", "baidu-batch-transfer.db"));
        BackupDirectory = EnsureChild(Path.Combine(RootDirectory, "backups"));
    }

    public string RootDirectory { get; }
    public string DatabasePath { get; }
    public string BackupDirectory { get; }

    private string EnsureChild(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = RootDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("应用数据路径超出配置目录。 ");
        return fullPath;
    }
}

public static class AppBootstrap
{
    public static IHost CreateHost(string dataRoot)
    {
        var paths = new AppDataPaths(dataRoot);
        return Host.CreateDefaultBuilder()
            .ConfigureServices(services => ConfigureServices(services, paths))
            .Build();
    }

    internal static void ConfigureServices(IServiceCollection services, AppDataPaths paths)
    {
        services.AddSingleton(paths);
        services.AddSingleton(_ => new SqliteConnectionFactory(paths.DatabasePath));
        services.AddSingleton<IUnitOfWorkFactory, SqliteUnitOfWorkFactory>();
        services.AddSingleton(sp => new DatabaseBackupService(
            sp.GetRequiredService<SqliteConnectionFactory>(), paths.BackupDirectory));
        services.AddSingleton(sp => new DatabaseMigrator(
            sp.GetRequiredService<SqliteConnectionFactory>(),
            sp.GetRequiredService<DatabaseBackupService>(),
            Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0"));

        services.AddSingleton<ImportPageQuery>();
        services.AddSingleton<IImportPreviewPageSource, ImportPreviewPageSource>();
        services.AddSingleton<BatchDraftValidator>();
        services.AddSingleton<IDiskSpaceProbe, WindowsDiskSpaceProbe>();
        services.AddSingleton(sp => new ReplicationPreflightEstimator(
            sp.GetRequiredService<IDiskSpaceProbe>(), paths.DatabasePath));
        services.AddSingleton<CreateBatchService>();
        services.AddSingleton<IWizardBatchService, WizardBatchService>();

        // Task4 尚未通过真人扫码验证，因此这里明确保持不可用，也不注册 IBaiduNetdiskAdapter。
        services.AddSingleton<IAdapterAvailability, UnavailableAdapterAvailability>();
        services.AddSingleton<IAccountLoginLauncher, UnavailableAccountLoginLauncher>();
        services.AddSingleton<IImportWorkflow, UnavailableImportWorkflow>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IWizardAccountProvider, WizardAccountProvider>();
        services.AddSingleton<IAppShellFactory, AppShellFactory>();
    }
}

public interface IWizardAccountProvider
{
    Task<IReadOnlyList<WizardAccountOption>> LoadEligibleAsync(CancellationToken cancellationToken = default);
}

public sealed class WizardAccountProvider(IUnitOfWorkFactory unitOfWorkFactory) : IWizardAccountProvider
{
    public async Task<IReadOnlyList<WizardAccountOption>> LoadEligibleAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<WizardAccountOption>();
        var pageNumber = 1;
        while (true)
        {
            await using var unit = await unitOfWorkFactory.BeginAsync(cancellationToken).ConfigureAwait(false);
            var page = await unit.Accounts.GetPageAsync(new(pageNumber, 1_000), cancellationToken).ConfigureAwait(false);
            result.AddRange(page.Items
                .Where(account => account.Status == "Active" && account.IsEnabled && !account.IsDeleted)
                .Select(account => new WizardAccountOption(
                    account.Id,
                    account.DisplayName,
                    account.Status,
                    account.IsEnabled,
                    account.IsDeleted,
                    account.CapacityTotal,
                    account.CapacityUsed,
                    0, 0, 0, 0, 0, 0, 0, 0, 0)));
            if (pageNumber * page.PageSize >= page.TotalCount) break;
            pageNumber++;
        }
        return result;
    }
}

public interface IAppShellFactory
{
    Task<MainWindow> CreateAsync(CancellationToken cancellationToken = default);
}

public sealed class AppShellFactory(
    IWizardAccountProvider accountProvider,
    IImportWorkflow importWorkflow,
    IImportPreviewPageSource pageSource,
    IWizardBatchService batchService,
    IDialogService dialogService,
    IAdapterAvailability adapterAvailability,
    IAccountLoginLauncher loginLauncher) : IAppShellFactory
{
    public async Task<MainWindow> CreateAsync(CancellationToken cancellationToken = default)
    {
        var accounts = await accountProvider.LoadEligibleAsync(cancellationToken).ConfigureAwait(true);
        var importViewModel = new ImportViewModel(importWorkflow, pageSource, dialogService, adapterAvailability);
        var wizardViewModel = new CreateJobWizardViewModel(accounts, pageSource, batchService, dialogService, adapterAvailability);
        var importView = new ImportView(importViewModel);
        var wizardView = new CreateJobWizardView(wizardViewModel);
        var mainViewModel = new MainWindowViewModel(importView, wizardView, loginLauncher);
        return new MainWindow(mainViewModel);
    }
}
