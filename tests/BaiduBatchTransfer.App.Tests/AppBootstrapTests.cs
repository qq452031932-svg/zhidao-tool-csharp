using BaiduBatchTransfer.App;
using BaiduBatchTransfer.App.Features.Accounts;
using BaiduBatchTransfer.App.Features.BrowserAutomation;
using BaiduBatchTransfer.App.Features.Imports;
using BaiduBatchTransfer.App.Features.Jobs;
using BaiduBatchTransfer.App.Features.Transfers;
using BaiduBatchTransfer.App.Features.Shares;
using BaiduBatchTransfer.App.Features.Results;
using BaiduBatchTransfer.App.Features.Acceptance;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Application.Accounts;
using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Application.Imports;
using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.BaiduAdapter.WebAutomation;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Infrastructure.Database;
using BaiduBatchTransfer.Infrastructure.Excel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using System.Runtime.CompilerServices;

namespace BaiduBatchTransfer.App.Tests;

public sealed class AppBootstrapTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "BaiduBatchTransfer-AppBootstrapTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void HostCompositionRegistersUiServicesButNeverAFakeBaiduAdapter()
    {
        using var host = AppBootstrap.CreateHost(root);

        var transferAdapter = Assert.IsType<CoordinatedOperationBoundBaiduNetdiskAdapter>(
            host.Services.GetRequiredService<IBaiduNetdiskAdapter>());
        Assert.IsAssignableFrom<IOperationBoundBaiduNetdiskAdapter>(transferAdapter);
        var shareAdapter = Assert.IsType<CoordinatedOperationBoundBaiduShareAdapter>(
            host.Services.GetRequiredService<IBaiduShareAdapter>());
        Assert.IsAssignableFrom<IOperationBoundBaiduShareAdapter>(shareAdapter);
        Assert.NotNull(host.Services.GetRequiredService<WebViewBaiduShareAdapter>());
        Assert.NotNull(host.Services.GetRequiredService<StandaloneShareCoordinator>());
        Assert.NotNull(host.Services.GetRequiredService<CreateStandaloneShareBatchService>());
        Assert.NotNull(host.Services.GetRequiredService<TransferCoordinator>());
        Assert.IsType<OperationExecutor>(host.Services.GetRequiredService<ITransferOperationExecutor>());
        Assert.Contains(
            host.Services.GetServices<IHostedService>(),
            service => service.GetType().Name == "TransferCoordinatorHostedService");
        Assert.NotNull(host.Services.GetRequiredService<BaiduBatchTransfer.BaiduAdapter.Http.LiveBaiduNetdiskAdapter>());
        Assert.NotNull(host.Services.GetRequiredService<BaiduBatchTransfer.BaiduAdapter.Http.LiveBaiduShareAdapter>());
        Assert.NotNull(host.Services.GetRequiredService<BaiduAdapterConcurrencyGate>());
        var capability = host.Services.GetRequiredService<AdapterCapability>();
        Assert.False(capability.TransferValidated);
        Assert.False(capability.ShareValidated);
        Assert.IsType<UnavailableAdapterAvailability>(host.Services.GetRequiredService<IAdapterAvailability>());
        Assert.IsType<BaiduAccountLoginLauncher>(host.Services.GetRequiredService<IAccountLoginLauncher>());
        Assert.IsType<LocalImportWorkflow>(host.Services.GetRequiredService<IImportWorkflow>());
        Assert.IsType<ExcelColumnMappingPresenter>(host.Services.GetRequiredService<IExcelColumnMappingPresenter>());
        Assert.NotNull(host.Services.GetRequiredService<OpenXmlWorkbookInspector>());
        Assert.NotNull(host.Services.GetRequiredService<ExcelColumnSuggestionEngine>());
        Assert.NotNull(host.Services.GetRequiredService<IImportPreviewPageSource>());
        Assert.NotNull(host.Services.GetRequiredService<IWizardBatchService>());
        Assert.NotNull(host.Services.GetRequiredService<BulkPrecheckService>());
        Assert.IsType<BulkWizardPrecheckWorkflow>(host.Services.GetRequiredService<IWizardPrecheckWorkflow>());
        Assert.IsType<LocalJobResultsWorkflow>(host.Services.GetRequiredService<IJobResultsWorkflow>());
        Assert.IsType<ResultSaveDialog>(host.Services.GetRequiredService<IResultSaveDialog>());
        Assert.IsType<UnavailableJobResultCapabilityAvailability>(
            host.Services.GetRequiredService<IJobResultCapabilityAvailability>());
        Assert.IsType<ExistingShareDecisionPresenter>(
            host.Services.GetRequiredService<IExistingShareDecisionPresenter>());
        Assert.IsType<ExistingShareDecisionInteraction>(
            host.Services.GetRequiredService<IExistingShareDecisionInteraction>());
        Assert.NotNull(host.Services.GetRequiredService<IJobResultActions>());
        Assert.NotNull(host.Services.GetRequiredService<ReplacementWorkbookExportService>());
        Assert.NotNull(host.Services.GetRequiredService<PauseBatchService>());
        Assert.NotNull(host.Services.GetRequiredService<ResumeBatchService>());
        Assert.NotNull(host.Services.GetRequiredService<RetryFailedTransferService>());
        Assert.IsType<DurableTransferResultDestinationSink>(
            host.Services.GetRequiredService<ITransferResultDestinationSink>());
        Assert.IsType<SqliteLocalResultDestinationStore>(
            host.Services.GetRequiredService<ILocalResultDestinationStore>());
        Assert.IsType<SqliteLocalResultExportStore>(
            host.Services.GetRequiredService<ILocalResultExportStore>());
        Assert.IsType<SqliteLocalResultExportOwnerRepository>(
            host.Services.GetRequiredService<ILocalResultExportOwnerRepository>());
        Assert.IsType<LocalResultExportArtifactService>(
            host.Services.GetRequiredService<ILocalResultExportArtifactService>());
        Assert.IsType<LocalResultExportCoordinator>(
            host.Services.GetRequiredService<ILocalResultExportCoordinator>());
        Assert.Contains(
            host.Services.GetServices<IHostedService>(),
            service => service is LocalResultExportHostedService);
        Assert.IsType<SqliteWebAutomationAttemptStore>(
            host.Services.GetRequiredService<IWebAutomationAttemptStore>());
        Assert.IsType<WebViewRemoteResourcePicker>(
            host.Services.GetRequiredService<IRemoteResourcePicker>());
        Assert.IsType<SqliteRemoteSelectionSessionStore>(
            host.Services.GetRequiredService<IRemoteSelectionSessionStore>());
        Assert.IsType<SqliteStandaloneShareStore>(
            host.Services.GetRequiredService<IStandaloneShareStore>());
        Assert.Same(
            host.Services.GetRequiredService<IStandaloneShareStore>(),
            host.Services.GetRequiredService<IStandaloneShareResultProjectionRepository>());
        Assert.IsType<ShareOfficialResultDestinationResolver>(
            host.Services.GetRequiredService<IShareOfficialResultDestinationResolver>());
        Assert.NotNull(host.Services.GetRequiredService<ExportStandaloneShareResultsService>());
        Assert.Same(
            host.Services.GetRequiredService<IStandaloneShareStore>(),
            host.Services.GetRequiredService<IStandaloneShareExecutionStore>());
        Assert.IsType<ImportWorkspaceFactory>(host.Services.GetRequiredService<IImportWorkspaceFactory>());
        Assert.NotNull(host.Services.GetRequiredService<IAppShellFactory>());
        Assert.IsType<StartupRecoveryRunner>(host.Services.GetRequiredService<IAppRecoveryRunner>());
        Assert.NotNull(host.Services.GetRequiredService<ISingleInstanceGuard>());
        Assert.IsType<AcceptanceWriteLeaseCoordinator>(
            host.Services.GetRequiredService<IAdapterWriteAuthorizationGate>());
        Assert.Same(
            host.Services.GetRequiredService<IAdapterWriteAuthorizationGate>(),
            host.Services.GetRequiredService<IBaiduCandidateActionAuthorizationSource>());
        Assert.IsAssignableFrom<IBoundVisibleWebAutomationSessionFactory>(
            host.Services.GetRequiredService<IVisibleWebAutomationSessionFactory>());
        Assert.IsType<SqliteAdapterCapabilityStore>(
            host.Services.GetRequiredService<IAdapterCapabilityStore>());
        Assert.Single(host.Services.GetServices<ITransferRecoveryAuthorizationSource>());
        Assert.IsType<TransferRecoveryAuthorizationSource>(
            host.Services.GetRequiredService<ITransferRecoveryAuthorizationSource>());
        Assert.NotNull(host.Services.GetRequiredService<
            Func<AdapterWriteAuthorizationRequest, AcceptanceConfirmationWindow>>());
    }

    [Fact]
    public async Task StartingTheHostRunsQueuedTransferWorkThroughTheRegisteredCoordinator()
    {
        var executor = new RecordingTransferExecutor();
        using var host = AppBootstrap.CreateHost(root, services =>
        {
            services.RemoveAll<ITransferOperationExecutor>();
            services.AddSingleton<ITransferOperationExecutor>(executor);
        });
        await host.Services.GetRequiredService<DatabaseMigrator>().MigrateAsync();
        var operationId = Guid.NewGuid();
        await SeedTransferOwnerAsync(
            host.Services.GetRequiredService<SqliteConnectionFactory>(),
            operationId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            generation: 1,
            baiduUserId: "hosted-loop-user",
            DateTimeOffset.UtcNow.AddMinutes(-1));

        await host.StartAsync();
        await executor.Executed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await host.StopAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(operationId, executor.OperationId);
    }

    [Fact]
    public void ExplicitAcceptanceSwitchUsesTheCompileTimeDebugGate()
    {
        using var host = AppBootstrap.CreateHost(
            root,
            [AcceptanceWriteLeaseOptions.Switch]);

        var options = host.Services.GetRequiredService<AcceptanceWriteLeaseOptions>();

#if DEBUG
        Assert.True(options.IsEnabled);
#else
        Assert.False(options.IsEnabled);
#endif
    }

    [Fact]
    public async Task ExactDebugAcceptanceSwitchOpensOnlyTransferCreationWhileWriteLeaseAndResultActionsStayGated()
    {
        var capabilityStore = new RecordingAdapterCapabilityStore();
        var presenter = new RecordingAcceptancePresenter(confirmed: false);
        using var host = AppBootstrap.CreateHost(
            root,
            [AcceptanceWriteLeaseOptions.Switch],
            services =>
            {
                services.RemoveAll<IAdapterCapabilityStore>();
                services.AddSingleton<IAdapterCapabilityStore>(capabilityStore);
                services.RemoveAll<IAcceptanceWriteLeasePresenter>();
                services.AddSingleton<IAcceptanceWriteLeasePresenter>(presenter);
            });

        var availability = host.Services.GetRequiredService<IAdapterAvailability>();
        var resultCapabilities = host.Services.GetRequiredService<IJobResultCapabilityAvailability>();
        var resultActionCapability = host.Services.GetRequiredService<IJobResultActionCapability>();

#if DEBUG
        Assert.True(availability.CanStartTransfers);
#else
        Assert.False(availability.CanStartTransfers);
#endif
        Assert.False(resultCapabilities.CanUseTransferActions);
        Assert.False(resultCapabilities.CanUseShareActions);
        Assert.False(resultActionCapability.CanUseTransferActions);
        Assert.False(resultActionCapability.CanUseShareActions);
        Assert.Empty(capabilityStore.Accepted);

        var request = new AdapterWriteAuthorizationRequest(
            new AdapterCapabilityVersion("BaiduTransfer", "p1", "s1", "b1"),
            Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(),
            AdapterWriteAction.EnsureDirectory);
        var binding = await host.Services.GetRequiredService<IAdapterWriteAuthorizationGate>()
            .TryCreateBindingAsync(request);

        Assert.Null(binding);
#if DEBUG
        Assert.Equal(1, presenter.PresentCount);
#else
        Assert.Equal(0, presenter.PresentCount);
#endif
        Assert.Equal(0, presenter.EnableCount);
        Assert.Empty(capabilityStore.Accepted);
    }

    [Fact]
    public void ExactDebugAcceptanceSwitchAloneMaySkipLegacyBulkPrecheck()
    {
        using var ordinaryHost = AppBootstrap.CreateHost(root);
        using var acceptanceHost = AppBootstrap.CreateHost(
            root,
            [AcceptanceWriteLeaseOptions.Switch]);

        Assert.True(ordinaryHost.Services.GetRequiredService<IAdapterAvailability>()
            .RequiresBulkPrecheckBeforeStart);
#if DEBUG
        Assert.False(acceptanceHost.Services.GetRequiredService<IAdapterAvailability>()
            .RequiresBulkPrecheckBeforeStart);
#else
        Assert.True(acceptanceHost.Services.GetRequiredService<IAdapterAvailability>()
            .RequiresBulkPrecheckBeforeStart);
#endif
    }

    [Theory]
    [InlineData("--webview-acceptance ")]
    [InlineData("--WEBVIEW-ACCEPTANCE")]
    [InlineData("--webview-acceptance=true")]
    public void NearMissAcceptanceSwitchKeepsTransfersUnavailable(string argument)
    {
        using var host = AppBootstrap.CreateHost(root, [argument]);

        Assert.False(host.Services.GetRequiredService<IAdapterAvailability>().CanStartTransfers);
    }

    [Fact]
    public async Task HostBindsTransferV2WithoutChangingTheIndependentShareV1Capability()
    {
        var gate = new RecordingWriteAuthorizationGate();
        using var host = AppBootstrap.CreateHost(root, services =>
        {
            services.RemoveAll<IAdapterWriteAuthorizationGate>();
            services.AddSingleton<IAdapterWriteAuthorizationGate>(gate);
        });
        var accountId = Guid.NewGuid();
        var session = new AccountSession(
            accountId,
            "stable-baidu-user",
            [new SessionCookie("BDUSS", "private", ".baidu.com", "/", null, true, true)],
            DateTimeOffset.UtcNow);
        var transferContext = new TransferOperationAutomationContext(
            Guid.NewGuid(), Guid.NewGuid(), accountId, 1,
            new ShareReference("share-id", "https://pan.baidu.com/s/share-id", "a1b2"));
        var items = new RemoteItemIdentitySet([new("remote-1", "item")]);
        var shareContext = new ShareOperationAutomationContext(
            Guid.NewGuid(), Guid.NewGuid(), accountId, 1,
            WebAutomationOwnerKind.StandaloneShare, items);

        await Assert.IsAssignableFrom<IOperationBoundBaiduNetdiskAdapter>(
                host.Services.GetRequiredService<IBaiduNetdiskAdapter>())
            .EnsureDirectoryForOperationAsync(
                transferContext, session, new RemotePath("/target"), CancellationToken.None);
        await Assert.IsAssignableFrom<IOperationBoundBaiduShareAdapter>(
                host.Services.GetRequiredService<IBaiduShareAdapter>())
            .PreparePermanentShareForOperationAsync(
                shareContext, session, new PermanentShareRequest(items), CancellationToken.None);

        var transfer = Assert.Single(gate.Requests, request =>
            request.Capability.AdapterKind == "BaiduTransfer");
        var share = Assert.Single(gate.Requests, request =>
            request.Capability.AdapterKind == "BaiduShare");
        Assert.Equal(BaiduSelectorCatalog.TransferProtocolVersion, transfer.Capability.ProtocolVersion);
        Assert.Equal(BaiduSelectorCatalog.TransferSelectorVersion, transfer.Capability.SelectorVersion);
        Assert.Equal(BaiduSelectorCatalog.ProtocolVersion, share.Capability.ProtocolVersion);
        Assert.Equal(BaiduSelectorCatalog.SelectorVersion, share.Capability.SelectorVersion);
    }

    [Fact]
    public async Task OnlyExactTransferV2FormalRecordAuthorizesTheProductionBinding()
    {
        using var host = AppBootstrap.CreateHost(root);
        await host.Services.GetRequiredService<DatabaseMigrator>().MigrateAsync();
        var store = host.Services.GetRequiredService<IAdapterCapabilityStore>();
        var gate = host.Services.GetRequiredService<IAdapterWriteAuthorizationGate>();
        var build = typeof(AppBootstrap).Assembly.GetName().Version?.ToString() ?? "1.0.0";
        var request = new AdapterWriteAuthorizationRequest(
            new AdapterCapabilityVersion(
                "BaiduTransfer",
                BaiduSelectorCatalog.TransferProtocolVersion,
                BaiduSelectorCatalog.TransferSelectorVersion,
                build),
            Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(),
            AdapterWriteAction.EnsureDirectory);
        var acceptedAt = DateTimeOffset.UtcNow.AddMinutes(-1);

        await store.AcceptAsync(new AdapterCapabilityAcceptanceRecord(
            "BaiduTransfer", BaiduSelectorCatalog.ProtocolVersion,
            BaiduSelectorCatalog.SelectorVersion, build, acceptedAt, new string('A', 64)));
        await store.AcceptAsync(new AdapterCapabilityAcceptanceRecord(
            "BaiduShare", BaiduSelectorCatalog.TransferProtocolVersion,
            BaiduSelectorCatalog.TransferSelectorVersion, build, acceptedAt, new string('B', 64)));
        await store.AcceptAsync(new AdapterCapabilityAcceptanceRecord(
            "BaiduTransfer", BaiduSelectorCatalog.TransferProtocolVersion,
            BaiduSelectorCatalog.TransferSelectorVersion, build + "-other", acceptedAt,
            new string('C', 64)));

        Assert.Null(await gate.TryCreateBindingAsync(request));

        await store.AcceptAsync(new AdapterCapabilityAcceptanceRecord(
            request.Capability.AdapterKind, request.Capability.ProtocolVersion,
            request.Capability.SelectorVersion, request.Capability.BuildVersion,
            acceptedAt, new string('D', 64)));

        var binding = await gate.TryCreateBindingAsync(request);
        Assert.NotNull(binding);
        Assert.Equal(AdapterWriteAuthorizationSource.ExactFormalRecord, binding.Source);
        Assert.Equal(request.Capability, binding.Capability);
    }

    [Fact]
    public async Task StoreBackedTransferRecoveryAuthorizationSurvivesHostRestart()
    {
        var operationId = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        const int generation = 2;
        const string baiduUserId = "stable-baidu-user";
        var build = typeof(AppBootstrap).Assembly.GetName().Version?.ToString() ?? "1.0.0";
        var capability = new AdapterCapabilityVersion(
            "BaiduTransfer",
            BaiduSelectorCatalog.TransferProtocolVersion,
            BaiduSelectorCatalog.TransferSelectorVersion,
            build);
        var target = new RemoteDirectory("target-id", new RemotePath("/target"));
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);

        using (var firstHost = AppBootstrap.CreateHost(root))
        {
            await firstHost.Services.GetRequiredService<DatabaseMigrator>().MigrateAsync();
            await SeedTransferOwnerAsync(
                firstHost.Services.GetRequiredService<SqliteConnectionFactory>(),
                operationId, batchId, accountId, generation, baiduUserId, now);
            var attempts = firstHost.Services.GetRequiredService<IWebAutomationAttemptStore>();
            Assert.True(await attempts.BeginAsync(WebAutomationAttemptRecord.Create(
                operationId, accountId, generation, WebAutomationOwnerKind.Transfer, now)));
            var source = new TransferSourceEvidence(
                new RemoteItemIdentitySet([new("source-id", "source")]),
                1, true, true, "source-generation", baiduUserId, capability,
                new string('a', 64));
            Assert.True(await attempts.TryPersistTransferSourceEvidenceAsync(
                operationId, accountId, generation, 0, source, now.AddSeconds(1)));
            var baseline = WebAutomationBaseline.Create(["existing-id"]);
            var preparation = new TransferTargetPreparationEvidence(
                target.RemoteId, target.Path, 1, true, true, "target-generation",
                baiduUserId, baseline, new string('b', 64));
            Assert.True(await attempts.TryPersistTransferTargetPreparationAsync(
                operationId, accountId, generation, 1, preparation, now.AddSeconds(2)));
            var submission = new TransferSubmissionEvidence(
                source.Items.Fingerprint, target.RemoteId, baseline.Fingerprint,
                baiduUserId, capability, now.AddSeconds(3));
            Assert.True(await attempts.TryPersistTransferSubmissionAsync(
                operationId, accountId, generation, 2, submission, now.AddSeconds(3)));
            Assert.True(await attempts.TryAdvanceAsync(
                operationId, accountId, generation,
                WebAutomationAttemptState.Submitting, 3,
                WebAutomationAttemptState.ResultPending, new string('c', 64), false,
                now.AddSeconds(4)));
        }

        var ownership = new ExactOwnershipStore(
            operationId, batchId, accountId, generation, WebAutomationOwnerKind.Transfer);
        using var restarted = AppBootstrap.CreateHost(root, services =>
        {
            services.RemoveAll<IWebAutomationRequestOwnershipStore>();
            services.AddSingleton<IWebAutomationRequestOwnershipStore>(ownership);
        });
        await restarted.Services.GetRequiredService<DatabaseMigrator>().MigrateAsync();
        var sources = restarted.Services.GetServices<ITransferRecoveryAuthorizationSource>().ToArray();

        var sourceAfterRestart = Assert.Single(sources);
        var binding = await sourceAfterRestart.TryAuthorizeSubmissionRecoveryAsync(
            new TransferSubmissionRecoveryAuthorizationRequest(
                operationId, batchId, accountId, generation,
                target, baiduUserId, capability));

        Assert.NotNull(binding);
        Assert.Equal(target, binding.Target);
        Assert.Equal(baiduUserId, binding.BaiduUserId);
        Assert.Null(binding.MappingFingerprint);
    }

