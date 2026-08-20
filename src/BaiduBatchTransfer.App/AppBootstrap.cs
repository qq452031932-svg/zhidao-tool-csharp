using System.Reflection;
using System.IO;
using BaiduBatchTransfer.App.Features.Imports;
using BaiduBatchTransfer.App.Features.Jobs;
using BaiduBatchTransfer.App.Features.Accounts;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Application.Accounts;
using BaiduBatchTransfer.Application.Imports;
using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.BaiduAdapter.Authentication;
using BaiduBatchTransfer.BaiduAdapter.Http;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Infrastructure.Database;
using BaiduBatchTransfer.Infrastructure.Excel;
using BaiduBatchTransfer.Infrastructure.Security;
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
        ProfileDirectory = EnsureChild(Path.Combine(RootDirectory, "profiles"));
    }

    public string RootDirectory { get; }
    public string DatabasePath { get; }
    public string BackupDirectory { get; }
    public string ProfileDirectory { get; }

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
    public static IHost CreateHost(string dataRoot, Action<IServiceCollection>? configure = null)
    {
        var paths = new AppDataPaths(dataRoot);
        return Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                ConfigureServices(services, paths);
                configure?.Invoke(services);
            })
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
        services.AddSingleton<DatabaseIntegrityService>();
        services.AddSingleton<IStartupDatabaseMaintenance, SqliteStartupDatabaseMaintenance>();
        services.AddSingleton<SqliteTransferSchedulingStore>();
        services.AddSingleton<IInterruptedOperationRecoveryStore>(sp => sp.GetRequiredService<SqliteTransferSchedulingStore>());
        services.AddSingleton<IAllocationCounterStore>(sp => sp.GetRequiredService<SqliteTransferSchedulingStore>());
        services.AddSingleton<AllocationCounterRebuilder>();
        services.AddSingleton<ISingleInstanceGuard>(_ =>
            new WindowsMutexSingleInstanceGuard(@"Local\BaiduBatchTransfer.Singleton.v1"));
        services.AddSingleton<StartupRecoveryService>();

        services.AddSingleton<ImportPageQuery>();
        services.AddSingleton<IImportPreviewPageSource, ImportPreviewPageSource>();
        services.AddSingleton<TextImportParser>();
        services.AddSingleton<OpenXmlImportReader>();
        services.AddSingleton<OpenXmlWorkbookInspector>();
        services.AddSingleton<ExcelColumnSuggestionEngine>();
        services.AddSingleton<IExcelColumnMappingPresenter, ExcelColumnMappingPresenter>();
        services.AddSingleton<IShareLinkResolver, DeferredShareLinkResolver>();
        services.AddSingleton(sp => new ImportStagingService(
            cancellationToken => sp.GetRequiredService<IUnitOfWorkFactory>().BeginAsync(cancellationToken),
            sp.GetRequiredService<IShareLinkResolver>()));
        services.AddSingleton<IImportWorkflow, LocalImportWorkflow>();
        services.AddSingleton<TransferModeReclassificationService>();
        services.AddSingleton<RequestedAccountMappingService>();
        services.AddSingleton<DraftRowEditingService>();
        services.AddSingleton<IWizardDraftWorkflow, WizardDraftWorkflow>();
        services.AddSingleton<IWizardPrecheckWorkflow, UnavailableWizardPrecheckWorkflow>();
        services.AddSingleton<BatchDraftValidator>();
        services.AddSingleton<IDiskSpaceProbe, WindowsDiskSpaceProbe>();
        services.AddSingleton(sp => new ReplicationPreflightEstimator(
            sp.GetRequiredService<IDiskSpaceProbe>(), paths.DatabasePath));
        services.AddSingleton<CreateBatchService>();
        services.AddSingleton<IWizardBatchService, WizardBatchService>();

        services.AddSingleton<BaiduSessionIdentityClient>();
        services.AddSingleton<IBaiduNetdiskAdapter, LiveBaiduNetdiskAdapter>();
        services.AddSingleton<IBaiduShareAdapter, LiveBaiduShareAdapter>();
        services.AddSingleton(LiveBaiduNetdiskAdapter.Capability);
        services.AddSingleton<WebViewCookieSessionBridge>();
        services.AddSingleton<ISessionProtector, DpapiSessionProtector>();
        services.AddSingleton(_ => new FileSystemAccountProfilePathPolicy(paths.ProfileDirectory));
        services.AddSingleton<IAccountProfilePathPolicy>(sp =>
            sp.GetRequiredService<FileSystemAccountProfilePathPolicy>());
        services.AddSingleton<WebView2ProfileCloser>();
        services.AddSingleton<IWebViewProfileCloser>(sp => sp.GetRequiredService<WebView2ProfileCloser>());
        services.AddSingleton<FileSystemWebViewProfileStore>();
        services.AddSingleton<IWebViewProfileStore>(sp => sp.GetRequiredService<FileSystemWebViewProfileStore>());
        services.AddSingleton<LoginProfileBootstrapper>();
        services.AddSingleton<AddAccountService>();
        services.AddSingleton<IBaiduLoginWindowPresenter, BaiduLoginWindowPresenter>();
        services.AddSingleton<IAccountLoginLauncher, BaiduAccountLoginLauncher>();

        services.AddSingleton<SqliteAccountRemovalStore>();
        services.AddSingleton<IAccountRemovalStore>(sp =>
            sp.GetRequiredService<SqliteAccountRemovalStore>());
        services.AddSingleton(sp => new RemoveAccountService(
            sp.GetRequiredService<IUnitOfWorkFactory>(),
            sp.GetRequiredService<IWebViewProfileStore>(),
            sp.GetRequiredService<IAccountProfilePathPolicy>()));
        // 管理窗口只走已在 SQLite 事务中完成凭据清除后的收尾路径。
        services.AddSingleton(sp => new AccountRemovalCoordinator(
            sp.GetRequiredService<IAccountRemovalStore>(),
            sp.GetRequiredService<IWebViewProfileStore>(),
            sp.GetRequiredService<IAccountProfilePathPolicy>()));
        services.AddSingleton<IAccountRemovalOperations, AccountRemovalOperations>();
        services.AddSingleton<IAccountManagementOwnedPrompt, WpfAccountManagementOwnedPrompt>();
        services.AddTransient<Func<IAccountManagementConfirmation, AccountManagementWindow>>(sp =>
            confirmation => new AccountManagementWindow(new AccountManagementViewModel(
                sp.GetRequiredService<IAccountRemovalOperations>(),
                confirmation)));
        services.AddSingleton<IAccountManagementWindowPresenter, AccountManagementWindowPresenter>();
        services.AddSingleton<IAccountManagementLauncher, AccountManagementLauncher>();

        // 真人扫码仅开放账号登录；实际转存方法尚未逐项通过现场门禁，继续禁止开始任务。
        services.AddSingleton<IAdapterAvailability, UnavailableAdapterAvailability>();
        services.AddSingleton<AppRuntimeState>();
        services.AddSingleton<IAppRecoveryRunner, StartupRecoveryRunner>();
        services.AddSingleton<AppStartupController>();
        services.AddSingleton<IUiDispatcher, WpfUiDispatcher>();
        services.AddSingleton<IExcelFileDialog, WpfExcelFileDialog>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IWizardAccountProvider, WizardAccountProvider>();
        services.AddSingleton<IAccountSummaryProvider, WizardAccountSummaryProvider>();
        services.AddSingleton<IImportWorkspaceFactory, ImportWorkspaceFactory>();
        services.AddSingleton<IAppShellFactory, AppShellFactory>();
    }
}

