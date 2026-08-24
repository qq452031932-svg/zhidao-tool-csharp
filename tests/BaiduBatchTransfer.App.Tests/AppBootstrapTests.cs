using BaiduBatchTransfer.App;
using BaiduBatchTransfer.App.Features.Accounts;
using BaiduBatchTransfer.App.Features.Imports;
using BaiduBatchTransfer.App.Features.Jobs;
using BaiduBatchTransfer.App.Features.Transfers;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Application.Accounts;
using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Infrastructure.Database;
using BaiduBatchTransfer.Infrastructure.Excel;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.CompilerServices;

namespace BaiduBatchTransfer.App.Tests;

public sealed class AppBootstrapTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "BaiduBatchTransfer-AppBootstrapTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void HostCompositionRegistersUiServicesButNeverAFakeBaiduAdapter()
    {
        using var host = AppBootstrap.CreateHost(root);

        Assert.IsType<CoordinatedBaiduNetdiskAdapter>(
            host.Services.GetRequiredService<IBaiduNetdiskAdapter>());
        Assert.IsType<CoordinatedBaiduShareAdapter>(
            host.Services.GetRequiredService<IBaiduShareAdapter>());
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
        Assert.IsType<InMemoryTransferResultDestinationSink>(
            host.Services.GetRequiredService<ITransferResultDestinationSink>());
        Assert.IsType<ImportWorkspaceFactory>(host.Services.GetRequiredService<IImportWorkspaceFactory>());
        Assert.NotNull(host.Services.GetRequiredService<IAppShellFactory>());
        Assert.IsType<StartupRecoveryRunner>(host.Services.GetRequiredService<IAppRecoveryRunner>());
        Assert.NotNull(host.Services.GetRequiredService<ISingleInstanceGuard>());
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
        Assert.Contains("var shareWorkspace = new System.Windows.Controls.ContentControl", bootstrap);
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
}
