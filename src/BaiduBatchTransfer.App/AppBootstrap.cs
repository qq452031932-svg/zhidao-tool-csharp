using System.Reflection;
using System.IO;
using BaiduBatchTransfer.App.Features.Imports;
using BaiduBatchTransfer.App.Features.Jobs;
using BaiduBatchTransfer.App.Features.Common;
using BaiduBatchTransfer.App.Features.Transfers;
using BaiduBatchTransfer.App.Features.Shares;
using BaiduBatchTransfer.App.Features.Acceptance;
using BaiduBatchTransfer.App.Features.Accounts;
using BaiduBatchTransfer.App.Features.BrowserAutomation;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Application.Accounts;
using BaiduBatchTransfer.Application.Imports;
using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.BaiduAdapter.Authentication;
using BaiduBatchTransfer.BaiduAdapter.Http;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Infrastructure.Database;
using BaiduBatchTransfer.Infrastructure.Database.Repositories;
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
        services.AddSingleton<SqliteShareSchedulingStore>();
        services.AddSingleton<IInterruptedOperationRecoveryStore>(sp => sp.GetRequiredService<SqliteTransferSchedulingStore>());
        services.AddSingleton<IInterruptedShareOperationRecoveryStore>(sp => sp.GetRequiredService<SqliteShareSchedulingStore>());
        services.AddSingleton<IAllocationCounterStore>(sp => sp.GetRequiredService<SqliteTransferSchedulingStore>());
        services.AddSingleton<IOperationReconciliationStore>(sp => sp.GetRequiredService<SqliteTransferSchedulingStore>());
        services.AddSingleton<IJobResultTransferActionStore>(sp => sp.GetRequiredService<SqliteTransferSchedulingStore>());
        services.AddSingleton<IBatchRunControlStore>(sp => sp.GetRequiredService<SqliteTransferSchedulingStore>());
        services.AddSingleton<IShareReconciliationStore>(sp => sp.GetRequiredService<SqliteShareSchedulingStore>());
        services.AddSingleton<IExistingShareDecisionStore>(sp => sp.GetRequiredService<SqliteShareSchedulingStore>());
        services.AddSingleton<IJobResultShareActionStore>(sp => sp.GetRequiredService<SqliteShareSchedulingStore>());
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
        services.AddSingleton<BulkPrecheckService>();
        services.AddSingleton<IBulkPrecheckRunner>(sp => sp.GetRequiredService<BulkPrecheckService>());
        services.AddSingleton<IWizardPrecheckSnapshotQuery, LocalWizardPrecheckSnapshotQuery>();
        services.AddSingleton<IWizardPrecheckWorkflow, BulkWizardPrecheckWorkflow>();
        services.AddSingleton<BatchDraftValidator>();
        services.AddSingleton<IDiskSpaceProbe, WindowsDiskSpaceProbe>();
        services.AddSingleton(sp => new ReplicationPreflightEstimator(
            sp.GetRequiredService<IDiskSpaceProbe>(), paths.DatabasePath));
        services.AddSingleton<CreateBatchService>();
        services.AddSingleton<IWizardBatchService, WizardBatchService>();
        services.AddSingleton<JobPageQuery>();
        services.AddSingleton<BatchStatisticsQuery>();
        services.AddSingleton<BatchHistoryQuery>();
        services.AddSingleton<IWorkspaceBatchHistorySource, WorkspaceBatchHistorySource>();
        services.AddSingleton<IRemoteResourcePicker, UnavailableRemoteResourcePicker>();
        services.AddSingleton<SqliteStandaloneShareStore>();
        services.AddSingleton<IStandaloneShareStore>(sp =>
            sp.GetRequiredService<SqliteStandaloneShareStore>());
        services.AddSingleton<IStandaloneShareExecutionStore>(sp =>
            sp.GetRequiredService<SqliteStandaloneShareStore>());
        services.AddSingleton<SqliteWebAutomationAttemptStore>();
        services.AddSingleton<IWebAutomationAttemptStore>(sp =>
            sp.GetRequiredService<SqliteWebAutomationAttemptStore>());
        services.AddSingleton<SqliteWebAutomationRequestOwnershipStore>();
        services.AddSingleton<IWebAutomationRequestOwnershipStore>(sp =>
            sp.GetRequiredService<SqliteWebAutomationRequestOwnershipStore>());
        services.AddSingleton<SqliteLocalResultDestinationStore>();
        services.AddSingleton<ILocalResultDestinationStore>(sp =>
            sp.GetRequiredService<SqliteLocalResultDestinationStore>());
        services.AddSingleton<SqliteLocalResultExportStore>();
        services.AddSingleton<ILocalResultExportStore>(sp =>
            sp.GetRequiredService<SqliteLocalResultExportStore>());
        services.AddSingleton<PauseBatchService>();
        services.AddSingleton<ResumeBatchService>();
        services.AddSingleton<RetryFailedTransferService>();
        services.AddSingleton<SqliteBatchResultProjectionRepository>();
        services.AddSingleton<IBatchResultProjectionRepository>(sp =>
            sp.GetRequiredService<SqliteBatchResultProjectionRepository>());
        services.AddSingleton<ReplacementWorkbookFileNameBuilder>();
        services.AddSingleton<ReplacementWorkbookExportService>();
        services.AddSingleton<IReplacementWorkbookWriter, OpenXmlReplacementResultWriter>();
        services.AddSingleton<IReplacementWorkbookFilePublisher, OpenXmlReplacementWorkbookFilePublisher>();

        services.AddSingleton<BaiduSessionIdentityClient>();
        services.AddSingleton<LiveBaiduNetdiskAdapter>();
        services.AddSingleton<LiveBaiduShareAdapter>();
        services.AddSingleton<BaiduAdapterConcurrencyGate>();
        services.AddSingleton<IBaiduNetdiskAdapter>(sp => new CoordinatedBaiduNetdiskAdapter(
            sp.GetRequiredService<LiveBaiduNetdiskAdapter>(),
            sp.GetRequiredService<BaiduAdapterConcurrencyGate>()));
        services.AddSingleton<IBaiduShareAdapter>(sp => new CoordinatedBaiduShareAdapter(
            sp.GetRequiredService<LiveBaiduShareAdapter>(),
            sp.GetRequiredService<BaiduAdapterConcurrencyGate>()));
        services.AddSingleton(LiveBaiduNetdiskAdapter.Capability);
        services.AddSingleton<WebViewCookieSessionBridge>();
        services.AddSingleton<ISessionProtector, DpapiSessionProtector>();
        services.AddSingleton<EncryptedBaiduSessionStore>();
        services.AddSingleton<IBaiduSessionStore>(sp => sp.GetRequiredService<EncryptedBaiduSessionStore>());
        services.AddSingleton(_ => new FileSystemAccountProfilePathPolicy(paths.ProfileDirectory));
        services.AddSingleton<IAccountProfilePathPolicy>(sp =>
            sp.GetRequiredService<FileSystemAccountProfilePathPolicy>());
        services.AddSingleton<WebView2ProfileCloser>();
        services.AddSingleton<IWebViewProfileCloser>(sp => sp.GetRequiredService<WebView2ProfileCloser>());
        services.AddSingleton<IWebAutomationNavigationAuthorizer, ProductionWebAutomationNavigationAuthorizer>();
        services.AddSingleton<IWebAutomationBatchCancellation, UnavailableWebAutomationBatchCancellation>();
        services.AddSingleton<IWebAutomationUiDispatcher, WpfWebAutomationUiDispatcher>();
        services.AddSingleton<IVisibleWebAutomationSessionFactory, WebView2AutomationSessionFactory>();
        services.AddSingleton(sp => new UserHandoffCoordinator(
            sp.GetRequiredService<IWebAutomationAttemptStore>(),
            sp.GetRequiredService<IWebAutomationBatchCancellation>(),
            TimeProvider.System));
        services.AddSingleton<VisibleWebAutomationQueue>();
        services.AddSingleton<IVisibleWebAutomationBroker>(sp =>
            sp.GetRequiredService<VisibleWebAutomationQueue>());
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
        services.AddSingleton<IJobResultCapabilityAvailability, UnavailableJobResultCapabilityAvailability>();
        services.AddSingleton<IJobResultActionCapability, UnavailableJobResultActionCapability>();
        services.AddSingleton<IAsyncDelay>(_ => new TimeProviderDelay(TimeProvider.System));
        services.AddSingleton<OperationReconciliationService>();
        services.AddSingleton<ITransferResultReconciler>(sp => sp.GetRequiredService<OperationReconciliationService>());
        services.AddSingleton<ShareOperationReconciliationService>();
        services.AddSingleton<IShareResultReconciler>(sp => sp.GetRequiredService<ShareOperationReconciliationService>());
        services.AddSingleton<IJobResultActions, JobResultActions>();
        services.AddSingleton<IJobResultsWorkflow, LocalJobResultsWorkflow>();
        services.AddSingleton<ExistingShareDecisionService>();
        services.AddSingleton<IExistingShareDecisionPresenter, ExistingShareDecisionPresenter>();
        services.AddSingleton<IExistingShareDecisionApplication, ExistingShareDecisionApplication>();
        services.AddSingleton<IExistingShareDecisionInteraction, ExistingShareDecisionInteraction>();
        services.AddSingleton<AppRuntimeState>();
        services.AddSingleton<IAppRecoveryRunner, StartupRecoveryRunner>();
        services.AddSingleton<AppStartupController>();
        services.AddSingleton<IUiDispatcher, WpfUiDispatcher>();
        services.AddSingleton<IExcelFileDialog, WpfExcelFileDialog>();
        services.AddSingleton<IFolderDialog, WpfFolderDialog>();
        services.AddSingleton<IResultSaveDialog, ResultSaveDialog>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<DurableTransferResultDestinationSink>();
        services.AddSingleton<ITransferResultDestinationSink>(sp =>
            sp.GetRequiredService<DurableTransferResultDestinationSink>());
