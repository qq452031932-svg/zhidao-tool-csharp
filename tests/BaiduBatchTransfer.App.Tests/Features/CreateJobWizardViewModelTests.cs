using BaiduBatchTransfer.App.Features.Jobs;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class CreateJobWizardViewModelTests
{
    [Fact]
    public async Task ExpectedSessionDetachClearsCreationPrecheckPreviewAndStepState()
    {
        var vm = Create(Accounts(1), ready: 2);
        var session = Assert.IsType<Guid>(vm.CurrentImportSessionId);
        vm.UseBulkPrecheck = true;
        await vm.LoadFirstPreviewPageAsync();
        vm.NextStepCommand.Execute(null);

        Assert.False(vm.TryDetachAfterSuccessfulCleanup(Guid.NewGuid()));
        Assert.Equal(session, vm.CurrentImportSessionId);

        Assert.True(vm.TryDetachAfterSuccessfulCleanup(session));
        Assert.Null(vm.CurrentImportSessionId);
        Assert.Equal(0, vm.ReadyResourceCount);
        Assert.Equal(0, vm.PreviewTotalCount);
        Assert.Empty(vm.PreviewRows);
        Assert.Null(vm.LastPrecheckResult);
        Assert.Null(vm.PrecheckAccountId);
        Assert.Empty(vm.MappedAccountIds);
        Assert.Null(vm.ConfirmationToken);
        Assert.False(vm.IsFinalConfirmationChecked);
        Assert.Equal(0, vm.CurrentStepIndex);
        Assert.Empty(vm.AccountSelectionNotice);
        Assert.False(vm.CanCreateJob);
        Assert.False(vm.CanMoveNext);
    }

    [Fact]
    public async Task ExternalCleanupFenceRefusesNavigationPrecheckPreviewAndCreation()
    {
        var gateway = new FakeBatchGateway();
        var vm = Create(Accounts(1), ready: 1, gateway: gateway);
        vm.UseBulkPrecheck = true;
        vm.SetExternalCleanupBusy(true);

        var step = vm.CurrentStepIndex;
        vm.NextStepCommand.Execute(null);
        await vm.RunPrecheckAsync();
        await vm.LoadFirstPreviewPageAsync();
        await vm.PrepareConfirmationAsync();
        await vm.CreateJobAsync();

        Assert.False(vm.CanEdit);
        Assert.Equal(step, vm.CurrentStepIndex);
        Assert.False(vm.NextStepCommand.CanExecute(null));
        Assert.False(vm.RunPrecheckCommand.CanExecute(null));
        Assert.False(vm.LoadFirstPreviewPageCommand.CanExecute(null));
        Assert.False(vm.PrepareConfirmationCommand.CanExecute(null));
        Assert.False(vm.CreateJobCommand.CanExecute(null));
        Assert.Null(gateway.LastPreviewDraft);
    }
    [Theory]
    [InlineData(BatchAccountSelectionDecision.Unspecified)]
    [InlineData((BatchAccountSelectionDecision)99)]
    public void WizardRejectsMissingOrInvalidAccountSelectionDecision(
        BatchAccountSelectionDecision selectionDecision)
    {
        var option = Account("A") with { SelectionDecision = selectionDecision };

        Assert.Throws<ArgumentException>(() => new AccountAllocationRowViewModel(option, "/目标"));
        var error = Assert.Throws<ArgumentException>(() => new CreateJobWizardViewModel(
            [option], new FakePageSource([], 1, null), new FakeBatchGateway(), new FakeDialogService()));

        Assert.Contains("明确选择", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AdaptiveStepsKeepPrecheckVisibleButSkipItWhenDisabled()
    {
        var vm = Create(Accounts(2), ready: 100);

        Assert.Equal(["导入数据", "后台检查", "选择目录", "分配账号", "转存确认"], vm.Steps.Select(x => x.Title));
        Assert.True(vm.Steps.Single(x => x.Kind == WizardStepKind.Precheck).IsSkipped);

        vm.UseBulkPrecheck = true;

        Assert.False(vm.Steps.Single(x => x.Kind == WizardStepKind.Precheck).IsSkipped);
        Assert.Equal(vm.Accounts[0].AccountId, vm.PrecheckAccountId);
    }

    [Fact]
    public void OneEligibleAccountIsSelectedByDefaultButStillNeedsConfirmation()
    {
        var accounts = new[]
        {
            Account("可用账号", status: "Active", enabled: true),
            Account("已停用", status: "Active", enabled: false),
            Account("登录失效", status: "Expired", enabled: true),
            Account("已删除", status: "Active", enabled: true, deleted: true),
        };

        var vm = Create(accounts, ready: 20);

        var selected = Assert.Single(vm.Accounts);
        Assert.True(selected.IsSelected);
        Assert.Equal(TransferMode.Distribution, vm.TransferMode);
        Assert.Contains("已自动选择唯一可用账号", vm.AccountSelectionNotice);
        Assert.False(vm.CanCreateJob);
    }

    [Fact]
    public void ZeroEligibleAccountsCannotContinue()
    {
        var vm = Create([], ready: 20);

        Assert.Empty(vm.Accounts);
        Assert.Null(vm.TransferMode);
        Assert.False(vm.CanContinue);
        Assert.False(vm.CanMoveNext);
    }

    [Fact]
    public void AccountRowExposesSelectedShareAndServerConfirmedWorkloadState()
    {
        var option = Account("A") with
        {
            SelectionDecision = BatchAccountSelectionDecision.Selected,
            ShareEnabled = false,
            PlannedTransferCount = 200_000,
            PlannedShareCount = 0,
        };
        var row = new AccountAllocationRowViewModel(option, "/目标");
        Assert.False(row.HasCurrentConfirmation);
        Assert.Equal(0, row.PlannedTransferCount);
        Assert.Equal(0, row.PlannedShareCount);

        row.ApplyConfirmation(new WizardAccountConfirmation(
            option.AccountId, 1, 1, 1, 0, 0, 0, 0, 0, 0,
            PlannedTransferCount: 200_000, PlannedShareCount: 0,
            ConfirmedTransferCount: 200_000, ConfirmedShareCount: 0, ShareEnabled: false));

        Assert.True(row.IsSelected);
        Assert.False(row.ShareEnabled);
        Assert.Equal(200_000, row.PlannedTransferCount);
        Assert.Equal(0, row.PlannedShareCount);
        Assert.Equal(200_000, row.ConfirmedTransferCount);
        Assert.Equal(0, row.ConfirmedShareCount);
    }

    [Fact]
    public void AccountRowRejectsIncompletePreviewWithoutApplyingPartialDerivedState()
    {
        var row = new AccountAllocationRowViewModel(Account("A"), "/目标");

        Assert.Throws<ArgumentException>(() => row.ApplyConfirmation(new WizardAccountConfirmation(
            row.AccountId, 10, 8, 7, 3, 2, 1, 4, 5, 6)));

        Assert.False(row.HasCurrentConfirmation);
        Assert.Equal(0, row.OriginalTargetSuccessCount);
        Assert.Equal(0, row.EffectiveTargetSuccessCount);
        Assert.Equal(0, row.ReachableTargetSuccessCount);
        Assert.Equal(0, row.LockedItemCount);
    }

    [Fact]
    public async Task PreviewDraftCarriesAuthoritativeSelectionAndPerAccountShareChoice()
    {
        var gateway = new FakeBatchGateway();
        var vm = Create(Accounts(2), ready: 2, gateway: gateway);
        vm.SelectMode(TransferMode.Distribution);
        vm.QuotaBasis = QuotaBasis.TaskCount;
        vm.Accounts[0].IsSelected = true;
        vm.Accounts[0].ShareEnabled = false;
        vm.Accounts[0].TargetItemCount = 2;

        await vm.PrepareConfirmationAsync();

        var draft = Assert.IsType<WizardBatchDraft>(gateway.LastPreviewDraft);
        Assert.Equal(2, draft.Accounts.Count);
        var selected = Assert.Single(draft.Accounts, account => account.IsSelected);
        Assert.True(selected.IsSelected);
        Assert.False(selected.ShareEnabled);
        Assert.Equal(BatchAccountSelectionDecision.NotSelected,
            Assert.Single(draft.Accounts, account => !account.IsSelected).SelectionDecision);
    }

    [Fact]
    public async Task PreviewValidationFailurePreservesConcreteMessage()
    {
        const string message = "The import draft does not exist.";
        var gateway = new FakeBatchGateway
        {
            PreviewException = new BatchDraftValidationException(message),
        };
        var vm = Create(Accounts(1), ready: 1, gateway: gateway);
        vm.QuotaBasis = QuotaBasis.TaskCount;
        vm.Accounts[0].TargetItemCount = 1;

        await vm.PrepareConfirmationAsync();

        Assert.Null(vm.ConfirmationToken);
        Assert.Equal(message, vm.ValidationMessage);
        Assert.NotEqual("转存配置或导入数据已变化，请返回检查并重新预览。", vm.ValidationMessage);
    }

    [Fact]
    public async Task ConfirmationValidationFailurePreservesConcreteMessage()
    {
        const string message = "Direct-background activation graph is incomplete.";
        var gateway = new FakeBatchGateway
        {
            ConfirmException = new BatchDraftValidationException(message),
        };
        var vm = Create(Accounts(1), ready: 1, gateway: gateway);
        vm.QuotaBasis = QuotaBasis.TaskCount;
        vm.Accounts[0].TargetItemCount = 1;
        await vm.PrepareConfirmationAsync();

        Assert.Null(await vm.ConfirmPreparedFromUserIntentAsync());

        Assert.Equal(message, vm.ValidationMessage);
        Assert.NotEqual("任务创建失败，未写入执行队列，请重试。", vm.ValidationMessage);
    }

    [Fact]
    public async Task ConfirmationDeclineFlagResetsBeforeLaterConfirmationFailure()
    {
        var gateway = new FakeBatchGateway();
        var dialog = new FakeDialogService { ConfirmationResult = false };
        var vm = new CreateJobWizardViewModel(
            Accounts(1), new FakePageSource([], 1, null), gateway, dialog);
        vm.Initialize(new(Guid.NewGuid(), "测试任务", 1, false, 0, 0, 0, "/我的资源"));
        vm.QuotaBasis = QuotaBasis.TaskCount;
        vm.Accounts[0].TargetItemCount = 1;
        await vm.PrepareConfirmationAsync();

        Assert.Null(await vm.ConfirmPreparedFromUserIntentAsync());
        Assert.True(vm.LastConfirmationWasDeclined);

        dialog.ConfirmationResult = true;
        gateway.ConfirmException = new BatchConfirmationException("stale");
        Assert.Null(await vm.ConfirmPreparedFromUserIntentAsync());
        Assert.False(vm.LastConfirmationWasDeclined);
        Assert.NotEmpty(vm.ValidationMessage);
    }

    [Fact]
    public async Task AcceptanceDraftCarriesImmutableManagedProfileAndResultDirectory()
    {
        var profile = ExecutionProfileSnapshot.Managed(
            BrowserFamily.Chrome, "acceptance-profile", Guid.NewGuid(), Guid.NewGuid());
        var gateway = new FakeBatchGateway();
        var option = Account("Managed") with
        {
            ExecutionProfile = profile,
            ShareEnabled = true,
        };
        var vm = Create([option], ready: 1, gateway: gateway);
        vm.AcceptanceOnly = true;
        vm.AcceptanceResultDirectory = Path.GetTempPath();
        vm.QuotaBasis = QuotaBasis.TaskCount;
        vm.Accounts[0].TargetItemCount = 1;

        await vm.PrepareConfirmationAsync();

        var draft = Assert.IsType<WizardBatchDraft>(gateway.LastPreviewDraft);
        Assert.True(draft.AcceptanceOnly);
        Assert.Equal(Path.GetTempPath(), draft.ResultDirectory);
        Assert.Equal(profile, Assert.Single(draft.Accounts).ExecutionProfile);
        Assert.Equal(profile, Assert.Single(vm.Accounts).ExecutionProfile);
    }

    [Fact]
    public void WorkloadContractsRedactIdsPathsCodesNamesAndConfirmationTokens()
    {
        var accountId = Guid.NewGuid();
        var account = new WizardAccountDraft(
            accountId, 0, "/敏感目录", 1, 1, BatchAccountSelectionDecision.Selected, true);
        var draft = new WizardBatchDraft(
            Guid.NewGuid(), Guid.NewGuid(), "敏感任务", TransferMode.Distribution, QuotaBasis.TaskCount,
            ShortagePolicy.ProportionalReduction, [account], "/敏感目录", "None", DuplicatePolicy.Skip,
            "Immediate", true, 1, FixedExtractionCode: "s3cr", ExtractionCodeBehavior: ShareExtractionCodeBehavior.Fixed);
        var summary = new WizardConfirmationSummary(1, 1, "secret-token", true);

        var text = $"{account} {draft} {summary}";

        Assert.DoesNotContain(accountId.ToString(), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("敏感目录", text, StringComparison.Ordinal);
        Assert.DoesNotContain("敏感任务", text, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cr", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", text, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void MultipleAccountsRequireExplicitModeAndSwitchingRecalculatesOperationCount()
    {
        var vm = Create(Accounts(3), ready: 1_000);
        Assert.Null(vm.TransferMode);

        vm.SelectMode(TransferMode.Replication);
        foreach (var account in vm.Accounts) account.IsSelected = true;

        Assert.Equal(3_000, vm.OperationCount);

        vm.SelectMode(TransferMode.Distribution);

        Assert.Equal(1_000, vm.OperationCount);
    }

    [Fact]
    public void MultipleAccountsRequireAtLeastOneExplicitParticipantAfterModeSelection()
    {
        var vm = Create(Accounts(2), ready: 10);

        vm.SelectMode(TransferMode.Distribution);
        vm.QuotaBasis = QuotaBasis.TaskCount;

        Assert.DoesNotContain(vm.Accounts, account => account.IsSelected);
        Assert.False(vm.CanContinue);

        vm.Accounts[0].IsSelected = true;
        vm.Accounts[0].TargetItemCount = 10;

        Assert.True(vm.CanContinue);
    }

    [Fact]
    public void ConfirmationPolicyLabelsAreAlwaysChinese()
    {
        var vm = Create(Accounts(2), ready: 10);
        vm.SelectMode(TransferMode.Distribution);
        vm.QuotaBasis = QuotaBasis.SuccessTarget;
        vm.ShortagePolicy = ShortagePolicy.AccountPriority;
        vm.FolderCreationMode = "OuterFolder";
        vm.DuplicatePolicy = DuplicatePolicy.Rename;

        Assert.Equal("多账号分流", vm.TransferModeText);
        Assert.Equal("按成功数量补足", vm.QuotaBasisText);
        Assert.Equal("账号优先级", vm.ShortagePolicyText);
        Assert.Equal("创建外层文件夹", vm.FolderCreationModeText);
        Assert.Equal("自动重命名", vm.DuplicatePolicyText);
    }

    [Fact]
    public void ExcelAccountColumnIsAHardGateForReplicationUntilIgnoredOrModeChanged()
    {
        var vm = Create(Accounts(2), ready: 10, hasExcelAccounts: true);
        vm.SelectMode(TransferMode.Replication);
        foreach (var account in vm.Accounts) account.IsSelected = true;

        Assert.False(vm.CanContinue);
        Assert.Contains("逐行指定账号", vm.ValidationMessage);

        vm.ExcelAccountHandling = ExcelAccountHandling.IgnoreColumn;

        Assert.True(vm.CanContinue);
        vm.SelectMode(TransferMode.Distribution);
        vm.ExcelAccountHandling = ExcelAccountHandling.KeepMappings;
        Assert.DoesNotContain("逐行指定账号", vm.ValidationMessage);
    }

    [Fact]
    public void UnresolvedExcelAccountsRequireMappingOrOverrideInDistribution()
    {
        var vm = Create(Accounts(2), ready: 10, hasExcelAccounts: true, unresolvedMappings: 2);
        vm.SelectMode(TransferMode.Distribution);
        foreach (var account in vm.Accounts) account.IsSelected = true;
        vm.ExcelAccountHandling = ExcelAccountHandling.KeepMappings;

        Assert.False(vm.CanContinue);
        Assert.Contains("2 个账号名称尚未映射", vm.ValidationMessage);

        vm.ExcelAccountHandling = ExcelAccountHandling.OverrideAll;

        Assert.DoesNotContain("尚未映射", vm.ValidationMessage);
    }

    [Fact]
    public async Task CurrentPreviewLockedAssignmentsBlockContinuationUntilTheDraftChanges()
    {
        var accounts = Accounts(2);
        var gateway = new FakeBatchGateway
        {
            Summary = new WizardConfirmationSummary(
                100, 100, "server-token", true,
                [new WizardAccountConfirmation(
                    accounts[0].AccountId, 0, 0, 0, 0, 0, 30, 0, 100, 0,
                    20, 20, 20, 20),
                 new WizardAccountConfirmation(
                    accounts[1].AccountId, 0, 0, 0, 0, 0, 0, 0, 100, 0,
                    80, 80, 80, 80)],
                TransferCount: 100, ShareCount: 100, TotalActionCount: 200, ResultColumnCount: 8),
        };
        var vm = Create(accounts, ready: 100, gateway: gateway);
        vm.SelectMode(TransferMode.Distribution);
        vm.QuotaBasis = QuotaBasis.TaskCount;
        vm.Accounts[0].IsSelected = vm.Accounts[1].IsSelected = true;
        vm.Accounts[0].TargetItemCount = 20;
        vm.Accounts[1].TargetItemCount = 80;
        vm.NextStepCommand.Execute(null);
        vm.NextStepCommand.Execute(null);
        Assert.Equal(WizardStepKind.Allocation, vm.Steps[vm.CurrentStepIndex].Kind);
        Assert.True(vm.NextStepCommand.CanExecute(null));
        var notifications = new List<string?>();
        var canExecuteChanged = 0;
        vm.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        vm.NextStepCommand.CanExecuteChanged += (_, _) => canExecuteChanged++;

        await vm.PrepareConfirmationAsync();

        Assert.False(vm.CanContinue);
        Assert.False(vm.CanMoveNext);
        Assert.False(vm.NextStepCommand.CanExecute(null));
        Assert.Contains("锁定任务 30 条", vm.ValidationMessage);
        Assert.Contains(nameof(CreateJobWizardViewModel.WarningMessage), notifications);
        Assert.Contains(nameof(CreateJobWizardViewModel.CanContinue), notifications);
        Assert.Contains(nameof(CreateJobWizardViewModel.CanMoveNext), notifications);
        Assert.True(canExecuteChanged > 0);
        Assert.Equal(30, vm.Accounts[0].LockedItemCount);

        vm.Accounts[0].TargetItemCount = 30;
        vm.Accounts[1].TargetItemCount = 60;
        Assert.Contains("还差 10 条", vm.ValidationMessage);

        vm.Accounts[1].TargetItemCount = 70;
        Assert.True(vm.CanContinue);
    }

    [Fact]
    public void OneTwoOneShortcutAllocatesExactIntegerQuota()
    {
        var vm = Create(Accounts(3), ready: 200_000);
        vm.SelectMode(TransferMode.Distribution);
        vm.QuotaBasis = QuotaBasis.TaskCount;
        foreach (var account in vm.Accounts) account.IsSelected = true;

        Assert.True(vm.TryApplyOneTwoOne());

        Assert.Equal([50_000, 100_000, 50_000], vm.Accounts.Select(x => x.TargetItemCount));
        Assert.Equal(200_000, vm.AssignedCount);
        Assert.Equal(0, vm.UnassignedCount);
    }

    [Fact]
    public async Task SuccessTargetPreviewExposesReserveAndPerAccountReachabilityWarnings()
    {
        var accounts = Accounts(2);
        var gateway = new FakeBatchGateway
        {
            Summary = new WizardConfirmationSummary(
                20, 20, "server-token", true,
                [new WizardAccountConfirmation(
                    accounts[0].AccountId, 10, 8, 8, 2, 1, 0, 8, 12, 3,
                    10, 10, 10, 10),
                 new WizardAccountConfirmation(
                    accounts[1].AccountId, 10, 10, 10, 0, 0, 0, 0, 12, 0,
                    10, 10, 10, 10)],
                TransferCount: 20, ShareCount: 20, TotalActionCount: 40, ResultColumnCount: 8),
        };
        var vm = Create(accounts, ready: 20, gateway: gateway);
        vm.SelectMode(TransferMode.Distribution);
        vm.QuotaBasis = QuotaBasis.SuccessTarget;
        foreach (var account in vm.Accounts) account.IsSelected = true;
        vm.Accounts[0].TargetSuccessCount = 10;
        vm.Accounts[1].TargetSuccessCount = 10;
        var notifications = new List<string?>();
        vm.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        await vm.PrepareConfirmationAsync();

        Assert.Equal(0, vm.ReserveCandidateCount);
        Assert.Contains("没有候补资源", vm.WarningMessage);
        Assert.Contains("A 的可达目标为 8", vm.WarningMessage);
        Assert.Equal(2, vm.Accounts[0].FulfilledCount);
        Assert.Equal(1, vm.Accounts[0].ReservedSlotCount);
        Assert.Equal(3, vm.Accounts[0].UnusedExclusiveCandidateCount);
        Assert.Equal(2, vm.Accounts[0].ShortfallCount);
        Assert.Contains(nameof(CreateJobWizardViewModel.WarningMessage), notifications);
    }

    [Fact]
    public void EqualAndCapacityShortcutsAllocateEveryResourceDeterministically()
    {
        var accounts = new[]
        {
            AccountWithCapacity("A", 100),
            AccountWithCapacity("B", 300),
            AccountWithCapacity("C", 600),
        };
        var vm = Create(accounts, ready: 10);
        vm.SelectMode(TransferMode.Distribution);
        vm.QuotaBasis = QuotaBasis.TaskCount;
        foreach (var account in vm.Accounts) account.IsSelected = true;

        Assert.True(vm.TryApplyEqualAllocation());
        Assert.Equal([4, 3, 3], vm.Accounts.Select(x => x.TargetItemCount));

        Assert.True(vm.TryApplyCapacityAllocation());
        Assert.Equal([1, 3, 6], vm.Accounts.Select(x => x.TargetItemCount));
        Assert.Equal(10, vm.Accounts.Sum(x => x.TargetItemCount));
    }

    [Fact]
    public async Task PreviewUsesOnlyDatabasePageAndNeverCreatesCollectionForEntireImport()
    {
        var source = new FakePageSource(Enumerable.Range(1, 25).Select(Row).ToArray(), total: 200_000, next: 25);
        var vm = Create(Accounts(1), ready: 200_000, pageSource: source);

        await vm.LoadFirstPreviewPageAsync();

        Assert.Equal(25, vm.PreviewRows.Count);
        Assert.Equal(200_000, vm.PreviewTotalCount);
        var request = Assert.Single(source.Requests);
        Assert.InRange(request.PageSize, 1, 1_000);
        Assert.Null(request.AfterSourceRowNumber);
    }

    [Fact]
    public async Task PageFromAnOlderImportCannotReplaceTheCurrentDraftPage()
    {
        var source = new DelayedPageSource();
        var vm = new CreateJobWizardViewModel(
            Accounts(1), source, new FakeBatchGateway(), new FakeDialogService());
        vm.Initialize(new(Guid.NewGuid(), "旧任务", 1, false, 0, 0, 0, "/旧"));

        var oldLoad = vm.LoadFirstPreviewPageAsync();
        vm.Initialize(new(Guid.NewGuid(), "新任务", 1, false, 0, 0, 0, "/新"));
        source.Complete(new ImportPreviewPage([Row(1)], 1, null));
        await oldLoad;

        Assert.Empty(vm.PreviewRows);
    }

    [Fact]
    public async Task PendingConflictBlocksServerPreviewAndFinalConfirmationUsesOriginalToken()
    {
        var gateway = new FakeBatchGateway();
        var vm = Create(Accounts(1), ready: 10, unresolvedRows: 1, gateway: gateway);

        await vm.PrepareConfirmationAsync();

        Assert.Equal(0, gateway.PreviewCalls);
        Assert.Contains("仍有 1 条冲突或待处理记录", vm.ValidationMessage);

        vm.SetImportResolutionCounts(0, 0);
        vm.Accounts[0].TargetSuccessCount = 8;
        await vm.PrepareConfirmationAsync();
        Guid? createdBatchId = null;
        vm.JobCreated += (_, batchId) => createdBatchId = batchId;
        vm.IsFinalConfirmationChecked = true;
        await vm.CreateJobAsync();

        Assert.Equal("preview-token", gateway.ConfirmedToken);
        Assert.Equal(gateway.LastPreviewDraft!.BatchJobId, createdBatchId);
    }

    [Fact]
    public async Task AnyDraftChangeInvalidatesPreviewAndDiskShortageIsReportedInChinese()
    {
        var gateway = new FakeBatchGateway { ThrowDiskShortage = true };
        var vm = Create(Accounts(1), ready: 10, gateway: gateway);

        await vm.PrepareConfirmationAsync();
        vm.IsFinalConfirmationChecked = true;
        vm.DefaultTargetPath = "/新目录";

        Assert.False(vm.CanCreateJob);
        Assert.Null(vm.ConfirmationToken);

        await vm.PrepareConfirmationAsync();

        Assert.Contains("本机磁盘空间不足", vm.ValidationMessage);
        Assert.DoesNotContain("StorageCapacityException", vm.ValidationMessage);
    }

    [Fact]
    public async Task FinalConfirmationUsesServerCountsDiskEstimateAndAccountSnapshot()
    {
        var account = Account("A");
        var gateway = new FakeBatchGateway
        {
            Summary = new WizardConfirmationSummary(
                8, 9, "server-token", true,
                [new WizardAccountConfirmation(
                    account.AccountId, 10, 8, 7, 3, 2, 1, 4, 5, 6,
                    PlannedTransferCount: 9, PlannedShareCount: 7,
                    ConfirmedTransferCount: 9, ConfirmedShareCount: 7)],
                12_345, 67_890, TransferCount: 9, ShareCount: 7,
                TotalActionCount: 16, ResultColumnCount: 8),
        };
        var vm = Create([account], ready: 10, gateway: gateway);

        await vm.PrepareConfirmationAsync();

        Assert.Equal(8, vm.ConfirmedResourceCount);
        Assert.Equal(9, vm.ConfirmedOperationCount);
        Assert.Equal(9, vm.TransferCount);
        Assert.Equal(7, vm.ShareCount);
        Assert.Equal(16, vm.TotalActionCount);
        Assert.Equal(8, vm.ResultColumnCount);
        Assert.Equal(12_345, vm.RequiredFreeBytes);
        Assert.Equal(67_890, vm.AvailableFreeBytes);
        Assert.Equal(8, vm.Accounts[0].EffectiveTargetSuccessCount);
        Assert.Equal(7, vm.Accounts[0].ReachableTargetSuccessCount);
        Assert.Equal(2, vm.Accounts[0].ReservedSlotCount);
        Assert.Equal(9, vm.Accounts[0].ConfirmedTransferCount);
        Assert.Equal(7, vm.Accounts[0].ConfirmedShareCount);
        Assert.True(vm.Accounts[0].HasCurrentConfirmation);
    }

    [Fact]
    public async Task OneOfTenTwoHundredThousandConfirmationUsesOnlyAuthoritativeSelectedAccountWorkload()
    {
        var accounts = Accounts(10);
        var selected = accounts[0];
        var gateway = new FakeBatchGateway
        {
            Summary = new WizardConfirmationSummary(
                200_000, 200_000, "server-token", true,
                [new WizardAccountConfirmation(
                    selected.AccountId, 200_000, 200_000, 200_000, 0, 0, 0, 0, 200_000, 0,
                    PlannedTransferCount: 200_000, PlannedShareCount: 200_000,
                    ConfirmedTransferCount: 200_000, ConfirmedShareCount: 200_000,
                    ShareEnabled: true)],
                TransferCount: 200_000, ShareCount: 200_000,
                TotalActionCount: 400_000, ResultColumnCount: 8),
        };
        var vm = Create(accounts, ready: 200_000, gateway: gateway);
        vm.SelectMode(TransferMode.Distribution);
        vm.QuotaBasis = QuotaBasis.TaskCount;
        vm.Accounts[0].IsSelected = true;
        vm.Accounts[0].TargetItemCount = 200_000;

        await vm.PrepareConfirmationAsync();

        var confirmationAccount = Assert.Single(vm.ConfirmationAccounts);
        Assert.Equal(selected.AccountId, confirmationAccount.AccountId);
        Assert.Equal(200_000, confirmationAccount.ConfirmedTransferCount);
        Assert.Equal(200_000, confirmationAccount.ConfirmedShareCount);
        Assert.Equal(200_000, vm.TransferCount);
        Assert.Equal(200_000, vm.ShareCount);
        Assert.Equal(400_000, vm.TotalActionCount);
        Assert.Equal(8, vm.ResultColumnCount);
    }

    [Fact]
    public async Task BusyConfirmationDisablesBothNavigationDirections()
    {
        var gateway = new DelayedBatchGateway();
        var vm = Create(Accounts(1), ready: 2, gateway: gateway);
        vm.NextStepCommand.Execute(null);
        vm.NextStepCommand.Execute(null);
        Assert.Equal(WizardStepKind.Allocation, vm.Steps.Single(step => step.IsCurrent).Kind);

        var preparing = vm.PrepareConfirmationAsync();
        await gateway.Started.Task;

        Assert.True(vm.IsBusy);
        Assert.False(vm.CanEdit);
        Assert.False(vm.NextStepCommand.CanExecute(null));
        Assert.False(vm.PreviousStepCommand.CanExecute(null));

        gateway.Complete();
        await preparing;
    }

    [Fact]
    public async Task NavigatingInvalidatesConfirmationTokenAndSummary()
    {
        var vm = Create(Accounts(1), ready: 2);
        await vm.PrepareConfirmationAsync();
        Assert.NotNull(vm.ConfirmationToken);
        Assert.True(vm.TotalActionCount > 0);

        vm.NextStepCommand.Execute(null);

        Assert.Null(vm.ConfirmationToken);
        Assert.Equal(0, vm.TransferCount);
        Assert.Equal(0, vm.ShareCount);
        Assert.Equal(0, vm.TotalActionCount);
        Assert.Equal(0, vm.ResultColumnCount);
        Assert.Empty(vm.ConfirmationAccounts);
    }

    [Theory]
    [InlineData("switch")]
    [InlineData("ignore")]
    [InlineData("auto-map")]
    [InlineData("override")]
    public async Task DraftWorkflowCompletionCannotOverwriteANewerImportSession(string operation)
    {
        var workflow = new DelayedDraftWorkflow();
        var vm = new CreateJobWizardViewModel(
            Accounts(2), new FakePageSource([], 10, null), new FakeBatchGateway(),
            new FakeDialogService(), draftWorkflow: workflow);
        vm.Initialize(new(Guid.NewGuid(), "旧任务", 10, true, 1, 0, 0, "/旧目录"));

        Task pending;
        switch (operation)
        {
            case "switch":
                pending = vm.SelectModeAsync(TransferMode.Replication);
                break;
            case "ignore":
                vm.SelectMode(TransferMode.Replication);
                pending = vm.IgnoreExcelAccountsAsync();
                break;
            case "auto-map":
                vm.SelectMode(TransferMode.Distribution);
                pending = vm.AutoMapUniqueExcelAccountsAsync();
                break;
            case "override":
                vm.SelectMode(TransferMode.Distribution);
                vm.Accounts[0].IsSelected = true;
                pending = vm.OverrideAllExcelAccountsAsync();
                break;
            default:
                throw new InvalidOperationException(operation);
        }

        await workflow.Started.Task;
        var newSessionId = Guid.NewGuid();
        vm.Initialize(new(newSessionId, "新任务", 7, true, 3, 0, 0, "/新目录"));
        workflow.Complete();
        await pending;

        Assert.Equal(newSessionId, vm.CurrentImportSessionId);
        Assert.Equal(7, vm.ReadyResourceCount);
        Assert.Null(vm.TransferMode);
        Assert.Equal(ExcelAccountHandling.Unresolved, vm.ExcelAccountHandling);
    }

    [Fact]
    public void WizardXamlWiresShareControlsAndAuthoritativeConfirmationSummary()
    {
        var xaml = File.ReadAllText(FindRepositoryFile(
            "src", "BaiduBatchTransfer.App", "Features", "Jobs", "CreateJobWizardView.xaml"));

        Assert.Contains("{Binding ShareEnabled", xaml, StringComparison.Ordinal);
        Assert.Contains("{Binding ShareValidityText", xaml, StringComparison.Ordinal);
        Assert.Contains("{Binding ShareAccessText", xaml, StringComparison.Ordinal);
        Assert.Contains("{Binding ExtractionCodeBehavior", xaml, StringComparison.Ordinal);
        Assert.Contains("{Binding FixedExtractionCode", xaml, StringComparison.Ordinal);
        Assert.Contains("{Binding ExistingSharePolicyText", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Tag=\"Reuse\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Tag=\"Regenerate\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Tag=\"Cancel\"", xaml, StringComparison.Ordinal);
        Assert.Contains("{Binding ConfirmationAccounts", xaml, StringComparison.Ordinal);
        Assert.Contains("{Binding ConfirmedTransferCount", xaml, StringComparison.Ordinal);
        Assert.Contains("{Binding ConfirmedShareCount", xaml, StringComparison.Ordinal);
        Assert.Contains("{Binding TransferCount", xaml, StringComparison.Ordinal);
        Assert.Contains("{Binding ShareCount", xaml, StringComparison.Ordinal);
        Assert.Contains("{Binding TotalActionCount", xaml, StringComparison.Ordinal);
        Assert.Contains("{Binding ResultColumnCount", xaml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("quota")]
    [InlineData("shortage")]
    [InlineData("mapping")]
    [InlineData("share")]
    [InlineData("selection")]
    public async Task EveryDraftEditClearsEveryPreviewDerivedAccountValue(string mutation)
    {
        var account = Account("A", locked: 99, exclusive: 99, publicCandidates: 99,
            original: 99, effective: 99, reachable: 99, fulfilled: 99, reserved: 99, unused: 99);
        var gateway = new FakeBatchGateway
        {
            Summary = new WizardConfirmationSummary(
                10, 10, "server-token", true,
                [new WizardAccountConfirmation(
                    account.AccountId, 10, 8, 7, 3, 2, 1, 4, 5, 6,
                    PlannedTransferCount: 10, PlannedShareCount: 10,
                    ConfirmedTransferCount: 10, ConfirmedShareCount: 10)],
                TransferCount: 10, ShareCount: 10, TotalActionCount: 20, ResultColumnCount: 5),
        };
        var vm = Create([account], ready: 10, hasExcelAccounts: true, gateway: gateway);
        vm.QuotaBasis = QuotaBasis.SuccessTarget;
        vm.ExcelAccountHandling = ExcelAccountHandling.KeepMappings;

        await vm.PrepareConfirmationAsync();

        var row = Assert.Single(vm.Accounts);
        Assert.True(row.HasCurrentConfirmation);
        Assert.Equal(10, row.OriginalTargetSuccessCount);
        Assert.Equal(8, row.EffectiveTargetSuccessCount);
        Assert.Equal(7, row.ReachableTargetSuccessCount);
        Assert.Equal(3, row.FulfilledCount);
        Assert.Equal(2, row.ReservedSlotCount);
        Assert.Equal(1, row.LockedItemCount);
        Assert.Equal(4, row.ExclusiveCandidateCount);
        Assert.Equal(5, row.PublicCandidateCount);
        Assert.Equal(6, row.UnusedExclusiveCandidateCount);
        Assert.Equal(3, row.ShortfallCount);
        Assert.NotEmpty(vm.WarningMessage);

        switch (mutation)
        {
            case "quota": row.TargetSuccessCount = 9; break;
            case "shortage": vm.ShortagePolicy = ShortagePolicy.AccountPriority; break;
            case "mapping": vm.ExcelAccountHandling = ExcelAccountHandling.OverrideAll; break;
            case "share": row.ShareEnabled = false; break;
            case "selection": row.IsSelected = false; break;
            default: throw new InvalidOperationException(mutation);
        }

        AssertNoPreviewDerivedState(vm, row);
    }

    [Fact]
    public async Task FailedRepreviewClearsDerivedStateAndNotifiesBindableWarning()
    {
        var gateway = new FakeBatchGateway();
        var vm = Create(Accounts(1), ready: 10, gateway: gateway);
        await vm.PrepareConfirmationAsync();
        Assert.NotEmpty(vm.WarningMessage);
        var notifications = new List<string?>();
        vm.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        gateway.ThrowDiskShortage = true;

        await vm.PrepareConfirmationAsync();

        AssertNoPreviewDerivedState(vm, Assert.Single(vm.Accounts));
        Assert.Contains(nameof(CreateJobWizardViewModel.WarningMessage), notifications);
    }

    [Fact]
    public async Task BindableShareSettingsBuildTheDraftAndEveryEditInvalidatesPreview()
    {
        var gateway = new FakeBatchGateway();
        var vm = Create(Accounts(1), ready: 2, gateway: gateway);

        Assert.True(vm.IsSharePermanent);
        Assert.True(vm.IsShareAccessUnlimited);
        Assert.Equal("永久", vm.ShareValidityText);
        Assert.Equal("不限次数", vm.ShareAccessText);
        Assert.Equal("随机提取码", vm.ExtractionCodeBehaviorText);
        Assert.Equal("Ask", vm.ExistingSharePolicy);
        Assert.Equal("询问，默认复用", vm.ExistingSharePolicyText);

        await vm.PrepareConfirmationAsync();
        Assert.NotNull(vm.ConfirmationToken);

        vm.ExtractionCodeBehavior = ShareExtractionCodeBehavior.Fixed;
        Assert.Null(vm.ConfirmationToken);
        Assert.Contains("固定提取码", vm.ValidationMessage);
        vm.FixedExtractionCode = "a1b2";
        Assert.Equal("固定提取码", vm.ExtractionCodeBehaviorText);
        Assert.Equal("询问，默认复用", vm.ExistingSharePolicyText);
        await vm.PrepareConfirmationAsync();

        var draft = Assert.IsType<WizardBatchDraft>(gateway.LastPreviewDraft);
        Assert.True(draft.IsSharePermanent);
        Assert.True(draft.IsShareAccessUnlimited);
        Assert.Equal(ShareExtractionCodeBehavior.Fixed, draft.ExtractionCodeBehavior);
        Assert.Equal("a1b2", draft.FixedExtractionCode);
        Assert.Equal("Ask", draft.ExistingSharePolicy);
        Assert.NotNull(vm.ConfirmationToken);

        vm.FixedExtractionCode = "b2c3";
        Assert.Null(vm.ConfirmationToken);
        Assert.Equal(0, vm.TransferCount);
        Assert.Equal(0, vm.ShareCount);
        Assert.Equal(0, vm.TotalActionCount);
        Assert.Equal(0, vm.ResultColumnCount);
        Assert.All(vm.Accounts, row =>
        {
            Assert.Equal(0, row.PlannedTransferCount);
            Assert.Equal(0, row.PlannedShareCount);
            Assert.Equal(0, row.ConfirmedTransferCount);
            Assert.Equal(0, row.ConfirmedShareCount);
        });
    }

    [Fact]
    public async Task DirectBackgroundSettingsAreIncludedInTheConfirmedDraft()
    {
        var gateway = new FakeBatchGateway();
        var vm = Create(Accounts(1), ready: 2, gateway: gateway);
        vm.DirectBackground = true;
        vm.MaxConcurrency = 2;
        vm.DuplicatePolicy = DuplicatePolicy.Rename;
        vm.QuotaBasis = QuotaBasis.TaskCount;
        vm.ExistingSharePolicy = "Regenerate";
        vm.ExtractionCodeBehavior = ShareExtractionCodeBehavior.Random;
        vm.ResultDirectory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "direct-results"));
        vm.Accounts[0].ShareEnabled = true;

        await vm.PrepareConfirmationAsync();

        var draft = Assert.IsType<WizardBatchDraft>(gateway.LastPreviewDraft);
        Assert.True(draft.DirectBackground);
        Assert.Equal(2, draft.MaxConcurrency);
        Assert.Equal(DuplicatePolicy.Rename, draft.DuplicatePolicy);
        Assert.Equal("Regenerate", draft.ExistingSharePolicy);
        Assert.Equal(vm.ResultDirectory, draft.ResultDirectory);
    }

    [Theory]
    [InlineData(TransferMode.Replication)]
    [InlineData(TransferMode.Distribution)]
    public async Task DirectBackgroundAllowsMultipleSelectedAccountsInBothTaskCountModes(TransferMode mode)
    {
        var gateway = new FakeBatchGateway();
        var vm = Create(Accounts(2), ready: 2, gateway: gateway);
        vm.SelectMode(mode);
        vm.DirectBackground = true;
        vm.QuotaBasis = mode == TransferMode.Replication
            ? QuotaBasis.SuccessTarget
            : QuotaBasis.TaskCount;
        vm.DuplicatePolicy = DuplicatePolicy.Rename;
        vm.ExistingSharePolicy = "Regenerate";
        vm.ExtractionCodeBehavior = ShareExtractionCodeBehavior.Random;
        vm.ResultDirectory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "direct-multi-results"));
        foreach (var account in vm.Accounts)
        {
            account.IsSelected = true;
            account.ShareEnabled = true;
            account.TargetItemCount = mode == TransferMode.Distribution ? 1 : 2;
        }

        Assert.True(vm.CanContinue);
        await vm.PrepareConfirmationAsync();

        var draft = Assert.IsType<WizardBatchDraft>(gateway.LastPreviewDraft);
        Assert.Equal(mode, draft.TransferMode);
        Assert.Equal(2, draft.Accounts.Count(account => account.IsSelected));
        Assert.All(draft.Accounts.Where(account => account.IsSelected), account => Assert.True(account.ShareEnabled));
        Assert.Equal(QuotaBasis.TaskCount, draft.QuotaBasis);
        Assert.NotNull(vm.ConfirmationToken);
    }

    [Theory]
    [InlineData("no-account")]
    [InlineData("success-target")]
    [InlineData("skip")]
    [InlineData("reuse")]
    [InlineData("fixed-code")]
    [InlineData("share-disabled")]
    [InlineData("relative-result")]
    public void DirectBackgroundRejectsAnyNonExactMultiAccountWizardShape(string scenario)
    {
        var vm = Create(Accounts(2), ready: 2);
        vm.SelectMode(TransferMode.Distribution);
        vm.DirectBackground = true;
        vm.QuotaBasis = QuotaBasis.TaskCount;
        vm.DuplicatePolicy = DuplicatePolicy.Rename;
        vm.ExistingSharePolicy = "Regenerate";
        vm.ExtractionCodeBehavior = ShareExtractionCodeBehavior.Random;
        vm.ResultDirectory = Path.GetTempPath();
        foreach (var account in vm.Accounts)
        {
            account.IsSelected = true;
            account.ShareEnabled = true;
            account.TargetItemCount = 1;
        }

        switch (scenario)
        {
            case "no-account":
                foreach (var account in vm.Accounts) account.IsSelected = false;
                break;
            case "success-target": vm.QuotaBasis = QuotaBasis.SuccessTarget; break;
            case "skip": vm.DuplicatePolicy = DuplicatePolicy.Skip; break;
            case "reuse": vm.ExistingSharePolicy = "Reuse"; break;
            case "fixed-code": vm.ExtractionCodeBehavior = ShareExtractionCodeBehavior.Fixed; break;
            case "share-disabled": vm.Accounts[1].ShareEnabled = false; break;
            case "relative-result": vm.ResultDirectory = "relative"; break;
            default: throw new InvalidOperationException(scenario);
        }

        Assert.False(vm.CanContinue);
    }

    [Fact]
    public void DirectBackgroundConcurrencyAcceptsOneThroughThreeOnly()
    {
        var vm = Create(Accounts(1), ready: 1);

        vm.MaxConcurrency = 1;
        Assert.Equal(1, vm.MaxConcurrency);
        vm.MaxConcurrency = 3;
        Assert.Equal(3, vm.MaxConcurrency);
        Assert.Throws<ArgumentOutOfRangeException>(() => vm.MaxConcurrency = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => vm.MaxConcurrency = 4);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("counts")]
    [InlineData("share-setting")]
    public async Task PreviewRejectsIncompleteOrAmbiguousAccountConfirmationSnapshot(string shape)
    {
        var options = Accounts(2).Select(option => option with
        {
            PlannedTransferCount = 12,
            PlannedShareCount = 11,
        }).ToArray();
        var confirmations = shape switch
        {
            "missing" => new[]
            {
                Confirmation(options[0].AccountId),
            },
            "duplicate" => new[]
            {
                Confirmation(options[0].AccountId),
                Confirmation(options[0].AccountId),
            },
            "counts" => new[]
            {
                Confirmation(options[0].AccountId),
                Confirmation(options[1].AccountId),
            },
            "share-setting" => new[]
            {
                Confirmation(options[0].AccountId) with
                {
                    PlannedShareCount = 0,
                    ConfirmedShareCount = 0,
                    ShareEnabled = false,
                },
                Confirmation(options[1].AccountId),
            },
            _ => new[]
            {
                Confirmation(options[0].AccountId),
                Confirmation(Guid.NewGuid()),
            },
        };
        var gateway = new FakeBatchGateway
        {
            Summary = new WizardConfirmationSummary(
                2, shape == "counts" ? 5 : 4, "must-not-survive", true, confirmations,
                TransferCount: shape == "counts" ? 5 : 4,
                ShareCount: shape == "share-setting" ? 2 : 4,
                TotalActionCount: shape == "counts" ? 9 : shape == "share-setting" ? 6 : 8,
                ResultColumnCount: 10),
        };
        var vm = Create(options, ready: 2, gateway: gateway);
        vm.SelectMode(TransferMode.Replication);
        foreach (var account in vm.Accounts) account.IsSelected = true;

        await vm.PrepareConfirmationAsync();

        Assert.Null(vm.ConfirmationToken);
        Assert.Contains("重新预览", vm.ValidationMessage);
        Assert.Equal(0, vm.TransferCount);
        Assert.Equal(0, vm.ShareCount);
        Assert.All(vm.Accounts, account =>
        {
            Assert.True(account.ShareEnabled);
            Assert.Equal(0, account.PlannedTransferCount);
            Assert.Equal(0, account.PlannedShareCount);
            Assert.Equal(0, account.ConfirmedTransferCount);
            Assert.Equal(0, account.ConfirmedShareCount);
        });
    }

    [Fact]
    public async Task ModeAndExcelActionsPersistThroughDraftWorkflowBeforeChangingUiState()
    {
        var draftWorkflow = new FakeDraftWorkflow();
        var vm = new CreateJobWizardViewModel(
            Accounts(2), new FakePageSource([], 10, null), new FakeBatchGateway(),
            new FakeDialogService(), draftWorkflow: draftWorkflow);
        vm.Initialize(new(Guid.NewGuid(), "任务", 10, true, 0, 0, 0, "/资源"));

        await vm.SelectModeAsync(TransferMode.Distribution);
        Assert.False(vm.IsBusy);
        await vm.AutoMapUniqueExcelAccountsAsync();
        Assert.False(vm.IsBusy);
        await vm.IgnoreExcelAccountsAsync();
        Assert.False(vm.IsBusy);
        vm.Accounts[0].IsSelected = true;
        await vm.OverrideAllExcelAccountsAsync();
        Assert.False(vm.IsBusy);

        Assert.Equal(TransferMode.Distribution, draftWorkflow.LastMode);
        Assert.True(draftWorkflow.AutoMapCalled);
        Assert.True(draftWorkflow.IgnoreCalled);
        Assert.Equal(ExcelAccountHandling.OverrideAll, vm.ExcelAccountHandling);
    }

    [Fact]
    public async Task PrecheckPausesAndResumesWithTheSameExplicitAccount()
    {
        var precheck = new FakePrecheckWorkflow();
        var vm = new CreateJobWizardViewModel(
            Accounts(2), new FakePageSource([], 10, null), new FakeBatchGateway(), new FakeDialogService(),
            precheckWorkflow: precheck);
        vm.Initialize(new(Guid.NewGuid(), "任务", 10, false, 0, 0, 0, "/资源"));
        vm.SelectMode(TransferMode.Distribution);
        vm.UseBulkPrecheck = true;
        vm.PrecheckAccountId = vm.Accounts[1].AccountId;

        await vm.RunPrecheckAsync();
        await vm.RunPrecheckAsync();

        Assert.Equal([vm.Accounts[1].AccountId, vm.Accounts[1].AccountId], precheck.Accounts);
        Assert.Contains("继续", vm.PrecheckStatusMessage);
    }

    [Fact]
    public async Task InflightPrecheckCannotWriteIntoANewerImportSessionOrKeepItBusy()
    {
        var precheck = new DelayedPrecheckWorkflow();
        var vm = new CreateJobWizardViewModel(
            Accounts(2), new FakePageSource([], 10, null), new FakeBatchGateway(), new FakeDialogService(),
            precheckWorkflow: precheck);
        vm.Initialize(new(Guid.NewGuid(), "旧任务", 10, false, 0, 0, 0, "/资源"));
        vm.SelectMode(TransferMode.Distribution);
        vm.UseBulkPrecheck = true;
        vm.PrecheckAccountId = vm.Accounts[0].AccountId;

        var running = vm.RunPrecheckAsync();
        await precheck.Started.Task;
        Assert.True(vm.IsBusy);

        var currentSession = Guid.NewGuid();
        vm.Initialize(new(currentSession, "新任务", 3, false, 0, 0, 0, "/新资源"));
        Assert.False(vm.IsBusy);
        precheck.Complete();
        await running;

        Assert.Equal(currentSession, vm.CurrentImportSessionId);
        Assert.Empty(vm.PrecheckStatusMessage);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task ReloadAccountsPreservesSurvivingSettingsIncludingShareChoiceByAccountIdentity()
    {
        var original = Accounts(3);
        var gateway = new FakeBatchGateway();
        var vm = Create(original, ready: 3, gateway: gateway);
        vm.SelectMode(TransferMode.Replication);
        foreach (var account in vm.Accounts)
        {
            account.IsSelected = true;
            account.TargetSuccessCount = 1;
        }
        var first = vm.Accounts.Single(account => account.AccountId == original[0].AccountId);
        var third = vm.Accounts.Single(account => account.AccountId == original[2].AccountId);
        first.ShareEnabled = false;
        first.DefaultTargetPath = "/A";
        third.ShareEnabled = true;
        third.DefaultTargetPath = "/C";
        await vm.PrepareConfirmationAsync();
        vm.IsFinalConfirmationChecked = true;
        Assert.Equal("preview-token", vm.ConfirmationToken);
        Assert.Equal(9, vm.ConfirmedOperationCount);

        var added = Account("M");
        vm.ReloadAccounts(
        [
            original[2] with { DisplayName = "A-刷新", ShareEnabled = false },
            added,
            original[0] with { DisplayName = "Z-刷新", ShareEnabled = true },
        ]);

        Assert.DoesNotContain(vm.Accounts, account => account.AccountId == original[1].AccountId);
        first = vm.Accounts.Single(account => account.AccountId == original[0].AccountId);
        third = vm.Accounts.Single(account => account.AccountId == original[2].AccountId);
        var newAccount = vm.Accounts.Single(account => account.AccountId == added.AccountId);
        Assert.True(first.IsSelected);
        Assert.False(first.ShareEnabled);
        Assert.Equal("/A", first.DefaultTargetPath);
        Assert.True(third.IsSelected);
        Assert.True(third.ShareEnabled);
        Assert.Equal("/C", third.DefaultTargetPath);
        Assert.False(newAccount.IsSelected);
        Assert.True(newAccount.ShareEnabled);
        Assert.Equal(6, vm.OperationCount);
        Assert.Null(vm.ConfirmationToken);
        Assert.False(vm.IsFinalConfirmationChecked);
        Assert.Equal(0, vm.ConfirmedOperationCount);

        newAccount.IsSelected = true;
        await vm.PrepareConfirmationAsync();

        Assert.Equal(2, gateway.PreviewCalls);
        Assert.Equal(9, vm.ConfirmedOperationCount);
        var refreshedDraft = Assert.IsType<WizardBatchDraft>(gateway.LastPreviewDraft);
        Assert.False(refreshedDraft.Accounts.Single(account => account.AccountId == first.AccountId).ShareEnabled);
        Assert.True(refreshedDraft.Accounts.Single(account => account.AccountId == newAccount.AccountId).IsSelected);
        Assert.True(refreshedDraft.Accounts.Single(account => account.AccountId == newAccount.AccountId).ShareEnabled);
    }

    [Fact]
    public void ReloadToOneAccountDefaultsSelectionButStillRequiresFreshConfirmation()
    {
        var original = Accounts(2);
        var source = new FakePageSource([], 4, null);
        var vm = Create(original, ready: 4, pageSource: source);

        vm.ReloadAccounts([original[0]]);

        var remaining = Assert.Single(vm.Accounts);
        Assert.True(remaining.IsSelected);
        Assert.Equal(TransferMode.Distribution, vm.TransferMode);
        Assert.Equal(4, remaining.TargetItemCount);
        Assert.Contains("已自动选择唯一可用账号", vm.AccountSelectionNotice);
        Assert.False(vm.CanCreateJob);
        Assert.Empty(source.Requests);
    }

    [Fact]
    public async Task ShareEnabledDraftIsBlockedIndependentlyUntilShareCapabilityIsAccepted()
    {
        var vm = new CreateJobWizardViewModel(
            Accounts(1), new FakePageSource([], 4, null), new FakeBatchGateway(), new FakeDialogService(),
            resultCapabilities: new TransferOnlyCapabilities());
        vm.Initialize(new(Guid.NewGuid(), "任务", 4, false, 0, 0, 0, "/资源"));

        Assert.False(vm.CanContinue);
        Assert.Contains("分享功能尚未验证", vm.ValidationMessage);
        Assert.Equal("分享功能尚未验证", vm.ShareCapabilityStatusMessage);

        vm.Accounts[0].ShareEnabled = false;
        await vm.PrepareConfirmationAsync();

        Assert.NotNull(vm.ConfirmationToken);
    }

    [Fact]
    public async Task ReloadClearsHiddenStalePrecheckBeforeBulkPrecheckIsEnabledAgain()
    {
        var original = Accounts(2);
        var vm = Create(original, ready: 2);
        vm.SelectMode(TransferMode.Distribution);
        foreach (var account in vm.Accounts)
        {
            account.IsSelected = true;
            account.TargetSuccessCount = 1;
        }
        vm.PrecheckAccountId = original[1].AccountId;
        await vm.PrepareConfirmationAsync();
        Assert.Equal("preview-token", vm.ConfirmationToken);

        vm.ReloadAccounts([original[0]]);

        Assert.Null(vm.PrecheckAccountId);
        Assert.Null(vm.ConfirmationToken);
        vm.UseBulkPrecheck = true;
        Assert.Equal(original[0].AccountId, vm.PrecheckAccountId);
    }

    [Fact]
    public async Task AccountReloadMakesAnInflightConfirmationStale()
    {
        var accounts = Accounts(2);
        var gateway = new DelayedBatchGateway();
        var vm = Create(accounts, ready: 2, gateway: gateway);
        vm.SelectMode(TransferMode.Replication);
        foreach (var account in vm.Accounts)
        {
            account.IsSelected = true;
            account.TargetSuccessCount = 1;
        }

        var preparing = vm.PrepareConfirmationAsync();
        await gateway.Started.Task;
        vm.ReloadAccounts([accounts[0]]);
        gateway.Complete();
        await preparing;

        Assert.Null(vm.ConfirmationToken);
        Assert.False(vm.IsFinalConfirmationChecked);
        Assert.Equal(2, vm.OperationCount);
        Assert.Equal(0, vm.ConfirmedOperationCount);
    }

    [Fact]
    public void ReloadAccountsRejectsUnboundedInput()
    {
        var vm = Create([], ready: 1);

        Assert.Throws<ArgumentOutOfRangeException>(() => vm.ReloadAccounts(Accounts(1_001)));
    }

    [Fact]
    public void ReloadAccountsRejectsMissingSelectionDecisionWithoutReplacingExistingSnapshot()
    {
        var original = Accounts(2);
        var vm = Create(original, ready: 1);
        var missing = Account("C") with { SelectionDecision = BatchAccountSelectionDecision.Unspecified };

        Assert.Throws<ArgumentException>(() => vm.ReloadAccounts([missing]));

        Assert.Equal(original.Select(account => account.AccountId), vm.Accounts.Select(account => account.AccountId));
    }

    private static CreateJobWizardViewModel Create(
        IReadOnlyList<WizardAccountOption> accounts,
        int ready,
        bool hasExcelAccounts = false,
        int unresolvedMappings = 0,
        int unresolvedRows = 0,
        IImportPreviewPageSource? pageSource = null,
        IWizardBatchService? gateway = null)
    {
        var vm = new CreateJobWizardViewModel(
            accounts,
            pageSource ?? new FakePageSource([], ready, null),
            gateway ?? new FakeBatchGateway(),
            new FakeDialogService());
        vm.Initialize(new ImportWizardContext(
            Guid.NewGuid(), "测试任务", ready, hasExcelAccounts, unresolvedMappings,
            unresolvedRows, 0, "/我的资源"));
        return vm;
    }

    private static WizardAccountOption[] Accounts(int count) =>
        Enumerable.Range(0, count).Select(i => Account(((char)('A' + i)).ToString())).ToArray();

    private static WizardAccountOption Account(
        string name,
        string status = "Active",
        bool enabled = true,
        bool deleted = false,
        int locked = 0,
        int exclusive = 0,
        int publicCandidates = 0,
        int original = 0,
        int effective = 0,
        int reachable = 0,
        int fulfilled = 0,
        int reserved = 0,
        int unused = 0) =>
        new(Guid.NewGuid(), name, status, enabled, deleted, 2_000_000_000, 500_000_000,
            locked, exclusive, publicCandidates, unused, original, effective, reachable, fulfilled, reserved,
            BatchAccountSelectionDecision.NotSelected);

    private static WizardAccountOption AccountWithCapacity(string name, long remaining) =>
        new(Guid.NewGuid(), name, "Active", true, false, remaining, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, BatchAccountSelectionDecision.NotSelected);

    private static WizardAccountConfirmation Confirmation(Guid accountId) =>
        new(accountId, 1, 1, 1, 0, 0, 0, 0, 2, 0,
            PlannedTransferCount: 2, PlannedShareCount: 2,
            ConfirmedTransferCount: 2, ConfirmedShareCount: 2);

    private static string FindRepositoryFile(params string[] segments)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "BaiduBatchTransfer.sln"))) continue;
            return Path.Combine([directory.FullName, .. segments]);
        }

        throw new DirectoryNotFoundException("无法定位仓库根目录。");
    }

    private static void AssertNoPreviewDerivedState(
        CreateJobWizardViewModel vm,
        AccountAllocationRowViewModel row)
    {
        Assert.False(row.HasCurrentConfirmation);
        Assert.Equal(0, row.OriginalTargetSuccessCount);
        Assert.Equal(0, row.EffectiveTargetSuccessCount);
        Assert.Equal(0, row.ReachableTargetSuccessCount);
        Assert.Equal(0, row.FulfilledCount);
        Assert.Equal(0, row.ReservedSlotCount);
        Assert.Equal(0, row.LockedItemCount);
        Assert.Equal(0, row.ExclusiveCandidateCount);
        Assert.Equal(0, row.PublicCandidateCount);
        Assert.Equal(0, row.UnusedExclusiveCandidateCount);
        Assert.Equal(0, row.PlannedTransferCount);
        Assert.Equal(0, row.PlannedShareCount);
        Assert.Equal(0, row.ConfirmedTransferCount);
        Assert.Equal(0, row.ConfirmedShareCount);
        Assert.Equal(0, row.ShortfallCount);
        Assert.Empty(vm.WarningMessage);
        Assert.Null(vm.ConfirmationToken);
    }

    private static ImportPreviewRow Row(int number) =>
        new(Guid.NewGuid(), number, $"https://pan.baidu.com/s/{number}", null, null, "就绪", null);

    private sealed class FakePageSource(
        IReadOnlyList<ImportPreviewRow> items,
        long total,
        long? next) : IImportPreviewPageSource
    {
        public List<ImportPreviewPageRequest> Requests { get; } = [];
        public Task<ImportPreviewPage> LoadAsync(ImportPreviewPageRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new ImportPreviewPage(items, total, next));
        }
    }

    private sealed class DelayedPageSource : IImportPreviewPageSource
    {
        private readonly TaskCompletionSource<ImportPreviewPage> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ImportPreviewPage> LoadAsync(ImportPreviewPageRequest request, CancellationToken cancellationToken) =>
            completion.Task.WaitAsync(cancellationToken);

        public void Complete(ImportPreviewPage page) => completion.TrySetResult(page);
    }

    private sealed class FakeBatchGateway : IWizardBatchService
    {
        public int PreviewCalls { get; private set; }
        public string? ConfirmedToken { get; private set; }
        public bool ThrowDiskShortage { get; set; }
        public Exception? PreviewException { get; init; }
        public Exception? ConfirmException { get; set; }
        public WizardConfirmationSummary? Summary { get; init; }
        public WizardBatchDraft? LastPreviewDraft { get; private set; }

        public Task<WizardConfirmationSummary> PreviewAsync(WizardBatchDraft draft, CancellationToken cancellationToken)
        {
            PreviewCalls++;
            LastPreviewDraft = draft;
            if (ThrowDiskShortage) throw new StorageCapacityException(10, 1);
            if (PreviewException is not null) throw PreviewException;
            if (Summary is not null) return Task.FromResult(Summary);
            var selected = draft.Accounts.Where(account => account.IsSelected).ToArray();
            var transferCounts = selected.Select((account, index) =>
                draft.TransferMode == TransferMode.Replication
                    ? (long)draft.ReadyResourceCount
                    : index == 0 ? (long)draft.ReadyResourceCount : 0L).ToArray();
            var accountConfirmations = selected.Select((account, index) => new WizardAccountConfirmation(
                account.AccountId, account.TargetSuccessCount, account.TargetSuccessCount,
                account.TargetSuccessCount, 0, 0, 0, 0, draft.ReadyResourceCount, 0,
                transferCounts[index], account.ShareEnabled ? transferCounts[index] : 0,
                transferCounts[index], account.ShareEnabled ? transferCounts[index] : 0,
                account.ShareEnabled)).ToArray();
            var transferCount = transferCounts.Sum();
            var shareCount = accountConfirmations.Sum(account => account.ConfirmedShareCount ?? 0);
            return Task.FromResult(new WizardConfirmationSummary(
                draft.ReadyResourceCount,
                transferCount,
                "preview-token",
                true,
                accountConfirmations,
                TransferCount: transferCount,
                ShareCount: shareCount,
                TotalActionCount: checked(transferCount + shareCount),
                ResultColumnCount: checked(2 + selected.Length * 3)));
        }

        public Task<Guid> ConfirmAsync(WizardBatchDraft draft, string confirmationToken, CancellationToken cancellationToken)
        {
            if (ConfirmException is not null) throw ConfirmException;
            ConfirmedToken = confirmationToken;
            return Task.FromResult(draft.BatchJobId);
        }
    }

    private sealed class DelayedBatchGateway : IWizardBatchService
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<WizardConfirmationSummary> PreviewAsync(
            WizardBatchDraft draft,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return new(draft.ReadyResourceCount, draft.ReadyResourceCount, "stale-token", true);
        }

        public Task<Guid> ConfirmAsync(
            WizardBatchDraft draft,
            string confirmationToken,
            CancellationToken cancellationToken) => Task.FromResult(draft.BatchJobId);

        public void Complete() => release.TrySetResult();
    }

    private sealed class FakeDialogService : IDialogService
    {
        public bool ConfirmationResult { get; set; } = true;
        public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default) =>
            Task.FromResult(ConfirmationResult);

        public Task ShowMessageAsync(string title, string message, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<string?> PickExcelFileAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
    }

    private sealed class FakeDraftWorkflow : IWizardDraftWorkflow
    {
        public TransferMode? LastMode { get; private set; }
        public bool IgnoreCalled { get; private set; }
        public bool AutoMapCalled { get; private set; }
        public Task<WizardDraftMutation> SwitchModeAsync(Guid sessionId, TransferMode mode, CancellationToken cancellationToken)
        {
            LastMode = mode;
            return Task.FromResult(new WizardDraftMutation(10, 10, 0, 0, 0, 0, 0));
        }
        public Task<WizardDraftMutation> IgnoreAllAccountNamesAsync(Guid sessionId, TransferMode mode, CancellationToken cancellationToken)
        {
            IgnoreCalled = true;
            return Task.FromResult(new WizardDraftMutation(10, 10, 0, 0, 0, 0, 0));
        }
        public Task<WizardDraftMutation> AutoMapUniqueAccountNamesAsync(Guid sessionId, TransferMode mode, CancellationToken cancellationToken)
        {
            AutoMapCalled = true;
            return Task.FromResult(new WizardDraftMutation(10, 10, 0, 0, 0, 0, 0, 0));
        }
        public Task<WizardDraftMutation> OverrideAllAccountsAsync(Guid sessionId, Guid accountId, TransferMode mode, CancellationToken cancellationToken) =>
            Task.FromResult(new WizardDraftMutation(10, 10, 0, 0, 0, 0, 0));
    }

    private sealed class DelayedDraftWorkflow : IWizardDraftWorkflow
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<WizardDraftMutation> SwitchModeAsync(
            Guid sessionId, TransferMode mode, CancellationToken cancellationToken) => WaitAsync(cancellationToken);

        public Task<WizardDraftMutation> IgnoreAllAccountNamesAsync(
            Guid sessionId, TransferMode mode, CancellationToken cancellationToken) => WaitAsync(cancellationToken);

        public Task<WizardDraftMutation> AutoMapUniqueAccountNamesAsync(
            Guid sessionId, TransferMode mode, CancellationToken cancellationToken) => WaitAsync(cancellationToken);

        public Task<WizardDraftMutation> OverrideAllAccountsAsync(
            Guid sessionId, Guid accountId, TransferMode mode, CancellationToken cancellationToken) => WaitAsync(cancellationToken);

        public void Complete() => release.TrySetResult();

        private async Task<WizardDraftMutation> WaitAsync(CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return new WizardDraftMutation(99, 99, 0, 0, 0, 0, 0);
        }
    }

    private sealed class FakePrecheckWorkflow : IWizardPrecheckWorkflow
    {
        public List<Guid> Accounts { get; } = [];
        public Task<WizardPrecheckResult> RunAsync(Guid sessionId, Guid accountId, CancellationToken cancellationToken)
        {
            Accounts.Add(accountId);
            return Task.FromResult(new WizardPrecheckResult(
                Accounts.Count == 1 ? WizardPrecheckStatus.PausedForVerification : WizardPrecheckStatus.Completed,
                5));
        }
    }

    private sealed class DelayedPrecheckWorkflow : IWizardPrecheckWorkflow
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<WizardPrecheckResult> RunAsync(
            Guid sessionId,
            Guid accountId,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return new(WizardPrecheckStatus.Completed, 10);
        }

        public void Complete() => release.TrySetResult();
    }

    private sealed class TransferOnlyCapabilities : IJobResultCapabilityAvailability
    {
        public bool CanUseTransferActions => true;
        public bool CanUseShareActions => false;
        public string TransferStatusMessage => string.Empty;
        public string ShareStatusMessage => "分享功能尚未验证";
    }
}
