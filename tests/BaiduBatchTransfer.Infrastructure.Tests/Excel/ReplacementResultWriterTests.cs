using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Infrastructure.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Validation;

namespace BaiduBatchTransfer.Infrastructure.Tests.Excel;

public sealed class ReplacementResultWriterTests
{
    [Fact]
    public async Task EveryHeaderAndDataCellIsSanitizedInlineTextWithoutFormulaOrHyperlink()
    {
        var layout = new ReplacementWorkbookLayout([
            "=raw-header",
            "\u0001safe-header",
            "转存结果_解析后标题",
        ]);
        var row = new ReplacementWorkbookRow(1, [
            "\t+SUM(A1:A2)",
            "a\u0001b",
            "\u200B@cmd",
        ]);

        await using var output = new MemoryStream();
        await new OpenXmlReplacementResultWriter().WriteAsync(output, layout, Rows(row));

        using var document = Open(output);
        Assert.Empty(new OpenXmlValidator().Validate(document));
        var worksheet = document.WorkbookPart!.WorksheetParts.Single();
        Assert.Empty(worksheet.HyperlinkRelationships);
        Assert.Null(worksheet.Worksheet!.GetFirstChild<Hyperlinks>());
        var rows = worksheet.Worksheet.GetFirstChild<SheetData>()!.Elements<Row>().ToArray();
        Assert.Equal(["'=raw-header", "safe-header", "转存结果_解析后标题"], Texts(rows[0]));
        Assert.Equal(["'\t+SUM(A1:A2)", "ab", "'\u200B@cmd"], Texts(rows[1]));
        Assert.All(rows.SelectMany(value => value.Elements<Cell>()), cell =>
        {
            Assert.Equal(CellValues.InlineString, cell.DataType!.Value);
            Assert.Null(cell.CellFormula);
        });
    }

    [Fact]
    public async Task SanitizedTextAtExcelLimitSucceedsButLongerTextFailsWithoutTruncation()
    {
        var maximumAfterPrefix = "=" + new string('a', OpenXmlReplacementResultWriter.ExcelMaximumTextLength - 2);
        await using (var output = new MemoryStream())
        {
            await new OpenXmlReplacementResultWriter().WriteAsync(
                output,
                new ReplacementWorkbookLayout([maximumAfterPrefix]),
                Rows(new ReplacementWorkbookRow(1, [maximumAfterPrefix])));
            using var document = Open(output);
            var cells = document.WorkbookPart!.WorksheetParts.Single().Worksheet!
                .GetFirstChild<SheetData>()!.Descendants<Cell>().ToArray();
            Assert.All(cells, cell => Assert.Equal(
                OpenXmlReplacementResultWriter.ExcelMaximumTextLength,
                cell.InlineString!.Text!.Text.Length));
        }

        var tooLongAfterPrefix = "=" + new string('a', OpenXmlReplacementResultWriter.ExcelMaximumTextLength - 1);
        await using var rejected = new MemoryStream();
        var error = await Assert.ThrowsAsync<ReplacementWorkbookWriteException>(() =>
            new OpenXmlReplacementResultWriter().WriteAsync(
                rejected,
                new ReplacementWorkbookLayout(["safe"]),
                Rows(new ReplacementWorkbookRow(1, [tooLongAfterPrefix]))));
        Assert.Equal(ReplacementWorkbookWriteErrorCode.CellTextTooLong, error.Code);
        Assert.DoesNotContain(tooLongAfterPrefix, error.ToString(), StringComparison.Ordinal);

        await using var rejectedHeader = new MemoryStream();
        var headerError = await Assert.ThrowsAsync<ReplacementWorkbookWriteException>(() =>
            new OpenXmlReplacementResultWriter().WriteAsync(
                rejectedHeader,
                new ReplacementWorkbookLayout([tooLongAfterPrefix]),
                Rows()));
        Assert.Equal(ReplacementWorkbookWriteErrorCode.CellTextTooLong, headerError.Code);
        Assert.Equal(0, rejectedHeader.Length);
    }

    [Fact]
    public async Task RowColumnCountMustExactlyMatchLayout()
    {
        await using var output = new MemoryStream();
        var error = await Assert.ThrowsAsync<ReplacementWorkbookWriteException>(() =>
            new OpenXmlReplacementResultWriter().WriteAsync(
                output,
                new ReplacementWorkbookLayout(["A", "B"]),
                Rows(new ReplacementWorkbookRow(1, ["only-one"]))));

        Assert.Equal(ReplacementWorkbookWriteErrorCode.ColumnCountMismatch, error.Code);
        Assert.DoesNotContain("only-one", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RowLimitIncludesHeaderAndRolloverRepeatsHeaders()
    {
        await using var output = new MemoryStream();
        await new OpenXmlReplacementResultWriter(maxRowsPerSheet: 3).WriteAsync(
            output,
            new ReplacementWorkbookLayout(["A", "B"]),
            Rows(
                new(1, ["1a", "1b"]),
                new(2, ["2a", "2b"]),
                new(3, ["3a", "3b"]),
                new(4, ["4a", "4b"]),
                new(5, ["5a", "5b"])));

        using var document = Open(output);
        var workbookPart = document.WorkbookPart!;
        var sheets = workbookPart.Workbook!.Sheets!.Elements<Sheet>().ToArray();
        Assert.Equal(["替换结果", "替换结果 2", "替换结果 3"], sheets.Select(sheet => sheet.Name!.Value));
        Assert.Equal([3, 3, 2], sheets.Select(sheet =>
        {
            Assert.NotNull(sheet.Id?.Value);
            var part = Assert.IsType<WorksheetPart>(workbookPart.GetPartById(sheet.Id!.Value!));
            var rows = part.Worksheet!.GetFirstChild<SheetData>()!.Elements<Row>().ToArray();
            Assert.Equal(["A", "B"], Texts(rows[0]));
            return rows.Length;
        }));
    }

    [Fact]
    public async Task EmptyInputStillCreatesOneHeaderOnlySheetWithResolvableRelationship()
    {
        await using var output = new MemoryStream();
        await new OpenXmlReplacementResultWriter().WriteAsync(
            output,
            new ReplacementWorkbookLayout(["", "B"]),
            Rows());

        using var document = Open(output);
        var workbookPart = document.WorkbookPart!;
        var sheet = Assert.Single(workbookPart.Workbook!.Sheets!.Elements<Sheet>());
        Assert.Equal("替换结果", sheet.Name!.Value);
        var worksheet = Assert.IsType<WorksheetPart>(workbookPart.GetPartById(sheet.Id!.Value!));
        var row = Assert.Single(worksheet.Worksheet!.GetFirstChild<SheetData>()!.Elements<Row>());
        Assert.Equal(["", "B"], Texts(row));
    }

    private static SpreadsheetDocument Open(MemoryStream output)
    {
        output.Position = 0;
        return SpreadsheetDocument.Open(output, false);
    }

    private static string[] Texts(Row row) => row.Elements<Cell>()
        .Select(cell => cell.InlineString?.Text?.Text ?? string.Empty)
        .ToArray();

    private static async IAsyncEnumerable<ReplacementWorkbookRow> Rows(
        params ReplacementWorkbookRow[] rows)
    {
        foreach (var row in rows)
        {
            yield return row;
            await Task.Yield();
        }
    }
}
