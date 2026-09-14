using BaiduBatchTransfer.App;
using BaiduBatchTransfer.App.Features.Accounts;
using BaiduBatchTransfer.App.Features.Common;
using BaiduBatchTransfer.App.Features.BrowserAutomation;
using BaiduBatchTransfer.App.Features.ManagedBrowser;
using BaiduBatchTransfer.App.Features.Imports;
using BaiduBatchTransfer.App.Features.Jobs;
using BaiduBatchTransfer.App.Features.Transfers;
using BaiduBatchTransfer.App.Features.Shares;
using BaiduBatchTransfer.App.Features.Results;
using BaiduBatchTransfer.App.Features.Acceptance;
using BaiduBatchTransfer.App.Features.ShareAnalytics;
using BaiduBatchTransfer.App.Features.Licensing;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.App.Updates;
using BaiduBatchTransfer.Application.Accounts;
using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Application.Imports;
using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.Application.ShareAnalytics;
using BaiduBatchTransfer.BaiduAdapter.WebAutomation;
using BaiduBatchTransfer.BaiduAdapter.Http;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Infrastructure.Database;
using BaiduBatchTransfer.Infrastructure.Excel;
using BaiduBatchTransfer.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using SuperPan.Licensing.Client;

namespace BaiduBatchTransfer.App.Tests;

