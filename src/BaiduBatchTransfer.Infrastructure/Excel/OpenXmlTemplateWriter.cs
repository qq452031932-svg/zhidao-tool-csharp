using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace BaiduBatchTransfer.Infrastructure.Excel;

public sealed class OpenXmlTemplateWriter
{
    private static readonly string[] Headers = ["分享链接", "提取码", "目标目录", "外层目录", "指定账号", "备注"];

    public Task WriteAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();
        using var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook, autoSave: true);
        var workbookPart = document.AddWorkbookPart();
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>("rId1");
        AddTextStyles(workbookPart);
        WriteWorkbook(workbookPart, "rId1", "导入模板");
        using var writer = OpenXmlWriter.Create(worksheetPart);
        writer.WriteStartElement(new Worksheet());
        writer.WriteStartElement(new Columns());
        writer.WriteElement(new Column { Min = 1, Max = 1, Width = 48, CustomWidth = true, Style = 1 });
        writer.WriteElement(new Column { Min = 2, Max = 6, Width = 22, CustomWidth = true, Style = 1 });
        writer.WriteEndElement();
        writer.WriteStartElement(new SheetData());
        writer.WriteStartElement(new Row { RowIndex = 1 });
        foreach (var header in Headers) WriteTextCell(writer, header);
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
        return Task.CompletedTask;
    }

    internal static void WriteTextCell(OpenXmlWriter writer, string? value) =>
        writer.WriteElement(new Cell(
            new InlineString(new Text(ExcelTextSanitizer.Sanitize(value)) { Space = SpaceProcessingModeValues.Preserve }))
        {
            DataType = CellValues.InlineString,
            StyleIndex = 1,
        });

    internal static void WriteWorkbook(WorkbookPart workbookPart, string relationshipId, string sheetName)
    {
        using var writer = OpenXmlWriter.Create(workbookPart);
        writer.WriteStartElement(new Workbook());
        writer.WriteStartElement(new Sheets());
        writer.WriteElement(new Sheet { Id = relationshipId, SheetId = 1, Name = sheetName });
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void AddTextStyles(WorkbookPart workbookPart)
    {
        var styles = workbookPart.AddNewPart<WorkbookStylesPart>();
        styles.Stylesheet = new Stylesheet(
            new Fonts(new Font()) { Count = 1 },
            new Fills(
                new Fill(new PatternFill { PatternType = PatternValues.None }),
                new Fill(new PatternFill { PatternType = PatternValues.Gray125 })) { Count = 2 },
            new Borders(new Border()) { Count = 1 },
            new CellStyleFormats(new CellFormat()) { Count = 1 },
            new CellFormats(
                new CellFormat(),
                new CellFormat { NumberFormatId = 49, ApplyNumberFormat = true }) { Count = 2 },
            new CellStyles(new CellStyle { Name = "Normal", FormatId = 0, BuiltinId = 0 }) { Count = 1 });
    }
}
