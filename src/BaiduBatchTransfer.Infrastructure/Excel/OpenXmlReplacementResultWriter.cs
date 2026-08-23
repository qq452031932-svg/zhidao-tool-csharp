using BaiduBatchTransfer.Application.Jobs;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace BaiduBatchTransfer.Infrastructure.Excel;

public enum ReplacementWorkbookWriteErrorCode
{
    InvalidLayout,
    CellTextTooLong,
    ColumnCountMismatch,
}

public sealed class ReplacementWorkbookWriteException(
    ReplacementWorkbookWriteErrorCode code,
    string safeMessage) : IOException(safeMessage)
{
    public ReplacementWorkbookWriteErrorCode Code { get; } = code;
}

public sealed class OpenXmlReplacementResultWriter : IReplacementWorkbookWriter
{
    private const string SpreadsheetNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string OfficeRelationshipNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PackageRelationshipNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string ContentTypeNamespace = "http://schemas.openxmlformats.org/package/2006/content-types";
    public const int ExcelMaximumRowsPerSheet = 1_048_576;
    public const int ExcelMaximumColumns = 16_384;
    public const int ExcelMaximumTextLength = 32_767;

    private readonly int maxRowsPerSheet;

    public OpenXmlReplacementResultWriter(int maxRowsPerSheet = ExcelMaximumRowsPerSheet)
    {
        if (maxRowsPerSheet is < 2 or > ExcelMaximumRowsPerSheet)
            throw new ArgumentOutOfRangeException(nameof(maxRowsPerSheet));
        this.maxRowsPerSheet = maxRowsPerSheet;
    }

    public async Task WriteAsync(
        Stream output,
        ReplacementWorkbookLayout layout,
        IAsyncEnumerable<ReplacementWorkbookRow> rows,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(rows);
        cancellationToken.ThrowIfCancellationRequested();
        if (layout.Headers.Length is < 1 or > ExcelMaximumColumns)
            throw new ReplacementWorkbookWriteException(
                ReplacementWorkbookWriteErrorCode.InvalidLayout,
                "The replacement workbook column layout is invalid.");

        var headers = new string[layout.Headers.Length];
        for (var index = 0; index < layout.Headers.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            headers[index] = SanitizeCellText(layout.Headers[index]);
        }

        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: Encoding.UTF8);
        var sheets = new List<ResultSheet>();
        WorksheetStreamWriter? writer = null;
        try
        {
            writer = StartSheet(archive, sheets, headers, cancellationToken);
            uint outputRow = 1;
            await foreach (var row in rows.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (row.TextCells.Length != headers.Length)
                    throw new ReplacementWorkbookWriteException(
                        ReplacementWorkbookWriteErrorCode.ColumnCountMismatch,
                        "A replacement workbook row does not match the declared column layout.");

                if (outputRow >= maxRowsPerSheet)
                {
                    writer.Finish();
                    writer.Dispose();
                    writer = StartSheet(archive, sheets, headers, cancellationToken);
                    outputRow = 1;
                }

                outputRow = checked(outputRow + 1);
                writer.StartRow(outputRow);
                foreach (var value in row.TextCells)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    writer.WriteTextCell(SanitizeCellText(value));
                }
                writer.EndRow();
            }