public sealed class AppBootstrapTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "BaiduBatchTransfer-AppBootstrapTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void LocalHistoryCleanupUsesOneStoreInstanceAcrossScopes()
    {
        using var host = AppBootstrap.CreateHost(root);
        var store = host.Services.GetRequiredService<SqliteLocalTerminalHistoryStore>();
        using var scope = host.Services.CreateScope();
        Assert.Same(store, host.Services.GetRequiredService<ILocalTerminalHistoryStore>());
        Assert.Same(store, scope.ServiceProvider.GetRequiredService<ILocalTerminalHistoryStore>());
        Assert.Same(host.Services.GetRequiredService<LocalTerminalHistoryService>(),
            scope.ServiceProvider.GetRequiredService<LocalTerminalHistoryService>());
        Assert.IsType<WpfLocalHistoryCleanupInteraction>(host.Services.GetRequiredService<ILocalHistoryCleanupInteraction>());
    }

    [Fact]
    public void LocalImportDraftCleanupUsesOneStoreAndServiceInstanceAcrossScopes()
    {
        using var host = AppBootstrap.CreateHost(root);
        var concreteStore = host.Services.GetRequiredService<SqliteLocalImportDraftCleanupStore>();
        var service = host.Services.GetRequiredService<LocalImportDraftCleanupService>();
        using var scope = host.Services.CreateScope();

        Assert.Same(concreteStore, host.Services.GetRequiredService<ILocalImportDraftCleanupStore>());
        Assert.Same(concreteStore, scope.ServiceProvider.GetRequiredService<ILocalImportDraftCleanupStore>());
        Assert.Same(service, scope.ServiceProvider.GetRequiredService<LocalImportDraftCleanupService>());
        Assert.Same(concreteStore, PrivateField<ILocalImportDraftCleanupStore>(service, "store"));
        var shellConstructorParameters = Assert.Single(typeof(AppShellFactory).GetConstructors()).GetParameters();
        Assert.Contains(shellConstructorParameters, parameter =>
            parameter.ParameterType == typeof(LocalImportDraftCleanupService));
    }

    [Fact]
    public void BootstrapUsesTheApprovedUserFacingBrandAndDefaultExportName()
    {
        var repositoryRoot = FindRepositoryRoot();
        var bootstrap = File.ReadAllText(Path.Combine(
            repositoryRoot, "src", "BaiduBatchTransfer.App", "AppBootstrap.cs"));
        var app = File.ReadAllText(Path.Combine(
            repositoryRoot, "src", "BaiduBatchTransfer.App", "App.xaml.cs"));

        Assert.Contains("\"知道综合工具\"", bootstrap, StringComparison.Ordinal);
        Assert.Contains("\"知道综合工具结果\"", bootstrap, StringComparison.Ordinal);
        Assert.DoesNotContain("\"百度批量转存\"", bootstrap, StringComparison.Ordinal);
        Assert.DoesNotContain("百度批量转存结果", bootstrap, StringComparison.Ordinal);
        Assert.DoesNotContain("\"百度批量转存\"", app, StringComparison.Ordinal);
        Assert.Contains("\"知道综合工具\"", app, StringComparison.Ordinal);
    }

    [Fact]
    public void LicenseGateAndRuntimeStateAreSingleSharedBootstrapServices()
    {
        using var host = AppBootstrap.CreateHost(root);

        var gate = host.Services.GetRequiredService<ILicenseAuthorizationGate>();
        var state = host.Services.GetRequiredService<LicenseRuntimeAuthorizationState>();
        var startup = host.Services.GetRequiredService<AppStartupController>();
        var transfer = host.Services.GetServices<IHostedService>()
            .OfType<TransferCoordinatorHostedService>().Single();
        var share = host.Services.GetServices<IHostedService>()
            .OfType<StandaloneShareCoordinatorHostedService>().Single();

        Assert.Same(gate, PrivateField<ILicenseAuthorizationGate>(startup, "licenseGate"));
        Assert.Same(state, PrivateField<LicenseRuntimeAuthorizationState>(startup, "authorizationState"));
        Assert.Same(state, PrivateField<LicenseRuntimeAuthorizationState>(transfer, "authorizationState"));
        Assert.Same(state, PrivateField<LicenseRuntimeAuthorizationState>(share, "authorizationState"));
    }

    [Fact]
    public async Task RejectedLicensePreparesFreshAndOlderDatabasesAndCreatesLocalShellWithoutAuthorizedRecovery()
    {
        Assert.True(await RunStaAsync(async () =>
        {
            var application = new App(new RecordingUpdateRuntime(UpdateSnapshot(ApplicationUpdateStatus.Idle)));
            application.InitializeComponent();
            application.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
            try
            {
                await VerifyScenarioAsync("fresh", startFromOlderSchema: false);
                await VerifyScenarioAsync("older", startFromOlderSchema: true);
                return true;
            }
            finally
            {
                application.Shutdown();
            }
        }));

        async Task VerifyScenarioAsync(string name, bool startFromOlderSchema)
        {
            var hostStartCalls = 0;
            using var host = AppBootstrap.CreateHost(Path.Combine(root, name), services =>
            {
                services.RemoveAll<ILicenseAuthorizationGate>();
                services.AddSingleton<ILicenseAuthorizationGate>(new RejectingStartupLicenseGate());
                services.RemoveAll<IInterruptedOperationRecoveryStore>();
                services.AddSingleton<IInterruptedOperationRecoveryStore>(new ForbiddenStartupRecoveryStore());
                services.RemoveAll<IInterruptedShareOperationRecoveryStore>();
                services.AddSingleton<IInterruptedShareOperationRecoveryStore>(new ForbiddenStartupRecoveryStore());
                services.RemoveAll<IAllocationCounterStore>();
                services.AddSingleton<IAllocationCounterStore>(new ForbiddenStartupRecoveryStore());
                services.RemoveAll<IRemoteSelectionSessionStore>();
                services.AddSingleton<IRemoteSelectionSessionStore>(new ForbiddenStartupRecoveryStore());
                services.RemoveAll<ILocalResultExportCoordinator>();
                services.AddSingleton<ILocalResultExportCoordinator>(new ForbiddenStartupRecoveryStore());
            });
            if (startFromOlderSchema)
            {
                var migrator = host.Services.GetRequiredService<DatabaseMigrator>();
                var applyMigration = typeof(DatabaseMigrator).GetMethod(
                    "ApplyMigrationAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;
                for (var version = 1; version < DatabaseMigrator.LatestVersion; version++)
                    await (Task)applyMigration.Invoke(migrator, [version, CancellationToken.None])!;
            }

            var result = await host.Services.GetRequiredService<AppStartupController>().StartAsync(_ =>
            {
                hostStartCalls++;
                return Task.CompletedTask;
            });

            Assert.False(result.LicenseAuthorized);
            Assert.True(result.LocalUiMayOpen);
            Assert.Equal(1, hostStartCalls);
            var shell = await host.Services.GetRequiredService<IAppShellFactory>().CreateAsync();
            try { Assert.NotNull(shell.DataContext); }
            finally { shell.Close(); }
            Assert.True((await host.Services.GetRequiredService<DatabaseIntegrityService>().CheckAsync()).IsHealthy);
        }
    }

    [Fact]
    public void ShellCompositionPassesTheSharedGateToEveryRemoteWorkViewModel()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "BaiduBatchTransfer.App", "AppBootstrap.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("ILicenseAuthorizationGate licenseGate", source, StringComparison.Ordinal);
        Assert.Contains("textClipboard, licenseGate", source, StringComparison.Ordinal);
        Assert.Contains("batchHistorySource,\n            licenseGate: licenseGate", source, StringComparison.Ordinal);
        Assert.Contains("dialogService,\n            licenseGate", source, StringComparison.Ordinal);
        Assert.Contains("resultSaveDialog,\n            licenseGate: licenseGate", source, StringComparison.Ordinal);
        Assert.Contains("localResultExportStore: localResultExportStore,\n            licenseGate: licenseGate", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TransferTargetDirectoryPickerServicesAreRegistered()
    {
        using var host = AppBootstrap.CreateHost(root);

        Assert.IsType<BaiduTransferTargetDirectorySource>(
            host.Services.GetRequiredService<ITransferTargetDirectorySource>());
        Assert.IsType<BaiduTransferTargetDirectoryCreator>(
            host.Services.GetRequiredService<ITransferTargetDirectoryCreator>());
        Assert.IsType<WpfTransferTargetDirectoryPicker>(
            host.Services.GetRequiredService<ITransferTargetDirectoryPicker>());
    }

    [Fact]
    public void TransferCompletionDialogUsesRegisteredDialogDependency()
    {
        using var host = AppBootstrap.CreateHost(root);
        var dialog = host.Services.GetRequiredService<IDialogService>();
        var shell = host.Services.GetRequiredService<IAppShellFactory>();
        var create = shell.GetType().GetMethod(nameof(IAppShellFactory.CreateAsync))!;
        var stateMachine = create.GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType;
        var moveNext = stateMachine.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var instructions = ReadCompositionInstructions(moveNext);
        var constructorIndex = Assert.Single(Enumerable.Range(0, instructions.Count), index =>
            instructions[index].Code == OpCodes.Newobj &&
            instructions[index].Member is ConstructorInfo { DeclaringType: { } type } &&
            type == typeof(TransferWorkspaceViewModel));
        var constructor = (ConstructorInfo)instructions[constructorIndex].Member!;
        Assert.Equal(typeof(IDialogService), constructor.GetParameters().Single(parameter => parameter.Name == "dialogService").ParameterType);
        Assert.Equal(typeof(ILicenseAuthorizationGate), constructor.GetParameters().Single(parameter => parameter.Name == "licenseGate").ParameterType);
        Assert.Equal(typeof(LocalTerminalHistoryService), constructor.GetParameters()[^2].ParameterType);
        Assert.Equal(typeof(ILocalHistoryCleanupInteraction), constructor.GetParameters()[^1].ParameterType);

        // Verify the actual compiled composition call's final argument. Omitting the optional
        // dialog argument emits ldnull here, even though the factory still holds a dialog field.
        var dialogArgument = instructions.Take(constructorIndex).Last(instruction =>
            instruction.Code == OpCodes.Ldfld &&
            instruction.Member is FieldInfo { FieldType: { } fieldType } &&
            fieldType == typeof(IDialogService));
        Assert.Equal(OpCodes.Ldfld, dialogArgument.Code);
        var dialogField = Assert.IsAssignableFrom<FieldInfo>(dialogArgument.Member);
        Assert.Equal(shell.GetType(), dialogField.DeclaringType);
        Assert.Equal(typeof(IDialogService), dialogField.FieldType);
        Assert.Same(dialog, dialogField.GetValue(shell));
    }

    private static IReadOnlyList<(OpCode Code, MemberInfo? Member)> ReadCompositionInstructions(MethodInfo method)
    {
        var opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode))
            .Select(field => (OpCode)field.GetValue(null)!)
            .ToDictionary(code => unchecked((ushort)code.Value));
        var bytes = method.GetMethodBody()!.GetILAsByteArray()!;
        var instructions = new List<(OpCode, MemberInfo?)>();
        for (var offset = 0; offset < bytes.Length;)
        {
            var first = bytes[offset++];
            var code = opcodes[first == 0xfe ? (ushort)(0xfe00 | bytes[offset++]) : first];
            MemberInfo? member = code.OperandType switch
            {
                OperandType.InlineField => method.Module.ResolveField(BitConverter.ToInt32(bytes, offset)),
                OperandType.InlineMethod => method.Module.ResolveMethod(BitConverter.ToInt32(bytes, offset)),
                _ => null,
            };
            instructions.Add((code, member));
            offset += code.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(bytes, offset),
                _ => 4,
            };
        }
        return instructions;
    }

    [Theory]
    [InlineData("Succeeded", 1L, 0L)]
    [InlineData("Failed", 0L, 1L)]
    public async Task LocalResultsSummaryMapsTerminalStatistics(string transferStatus, long successes, long problems)
    {
        using var host = AppBootstrap.CreateHost(root);
        await host.Services.GetRequiredService<DatabaseMigrator>().MigrateAsync();
        var factory = host.Services.GetRequiredService<SqliteConnectionFactory>();
        var batch = Guid.NewGuid();
        var account = Guid.NewGuid();
        var operation = Guid.NewGuid();
        await SeedTransferOwnerAsync(factory, operation, batch, account, 1, "terminal-summary", DateTimeOffset.UtcNow);
        await using (var connection = await factory.OpenConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO BatchOutputAccounts(BatchJobId,AccountId,OutputOrdinal,OutputLabel,PlannedTransferCount,PlannedShareCount,ShareEnabled)
                VALUES($batch,$account,1,'账号',1,0,0);
                UPDATE TransferOperations SET Status=$status WHERE Id=$operation;
                """;
            command.Parameters.AddWithValue("$batch", batch.ToString("D"));
            command.Parameters.AddWithValue("$account", account.ToString("D"));
            command.Parameters.AddWithValue("$operation", operation.ToString("D"));
            command.Parameters.AddWithValue("$status", transferStatus);
            await command.ExecuteNonQueryAsync();
        }
        var summary = await host.Services.GetRequiredService<IJobResultsWorkflow>().LoadSummaryAsync(batch);
        Assert.Equal(successes, summary.CompleteSuccessCount);
        Assert.Equal(problems, summary.TerminalProblemCount);
        Assert.Equal(problems, summary.FailedCount);
    }

    [Fact]
    public void LegacyCreateHostUsesUnavailableUpdateRuntime()
    {
        using var host = AppBootstrap.CreateHost(root);

        Assert.IsType<UnavailableApplicationUpdateRuntime>(
            host.Services.GetRequiredService<IApplicationUpdateRuntime>());
        Assert.IsType<ApplicationUpdateService>(
            host.Services.GetRequiredService<IApplicationUpdateService>());
    }

    [Fact]
    public void SuppliedUpdateRuntimeIsSharedButNeverOwnedByTheHost()
    {
        var runtime = new RecordingUpdateRuntime(UpdateSnapshot(ApplicationUpdateStatus.Idle));
        var host = AppBootstrap.CreateHost(root, runtime);

        Assert.Same(runtime, host.Services.GetRequiredService<IApplicationUpdateRuntime>());
        Assert.Same(
            host.Services.GetRequiredService<ApplicationUpdateService>(),
            host.Services.GetRequiredService<IApplicationUpdateService>());
        Assert.Equal(1, runtime.SubscriptionCount);

        host.Dispose();

        Assert.Equal(0, runtime.SubscriptionCount);
        Assert.Equal(0, runtime.DisposeCalls);
    }

    [Fact]
    public async Task StartupUpdateCheckReturnsImmediatelyRunsOnlyOnceAndReportsAvailable()
    {
        var result = new TaskCompletionSource<ApplicationUpdateSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new RecordingUpdateService
        {
            CheckHandler = _ => result.Task
        };
        var reported = new List<string>();
        var coordinator = new StartupUpdateCheckCoordinator(
            service,
            version =>
            {
                reported.Add(version);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        coordinator.Start();
        coordinator.Start();

        await service.CheckStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, service.CheckCalls);
        Assert.False(coordinator.Completion.IsCompleted);
        Assert.False(service.LastCheckWasManual);

        result.SetResult(UpdateSnapshot(ApplicationUpdateStatus.Available, "2.0.0"));
        await coordinator.Completion;

        Assert.Equal(["2.0.0"], reported);
    }

    [Fact]
    public async Task StartupUpdateCheckObservesFailuresWithoutEscaping()
    {
        var service = new RecordingUpdateService
        {
            CheckHandler = _ => Task.FromException<ApplicationUpdateSnapshot>(
                new InvalidOperationException("sensitive failure"))
        };
        var coordinator = new StartupUpdateCheckCoordinator(
            service,
            _ => throw new InvalidOperationException("must not report"),
            CancellationToken.None);

        coordinator.Start();

        await coordinator.Completion;
        Assert.Equal(1, service.CheckCalls);
    }

    [Theory]
    [InlineData(ApplicationUpdateStatus.UpToDate)]
    [InlineData(ApplicationUpdateStatus.NetworkUnavailable)]
    [InlineData(ApplicationUpdateStatus.NotConfigured)]
    [InlineData(ApplicationUpdateStatus.Unsupported)]
    public async Task StartupUpdateCheckSilentlyIgnoresNonAvailableOutcomes(
        ApplicationUpdateStatus status)
    {
        var service = new RecordingUpdateService
        {
            CheckHandler = _ => Task.FromResult(UpdateSnapshot(status))
        };
        var reports = 0;
        var coordinator = new StartupUpdateCheckCoordinator(
            service,
            _ =>
            {
                reports++;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        coordinator.Start();
        await coordinator.Completion;

        Assert.Equal(0, reports);
    }

    [Fact]
    public void BootstrapUsesProductVersionForMigrationAndAdapterVersionForBrowserCapabilities()
    {
        using var host = AppBootstrap.CreateHost(root);

        var migratorVersion = ReadPrivateString(
            host.Services.GetRequiredService<DatabaseMigrator>(),
            "appVersion");
        var adapterVersion = ReadPrivateString(
            host.Services.GetRequiredService<WebViewBaiduNetdiskAdapter>(),
            "applicationBuildVersion");
        var shareAdapterVersion = ReadPrivateString(
            host.Services.GetRequiredService<WebViewBaiduShareAdapter>(),
            "applicationBuildVersion");
        var picker = Assert.IsType<WebViewRemoteResourcePicker>(
            host.Services.GetRequiredService<IRemoteResourcePicker>());
        var pickerNavigation = InvokePrivate<BaiduNavigationAuthorizationRequest>(
            picker,
            "Navigation");
        var runtimeCapability = ReadPrivate<AdapterCapabilityVersion>(
            host.Services.GetRequiredService<ManagedChromiumTransferRuntimeFactory>(),
            "capability");

        Assert.Equal(ApplicationBuildInfo.Current.ProductVersion, migratorVersion);
        Assert.Equal(ApplicationBuildInfo.Current.AdapterBuildVersion, adapterVersion);
        Assert.Equal(ApplicationBuildInfo.Current.AdapterBuildVersion, shareAdapterVersion);
        Assert.Equal(ApplicationBuildInfo.Current.AdapterBuildVersion, pickerNavigation.ApplicationBuildVersion);
        Assert.Equal(ApplicationBuildInfo.Current.AdapterBuildVersion, pickerNavigation.Acceptance.ApplicationBuildVersion);
        Assert.Equal(ApplicationBuildInfo.Current.AdapterBuildVersion, runtimeCapability.BuildVersion);
        Assert.NotEqual(migratorVersion, adapterVersion);
    }

    [Fact]
    public void TransferManagedCompositionResolvesWithoutStartingAHelper()
    {
        var helperStarts = 0;
        using var host = AppBootstrap.CreateHost(root, services =>
        {
            services.RemoveAll<ManagedBrowserHelperClientFactory>();
            services.AddSingleton(_ => new ManagedBrowserHelperClientFactory(_ =>
            {
                Interlocked.Increment(ref helperStarts);
                throw new InvalidOperationException("Resolving transfer services must not start a helper.");
            }));
        });

        Assert.Same(
            host.Services.GetRequiredService<SqliteManagedBrowserStateStore>(),
            host.Services.GetRequiredService<IBaiduExecutionBindingStore>());
        Assert.IsType<BaiduExecutionContextResolver>(
            host.Services.GetRequiredService<IBaiduExecutionContextResolver>());
        Assert.NotNull(host.Services.GetRequiredService<ManagedBrowserHelperClientFactory>());
        Assert.NotNull(host.Services.GetRequiredService<ManagedTransferQuarantineRegistry>());
        Assert.NotNull(host.Services.GetRequiredService<ManagedTransferShareHandoffRegistry>());
        Assert.NotNull(host.Services.GetRequiredService<ManagedChromiumTransferRuntimeFactory>());
        Assert.IsType<ManagedChromiumTransferAdapter>(
            host.Services.GetRequiredService<IManagedChromiumTransferAdapter>());
        Assert.NotNull(host.Services.GetRequiredService<ExecutionBoundTransferAdapter>());
        Assert.Same(
            host.Services.GetRequiredService<SqliteTransferSchedulingStore>(),
            host.Services.GetRequiredService<IBatchSchedulingPolicySource>());
        Assert.Equal(0, helperStarts);
    }

    [Fact]
    public void ShareManagedCompositionResolvesWithoutStartingAHelper()
    {
        var helperStarts = 0;
        using var host = AppBootstrap.CreateHost(root, services =>
        {
            services.RemoveAll<ManagedBrowserHelperClientFactory>();
            services.AddSingleton(_ => new ManagedBrowserHelperClientFactory(_ =>
            {
                Interlocked.Increment(ref helperStarts);
                throw new InvalidOperationException("Resolving share services must not start a helper.");
            }));
        });

        Assert.IsType<ManagedShareAutomationSessionFactory>(
            host.Services.GetRequiredService<IManagedShareAutomationSessionFactory>());
        Assert.IsType<ManagedChromiumShareAdapter>(
            host.Services.GetRequiredService<IManagedChromiumShareAdapter>());
        Assert.NotNull(host.Services.GetRequiredService<ExecutionBoundShareAdapter>());
        Assert.Equal(0, helperStarts);
    }

    [Fact]
    public void DirectBackgroundModeUsesCookieHttpTransferExecutorAndReconciler()
    {
        using var host = AppBootstrap.CreateHost(root);

        var live = host.Services.GetRequiredService<IBaiduNetdiskAdapter>();
        var executor = Assert.IsType<OperationExecutor>(
            host.Services.GetRequiredService<ITransferOperationExecutor>());
        var reconciler = Assert.IsType<OperationReconciliationService>(
            host.Services.GetRequiredService<ITransferResultReconciler>());

        Assert.Same(live, PrivateField<IBaiduNetdiskAdapter>(executor, "adapter"));
        Assert.Null(PrivateField<ExecutionBoundTransferAdapter>(executor, "boundAdapter"));
        Assert.Same(live, PrivateField<IBaiduNetdiskAdapter>(reconciler, "adapter"));
        Assert.Null(PrivateField<ExecutionBoundTransferAdapter>(reconciler, "boundAdapter"));
    }

    [Fact]
    public void DirectBackgroundModeUsesCookieHttpShareExecutorAndReconciler()
    {
        using var host = AppBootstrap.CreateHost(root);

        var live = host.Services.GetRequiredService<IBaiduShareAdapter>();
        var executor = Assert.IsType<ShareOperationExecutor>(
            host.Services.GetRequiredService<IShareOperationExecutor>());
        var reconciler = Assert.IsType<ShareOperationReconciliationService>(
            host.Services.GetRequiredService<IShareResultReconciler>());

        Assert.Same(live, PrivateField<IBaiduShareAdapter>(executor, "adapter"));
        Assert.Null(PrivateField<ExecutionBoundShareAdapter>(executor, "boundAdapter"));
        Assert.Same(live, PrivateField<IBaiduShareAdapter>(reconciler, "adapter"));
        Assert.Null(PrivateField<ExecutionBoundShareAdapter>(reconciler, "boundAdapter"));
    }

#if DEBUG
    [Fact]
    public void AcceptanceOnlyModeUsesBoundTransferExecutorAndReconciler()
    {
        using var host = AppBootstrap.CreateHost(root, [AcceptanceWriteLeaseOptions.Switch]);
        var bound = host.Services.GetRequiredService<ExecutionBoundTransferAdapter>();
        var executor = Assert.IsType<OperationExecutor>(
            host.Services.GetRequiredService<ITransferOperationExecutor>());
        var reconciler = Assert.IsType<OperationReconciliationService>(
            host.Services.GetRequiredService<ITransferResultReconciler>());

        Assert.Same(bound, PrivateField<ExecutionBoundTransferAdapter>(executor, "boundAdapter"));
        Assert.Null(PrivateField<IBaiduNetdiskAdapter>(executor, "adapter"));
        Assert.Same(bound, PrivateField<ExecutionBoundTransferAdapter>(reconciler, "boundAdapter"));
        Assert.Null(PrivateField<IBaiduNetdiskAdapter>(reconciler, "adapter"));
    }

    [Fact]
    public void AcceptanceOnlyModeUsesBoundShareExecutorAndReconciler()
    {
        using var host = AppBootstrap.CreateHost(root, [AcceptanceWriteLeaseOptions.Switch]);
        var bound = host.Services.GetRequiredService<ExecutionBoundShareAdapter>();
        var executor = Assert.IsType<ShareOperationExecutor>(
            host.Services.GetRequiredService<IShareOperationExecutor>());
        var reconciler = Assert.IsType<ShareOperationReconciliationService>(
            host.Services.GetRequiredService<IShareResultReconciler>());

        Assert.Same(bound, PrivateField<ExecutionBoundShareAdapter>(executor, "boundAdapter"));
        Assert.Null(PrivateField<IBaiduShareAdapter>(executor, "adapter"));
        Assert.Same(bound, PrivateField<ExecutionBoundShareAdapter>(reconciler, "boundAdapter"));
        Assert.Null(PrivateField<IBaiduShareAdapter>(reconciler, "adapter"));
    }
#endif

    private static T? PrivateField<T>(object instance, string name) where T : class =>
        (T?)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(instance);

    [Fact]
    public void ManagedBrowserProfilePolicyAndBrowserSelectionProcessDependenciesAreSharedSingletons()
    {
        using var host = AppBootstrap.CreateHost(root);

        var concretePolicy = host.Services.GetRequiredService<FileSystemManagedBrowserProfilePolicy>();
        var policy = host.Services.GetRequiredService<IManagedBrowserProfilePolicy>();
        var firstContext = host.Services.GetRequiredService<BrowserSelectionProcessContext>();
        var secondContext = host.Services.GetRequiredService<BrowserSelectionProcessContext>();
        var concreteRegistry = host.Services.GetRequiredService<ProcessBrowserSelectionActivityRegistry>();
        var registry = host.Services.GetRequiredService<IBrowserSelectionActivityRegistry>();

        Assert.Same(concretePolicy, policy);
        Assert.Same(firstContext, secondContext);
        Assert.Same(concreteRegistry, registry);
    }

    [Fact]
    public void HostCompositionRegistersUiServicesButNeverAFakeBaiduAdapter()
    {
        using var host = AppBootstrap.CreateHost(root);

        var transferAdapter = Assert.IsType<LiveBaiduNetdiskAdapter>(
            host.Services.GetRequiredService<IBaiduNetdiskAdapter>());
        var shareAdapter = Assert.IsType<LiveBaiduShareAdapter>(
            host.Services.GetRequiredService<IBaiduShareAdapter>());
        Assert.NotNull(transferAdapter);
        Assert.NotNull(shareAdapter);
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
        Assert.IsType<SqliteAccountCapacityStore>(host.Services.GetRequiredService<IAccountCapacityStore>());
        Assert.NotNull(host.Services.GetRequiredService<AccountCapacityRefreshService>());
        Assert.NotNull(host.Services.GetRequiredService<BaiduAdapterConcurrencyGate>());
        var capability = host.Services.GetRequiredService<AdapterCapability>();
        Assert.False(capability.TransferValidated);
        Assert.False(capability.ShareValidated);
        Assert.True(host.Services.GetRequiredService<IAdapterAvailability>().CanStartTransfers);
        var cookieLogin = Assert.IsType<DirectBackgroundAccountLoginLauncher>(
            host.Services.GetRequiredService<IAccountLoginLauncher>());
        Assert.Same(cookieLogin, host.Services.GetRequiredService<IAccountReloginLauncher>());
        Assert.IsType<ManagedBrowserLoginWindowPresenter>(host.Services.GetRequiredService<IManagedBrowserLoginWindowPresenter>());
        Assert.Same(host.Services.GetRequiredService<ManagedBrowserLoginLauncher>(), host.Services.GetRequiredService<IManagedBrowserLoginOperations>());
        Assert.Same(host.Services.GetRequiredService<IDialogService>(), host.Services.GetRequiredService<IBrowserDirectoryPicker>());
        Assert.IsType<SqliteBrowserDirectoryState>(host.Services.GetRequiredService<IBrowserDirectoryState>());
        Assert.NotNull(host.Services.GetRequiredService<BrowserSetupService>());
        Assert.NotNull(host.Services.GetRequiredService<RetiredBrowserFamilySlotCoordinator>());
        Assert.IsType<BaiduLoginWindowPresenter>(host.Services.GetRequiredService<IBaiduLoginWindowPresenter>());
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
        Assert.IsType<ShareAnalyticsWorkbookExportService>(
            host.Services.GetRequiredService<IShareAnalyticsWorkbookExportService>());
        Assert.IsType<DirectBackgroundJobResultCapabilityAvailability>(
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
        var shellConstructorParameters = Assert.Single(typeof(AppShellFactory).GetConstructors()).GetParameters();
        Assert.Contains(shellConstructorParameters, parameter =>
            parameter.ParameterType == typeof(IStandaloneShareResultProjectionRepository));
        Assert.Contains(shellConstructorParameters, parameter =>
            parameter.ParameterType == typeof(ILocalResultExportStore));
        Assert.Contains(shellConstructorParameters, parameter =>
            parameter.ParameterType == typeof(IShareAnalyticsWorkbookExportService));
        Assert.Contains(shellConstructorParameters, parameter =>
            parameter.ParameterType == typeof(IResultSaveDialog));
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
    public async Task StartingAnOrdinaryHostLeavesQueuedTransferWorkUntouched()
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
        await Task.Delay(150);
        await host.StopAsync(TimeSpan.FromSeconds(2));

        Assert.Null(executor.OperationId);
        Assert.Equal(
            "Waiting",
            await ReadOperationStatusAsync(
                host.Services.GetRequiredService<SqliteConnectionFactory>(), operationId));
    }

#if DEBUG
    [Fact]
    public async Task StartingAnExplicitAcceptanceHostDoesNotRunTheGlobalTransferQueue()
    {
        var executor = new RecordingTransferExecutor();
        using var host = AppBootstrap.CreateHost(root, [AcceptanceWriteLeaseOptions.Switch], services =>
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
            baiduUserId: "acceptance-hosted-loop-user",
            DateTimeOffset.UtcNow.AddMinutes(-1));

        await host.StartAsync();
        await Task.Delay(150);
        await host.StopAsync(TimeSpan.FromSeconds(2));

        Assert.Null(executor.OperationId);
        Assert.Equal(RemoteWriteExecutionMode.AcceptanceOnly,
            host.Services.GetRequiredService<RemoteWriteExecutionMode>());
        Assert.Equal("Waiting", await ReadOperationStatusAsync(
            host.Services.GetRequiredService<SqliteConnectionFactory>(), operationId));
    }
#endif

    [Fact]
    public void ExplicitAcceptanceSwitchUsesTheCompileTimeDebugGate()
    {
        using var host = AppBootstrap.CreateHost(
            root,
            [AcceptanceWriteLeaseOptions.Switch]);

        var options = host.Services.GetRequiredService<AcceptanceWriteLeaseOptions>();
        var mode = host.Services.GetRequiredService<RemoteWriteExecutionMode>();

#if DEBUG
        Assert.True(options.IsEnabled);
        Assert.Equal(RemoteWriteExecutionMode.AcceptanceOnly, mode);
#else
        Assert.False(options.IsEnabled);
        Assert.Equal(RemoteWriteExecutionMode.DirectBackground, mode);
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

        Assert.True(availability.CanStartTransfers);
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

        Assert.False(ordinaryHost.Services.GetRequiredService<IAdapterAvailability>()
            .RequiresBulkPrecheckBeforeStart);
#if DEBUG
        Assert.False(acceptanceHost.Services.GetRequiredService<IAdapterAvailability>()
            .RequiresBulkPrecheckBeforeStart);
#else
        Assert.False(acceptanceHost.Services.GetRequiredService<IAdapterAvailability>()
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

        Assert.True(host.Services.GetRequiredService<IAdapterAvailability>().CanStartTransfers);
    }

    [Fact]
    public void OrdinaryHostUsesDirectWebApiAdaptersWithoutTheBrowserWriteGate()
    {
        var gate = new RecordingWriteAuthorizationGate();
        using var host = AppBootstrap.CreateHost(root, services =>
        {
            services.RemoveAll<IAdapterWriteAuthorizationGate>();
            services.AddSingleton<IAdapterWriteAuthorizationGate>(gate);
        });
        Assert.IsType<LiveBaiduNetdiskAdapter>(host.Services.GetRequiredService<IBaiduNetdiskAdapter>());
        Assert.IsType<LiveBaiduShareAdapter>(host.Services.GetRequiredService<IBaiduShareAdapter>());
        Assert.Empty(gate.Requests);
    }

    [Fact]
    public async Task DisabledProductionModeIgnoresEvenAnExactFormalRecord()
    {
        using var host = AppBootstrap.CreateHost(root);
        await host.Services.GetRequiredService<DatabaseMigrator>().MigrateAsync();
        var store = host.Services.GetRequiredService<IAdapterCapabilityStore>();
        var gate = host.Services.GetRequiredService<IAdapterWriteAuthorizationGate>();
        var build = ApplicationBuildInfo.Current.AdapterBuildVersion;
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

        Assert.Null(await gate.TryCreateBindingAsync(request));
    }

    [Fact]
    public async Task DisabledProductionModeDoesNotAuthorizeStoreBackedRecoveryAfterRestart()
    {
        var operationId = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        const int generation = 2;
        const string baiduUserId = "stable-baidu-user";
        var build = ApplicationBuildInfo.Current.AdapterBuildVersion;
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
            var targetFingerprint = TransferTargetPreparationEvidence.ComputeObservationFingerprint(
                target.RemoteId, target.Path, 1, true, true, "target-generation",
                baiduUserId, TransferTargetInventoryScope.AllItems, baseline);
            var preparation = new TransferTargetPreparationEvidence(
                target.RemoteId, target.Path, 1, true, true, "target-generation",
                baiduUserId, TransferTargetInventoryScope.AllItems, baseline, targetFingerprint);
            Assert.True(await attempts.TryPersistTransferTargetPreparationAsync(
                operationId, accountId, generation, 1, preparation, now.AddSeconds(2)));
            var submission = new TransferSubmissionEvidence(
                source.Items.Fingerprint, target.RemoteId, baseline.Fingerprint,
                baiduUserId, capability, now.AddSeconds(3));
            Assert.False(await attempts.TryPersistTransferSubmissionAsync(
                operationId, accountId, generation, 2, submission, now.AddSeconds(3)));
            Assert.False(await attempts.TryAdvanceAsync(
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

        Assert.Null(binding);
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
        var build = ApplicationBuildInfo.Current.AdapterBuildVersion;
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
    public async Task ProductionGateHonorsBuildConfigurationBeforePresentingOrReachingInner()
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

#if DEBUG
        Assert.True(prepared.IsSuccess);
        Assert.True(result.IsSuccess);
        Assert.Equal(1, presenter.PresentCount);
        Assert.Equal(context.OperationId, presenter.Seen!.OperationId);
        Assert.Equal(context.AccountId, presenter.Seen.AccountId);
        Assert.Equal(context.Generation, presenter.Seen.Generation);
        Assert.Equal(AdapterWriteAction.PrepareShare, presenter.Seen.Action);
        Assert.Equal(1, inner.PrepareCount);
        Assert.Equal(1, inner.SubmitCount);
#else
        Assert.False(prepared.IsSuccess);
        Assert.False(result.IsSuccess);
        Assert.Equal(BaiduErrorKind.NotValidated, prepared.ErrorKind);
        Assert.Equal(BaiduErrorKind.NotValidated, result.ErrorKind);
        Assert.Equal(SubmissionAcceptance.DefinitelyNotAccepted, prepared.SubmissionAcceptance);
        Assert.Equal(SubmissionAcceptance.DefinitelyNotAccepted, result.SubmissionAcceptance);
        Assert.Equal(0, presenter.PresentCount);
        Assert.Null(presenter.Seen);
        Assert.Equal(0, inner.PrepareCount);
        Assert.Equal(0, inner.SubmitCount);
#endif
        Assert.Equal(0, presenter.EnableCount);
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
    public void ShellCompositionPreservesShareRuntimeButHidesItFromMainNavigation()
    {
        var bootstrap = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "BaiduBatchTransfer.App",
            "AppBootstrap.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("var transferWorkspace = new TransferWorkspaceView(transferViewModel)", bootstrap);
        Assert.Contains("var shareWorkspace = new ShareWorkspaceView(shareViewModel)", bootstrap);
        Assert.Contains("var homeWorkspace = new DeferredWorkspaceView(", bootstrap);
        Assert.Contains("var shareAnalyticsViewModel = new ShareAnalyticsViewModel(", bootstrap);
        Assert.Contains("var shareDataWorkspace = new ShareAnalyticsView(shareAnalyticsViewModel)", bootstrap);
        Assert.Contains("var adInsertionWorkspace = new DeferredWorkspaceView(", bootstrap);
        Assert.Contains("var fileExclusionWorkspace = new DeferredWorkspaceView(", bootstrap);
        Assert.Contains("var statisticsWorkspace = new DeferredWorkspaceView(", bootstrap);
        Assert.Contains("var settingsViewModel = new SettingsViewModel(", bootstrap);
        Assert.Contains("var settingsWorkspace = new SettingsView(settingsViewModel);", bootstrap);
        Assert.DoesNotContain("new DeferredWorkspaceViewModel(\"设置中心\"", bootstrap);
        Assert.Contains("new System.Windows.Threading.DispatcherSynchronizationContext(", bootstrap);
        Assert.DoesNotContain("new DeferredWorkspaceViewModel(\"分享数据\"", bootstrap);
        Assert.Contains("mainViewModel = new MainWindowViewModel(", bootstrap);
        Assert.Contains(
            "mainViewModel = new MainWindowViewModel(\n" +
            "            homeWorkspace,\n" +
            "            shareDataWorkspace,\n" +
            "            transferWorkspace,\n" +
            "            adInsertionWorkspace,\n" +
            "            fileExclusionWorkspace,\n" +
            "            statisticsWorkspace,\n" +
            "            settingsWorkspace,",
            bootstrap,
            StringComparison.Ordinal);
        Assert.Contains("mainViewModel?.ShowBatchTransferCommand.Execute(null)", bootstrap);
        Assert.DoesNotContain("mainViewModel?.ShowShareCommand", bootstrap);
        Assert.DoesNotContain("shareWorkspace,\n            loginLauncher", bootstrap, StringComparison.Ordinal);
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
        Assert.Contains("settingsViewModel.Dispose();", bootstrap);
    }

    [Fact]
    public void ApplicationStartupPassesTheProcessOwnedUpdateRuntimeIntoTheHost()
    {
        var app = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "BaiduBatchTransfer.App",
            "App.xaml.cs"));

        Assert.Contains(
            "AppBootstrap.CreateHost(dataRoot, e.Args, updateRuntime)",
            app,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AcceptanceTitleIsConfiguredAfterShellConstruction()
    {
        var bootstrap = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "BaiduBatchTransfer.App",
            "AppBootstrap.cs"));

        Assert.Contains(
            "mainViewModel.ConfigureOtaAcceptanceBuild(buildInfo.IsOtaAcceptanceBuild);",
            bootstrap,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "isOtaAcceptanceBuild: buildInfo.IsOtaAcceptanceBuild",
            bootstrap,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadedInitializationWaitsForEveryPrerequisiteBeforeStartingTheSilentCheck()
    {
        var activation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();

        var initialization = LoadedInitializationSequencer.RunAsync(
            () =>
            {
                order.Add("run-panel");
                return Task.CompletedTask;
            },
            () =>
            {
                order.Add("share");
                return Task.CompletedTask;
            },
            () =>
            {
                order.Add("activate");
                return activation.Task;
            },
            () =>
            {
                order.Add("handoff");
                return Task.CompletedTask;
            },
            () =>
            {
                order.Add("poll");
                return Task.CompletedTask;
            },
            () => order.Add("update-check"));

        Assert.Equal(["run-panel", "share", "activate"], order);
        Assert.False(initialization.IsCompleted);

        activation.SetResult();
        await initialization;

        Assert.Equal(
            ["run-panel", "share", "activate", "handoff", "poll", "update-check"],
            order);
    }

    [Fact]
    public async Task LoadedInitializationDoesNotStartTheSilentCheckAfterShellCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var updateCheckStarted = false;

        var initialization = LoadedInitializationSequencer.RunAsync(
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            () =>
            {
                cancellation.Cancel();
                return Task.CompletedTask;
            },
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            () => updateCheckStarted = true,
            cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialization);
        Assert.False(updateCheckStarted);
    }

    [Fact]
    public void CloseRequesterOnlyClosesTheMainWindow()
    {
        var bootstrap = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "BaiduBatchTransfer.App",
            "AppBootstrap.cs"));

        Assert.Contains("mainWindow?.Close()", bootstrap, StringComparison.Ordinal);
        Assert.DoesNotContain("Application.Current.Shutdown", bootstrap, StringComparison.Ordinal);
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
        Assert.True(host.Services.GetRequiredService<IAdapterAvailability>().CanStartTransfers);
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
        Assert.IsType<SqliteManagedBrowserStateStore>(
            host.Services.GetRequiredService<IManagedBrowserStateStore>());
        Assert.IsType<FileSystemManagedBrowserProfileStore>(
            host.Services.GetRequiredService<IManagedBrowserProfileStore>());
        Assert.IsType<ManagedBrowserLifecycleCoordinator>(
            host.Services.GetRequiredService<IManagedBrowserLifecycle>());
        Assert.Same(host.Services.GetRequiredService<IManagedBrowserLifecycle>(), host.Services.GetRequiredService<IManagedBrowserAccountLifecycle>());
        Assert.IsType<ManagedBrowserFamilySwitchReadinessFence>(host.Services.GetRequiredService<IManagedBrowserFamilySwitchFence>());
        Assert.NotNull(host.Services.GetRequiredService<ManagedBrowserFamilySwitchCoordinator>());
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
    public async Task LegacyAccountWithActiveManagedSessionUsesManagedExecutionProfile()
    {
        using var host = AppBootstrap.CreateHost(root, [AcceptanceWriteLeaseOptions.Switch]);
        await host.Services.GetRequiredService<DatabaseMigrator>().MigrateAsync();
        var accountId = Guid.NewGuid();
        const string baiduUserId = "legacy-managed-user";
        await using (var unit = await host.Services.GetRequiredService<IUnitOfWorkFactory>().BeginAsync())
        {
            await unit.Accounts.UpsertAsync(AccountRecord.Create(accountId, baiduUserId, "旧账号"));
            await unit.CommitAsync();
        }

        var installation = new BrowserInstallationIdentity(
            Guid.NewGuid(),
            BrowserFamily.Chrome,
            @"C:\Browser",
            @"C:\Browser\chrome.exe",
            "Google LLC",
            "Google Chrome",
            Guid.NewGuid().ToString("N"));
        var session = new ManagedBrowserSessionReference(
            Guid.NewGuid(),
            installation.InstallationId,
            accountId,
            baiduUserId,
            installation.BrowserFamily,
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"));
        var binding = await host.Services.GetRequiredService<IManagedBrowserLoginStore>()
            .BindLoginAsync(false, installation, session, null, CancellationToken.None);
        Assert.Equal(ManagedBrowserBindingStatus.Bound, binding);

        var account = Assert.Single(await host.Services.GetRequiredService<IWizardAccountProvider>()
            .LoadEligibleAsync());
        Assert.Equal(accountId, account.AccountId);
        Assert.Equal(BaiduExecutionEngine.ManagedChromiumV1, account.ExecutionProfile!.Engine);
        Assert.Equal(session.SessionId, account.ExecutionProfile.SessionId);
    }

    [Fact]
    public async Task ManagedAccountsRemainVisibleButCannotHideLegacyAccountsOnLaterPages()
    {
        using var host = AppBootstrap.CreateHost(root);
        await host.Services.GetRequiredService<DatabaseMigrator>().MigrateAsync();
        var legacyId = Guid.NewGuid();
        await using (var unit = await host.Services.GetRequiredService<IUnitOfWorkFactory>().BeginAsync())
        {
            for (var index = 0; index < 1_000; index++)
                await unit.Accounts.UpsertAsync(AccountRecord.Create(
                    Guid.NewGuid(), $"managed-{index}", $"新浏览器账号 {index}") with
                {
                    LoginMethod = "ManagedChromium",
                    SortOrder = index,
                });
            await unit.Accounts.UpsertAsync(AccountRecord.Create(
                legacyId, "legacy-after-managed", "旧浏览器账号") with { SortOrder = 1_000 });
            await unit.CommitAsync();
        }

        var snapshot = await host.Services.GetRequiredService<IWizardAccountProvider>().LoadEligibleAsync();
        var visibleCount = await host.Services.GetRequiredService<IAccountSummaryProvider>()
            .GetActiveAccountCountAsync();

        Assert.Equal(legacyId, Assert.Single(snapshot).AccountId);
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

    private static string ReadPrivateString(object instance, string fieldName)
        => ReadPrivate<string>(instance, fieldName);

    private static T ReadPrivate<T>(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<T>(field.GetValue(instance));
    }

    private static T InvokePrivate<T>(object instance, string methodName)
    {
        var method = instance.GetType().GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsType<T>(method.Invoke(instance, null));
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
              OperationFingerprint,SubmitState,TargetSlotState,CreatedAt,UpdatedAt,
              ExecutionEngineVersion,BrowserFamily,ProfileId,InstallationId,SessionId)
            VALUES($operation,$resource,$batch,1,1,'/target','/target','',$account,'Automatic','Waiting',$generation,
              'transfer-fingerprint','NotPrepared','Unoccupied',$now,$now,
              'WebView2-v1','LegacyWebView2','legacy-webview2-account-' || lower(replace($account,'-','')),NULL,NULL);
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

    private static async Task<string?> ReadOperationStatusAsync(
        SqliteConnectionFactory factory,
        Guid operationId)
    {
        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Status FROM TransferOperations WHERE Id=$id;";
        command.Parameters.AddWithValue("$id", operationId.ToString("D"));
        return await command.ExecuteScalarAsync() as string;
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

    private static ApplicationUpdateSnapshot UpdateSnapshot(
        ApplicationUpdateStatus status,
        string? latestVersion = null) =>
        new(status, "1.0.0", latestVersion, null, 0, status.ToString());

    private sealed class RecordingUpdateRuntime(ApplicationUpdateSnapshot snapshot)
        : IApplicationUpdateRuntime
    {
        private EventHandler<ApplicationUpdateSnapshot>? snapshotChanged;

        public ApplicationUpdateSnapshot Snapshot { get; } = snapshot;
        public int DisposeCalls { get; private set; }
        public int SubscriptionCount { get; private set; }

        public event EventHandler<ApplicationUpdateSnapshot>? SnapshotChanged
        {
            add
            {
                snapshotChanged += value;
                SubscriptionCount++;
            }
            remove
            {
                snapshotChanged -= value;
                SubscriptionCount--;
            }
        }

        public Task<ApplicationUpdateSnapshot> CheckAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Snapshot);

        public Task<ApplicationUpdateSnapshot> DownloadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Snapshot);

        public bool TryPrepareRestart() => false;
        public bool LaunchPreparedUpdateAndRestart() => false;
        public void Dispose() => DisposeCalls++;
    }

    private sealed class RecordingUpdateService : IApplicationUpdateService
    {
        public ApplicationUpdateSnapshot Snapshot { get; } =
            UpdateSnapshot(ApplicationUpdateStatus.Idle);
        public Func<CancellationToken, Task<ApplicationUpdateSnapshot>>? CheckHandler { get; init; }
        public TaskCompletionSource CheckStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public int CheckCalls { get; private set; }
        public bool LastCheckWasManual { get; private set; }

        public event EventHandler<ApplicationUpdateSnapshot>? SnapshotChanged
        {
            add { }
            remove { }
        }

        public Task<ApplicationUpdateSnapshot> CheckAsync(
            bool manual,
            CancellationToken cancellationToken)
        {
            CheckCalls++;
            LastCheckWasManual = manual;
            CheckStarted.TrySetResult();
            return CheckHandler?.Invoke(cancellationToken) ?? Task.FromResult(Snapshot);
        }

        public Task<ApplicationUpdateSnapshot> DownloadAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public bool TryPrepareRestart() => false;
    }

    private sealed class RejectingStartupLicenseGate : ILicenseAuthorizationGate
    {
        public Task<LicenseGateResult> AuthorizeAsync(
            LicenseOperationKind operation,
            CancellationToken cancellationToken) => Task.FromResult(new LicenseGateResult(
                operation,
                IsAuthorized: false,
                UsedCachedLease: false,
                Code: "activation_required"));
    }

    private static Task<T> RunStaAsync<T>(Func<Task<T>> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(
                new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(() => _ = ExecuteAsync()));
            System.Windows.Threading.Dispatcher.Run();

            async Task ExecuteAsync()
            {
                try { completion.TrySetResult(await action()); }
                catch (Exception error) { completion.TrySetException(error); }
                finally
                {
                    if (!dispatcher.HasShutdownStarted)
                        dispatcher.InvokeShutdown();
                }
            }
        })
        {
            IsBackground = true,
            Name = "AppBootstrapTests STA shell",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private sealed class ForbiddenStartupRecoveryStore :
        IInterruptedOperationRecoveryStore,
        IInterruptedShareOperationRecoveryStore,
        IAllocationCounterStore,
        IRemoteSelectionSessionStore,
        ILocalResultExportCoordinator
    {
        private static Exception Forbidden() =>
            new InvalidOperationException("Authorized startup recovery must not run after license rejection.");

        public Task<LeaseRecoveryResult> RecoverInterruptedOperationsAsync(DateTimeOffset recoveredAt, CancellationToken cancellationToken = default) =>
            Task.FromException<LeaseRecoveryResult>(Forbidden());

        public Task<ShareLeaseRecoveryResult> RecoverInterruptedShareOperationsAsync(DateTimeOffset recoveredAt, CancellationToken cancellationToken = default) =>
            Task.FromException<ShareLeaseRecoveryResult>(Forbidden());

        public Task<ShareLeaseRecoveryResult> RecoverExpiredShareOperationsAsync(DateTimeOffset recoveredAt, CancellationToken cancellationToken = default) =>
            Task.FromException<ShareLeaseRecoveryResult>(Forbidden());

        public Task<AllocationCounterRebuildResult> RebuildAllocationCountersAsync(CancellationToken cancellationToken = default) =>
            Task.FromException<AllocationCounterRebuildResult>(Forbidden());

        public Task<RemoteSelectionSession> CreateAsync(Guid accountId, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            Task.FromException<RemoteSelectionSession>(Forbidden());

        public Task<RemoteSelectionSession?> GetAsync(Guid sessionId, Guid workspaceDraftId, Guid accountId, int generation, CancellationToken cancellationToken = default) =>
            Task.FromException<RemoteSelectionSession?>(Forbidden());

        public Task<bool> TryCompleteAsync(Guid sessionId, Guid workspaceDraftId, Guid accountId, int generation, long expectedRevision, RemoteSelectionSessionState terminalState, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            Task.FromException<bool>(Forbidden());

        public Task<int> RecoverIncompleteAsync(DateTimeOffset now, CancellationToken cancellationToken = default) =>
            Task.FromException<int>(Forbidden());

        public Task<LocalResultExportOutcome> ExportStandaloneAsync(Guid batchId, CancellationToken cancellationToken = default) =>
            Task.FromException<LocalResultExportOutcome>(Forbidden());

        public Task<LocalResultExportOutcome> ExportTransferAsync(Guid batchId, CancellationToken cancellationToken = default) =>
            Task.FromException<LocalResultExportOutcome>(Forbidden());

        public Task<int> RecoverAsync(CancellationToken cancellationToken = default) =>
            Task.FromException<int>(Forbidden());
    }
}
