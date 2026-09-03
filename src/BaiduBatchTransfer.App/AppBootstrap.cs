using System.IO;
using BaiduBatchTransfer.App.Features.Imports;
using BaiduBatchTransfer.App.Features.Jobs;
using BaiduBatchTransfer.App.Features.Common;
using BaiduBatchTransfer.App.Features.ShareAnalytics;
using BaiduBatchTransfer.App.Features.Transfers;
using BaiduBatchTransfer.App.Features.Shares;
using BaiduBatchTransfer.App.Features.Acceptance;
using BaiduBatchTransfer.App.Features.Accounts;
using BaiduBatchTransfer.App.Features.BrowserAutomation;
using BaiduBatchTransfer.App.Features.ManagedBrowser;
using BaiduBatchTransfer.App.Features.Results;
using BaiduBatchTransfer.App.Features.Settings;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.App.Updates;
using BaiduBatchTransfer.Application.Accounts;
using BaiduBatchTransfer.Application.Imports;
using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.Application.ShareAnalytics;
using BaiduBatchTransfer.BaiduAdapter.Authentication;
using BaiduBatchTransfer.BaiduAdapter.Http;
using BaiduBatchTransfer.BaiduAdapter.WebAutomation;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
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
        => CreateHost(
            dataRoot,
            Array.Empty<string>(),
            new UnavailableApplicationUpdateRuntime(),
            configure);

    public static IHost CreateHost(
        string dataRoot,
        IApplicationUpdateRuntime updateRuntime,
        Action<IServiceCollection>? configure = null) =>
        CreateHost(dataRoot, Array.Empty<string>(), updateRuntime, configure);

    public static IHost CreateHost(
        string dataRoot,
        IReadOnlyList<string> startupArguments,
        Action<IServiceCollection>? configure = null) =>
        CreateHost(
            dataRoot,
            startupArguments,
            new UnavailableApplicationUpdateRuntime(),
            configure);

    public static IHost CreateHost(
        string dataRoot,
        IReadOnlyList<string> startupArguments,
        IApplicationUpdateRuntime updateRuntime,
        Action<IServiceCollection>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(startupArguments);
        ArgumentNullException.ThrowIfNull(updateRuntime);
        var paths = new AppDataPaths(dataRoot);
        return Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                ConfigureServices(services, paths, startupArguments, updateRuntime);
                configure?.Invoke(services);
            })
            .Build();
    }

    internal static void ConfigureServices(
        IServiceCollection services,
        AppDataPaths paths,
        IReadOnlyList<string>? startupArguments,
        IApplicationUpdateRuntime updateRuntime)
    {
        startupArguments ??= Array.Empty<string>();
        ArgumentNullException.ThrowIfNull(updateRuntime);
        var buildInfo = ApplicationBuildInfo.Current;
        services.AddSingleton(buildInfo);
        services.AddSingleton<IApplicationUpdateRuntime>(updateRuntime);
        services.AddSingleton<ApplicationUpdateService>();
        services.AddSingleton<IApplicationUpdateService>(sp =>
            sp.GetRequiredService<ApplicationUpdateService>());
        services.AddSingleton(paths);
        services.AddSingleton(_ => new SqliteConnectionFactory(paths.DatabasePath));
        services.AddSingleton<IUnitOfWorkFactory, SqliteUnitOfWorkFactory>();
        services.AddSingleton<SqliteAccountCapacityStore>();
        services.AddSingleton<IAccountCapacityStore>(sp =>
            sp.GetRequiredService<SqliteAccountCapacityStore>());
        services.AddSingleton(sp => new DatabaseBackupService(
            sp.GetRequiredService<SqliteConnectionFactory>(), paths.BackupDirectory));
        services.AddSingleton(sp => new DatabaseMigrator(
            sp.GetRequiredService<SqliteConnectionFactory>(),
            sp.GetRequiredService<DatabaseBackupService>(),
            buildInfo.ProductVersion));
        services.AddSingleton<DatabaseIntegrityService>();
        services.AddSingleton<IStartupDatabaseMaintenance, SqliteStartupDatabaseMaintenance>();
        services.AddSingleton(sp => new SqliteTransferSchedulingStore(
            sp.GetRequiredService<SqliteConnectionFactory>(),
            sp.GetRequiredService<TimeProvider>(),
            directBackgroundOnly: true));
        services.AddSingleton<SqliteShareSchedulingStore>();
        services.AddSingleton<SqliteManagedTransferAcceptanceStore>();
        services.AddSingleton<IManagedTransferAcceptanceStore>(sp =>
            sp.GetRequiredService<SqliteManagedTransferAcceptanceStore>());
        services.AddSingleton<IAllocationTransactionStore>(sp => sp.GetRequiredService<SqliteTransferSchedulingStore>());
        services.AddSingleton<IBatchSchedulingPolicySource>(sp =>
            sp.GetRequiredService<SqliteTransferSchedulingStore>());
        services.AddSingleton<ISubmissionBoundaryStore>(sp => sp.GetRequiredService<SqliteTransferSchedulingStore>());
        services.AddSingleton<IOperationExecutionStore>(sp => sp.GetRequiredService<SqliteTransferSchedulingStore>());
        services.AddSingleton<IShareSchedulingStore>(sp => sp.GetRequiredService<SqliteShareSchedulingStore>());
        services.AddSingleton<IShareExecutionStore>(sp => sp.GetRequiredService<SqliteShareSchedulingStore>());
        services.AddSingleton<IInterruptedOperationRecoveryStore>(sp => sp.GetRequiredService<SqliteTransferSchedulingStore>());
        services.AddSingleton<IInterruptedShareOperationRecoveryStore>(sp => sp.GetRequiredService<SqliteShareSchedulingStore>());
        services.AddSingleton<IAllocationCounterStore>(sp => sp.GetRequiredService<SqliteTransferSchedulingStore>());
        services.AddSingleton<IOperationReconciliationStore>(sp => sp.GetRequiredService<SqliteTransferSchedulingStore>());
        services.AddSingleton<IPendingTransferReconciliationSource>(sp =>
            sp.GetRequiredService<SqliteTransferSchedulingStore>());
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
        services.AddSingleton<IAccountShareAnalyticsClient, BaiduAccountShareAnalyticsClient>();
        services.AddSingleton<SqliteAccountShareAnalyticsRepository>();
        services.AddSingleton<IAccountShareAnalyticsRepository>(sp =>
            sp.GetRequiredService<SqliteAccountShareAnalyticsRepository>());
        services.AddSingleton<AccountShareAnalyticsProjector>();
        services.AddSingleton<AccountShareAnalyticsSyncService>();
        services.AddSingleton<SqliteShareAnalyticsPreferenceStore>();
        services.AddSingleton<IShareAnalyticsPreferenceStore>(sp =>
            sp.GetRequiredService<SqliteShareAnalyticsPreferenceStore>());
        services.AddSingleton<IShareAnalyticsWorkspaceService>(sp =>
            sp.GetRequiredService<AccountShareAnalyticsSyncService>());
        services.AddSingleton<IShareAnalyticsDelay, SystemShareAnalyticsDelay>();
        services.AddSingleton<IExternalProcessStarter, ExternalProcessStarter>();
        services.AddSingleton<IShareLinkLauncher, SystemShareLinkLauncher>();
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
        services.AddSingleton<SqliteStandaloneShareStore>();
        services.AddSingleton<IStandaloneShareStore>(sp =>
            sp.GetRequiredService<SqliteStandaloneShareStore>());
        services.AddSingleton<IStandaloneShareExecutionStore>(sp =>
            sp.GetRequiredService<SqliteStandaloneShareStore>());
        services.AddSingleton<IStandaloneShareResultProjectionRepository>(sp =>
            sp.GetRequiredService<SqliteStandaloneShareStore>());
        services.AddSingleton<SqliteWebAutomationAttemptStore>();
        services.AddSingleton<IWebAutomationAttemptStore>(sp =>
            sp.GetRequiredService<SqliteWebAutomationAttemptStore>());
        services.AddSingleton<SqliteRemoteSelectionSessionStore>();
        services.AddSingleton<IRemoteSelectionSessionStore>(sp =>
            sp.GetRequiredService<SqliteRemoteSelectionSessionStore>());
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
        services.AddSingleton<ExportStandaloneShareResultsService>();
        services.AddSingleton<IReplacementWorkbookWriter, OpenXmlReplacementResultWriter>();
        services.AddSingleton<IReplacementWorkbookFilePublisher, OpenXmlReplacementWorkbookFilePublisher>();
        services.AddSingleton<IShareAnalyticsWorkbookExportService, ShareAnalyticsWorkbookExportService>();
        services.AddSingleton<IDownloadedWorkbookValidator, OpenXmlDownloadedWorkbookValidator>();
        services.AddSingleton<SqliteLocalResultExportOwnerRepository>();
        services.AddSingleton<ILocalResultExportOwnerRepository>(sp =>
            sp.GetRequiredService<SqliteLocalResultExportOwnerRepository>());
        services.AddSingleton<ILocalResultExportArtifactService, LocalResultExportArtifactService>();
        services.AddSingleton<LocalResultExportCoordinator>();
        services.AddSingleton<ILocalResultExportCoordinator>(sp =>
            sp.GetRequiredService<LocalResultExportCoordinator>());
        services.AddSingleton<LocalResultExportHostedService>();
        services.AddSingleton<IHostedService>(sp =>
            sp.GetRequiredService<LocalResultExportHostedService>());

        services.AddSingleton<BaiduSessionIdentityClient>();
        services.AddSingleton<BaiduWebApiClient>();
        services.AddSingleton<LiveBaiduNetdiskAdapter>();
        services.AddSingleton<LiveBaiduShareAdapter>();
        services.AddSingleton<BaiduAdapterConcurrencyGate>();
        services.AddSingleton<SqliteAdapterCapabilityStore>();
        services.AddSingleton<IAdapterCapabilityStore>(sp =>
            sp.GetRequiredService<SqliteAdapterCapabilityStore>());
        var acceptanceWriteLeaseOptions = AcceptanceWriteLeaseOptions.FromStartup(startupArguments);
        services.AddSingleton(acceptanceWriteLeaseOptions);
        services.AddSingleton(
            typeof(RemoteWriteExecutionMode),
            acceptanceWriteLeaseOptions.IsEnabled
                ? RemoteWriteExecutionMode.AcceptanceOnly
                : RemoteWriteExecutionMode.DirectBackground);
        services.AddSingleton<IAcceptanceWriteLeasePresenter, AcceptanceWriteLeasePresenter>();
        services.AddSingleton<AcceptanceWriteLeaseCoordinator>();
        services.AddSingleton<IAdapterWriteAuthorizationGate>(sp =>
            sp.GetRequiredService<AcceptanceWriteLeaseCoordinator>());
        services.AddSingleton<IBaiduCandidateActionAuthorizationSource>(sp =>
            sp.GetRequiredService<AcceptanceWriteLeaseCoordinator>());
        services.AddSingleton<IAcceptanceOperationEvidenceSink>(sp =>
            sp.GetRequiredService<AcceptanceWriteLeaseCoordinator>());
        services.AddSingleton<ILocalResultExportObserver>(sp =>
            sp.GetRequiredService<AcceptanceWriteLeaseCoordinator>());
        services.AddTransient<Func<AdapterWriteAuthorizationRequest, AcceptanceConfirmationWindow>>(sp =>
            request => new AcceptanceConfirmationWindow(
                sp.GetRequiredService<AcceptanceWriteLeaseCoordinator>(),
                request,
                sp.GetRequiredService<RemoteWriteExecutionMode>()));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<TransferAutomationFlow>();
        services.AddSingleton<ITransferRecoveryAuthorizationSource, TransferRecoveryAuthorizationSource>();
        services.AddSingleton<WebViewBaiduNetdiskAdapter>(sp => new WebViewBaiduNetdiskAdapter(
            sp.GetRequiredService<LiveBaiduNetdiskAdapter>(),
            sp.GetRequiredService<IVisibleWebAutomationBroker>(),
            sp.GetRequiredService<IAccountProfilePathPolicy>(),
            sp.GetRequiredService<TransferAutomationFlow>(),
            buildInfo.AdapterBuildVersion,
            sp.GetRequiredService<IWebAutomationAttemptStore>(),
            sp.GetRequiredService<IAcceptanceOperationEvidenceSink>(),
            sp.GetRequiredService<ITransferRecoveryAuthorizationSource>()));
        services.AddSingleton<IBaiduNetdiskAdapter>(sp =>
            sp.GetRequiredService<RemoteWriteExecutionMode>() == RemoteWriteExecutionMode.AcceptanceOnly
                ? CoordinatedBaiduNetdiskAdapter.Wrap(
                    sp.GetRequiredService<WebViewBaiduNetdiskAdapter>(),
                    sp.GetRequiredService<BaiduAdapterConcurrencyGate>(),
                    sp.GetRequiredService<IAdapterWriteAuthorizationGate>(),
                    new AdapterCapabilityVersion(
                        "BaiduTransfer",
                        BaiduSelectorCatalog.TransferProtocolVersion,
                        BaiduSelectorCatalog.TransferSelectorVersion,
                        buildInfo.AdapterBuildVersion))
                : sp.GetRequiredService<LiveBaiduNetdiskAdapter>());
        services.AddSingleton(LiveBaiduNetdiskAdapter.Capability);
        services.AddSingleton<WebViewCookieSessionBridge>();
        services.AddSingleton<ISessionProtector, DpapiSessionProtector>();
        services.AddSingleton<EncryptedBaiduSessionStore>();
        services.AddSingleton<IBaiduSessionStore>(sp => sp.GetRequiredService<EncryptedBaiduSessionStore>());
        services.AddSingleton<AccountCapacityRefreshService>();
        services.AddSingleton(_ => new FileSystemManagedBrowserProfilePolicy(paths.RootDirectory));
        services.AddSingleton<IManagedBrowserProfilePolicy>(sp =>
            sp.GetRequiredService<FileSystemManagedBrowserProfilePolicy>());
        services.AddSingleton<SqliteManagedBrowserStateStore>();
        services.AddSingleton<IManagedBrowserStateStore>(sp =>
            sp.GetRequiredService<SqliteManagedBrowserStateStore>());
        services.AddSingleton<IBaiduExecutionBindingStore>(sp =>
            sp.GetRequiredService<SqliteManagedBrowserStateStore>());
        services.AddSingleton<IBaiduExecutionContextResolver, BaiduExecutionContextResolver>();
        services.AddSingleton(sp => new FileSystemManagedBrowserProfileStore(
            paths.RootDirectory,
            sp.GetRequiredService<IManagedBrowserProfilePolicy>()));
        services.AddSingleton<IManagedBrowserProfileStore>(sp =>
            sp.GetRequiredService<FileSystemManagedBrowserProfileStore>());
        services.AddSingleton<ManagedChromiumProcessManager>();
        services.AddSingleton(sp => new ManagedBrowserLifecycleCoordinator(
            sp.GetRequiredService<IManagedBrowserStateStore>(),
            sp.GetRequiredService<ManagedChromiumProcessManager>(), paths.RootDirectory,
            sp.GetRequiredService<IManagedBrowserProfilePolicy>()));
        services.AddSingleton<IManagedBrowserLifecycle>(sp =>
            sp.GetRequiredService<ManagedBrowserLifecycleCoordinator>());
        services.AddSingleton<IManagedBrowserAccountLifecycle>(sp =>
            sp.GetRequiredService<ManagedBrowserLifecycleCoordinator>());
        services.AddSingleton(_ => new ManagedBrowserHelperClientFactory(
            Path.Combine(AppContext.BaseDirectory, "managed-browser-helper")));
        services.AddSingleton<ManagedTransferQuarantineRegistry>();
        services.AddSingleton<ManagedTransferShareHandoffRegistry>();
        services.AddSingleton(sp => new ManagedChromiumTransferRuntimeFactory(
            sp.GetRequiredService<ManagedBrowserLifecycleCoordinator>(),
            sp.GetRequiredService<ManagedChromiumProcessManager>(),
            sp.GetRequiredService<ManagedBrowserHelperClientFactory>(),
            sp.GetRequiredService<IAdapterWriteAuthorizationGate>(),
            sp.GetRequiredService<IBaiduCandidateActionAuthorizationSource>(),
            sp.GetRequiredService<IWebAutomationAttemptStore>(),
            sp.GetRequiredService<ITransferRecoveryAuthorizationSource>(),
            sp.GetRequiredService<ManagedTransferShareHandoffRegistry>(),
            buildInfo.AdapterBuildVersion));
        services.AddSingleton<ManagedChromiumTransferAdapter>(sp => new(
            sp.GetRequiredService<ManagedChromiumTransferRuntimeFactory>().BeginOperationAsync,
            sp.GetRequiredService<ManagedTransferQuarantineRegistry>()));
        services.AddSingleton<IManagedChromiumTransferAdapter>(sp =>
            sp.GetRequiredService<ManagedChromiumTransferAdapter>());
        services.AddSingleton(sp => new ExecutionBoundTransferAdapter(
            sp.GetRequiredService<WebViewBaiduNetdiskAdapter>(),
            sp.GetRequiredService<IManagedChromiumTransferAdapter>()));
        services.AddSingleton<IManagedBrowserFamilySwitchFence, ManagedBrowserFamilySwitchReadinessFence>();
        services.AddSingleton<ManagedBrowserFamilySwitchCoordinator>();
        services.AddSingleton<BrowserSelectionProcessContext>();
        services.AddSingleton<ProcessBrowserSelectionActivityRegistry>();
        services.AddSingleton<IBrowserSelectionActivityRegistry>(sp =>
            sp.GetRequiredService<ProcessBrowserSelectionActivityRegistry>());
        services.AddSingleton<IBrowserDirectoryState, SqliteBrowserDirectoryState>();
        services.AddSingleton<IBrowserDirectoryFileSystem, BrowserDirectoryFileSystem>();
        services.AddSingleton<IBrowserExecutableVerifier, BrowserExecutableVerifier>();
        services.AddSingleton<BrowserSetupService>();
        services.AddSingleton<IManagedBrowserLoginStore>(sp => sp.GetRequiredService<SqliteManagedBrowserStateStore>());
        services.AddSingleton<ManagedBrowserAccountService>();
        services.AddSingleton(sp => new RetiredBrowserFamilySlotCoordinator(
            sp.GetRequiredService<IManagedBrowserStateStore>(),
            sp.GetRequiredService<ManagedChromiumProcessManager>(),
            sp.GetRequiredService<IManagedBrowserProfilePolicy>(),
            sp.GetRequiredService<IManagedBrowserProfileStore>(),
            sp.GetRequiredService<RemoveAccountService>(), paths.RootDirectory));
        services.AddSingleton(sp => new ManagedBrowserLoginLauncher(
            sp.GetRequiredService<BrowserSetupService>(),
            sp.GetRequiredService<IManagedBrowserProfilePolicy>(),
            sp.GetRequiredService<ManagedChromiumProcessManager>(),
            sp.GetRequiredService<ManagedBrowserLifecycleCoordinator>(),
            sp.GetRequiredService<IManagedBrowserStateStore>(),
            sp.GetRequiredService<IUnitOfWorkFactory>(),
            sp.GetRequiredService<ManagedBrowserAccountService>(),
            sp.GetRequiredService<ManagedBrowserFamilySwitchCoordinator>(),
            sp.GetRequiredService<RetiredBrowserFamilySlotCoordinator>(),
            Path.Combine(AppContext.BaseDirectory, "managed-browser-helper")));
        services.AddSingleton<IManagedBrowserLoginOperations>(sp => sp.GetRequiredService<ManagedBrowserLoginLauncher>());
        services.AddSingleton(_ => new FileSystemAccountProfilePathPolicy(paths.ProfileDirectory));
        services.AddSingleton<IAccountProfilePathPolicy>(sp =>
            sp.GetRequiredService<FileSystemAccountProfilePathPolicy>());
        services.AddSingleton<WebView2ProfileCloser>();
        services.AddSingleton<IWebViewProfileCloser>(sp => sp.GetRequiredService<WebView2ProfileCloser>());
        services.AddSingleton<IWebAutomationNavigationAuthorizer>(sp =>
            new ProductionWebAutomationNavigationAuthorizer(
                sp.GetRequiredService<AcceptanceWriteLeaseCoordinator>()));
        services.AddSingleton<SqliteWebAutomationBatchCancellationStore>();
        services.AddSingleton<IWebAutomationBatchCancellationStore>(sp =>
            sp.GetRequiredService<SqliteWebAutomationBatchCancellationStore>());
        services.AddSingleton<IWebAutomationBatchCancellation>(sp =>
            new WebAutomationBatchCancellation(
                sp.GetRequiredService<IWebAutomationBatchCancellationStore>(),
                sp.GetRequiredService<IWebAutomationAttemptStore>(),
                TimeProvider.System));
        services.AddSingleton<IWebAutomationUiDispatcher, WpfWebAutomationUiDispatcher>();
        services.AddSingleton<IVisibleWebAutomationSessionFactory, WebView2AutomationSessionFactory>();
        services.AddSingleton(sp => new UserHandoffCoordinator(
            sp.GetRequiredService<IWebAutomationAttemptStore>(),
            sp.GetRequiredService<IWebAutomationBatchCancellation>(),
            TimeProvider.System));
        services.AddSingleton<VisibleWebAutomationQueue>();
        services.AddSingleton<IVisibleWebAutomationBroker>(sp =>
            sp.GetRequiredService<VisibleWebAutomationQueue>());
        services.AddSingleton<ShareAutomationFlow>();
        services.AddSingleton<SqliteShareAutomationAcceptanceEvidenceStore>();
        services.AddSingleton<IShareAutomationAcceptanceEvidenceStore>(sp =>
            sp.GetRequiredService<SqliteShareAutomationAcceptanceEvidenceStore>());
        services.AddSingleton<IShareOfficialResultDestinationResolver, ShareOfficialResultDestinationResolver>();
        services.AddSingleton<WebViewBaiduShareAdapter>(sp => new WebViewBaiduShareAdapter(
            sp.GetRequiredService<LiveBaiduShareAdapter>(),
            sp.GetRequiredService<IVisibleWebAutomationBroker>(),
            sp.GetRequiredService<IAccountProfilePathPolicy>(),
            sp.GetRequiredService<ShareAutomationFlow>(),
            buildInfo.AdapterBuildVersion,
            sp.GetRequiredService<IWebAutomationAttemptStore>(),
            sp.GetRequiredService<IShareAutomationAcceptanceEvidenceStore>(),
            sp.GetRequiredService<IShareOfficialResultDestinationResolver>(),
            sp.GetRequiredService<IAcceptanceOperationEvidenceSink>()));
        services.AddSingleton<IBaiduShareAdapter>(sp =>
            sp.GetRequiredService<RemoteWriteExecutionMode>() == RemoteWriteExecutionMode.AcceptanceOnly
                ? CoordinatedBaiduShareAdapter.Wrap(
                    sp.GetRequiredService<WebViewBaiduShareAdapter>(),
                    sp.GetRequiredService<BaiduAdapterConcurrencyGate>(),
                    sp.GetRequiredService<IAdapterWriteAuthorizationGate>(),
                    new AdapterCapabilityVersion(
                        "BaiduShare",
                        BaiduSelectorCatalog.ProtocolVersion,
                        BaiduSelectorCatalog.SelectorVersion,
                        buildInfo.AdapterBuildVersion))
                : sp.GetRequiredService<LiveBaiduShareAdapter>());
        services.AddSingleton<ShareSubmissionEngine>();
        services.AddSingleton<ManagedShareAutomationSessionFactory>(sp => new(
            sp.GetRequiredService<ManagedTransferShareHandoffRegistry>(),
            sp.GetRequiredService<IManagedBrowserLifecycle>(),
            sp.GetRequiredService<ManagedChromiumProcessManager>(),
            sp.GetRequiredService<ManagedBrowserHelperClientFactory>(),
            sp.GetRequiredService<IWebAutomationAttemptStore>(),
            sp.GetRequiredService<IShareAutomationAcceptanceEvidenceStore>()));
        services.AddSingleton<IManagedShareAutomationSessionFactory>(sp =>
            sp.GetRequiredService<ManagedShareAutomationSessionFactory>());
        services.AddSingleton<ManagedChromiumShareAdapter>(sp => new(
            sp.GetRequiredService<IManagedShareAutomationSessionFactory>(),
            sp.GetRequiredService<IWebAutomationAttemptStore>(),
            sp.GetRequiredService<IShareAutomationAcceptanceEvidenceStore>(),
            sp.GetRequiredService<ShareAutomationFlow>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IManagedChromiumShareAdapter>(sp =>
            sp.GetRequiredService<ManagedChromiumShareAdapter>());
        services.AddSingleton(sp => new ExecutionBoundShareAdapter(
            sp.GetRequiredService<WebViewBaiduShareAdapter>(),
            sp.GetRequiredService<IManagedChromiumShareAdapter>(),
            sp.GetRequiredService<ShareSubmissionEngine>()));
        services.AddSingleton<StandaloneShareOperationExecutor>();
        services.AddSingleton<IStandaloneShareOperationRunner>(sp =>
            sp.GetRequiredService<StandaloneShareOperationExecutor>());
        services.AddSingleton<StandaloneShareCoordinator>();
        services.AddSingleton<IStandaloneShareBatchNotifications>(sp =>
            sp.GetRequiredService<StandaloneShareCoordinator>());
        services.AddSingleton<CreateStandaloneShareBatchService>();
        services.AddSingleton<IHostedService, StandaloneShareCoordinatorHostedService>();
        services.AddSingleton<RemoteResourcePickerFlow>();
        services.AddSingleton<IRemoteResourcePicker>(sp => new WebViewRemoteResourcePicker(
            sp.GetRequiredService<IVisibleWebAutomationBroker>(),
            sp.GetRequiredService<IAccountProfilePathPolicy>(),
            sp.GetRequiredService<IRemoteSelectionSessionStore>(),
            sp.GetRequiredService<RemoteResourcePickerFlow>(),
            TimeProvider.System,
            buildInfo.AdapterBuildVersion));
        services.AddSingleton<FileSystemWebViewProfileStore>();
        services.AddSingleton<IWebViewProfileStore>(sp => sp.GetRequiredService<FileSystemWebViewProfileStore>());
        services.AddSingleton<LoginProfileBootstrapper>();
        services.AddSingleton<AddAccountService>();
        services.AddSingleton<RefreshAccountSessionService>();
        services.AddSingleton<BaiduLoginSessionRegistrar>();
        services.AddSingleton<IBaiduLoginWindowPresenter, BaiduLoginWindowPresenter>();
        services.AddSingleton<IManagedBrowserLoginWindowPresenter>(sp => new ManagedBrowserLoginWindowPresenter(
            sp.GetRequiredService<IManagedBrowserLoginOperations>(),
            sp.GetRequiredService<IUiDispatcher>(),
            vm => new ManagedBrowserLoginWindow(vm)
            {
                Owner = System.Windows.Application.Current?.Windows.OfType<System.Windows.Window>().FirstOrDefault(window => window.IsActive)
                    ?? System.Windows.Application.Current?.MainWindow,
            },
            async (accountId, token) => (await sp.GetRequiredService<ManagedBrowserLifecycleCoordinator>()
                .LoadPersistedAsync(accountId, token).ConfigureAwait(false))?.BrowserFamily));
        var resumedManagedAccountId = ManagedBrowserAccountLoginLauncher.ResumeProfileFromStartup(
            startupArguments, acceptanceWriteLeaseOptions.IsEnabled);
        services.AddSingleton(sp => new ManagedBrowserAccountLoginLauncher(
            sp.GetRequiredService<IManagedBrowserLoginWindowPresenter>(), resumedManagedAccountId));
        services.AddSingleton<DirectBackgroundAccountLoginLauncher>();
        services.AddSingleton<IAccountLoginLauncher>(sp =>
            sp.GetRequiredService<RemoteWriteExecutionMode>() == RemoteWriteExecutionMode.AcceptanceOnly
                ? sp.GetRequiredService<ManagedBrowserAccountLoginLauncher>()
                : sp.GetRequiredService<DirectBackgroundAccountLoginLauncher>());
        services.AddSingleton<IAccountReloginLauncher>(sp =>
            sp.GetRequiredService<DirectBackgroundAccountLoginLauncher>());

        services.AddSingleton<SqliteAccountRemovalStore>();
        services.AddSingleton<IAccountRemovalStore>(sp =>
            sp.GetRequiredService<SqliteAccountRemovalStore>());
        services.AddSingleton(sp => new RemoveAccountService(
            sp.GetRequiredService<IUnitOfWorkFactory>(),
            sp.GetRequiredService<IWebViewProfileStore>(),
            sp.GetRequiredService<IAccountProfilePathPolicy>(),
            sp.GetRequiredService<IManagedBrowserStateStore>(),
            sp.GetRequiredService<IManagedBrowserProfileStore>()));
        // 管理窗口只走已在 SQLite 事务中完成凭据清除后的收尾路径。
        services.AddSingleton(sp => new AccountRemovalCoordinator(
            sp.GetRequiredService<IAccountRemovalStore>(),
            sp.GetRequiredService<RemoveAccountService>(),
            sp.GetRequiredService<IManagedBrowserLifecycle>(),
            sp.GetRequiredService<IManagedBrowserProfileStore>()));
        services.AddSingleton<IAccountRemovalOperations, AccountRemovalOperations>();
        services.AddSingleton<IAccountManagementOwnedPrompt, WpfAccountManagementOwnedPrompt>();
        services.AddTransient<Func<IAccountManagementConfirmation, AccountManagementWindow>>(sp =>
            confirmation => new AccountManagementWindow(new AccountManagementViewModel(
                sp.GetRequiredService<IAccountRemovalOperations>(),
                confirmation,
                reloginLauncher: sp.GetRequiredService<IAccountReloginLauncher>())));
        services.AddSingleton<IAccountManagementWindowPresenter, AccountManagementWindowPresenter>();
        services.AddSingleton<IAccountManagementLauncher, AccountManagementLauncher>();

        // 普通版本继续禁止开始任务；Debug 显式现场验收仍由逐操作写入租约二次门禁。
        services.AddSingleton<IAdapterAvailability>(sp =>
        {
            var options = sp.GetRequiredService<AcceptanceWriteLeaseOptions>();
            return options.IsEnabled
                ? new TemporaryAcceptanceAdapterAvailability(options)
                : new DirectBackgroundAdapterAvailability();
        });
        services.AddSingleton<IJobResultCapabilityAvailability>(sp =>
            sp.GetRequiredService<RemoteWriteExecutionMode>() == RemoteWriteExecutionMode.AcceptanceOnly
                ? new UnavailableJobResultCapabilityAvailability()
                : new DirectBackgroundJobResultCapabilityAvailability());
        services.AddSingleton<IJobResultActionCapability>(sp =>
            sp.GetRequiredService<RemoteWriteExecutionMode>() == RemoteWriteExecutionMode.AcceptanceOnly
                ? new UnavailableJobResultActionCapability()
                : new DirectBackgroundJobResultActionCapability());
        services.AddSingleton<IAsyncDelay>(_ => new TimeProviderDelay(TimeProvider.System));
        services.AddSingleton(_ => RetryPolicy.Default);
        services.AddSingleton<AllocationTransactionService>();
        services.AddSingleton<AccountDailyLimitService>();
        services.AddSingleton<OperationExecutor>(sp =>
            sp.GetRequiredService<RemoteWriteExecutionMode>() ==
                RemoteWriteExecutionMode.AcceptanceOnly
                ? new OperationExecutor(
                    sp.GetRequiredService<ExecutionBoundTransferAdapter>(),
                    sp.GetRequiredService<IBaiduExecutionContextResolver>(),
                    sp.GetRequiredService<IOperationExecutionStore>(),
                    sp.GetRequiredService<AccountDailyLimitService>(),
                    sp.GetRequiredService<RetryPolicy>(),
                    sp.GetRequiredService<IAsyncDelay>(),
                    sp.GetRequiredService<TimeProvider>())
                : new OperationExecutor(
                    sp.GetRequiredService<IBaiduNetdiskAdapter>(),
                    sp.GetRequiredService<IBaiduSessionStore>(),
                    sp.GetRequiredService<IOperationExecutionStore>(),
                    sp.GetRequiredService<AccountDailyLimitService>(),
                    sp.GetRequiredService<RetryPolicy>(),
                    sp.GetRequiredService<IAsyncDelay>(),
                    sp.GetRequiredService<TimeProvider>(),
                    allowLegacySessionForAnyProfile: true));
        services.AddSingleton<ITransferOperationExecutor>(sp => sp.GetRequiredService<OperationExecutor>());
        services.AddSingleton<ShareOperationExecutor>(sp =>
            sp.GetRequiredService<RemoteWriteExecutionMode>() ==
                RemoteWriteExecutionMode.AcceptanceOnly
                ? new ShareOperationExecutor(
                    sp.GetRequiredService<ExecutionBoundShareAdapter>(),
                    sp.GetRequiredService<IBaiduExecutionContextResolver>(),
                    sp.GetRequiredService<IShareExecutionStore>(),
                    sp.GetRequiredService<RetryPolicy>(),
                    sp.GetRequiredService<IAsyncDelay>(),
                    sp.GetRequiredService<TimeProvider>(),
                    sp.GetRequiredService<ShareSubmissionEngine>())
                : new ShareOperationExecutor(
                    sp.GetRequiredService<IBaiduShareAdapter>(),
                    sp.GetRequiredService<IBaiduSessionStore>(),
                    sp.GetRequiredService<IShareExecutionStore>(),
                    sp.GetRequiredService<RetryPolicy>(),
                    sp.GetRequiredService<IAsyncDelay>(),
                    sp.GetRequiredService<TimeProvider>(),
                    sp.GetRequiredService<ShareSubmissionEngine>(),
                    allowLegacySessionForAnyProfile: true));
        services.AddSingleton<IShareOperationExecutor>(sp =>
            sp.GetRequiredService<ShareOperationExecutor>());
        services.AddSingleton<ManagedTransferAcceptanceRunner>();
        services.AddSingleton<ShareCoordinatorWorkSource>();
        services.AddSingleton<IShareCoordinatorWorkSource>(sp => sp.GetRequiredService<ShareCoordinatorWorkSource>());
        services.AddSingleton(_ => new NetworkExecutionGate(initiallyOnline: true));
        services.AddSingleton<TransferCoordinator>();
        services.AddSingleton<IHostedService, TransferCoordinatorHostedService>();
        services.AddSingleton<OperationReconciliationService>(sp =>
            sp.GetRequiredService<RemoteWriteExecutionMode>() ==
                RemoteWriteExecutionMode.AcceptanceOnly
                ? new OperationReconciliationService(
                    sp.GetRequiredService<ExecutionBoundTransferAdapter>(),
                    sp.GetRequiredService<IBaiduExecutionContextResolver>(),
                    sp.GetRequiredService<IOperationReconciliationStore>(),
                    sp.GetRequiredService<TimeProvider>())
                : new OperationReconciliationService(
                    sp.GetRequiredService<IBaiduNetdiskAdapter>(),
                    sp.GetRequiredService<IBaiduSessionStore>(),
                    sp.GetRequiredService<IOperationReconciliationStore>(),
                    sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<ITransferResultReconciler>(sp => sp.GetRequiredService<OperationReconciliationService>());
        services.AddSingleton<ShareOperationReconciliationService>(sp =>
            sp.GetRequiredService<RemoteWriteExecutionMode>() ==
                RemoteWriteExecutionMode.AcceptanceOnly
                ? new ShareOperationReconciliationService(
                    sp.GetRequiredService<ExecutionBoundShareAdapter>(),
                    sp.GetRequiredService<IBaiduExecutionContextResolver>(),
                    sp.GetRequiredService<IShareReconciliationStore>(),
                    sp.GetRequiredService<IAsyncDelay>(),
                    timeProvider: sp.GetRequiredService<TimeProvider>(),
                    submissionEngine: sp.GetRequiredService<ShareSubmissionEngine>())
                : new ShareOperationReconciliationService(
                    sp.GetRequiredService<IBaiduShareAdapter>(),
                    sp.GetRequiredService<IBaiduSessionStore>(),
                    sp.GetRequiredService<IShareReconciliationStore>(),
                    sp.GetRequiredService<IAsyncDelay>(),
                    timeProvider: sp.GetRequiredService<TimeProvider>(),
                    submissionEngine: sp.GetRequiredService<ShareSubmissionEngine>()));
        services.AddSingleton<IShareResultReconciler>(sp => sp.GetRequiredService<ShareOperationReconciliationService>());
        services.AddSingleton<IJobResultActions, JobResultActions>();
        services.AddSingleton<IJobResultsWorkflow, LocalJobResultsWorkflow>();
        services.AddSingleton<ExistingShareDecisionService>();
        services.AddSingleton<IExistingShareDecisionPresenter, ExistingShareDecisionPresenter>();
        services.AddSingleton<IExistingShareDecisionApplication, ExistingShareDecisionApplication>();
        services.AddSingleton<IExistingShareDecisionInteraction, ExistingShareDecisionInteraction>();
        services.AddSingleton<AppRuntimeState>();
        services.AddSingleton(sp => new StartupRecoveryRunner(
            sp.GetRequiredService<StartupRecoveryService>(),
            sp.GetRequiredService<IRemoteSelectionSessionStore>(),
            sp.GetRequiredService<ILocalResultExportCoordinator>(),
            TimeProvider.System));
        services.AddSingleton<IAppRecoveryRunner>(sp =>
            sp.GetRequiredService<StartupRecoveryRunner>());
        services.AddSingleton<AppStartupController>();
        services.AddSingleton<IUiDispatcher, WpfUiDispatcher>();
        services.AddSingleton<ITextClipboard, WpfTextClipboard>();
        services.AddSingleton<IExcelFileDialog, WpfExcelFileDialog>();
        services.AddSingleton<IFolderDialog, WpfFolderDialog>();
        services.AddSingleton<IResultSaveDialog, ResultSaveDialog>();
        services.AddSingleton<DialogService>();
        services.AddSingleton<IDialogService>(sp => sp.GetRequiredService<DialogService>());
        services.AddSingleton<IBrowserDirectoryPicker>(sp => sp.GetRequiredService<DialogService>());
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

public sealed class WizardAccountProvider(
    IUnitOfWorkFactory unitOfWorkFactory,
    IManagedBrowserLifecycle managedBrowserLifecycle) : IWizardAccountProvider
{
    private const int MaximumSnapshotSize = 1_000;

    public WizardAccountProvider(IUnitOfWorkFactory unitOfWorkFactory)
        : this(unitOfWorkFactory, new NullManagedBrowserLifecycle())
    {
    }

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
                ManagedBrowserLifecycleSession? active = null;
                try
                {
                    active = await managedBrowserLifecycle.LoadActiveSessionAsync(
                        account.Id, cancellationToken).ConfigureAwait(false);
                }
                catch when (!string.Equals(account.LoginMethod, "ManagedChromium", StringComparison.Ordinal))
                {
                    // A legacy account can still use its original execution profile when
                    // no managed-only state is required for that account.
                }

                ExecutionProfileSnapshot executionProfile;
                if (active is not null)
                {
                    executionProfile = ExecutionProfileSnapshot.Managed(
                        active.BrowserFamily,
                        active.ProfileId,
                        active.InstallationId,
                        active.SessionId);
                }
                else if (string.Equals(account.LoginMethod, "ManagedChromium", StringComparison.Ordinal))
                {
                    continue;
                }
                else
                {
                    executionProfile = ExecutionProfileSnapshot.LegacyForAccount(account.Id);
                }
                result.Add(new WizardAccountOption(
                    account.Id,
                    account.DisplayName,
                    account.Status,
                    account.IsEnabled,
                    account.IsDeleted,
                    account.CapacityTotal,
                    account.CapacityUsed,
                    0, 0, 0, 0, 0, 0, 0, 0, 0,
                    BatchAccountSelectionDecision.NotSelected,
                    ExecutionProfile: executionProfile));
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
    AccountCapacityRefreshService accountCapacityRefreshService,
    IJobResultsWorkflow jobResultsWorkflow,
    IResultSaveDialog resultSaveDialog,
    ITextClipboard textClipboard,
    IJobResultCapabilityAvailability resultCapabilities,
    IExistingShareDecisionInteraction existingShareInteraction,
    PauseBatchService pauseBatchService,
    ResumeBatchService resumeBatchService,
    RetryFailedTransferService retryFailedTransferService,
    IWorkspaceBatchHistorySource batchHistorySource,
    ITransferResultDestinationSink resultDestinationSink,
    IRemoteResourcePicker remoteResourcePicker,
    IStandaloneShareStore standaloneShareStore,
    CreateStandaloneShareBatchService standaloneShareBatchService,
    ILocalResultExportCoordinator localResultExports,
    IStandaloneShareResultProjectionRepository standaloneShareResults,
    ILocalResultExportStore localResultExportStore,
    IShareAnalyticsWorkspaceService shareAnalyticsService,
    IShareAnalyticsPreferenceStore shareAnalyticsPreferenceStore,
    AccountShareAnalyticsProjector shareAnalyticsProjector,
    IShareLinkLauncher shareLinkLauncher,
    TimeProvider timeProvider,
    IShareAnalyticsDelay shareAnalyticsDelay,
    IShareAnalyticsWorkbookExportService shareAnalyticsExportService,
    ManagedTransferAcceptanceRunner acceptanceRunner,
    RemoteWriteExecutionMode remoteWriteMode,
    IApplicationUpdateService updateService,
    ApplicationBuildInfo buildInfo,
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
        Guid? preferredShareAnalyticsAccountId = null;
        var shareAnalyticsPreferenceLoadFailed = false;
        try
        {
            preferredShareAnalyticsAccountId = await shareAnalyticsPreferenceStore
                .LoadLastViewedAccountAsync(cancellationToken)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            shareAnalyticsPreferenceLoadFailed = true;
        }
        var importViewModel = await importWorkspaceFactory.CreateAsync(cancellationToken).ConfigureAwait(true);
        var wizardViewModel = new CreateJobWizardViewModel(
            accounts, pageSource, batchService, dialogService, adapterAvailability, draftWorkflow, precheckWorkflow,
            resultCapabilities);
        var resultsViewModel = new JobResultsViewModel(
            jobResultsWorkflow, resultSaveDialog, resultCapabilities, existingShareInteraction, textClipboard);
        var transferOptions = new TransferWorkspaceOptions(
            DefaultResultDirectory: Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "百度批量转存结果"),
            DefaultTargetDirectory: "/",
            SimplifiedDirectMode: true,
            DefaultMaxConcurrency: 2).Validate();
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
            mainViewModel?.ShowBatchTransferCommand.Execute(null);
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
            adapterAvailability,
            transferOptions,
            remoteWriteMode,
            acceptanceRunner.ExecuteAsync,
            new LocalResultDirectoryPreparer());
        var transferWorkspace = new TransferWorkspaceView(transferViewModel);
        var homeWorkspace = new DeferredWorkspaceView(
            new DeferredWorkspaceViewModel("首页", "功能建设中"));
        var shareAnalyticsViewModel = new ShareAnalyticsViewModel(
            shareAnalyticsService,
            shareAnalyticsProjector,
            shareLinkLauncher,
            timeProvider,
            shareAnalyticsDelay,
            shareAnalyticsExportService,
            resultSaveDialog);
        var shareDataWorkspace = new ShareAnalyticsView(shareAnalyticsViewModel);
        var adInsertionWorkspace = new DeferredWorkspaceView(
            new DeferredWorkspaceViewModel("广告插入", "功能建设中"));
        var fileExclusionWorkspace = new DeferredWorkspaceView(
            new DeferredWorkspaceViewModel("文件排除", "功能建设中"));
        var statisticsWorkspace = new DeferredWorkspaceView(
            new DeferredWorkspaceViewModel("数据统计", "功能建设中"));
        MainWindow? mainWindow = null;
        var uiDispatcher = System.Windows.Application.Current?.Dispatcher ??
            System.Windows.Threading.Dispatcher.CurrentDispatcher;
        var uiContext = new System.Windows.Threading.DispatcherSynchronizationContext(
            uiDispatcher);
        var closeRequester = new WpfApplicationCloseRequester(
            uiDispatcher,
            () => mainWindow?.Close());
        var settingsViewModel = new SettingsViewModel(
            updateService,
            dialogService,
            closeRequester,
            uiContext,
            buildInfo);
        var settingsWorkspace = new SettingsView(settingsViewModel);
        var shareViewModel = new ShareWorkspaceViewModel(
            accounts.Select(account => new ShareAccountOption(account.AccountId, account.DisplayName)).ToArray(),
            remoteResourcePicker,
            standaloneShareStore,
            resultSaveDialog,
            batchService: standaloneShareBatchService,
            localResultExports: localResultExports,
            resultProjectionRepository: standaloneShareResults,
            localResultExportStore: localResultExportStore);
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
            homeWorkspace,
            shareDataWorkspace,
            transferWorkspace,
            adInsertionWorkspace,
            fileExclusionWorkspace,
            statisticsWorkspace,
            settingsWorkspace,
            loginLauncher,
            accountManagementLauncher,
            accountRefresh.RefreshAsync,
            initialActiveAccountCount,
            accounts: wizardViewModel.Accounts,
            refreshAccountCapacity: accountCapacityRefreshService.RefreshAsync,
            selectShareAnalyticsAccount: shareAnalyticsViewModel.SelectAccount,
            initialShareAnalyticsAccountId: preferredShareAnalyticsAccountId,
            saveShareAnalyticsAccount: shareAnalyticsPreferenceStore.SaveLastViewedAccountAsync,
            shareAnalyticsPreferenceLoadFailed: shareAnalyticsPreferenceLoadFailed,
            enterShareData: shareAnalyticsViewModel.EnterAsync,
            leaveShareData: shareAnalyticsViewModel.Leave,
            isOtaAcceptanceBuild: buildInfo.IsOtaAcceptanceBuild);
        mainWindow = new MainWindow(mainViewModel);

        static async Task ObserveAsync(Task task)
        {
            try { await task.ConfigureAwait(true); }
            catch { }
        }

        void Observe(Task task) => _ = ObserveAsync(task);

        var startupUpdateCheck = new StartupUpdateCheckCoordinator(
            updateService,
            version => DispatchOnUiAsync(() =>
            {
                if (shellLifetime.IsCancellationRequested || Volatile.Read(ref shellClosed) != 0)
                    return;
                mainViewModel.ReportUpdateAvailable(version);
            }),
            shellLifetime.Token);

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
            Observe(mainViewModel.ActivateInitialWorkspaceAsync(shellLifetime.Token));
            await handoff.RestorePendingAsync().ConfigureAwait(true);
            await DispatchOnUiAsync(() =>
            {
                if (shellLifetime.IsCancellationRequested || Volatile.Read(ref shellClosed) != 0) return;
                if (resultCapabilities.CanUseShareActions) decisionPoll.Start();
            }).ConfigureAwait(true);
            startupUpdateCheck.Start();
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
            settingsViewModel.Dispose();
            transferViewModel.Dispose();
            shareAnalyticsViewModel.Dispose();
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

internal sealed class StartupUpdateCheckCoordinator
{
    private readonly IApplicationUpdateService updateService;
    private readonly Func<string, Task> reportAvailableAsync;
    private readonly CancellationToken cancellationToken;
    private readonly TaskCompletionSource completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private int started;

    public StartupUpdateCheckCoordinator(
        IApplicationUpdateService updateService,
        Func<string, Task> reportAvailableAsync,
        CancellationToken cancellationToken)
    {
        this.updateService = updateService ?? throw new ArgumentNullException(nameof(updateService));
        this.reportAvailableAsync = reportAvailableAsync ??
            throw new ArgumentNullException(nameof(reportAvailableAsync));
        this.cancellationToken = cancellationToken;
    }

    public Task Completion => completion.Task;

    public void Start()
    {
        if (Interlocked.CompareExchange(ref started, 1, 0) != 0)
            return;

        _ = Task.Run(CheckAsync);
    }

    private async Task CheckAsync()
    {
        try
        {
            var snapshot = await updateService
                .CheckAsync(manual: false, cancellationToken)
                .ConfigureAwait(false);
            if (snapshot.Status == ApplicationUpdateStatus.Available &&
                !string.IsNullOrWhiteSpace(snapshot.LatestVersion))
            {
                await reportAvailableAsync(snapshot.LatestVersion).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
        }
        finally
        {
            completion.TrySetResult();
        }
    }
}

internal sealed class WpfApplicationCloseRequester(
    System.Windows.Threading.Dispatcher dispatcher,
    Action requestClose) : IApplicationCloseRequester
{
    public void RequestClose()
    {
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            return;
        if (dispatcher.CheckAccess())
            requestClose();
        else
            dispatcher.BeginInvoke(requestClose);
    }
}
