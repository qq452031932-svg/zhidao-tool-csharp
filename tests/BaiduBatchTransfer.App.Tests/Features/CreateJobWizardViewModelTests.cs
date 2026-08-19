using BaiduBatchTransfer.App.Features.Jobs;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class CreateJobWizardViewModelTests
{
    [Fact]
    public void AdaptiveStepsKeepPrecheckVisibleButSkipItWhenDisabled()
    {
        var vm = Create(Accounts(2), ready: 100);

        Assert.Equal(["导入数据", "批量预检", "选择目录", "分配账号", "确认任务"], vm.Steps.Select(x => x.Title));
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
    public void TaskQuotaMustEqualReadyResourcesAndCoverLockedAssignments()
    {
        var vm = Create(
            [Account("A", locked: 30), Account("B")],
            ready: 100);
        vm.SelectMode(TransferMode.Distribution);
        vm.QuotaBasis = QuotaBasis.TaskCount;
        vm.Accounts[0].IsSelected = vm.Accounts[1].IsSelected = true;
        vm.Accounts[0].TargetItemCount = 20;
        vm.Accounts[1].TargetItemCount = 80;

        Assert.False(vm.CanContinue);
        Assert.Contains("锁定任务 30 条", vm.ValidationMessage);

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
    public void SuccessTargetsExposeReserveAndPerAccountReachabilityWarnings()
    {
        var vm = Create(
            [Account("A", exclusive: 8, original: 10, effective: 8, reachable: 8, fulfilled: 2, reserved: 1, unused: 3),
             Account("B", publicCandidates: 12, original: 10, effective: 10, reachable: 10)],
            ready: 20);
        vm.SelectMode(TransferMode.Distribution);
        vm.QuotaBasis = QuotaBasis.SuccessTarget;
        foreach (var account in vm.Accounts) account.IsSelected = true;
        vm.Accounts[0].TargetSuccessCount = 10;
        vm.Accounts[1].TargetSuccessCount = 10;

        Assert.Equal(0, vm.ReserveCandidateCount);
        Assert.Contains("没有候补资源", vm.WarningMessage);
        Assert.Contains("A 的可达目标为 8", vm.WarningMessage);
        Assert.Equal(2, vm.Accounts[0].FulfilledCount);
        Assert.Equal(1, vm.Accounts[0].ReservedSlotCount);
        Assert.Equal(3, vm.Accounts[0].UnusedExclusiveCandidateCount);
        Assert.Equal(2, vm.Accounts[0].ShortfallCount);
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
        vm.IsFinalConfirmationChecked = true;
        await vm.CreateJobAsync();

        Assert.Equal("preview-token", gateway.ConfirmedToken);
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
            locked, exclusive, publicCandidates, unused, original, effective, reachable, fulfilled, reserved);

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

    private sealed class FakeBatchGateway : IWizardBatchService
    {
        public int PreviewCalls { get; private set; }
        public string? ConfirmedToken { get; private set; }
        public bool ThrowDiskShortage { get; init; }

        public Task<WizardConfirmationSummary> PreviewAsync(WizardBatchDraft draft, CancellationToken cancellationToken)
        {
            PreviewCalls++;
            if (ThrowDiskShortage) throw new StorageCapacityException(10, 1);
            return Task.FromResult(new WizardConfirmationSummary(
                draft.ReadyResourceCount,
                draft.TransferMode == TransferMode.Replication
                    ? draft.ReadyResourceCount * draft.Accounts.Count
                    : draft.ReadyResourceCount,
                "preview-token",
                true));
        }

        public Task ConfirmAsync(WizardBatchDraft draft, string confirmationToken, CancellationToken cancellationToken)
        {
            ConfirmedToken = confirmationToken;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDialogService : IDialogService
    {
        public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task ShowMessageAsync(string title, string message, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public string? PickExcelFile() => null;
    }
}
