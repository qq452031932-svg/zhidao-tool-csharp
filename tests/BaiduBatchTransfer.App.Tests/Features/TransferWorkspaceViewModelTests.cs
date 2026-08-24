using System.Runtime.CompilerServices;
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
            IWizardPrecheckWorkflow? precheckWorkflow)
        {
            Ready = ready;
            Events = [];
            ImportWorkflow = new FakeImportWorkflow(Events);
            var pageSource = new FakePageSource();
            Import = new ImportViewModel(
                ImportWorkflow, pageSource, new FakeDialog(), new AvailableAdapter());
            BatchService = new FakeBatchService(Events);
            Wizard = new CreateJobWizardViewModel(
                Enumerable.Range(1, accountCount).Select(index => Account($"账号 {index}")).ToArray(),
                pageSource,
                BatchService,
                new FakeDialog(),
                new AvailableAdapter(),
                new FakeDraftWorkflow(ready),
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
            ViewModel = new TransferWorkspaceViewModel(
                Import,
                Wizard,
                RunPanel,
                new FakeResultSaveDialog(),
                DestinationSink,
                (batch, _) =>
                {
                    Events.Add("handoff");
                    HandedOffBatches.Add(batch);
                    return Task.CompletedTask;
                });
        }

        public int Ready { get; }
        public List<string> Events { get; }
        public FakeImportWorkflow ImportWorkflow { get; }
        public ImportViewModel Import { get; }
        public FakeBatchService BatchService { get; }
        public CreateJobWizardViewModel Wizard { get; }
        public WorkspaceRunPanelViewModel RunPanel { get; }
        public RecordingDestinationSink DestinationSink { get; }
        public List<Guid> HandedOffBatches { get; } = [];
        public TransferWorkspaceViewModel ViewModel { get; }

        public static Fixture Create(
            int accountCount = 1,
            int ready = 4,
            WizardPrecheckResult? precheck = null,
            IWizardPrecheckWorkflow? precheckWorkflow = null) =>
            new(accountCount, ready, precheck, precheckWorkflow);

        public static Fixture Valid(
            int accountCount = 1,
            int ready = 4,
            WizardPrecheckResult? precheck = null,
            IWizardPrecheckWorkflow? precheckWorkflow = null)
        {
            var fixture = Create(accountCount, ready, precheck, precheckWorkflow);
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

        public void ApplyReadyDraft() => ViewModel.ApplyImportedDraft(new(
            Guid.NewGuid(), new(Ready, Ready, 0, 0, 0)));

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
        public Task<ImportWorkflowResult> ImportTextAsync(Guid sessionId, string text, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ImportWorkflowResult> ImportExcelAsync(Guid sessionId, string filePath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ImportWorkflowResult> ResumeAsync(Guid sessionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetExcludedAsync(Guid rowId, bool excluded, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task UpdateLinkAsync(Guid rowId, string shareUrl, string? extractionCode, CancellationToken cancellationToken)
        {
            events.Add("save");
            SavedUrl = shareUrl;
            SavedCode = extractionCode;
            return Task.CompletedTask;
        }
    }

    private sealed class FakePageSource : IImportPreviewPageSource
    {
        public Task<ImportPreviewPage> LoadAsync(ImportPreviewPageRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new ImportPreviewPage([], 0, null));
    }

    private sealed class FakeBatchService(List<string> events) : IWizardBatchService
    {
        public int PreviewCalls { get; private set; }
        public int ShareOperationCount { get; private set; }
        public WizardBatchDraft? ConfirmedDraft { get; private set; }

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

        public Task ConfirmAsync(WizardBatchDraft draft, string confirmationToken, CancellationToken cancellationToken)
        {
            events.Add("confirm");
            ConfirmedDraft = draft;
            return Task.CompletedTask;
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
        private WizardDraftMutation Mutation => new(ready, ready, 0, 0, 0, 0, 0);
        public Task<WizardDraftMutation> SwitchModeAsync(Guid sessionId, TransferMode mode, CancellationToken cancellationToken) => Task.FromResult(Mutation);
        public Task<WizardDraftMutation> AutoMapUniqueAccountNamesAsync(Guid sessionId, TransferMode mode, CancellationToken cancellationToken) => Task.FromResult(Mutation);
        public Task<WizardDraftMutation> IgnoreAllAccountNamesAsync(Guid sessionId, TransferMode mode, CancellationToken cancellationToken) => Task.FromResult(Mutation);
        public Task<WizardDraftMutation> OverrideAllAccountsAsync(Guid sessionId, Guid accountId, TransferMode mode, CancellationToken cancellationToken) => Task.FromResult(Mutation);
    }

    private sealed class RecordingDestinationSink(List<string> events) : ITransferResultDestinationSink
    {
        public List<TransferResultDestination> Values { get; } = [];
        public Task SetAsync(TransferResultDestination destination, CancellationToken cancellationToken = default)
        {
            events.Add("sink");
            Values.Add(destination);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDialog : IDialogService
    {
        public Task<string?> PickExcelFileAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default) => Task.FromResult(true);
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
