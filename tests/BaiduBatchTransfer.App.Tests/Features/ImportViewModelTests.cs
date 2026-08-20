using BaiduBatchTransfer.App.Features.Imports;
using BaiduBatchTransfer.App.Features.Jobs;
using BaiduBatchTransfer.App.Services;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class ImportViewModelTests
{
    [Fact]
    public async Task TextAndExcelImportsRunAsynchronouslyAndCancellationLeavesAResumableDraft()
    {
        var workflow = new FakeImportWorkflow { Cancel = true };
        var vm = Create(workflow);
        vm.TextContent = "https://pan.baidu.com/s/abc1";

        await vm.ImportTextAsync();

        Assert.True(vm.CanResumeImport);
        Assert.NotNull(vm.SessionId);
        Assert.Equal("导入已取消，已保存的进度可以继续。", vm.StatusMessage);

        workflow.Cancel = false;
        await vm.ImportExcelAsync("D:\\data.xlsx");
        Assert.Equal(1, workflow.ExcelCalls);
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
    public void StatisticsExposeReadyDuplicateInvalidExcludedAndTotalCounts()
    {
        var vm = Create(new FakeImportWorkflow());
        vm.ApplyStatistics(new ImportDraftStatistics(100, 80, 10, 5, 5));

        Assert.Equal("总计 100 · 就绪 80 · 重复 10 · 异常 5 · 已排除 5", vm.StatisticsText);
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
        public string? PickExcelFile() => excelFile;
    }

    private sealed class TransferUnavailable : IAdapterAvailability
    {
        public bool CanStartTransfers => false;
        public string StatusMessage => "实际转存尚未开放。";
    }
}
