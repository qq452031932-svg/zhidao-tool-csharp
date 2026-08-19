using System.IO.Compression;
using System.Runtime.CompilerServices;
using BaiduBatchTransfer.Core.Abstractions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace BaiduBatchTransfer.Infrastructure.Excel;

public sealed record OpenXmlImportLimits(
    long MaxWorkbookBytes = 256L * 1024 * 1024,
    long MaxPartBytes = 128L * 1024 * 1024,
    int MaxRows = 250_000,
    int MaxCellsPerRow = 64,
    int MaxSharedStrings = 250_000,
    long MaxSharedStringCharacters = 64L * 1024 * 1024,
    double MaxCompressionRatio = 1_000);

public enum ExcelImportErrorCode
{
    InvalidWorkbook,
    UnsafePackage,
    MissingWorksheet,
    MissingRequiredHeader,
    DuplicateHeader,
    RowLimitExceeded,
    CellLimitExceeded,
    SharedStringLimitExceeded,
}

public sealed class ExcelImportException : Exception
{
    public ExcelImportException(ExcelImportErrorCode code, string message, Exception? innerException = null)
        : base(message, innerException) => Code = code;

    public ExcelImportErrorCode Code { get; }

    public override string ToString() =>
        $"ExcelImportException {{ Code = {Code}, Message = {Message} }}";
}

public sealed class OpenXmlImportReader
{
    private readonly OpenXmlImportLimits limits;