#if DEBUG
    [Fact]
    public async Task DebugAcceptanceLeaseCreatesOnlyOneTransferV2BindingForTheExactOperation()
    {
        var presenter = new RecordingAcceptancePresenter(confirmed: true);
        using var host = AppBootstrap.CreateHost(root, [AcceptanceWriteLeaseOptions.Switch], services =>
        {
            services.RemoveAll<IAcceptanceWriteLeasePresenter>();
            services.AddSingleton<IAcceptanceWriteLeasePresenter>(presenter);
        });
        await host.Services.GetRequiredService<DatabaseMigrator>().MigrateAsync();
        var build = typeof(AppBootstrap).Assembly.GetName().Version?.ToString() ?? "1.0.0";
        var request = new AdapterWriteAuthorizationRequest(
            new AdapterCapabilityVersion(
                "BaiduTransfer",
                BaiduSelectorCatalog.TransferProtocolVersion,
                BaiduSelectorCatalog.TransferSelectorVersion,
                build),
            Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(),
            AdapterWriteAction.EnsureDirectory);
        var gate = host.Services.GetRequiredService<IAdapterWriteAuthorizationGate>();

        var first = await gate.TryCreateBindingAsync(request);
        var second = await gate.TryCreateBindingAsync(request);

        Assert.NotNull(first);
        Assert.Equal(AdapterWriteAuthorizationSource.ConsumedLease, first.Source);
        Assert.Null(second);
    }