public interface IWizardAccountProvider
{
    Task<IReadOnlyList<WizardAccountOption>> LoadEligibleAsync(CancellationToken cancellationToken = default);
}

public sealed class WizardAccountSummaryProvider(SqliteConnectionFactory connectionFactory) : IAccountSummaryProvider
{
    public async Task<int> GetActiveAccountCountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Accounts WHERE Status='Active' AND IsEnabled=1 AND IsDeleted=0;";
        var count = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
        return checked((int)count);
    }
}

public sealed class WizardAccountProvider(IUnitOfWorkFactory unitOfWorkFactory) : IWizardAccountProvider
{
    private const int MaximumSnapshotSize = 1_000;

    public async Task<IReadOnlyList<WizardAccountOption>> LoadEligibleAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<WizardAccountOption>(MaximumSnapshotSize);
        var pageNumber = 1;
        while (result.Count < MaximumSnapshotSize)
        {
            await using var unit = await unitOfWorkFactory.BeginAsync(cancellationToken).ConfigureAwait(false);
            var page = await unit.Accounts
                .GetPageAsync(new(pageNumber, MaximumSnapshotSize), cancellationToken)
                .ConfigureAwait(false);
            foreach (var account in page.Items)
            {
                if (account.Status != "Active" || !account.IsEnabled || account.IsDeleted) continue;
                result.Add(new WizardAccountOption(
                    account.Id,
                    account.DisplayName,
                    account.Status,
                    account.IsEnabled,
                    account.IsDeleted,
                    account.CapacityTotal,
                    account.CapacityUsed,
                    0, 0, 0, 0, 0, 0, 0, 0, 0,
                    BatchAccountSelectionDecision.NotSelected));
                if (result.Count == MaximumSnapshotSize) break;
            }
            if (pageNumber * page.PageSize >= page.TotalCount) break;
            pageNumber++;
        }
        return result;
    }
}

public sealed class AccountRefreshCoordinator
{
    private readonly IWizardAccountProvider accountProvider;
    private readonly IAccountSummaryProvider accountSummaryProvider;
    private readonly Action<IReadOnlyList<WizardAccountOption>> applySnapshot;
    private readonly Func<Action, Task> dispatchAsync;
    private readonly object generationGate = new();
    private long refreshGeneration;

