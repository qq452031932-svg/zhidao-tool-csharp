using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using System.Globalization;

namespace BaiduBatchTransfer.Infrastructure.Excel;

public sealed record ExcelResultRow(
    long SourceRowNumber,
    string ShareUrl,
    string Disposition,
    string? Account,
    string? TargetPath,
    string? Note,
    string? ErrorMessage)
{
    public override string ToString() =>
        $"ExcelResultRow {{ SourceRowNumber = {SourceRowNumber}, UserContent = [REDACTED] }}";
}

public sealed class OpenXmlResultWriter
{
    private static readonly string[] Headers = ["源行号", "分享链接", "状态", "账号", "目标目录", "备注", "错误"];

    public async Task WriteAsync(
        Stream stream,
        IAsyncEnumerable<ExcelResultRow> rows,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(rows);
        using var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook, autoSave: true);
        var workbookPart = document.AddWorkbookPart();
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>("rId1");
        OpenXmlTemplateWriter.WriteWorkbook(workbookPart, "rId1", "导入结果");
        using var writer = OpenXmlWriter.Create(worksheetPart);
        writer.WriteStartElement(new Worksheet());
        writer.WriteStartElement(new SheetData());
        writer.WriteStartElement(new Row { RowIndex = 1 });
        foreach (var header in Headers) WriteTextCell(writer, header);
        writer.WriteEndElement();
        uint outputRow = 1;
        await foreach (var row in rows.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            outputRow = checked(outputRow + 1);
            writer.WriteStartElement(new Row { RowIndex = outputRow });
            writer.WriteElement(new Cell
            {
                DataType = CellValues.Number,
                CellValue = new CellValue(row.SourceRowNumber.ToString(CultureInfo.InvariantCulture)),
            });
            WriteTextCell(writer, row.ShareUrl);
            WriteTextCell(writer, row.Disposition);
            WriteTextCell(writer, row.Account);
            WriteTextCell(writer, row.TargetPath);
            WriteTextCell(writer, row.Note);
            WriteTextCell(writer, row.ErrorMessage);
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteTextCell(OpenXmlWriter writer, string? value) =>
        writer.WriteElement(new Cell(
            new InlineString(new Text(ExcelTextSanitizer.Sanitize(value)) { Space = SpaceProcessingModeValues.Preserve }))
        {
            DataType = CellValues.InlineString,
        });
}
