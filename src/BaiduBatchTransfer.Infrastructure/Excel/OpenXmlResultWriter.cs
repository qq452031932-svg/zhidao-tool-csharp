using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using System.Globalization;
using BaiduBatchTransfer.Application.Jobs;

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

public sealed class OpenXmlResultWriter : IBatchResultWriter
{
    public const int ExcelMaximumRowsPerSheet = 1_048_576;

    private static readonly string[] Headers = ["源行号", "分享链接", "状态", "账号", "目标目录", "备注", "错误"];
    private readonly int maxRowsPerSheet;

    public OpenXmlResultWriter(int maxRowsPerSheet = ExcelMaximumRowsPerSheet)
    {
        if (maxRowsPerSheet is < 2 or > ExcelMaximumRowsPerSheet)
            throw new ArgumentOutOfRangeException(nameof(maxRowsPerSheet));
        this.maxRowsPerSheet = maxRowsPerSheet;
    }

    public async Task WriteAsync(
        Stream stream,
        IAsyncEnumerable<ExcelResultRow> rows,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(rows);
        using var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook, autoSave: true);
        var workbookPart = document.AddWorkbookPart();
        var sheets = new List<ResultSheet>();
        OpenXmlWriter? writer = null;
        try
        {
            writer = StartSheet(workbookPart, sheets);
            uint outputRow = 1;
            await foreach (var row in rows.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (outputRow >= maxRowsPerSheet)
                {
                    FinishSheet(writer);
                    writer.Dispose();
                    writer = StartSheet(workbookPart, sheets);
                    outputRow = 1;
                }

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
            FinishSheet(writer);
            writer.Dispose();
            writer = null;
            WriteWorkbook(workbookPart, sheets);
        }
        finally
        {
            writer?.Dispose();
        }
    }

    public Task WriteAsync(
        Stream stream,
        IAsyncEnumerable<BatchResultExportRow> rows,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return WriteAsync(stream, ConvertRowsAsync(rows, cancellationToken), cancellationToken);
    }

    private static async IAsyncEnumerable<ExcelResultRow> ConvertRowsAsync(
        IAsyncEnumerable<BatchResultExportRow> rows,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var row in rows.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return new(
                row.SourceRowNumber,
                row.ShareUrl,
                row.Disposition,
                row.Account,
                row.TargetPath,
                row.Note,
                row.ErrorMessage);
        }
    }

    private static OpenXmlWriter StartSheet(WorkbookPart workbookPart, ICollection<ResultSheet> sheets)
    {
        var sheetNumber = sheets.Count + 1;
        var relationshipId = $"rId{sheetNumber}";
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>(relationshipId);
        sheets.Add(new ResultSheet(
            relationshipId,
            (uint)sheetNumber,
            sheetNumber == 1 ? "导入结果" : $"导入结果 {sheetNumber}"));
        var writer = OpenXmlWriter.Create(worksheetPart);
        writer.WriteStartElement(new Worksheet());
        writer.WriteStartElement(new SheetData());
        writer.WriteStartElement(new Row { RowIndex = 1 });
        foreach (var header in Headers) WriteTextCell(writer, header);
        writer.WriteEndElement();
        return writer;
    }

    private static void FinishSheet(OpenXmlWriter writer)
    {
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteWorkbook(WorkbookPart workbookPart, IEnumerable<ResultSheet> sheets)
    {
        using var writer = OpenXmlWriter.Create(workbookPart);
        writer.WriteStartElement(new Workbook());
        writer.WriteStartElement(new Sheets());
        foreach (var sheet in sheets)
        {
            writer.WriteElement(new Sheet
            {
                Id = sheet.RelationshipId,
                SheetId = sheet.SheetId,
                Name = sheet.Name,
            });
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

    private sealed record ResultSheet(string RelationshipId, uint SheetId, string Name);
}