    public OpenXmlImportReader(OpenXmlImportLimits? limits = null)
    {
        this.limits = limits ?? new OpenXmlImportLimits();
        if (this.limits.MaxWorkbookBytes <= 0 || this.limits.MaxPartBytes <= 0 ||
            this.limits.MaxRows <= 0 || this.limits.MaxCellsPerRow <= 0 ||
            this.limits.MaxSharedStrings <= 0 || this.limits.MaxSharedStringCharacters <= 0 ||
            this.limits.MaxCompressionRatio <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limits));
        }
    }

    public async IAsyncEnumerable<ImportSourceRow> ReadAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ExcelImportException(ExcelImportErrorCode.InvalidWorkbook, "The workbook stream must be readable and seekable.");
        }

        ValidatePackage(stream);
        stream.Position = 0;
        SpreadsheetDocument document;
        try
        {
            document = SpreadsheetDocument.Open(stream, false, new OpenSettings { AutoSave = false });
        }
        catch (Exception error) when (error is OpenXmlPackageException or InvalidDataException or IOException)
        {
            throw new ExcelImportException(ExcelImportErrorCode.InvalidWorkbook, "The file is not a valid Open XML workbook.", error);
        }

        using (document)
        {
            if (document.DocumentType != SpreadsheetDocumentType.Workbook)
            {
                throw new ExcelImportException(ExcelImportErrorCode.UnsafePackage, "Macro-enabled and template workbook types are not accepted.");
            }

            var workbookPart = document.WorkbookPart
                ?? throw new ExcelImportException(ExcelImportErrorCode.InvalidWorkbook, "The workbook part is missing.");
            if (workbookPart.VbaProjectPart is not null || workbookPart.ExternalWorkbookParts.Any())
            {
                throw new ExcelImportException(ExcelImportErrorCode.UnsafePackage, "Workbooks containing macros or external workbook links are not accepted.");
            }

            var relationshipId = ReadFirstWorksheetRelationshipId(workbookPart);
            if (relationshipId is null || workbookPart.GetPartById(relationshipId) is not WorksheetPart worksheetPart)
            {
                throw new ExcelImportException(ExcelImportErrorCode.MissingWorksheet, "The workbook does not contain a readable worksheet.");
            }

            var sharedStrings = ReadSharedStrings(workbookPart.SharedStringTablePart);
            using var reader = OpenXmlReader.Create(worksheetPart);
            HeaderMap? headers = null;
            var physicalRowCount = 0;
            var fallbackRowNumber = 0;
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.ElementType != typeof(Row) || !reader.IsStartElement)
                {
                    continue;
                }

                var row = reader.LoadCurrentElement() as Row
                    ?? throw new ExcelImportException(ExcelImportErrorCode.InvalidWorkbook, "A worksheet row could not be read.");
                physicalRowCount++;
                fallbackRowNumber++;
                var sourceRowNumber = checked((int)(row.RowIndex?.Value ?? (uint)fallbackRowNumber));
                var values = ReadCells(row, sharedStrings);
                if (headers is null)
                {
                    if (values.Count == 0 || values.Values.All(string.IsNullOrWhiteSpace)) continue;
                    headers = HeaderMap.Create(values);
                    continue;
                }

                if (physicalRowCount - 1 > limits.MaxRows)
                {
                    throw new ExcelImportException(ExcelImportErrorCode.RowLimitExceeded, "The workbook contains more data rows than allowed.");
                }

                if (values.Count == 0 || values.Values.All(string.IsNullOrWhiteSpace)) continue;
                var shareUrl = headers.Get(values, ImportColumn.ShareUrl);
                yield return new ImportSourceRow(
                    sourceRowNumber,
                    shareUrl ?? string.Empty,
                    headers.Get(values, ImportColumn.ExtractionCode),
                    headers.Get(values, ImportColumn.TargetPath),
                    headers.Get(values, ImportColumn.OuterFolder),
                    headers.Get(values, ImportColumn.Account),
                    Note: headers.Get(values, ImportColumn.Note),
                    ValidationError: string.IsNullOrWhiteSpace(shareUrl) ? "The share link is missing." : null);
                if ((physicalRowCount & 255) == 0) await Task.Yield();
            }

            if (headers is null)
            {
                throw new ExcelImportException(ExcelImportErrorCode.MissingRequiredHeader, "The required share-link header is missing.");
            }
        }
    }

    private void ValidatePackage(Stream stream)
    {
        if (stream.Length > limits.MaxWorkbookBytes)
        {
            throw new ExcelImportException(ExcelImportErrorCode.UnsafePackage, "The workbook package exceeds the allowed size.");
        }

        try
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            foreach (var entry in archive.Entries)
            {
                if (entry.Length > limits.MaxPartBytes ||
                    (entry.Length > 1_048_576 && entry.CompressedLength == 0) ||
                    (entry.CompressedLength > 0 && entry.Length / (double)entry.CompressedLength > limits.MaxCompressionRatio))
                {
                    throw new ExcelImportException(ExcelImportErrorCode.UnsafePackage, "The workbook contains an oversized or suspiciously compressed part.");
                }
            }
        }
        catch (ExcelImportException)
        {
            throw;
        }
        catch (InvalidDataException error)
        {
            throw new ExcelImportException(ExcelImportErrorCode.InvalidWorkbook, "The file is not a valid Open XML package.", error);
        }
        finally
        {
            stream.Position = 0;
        }
    }

    private static string? ReadFirstWorksheetRelationshipId(WorkbookPart workbookPart)
    {
        using var reader = OpenXmlReader.Create(workbookPart);
        while (reader.Read())
        {
            if (reader.ElementType != typeof(Sheet) || !reader.IsStartElement) continue;
            var sheet = reader.LoadCurrentElement() as Sheet
                ?? throw new ExcelImportException(ExcelImportErrorCode.InvalidWorkbook, "A worksheet declaration could not be read.");
            if (!string.IsNullOrWhiteSpace(sheet.Id?.Value)) return sheet.Id!.Value;
        }

        return null;
    }

    private IReadOnlyList<string> ReadSharedStrings(SharedStringTablePart? part)
    {
        if (part is null) return Array.Empty<string>();
        var values = new List<string>();
        long totalCharacters = 0;
        using var reader = OpenXmlReader.Create(part);
        while (reader.Read())
        {
            if (reader.ElementType != typeof(SharedStringItem) || !reader.IsStartElement) continue;
            if (values.Count >= limits.MaxSharedStrings)
                throw new ExcelImportException(ExcelImportErrorCode.SharedStringLimitExceeded, "The shared-string table exceeds the allowed entry count.");
            var item = reader.LoadCurrentElement() as SharedStringItem
                ?? throw new ExcelImportException(ExcelImportErrorCode.InvalidWorkbook, "A shared-string entry could not be read.");
            var value = item.InnerText;
            totalCharacters = checked(totalCharacters + value.Length);
            if (totalCharacters > limits.MaxSharedStringCharacters)
                throw new ExcelImportException(ExcelImportErrorCode.SharedStringLimitExceeded, "The shared-string table exceeds the allowed text size.");
            values.Add(value);
        }

        return values;
    }

    private Dictionary<int, string> ReadCells(Row row, IReadOnlyList<string> sharedStrings)
    {
        var cells = row.Elements<Cell>().ToArray();
        if (cells.Length > limits.MaxCellsPerRow)
            throw new ExcelImportException(ExcelImportErrorCode.CellLimitExceeded, "A worksheet row contains more cells than allowed.");
        var values = new Dictionary<int, string>();
        var fallbackColumn = 0;
        foreach (var cell in cells)
        {
            var column = GetColumnIndex(cell.CellReference?.Value) ?? fallbackColumn;
            fallbackColumn = column + 1;
            values[column] = ReadCellValue(cell, sharedStrings);
        }

        return values;
    }

    private static string ReadCellValue(Cell cell, IReadOnlyList<string> sharedStrings)
    {
        if (cell.DataType?.Value == CellValues.InlineString) return cell.InlineString?.InnerText ?? string.Empty;
        var raw = cell.CellValue?.Text ?? string.Empty;
        if (cell.DataType?.Value != CellValues.SharedString) return raw;
        return int.TryParse(raw, out var index) && index >= 0 && index < sharedStrings.Count
            ? sharedStrings[index]
            : throw new ExcelImportException(ExcelImportErrorCode.InvalidWorkbook, "A shared-string cell contains an invalid reference.");
    }

    private static int? GetColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return null;
        var value = 0;
        var letters = 0;
        foreach (var character in reference)
        {
            if (character is not (>= 'A' and <= 'Z') and not (>= 'a' and <= 'z')) break;
            value = checked(value * 26 + char.ToUpperInvariant(character) - 'A' + 1);
            letters++;
        }

        return letters == 0 ? null : value - 1;
    }

    private enum ImportColumn { ShareUrl, ExtractionCode, TargetPath, OuterFolder, Account, Note }

    private sealed class HeaderMap(Dictionary<ImportColumn, int> indexes)
    {
        private static readonly Dictionary<string, ImportColumn> Aliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["分享链接"] = ImportColumn.ShareUrl, ["链接"] = ImportColumn.ShareUrl,
            ["share url"] = ImportColumn.ShareUrl, ["share link"] = ImportColumn.ShareUrl,
            ["url"] = ImportColumn.ShareUrl, ["link"] = ImportColumn.ShareUrl,
            ["提取码"] = ImportColumn.ExtractionCode, ["访问码"] = ImportColumn.ExtractionCode,
            ["extraction code"] = ImportColumn.ExtractionCode, ["code"] = ImportColumn.ExtractionCode,
            ["目标目录"] = ImportColumn.TargetPath, ["目标路径"] = ImportColumn.TargetPath, ["target path"] = ImportColumn.TargetPath,
            ["外层目录"] = ImportColumn.OuterFolder, ["创建目录名称"] = ImportColumn.OuterFolder, ["outer folder"] = ImportColumn.OuterFolder,
            ["指定账号"] = ImportColumn.Account, ["账号"] = ImportColumn.Account, ["account"] = ImportColumn.Account,
            ["备注"] = ImportColumn.Note, ["note"] = ImportColumn.Note,
        };

        public static HeaderMap Create(IReadOnlyDictionary<int, string> values)
        {
            var indexes = new Dictionary<ImportColumn, int>();
            foreach (var (index, rawValue) in values)
            {
                var value = string.Join(' ', rawValue.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                if (!Aliases.TryGetValue(value, out var column)) continue;
                if (!indexes.TryAdd(column, index))
                    throw new ExcelImportException(ExcelImportErrorCode.DuplicateHeader, $"The workbook maps more than one header to {SafeName(column)}.");
            }

            if (!indexes.ContainsKey(ImportColumn.ShareUrl))
                throw new ExcelImportException(ExcelImportErrorCode.MissingRequiredHeader, "The required share-link header is missing.");
            return new HeaderMap(indexes);
        }

        public string? Get(IReadOnlyDictionary<int, string> values, ImportColumn column) =>
            indexes.TryGetValue(column, out var index) && values.TryGetValue(index, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value.Trim()
                : null;

        private static string SafeName(ImportColumn column) => column switch
        {
            ImportColumn.ShareUrl => "share link",
            ImportColumn.ExtractionCode => "extraction code",
            ImportColumn.TargetPath => "target path",
            ImportColumn.OuterFolder => "outer folder",
            ImportColumn.Account => "account",
            ImportColumn.Note => "note",
            _ => "column",
        };
    }
}