#endif

    [Fact]
    public async Task ProductionGatePresentsExactWriteAndReachesInnerOnlyAfterExplicitConfirmation()
    {
        var presenter = new RecordingAcceptancePresenter(confirmed: true);
        using var host = AppBootstrap.CreateHost(root, [AcceptanceWriteLeaseOptions.Switch], services =>
        {
            services.RemoveAll<IAcceptanceWriteLeasePresenter>();
            services.AddSingleton<IAcceptanceWriteLeasePresenter>(presenter);
        });
        await host.Services.GetRequiredService<DatabaseMigrator>().MigrateAsync();
        var capability = new AdapterCapabilityVersion("BaiduShare", "p1", "s1", "b1");
        var inner = new AcceptanceShareAdapter();
        var wrapped = Assert.IsAssignableFrom<IOperationBoundBaiduShareAdapter>(
            CoordinatedBaiduShareAdapter.Wrap(
                inner,
                new BaiduAdapterConcurrencyGate(),
                host.Services.GetRequiredService<IAdapterWriteAuthorizationGate>(),
                capability));
        var accountId = Guid.NewGuid();
        var items = new RemoteItemIdentitySet([new("remote-1", "item")]);
        var context = new ShareOperationAutomationContext(
            Guid.NewGuid(), Guid.NewGuid(), accountId, 1,
            WebAutomationOwnerKind.StandaloneShare, items);
        var session = new AccountSession(
            accountId, "baidu-user",
            [new SessionCookie("BDUSS", "private", ".baidu.com", "/", null, true, true)],
            DateTimeOffset.UtcNow);

        var prepared = await wrapped.PreparePermanentShareForOperationAsync(
            context, session, new PermanentShareRequest(items), CancellationToken.None);
        var result = await wrapped.SubmitPermanentShareForOperationAsync(
            context, session, new PermanentShareRequest(items), CancellationToken.None);

        Assert.True(prepared.IsSuccess);
        Assert.True(result.IsSuccess);
        Assert.Equal(1, presenter.PresentCount);
        Assert.Equal(context.OperationId, presenter.Seen!.OperationId);
        Assert.Equal(context.AccountId, presenter.Seen.AccountId);
        Assert.Equal(context.Generation, presenter.Seen.Generation);
        Assert.Equal(AdapterWriteAction.PrepareShare, presenter.Seen.Action);
        Assert.Equal(1, inner.PrepareCount);
        Assert.Equal(1, inner.SubmitCount);
    }

    [Fact]
    public async Task ProductionGateNeverPresentsInNormalModeOrForRecovery()
    {
        var presenter = new RecordingAcceptancePresenter(confirmed: true);
        using var normal = AppBootstrap.CreateHost(root, services =>
        {
            services.RemoveAll<IAcceptanceWriteLeasePresenter>();
            services.AddSingleton<IAcceptanceWriteLeasePresenter>(presenter);
        });
        await normal.Services.GetRequiredService<DatabaseMigrator>().MigrateAsync();
        var gate = normal.Services.GetRequiredService<IAdapterWriteAuthorizationGate>();
        var capability = new AdapterCapabilityVersion("BaiduShare", "p1", "s1", "b1");
        var identity = new AdapterWriteAuthorizationRequest(
            capability, Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(), AdapterWriteAction.SubmitShare);

        Assert.False(await gate.TryAuthorizeAsync(identity));
        Assert.Equal(0, presenter.PresentCount);

        var recoveryPresenter = new RecordingAcceptancePresenter(confirmed: true);
        var recoveryGate = new AcceptanceWriteLeaseCoordinator(
            new AcceptanceWriteLeaseOptions(true, [AcceptanceWriteLeaseOptions.Switch]),
            normal.Services.GetRequiredService<IAdapterCapabilityStore>(),
            TimeProvider.System,
            recoveryPresenter);
        Assert.False(await recoveryGate.TryAuthorizeAsync(identity with { IsRecovery = true }));
        Assert.Equal(0, recoveryPresenter.PresentCount);
    }

    [Fact]
    public async Task ProductionPrecheckSnapshotReadsUpdatedActionableCountsFromMigratedDatabase()
    {
        using var host = AppBootstrap.CreateHost(root);
        await host.Services.GetRequiredService<DatabaseMigrator>().MigrateAsync();
        var sessionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var rows = new[]
        {
            StagedImportRowRecord.Create(Guid.NewGuid(), sessionId, 1, "invalid", "Invalid"),
            StagedImportRowRecord.Create(Guid.NewGuid(), sessionId, 2, "pending", "NeedsResolution"),
            StagedImportRowRecord.Create(Guid.NewGuid(), sessionId, 3, "conflict", "Conflict"),
            FailedRow(sessionId, 4, "INVALID_LINK", now),
            FailedRow(sessionId, 5, "INVALID_EXTRACTION_CODE", now),
            FailedRow(sessionId, 6, "ACCOUNT_CAPACITY", now),
            FailedRow(sessionId, 7, "INVALID_TARGET_PATH", now),
        };
        await using (var unit = await host.Services.GetRequiredService<IUnitOfWorkFactory>().BeginAsync())
        {
            await unit.Imports.AddSessionAsync(ImportSessionRecord.Create(sessionId, "Text", "paste"));
            await unit.Imports.AddRowsAsync(rows);
            await unit.CommitAsync();
        }

        var snapshot = await host.Services.GetRequiredService<IWizardPrecheckSnapshotQuery>()
            .LoadAsync(sessionId, CancellationToken.None);

        Assert.Equal(new WizardPrecheckActionableSnapshot(3, 1, 2, 1), snapshot);
    }

    [Fact]
    public async Task ProductionPrecheckSnapshotCountsEveryUnknownOrTransientFailureAndChecksAuthority()
    {
        using var host = AppBootstrap.CreateHost(root);
        await host.Services.GetRequiredService<DatabaseMigrator>().MigrateAsync();
        var sessionId = Guid.NewGuid();
        var account = AccountRecord.Create(Guid.NewGuid(), "precheck-user", "检查账号");
        var now = DateTimeOffset.UtcNow;
        var session = ImportSessionRecord.Create(sessionId, "Text", "paste") with
        {
            Status = "ReadyToConfirm",
            TotalRows = 4,
            ReadyRows = 4,
            PrecheckAccountId = account.Id,
        };
        var succeeded = StagedImportRowRecord.Create(Guid.NewGuid(), sessionId, 1, "ok", "Ready") with
        {
            NormalizedShareId = "ok",
            PrecheckStatus = "Succeeded",
            PrecheckedAt = now,
        };
        await using (var unit = await host.Services.GetRequiredService<IUnitOfWorkFactory>().BeginAsync())
        {
            await unit.Accounts.UpsertAsync(account);
            await unit.Imports.AddSessionAsync(session);
            await unit.Imports.AddRowsAsync([
                succeeded,
                FailedRow(sessionId, 2, "TEMPORARY_UNAVAILABLE", now),
                FailedRow(sessionId, 3, "UNKNOWN_RESPONSE", now),
                FailedRow(sessionId, 4, "FUTURE_SERVER_CODE", now),
            ]);
            await unit.CommitAsync();
        }

        var snapshot = await host.Services.GetRequiredService<IWizardPrecheckSnapshotQuery>()
            .LoadAsync(sessionId, account.Id, CancellationToken.None);

        Assert.Equal(3, snapshot.OtherFailureCount);
        Assert.Equal(3, snapshot.FailedRowCount);
        Assert.True(snapshot.IsConsistent);
    }

    [Fact]
    public void ShellCompositionBuildsOnlyTransferAndShareWorkspaces()
    {
        var bootstrap = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "BaiduBatchTransfer.App",
            "AppBootstrap.cs"));

        Assert.Contains("var transferWorkspace = new TransferWorkspaceView(transferViewModel)", bootstrap);
        Assert.Contains("var shareWorkspace = new ShareWorkspaceView(shareViewModel)", bootstrap);
        Assert.Contains("transferWorkspace,", bootstrap);
        Assert.Contains("shareWorkspace,", bootstrap);
        Assert.DoesNotContain("mainViewModel.ShowResults", bootstrap);
        Assert.DoesNotContain("ShowImportCommand", bootstrap);
        Assert.DoesNotContain("ShowCreateJobCommand", bootstrap);
        Assert.DoesNotContain("ShowResultsCommand", bootstrap);
        Assert.DoesNotContain("transferWorkspace.Content = resultsView", bootstrap);
    }

    [Fact]
    public void ShellCompositionKeepsItsLifetimeUntilWindowClose()
    {
        var bootstrap = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "BaiduBatchTransfer.App",
            "AppBootstrap.cs"));

        Assert.DoesNotContain("using var shellLifetime", bootstrap);
        Assert.DoesNotContain("using var handoff", bootstrap);
        Assert.Contains("handoff.Dispose();", bootstrap);
        Assert.Contains("decisionPoll.Tick -= OnDecisionPollTick;", bootstrap);
        Assert.Contains("transferViewModel.Dispose();", bootstrap);
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
    public async Task EligibleAccountSnapshotIsStableAndBoundedWhileVisibleCountRemainsExact()
    {
        using var host = AppBootstrap.CreateHost(root);
        await host.Services.GetRequiredService<DatabaseMigrator>().MigrateAsync();
        await using (var unit = await host.Services.GetRequiredService<IUnitOfWorkFactory>().BeginAsync())
        {
            for (var index = 1; index <= 1_001; index++)
            {
                var account = AccountRecord.Create(
                    Guid.Parse($"00000000-0000-0000-0000-{index:000000000000}"),
                    $"baidu-{index:0000}",
                    $"账号 {index:0000}") with
                {
                    SortOrder = index,
                };
                await unit.Accounts.UpsertAsync(account);
            }
            await unit.CommitAsync();
        }

        var snapshot = await host.Services.GetRequiredService<IWizardAccountProvider>().LoadEligibleAsync();
        var visibleCount = await host.Services.GetRequiredService<IAccountSummaryProvider>()
            .GetActiveAccountCountAsync();

        Assert.Equal(1_000, snapshot.Count);
        Assert.Equal("00000000-0000-0000-0000-000000000001", snapshot[0].AccountId.ToString("D"));
        Assert.Equal("00000000-0000-0000-0000-000000001000", snapshot[^1].AccountId.ToString("D"));
        Assert.Equal(1_001, visibleCount);
    }

    [Fact]
    public async Task OlderQueuedRefreshCannotApplyAfterANewerGenerationStarts()
    {
        var provider = new ControlledWizardAccountProvider();
        var dispatcher = new QueuedDispatcher();
        IReadOnlyList<WizardAccountOption> applied = [];
        int? olderVisibleCount = null;
        int? newerVisibleCount = null;
        var coordinator = new AccountRefreshCoordinator(
            provider,
            new FixedAccountSummaryProvider(2),
            snapshot => applied = snapshot,
            dispatcher.DispatchAsync);

        var older = coordinator.RefreshAsync(count => olderVisibleCount = count);
        provider.Complete(0, [AccountOption(1)]);
        await dispatcher.FirstQueued.Task;

        var newer = coordinator.RefreshAsync(count => newerVisibleCount = count);
        provider.Complete(1, [AccountOption(2), AccountOption(3)]);
        await dispatcher.SecondQueued.Task;

        dispatcher.Run(0);
        await older;
        Assert.Empty(applied);
        Assert.Null(olderVisibleCount);

        dispatcher.Run(1);
        await newer;
        Assert.Equal(2, newerVisibleCount);
        Assert.Equal([AccountOption(2).AccountId, AccountOption(3).AccountId], applied.Select(x => x.AccountId));
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

    private static string FindRepositoryRoot([CallerFilePath] string sourceFilePath = "")
    {
        var current = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "BaiduBatchTransfer.sln")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("未找到测试仓库目录。");
    }

    private static StagedImportRowRecord FailedRow(
        Guid sessionId,
        int sourceRow,
        string errorCode,
        DateTimeOffset now) =>
        StagedImportRowRecord.Create(Guid.NewGuid(), sessionId, sourceRow, $"link-{sourceRow}", "Ready") with
        {
            NormalizedShareId = $"share-{sourceRow}",
            PrecheckStatus = "Failed",
            PrecheckErrorCode = errorCode,
            PrecheckedAt = now,
        };

    private static async Task SeedTransferOwnerAsync(
        SqliteConnectionFactory factory,
        Guid operationId,
        Guid batchId,
        Guid accountId,
        int generation,
        string baiduUserId,
        DateTimeOffset now)
    {
        var resourceId = Guid.NewGuid();
        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Accounts(Id,BaiduUserId,DisplayName,LoginMethod,Status,DailyLimit,DailyExecutedCount,
              SortOrder,IsEnabled,DailyCountDateKey,IsDeleted)
            VALUES($account,$uid,'recovery-owner','WebView2','Active',0,0,0,1,'2026-08-26',0);
            INSERT INTO BatchJobs(Id,Name,SourceType,TransferMode,QuotaBasis,ShortagePolicy,ResourceCount,
              OperationCount,DefaultTargetPath,FolderCreationMode,DuplicatePolicy,ValidationMode,Status,CreatedAt,QueueOrder)
            VALUES($batch,'recovery-job','Text','Distribution','TaskCount','ProportionalReduction',1,1,'/target',
              'None','Skip','Immediate','Active',$now,1);
            INSERT INTO AccountAllocations(Id,BatchJobId,AccountId,PriorityOrder,DefaultTargetPath,
              NormalizedDefaultTargetPath,TargetItemCount,TargetSuccessCount,EffectiveTargetSuccessCount,
              AssignedItemCount,AttemptedItemCount,NewSuccessItemCount,AlreadySatisfiedItemCount,
              SuccessItemCount,FailedItemCount,LockedItemCount,ExclusiveCandidateCount,UnusedCandidateCount,
              ReservedSlotCount,FulfilledCount)
            VALUES($allocation,$batch,$account,1,'/target','/target',1,1,1,1,0,0,0,0,0,0,0,0,0,0);
            INSERT INTO ResourceItems(Id,BatchJobId,CandidateSequence,ShareUrl,NormalizedShareId,ExtractionCode,
              FolderCreationModeOverride,GlobalValidityStatus,CandidateStatus,CreatedAt,UpdatedAt)
            VALUES($resource,$batch,1,'https://pan.baidu.com/s/test','test','a1b2','None','Valid','Expanded',$now,$now);
            INSERT INTO TransferOperations(Id,ResourceItemId,BatchJobId,SequenceNumber,AllocationOrdinal,TargetPath,
              NormalizedTargetPath,CreateFolderName,AssignedAccountId,AllocationSource,Status,AttemptCount,
              OperationFingerprint,SubmitState,TargetSlotState,CreatedAt,UpdatedAt)
            VALUES($operation,$resource,$batch,1,1,'/target','/target','',$account,'Automatic','Waiting',$generation,
              'transfer-fingerprint','NotPrepared','Unoccupied',$now,$now);
            """;
        command.Parameters.AddWithValue("$account", accountId.ToString("D"));
        command.Parameters.AddWithValue("$uid", baiduUserId);
        command.Parameters.AddWithValue("$batch", batchId.ToString("D"));
        command.Parameters.AddWithValue("$allocation", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$resource", resourceId.ToString("D"));
        command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        command.Parameters.AddWithValue("$generation", generation);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static WizardAccountOption AccountOption(int value) => new(
        Guid.Parse($"00000000-0000-0000-0000-{value:000000000000}"),
        $"账号 {value}",
        "Active",
        true,
        false,
        null,
        null,
        0, 0, 0, 0, 0, 0, 0, 0, 0,
        BatchAccountSelectionDecision.NotSelected);

    private sealed class ControlledWizardAccountProvider : IWizardAccountProvider
    {
        private readonly List<TaskCompletionSource<IReadOnlyList<WizardAccountOption>>> calls = [];

        public Task<IReadOnlyList<WizardAccountOption>> LoadEligibleAsync(
            CancellationToken cancellationToken = default)
        {
            var completion = new TaskCompletionSource<IReadOnlyList<WizardAccountOption>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            calls.Add(completion);
            return completion.Task.WaitAsync(cancellationToken);
        }

        public void Complete(int index, IReadOnlyList<WizardAccountOption> accounts) =>
            calls[index].TrySetResult(accounts);
    }

    private sealed class FixedAccountSummaryProvider(int count) : IAccountSummaryProvider
    {
        public Task<int> GetActiveAccountCountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(count);
    }

    private sealed class QueuedDispatcher
    {
        private readonly List<(Action Action, TaskCompletionSource Completion)> queue = [];
        public TaskCompletionSource FirstQueued { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondQueued { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task DispatchAsync(Action action)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            queue.Add((action, completion));
            if (queue.Count == 1) FirstQueued.TrySetResult();
            if (queue.Count == 2) SecondQueued.TrySetResult();
            return completion.Task;
        }

        public void Run(int index)
        {
            var item = queue[index];
            item.Action();
            item.Completion.TrySetResult();
        }
    }

    private sealed class RecordingAcceptancePresenter(bool confirmed) : IAcceptanceWriteLeasePresenter
    {
        public int PresentCount { get; private set; }
        public AdapterWriteAuthorizationRequest? Seen { get; private set; }
        public int EnableCount { get; private set; }

        public Task<bool> PresentAsync(AdapterWriteAuthorizationRequest request, CancellationToken cancellationToken = default)
        {
            PresentCount++;
            Seen = request;
            return Task.FromResult(confirmed);
        }

        public Task EnableFormalAcceptanceAsync(AdapterWriteAuthorizationRequest request, string evidenceHash, CancellationToken cancellationToken = default)
        {
            EnableCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingAdapterCapabilityStore : IAdapterCapabilityStore
    {
        public List<AdapterCapabilityAcceptanceRecord> Accepted { get; } = [];

        public Task AcceptAsync(
            AdapterCapabilityAcceptanceRecord acceptance,
            CancellationToken cancellationToken = default)
        {
            Accepted.Add(acceptance);
            return Task.CompletedTask;
        }

        public Task<AdapterCapabilityAcceptanceRecord?> GetAsync(
            string adapterKind,
            string protocolVersion,
            string selectorVersion,
            string adapterBuildVersion,
            CancellationToken cancellationToken = default) => Task.FromResult<AdapterCapabilityAcceptanceRecord?>(null);
    }

    private sealed class RecordingWriteAuthorizationGate : IAdapterWriteAuthorizationGate
    {
        public List<AdapterWriteAuthorizationRequest> Requests { get; } = [];

        public ValueTask<bool> TryAuthorizeAsync(
            AdapterWriteAuthorizationRequest request,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(false);

        public ValueTask<AdapterWriteAuthorizationBinding?> TryCreateBindingAsync(
            AdapterWriteAuthorizationRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult<AdapterWriteAuthorizationBinding?>(null);
        }
    }

    private sealed class RecordingTransferExecutor : ITransferOperationExecutor
    {
        public TaskCompletionSource Executed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Guid? OperationId { get; private set; }

        public Task ExecuteAsync(Guid operationId, CancellationToken cancellationToken = default)
        {
            OperationId = operationId;
            Executed.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private sealed class ExactOwnershipStore(
        Guid operationId,
        Guid batchId,
        Guid accountId,
        int generation,
        WebAutomationOwnerKind ownerKind) : IWebAutomationRequestOwnershipStore
    {
        public Task<bool> IsBoundAsync(
            Guid candidateOperationId,
            Guid candidateBatchId,
            Guid candidateAccountId,
            int candidateGeneration,
            WebAutomationOwnerKind candidateOwnerKind,
            CancellationToken cancellationToken = default) => Task.FromResult(
                candidateOperationId == operationId &&
                candidateBatchId == batchId &&
                candidateAccountId == accountId &&
                candidateGeneration == generation &&
                candidateOwnerKind == ownerKind);
    }

    private sealed class AcceptanceShareAdapter : IOperationBoundBaiduShareAdapter
    {
        public int SubmitCount { get; private set; }
        public int PrepareCount { get; private set; }

        public Task<BaiduAdapterResult<ShareCreationReceipt>> SubmitPermanentShareForOperationAsync(
            ShareOperationAutomationContext context,
            AccountSession session,
            PermanentShareRequest request,
            CancellationToken cancellationToken)
        {
            SubmitCount++;
            return Task.FromResult(BaiduAdapterResult<ShareCreationReceipt>.Success(new ShareCreationReceipt(
                DateTimeOffset.UtcNow,
                "request-1",
                request,
                "share-1",
                "https://pan.baidu.com/s/share-1",
                new EffectiveShareSettings(true, true, ShareExtractionCodeBehavior.Random, "a1b2"))));
        }

        public Task<BaiduAdapterResult<SharePreparationReceipt>> PreparePermanentShareForOperationAsync(ShareOperationAutomationContext context, AccountSession session, PermanentShareRequest request, CancellationToken cancellationToken)
        {
            Assert.NotNull(context.AuthorizationBinding);
            PrepareCount++;
            return Task.FromResult(BaiduAdapterResult<SharePreparationReceipt>.Success(SharePreparationReceipt.Ready));
        }
        public Task<BaiduAdapterResult<ShareCreationReceipt>> ConfirmExactShareForOperationAsync(ShareOperationAutomationContext context, AccountSession session, PermanentShareRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<ShareCreationReceipt>> CreatePermanentShareAsync(AccountSession session, PermanentShareRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<BaiduSharePage>> ListSharesAsync(AccountSession session, string? cursor, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
