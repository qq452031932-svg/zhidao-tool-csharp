using System.Diagnostics;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Infrastructure.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace BaiduBatchTransfer.Infrastructure.Tests.Excel;

public sealed class OpenXmlStreamingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"bbt-openxml-{Guid.NewGuid():N}");

    [Fact]
    public async Task ReaderMapsHeadersSharedStringsAndPreservesSourceRowNumber()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "mapped.xlsx");
        WriteSharedStringWorkbook(path);

        await using var stream = File.OpenRead(path);
        var rows = await ReadAllAsync(new OpenXmlImportReader().ReadAsync(stream));

        var row = Assert.Single(rows);
        Assert.Equal(3, row.SourceRowNumber);
        Assert.Equal("https://pan.baidu.com/s/id", row.ShareUrl);
        Assert.Equal("a1B2", row.ExtractionCode);
        Assert.Equal("/课程", row.TargetPath);
        Assert.Equal("外层", row.CreateFolderName);
        Assert.Equal("账号甲", row.RequestedAccountName);
        Assert.Equal("备注", row.Note);
    }

    [Fact]
    public async Task ReaderReportsMissingAndDuplicateMappedHeadersSafely()
    {
        Directory.CreateDirectory(root);
        var missing = Path.Combine(root, "missing.xlsx");
        var duplicate = Path.Combine(root, "duplicate.xlsx");
        WriteInlineWorkbook(missing, ["备注"], [["text"]]);
        WriteInlineWorkbook(duplicate, ["分享链接", "Share URL"], []);

        await using (var stream = File.OpenRead(missing))
        {
            var error = await Assert.ThrowsAsync<ExcelImportException>(() =>
                ReadAllAsync(new OpenXmlImportReader().ReadAsync(stream)));
            Assert.Equal(ExcelImportErrorCode.MissingRequiredHeader, error.Code);
            Assert.DoesNotContain("text", error.ToString(), StringComparison.Ordinal);
        }

        await using (var stream = File.OpenRead(duplicate))
        {
            var error = await Assert.ThrowsAsync<ExcelImportException>(() =>
                ReadAllAsync(new OpenXmlImportReader().ReadAsync(stream)));
            Assert.Equal(ExcelImportErrorCode.DuplicateHeader, error.Code);
        }
    }

    [Fact]
    public async Task ReaderRejectsWorkbookBeyondConfiguredRowLimit()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "limited.xlsx");
        WriteInlineWorkbook(path, ["分享链接"], [["https://pan.baidu.com/s/1"], ["https://pan.baidu.com/s/2"]]);

        await using var stream = File.OpenRead(path);
        var error = await Assert.ThrowsAsync<ExcelImportException>(() =>
            ReadAllAsync(new OpenXmlImportReader(new OpenXmlImportLimits(MaxRows: 1)).ReadAsync(stream)));

        Assert.Equal(ExcelImportErrorCode.RowLimitExceeded, error.Code);
    }

    [Fact]
    public async Task ReaderStreamsTwoHundredThousandRowsWithBoundedWorkingSet()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "large.xlsx");
        WriteLargeWorkbook(path, 200_000);
        var before = GC.GetTotalMemory(forceFullCollection: true);
        var peak = before;
        var count = 0;
        var stopwatch = Stopwatch.StartNew();

        await using (var stream = File.OpenRead(path))
        {
            await foreach (var row in new OpenXmlImportReader().ReadAsync(stream))
            {
                count++;
                if ((count & 4095) == 0) peak = Math.Max(peak, GC.GetTotalMemory(forceFullCollection: false));
                Assert.Equal(count + 1, row.SourceRowNumber);
            }
        }

        stopwatch.Stop();
        var delta = peak - before;
        Console.WriteLine($"OPENXML_200K elapsed_ms={stopwatch.ElapsedMilliseconds} peak_delta_bytes={delta}");
        Assert.Equal(200_000, count);
        Assert.True(delta < 96L * 1024 * 1024, $"Peak managed working-set delta was {delta:N0} bytes.");
    }

    [Fact]
    public async Task TemplateAndResultWriterUseExpectedColumnsAndExplicitTextCells()
    {
        Directory.CreateDirectory(root);
        var templatePath = Path.Combine(root, "template.xlsx");
        var resultPath = Path.Combine(root, "result.xlsx");
        await using (var template = File.Create(templatePath))
            await new OpenXmlTemplateWriter().WriteAsync(template);
        await using (var result = File.Create(resultPath))
            await new OpenXmlResultWriter().WriteAsync(result, ResultRows());

        Assert.Equal(
            new[] { "分享链接", "提取码", "目标目录", "外层目录", "指定账号", "备注" },
            ReadHeader(templatePath));
        using var document = SpreadsheetDocument.Open(resultPath, false);
        var worksheet = document.WorkbookPart!.WorksheetParts.Single();
        using var reader = OpenXmlReader.Create(worksheet);
        Cell? dangerousCell = null;
        Cell? numericCell = null;
        while (reader.Read())
        {
            if (reader.ElementType != typeof(Row)) continue;
            if (reader.LoadCurrentElement() is not Row row) continue;
            if (row.RowIndex?.Value == 2)
            {
                dangerousCell = row.Elements<Cell>().ElementAt(1);
                numericCell = row.Elements<Cell>().First();
                break;
            }
        }

        Assert.NotNull(dangerousCell);
        Assert.Equal(CellValues.InlineString, dangerousCell!.DataType!.Value);
        Assert.StartsWith("'", dangerousCell.InlineString!.Text!.Text, StringComparison.Ordinal);
        Assert.Equal(CellValues.Number, numericCell!.DataType!.Value);
    }

    [Fact]
    public async Task ReaderHonorsCancellationWithoutReadingRemainingRows()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "cancel.xlsx");
        WriteLargeWorkbook(path, 10_000);
        using var cancellation = new CancellationTokenSource();
        await using var stream = File.OpenRead(path);
        var count = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in new OpenXmlImportReader().ReadAsync(stream, cancellation.Token))
            {
                if (++count == 100) cancellation.Cancel();
            }
        });
        Assert.InRange(count, 100, 101);
    }

    private static async IAsyncEnumerable<ExcelResultRow> ResultRows()
    {
        yield return new ExcelResultRow(2, "=HYPERLINK(\"https://evil.test\")", "ready", "@account", "+path", "-error");
        await Task.Yield();
    }

    private static async Task<List<ImportSourceRow>> ReadAllAsync(IAsyncEnumerable<ImportSourceRow> source)
    {
        var rows = new List<ImportSourceRow>();
        await foreach (var row in source) rows.Add(row);
        return rows;
    }

    private static string[] ReadHeader(string path)
    {
        using var document = SpreadsheetDocument.Open(path, false);
        var worksheet = document.WorkbookPart!.WorksheetParts.Single();
        using var reader = OpenXmlReader.Create(worksheet);
        while (reader.Read())
        {
            if (reader.ElementType != typeof(Row)) continue;
            if (reader.LoadCurrentElement() is not Row row) continue;
            return row.Elements<Cell>().Select(cell => cell.InlineString!.Text!.Text).ToArray();
        }

        return [];
    }

    private static void WriteLargeWorkbook(string path, int rowCount)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        using (var writer = OpenXmlWriter.Create(workbookPart))
        {
            writer.WriteStartElement(new Workbook());
            writer.WriteStartElement(new Sheets());
            writer.WriteElement(new Sheet { Id = "rId1", SheetId = 1, Name = "导入" });
            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>("rId1");
        using var sheetWriter = OpenXmlWriter.Create(worksheetPart);
        sheetWriter.WriteStartElement(new Worksheet());
        sheetWriter.WriteStartElement(new SheetData());
        WriteInlineRow(sheetWriter, 1, "分享链接", "备注");
        for (var index = 1; index <= rowCount; index++)
            WriteInlineRow(sheetWriter, (uint)(index + 1), $"https://pan.baidu.com/s/id-{index}", $"note-{index}");
        sheetWriter.WriteEndElement();
        sheetWriter.WriteEndElement();
    }

    private static void WriteInlineWorkbook(string path, string[] headers, string[][] rows)
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
        WriteInlineRow(writer, 1, headers);
        for (var index = 0; index < rows.Length; index++) WriteInlineRow(writer, (uint)(index + 2), rows[index]);
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteSharedStringWorkbook(string path)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        var stringsPart = workbookPart.AddNewPart<SharedStringTablePart>();
        var values = new[] { "分享链接", "提取码", "目标目录", "外层目录", "指定账号", "备注", "https://pan.baidu.com/s/id", "a1B2", "/课程", "外层", "账号甲", "备注" };
        stringsPart.SharedStringTable = new SharedStringTable(values.Select(value => new SharedStringItem(new Text(value))));
        workbookPart.Workbook = new Workbook(new Sheets(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = 1,
            Name = "导入",
        }));
        using var writer = OpenXmlWriter.Create(worksheetPart);
        writer.WriteStartElement(new Worksheet());
        writer.WriteStartElement(new SheetData());
        WriteSharedRow(writer, 1, Enumerable.Range(0, 6).ToArray());
        WriteSharedRow(writer, 3, Enumerable.Range(6, 6).ToArray());
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteInlineRow(OpenXmlWriter writer, uint rowIndex, params string[] values)
    {
        writer.WriteStartElement(new Row { RowIndex = rowIndex });
        foreach (var value in values)
            writer.WriteElement(new Cell(new InlineString(new Text(value))) { DataType = CellValues.InlineString });
        writer.WriteEndElement();
    }

    private static void WriteSharedRow(OpenXmlWriter writer, uint rowIndex, params int[] indexes)
    {
        writer.WriteStartElement(new Row { RowIndex = rowIndex });
        foreach (var index in indexes)
            writer.WriteElement(new Cell { DataType = CellValues.SharedString, CellValue = new CellValue(index) });
        writer.WriteEndElement();
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
