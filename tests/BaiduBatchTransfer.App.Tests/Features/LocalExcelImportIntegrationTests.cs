using BaiduBatchTransfer.App.Features.Imports;
using BaiduBatchTransfer.App.Features.Jobs;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Application.Imports;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Infrastructure.Database;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.Extensions.DependencyInjection;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class LocalExcelImportIntegrationTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "BaiduBatchTransfer-LocalExcelImportIntegrationTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RealWorkbookImportsThroughTheMigratedHostWhileTransfersStayUnavailable()
    {
        Directory.CreateDirectory(root);
        var workbookPath = Path.Combine(root, "private-user-workbook.xlsx");
        WriteWorkbook(
            workbookPath,
            [
                ["https://pan.baidu.com/s/integration-alpha", "a1b2"],
                ["https://pan.baidu.com/s/integration-beta", "c3d4"],
            ]);
        using var host = AppBootstrap.CreateHost(root);
        await host.Services.GetRequiredService<DatabaseMigrator>().MigrateAsync();
        var availability = host.Services.GetRequiredService<IAdapterAvailability>();
        var vm = new ImportViewModel(
            host.Services.GetRequiredService<IImportWorkflow>(),
            host.Services.GetRequiredService<IImportPreviewPageSource>(),
            new FakeDialogService(workbookPath),
            availability);
        var statuses = new List<string>();
        ImportDraftReady? draft = null;
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ImportViewModel.StatusMessage))
                statuses.Add(vm.StatusMessage);
        };
        vm.DraftReady += (_, value) => draft = value;

        await vm.SelectAndImportExcelCommand.ExecuteAsync(null);

        Assert.False(availability.CanStartTransfers);
        var completed = Assert.IsType<ImportDraftReady>(draft);
        Assert.Equal(2, completed.Statistics.Total);
        Assert.Equal(2, completed.Statistics.Ready);
        Assert.Equal(0, completed.Statistics.Invalid);
        Assert.Equal(0, completed.Statistics.Duplicate);

        await using (var unit = await host.Services.GetRequiredService<IUnitOfWorkFactory>().BeginAsync())
        {
            var persisted = await unit.Imports.GetSessionAsync(completed.SessionId);
            Assert.NotNull(persisted);
            Assert.Equal("Excel", persisted.SourceType);
            Assert.Equal("[REDACTED]", persisted.SourceName);
            Assert.Equal(2, persisted.TotalRows);
            Assert.Equal(2, persisted.ReadyRows);
            await unit.RollbackAsync();
        }

        var query = host.Services.GetRequiredService<ImportPageQuery>();
        const int pageSize = 1;
        Assert.InRange(pageSize, 1, 1_000);
        var first = await query.ExecuteAsync(new(completed.SessionId, null, null, pageSize));
        var firstRow = Assert.Single(first.Items);
        Assert.Equal("https://pan.baidu.com/s/integration-alpha", firstRow.ShareUrl);
        Assert.NotNull(first.NextSourceRowNumber);
        var second = await query.ExecuteAsync(new(
            completed.SessionId,
            null,
            null,
            pageSize,
            first.NextSourceRowNumber));
        var secondRow = Assert.Single(second.Items);
        Assert.Equal("https://pan.baidu.com/s/integration-beta", secondRow.ShareUrl);

        Assert.Contains("请选择 Excel 文件", statuses);
        Assert.Contains("已选择 Excel 文件，正在检查", statuses);
        Assert.Contains(statuses, status => status.Contains("正在解析并写入草稿", StringComparison.Ordinal));
        Assert.Contains("导入完成，请分页检查异常、重复和账号映射。", statuses);
        Assert.All(statuses, status => Assert.DoesNotContain(workbookPath, status, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task InvalidRealWorkbookReturnsSafeChineseErrorWithoutLeakingItsPath()
    {
        Directory.CreateDirectory(root);
        var workbookPath = Path.Combine(root, "secret-token-invalid.xlsx");
        await File.WriteAllTextAsync(workbookPath, "not an Open XML workbook");
        using var host = AppBootstrap.CreateHost(root);
        await host.Services.GetRequiredService<DatabaseMigrator>().MigrateAsync();
        var vm = new ImportViewModel(
            host.Services.GetRequiredService<IImportWorkflow>(),
            host.Services.GetRequiredService<IImportPreviewPageSource>(),
            new FakeDialogService(workbookPath),
            host.Services.GetRequiredService<IAdapterAvailability>());

        await vm.SelectAndImportExcelCommand.ExecuteAsync(null);

        Assert.True(vm.CanResumeImport);
        Assert.Equal("导入中断，已保存的进度可以继续。", vm.StatusMessage);
        Assert.DoesNotContain(workbookPath, vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OpenXml", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Exception", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        var fullRoot = Path.GetFullPath(root);
        var expectedParent = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "BaiduBatchTransfer-LocalExcelImportIntegrationTests"));
        var prefix = expectedParent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (fullRoot.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && Directory.Exists(fullRoot))
            Directory.Delete(fullRoot, recursive: true);
    }

    private static void WriteWorkbook(string path, IReadOnlyList<string[]> rows)
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
        WriteInlineRow(writer, 1, ["分享链接", "提取码"]);
        for (var index = 0; index < rows.Count; index++)
            WriteInlineRow(writer, checked((uint)index + 2), rows[index]);
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteInlineRow(OpenXmlWriter writer, uint rowIndex, IReadOnlyList<string> values)
    {
        writer.WriteStartElement(new Row { RowIndex = rowIndex });
        foreach (var value in values)
            writer.WriteElement(new Cell(new InlineString(new Text(value)))
                { DataType = CellValues.InlineString });
        writer.WriteEndElement();
    }

    private sealed class FakeDialogService(string? selectedPath) : IDialogService
    {
        public Task<bool> ConfirmAsync(
            string title,
            string message,
            CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task ShowMessageAsync(
            string title,
            string message,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public string? PickExcelFile() => selectedPath;
    }
}