#if DEBUG
        services.AddSingleton(new BaiduAcceptanceProbeOutputStore(paths.RootDirectory));
        services.AddSingleton<IBaiduAcceptanceProbeOutputStore>(serviceProvider =>
            serviceProvider.GetRequiredService<BaiduAcceptanceProbeOutputStore>());
        services.AddSingleton<IBaiduAcceptanceProbeAccountSelector, BaiduAcceptanceProbeAccountSelector>();
        services.AddSingleton<IBaiduAcceptanceProbeProfilePathPolicy>(_ =>
            new BaiduAcceptanceProbeProfilePathPolicy(paths.RootDirectory));
        services.AddSingleton<IBaiduAcceptanceProbeDeferredCleanupRegistry, BaiduAcceptanceProbeDeferredCleanupRegistry>();
        services.AddSingleton<IBaiduAcceptanceProbeUiDispatcher, WpfBaiduAcceptanceProbeUiDispatcher>();
        services.AddSingleton<IBaiduAcceptanceProbeWindowFactory, BaiduAcceptanceProbeWindowFactory>();
        services.AddSingleton<IBaiduAcceptanceProbePresenter, BaiduAcceptanceProbePresenter>();
        services.AddSingleton<IBaiduAcceptanceProbeLauncher, BaiduAcceptanceProbeLauncher>();
