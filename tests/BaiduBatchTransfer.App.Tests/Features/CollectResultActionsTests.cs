using BaiduBatchTransfer.App.Features.Collect;
using BaiduBatchTransfer.App.Features.Imports;
using BaiduBatchTransfer.App.Features.Jobs;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Infrastructure.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class CollectResultActionsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "collect-actions-" + Guid.NewGuid().ToString("N"));
    private static readonly CollectedRow[] Rows =
    [
        new(1, "植物大战僵尸 中文版", "https://pan.baidu.com/s/1ExampleA", "9ha1", "https://zhidao.baidu.com/question/123.html"),
        new(2, "另一个资源", "https://pan.baidu.com/s/1ExampleB", "", "https://zhidao.baidu.com/question/456.html")
    ];

    [Fact]
    public async Task CopiesLinksWithCodesAndExportsWorkbookReadableByTransferImporter()
    {
        var ui = new TestUi { Destination = root };
        using var import = new ImportViewModel(new RecordingWorkflow(), new EmptyPreview(), ui);
        var actions = new CollectResultActions(ui, ui, ui, import, () => true, () => { }, root);
        await actions.CopyLinksAsync(Rows);
        Assert.Equal(Rows[0].Link + " 提取码：9ha1" + Environment.NewLine + Rows[1].Link, ui.Clipboard);
        await actions.ExportAsync(Rows);
        var file = Assert.Single(Directory.GetFiles(root, "*.xlsx"));
        using var document = SpreadsheetDocument.Open(file, false);
        Assert.Empty(document.WorkbookPart!.WorksheetParts.Single().Worksheet!.Descendants<CellFormula>());
        using var stream = File.OpenRead(file);
        var inspection = await new OpenXmlWorkbookInspector().InspectAsync(stream);
        var suggestion = new ExcelColumnSuggestionEngine().Suggest(inspection);
        var mapping = new ConfirmedImportColumnMapping(suggestion.HeaderRowNumber, suggestion.Columns, inspection.Columns);
        stream.Position = 0;
        var imported = new List<ImportSourceRow>();
        await foreach (var row in new OpenXmlImportReader().ReadAsync(stream, mapping)) imported.Add(row);
        Assert.Equal(2, imported.Count);
        Assert.Equal(Rows[0].Name, imported[0].SourceTitle);
        Assert.Equal(Rows[0].Link, imported[0].ShareUrl);
        Assert.Equal("9ha1", imported[0].ExtractionCode);
        Assert.Contains(Rows[0].Source, imported[0].RawValues);
    }

    [Fact]
    public async Task CancelledExportCreatesNoFile()
    {
        var ui = new TestUi();
        using var import = new ImportViewModel(new RecordingWorkflow(), new EmptyPreview(), ui);
        var actions = new CollectResultActions(ui, ui, ui, import, () => true, () => { }, root);
        Assert.Contains("取消", await actions.ExportAsync(Rows));
        Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData("https://pan.baidu.com/s/1Example?pwd=wjbd", "wjbd", "https://pan.baidu.com/s/1Example?pwd=wjbd")]
    [InlineData("https://pan.baidu.com/s/1Example?foo=1&pwd=wjbd#view", "wjbd", "https://pan.baidu.com/s/1Example?foo=1&pwd=wjbd#view")]
    [InlineData("https://pan.baidu.com/s/1Example?pwd=other", "wjbd", "https://pan.baidu.com/s/1Example?pwd=other 提取码：wjbd")]
    [InlineData("https://pan.baidu.com/s/1Example?notpwd=wjbd", "wjbd", "https://pan.baidu.com/s/1Example?notpwd=wjbd 提取码：wjbd")]
    public async Task CopiesCodeOnlyOnceWhenLinkAlreadyContainsSamePassword(string link, string code, string expected)
    {
        var ui = new TestUi();
        using var import = new ImportViewModel(new RecordingWorkflow(), new EmptyPreview(), ui);
        var actions = new CollectResultActions(ui, ui, ui, import, () => true, () => { }, root);

        await actions.CopyLinksAsync([new CollectedRow(1, "资源", link, code, "")]);

        Assert.Equal(expected, ui.Clipboard);
    }

    [Theory]
    [InlineData("copy", "复制链接失败")]
    [InlineData("export", "导出结果失败")]
    [InlineData("transfer", "送入转存失败")]
    public async Task ReportsWhichResultActionFailedWithoutExposingExceptionMessage(string operation, string expected)
    {
        var actions = new RecordingActions { Fail = true };
        await using var model = new CollectViewModel(new FakeCollectHelperSession(
            CollectHelperClient.Create(new FakeCollectHelperProcess())), action => action(), resultActions: actions);
        model.Resources.Add(Rows[0]);

        await (operation switch
        {
            "copy" => model.CopyLinksCommand,
            "export" => model.ExportResultsCommand,
            _ => model.SendToTransferCommand
        }).ExecuteAsync(null);

        Assert.Contains(expected, model.ResultActionMessage);
        Assert.Contains(model.Logs, line => line.Contains("IOException") && line.Contains("0x"));
        Assert.DoesNotContain("secret-cookie", model.ResultActionMessage);
        Assert.DoesNotContain(model.Logs, line => line.Contains("secret-cookie"));
        Assert.Single(model.Resources);
    }

    [Fact]
    public async Task TransferPreservesWorkbookAndUsesExistingImportWithoutStartingRemoteWork()
    {
        var ui = new TestUi();
        var workflow = new RecordingWorkflow();
        using var import = new ImportViewModel(workflow, new EmptyPreview(), ui);
        var navigated = false;
        var actions = new CollectResultActions(ui, ui, ui, import, () => true, () => navigated = true, root);
        var message = await actions.SendToTransferAsync(Rows);
        Assert.True(navigated);
        Assert.NotNull(import.SessionId);
        Assert.True(File.Exists(workflow.ExcelPath));
        Assert.Contains("导入完成", message);
        using var document = SpreadsheetDocument.Open(workflow.ExcelPath!, false);
        Assert.Contains(Rows[0].Name, document.WorkbookPart!.WorksheetParts.Single().Worksheet!.InnerText);
    }

    [Fact]
    public async Task BusyTransferOrDeclinedReplacementDoesNotModifyCurrentImport()
    {
        var ui = new TestUi { Confirm = false };
        var workflow = new RecordingWorkflow();
        using var import = new ImportViewModel(workflow, new EmptyPreview(), ui);
        var previous = Guid.NewGuid();
        import.AttachSession(previous);
        var available = false;
        var actions = new CollectResultActions(ui, ui, ui, import, () => available, () => throw new Exception("Must not navigate"), root);
        Assert.Contains("暂不能", await actions.SendToTransferAsync(Rows));
        available = true;
        Assert.Contains("取消", await actions.SendToTransferAsync(Rows));
        Assert.Equal(previous, import.SessionId);
        Assert.Null(workflow.ExcelPath);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task CommandsUseSelectionOrAllAndKeepResultsAfterFailure()
    {
        var actions = new RecordingActions();
        await using var model = new CollectViewModel(new FakeCollectHelperSession(
            CollectHelperClient.Create(new FakeCollectHelperProcess())), action => action(), resultActions: actions);
        Assert.False(model.CopyLinksCommand.CanExecute(null));
        foreach (var row in Rows) model.Resources.Add(row);
        Assert.True(model.ExportResultsCommand.CanExecute(null));
        await model.CopyLinksCommand.ExecuteAsync(null);
        Assert.Equal(Rows, actions.Received);
        model.SetSelectedResults([Rows[1]]);
        await model.SendToTransferCommand.ExecuteAsync(null);
        Assert.Equal([Rows[1]], actions.Received);
        Assert.Contains("已选 1", model.ResultScopeText);
        actions.Fail = true;
        await model.ExportResultsCommand.ExecuteAsync(null);
        Assert.Contains("失败", model.ResultActionMessage);
        Assert.Equal(2, model.Resources.Count);
        Assert.True(model.ExportResultsCommand.CanExecute(null));
        model.Resources.Clear();
        Assert.False(model.CopyLinksCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancellingMappingOrFailingBeforeCommitRetainsPreviousImportAndReplay(bool cancelled)
    {
        var ui = new TestUi();
        var previous = Guid.NewGuid();
        var workflow = new RecordingWorkflow { Previous = previous, Failure = cancelled
            ? new OperationCanceledException() : new ImportWorkflowException(ImportUiError.InvalidWorkbook) };
        using var import = new ImportViewModel(workflow, new EmptyPreview(), ui);
        await import.InitializeAsync();
        var row = new ImportPreviewRow(Guid.NewGuid(), 1, Rows[0].Link, "9ha1", null, "就绪", null);
        import.Rows.Add(row);
        import.SelectedRow = row;
        var actions = new CollectResultActions(ui, ui, ui, import, () => true, () => { }, root);
        var message = await actions.SendToTransferAsync(Rows);
        Assert.Equal(previous, import.SessionId);
        Assert.True(import.CanResumeImport);
        Assert.Same(row, import.SelectedRow);
        Assert.Same(row, Assert.Single(import.Rows));
        Assert.Empty(workflow.Discarded);
        Assert.Contains("原导入内容已保留", message);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    private sealed class RecordingActions : ICollectResultActions
    {
        public IReadOnlyList<CollectedRow> Received = [];
        public bool Fail;
        private Task<string> Record(IReadOnlyList<CollectedRow> rows)
        {
            Received = rows;
            return Fail ? Task.FromException<string>(new IOException("secret-cookie")) : Task.FromResult("完成");
        }
        public Task<string> CopyLinksAsync(IReadOnlyList<CollectedRow> rows) => Record(rows);
        public Task<string> ExportAsync(IReadOnlyList<CollectedRow> rows) => Record(rows);
        public Task<string> SendToTransferAsync(IReadOnlyList<CollectedRow> rows) => Record(rows);
    }

    private sealed class TestUi : ITextClipboard, IResultSaveDialog, IDialogService
    {
        public string? Clipboard;
        public string? Destination;
        public bool Confirm = true;
        public Task SetTextAsync(string text, CancellationToken cancellationToken = default) { Clipboard = text; return Task.CompletedTask; }
        public Task<string?> PickDestinationDirectoryAsync(CancellationToken cancellationToken = default) => Task.FromResult(Destination);
        public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default) => Task.FromResult(Confirm);
        public Task ShowMessageAsync(string title, string message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string?> PickExcelFileAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }

    private sealed class EmptyPreview : IImportPreviewPageSource
    {
        public Task<ImportPreviewPage> LoadAsync(ImportPreviewPageRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new ImportPreviewPage([], 2, null));
    }

    private sealed class RecordingWorkflow : IImportWorkflow
    {
        public string? ExcelPath;
        public Guid? Previous;
        public Exception? Failure;
        public List<Guid> Discarded = [];
        public void DiscardReplaySource(Guid sessionId) => Discarded.Add(sessionId);
        public Task<Guid?> FindLatestResumableExcelSessionAsync(CancellationToken cancellationToken = default) => Task.FromResult(Previous);
        public Task<ImportWorkflowResult> ImportExcelAsync(Guid sessionId, string filePath, CancellationToken cancellationToken)
        {
            ExcelPath = filePath;
            if (Failure is not null) return Task.FromException<ImportWorkflowResult>(Failure);
            return Task.FromResult(new ImportWorkflowResult(sessionId, new(2, 2, 0, 0, 0)));
        }
        public Task<ImportWorkflowResult> ImportTextAsync(Guid sessionId, string text, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ImportWorkflowResult> ResumeAsync(Guid sessionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ImportWorkflowResult> UpdateLinkAsync(Guid rowId, string shareUrl, string? extractionCode, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ImportWorkflowResult> SetExcludedAsync(Guid rowId, bool excluded, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
