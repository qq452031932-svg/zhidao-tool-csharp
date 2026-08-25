using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Infrastructure.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace BaiduBatchTransfer.Infrastructure.Tests.Excel;

public sealed class OpenXmlStandaloneShareResultWriterTests
{
    [Fact]
    public async Task WritesAReadableWorkbookAndSanitizesUserControlledCells()
    {
        await using var stream = new MemoryStream();
        IReplacementWorkbookWriter writer = new OpenXmlStandaloneShareResultWriter();

        await writer.WriteAsync(
            stream,
            new ReplacementWorkbookLayout(["账号", "资源名称", "资源类型", "新分享链接", "提取码", "状态"]),
            Rows(new ReplacementWorkbookRow(1,
                ["账号A", "=HYPERLINK(\"bad\")", "文件", "https://pan.baidu.com/s/safe", "a1b2", "分享成功"])));

        stream.Position = 0;
        using var document = SpreadsheetDocument.Open(stream, false);
        var workbookPart = Assert.IsType<WorkbookPart>(document.WorkbookPart);
        var worksheet = Assert.IsType<Worksheet>(workbookPart.WorksheetParts.Single().Worksheet);
        var sheet = Assert.IsType<SheetData>(
            worksheet.GetFirstChild<SheetData>());
        var cells = sheet.Elements<Row>().Skip(1).Single().Elements<Cell>().ToArray();
        Assert.Equal("'=HYPERLINK(\"bad\")", cells[1].InnerText);
        Assert.All(cells, cell => Assert.Null(cell.CellFormula));
    }

    private static async IAsyncEnumerable<ReplacementWorkbookRow> Rows(params ReplacementWorkbookRow[] rows)
    {
        foreach (var row in rows) { yield return row; await Task.Yield(); }
    }
}
