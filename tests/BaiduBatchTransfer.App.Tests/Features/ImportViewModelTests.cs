using BaiduBatchTransfer.App.Features.Imports;
using BaiduBatchTransfer.App.Features.Jobs;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Application.Imports;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Infrastructure.Database;
using BaiduBatchTransfer.Infrastructure.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class ImportViewModelTests
{
    [Fact]
    public void ImportWorkflowExceptionToStringDoesNotExposeInnerPathOrContent()
    {
        var error = new ImportWorkflowException(
            ImportUiError.InvalidWorkbook,
            new IOException(@"C:\private\secret-workbook.xlsx contains https://pan.baidu.com/s/private"));

        Assert.DoesNotContain("secret-workbook", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pan.baidu.com", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExcelMappingViewModelSupportsManualRemapAndRequiresOneLinkColumn()
    {
        var inspection = new TabularWorkbookInspection(
            1,
            [new(0, "名称"), new(1, "未知列")],
            [new(2, ["课程", "https://pan.baidu.com/s/id"])]);
        var vm = new ExcelColumnMappingViewModel(
            inspection,
            new ExcelColumnSuggestionEngine().Suggest(inspection));

        vm.Columns[0].Role = TabularImportColumnRole.Preserve;
        vm.Columns[1].Role = TabularImportColumnRole.ShareUrl;
        Assert.True(vm.TryConfirm());
        Assert.Equal(1, vm.ConfirmedMapping!.ShareUrlColumnIndex);

        vm.Columns[1].Role = TabularImportColumnRole.Preserve;
        Assert.False(vm.TryConfirm());
        Assert.Equal("必须且只能选择一列作为分享链接。", vm.ValidationMessage);
    }

    [Fact]
    public void ExcelMappingViewModelRebuildsHeadersPreviewAndFingerprintForHeaderOverride()
    {
        var inspection = new TabularWorkbookInspection(
            1,
            [new(0, "原表头"), new(1, "分享链接"), new(2, null)],
            [new(2, ["课程", "https://pan.baidu.com/s/id", "保留值"])],
            observedRows:
            [
                new(1, ["  原表头  ", "分享链接", ""]),
                new(2, ["课程", "https://pan.baidu.com/s/id", "保留值"]),
            ]);
        var suggestion = new ExcelColumnSuggestionEngine().Suggest(inspection);
        var vm = new ExcelColumnMappingViewModel(inspection, suggestion);

        Assert.True(vm.TryConfirm());
        var headerMapping = vm.ConfirmedMapping!;
        Assert.Equal("  原表头  ", headerMapping.SourceColumns[0].Header);
        Assert.Equal("第 3 列", headerMapping.SourceColumns[2].Header);
        Assert.Equal(2, Assert.Single(vm.PreviewRows).SourceRowNumber);

        vm.FirstRowIsHeader = false;
        Assert.Null(vm.ConfirmedMapping);
        Assert.Equal("第 1 列", vm.Columns[0].Header);
        Assert.Equal(1, vm.PreviewRows[0].SourceRowNumber);
        Assert.True(vm.TryConfirm());
        var headerlessMapping = vm.ConfirmedMapping!;
        Assert.Null(headerlessMapping.HeaderRowNumber);
        Assert.Equal("第 1 列", headerlessMapping.SourceColumns[0].Header);
        Assert.NotEqual(headerMapping.Fingerprint, headerlessMapping.Fingerprint);

        vm.FirstRowIsHeader = true;
        Assert.Equal("  原表头  ", vm.Columns[0].Header);
        Assert.True(vm.TryConfirm());
        Assert.Equal("  原表头  ", vm.ConfirmedMapping!.SourceColumns[0].Header);
        Assert.Equal(headerMapping.Fingerprint, vm.ConfirmedMapping.Fingerprint);
    }

    [Fact]
    public void ExcelMappingDisplaysExcelLettersInsteadOfZeroBasedSourceIndexes()
    {
        var columns = Enumerable.Range(0, 27).Select(index => new TabularInspectionColumn(index, null)).ToArray();
        var suggestion = new ImportColumnMappingSuggestion(
            null,
            columns.Select(column => new TabularImportColumnMapping(
                column.SourceColumnIndex,
                column.SourceColumnIndex == 0 ? TabularImportColumnRole.ShareUrl : TabularImportColumnRole.Preserve)));
        var vm = new ExcelColumnMappingViewModel(new TabularWorkbookInspection(null, columns, []), suggestion);

        Assert.Equal("A", vm.Columns[0].ColumnLabel);
        Assert.Equal("Z", vm.Columns[25].ColumnLabel);
        Assert.Equal("AA", vm.Columns[26].ColumnLabel);
        var xamlPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "BaiduBatchTransfer.App", "Features", "Imports", "ExcelColumnMappingWindow.xaml"));
        var xaml = File.ReadAllText(xamlPath);
        Assert.Contains("Binding=\"{Binding ColumnLabel}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Binding=\"{Binding SourceColumnIndex}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationBeforeSessionCommitDoesNotClaimAResumableDraft()
    {
        var workflow = new FakeImportWorkflow { Cancel = true };
        var vm = Create(workflow);
        vm.TextContent = "https://pan.baidu.com/s/abc1";

        await vm.ImportTextAsync();

        Assert.False(vm.CanResumeImport);
        Assert.Null(vm.SessionId);
        Assert.Equal("导入已取消，尚未保存可继续的草稿。", vm.StatusMessage);

        workflow.Cancel = false;
        await vm.ImportExcelAsync("D:\\data.xlsx");
        Assert.Equal(1, workflow.ExcelCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureAfterDurableSessionEnablesResume(bool reportCommittedChunk)
    {
        var workflow = new FailingAfterProgressWorkflow(reportCommittedChunk);
        var vm = Create(workflow);
        vm.TextContent = "https://pan.baidu.com/s/abc1";

        await vm.ImportTextAsync();

        Assert.True(vm.CanResumeImport);
        Assert.NotNull(vm.SessionId);
        Assert.Equal("导入中断，已保存的进度可以继续。", vm.StatusMessage);
    }

    [Fact]
    public async Task NewPrecommitFailureClearsAnOlderResumableSessionAndReplaySource()
    {
        var workflow = new DurableThenPrecommitFailureWorkflow();
        var vm = Create(workflow);
        vm.TextContent = "https://pan.baidu.com/s/abc1";
        await vm.ImportTextAsync();
        var oldSessionId = Assert.IsType<Guid>(vm.SessionId);
        Assert.True(vm.CanResumeImport);

        await vm.ImportTextAsync();

        Assert.False(vm.CanResumeImport);
        Assert.Null(vm.SessionId);
        Assert.Equal([oldSessionId], workflow.DiscardedSessionIds);
        Assert.Equal("导入失败，请检查文件后重试。", vm.StatusMessage);
    }

    [Theory]
    [InlineData(false, "导入失败，请检查文件后重试。")]
    [InlineData(true, "导入已取消，草稿已保存，但没有可继续的解析进度。")]
    public async Task PostStagingFailureWithoutReplayDoesNotClaimResume(
        bool cancellation,
        string expectedMessage)
    {
        var vm = Create(new PostStagingFailureWorkflow(cancellation));
        vm.TextContent = "https://pan.baidu.com/s/abc1";

        await vm.ImportTextAsync();

        Assert.False(vm.CanResumeImport);
        Assert.NotNull(vm.SessionId);
        Assert.Equal(expectedMessage, vm.StatusMessage);
    }

    [Theory]
    [InlineData(ImportUiError.InvalidWorkbook, "Excel 文件无法读取，请确认文件未损坏且格式为 .xlsx。")]
    [InlineData(ImportUiError.UnsafeWorkbook, "Excel 文件包含异常内容，为保护本机已停止导入。")]
    [InlineData(ImportUiError.MissingHeader, "Excel 缺少“分享链接”列，请使用软件提供的模板。")]
    [InlineData(ImportUiError.Unknown, "导入失败，请检查文件后重试。")]
    public async Task ImportErrorsAreMappedToSafeChineseMessages(ImportUiError error, string expected)
    {
        var workflow = new FakeImportWorkflow { Error = error };
        var vm = Create(workflow);

        await vm.ImportExcelAsync("D:\\private-user-name.xlsx");

        Assert.Equal(expected, vm.StatusMessage);
        Assert.DoesNotContain("private-user-name", vm.StatusMessage);
        Assert.DoesNotContain(error.ToString(), vm.StatusMessage);
    }

    [Fact]
    public async Task PreviewAndEditsStayPagedAndEditingLinkInvalidatesPrecheck()
    {
        var workflow = new FakeImportWorkflow();
        var source = new FakePageSource(
            Enumerable.Range(1, 50).Select(i => new ImportPreviewRow(Guid.NewGuid(), i, "链接", null, null, "就绪", "有效")).ToArray(),
            200_000,
            50);
        var vm = Create(workflow, source);
        vm.AttachSession(Guid.NewGuid());

        await vm.LoadFirstPageAsync();
        await vm.UpdateLinkAsync(vm.Rows[0].RowId, "https://pan.baidu.com/s/new1", "a1b2");
        Assert.Equal("预检结果已失效，需要重新预检或按未预检状态继续。", vm.StatusMessage);
        await vm.SetExcludedAsync(vm.Rows[1].RowId, true);

        Assert.Equal(50, vm.Rows.Count);
        Assert.Equal(200_000, vm.TotalRows);
        Assert.InRange(source.LastRequest!.PageSize, 1, 1_000);
        Assert.Equal((vm.Rows[0].RowId, "https://pan.baidu.com/s/new1", "a1b2"), workflow.LastEdit);
        Assert.Equal((vm.Rows[1].RowId, true), workflow.LastExclusion);
        Assert.Equal("已排除该记录。", vm.StatusMessage);
    }

    [Fact]
    public async Task SelectedRowCanBeEditedExcludedAndRestoredFromCommands()
    {
        var row = new ImportPreviewRow(Guid.NewGuid(), 1, "https://pan.baidu.com/s/old", "a1b2", null, "就绪", "有效");
        var workflow = new FakeImportWorkflow();
        var vm = Create(workflow, new FakePageSource([row], 1, null));
        vm.AttachSession(Guid.NewGuid());
        await vm.LoadFirstPageAsync();
        vm.SelectedRow = vm.Rows[0];
        vm.SelectedShareUrl = "https://pan.baidu.com/s/new";
        vm.SelectedExtractionCode = "c3d4";

        await vm.SaveSelectedRowCommand.ExecuteAsync(null);
        await vm.ToggleSelectedExclusionCommand.ExecuteAsync(null);
        await vm.ToggleSelectedExclusionCommand.ExecuteAsync(null);

        Assert.Equal((row.RowId, "https://pan.baidu.com/s/new", "c3d4"), workflow.LastEdit);
        Assert.Equal([(row.RowId, true), (row.RowId, false)], workflow.Exclusions);
    }

    [Fact]
    public void StatisticsExposeEveryDraftDispositionAndTotalCount()
    {
        var vm = Create(new FakeImportWorkflow());
        vm.ApplyStatistics(new ImportDraftStatistics(100, 65, 10, 5, 5, 8, 7));

        Assert.Equal("总计 100 · 就绪 65 · 重复 10 · 异常 5 · 已排除 5 · 冲突 8 · 待解析 7", vm.StatisticsText);
    }

    [Fact]
    public async Task CommittedProgressUsesExactSafeChineseMessagesAndFailedChunkDoesNotInflateRows()
    {
        var workflow = new ControlledProgressWorkflow();
        var vm = Create(workflow);
        var statuses = new List<string>();
        vm.TextContent = "https://pan.baidu.com/s/abc1";
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ImportViewModel.StatusMessage))
                statuses.Add(vm.StatusMessage);
        };

        var import = vm.ImportTextAsync();
        await workflow.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEqual("正在解析并写入草稿", vm.StatusMessage);

        await Task.Run(() => workflow.Report(
            new ImportCommittedProgress(ImportProgressKind.SessionCreated, 0)));
        Assert.Equal("正在解析并写入草稿", vm.StatusMessage);

        await Task.Run(() => workflow.Report(
            new ImportCommittedProgress(ImportProgressKind.ChunkCommitted, 3)));
        Assert.Equal("正在解析并写入草稿，已保存 3 行", vm.StatusMessage);

        workflow.Fail();
        await import;

        Assert.Contains("正在解析并写入草稿", statuses);
        Assert.Contains("正在解析并写入草稿，已保存 3 行", statuses);
        Assert.DoesNotContain(statuses, status => status.Contains("6 行", StringComparison.Ordinal));
        Assert.All(statuses, status => Assert.DoesNotContain("abc1", status, StringComparison.Ordinal));
        Assert.True(vm.CanResumeImport);
        Assert.Equal("导入中断，已保存的进度可以继续。", vm.StatusMessage);
    }

    [Fact]
    public void LocalImportRemainsEnabledWhileLiveTransferExecutionIsUnavailable()
    {
        var vm = new ImportViewModel(
            new FakeImportWorkflow(),
            new FakePageSource([], 0, null),
            new FakeDialogService(),
            new TransferUnavailable());
        vm.TextContent = "https://pan.baidu.com/s/abc123";

        Assert.True(vm.ImportTextCommand.CanExecute(null));
        Assert.Contains("可以先导入", vm.AdapterStatusMessage);
    }

    [Fact]
    public async Task ExcelSelectionStartsLocalWorkflowWhenTransfersAreUnavailable()
    {
        const string selectedPath = "D:\\imports\\selected.xlsx";
        var workflow = new FakeImportWorkflow();
        var vm = new ImportViewModel(
            workflow,
            new FakePageSource([], 0, null),
            new FakeDialogService(selectedPath),
            new TransferUnavailable());

        await vm.SelectAndImportExcelCommand.ExecuteAsync(null);

        Assert.Equal(1, workflow.ExcelCalls);
        Assert.Equal(selectedPath, workflow.LastExcelFilePath);
        Assert.NotNull(vm.SessionId);
        Assert.Equal("导入完成，请分页检查异常、重复和账号映射。", vm.StatusMessage);
    }

    [Fact]
    public async Task ExcelSelectionWithoutAFileDoesNotStartWorkflow()
    {
        var workflow = new FakeImportWorkflow();
        var vm = Create(workflow);

        await vm.SelectAndImportExcelCommand.ExecuteAsync(null);

        Assert.Equal(0, workflow.ExcelCalls);
        Assert.Null(vm.SessionId);
        Assert.Equal("未选择文件", vm.StatusMessage);
    }

    [Fact]
    public async Task ExcelSelectionReportsSafeStatusSequenceWithoutExposingPath()
    {
        const string selectedPath = "D:\\imports\\selected.xlsx";
        string[] expectedStatuses =
        [
            "请选择 Excel 文件",
            "已选择 Excel 文件，正在检查",
            "导入完成，请分页检查异常、重复和账号映射。",
        ];
        var workflow = new FakeImportWorkflow();
        var vm = new ImportViewModel(
            workflow,
            new FakePageSource([], 0, null),
            new FakeDialogService(selectedPath));
        var statusMessages = new List<string>();
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ImportViewModel.StatusMessage))
            {
                statusMessages.Add(vm.StatusMessage);
            }
        };

        await vm.SelectAndImportExcelCommand.ExecuteAsync(null);

        Assert.Equal(expectedStatuses, statusMessages.Where(expectedStatuses.Contains));
        Assert.All(statusMessages, status => Assert.DoesNotContain(selectedPath, status));
    }

    [Fact]
    public async Task CompletedImportHandsThePersistedSessionAndStatisticsToWizard()
    {
        var import = Create(new FakeImportWorkflow());
        ImportDraftReady? handoff = null;
        import.DraftReady += (_, value) => handoff = value;
        import.TextContent = "https://pan.baidu.com/s/abc1";

        await import.ImportTextAsync();

        Assert.NotNull(handoff);
        Assert.Equal(import.SessionId, handoff.SessionId);
        Assert.Equal(1, handoff.Statistics.Ready);
    }

    [Fact]
    public async Task SuccessfulTextImportDoesNotRetainPastedReplayContent()
    {
        await using var context = await LocalWorkflowTestContext.CreateAsync();
        var sessionId = Guid.NewGuid();

        await context.Workflow.ImportTextAsync(
            sessionId,
            "https://pan.baidu.com/s/text-success",
            CancellationToken.None);

        Assert.False(context.Workflow.HasResumableReplaySource(sessionId));
        var error = await Assert.ThrowsAsync<ImportWorkflowException>(() =>
            context.Workflow.ResumeAsync(sessionId, CancellationToken.None));
        Assert.Equal(ImportUiError.InvalidData, error.Error);
    }

    [Fact]
    public async Task SuccessfulExcelImportDoesNotRetainAbsoluteWorkbookPath()
    {
        await using var context = await LocalWorkflowTestContext.CreateAsync();
        var workbookPath = Path.Combine(context.Root, "private-workbook.xlsx");
        WriteWorkbook(workbookPath, "https://pan.baidu.com/s/excel-success");
        var sessionId = Guid.NewGuid();

        await context.Workflow.ImportExcelAsync(sessionId, workbookPath, CancellationToken.None);

        Assert.False(context.Workflow.HasResumableReplaySource(sessionId));
        var error = await Assert.ThrowsAsync<ImportWorkflowException>(() =>
            context.Workflow.ResumeAsync(sessionId, CancellationToken.None));
        Assert.Equal(ImportUiError.InvalidData, error.Error);
    }

    [Fact]
    public async Task ReplaySourceRemainsAfterDurableCancellationAndIsRemovedAfterResumeSucceeds()
    {
        await using var context = await LocalWorkflowTestContext.CreateAsync();
        var sessionId = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<ImportCommittedProgress>(value =>
        {
            if (value.Kind == ImportProgressKind.SessionCreated) cancellation.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            context.Workflow.ImportTextAsync(
                sessionId,
                "https://pan.baidu.com/s/cancelled",
                progress,
                cancellation.Token));

        Assert.True(context.Workflow.HasResumableReplaySource(sessionId));
        var resumed = await context.Workflow.ResumeAsync(sessionId, CancellationToken.None);
        Assert.Equal(sessionId, resumed.SessionId);
        Assert.False(context.Workflow.HasResumableReplaySource(sessionId));
        await Assert.ThrowsAsync<ImportWorkflowException>(() =>
            context.Workflow.ResumeAsync(sessionId, CancellationToken.None));
    }

    [Fact]
    public async Task ReplaySourceRemainsAfterDurableChunkFailure()
    {
        await using var context = await LocalWorkflowTestContext.CreateAsync(failCommitAttempt: 2);
        var sessionId = Guid.NewGuid();

        await Assert.ThrowsAsync<ImportWorkflowException>(() =>
            context.Workflow.ImportTextAsync(
                sessionId,
                "https://pan.baidu.com/s/failed-chunk",
                CancellationToken.None));

        Assert.True(context.Workflow.HasResumableReplaySource(sessionId));
        var resumed = await context.Workflow.ResumeAsync(sessionId, CancellationToken.None);
        Assert.Equal(sessionId, resumed.SessionId);
    }

    [Fact]
    public async Task ReplaySourceCacheEvictsTheOldestAbandonedDraftAtItsHardBound()
    {
        await using var context = await LocalWorkflowTestContext.CreateAsync();
        var sessions = new List<Guid>();
        for (var index = 0; index < 9; index++)
        {
            var sessionId = Guid.NewGuid();
            sessions.Add(sessionId);
            using var cancellation = new CancellationTokenSource();
            var progress = new InlineProgress<ImportCommittedProgress>(value =>
            {
                if (value.Kind == ImportProgressKind.SessionCreated) cancellation.Cancel();
            });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                context.Workflow.ImportTextAsync(
                    sessionId,
                    $"https://pan.baidu.com/s/abandoned-{index}",
                    progress,
                    cancellation.Token));
        }

        var evicted = await Assert.ThrowsAsync<ImportWorkflowException>(() =>
            context.Workflow.ResumeAsync(sessions[0], CancellationToken.None));
        Assert.Equal(ImportUiError.InvalidData, evicted.Error);
        Assert.False(context.Workflow.HasResumableReplaySource(sessions[0]));
        Assert.True(context.Workflow.HasResumableReplaySource(sessions[^1]));
        var newest = await context.Workflow.ResumeAsync(sessions[^1], CancellationToken.None);
        Assert.Equal(sessions[^1], newest.SessionId);
    }

    private static ImportViewModel Create(
        IImportWorkflow workflow,
        IImportPreviewPageSource? source = null) =>
        new(workflow, source ?? new FakePageSource([], 0, null), new FakeDialogService());

    private sealed class FakeImportWorkflow : IImportWorkflow
    {
        public bool Cancel { get; set; }
        public ImportUiError? Error { get; set; }
        public int ExcelCalls { get; private set; }
        public string? LastExcelFilePath { get; private set; }
        public (Guid, string, string?) LastEdit { get; private set; }
        public (Guid, bool) LastExclusion { get; private set; }
        public List<(Guid, bool)> Exclusions { get; } = [];

        public Task<ImportWorkflowResult> ImportTextAsync(Guid sessionId, string text, CancellationToken cancellationToken)
            => Result();

        public Task<ImportWorkflowResult> ImportExcelAsync(Guid sessionId, string filePath, CancellationToken cancellationToken)
        {
            ExcelCalls++;
            LastExcelFilePath = filePath;
            return Result();
        }

        public Task<ImportWorkflowResult> ResumeAsync(Guid sessionId, CancellationToken cancellationToken) => Result();

        public Task UpdateLinkAsync(Guid rowId, string shareUrl, string? extractionCode, CancellationToken cancellationToken)
        {
            LastEdit = (rowId, shareUrl, extractionCode);
            return Task.CompletedTask;
        }

        public Task SetExcludedAsync(Guid rowId, bool excluded, CancellationToken cancellationToken)
        {
            LastExclusion = (rowId, excluded);
            Exclusions.Add((rowId, excluded));
            return Task.CompletedTask;
        }

        private Task<ImportWorkflowResult> Result()
        {
            if (Cancel) throw new OperationCanceledException();
            if (Error is { } error) throw new ImportWorkflowException(error);
            return Task.FromResult(new ImportWorkflowResult(Guid.NewGuid(), new(1, 1, 0, 0, 0)));
        }
    }

    private sealed class FakePageSource(
        IReadOnlyList<ImportPreviewRow> items,
        long total,
        long? next) : IImportPreviewPageSource
    {
        public ImportPreviewPageRequest? LastRequest { get; private set; }
        public Task<ImportPreviewPage> LoadAsync(ImportPreviewPageRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new ImportPreviewPage(items, total, next));
        }
    }

    private sealed class FakeDialogService(string? excelFile = null) : IDialogService
    {
        public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
        public Task ShowMessageAsync(string title, string message, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task<string?> PickExcelFileAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(excelFile);
        }
    }

    private sealed class ControlledProgressWorkflow : IImportWorkflow
    {
        private readonly TaskCompletionSource<ImportWorkflowResult> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IProgress<ImportCommittedProgress>? progress;

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ImportWorkflowResult> ImportTextAsync(
            Guid sessionId,
            string text,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            return completion.Task;
        }

        public Task<ImportWorkflowResult> ImportTextAsync(
            Guid sessionId,
            string text,
            IProgress<ImportCommittedProgress> committedProgress,
            CancellationToken cancellationToken)
        {
            progress = committedProgress;
            Started.TrySetResult();
            return completion.Task;
        }

        public void Report(ImportCommittedProgress value) =>
            Assert.IsAssignableFrom<IProgress<ImportCommittedProgress>>(progress).Report(value);

        public void Fail() => completion.TrySetException(new ImportWorkflowException(ImportUiError.Unknown));

        public Task<ImportWorkflowResult> ImportExcelAsync(Guid sessionId, string filePath, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ImportWorkflowResult> ResumeAsync(Guid sessionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task UpdateLinkAsync(Guid rowId, string shareUrl, string? extractionCode, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SetExcludedAsync(Guid rowId, bool excluded, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FailingAfterProgressWorkflow(bool reportCommittedChunk) : IImportWorkflow
    {
        public Task<ImportWorkflowResult> ImportTextAsync(
            Guid sessionId,
            string text,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ImportWorkflowResult> ImportTextAsync(
            Guid sessionId,
            string text,
            IProgress<ImportCommittedProgress> committedProgress,
            CancellationToken cancellationToken)
        {
            committedProgress.Report(new(ImportProgressKind.SessionCreated, 0));
            if (reportCommittedChunk)
                committedProgress.Report(new(ImportProgressKind.ChunkCommitted, 3));
            throw new ImportWorkflowException(ImportUiError.Unknown);
        }

        public Task<ImportWorkflowResult> ImportExcelAsync(Guid sessionId, string filePath, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<ImportWorkflowResult> ResumeAsync(Guid sessionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task UpdateLinkAsync(Guid rowId, string shareUrl, string? extractionCode, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task SetExcludedAsync(Guid rowId, bool excluded, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class DurableThenPrecommitFailureWorkflow : IImportWorkflow
    {
        private int callCount;
        public List<Guid> DiscardedSessionIds { get; } = [];

        public Task<ImportWorkflowResult> ImportTextAsync(Guid sessionId, string text, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ImportWorkflowResult> ImportTextAsync(
            Guid sessionId,
            string text,
            IProgress<ImportCommittedProgress> committedProgress,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref callCount) == 1)
                committedProgress.Report(new(ImportProgressKind.SessionCreated, 0));
            throw new ImportWorkflowException(ImportUiError.Unknown);
        }

        public void DiscardReplaySource(Guid sessionId) => DiscardedSessionIds.Add(sessionId);

        public Task<ImportWorkflowResult> ImportExcelAsync(Guid sessionId, string filePath, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<ImportWorkflowResult> ResumeAsync(Guid sessionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task UpdateLinkAsync(Guid rowId, string shareUrl, string? extractionCode, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task SetExcludedAsync(Guid rowId, bool excluded, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class PostStagingFailureWorkflow(bool cancellation) : IImportWorkflow
    {
        public bool HasResumableReplaySource(Guid sessionId) => false;

        public Task<ImportWorkflowResult> ImportTextAsync(Guid sessionId, string text, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ImportWorkflowResult> ImportTextAsync(
            Guid sessionId,
            string text,
            IProgress<ImportCommittedProgress> committedProgress,
            CancellationToken cancellationToken)
        {
            committedProgress.Report(new(ImportProgressKind.SessionCreated, 0));
            if (cancellation) throw new OperationCanceledException();
            throw new ImportWorkflowException(ImportUiError.Unknown);
        }

        public Task<ImportWorkflowResult> ImportExcelAsync(Guid sessionId, string filePath, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<ImportWorkflowResult> ResumeAsync(Guid sessionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task UpdateLinkAsync(Guid rowId, string shareUrl, string? extractionCode, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task SetExcludedAsync(Guid rowId, bool excluded, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class TransferUnavailable : IAdapterAvailability
    {
        public bool CanStartTransfers => false;
        public string StatusMessage => "实际转存尚未开放。";
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class LocalWorkflowTestContext : IAsyncDisposable
    {
        private LocalWorkflowTestContext(string root, LocalImportWorkflow workflow)
        {
            Root = root;
            Workflow = workflow;
        }

        public string Root { get; }
        public LocalImportWorkflow Workflow { get; }

        public static async Task<LocalWorkflowTestContext> CreateAsync(int? failCommitAttempt = null)
        {
            var root = Path.Combine(Path.GetTempPath(), $"bbt-local-import-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var connectionFactory = new SqliteConnectionFactory(Path.Combine(root, "imports.db"));
            await new DatabaseMigrator(
                connectionFactory,
                new DatabaseBackupService(connectionFactory, Path.Combine(root, "backups")),
                "task2-followup-test").MigrateAsync();
            IUnitOfWorkFactory unitOfWorkFactory = new SqliteUnitOfWorkFactory(connectionFactory);
            if (failCommitAttempt is { } attempt)
                unitOfWorkFactory = new FailOnceUnitOfWorkFactory(unitOfWorkFactory, attempt);
            var staging = new ImportStagingService(
                unitOfWorkFactory.BeginAsync,
                new DeferredShareLinkResolver(),
                chunkSize: 1,
                maxResolutionConcurrency: 1);
            var workflow = new LocalImportWorkflow(
                staging,
                new TextImportParser(),
                new OpenXmlImportReader(),
                new OpenXmlWorkbookInspector(),
                new ConfirmingMappingPresenter(),
                new FakeDialogService(),
                new DraftRowEditingService(unitOfWorkFactory),
                unitOfWorkFactory);
            return new(root, workflow);
        }

        public async ValueTask DisposeAsync()
        {
            await Task.Yield();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class ConfirmingMappingPresenter : IExcelColumnMappingPresenter
    {
        public Task<ConfirmedImportColumnMapping?> ConfirmAsync(
            TabularWorkbookInspection inspection,
            CancellationToken cancellationToken = default)
        {
            var suggestion = new ExcelColumnSuggestionEngine().Suggest(inspection);
            return Task.FromResult<ConfirmedImportColumnMapping?>(
                new ConfirmedImportColumnMapping(
                    suggestion.HeaderRowNumber,
                    suggestion.Columns,
                    inspection.Columns));
        }
    }

    private sealed class FailOnceUnitOfWorkFactory(IUnitOfWorkFactory inner, int failCommitAttempt)
        : IUnitOfWorkFactory
    {
        private int commitAttempts;

        public async Task<IUnitOfWork> BeginAsync(CancellationToken cancellationToken = default) =>
            new FailOnceUnitOfWork(
                await inner.BeginAsync(cancellationToken),
                this,
                failCommitAttempt);

        private sealed class FailOnceUnitOfWork(
            IUnitOfWork inner,
            FailOnceUnitOfWorkFactory owner,
            int failCommitAttempt) : IUnitOfWork
        {
            public IImportRepository Imports => inner.Imports;
            public IJobRepository Jobs => inner.Jobs;
            public IAccountRepository Accounts => inner.Accounts;

            public async Task CommitAsync(CancellationToken cancellationToken = default)
            {
                if (Interlocked.Increment(ref owner.commitAttempts) == failCommitAttempt)
                    throw new InvalidOperationException("Injected commit failure.");
                await inner.CommitAsync(cancellationToken);
            }

            public Task RollbackAsync(CancellationToken cancellationToken = default) =>
                inner.RollbackAsync(cancellationToken);

            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    private static void WriteWorkbook(string path, string shareUrl)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        workbookPart.Workbook = new Workbook(new Sheets(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = 1,
            Name = "导入",
        }));
        using var writer = OpenXmlWriter.Create(worksheetPart);
        writer.WriteStartElement(new Worksheet());
        writer.WriteStartElement(new SheetData());
        WriteInlineRow(writer, 1, "分享链接");
        WriteInlineRow(writer, 2, shareUrl);
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteInlineRow(OpenXmlWriter writer, uint rowIndex, params string[] values)
    {
        writer.WriteStartElement(new Row { RowIndex = rowIndex });
        foreach (var value in values)
            writer.WriteElement(new Cell(new InlineString(new Text(value)))
                { DataType = CellValues.InlineString });
        writer.WriteEndElement();
    }
}