#endif
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
    IJobResultsWorkflow jobResultsWorkflow,
    IResultSaveDialog resultSaveDialog,
    IJobResultCapabilityAvailability resultCapabilities,
    IExistingShareDecisionInteraction existingShareInteraction,
    PauseBatchService pauseBatchService,
    ResumeBatchService resumeBatchService,
    RetryFailedTransferService retryFailedTransferService,
    IWorkspaceBatchHistorySource batchHistorySource,
    ITransferResultDestinationSink resultDestinationSink,
    IRemoteResourcePicker remoteResourcePicker,
    IStandaloneShareStore standaloneShareStore,
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
            accounts, pageSource, batchService, dialogService, adapterAvailability, draftWorkflow, precheckWorkflow,
            resultCapabilities);
        var resultsViewModel = new JobResultsViewModel(
            jobResultsWorkflow, resultSaveDialog, resultCapabilities, existingShareInteraction);
        var transferOptions = new TransferWorkspaceOptions().Validate();
        var runPanelViewModel = new WorkspaceRunPanelViewModel(
            resultsViewModel,
            pauseBatchService,
            resumeBatchService,
            retryFailedTransferService,
            transferOptions.MaximumRecentBatches,
            batchHistorySource);

        async Task DispatchOnUiAsync(Action action)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess()) action();
            else await dispatcher.InvokeAsync(action);
        }
        var shellLifetime = new CancellationTokenSource();
        var shellClosed = 0;
        MainWindowViewModel? mainViewModel = null;

        async Task DispatchHandoffAsync(
            Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                await operation(cancellationToken).ConfigureAwait(true);
                return;
            }

            var dispatched = dispatcher.InvokeAsync(
                () => operation(cancellationToken),
                System.Windows.Threading.DispatcherPriority.Normal,
                cancellationToken);
            var nestedOperation = await dispatched.Task.ConfigureAwait(false);
            await nestedOperation.ConfigureAwait(false);
        }

        Task NavigateToResultsAsync(Guid batchJobId, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested || Volatile.Read(ref shellClosed) != 0)
                return Task.CompletedTask;
            runPanelViewModel.PresentBatch(batchJobId);
            mainViewModel?.ShowTransferCommand.Execute(null);
            return Task.CompletedTask;
        }

        async Task ReportHandoffFailureAsync()
        {
            try
            {
                await DispatchOnUiAsync(() =>
                {
                    if (shellLifetime.IsCancellationRequested || Volatile.Read(ref shellClosed) != 0) return;
                    mainViewModel?.ReportHandoffFailure();
                }).ConfigureAwait(true);
            }
            catch { }
        }

        var handoff = new ResultWorkspaceHandoffCoordinator(
            new JobResultsWorkspaceHandoffOperations(resultsViewModel, NavigateToResultsAsync),
            DispatchHandoffAsync,
            exception => { _ = ReportHandoffFailureAsync(); });

        var transferViewModel = new TransferWorkspaceViewModel(
            importViewModel,
            wizardViewModel,
            runPanelViewModel,
            resultSaveDialog,
            resultDestinationSink,
            (batchJobId, token) => handoff.ActivateJobAsync(batchJobId, token),
            transferOptions);
        var transferWorkspace = new TransferWorkspaceView(transferViewModel);
        var shareViewModel = new ShareWorkspaceViewModel(
            accounts.Select(account => new ShareAccountOption(account.AccountId, account.DisplayName)).ToArray(),
            remoteResourcePicker,
            standaloneShareStore,
            resultSaveDialog);
        var shareWorkspace = new ShareWorkspaceView(shareViewModel);
        var accountRefresh = new AccountRefreshCoordinator(
            accountProvider,
            accountSummaryProvider,
            snapshot =>
            {
                wizardViewModel.ReloadAccounts(snapshot);
                shareViewModel.ReloadAccounts(snapshot
                    .Select(account => new ShareAccountOption(account.AccountId, account.DisplayName))
                    .ToArray());
            },
            DispatchOnUiAsync);
        mainViewModel = new MainWindowViewModel(
            transferWorkspace,
            shareWorkspace,
            loginLauncher,
            accountManagementLauncher,
            accountRefresh.RefreshAsync,
            initialActiveAccountCount);
        var mainWindow = new MainWindow(mainViewModel);

        static async Task ObserveAsync(Task task)
        {
            try { await task.ConfigureAwait(true); }
            catch { }
        }

        void Observe(Task task) => _ = ObserveAsync(task);

        var decisionPoll = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2),
        };

        void OnDecisionPollTick(object? _, EventArgs __)
        {
            if (Volatile.Read(ref shellClosed) != 0) return;
            Observe(handoff.RestorePendingAsync());
        }

        async Task HandleLoadedAsync()
        {
            await runPanelViewModel.InitializeAsync(shellLifetime.Token).ConfigureAwait(true);
            await shareViewModel.InitializeAsync(shellLifetime.Token).ConfigureAwait(true);
            await handoff.RestorePendingAsync().ConfigureAwait(true);
            await DispatchOnUiAsync(() =>
            {
                if (shellLifetime.IsCancellationRequested || Volatile.Read(ref shellClosed) != 0) return;
                if (resultCapabilities.CanUseShareActions) decisionPoll.Start();
            }).ConfigureAwait(true);
        }

        void OnLoaded(object? _, System.Windows.RoutedEventArgs __) => Observe(HandleLoadedAsync());
        void OnClosed(object? _, EventArgs __)
        {
            if (Interlocked.Exchange(ref shellClosed, 1) != 0) return;
            shellLifetime.Cancel();
            handoff.Close();
            decisionPoll.Stop();
            decisionPoll.Tick -= OnDecisionPollTick;
            mainWindow.Loaded -= OnLoaded;
            mainWindow.Closed -= OnClosed;
            transferViewModel.Dispose();
            shareViewModel.Dispose();
            handoff.Dispose();
            shellLifetime.Dispose();
        }

        decisionPoll.Tick += OnDecisionPollTick;
        mainWindow.Loaded += OnLoaded;
        mainWindow.Closed += OnClosed;
        return mainWindow;
    }
}
