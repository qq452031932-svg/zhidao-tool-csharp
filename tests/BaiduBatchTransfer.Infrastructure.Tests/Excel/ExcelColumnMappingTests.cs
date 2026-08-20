using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Services;
using BaiduBatchTransfer.Infrastructure.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace BaiduBatchTransfer.Infrastructure.Tests.Excel;

public sealed class ExcelColumnMappingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"bbt-mapping-{Guid.NewGuid():N}");

    [Fact]
    public async Task InspectorAndSuggestionsRecognizeKnownHeaders()
    {
        var path = WriteWorkbook(
            ["源标题", "分享链接", "提取码", "自定义字段"],
            [["课程一", "https://pan.baidu.com/s/known", " a1B2 ", "保留"]]);

        await using var stream = File.OpenRead(path);
        var inspection = await new OpenXmlWorkbookInspector().InspectAsync(stream);
        var suggestion = new ExcelColumnSuggestionEngine().Suggest(inspection);

        Assert.Equal(1, inspection.HeaderRowNumber);
        Assert.Equal(new[] { "源标题", "分享链接", "提取码", "自定义字段" }, inspection.Columns.Select(x => x.Header));
        Assert.Equal(2, Assert.Single(inspection.SampleRows).SourceRowNumber);
        Assert.Equal(TabularImportColumnRole.SourceTitle, Role(suggestion, 0));
        Assert.Equal(TabularImportColumnRole.ShareUrl, Role(suggestion, 1));
        Assert.Equal(TabularImportColumnRole.ExtractionCode, Role(suggestion, 2));
        Assert.Equal(TabularImportColumnRole.Preserve, Role(suggestion, 3));
    }

    [Fact]
    public async Task InspectorTreatsUnfamiliarFirstRowAsHeaderWhenSamplesContainUrls()
    {
        var path = WriteWorkbook(
            ["课程名称", "资源站点", "暗号"],
            [
                ["第一课", "说明 https://pan.baidu.com/s/alpha 结束", "x1y2"],
                ["第二课", "https://pan.baidu.com/s/beta", "z9w8"],
            ]);

        await using var stream = File.OpenRead(path);
        var inspection = await new OpenXmlWorkbookInspector().InspectAsync(stream);
        var suggestion = new ExcelColumnSuggestionEngine().Suggest(inspection);

        Assert.Equal(1, suggestion.HeaderRowNumber);
        Assert.Equal(TabularImportColumnRole.ShareUrl, Role(suggestion, 1));
        Assert.Equal(TabularImportColumnRole.SourceTitle, Role(suggestion, 0));
        Assert.Equal(TabularImportColumnRole.Preserve, Role(suggestion, 2));
    }

    [Fact]
    public async Task TrulyHeaderlessSheetKeepsFirstRowAsDataAndSuggestsNearestLeftTitle()
    {
        var path = WriteWorkbook(
            headers: null,
            [
                ["分类", "第一课", "https://pan.baidu.com/s/one", "尾注"],
                ["分类", "第二课", "https://pan.baidu.com/s/two", "尾注"],
            ]);

        await using var stream = File.OpenRead(path);
        var inspection = await new OpenXmlWorkbookInspector().InspectAsync(stream);
        var suggestion = new ExcelColumnSuggestionEngine().Suggest(inspection);

        Assert.Null(inspection.HeaderRowNumber);
        Assert.Equal(1, inspection.SampleRows[0].SourceRowNumber);
        Assert.Equal(TabularImportColumnRole.SourceTitle, Role(suggestion, 1));
        Assert.Equal(TabularImportColumnRole.ShareUrl, Role(suggestion, 2));
        Assert.Equal(TabularImportColumnRole.Preserve, Role(suggestion, 0));
        Assert.Equal(TabularImportColumnRole.Preserve, Role(suggestion, 3));
    }

    [Fact]
    public async Task InspectionIsCappedAtTwentyDataRows()
    {
        var rows = Enumerable.Range(1, 30)
            .Select(index => new[] { $"标题 {index}", $"https://pan.baidu.com/s/{index}" })
            .ToArray();
        var path = WriteWorkbook(["标题", "链接"], rows);

        await using var stream = File.OpenRead(path);
        var inspection = await new OpenXmlWorkbookInspector().InspectAsync(stream);

        Assert.Equal(20, inspection.SampleRows.Count);
        Assert.Equal(21, inspection.SampleRows[^1].SourceRowNumber);
    }

    [Fact]
    public async Task ReaderRejectsColumnsThatAppearOnlyAfterTheBoundedConfirmationSample()
    {
        var rows = Enumerable.Range(1, 20)
            .Select(index => new[] { $"标题 {index}", $"https://pan.baidu.com/s/{index}" })
            .Append(["标题 21", "https://pan.baidu.com/s/21", "未确认字段"])
            .ToArray();
        var path = WriteWorkbook(["标题", "链接"], rows);
        await using var inspectStream = File.OpenRead(path);
        var inspection = await new OpenXmlWorkbookInspector().InspectAsync(inspectStream);
        var suggestion = new ExcelColumnSuggestionEngine().Suggest(inspection);
        var mapping = new ConfirmedImportColumnMapping(suggestion.HeaderRowNumber, suggestion.Columns);

        await using var readStream = File.OpenRead(path);
        var error = await Assert.ThrowsAsync<ExcelImportException>(() =>
            ReadAllAsync(new OpenXmlImportReader().ReadAsync(readStream, mapping)));

        Assert.Equal(ExcelImportErrorCode.ColumnMappingMismatch, error.Code);
        Assert.DoesNotContain("未确认字段", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InspectorSkipsNonWorksheetSheetDeclarationsBeforeTheFirstWorksheet()
    {
        var path = WriteWorkbookWithChartSheetFirst();
        await using var stream = File.OpenRead(path);

        var inspection = await new OpenXmlWorkbookInspector().InspectAsync(stream);

        Assert.Equal(1, inspection.HeaderRowNumber);
        Assert.Equal("分享链接", Assert.Single(inspection.Columns).Header);
    }

    [Fact]
    public void SuggestionsDoNotAuthorizeAndConfirmedMappingRequiresExactlyOneShareUrl()
    {
        Assert.Throws<ArgumentException>(() => new ConfirmedImportColumnMapping(
            headerRowNumber: 1,
            [new(0, TabularImportColumnRole.SourceTitle), new(1, TabularImportColumnRole.Preserve)]));
        Assert.Throws<ArgumentException>(() => new ConfirmedImportColumnMapping(
            headerRowNumber: 1,
            [new(0, TabularImportColumnRole.ShareUrl), new(1, TabularImportColumnRole.ShareUrl)]));
        Assert.Throws<ArgumentException>(() => new ConfirmedImportColumnMapping(
            headerRowNumber: 1,
            [new(0, TabularImportColumnRole.SourceTitle), new(1, TabularImportColumnRole.SourceTitle), new(2, TabularImportColumnRole.ShareUrl)]));
        Assert.ThrowsAny<ArgumentException>(() => new ConfirmedImportColumnMapping(
            headerRowNumber: 1,
            [new(0, TabularImportColumnRole.ShareUrl), new(1, (TabularImportColumnRole)999)]));
    }

    [Fact]
    public void ConfirmedMappingFingerprintIsDeterministicAndDecisionSensitive()
    {
        var first = new ConfirmedImportColumnMapping(1,
        [
            new(0, TabularImportColumnRole.SourceTitle),
            new(1, TabularImportColumnRole.ShareUrl),
            new(2, TabularImportColumnRole.Preserve),
        ]);
        var reordered = new ConfirmedImportColumnMapping(1,
        [
            new(2, TabularImportColumnRole.Preserve),
            new(1, TabularImportColumnRole.ShareUrl),
            new(0, TabularImportColumnRole.SourceTitle),
        ]);
        var remapped = new ConfirmedImportColumnMapping(1,
        [
            new(0, TabularImportColumnRole.Preserve),
            new(1, TabularImportColumnRole.ShareUrl),
            new(2, TabularImportColumnRole.SourceTitle),
        ]);

        Assert.Equal(first.Fingerprint, reordered.Fingerprint);
        Assert.NotEqual(first.Fingerprint, remapped.Fingerprint);
        Assert.Matches("^[0-9a-f]{64}$", first.Fingerprint);
        Assert.DoesNotContain("ShareUrl", first.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("下载：https://evil.example/a 然后 https://pan.baidu.com/s/good?pwd=A1b2。", "https://pan.baidu.com/s/good?pwd=A1b2")]
    [InlineData("https://yun.baidu.com/share/init?surl=abc123&pwd=xY9z 尾注", "https://yun.baidu.com/share/init?surl=abc123&pwd=xY9z")]
    [InlineData("https://evil.example/?next=https://pan.baidu.com/s/inside", "https://pan.baidu.com/s/inside")]
    public void ExtractorReturnsOnlySupportedBaiduUrlSubstring(string raw, string expected) =>
        Assert.Equal(expected, new BaiduShareUrlExtractor().ExtractSupportedUrl(raw));

    [Theory]
    [InlineData("https://example.com/s/no")]
    [InlineData("https://pan.baidu.com.evil.example/s/no")]
    [InlineData("http://pan.baidu.com/s/no")]
    [InlineData("https://pan.baidu.com/%ZZ")]
    [InlineData("https://user@pan.baidu.com/s/no")]
    [InlineData("=HYPERLINK(\"https://evil.example\")")]
    public void ExtractorRejectsExternalAndMalformedBaiduLikeUrls(string raw) =>
        Assert.Null(new BaiduShareUrlExtractor().ExtractSupportedUrl(raw));

    [Theory]
    [InlineData(" a1B2 ", "a1B2")]
    [InlineData("\t\r\n", null)]
    [InlineData(null, null)]
    public void ExtractionCodeIsNormalizedSeparately(string? raw, string? expected) =>
        Assert.Equal(expected, BaiduShareUrlExtractor.NormalizeExtractionCode(raw));

    [Fact]
    public async Task ReaderRequiresManualConfirmationAndHonorsRemapWithMissingOptionalFields()
    {
        var path = WriteWorkbook(
            ["A", "B", "C"],
            [["https://evil.example", " 说明 https://pan.baidu.com/s/remapped。 ", "=RAW()"]]);
        var mapping = new ConfirmedImportColumnMapping(1,
        [
            new(0, TabularImportColumnRole.Preserve),
            new(1, TabularImportColumnRole.ShareUrl),
            new(2, TabularImportColumnRole.Preserve),
        ]);

        await using var stream = File.OpenRead(path);
        var row = Assert.Single(await ReadAllAsync(new OpenXmlImportReader().ReadAsync(stream, mapping)));

        Assert.Equal("https://pan.baidu.com/s/remapped", row.ShareUrl);
        Assert.Equal("https://pan.baidu.com/s/remapped", row.OriginalExtractedUrl);
        Assert.Null(row.SourceTitle);
        Assert.Null(row.ExtractionCode);
        Assert.Equal(new[] { "https://evil.example", " 说明 https://pan.baidu.com/s/remapped。 ", "=RAW()" }, row.RawValues);
    }

    [Fact]
    public async Task ReaderPreservesBlankCellsExactWhitespaceAndFormulaLikeTextInImmutableOrder()
    {
        var path = WriteWorkbook(
            ["标题", "链接", "空白", "原文"],
            [["  标题  ", "  https://pan.baidu.com/s/raw  ", "", "\t=HYPERLINK(\"https://evil.example\")  "]]);
        var mapping = new ConfirmedImportColumnMapping(1,
        [
            new(0, TabularImportColumnRole.SourceTitle),
            new(1, TabularImportColumnRole.ShareUrl),
            new(2, TabularImportColumnRole.Preserve),
            new(3, TabularImportColumnRole.Preserve),
        ]);

        await using var stream = File.OpenRead(path);
        var row = Assert.Single(await ReadAllAsync(new OpenXmlImportReader().ReadAsync(stream, mapping)));

        Assert.Equal("标题", row.SourceTitle);
        Assert.Equal(new[] { "  标题  ", "  https://pan.baidu.com/s/raw  ", "", "\t=HYPERLINK(\"https://evil.example\")  " }, row.RawValues);
        Assert.False(row.RawValues is string[]);
        Assert.DoesNotContain("标题", row.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("pan.baidu.com", row.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InspectorEnforcesSharedStringAndOversizedRowLimits()
    {
        var sharedPath = WriteSharedStringWorkbook();
        await using (var stream = File.OpenRead(sharedPath))
        {
            var error = await Assert.ThrowsAsync<ExcelImportException>(() =>
                new OpenXmlWorkbookInspector(new OpenXmlImportLimits(MaxSharedStrings: 1)).InspectAsync(stream));
            Assert.Equal(ExcelImportErrorCode.SharedStringLimitExceeded, error.Code);
        }

        var widePath = WriteWorkbook(["A", "B", "C"], []);
        await using (var stream = File.OpenRead(widePath))
        {
            var error = await Assert.ThrowsAsync<ExcelImportException>(() =>
                new OpenXmlWorkbookInspector(new OpenXmlImportLimits(MaxCellsPerRow: 2)).InspectAsync(stream));
            Assert.Equal(ExcelImportErrorCode.CellLimitExceeded, error.Code);
            Assert.Equal(3, error.ObservedCount);
        }
    }

    [Fact]
    public async Task InspectorRejectsCellReferencesBeyondExcelColumnBoundary()
    {
        var path = WriteSparseColumnWorkbook("XFE1");
        await using var stream = File.OpenRead(path);

        var error = await Assert.ThrowsAsync<ExcelImportException>(() =>
            new OpenXmlWorkbookInspector().InspectAsync(stream));

        Assert.Equal(ExcelImportErrorCode.CellLimitExceeded, error.Code);
        Assert.Equal(16_385, error.ObservedCount);
    }

    [Fact]
    public async Task InspectorRejectsMacrosAndExternalRelationshipsWithoutLeakingTargets()
    {
        var macroPath = WriteMacroWorkbook();
        await using (var stream = File.OpenRead(macroPath))
        {
            var error = await Assert.ThrowsAsync<ExcelImportException>(() => new OpenXmlWorkbookInspector().InspectAsync(stream));
            Assert.Equal(ExcelImportErrorCode.UnsafePackage, error.Code);
        }

        var externalPath = WriteExternalRelationshipWorkbook();
        await using (var stream = File.OpenRead(externalPath))
        {
            var error = await Assert.ThrowsAsync<ExcelImportException>(() => new OpenXmlWorkbookInspector().InspectAsync(stream));
            Assert.Equal(ExcelImportErrorCode.UnsafePackage, error.Code);
            Assert.DoesNotContain("evil.example", error.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task InspectorHonorsCancellation()
    {
        var path = WriteWorkbook(["链接"], [["https://pan.baidu.com/s/id"]]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await using var stream = File.OpenRead(path);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new OpenXmlWorkbookInspector().InspectAsync(stream, cancellation.Token));
    }

    [Fact]
    public async Task InspectionModelToStringsRedactHeadersAndRawRows()
    {
        var path = WriteWorkbook(
            ["private-header"],
            [["https://pan.baidu.com/s/private-id"]]);
        await using var stream = File.OpenRead(path);

        var inspection = await new OpenXmlWorkbookInspector().InspectAsync(stream);

        Assert.DoesNotContain("private-header", inspection.Columns[0].ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-id", inspection.SampleRows[0].ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private", inspection.ToString(), StringComparison.Ordinal);
    }

    private static TabularImportColumnRole Role(ImportColumnMappingSuggestion suggestion, int index) =>
        suggestion.Columns.Single(column => column.SourceColumnIndex == index).Role;

    private string WriteWorkbook(string[]? headers, string[][] rows)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, $"{Guid.NewGuid():N}.xlsx");
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
        uint rowNumber = 1;
        if (headers is not null) WriteInlineRow(writer, rowNumber++, headers);
        foreach (var row in rows) WriteInlineRow(writer, rowNumber++, row);
        writer.WriteEndElement();
        writer.WriteEndElement();
        return path;
    }

    private string WriteWorkbookWithChartSheetFirst()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, $"{Guid.NewGuid():N}.xlsx");
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        var chartSheetPart = workbookPart.AddNewPart<ChartsheetPart>();
        chartSheetPart.Chartsheet = new Chartsheet();
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        worksheetPart.Worksheet = new Worksheet(new SheetData(
            new Row(
                new Cell(new InlineString(new Text("分享链接"))) { DataType = CellValues.InlineString })
            { RowIndex = 1 },
            new Row(
                new Cell(new InlineString(new Text("https://pan.baidu.com/s/id"))) { DataType = CellValues.InlineString })
            { RowIndex = 2 }));
        workbookPart.Workbook = new Workbook(new Sheets(
            new Sheet
            {
                Id = workbookPart.GetIdOfPart(chartSheetPart),
                SheetId = 1,
                Name = "图表",
            },
            new Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = 2,
                Name = "数据",
            }));
        workbookPart.Workbook.Save();
        return path;
    }

    private string WriteSharedStringWorkbook()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "shared.xlsx");
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        var shared = workbookPart.AddNewPart<SharedStringTablePart>();
        shared.SharedStringTable = new SharedStringTable(
            new SharedStringItem(new Text("链接")),
            new SharedStringItem(new Text("https://pan.baidu.com/s/id")));
        workbookPart.Workbook = new Workbook(new Sheets(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart), SheetId = 1, Name = "导入",
        }));
        using var writer = OpenXmlWriter.Create(worksheetPart);
        writer.WriteStartElement(new Worksheet());
        writer.WriteStartElement(new SheetData());
        writer.WriteStartElement(new Row { RowIndex = 1 });
        writer.WriteElement(new Cell { DataType = CellValues.SharedString, CellValue = new CellValue(0) });
        writer.WriteEndElement();
        writer.WriteStartElement(new Row { RowIndex = 2 });
        writer.WriteElement(new Cell { DataType = CellValues.SharedString, CellValue = new CellValue(1) });
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
        return path;
    }

    private string WriteMacroWorkbook()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "macro.xlsm");
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.MacroEnabledWorkbook);
        var workbookPart = document.AddWorkbookPart();
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        var vba = workbookPart.AddNewPart<VbaProjectPart>();
        using (var target = vba.GetStream(FileMode.Create, FileAccess.Write)) target.Write([1, 2, 3]);
        workbookPart.Workbook = new Workbook(new Sheets(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart), SheetId = 1, Name = "导入",
        }));
        worksheetPart.Worksheet = new Worksheet(new SheetData());
        return path;
    }

    private string WriteSparseColumnWorkbook(string cellReference)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "sparse.xlsx");
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        workbookPart.Workbook = new Workbook(new Sheets(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart), SheetId = 1, Name = "导入",
        }));
        worksheetPart.Worksheet = new Worksheet(new SheetData(new Row(
            new Cell(new InlineString(new Text("value")))
            {
                CellReference = cellReference,
                DataType = CellValues.InlineString,
            }) { RowIndex = 1 }));
        return path;
    }

    private string WriteExternalRelationshipWorkbook()
    {
        var path = WriteWorkbook(["链接"], [["https://pan.baidu.com/s/id"]]);
        using var document = SpreadsheetDocument.Open(path, true);
        document.WorkbookPart!.WorksheetParts.Single().AddHyperlinkRelationship(
            new Uri("https://evil.example/private"),
            isExternal: true);
        return path;
    }

    private static void WriteInlineRow(OpenXmlWriter writer, uint rowIndex, params string[] values)
    {
        writer.WriteStartElement(new Row { RowIndex = rowIndex });
        foreach (var value in values)
        {
            writer.WriteElement(new Cell(
                new InlineString(new Text(value) { Space = SpaceProcessingModeValues.Preserve }))
            {
                DataType = CellValues.InlineString,
            });
        }
        writer.WriteEndElement();
    }

    private static async Task<List<ImportSourceRow>> ReadAllAsync(IAsyncEnumerable<ImportSourceRow> source)
    {
        var result = new List<ImportSourceRow>();
        await foreach (var row in source) result.Add(row);
        return result;
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