    public AccountRefreshCoordinator(
        IWizardAccountProvider accountProvider,
        IAccountSummaryProvider accountSummaryProvider,
        Action<IReadOnlyList<WizardAccountOption>> applySnapshot,
        Func<Action, Task> dispatchAsync)
    {
        this.accountProvider = accountProvider ?? throw new ArgumentNullException(nameof(accountProvider));
        this.accountSummaryProvider = accountSummaryProvider ?? throw new ArgumentNullException(nameof(accountSummaryProvider));
        this.applySnapshot = applySnapshot ?? throw new ArgumentNullException(nameof(applySnapshot));
        this.dispatchAsync = dispatchAsync ?? throw new ArgumentNullException(nameof(dispatchAsync));
    }

    public async Task RefreshAsync(
        Action<int> applyActiveCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(applyActiveCount);
        long generation;
        lock (generationGate) generation = ++refreshGeneration;

        var snapshotTask = accountProvider.LoadEligibleAsync(cancellationToken);
        var countTask = accountSummaryProvider.GetActiveAccountCountAsync(cancellationToken);
        await Task.WhenAll(snapshotTask, countTask).ConfigureAwait(false);
        var snapshot = await snapshotTask.ConfigureAwait(false);
        var activeCount = await countTask.ConfigureAwait(false);

        await dispatchAsync(() =>
        {
            lock (generationGate)
            {
                if (generation != refreshGeneration) return;
                applySnapshot(snapshot);
                applyActiveCount(activeCount);
            }
        }).ConfigureAwait(false);
    }
}

public interface IImportWorkspaceFactory
{
    Task<ImportViewModel> CreateAsync(CancellationToken cancellationToken = default);
}

public sealed class ImportWorkspaceFactory(
    IImportWorkflow workflow,
    IImportPreviewPageSource pageSource,
    IDialogService dialogService,
    IAdapterAvailability adapterAvailability) : IImportWorkspaceFactory
{
    public async Task<ImportViewModel> CreateAsync(CancellationToken cancellationToken = default)
    {
        var viewModel = new ImportViewModel(workflow, pageSource, dialogService, adapterAvailability);
        await viewModel.InitializeAsync(cancellationToken).ConfigureAwait(true);
        return viewModel;
    }
}

public interface IAppShellFactory
{
    Task<MainWindow> CreateAsync(CancellationToken cancellationToken = default);
}

public sealed class AppShellFactory(
    IWizardAccountProvider accountProvider,
    IImportWorkspaceFactory importWorkspaceFactory,
    IImportPreviewPageSource pageSource,
    IWizardBatchService batchService,
    IWizardDraftWorkflow draftWorkflow,
    IWizardPrecheckWorkflow precheckWorkflow,
    IDialogService dialogService,
    IAdapterAvailability adapterAvailability,
    IAccountLoginLauncher loginLauncher,
    IAccountManagementLauncher accountManagementLauncher,
    IAccountSummaryProvider accountSummaryProvider,
    AppRuntimeState runtimeState) : IAppShellFactory
{
    public async Task<MainWindow> CreateAsync(CancellationToken cancellationToken = default)
    {
        var accounts = runtimeState.CoordinatorMayStart
            ? await accountProvider.LoadEligibleAsync(cancellationToken).ConfigureAwait(true)
            : [];
        var initialActiveAccountCount = runtimeState.CoordinatorMayStart
            ? await accountSummaryProvider.GetActiveAccountCountAsync(cancellationToken).ConfigureAwait(true)
            : 0;
        var importViewModel = await importWorkspaceFactory.CreateAsync(cancellationToken).ConfigureAwait(true);
        var wizardViewModel = new CreateJobWizardViewModel(
            accounts, pageSource, batchService, dialogService, adapterAvailability, draftWorkflow, precheckWorkflow);

        async Task DispatchOnUiAsync(Action action)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess()) action();
            else await dispatcher.InvokeAsync(action);
        }
        var accountRefresh = new AccountRefreshCoordinator(
            accountProvider,
            accountSummaryProvider,
            wizardViewModel.ReloadAccounts,
            DispatchOnUiAsync);
        importViewModel.DraftReady += (_, draft) => wizardViewModel.Initialize(new ImportWizardContext(
            draft.SessionId,
            $"批量转存 {DateTime.Now:yyyy-MM-dd HH:mm}",
            draft.Statistics.Ready,
            draft.Statistics.HasExcelAccountColumn,
            draft.Statistics.UnresolvedAccountMappings,
            draft.Statistics.Conflict,
            draft.Statistics.NeedsResolution,
            "/我的资源"));
        var importView = new ImportView(importViewModel);
        var wizardView = new CreateJobWizardView(wizardViewModel);
        var mainViewModel = new MainWindowViewModel(
            importView,
            wizardView,
            loginLauncher,
            accountManagementLauncher,
            accountRefresh.RefreshAsync,
            initialActiveAccountCount);
        return new MainWindow(mainViewModel);
    }
}
