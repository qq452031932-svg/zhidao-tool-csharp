using System.Runtime.CompilerServices;
using BaiduBatchTransfer.App;
using BaiduBatchTransfer.App.Features.Common;
using BaiduBatchTransfer.App.Features.Imports;
using BaiduBatchTransfer.App.Features.Jobs;
using BaiduBatchTransfer.App.Features.Transfers;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class TransferWorkspaceViewModelTests
{
    [Theory]
    [InlineData(false, null, true)]
    [InlineData(true, null, false)]
    [InlineData(true, "C:\\results", true)]
    public void ResultDirectoryIsRequiredOnlyForAutomaticSharing(
        bool autoShare,
        string? resultDirectory,
        bool expected)
    {
        using var fixture = Fixture.Valid();

        fixture.ViewModel.AutoShare = autoShare;
        fixture.ViewModel.ShareResultDirectory = resultDirectory ?? string.Empty;

        Assert.Equal(expected, fixture.ViewModel.CanStart);
    }

    [Fact]
    public async Task TransferOnlyCreatesNoShareWorkAndNeedsNoLocalDirectory()
    {
        using var fixture = Fixture.Valid();
        fixture.ViewModel.AutoShare = false;

        await fixture.ViewModel.StartAsync();

        var draft = Assert.IsType<WizardBatchDraft>(fixture.BatchService.ConfirmedDraft);
        Assert.All(draft.Accounts.Where(account => account.IsSelected), account => Assert.False(account.ShareEnabled));
        Assert.All(fixture.Wizard.Accounts.Where(account => account.IsSelected), account =>
        {
            Assert.Equal(0, account.PlannedShareCount);
            Assert.False(account.ShareEnabled);
        });
        Assert.Equal(0, fixture.BatchService.ShareOperationCount);
        Assert.Empty(fixture.DestinationSink.Values);
    }

    [Fact]
    public async Task AutomaticSharingPassesCreatedBatchAndExactDirectoryToInterimSink()
    {
        using var fixture = Fixture.Valid();
        fixture.ViewModel.AutoShare = true;
        fixture.ViewModel.ShareResultDirectory = "C:\\private\\results";

        await fixture.ViewModel.StartAsync();

        var stored = Assert.Single(fixture.DestinationSink.Values);
        Assert.Equal(fixture.BatchService.ConfirmedDraft!.BatchJobId, stored.BatchJobId);
        Assert.Equal("C:\\private\\results", stored.ResultDirectory);
        Assert.Equal([stored.BatchJobId], fixture.HandedOffBatches);
    }

    [Fact]
    public async Task AccountModesMapToExistingWizardAllocationSemantics()
    {
        using var fixture = Fixture.Valid(accountCount: 2, ready: 4);
        fixture.Wizard.Accounts[1].IsSelected = false;

        await fixture.ViewModel.SelectAccountModeAsync(TransferAccountMode.SingleAccount);
        fixture.Wizard.QuotaBasis = QuotaBasis.TaskCount;
        fixture.Wizard.Accounts[0].TargetItemCount = 4;
        Assert.True(fixture.ViewModel.CanStart);
        Assert.Equal(TransferMode.Distribution, fixture.Wizard.TransferMode);

        fixture.Wizard.Accounts[1].IsSelected = true;
        await fixture.ViewModel.SelectAccountModeAsync(TransferAccountMode.MultiAccountReplication);
        Assert.True(fixture.ViewModel.CanStart);
        Assert.Equal(TransferMode.Replication, fixture.Wizard.TransferMode);
        Assert.Equal(8, fixture.Wizard.OperationCount);

        await fixture.ViewModel.SelectAccountModeAsync(TransferAccountMode.MultiAccountDistribution);
        fixture.Wizard.QuotaBasis = QuotaBasis.TaskCount;
        fixture.Wizard.Accounts[0].TargetItemCount = 2;
        fixture.Wizard.Accounts[1].TargetItemCount = 2;
        Assert.True(fixture.ViewModel.CanStart);
        Assert.Equal(TransferMode.Distribution, fixture.Wizard.TransferMode);
        Assert.Equal(4, fixture.Wizard.AssignedCount);
    }

    [Fact]
    public async Task StartSavesCurrentEditThenPrechecksBeforeConfirmation()
    {
        using var fixture = Fixture.Valid();
        fixture.Import.SelectedRow = new(
            Guid.NewGuid(), 1, "https://pan.baidu.com/s/original", "a1b2", null, "就绪", null);
        fixture.Import.SelectedShareUrl = "https://pan.baidu.com/s/edited";
        fixture.Import.SelectedExtractionCode = "c3d4";
        fixture.ViewModel.AutoShare = true;
        fixture.ViewModel.ShareResultDirectory = "C:\\results";
        fixture.Events.Clear();

        await fixture.ViewModel.StartAsync();

        Assert.Equal(["save", "precheck", "preview", "confirm", "sink", "handoff"], fixture.Events);
        Assert.Equal("https://pan.baidu.com/s/edited", fixture.ImportWorkflow.SavedUrl);
        Assert.Equal("c3d4", fixture.ImportWorkflow.SavedCode);
        Assert.DoesNotContain("创建任务", fixture.Dialog.LastConfirmationMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ActionablePrecheckFailureStopsBeforeBatchPreview()
    {
        using var fixture = Fixture.Valid(precheck: new WizardPrecheckResult(
            WizardPrecheckStatus.Completed,
            4,
            InvalidLinkCount: 1,
            MissingExtractionCodeCount: 2));

        await fixture.ViewModel.StartAsync();

        Assert.Equal(0, fixture.BatchService.PreviewCalls);
        Assert.Contains("无效链接", fixture.ViewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("缺少提取码", fixture.ViewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownPrecheckFailureStopsTheWholeBatchBeforePreviewOrConfirmation()
    {
        using var fixture = Fixture.Valid(precheck: new WizardPrecheckResult(
            WizardPrecheckStatus.Completed,
            2,
            OtherFailureCount: 1));

        await fixture.ViewModel.StartAsync();

        Assert.Equal(0, fixture.BatchService.PreviewCalls);
        Assert.Equal(0, fixture.BatchService.ConfirmCalls);
        Assert.Null(fixture.ViewModel.CreatedBatchJobId);
        Assert.Contains("暂时无法确认", fixture.ViewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DirectUnavailablePrecheckStopsBeforeDurableCreationAndAllowsRetry()
    {
        using var fixture = Fixture.Valid(precheck: new WizardPrecheckResult(
            WizardPrecheckStatus.Unavailable,
            0));
        fixture.ViewModel.AutoShare = true;
        fixture.ViewModel.ShareResultDirectory = "C:\\results";

        await fixture.ViewModel.StartAsync();

        Assert.Equal("链接检查暂不可用，请稍后重试。", fixture.ViewModel.StatusMessage);
        Assert.Equal(0, fixture.BatchService.PreviewCalls);
        Assert.Equal(0, fixture.BatchService.ConfirmCalls);
        Assert.Empty(fixture.DestinationSink.Values);
        Assert.Empty(fixture.HandedOffBatches);
        Assert.Null(fixture.ViewModel.CreatedBatchJobId);
        Assert.True(fixture.ViewModel.CanStart);
    }

    [Fact]
    public async Task PrecheckExceptionMapsToUnavailableAndStopsEveryPostCheckAction()
    {
        using var fixture = Fixture.Valid(
            precheckWorkflow: new ThrowingPrecheckWorkflow());
        fixture.ViewModel.AutoShare = true;
        fixture.ViewModel.ShareResultDirectory = "C:\\results";

        await fixture.ViewModel.StartAsync();

        Assert.Equal("链接检查暂不可用，请稍后重试。", fixture.ViewModel.StatusMessage);
        Assert.Equal(0, fixture.BatchService.PreviewCalls);
        Assert.Equal(0, fixture.BatchService.ConfirmCalls);
        Assert.Empty(fixture.DestinationSink.Values);
        Assert.Empty(fixture.HandedOffBatches);
        Assert.Null(fixture.ViewModel.CreatedBatchJobId);
        Assert.True(fixture.ViewModel.CanStart);
    }

    [Fact]
    public async Task CompletedRetryAfterUnavailableCreatesExactlyOneBatch()
    {
        var precheck = new SequencePrecheckWorkflow(
            new(WizardPrecheckStatus.Unavailable, 0),
            new(WizardPrecheckStatus.Completed, 4));
        using var fixture = Fixture.Valid(precheckWorkflow: precheck);

        await fixture.ViewModel.StartAsync();
        Assert.Equal(0, fixture.BatchService.PreviewCalls);
        Assert.True(fixture.ViewModel.CanStart);

        await fixture.ViewModel.StartAsync();

        Assert.Equal(2, precheck.Calls);
        Assert.Equal(1, fixture.BatchService.PreviewCalls);
        Assert.Equal(1, fixture.BatchService.ConfirmCalls);
        Assert.Single(fixture.HandedOffBatches);
        Assert.NotNull(fixture.ViewModel.CreatedBatchJobId);
    }

    [Fact]
    public async Task InvalidSessionAccountTargetModeAndFixedCodeCannotStart()
    {
        using var fixture = Fixture.Create(accountCount: 2, ready: 4);
        Assert.False(fixture.ViewModel.CanStart);

        fixture.ApplyReadyDraft();
        Assert.False(fixture.ViewModel.CanStart);

        fixture.Wizard.Accounts[0].IsSelected = true;
        await fixture.ViewModel.SelectAccountModeAsync(TransferAccountMode.SingleAccount);
        fixture.Wizard.QuotaBasis = QuotaBasis.TaskCount;
        fixture.Wizard.Accounts[0].TargetItemCount = 4;
        Assert.True(fixture.ViewModel.CanStart);

        fixture.Wizard.Accounts[0].DefaultTargetPath = string.Empty;
        Assert.False(fixture.ViewModel.CanStart);
        fixture.Wizard.Accounts[0].DefaultTargetPath = "/目标";

        fixture.ViewModel.AutoShare = true;
        fixture.ViewModel.ShareResultDirectory = "C:\\results";
        fixture.ViewModel.ExtractionCodeBehavior = ShareExtractionCodeBehavior.Fixed;
        fixture.ViewModel.FixedExtractionCode = "bad!";
        Assert.False(fixture.ViewModel.CanStart);
        fixture.ViewModel.FixedExtractionCode = "a1B2";
        Assert.True(fixture.ViewModel.CanStart);

        fixture.Wizard.Accounts[0].IsSelected = false;
        Assert.False(fixture.ViewModel.CanStart);
    }

    [Fact]
    public void AccountRefreshKeepsNewAccountsOutOfSharingWhenAutomaticSharingIsOff()
    {
        using var fixture = Fixture.Valid();
        fixture.ViewModel.AutoShare = false;

        fixture.Wizard.ReloadAccounts([
            AccountOption("原账号"),
            AccountOption("新账号") with { ShareEnabled = true },
        ]);

        Assert.All(fixture.Wizard.Accounts, account => Assert.False(account.ShareEnabled));
    }

    [Fact]
    public async Task ExcludingEveryReadyRowRefreshesSnapshotAndDisablesStart()
    {
        using var fixture = Fixture.Valid(ready: 1);
        var session = Assert.IsType<Guid>(fixture.Import.SessionId);
        fixture.ImportWorkflow.NextMutation = new(
            session,
            new ImportDraftStatistics(1, 0, 0, 0, 1));

        await fixture.Import.SetExcludedAsync(Guid.NewGuid(), excluded: true);

        Assert.Equal(0, fixture.Wizard.ReadyResourceCount);
        Assert.False(fixture.ViewModel.CanStart);
        Assert.Contains("就绪 0", fixture.Import.StatisticsText, StringComparison.Ordinal);
        Assert.True(fixture.PageSource.LoadCalls > 0);
    }

    [Fact]
    public async Task RepairingAConflictRefreshesSameSessionAndCanEnableStart()
    {
        using var fixture = Fixture.Create(accountCount: 1, ready: 1);
        fixture.ApplyDraft(new ImportDraftStatistics(2, 1, 0, 0, 0, Conflict: 1));
        fixture.Wizard.QuotaBasis = QuotaBasis.TaskCount;
        fixture.Wizard.Accounts[0].TargetItemCount = 1;
        Assert.False(fixture.ViewModel.CanStart);
        var session = Assert.IsType<Guid>(fixture.Import.SessionId);
        fixture.ImportWorkflow.NextMutation = new(
            session,
            new ImportDraftStatistics(2, 1, 0, 0, 1));

        await fixture.Import.UpdateLinkAsync(Guid.NewGuid(), "https://pan.baidu.com/s/repaired", "a1b2");

        Assert.Equal(session, fixture.Wizard.CurrentImportSessionId);
        Assert.True(fixture.ViewModel.CanStart);
    }

    [Fact]
    public async Task ExcelAccountColumnCanBeResolvedForReplicationAndDistributionOnPage()
    {
        using var fixture = Fixture.Create(accountCount: 2, ready: 2);
        fixture.ApplyDraft(new ImportDraftStatistics(
            2, 2, 0, 0, 0,
            HasExcelAccountColumn: true,
            UnresolvedAccountMappings: 2));
        fixture.Wizard.Accounts[0].IsSelected = true;
        fixture.Wizard.Accounts[1].IsSelected = true;

        await fixture.ViewModel.SelectAccountModeAsync(TransferAccountMode.MultiAccountReplication);
        Assert.False(fixture.ViewModel.CanStart);
        await fixture.Wizard.IgnoreExcelAccountsAsync();
        Assert.True(fixture.ViewModel.CanStart);

        await fixture.ViewModel.SelectAccountModeAsync(TransferAccountMode.MultiAccountDistribution);
        fixture.Wizard.QuotaBasis = QuotaBasis.TaskCount;
        fixture.Wizard.Accounts[0].TargetItemCount = 1;
        fixture.Wizard.Accounts[1].TargetItemCount = 1;
        fixture.Wizard.RefreshImportSnapshot(2, 0, 0, true, 2);
        Assert.False(fixture.ViewModel.CanStart);
        await fixture.Wizard.AutoMapUniqueExcelAccountsAsync();
        Assert.True(fixture.ViewModel.CanStart);
    }

    [Fact]
    public async Task ExcelOverrideUsesExplicitlySelectedParticipatingAccount()
    {
        using var fixture = Fixture.Create(accountCount: 2, ready: 2);
        fixture.ApplyDraft(new ImportDraftStatistics(
            2, 2, 0, 0, 0,
            HasExcelAccountColumn: true,
            UnresolvedAccountMappings: 1));
        fixture.Wizard.Accounts[0].IsSelected = true;
        fixture.Wizard.Accounts[1].IsSelected = true;
        await fixture.ViewModel.SelectAccountModeAsync(TransferAccountMode.MultiAccountDistribution);
        fixture.Wizard.ExcelOverrideAccountId = fixture.Wizard.Accounts[1].AccountId;

        await fixture.Wizard.OverrideAllExcelAccountsAsync();

        Assert.Equal(fixture.Wizard.Accounts[1].AccountId, fixture.DraftWorkflow.LastOverrideAccountId);
        Assert.Equal(ExcelAccountHandling.OverrideAll, fixture.Wizard.ExcelAccountHandling);
    }

    [Fact]
    public async Task ExcelMappingsRequireCurrentEligibleSelectedAccountsAndReevaluateImmediately()
    {
        using var fixture = Fixture.Create(accountCount: 3, ready: 3);
        var mapped = fixture.Wizard.Accounts[2].AccountId;
        fixture.ApplyDraft(new ImportDraftStatistics(
            3, 3, 0, 0, 0,
            HasExcelAccountColumn: true,
            MappedAccountIds: [mapped]));
        fixture.Wizard.Accounts[0].IsSelected = true;
        fixture.Wizard.Accounts[1].IsSelected = true;
        await fixture.ViewModel.SelectAccountModeAsync(TransferAccountMode.MultiAccountDistribution);
        fixture.Wizard.RefreshImportSnapshot(3, 0, 0, true, 0, [mapped]);
        fixture.Wizard.ExcelAccountHandling = ExcelAccountHandling.KeepMappings;
        fixture.Wizard.QuotaBasis = QuotaBasis.TaskCount;
        fixture.Wizard.Accounts[0].TargetItemCount = 1;
        fixture.Wizard.Accounts[1].TargetItemCount = 1;
        fixture.Wizard.Accounts[2].TargetItemCount = 1;

        Assert.False(fixture.ViewModel.CanStart);
        Assert.Contains("已映射", fixture.ViewModel.ConfigurationMessage, StringComparison.Ordinal);

        fixture.Wizard.Accounts[2].IsSelected = true;

        Assert.True(fixture.ViewModel.CanStart);
    }

    [Fact]
    public async Task ExcelMappingToUnavailableAccountRequiresOverride()
    {
        using var fixture = Fixture.Create(accountCount: 2, ready: 2);
        var unavailable = Guid.NewGuid();
        fixture.ApplyDraft(new ImportDraftStatistics(
            2, 2, 0, 0, 0,
            HasExcelAccountColumn: true,
            MappedAccountIds: [unavailable]));
        fixture.Wizard.Accounts[0].IsSelected = true;
        fixture.Wizard.Accounts[1].IsSelected = true;
        await fixture.ViewModel.SelectAccountModeAsync(TransferAccountMode.MultiAccountDistribution);
        fixture.Wizard.RefreshImportSnapshot(2, 0, 0, true, 0, [unavailable]);
        fixture.Wizard.ExcelAccountHandling = ExcelAccountHandling.KeepMappings;
        fixture.Wizard.QuotaBasis = QuotaBasis.TaskCount;
        fixture.Wizard.Accounts[0].TargetItemCount = 1;
        fixture.Wizard.Accounts[1].TargetItemCount = 1;

        Assert.False(fixture.ViewModel.CanStart);
        Assert.Contains("账号已不可用", fixture.ViewModel.ConfigurationMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CloseCancelsStartAndPreventsLateSinkHandoffOrStatusMutation()
    {
        var precheck = new BlockingPrecheckWorkflow();
        using var fixture = Fixture.Valid(precheckWorkflow: precheck);
        var start = fixture.ViewModel.StartAsync();
        await precheck.Started.Task;
        var statusAtClose = fixture.ViewModel.StatusMessage;
        fixture.ViewModel.Close();
        precheck.Release();
        await start;

        Assert.Equal(statusAtClose, fixture.ViewModel.StatusMessage);
        Assert.Empty(fixture.DestinationSink.Values);
        Assert.Empty(fixture.HandedOffBatches);
        Assert.True(fixture.Import.IsClosed);
    }

    [Fact]
    public async Task SinkFailureAfterDurableCreationStillHandsOffAndCannotCreateDuplicate()
    {
        using var fixture = Fixture.Valid();
        fixture.ViewModel.AutoShare = true;
        fixture.ViewModel.ShareResultDirectory = "C:\\results";
        fixture.DestinationSink.Error = new IOException("sink unavailable");

        await fixture.ViewModel.StartAsync();
        var created = Assert.IsType<Guid>(fixture.ViewModel.CreatedBatchJobId);
        await fixture.ViewModel.StartAsync();

        Assert.Equal(1, fixture.BatchService.ConfirmCalls);
        Assert.Equal([created], fixture.HandedOffBatches);
        Assert.False(fixture.ViewModel.CanStart);
        Assert.Equal("批次已创建，结果目录暂未保存。", fixture.ViewModel.StatusMessage);
    }

    [Fact]
    public async Task HandoffFailureAfterDurableCreationReportsRecoverableBatch()
    {
        using var fixture = Fixture.Valid();
        fixture.Handoff.Error = new InvalidOperationException("navigation unavailable");

        await fixture.ViewModel.StartAsync();

        Assert.NotNull(fixture.ViewModel.CreatedBatchJobId);
        Assert.Equal(1, fixture.BatchService.ConfirmCalls);
        Assert.Contains(fixture.RunPanel.Batches,
            item => item.BatchJobId == fixture.ViewModel.CreatedBatchJobId);
        Assert.Contains("批次已创建", fixture.ViewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("稍后", fixture.ViewModel.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("转存未能开始", fixture.ViewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallerCancellationAfterDurableConfirmationStillCompletesPostCommitHandoff()
    {
        using var fixture = Fixture.Valid();
        using var cancellation = new CancellationTokenSource();
        fixture.BatchService.AfterConfirm = cancellation.Cancel;

        await fixture.ViewModel.StartAsync(cancellation.Token);

        var created = Assert.IsType<Guid>(fixture.ViewModel.CreatedBatchJobId);
        Assert.Equal([created], fixture.HandedOffBatches);
        Assert.Equal(1, fixture.BatchService.ConfirmCalls);
    }

    [Fact]
    public async Task CloseDuringPostCommitHandoffCancelsTokenWithoutLateUiMutation()
    {
        using var fixture = Fixture.Valid();
        fixture.Handoff.Block = true;
        var changes = new List<string?>();
        fixture.ViewModel.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

        var start = fixture.ViewModel.StartAsync();
        await fixture.Handoff.Started.Task;
        fixture.ViewModel.Close();
        var changesAtClose = changes.Count;
        fixture.Handoff.Release();
        await start;

        Assert.True(SpinWait.SpinUntil(() => fixture.Handoff.CancellationObserved, TimeSpan.FromSeconds(1)));
        Assert.Equal(changesAtClose, changes.Count);
        Assert.NotNull(fixture.ViewModel.CreatedBatchJobId);
    }

    [Fact]
    public async Task NonCooperativeHandoffTimesOutWithRecoverableBatchAndCannotDuplicateCreate()
    {
        using var fixture = Fixture.Valid(options: new TransferWorkspaceOptions(PostCommitTimeoutSeconds: 1));
        fixture.Handoff.Block = true;

        var start = fixture.ViewModel.StartAsync();
        await fixture.Handoff.Started.Task;
        await start.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.NotNull(fixture.ViewModel.CreatedBatchJobId);
        Assert.Contains("批次已创建", fixture.ViewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("恢复查看", fixture.ViewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(1, fixture.BatchService.ConfirmCalls);

        await fixture.ViewModel.StartAsync();
        Assert.Equal(1, fixture.BatchService.ConfirmCalls);
        fixture.Handoff.Release();
    }

    [Fact]
    public async Task RealHandoffCoordinatorTimeoutReportsLateFailureWithoutChangingCreatedBatchOutcome()
    {
        var operations = new NonCooperativeHandoffOperations();
        var reported = new List<Exception>();
        using var coordinator = new ResultWorkspaceHandoffCoordinator(operations, reported.Add);
        using var fixture = Fixture.Valid(
            options: new TransferWorkspaceOptions(PostCommitTimeoutSeconds: 1),
            explicitHandoff: coordinator.ActivateJobAsync);

        var start = fixture.ViewModel.StartAsync();
        await operations.Started.Task;
        await start.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.NotNull(fixture.ViewModel.CreatedBatchJobId);
        Assert.Contains("恢复查看", fixture.ViewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(1, fixture.BatchService.ConfirmCalls);
        operations.ReleaseWithFailure();
        Assert.True(SpinWait.SpinUntil(() => reported.Count == 1, TimeSpan.FromSeconds(1)));
        Assert.Equal(1, fixture.BatchService.ConfirmCalls);
    }

    [Fact]
    public void SinglePageSourceHasRequiredSectionsAndNoRetiredWorkflowLabels()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            root, "src", "BaiduBatchTransfer.App", "Features", "Transfers", "TransferWorkspaceView.xaml"));

        Assert.Contains("导入链接", xaml, StringComparison.Ordinal);
        Assert.Contains("选择账号和保存目录", xaml, StringComparison.Ordinal);
        Assert.Contains("自动分享", xaml, StringComparison.Ordinal);
        Assert.Contains("开始转存", xaml, StringComparison.Ordinal);
        Assert.Contains("进度与结果", xaml, StringComparison.Ordinal);
        Assert.Contains("ScrollViewer", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("草稿", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("创建任务", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("批量预检", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("确认任务", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("NextStepCommand", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("PreviousStepCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("Wizard.IgnoreExcelAccountColumnCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("Wizard.KeepExcelMappingsCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("Wizard.AutoMapUniqueExcelAccountsCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("Wizard.OverrideExcelMappingsCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("Wizard.ExcelOverrideAccountId", xaml, StringComparison.Ordinal);

        var appRoot = Path.Combine(root, "src", "BaiduBatchTransfer.App");
        foreach (var xamlPath in Directory.EnumerateFiles(appRoot, "*.xaml", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(xamlPath);
            Assert.DoesNotContain("草稿", source, StringComparison.Ordinal);
            Assert.DoesNotContain("创建任务", source, StringComparison.Ordinal);
            Assert.DoesNotContain("批量预检", source, StringComparison.Ordinal);
            Assert.DoesNotContain("确认任务", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RuntimeWorkspaceCopyDoesNotExposeForbiddenWorkflowTerms()
    {
        using var fixture = Fixture.Valid();
        var copy = fixture.Wizard.Steps.Select(step => step.Title)
            .Append(fixture.Wizard.ValidationModeText)
            .Append(fixture.Import.StatusMessage)
            .Append(fixture.Import.AdapterStatusMessage)
            .Append(fixture.ViewModel.StatusMessage)
            .Append(fixture.Wizard.ValidationMessage)
            .ToArray();

        Assert.All(copy, value =>
        {
            Assert.DoesNotContain("草稿", value, StringComparison.Ordinal);
            Assert.DoesNotContain("创建任务", value, StringComparison.Ordinal);
            Assert.DoesNotContain("批量预检", value, StringComparison.Ordinal);
            Assert.DoesNotContain("确认任务", value, StringComparison.Ordinal);
        });
    }

    private static string FindRepositoryRoot([CallerFilePath] string sourceFilePath = "")
    {
        var current = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "BaiduBatchTransfer.sln"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException();
    }

    private static WizardAccountOption AccountOption(string name) => new(
        Guid.NewGuid(), name, "Active", true, false, 1_000, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0,
        BatchAccountSelectionDecision.NotSelected,
        ShareEnabled: false);

    private sealed class Fixture : IDisposable
    {
        private Fixture(
            int accountCount,
            int ready,
            WizardPrecheckResult? precheck,
            IWizardPrecheckWorkflow? precheckWorkflow,
            TransferWorkspaceOptions? options,
            Func<Guid, CancellationToken, Task<ResultWorkspaceHandoffOutcome>>? explicitHandoff)
        {
            Ready = ready;
            Events = [];
            ImportWorkflow = new FakeImportWorkflow(Events);
            PageSource = new FakePageSource();
            Dialog = new FakeDialog();
            Import = new ImportViewModel(
                ImportWorkflow, PageSource, Dialog, new AvailableAdapter());
            BatchService = new FakeBatchService(Events);
            DraftWorkflow = new FakeDraftWorkflow(ready);
            Wizard = new CreateJobWizardViewModel(
                Enumerable.Range(1, accountCount).Select(index => Account($"账号 {index}")).ToArray(),
                PageSource,
                BatchService,
                Dialog,
                new AvailableAdapter(),
                DraftWorkflow,
                precheckWorkflow ?? new FakePrecheckWorkflow(Events, precheck ?? new(WizardPrecheckStatus.Completed, ready)),
                new AvailableCapabilities());
            var results = new JobResultsViewModel(
                new EmptyResultsWorkflow(), new FakeResultSaveDialog(), new AvailableCapabilities());
            RunPanel = new WorkspaceRunPanelViewModel(
                results,
                new PauseBatchService(new FakeRunControlStore()),
                new ResumeBatchService(new FakeRunControlStore()),
                new RetryFailedTransferService(new FakeRunControlStore()));
            DestinationSink = new RecordingDestinationSink(Events);
            Handoff = new RecordingHandoff(Events, HandedOffBatches);
            ViewModel = explicitHandoff is null
                ? new TransferWorkspaceViewModel(
                    Import, Wizard, RunPanel, new FakeResultSaveDialog(), DestinationSink, Handoff.RunAsync, options)
                : new TransferWorkspaceViewModel(
                    Import, Wizard, RunPanel, new FakeResultSaveDialog(), DestinationSink, explicitHandoff, options);
        }

        public int Ready { get; }
        public List<string> Events { get; }
        public FakeImportWorkflow ImportWorkflow { get; }
        public FakePageSource PageSource { get; }
        public FakeDialog Dialog { get; }
        public ImportViewModel Import { get; }
        public FakeBatchService BatchService { get; }
        public CreateJobWizardViewModel Wizard { get; }
        public FakeDraftWorkflow DraftWorkflow { get; }
        public WorkspaceRunPanelViewModel RunPanel { get; }
        public RecordingDestinationSink DestinationSink { get; }
        public RecordingHandoff Handoff { get; }
        public List<Guid> HandedOffBatches { get; } = [];
        public TransferWorkspaceViewModel ViewModel { get; }

        public static Fixture Create(
            int accountCount = 1,
            int ready = 4,
            WizardPrecheckResult? precheck = null,
            IWizardPrecheckWorkflow? precheckWorkflow = null,
            TransferWorkspaceOptions? options = null,
            Func<Guid, CancellationToken, Task<ResultWorkspaceHandoffOutcome>>? explicitHandoff = null) =>
            new(accountCount, ready, precheck, precheckWorkflow, options, explicitHandoff);

        public static Fixture Valid(
            int accountCount = 1,
            int ready = 4,
            WizardPrecheckResult? precheck = null,
            IWizardPrecheckWorkflow? precheckWorkflow = null,
            TransferWorkspaceOptions? options = null,
            Func<Guid, CancellationToken, Task<ResultWorkspaceHandoffOutcome>>? explicitHandoff = null)
        {
            var fixture = Create(accountCount, ready, precheck, precheckWorkflow, options, explicitHandoff);
            fixture.ApplyReadyDraft();
            if (accountCount == 1)
            {
                fixture.Wizard.QuotaBasis = QuotaBasis.TaskCount;
                fixture.Wizard.Accounts[0].TargetItemCount = ready;
            }
            else
            {
                fixture.Wizard.Accounts[0].IsSelected = true;
                fixture.ViewModel.SelectAccountModeAsync(TransferAccountMode.SingleAccount).GetAwaiter().GetResult();
                fixture.Wizard.QuotaBasis = QuotaBasis.TaskCount;
                fixture.Wizard.Accounts[0].TargetItemCount = ready;
            }
            fixture.ViewModel.AutoShare = false;
            return fixture;
        }

        public void ApplyReadyDraft() => ApplyDraft(new(Ready, Ready, 0, 0, 0));

        public void ApplyDraft(ImportDraftStatistics statistics)
        {
            var draft = new ImportDraftReady(Guid.NewGuid(), statistics);
            ImportWorkflow.NextMutation = new(draft.SessionId, statistics);
            Import.AttachSession(draft.SessionId);
            ViewModel.ApplyImportedDraft(draft);
        }

        public void Dispose()
        {
            ViewModel.Dispose();
        }

        private static WizardAccountOption Account(string name) => new(
            Guid.NewGuid(), name, "Active", true, false, 1_000, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0,
            BatchAccountSelectionDecision.NotSelected,
            ShareEnabled: false);
    }

    private sealed class FakeImportWorkflow(List<string> events) : IImportWorkflow
    {
        public string? SavedUrl { get; private set; }
        public string? SavedCode { get; private set; }
        public ImportWorkflowResult? NextMutation { get; set; }
        public Task<ImportWorkflowResult> ImportTextAsync(Guid sessionId, string text, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ImportWorkflowResult> ImportExcelAsync(Guid sessionId, string filePath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ImportWorkflowResult> ResumeAsync(Guid sessionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ImportWorkflowResult> SetExcludedAsync(Guid rowId, bool excluded, CancellationToken cancellationToken) =>
            Task.FromResult(NextMutation ?? throw new InvalidOperationException("A mutation result is required."));
        public Task<ImportWorkflowResult> UpdateLinkAsync(Guid rowId, string shareUrl, string? extractionCode, CancellationToken cancellationToken)
        {
            events.Add("save");
            SavedUrl = shareUrl;
            SavedCode = extractionCode;
            return Task.FromResult(NextMutation ?? new ImportWorkflowResult(
                Guid.NewGuid(), new ImportDraftStatistics(4, 4, 0, 0, 0)));
        }
    }

    private sealed class FakePageSource : IImportPreviewPageSource
    {
        public int LoadCalls { get; private set; }
        public Task<ImportPreviewPage> LoadAsync(ImportPreviewPageRequest request, CancellationToken cancellationToken)
        {
            LoadCalls++;
            return Task.FromResult(new ImportPreviewPage([], 0, null));
        }
    }

    private sealed class FakeBatchService(List<string> events) : IWizardBatchService
    {
        public int PreviewCalls { get; private set; }
        public int ShareOperationCount { get; private set; }
        public WizardBatchDraft? ConfirmedDraft { get; private set; }
        public int ConfirmCalls { get; private set; }
        public Action? AfterConfirm { get; set; }

        public Task<WizardConfirmationSummary> PreviewAsync(WizardBatchDraft draft, CancellationToken cancellationToken)
        {
            events.Add("preview");
            PreviewCalls++;
            var selected = draft.Accounts.Where(account => account.IsSelected).ToArray();
            var transferCount = draft.TransferMode == TransferMode.Replication
                ? (long)draft.ReadyResourceCount * selected.Length
                : draft.ReadyResourceCount;
            var confirmations = selected.Select(account =>
            {
                var plannedTransfer = draft.TransferMode == TransferMode.Replication
                    ? draft.ReadyResourceCount
                    : account.TargetItemCount;
                var plannedShare = account.ShareEnabled ? plannedTransfer : 0;
                ShareOperationCount += plannedShare;
                return new WizardAccountConfirmation(
                    account.AccountId, account.TargetSuccessCount, account.TargetSuccessCount,
                    account.TargetSuccessCount, 0, 0, 0, 0, 0, 0,
                    plannedTransfer, plannedShare, plannedTransfer, plannedShare, account.ShareEnabled);
            }).ToArray();
            var shareCount = confirmations.Sum(account => account.PlannedShareCount ?? 0);
            return Task.FromResult(new WizardConfirmationSummary(
                draft.ReadyResourceCount, transferCount, "token", true, confirmations,
                TransferCount: transferCount, ShareCount: shareCount,
                TotalActionCount: transferCount + shareCount, ResultColumnCount: selected.Length * 3));
        }

        public Task<Guid> ConfirmAsync(WizardBatchDraft draft, string confirmationToken, CancellationToken cancellationToken)
        {
            events.Add("confirm");
            ConfirmCalls++;
            ConfirmedDraft = draft;
            AfterConfirm?.Invoke();
            return Task.FromResult(draft.BatchJobId);
        }
    }

    private sealed class FakePrecheckWorkflow(List<string> events, WizardPrecheckResult result) : IWizardPrecheckWorkflow
    {
        public Task<WizardPrecheckResult> RunAsync(Guid sessionId, Guid accountId, CancellationToken cancellationToken)
        {
            events.Add("precheck");
            return Task.FromResult(result);
        }
    }

    private sealed class ThrowingPrecheckWorkflow : IWizardPrecheckWorkflow
    {
        public Task<WizardPrecheckResult> RunAsync(
            Guid sessionId,
            Guid accountId,
            CancellationToken cancellationToken) =>
            Task.FromException<WizardPrecheckResult>(new InvalidOperationException("adapter detail"));
    }

    private sealed class SequencePrecheckWorkflow(params WizardPrecheckResult[] results) : IWizardPrecheckWorkflow
    {
        private readonly Queue<WizardPrecheckResult> remaining = new(results);
        public int Calls { get; private set; }

        public Task<WizardPrecheckResult> RunAsync(
            Guid sessionId,
            Guid accountId,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(remaining.Dequeue());
        }
    }

    private sealed class BlockingPrecheckWorkflow : IWizardPrecheckWorkflow
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<WizardPrecheckResult> RunAsync(Guid sessionId, Guid accountId, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await release.Task;
            cancellationToken.ThrowIfCancellationRequested();
            return new(WizardPrecheckStatus.Completed, 4);
        }
        public void Release() => release.TrySetResult();
    }

    private sealed class FakeDraftWorkflow(int ready) : IWizardDraftWorkflow
    {
        public Guid? LastOverrideAccountId { get; private set; }
        private WizardDraftMutation Mutation => new(ready, ready, 0, 0, 0, 0, 0);
        public Task<WizardDraftMutation> SwitchModeAsync(Guid sessionId, TransferMode mode, CancellationToken cancellationToken) => Task.FromResult(Mutation);
        public Task<WizardDraftMutation> AutoMapUniqueAccountNamesAsync(Guid sessionId, TransferMode mode, CancellationToken cancellationToken) => Task.FromResult(Mutation);
        public Task<WizardDraftMutation> IgnoreAllAccountNamesAsync(Guid sessionId, TransferMode mode, CancellationToken cancellationToken) => Task.FromResult(Mutation);
        public Task<WizardDraftMutation> OverrideAllAccountsAsync(Guid sessionId, Guid accountId, TransferMode mode, CancellationToken cancellationToken)
        {
            LastOverrideAccountId = accountId;
            return Task.FromResult(Mutation);
        }
    }

    private sealed class RecordingDestinationSink(List<string> events) : ITransferResultDestinationSink
    {
        public List<TransferResultDestination> Values { get; } = [];
        public Exception? Error { get; set; }
        public Task SetAsync(TransferResultDestination destination, CancellationToken cancellationToken = default)
        {
            events.Add("sink");
            if (Error is not null) throw Error;
            Values.Add(destination);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingHandoff(List<string> events, List<Guid> batches)
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? Error { get; set; }
        public bool Block { get; set; }
        public bool CancellationObserved { get; private set; }

        public async Task RunAsync(Guid batch, CancellationToken cancellationToken)
        {
            events.Add("handoff");
            if (Error is not null) throw Error;
            if (Block)
            {
                Started.TrySetResult();
                await release.Task;
                CancellationObserved = cancellationToken.IsCancellationRequested;
                cancellationToken.ThrowIfCancellationRequested();
            }
            batches.Add(batch);
        }

        public void Release() => release.TrySetResult();
    }

    private sealed class NonCooperativeHandoffOperations : IResultWorkspaceHandoffOperations
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<Guid?> FindPendingBatchAsync(CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);
        public async Task InitializeAsync(Guid batchJobId, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await release.Task;
            throw new InvalidOperationException("late private detail");
        }
        public Task ResolveExistingSharesAsync(Guid batchJobId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task NavigateToResultsAsync(Guid batchJobId, CancellationToken cancellationToken) => Task.CompletedTask;
        public void ReleaseWithFailure() => release.TrySetResult();
    }

    private sealed class FakeDialog : IDialogService
    {
        public string LastConfirmationMessage { get; private set; } = string.Empty;
        public Task<string?> PickExcelFileAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default)
        {
            LastConfirmationMessage = message;
            return Task.FromResult(true);
        }
        public Task ShowMessageAsync(string title, string message, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeResultSaveDialog : IResultSaveDialog
    {
        public Task<string?> PickDestinationDirectoryAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }

    private sealed class AvailableAdapter : IAdapterAvailability
    {
        public bool CanStartTransfers => true;
        public string StatusMessage => string.Empty;
    }

    private sealed class AvailableCapabilities : IJobResultCapabilityAvailability
    {
        public bool CanUseTransferActions => true;
        public bool CanUseShareActions => true;
        public string TransferStatusMessage => string.Empty;
        public string ShareStatusMessage => string.Empty;
    }

    private sealed class EmptyResultsWorkflow : IJobResultsWorkflow
    {
        public Task<JobResultsSummary> LoadSummaryAsync(Guid batchJobId, CancellationToken cancellationToken = default) => Task.FromResult(new JobResultsSummary(0, 0, 0, 0));
        public Task<JobResultsPage> LoadPageAsync(Guid batchJobId, long? afterCursor, int pageSize, CancellationToken cancellationToken = default) => Task.FromResult(new JobResultsPage([], 0, null));
        public Task<JobResultActionOutcome> RetryShareOnlyAsync(JobResultRow row, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JobResultActionOutcome> ReconcileTransferAsync(Guid transferOperationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JobResultActionOutcome> ReconcileShareAsync(Guid shareOperationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JobResultActionOutcome> ResumeTransferAsync(JobResultRow row, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JobResultActionOutcome> ResumeShareAsync(JobResultRow row, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<long> ExportAsync(Guid batchJobId, string destinationDirectory, IProgress<long> progress, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeRunControlStore : IBatchRunControlStore
    {
        public Task<PauseBatchDisposition> TryPauseBatchAsync(Guid batchJobId, CancellationToken cancellationToken = default) => Task.FromResult(PauseBatchDisposition.Paused);
        public Task<ResumeBatchDisposition> TryResumeBatchAsync(Guid batchJobId, CancellationToken cancellationToken = default) => Task.FromResult(ResumeBatchDisposition.Queued);
        public Task<RetryFailedTransferDisposition> TryRetryFailedTransferAsync(Guid operationId, DateTimeOffset retriedAt, CancellationToken cancellationToken = default) => Task.FromResult(RetryFailedTransferDisposition.Requeued);
    }
}