            writer.Finish();
            writer.Dispose();
            writer = null;
            WritePackageParts(archive, sheets);
        }
        finally
        {
            writer?.Dispose();
        }
    }

    private static WorksheetStreamWriter StartSheet(
        ZipArchive archive,
        ICollection<ResultSheet> sheets,
        IReadOnlyList<string> headers,
        CancellationToken cancellationToken)
    {
        var sheetNumber = checked(sheets.Count + 1);
        sheets.Add(new(
            $"rId{sheetNumber}",
            checked((uint)sheetNumber),
            sheetNumber == 1 ? "替换结果" : $"替换结果 {sheetNumber}"));
        var writer = new WorksheetStreamWriter(
            archive.CreateEntry($"xl/worksheets/sheet{sheetNumber}.xml", CompressionLevel.Fastest));
        try
        {
            writer.StartRow(1);
            foreach (var header in headers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.WriteTextCell(header);
            }
            writer.EndRow();
            return writer;
        }
        catch
        {
            writer.Dispose();
            throw;
        }
    }

    private static void WritePackageParts(ZipArchive archive, IReadOnlyList<ResultSheet> sheets)
    {
        WriteContentTypes(archive, sheets.Count);
        WriteRootRelationships(archive);
        WriteWorkbook(archive, sheets);
        WriteWorkbookRelationships(archive, sheets);
    }

    private static void WriteContentTypes(ZipArchive archive, int sheetCount)
    {
        using var writer = CreateXmlWriter(archive.CreateEntry("[Content_Types].xml", CompressionLevel.Fastest));
        writer.WriteStartDocument();
        writer.WriteStartElement("Types", ContentTypeNamespace);
        WriteEmptyElement(writer, "Default", ContentTypeNamespace,
            ("Extension", "rels"), ("ContentType", "application/vnd.openxmlformats-package.relationships+xml"));
        WriteEmptyElement(writer, "Default", ContentTypeNamespace,
            ("Extension", "xml"), ("ContentType", "application/xml"));
        WriteEmptyElement(writer, "Override", ContentTypeNamespace,
            ("PartName", "/xl/workbook.xml"),
            ("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"));
        for (var sheetNumber = 1; sheetNumber <= sheetCount; sheetNumber++)
        {
            WriteEmptyElement(writer, "Override", ContentTypeNamespace,
                ("PartName", $"/xl/worksheets/sheet{sheetNumber}.xml"),
                ("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"));
        }
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WriteRootRelationships(ZipArchive archive)
    {
        using var writer = CreateXmlWriter(archive.CreateEntry("_rels/.rels", CompressionLevel.Fastest));
        writer.WriteStartDocument();
        writer.WriteStartElement("Relationships", PackageRelationshipNamespace);
        WriteEmptyElement(writer, "Relationship", PackageRelationshipNamespace,
            ("Id", "rId1"),
            ("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"),
            ("Target", "xl/workbook.xml"));
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WriteWorkbook(ZipArchive archive, IReadOnlyList<ResultSheet> sheets)
    {
        using var writer = CreateXmlWriter(archive.CreateEntry("xl/workbook.xml", CompressionLevel.Fastest));
        writer.WriteStartDocument();
        writer.WriteStartElement("workbook", SpreadsheetNamespace);
        writer.WriteAttributeString("xmlns", "r", null, OfficeRelationshipNamespace);
        writer.WriteStartElement("sheets", SpreadsheetNamespace);
        foreach (var sheet in sheets)
        {
            writer.WriteStartElement("sheet", SpreadsheetNamespace);
            writer.WriteAttributeString("name", sheet.Name);
            writer.WriteAttributeString("sheetId", sheet.SheetId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteAttributeString("r", "id", OfficeRelationshipNamespace, sheet.RelationshipId);
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WriteWorkbookRelationships(ZipArchive archive, IReadOnlyList<ResultSheet> sheets)
    {
        using var writer = CreateXmlWriter(archive.CreateEntry("xl/_rels/workbook.xml.rels", CompressionLevel.Fastest));
        writer.WriteStartDocument();
        writer.WriteStartElement("Relationships", PackageRelationshipNamespace);
        for (var index = 0; index < sheets.Count; index++)
        {
            WriteEmptyElement(writer, "Relationship", PackageRelationshipNamespace,
                ("Id", sheets[index].RelationshipId),
                ("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"),
                ("Target", $"worksheets/sheet{index + 1}.xml"));
        }
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static XmlWriter CreateXmlWriter(ZipArchiveEntry entry) => XmlWriter.Create(
        entry.Open(),
        new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            CloseOutput = true,
            CheckCharacters = true,
        });

    private static void WriteEmptyElement(
        XmlWriter writer,
        string localName,
        string xmlNamespace,
        params (string Name, string Value)[] attributes)
    {
        writer.WriteStartElement(localName, xmlNamespace);
        foreach (var (name, value) in attributes) writer.WriteAttributeString(name, value);
        writer.WriteEndElement();
    }

    private static string SanitizeCellText(string? value)
    {
        var sanitized = ExcelTextSanitizer.Sanitize(value);
        if (sanitized.Length > ExcelMaximumTextLength)
            throw new ReplacementWorkbookWriteException(
                ReplacementWorkbookWriteErrorCode.CellTextTooLong,
                "A replacement workbook cell exceeds Excel's text limit.");
        return sanitized;
    }

    private sealed record ResultSheet(string RelationshipId, uint SheetId, string Name);

    private sealed class WorksheetStreamWriter : IDisposable
    {
        private readonly XmlWriter writer;
        private bool finished;

        public WorksheetStreamWriter(ZipArchiveEntry entry)
        {
            writer = CreateXmlWriter(entry);
            writer.WriteStartDocument();
            writer.WriteStartElement("worksheet", SpreadsheetNamespace);
            writer.WriteStartElement("sheetData", SpreadsheetNamespace);
        }

        public void StartRow(uint rowIndex)
        {
            writer.WriteStartElement("row", SpreadsheetNamespace);
            writer.WriteAttributeString("r", rowIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        public void EndRow() => writer.WriteEndElement();

        public void WriteTextCell(string value)
        {
            writer.WriteStartElement("c", SpreadsheetNamespace);
            writer.WriteAttributeString("t", "inlineStr");
            writer.WriteStartElement("is", SpreadsheetNamespace);
            writer.WriteStartElement("t", SpreadsheetNamespace);
            writer.WriteAttributeString("xml", "space", "http://www.w3.org/XML/1998/namespace", "preserve");
            writer.WriteString(value);
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        public void Finish()
        {
            if (finished) return;
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndDocument();
            writer.Flush();
            finished = true;
        }

        public void Dispose() => writer.Dispose();
    }
}
