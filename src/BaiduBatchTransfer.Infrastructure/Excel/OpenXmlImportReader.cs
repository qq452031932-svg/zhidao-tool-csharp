using System.Buffers;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Services;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace BaiduBatchTransfer.Infrastructure.Excel;

public sealed record OpenXmlImportLimits(
    long MaxWorkbookBytes = 256L * 1024 * 1024,
    long MaxPartBytes = 128L * 1024 * 1024,
    int MaxRows = 1_048_575,
    int MaxCellsPerRow = 64,
    int MaxCharactersPerCell = 1_048_576,
    int MaxSharedStrings = 250_000,
    int MaxCharactersPerSharedStringItem = 1_048_576,
    long MaxSharedStringCharacters = 64L * 1024 * 1024,
    double MaxCompressionRatio = 1_000,
    int MaxArchiveEntries = 2_048,
    long MaxTotalUncompressedBytes = 512L * 1024 * 1024)
{
    public const int ExcelMaximumRows = 1_048_576;
    public const int ExcelMaximumDataRows = ExcelMaximumRows - 1;
    public const int ExcelMaximumColumns = 16_384;
}

public enum ExcelImportErrorCode
{
    InvalidWorkbook,
    UnsafePackage,
    MissingWorksheet,
    MissingRequiredHeader,
    DuplicateHeader,
    ColumnMappingMismatch,
    InvalidRowOrder,
    RowLimitExceeded,
    CellLimitExceeded,
    SharedStringLimitExceeded,
    SharedStringItemLimitExceeded,
    CellTextLimitExceeded,
}

public sealed class ExcelImportException : Exception
{
    public ExcelImportException(
        ExcelImportErrorCode code,
        string message,
        Exception? innerException = null,
        int? observedCount = null)
        : base(message, innerException)
    {
        Code = code;
        ObservedCount = observedCount;
    }

    public ExcelImportErrorCode Code { get; }

    public int? ObservedCount { get; }

    public override string ToString() =>
        $"ExcelImportException {{ Code = {Code}, ObservedCount = {ObservedCount}, Message = {Message} }}";
}

public sealed class OpenXmlImportReader
{
    private readonly OpenXmlImportLimits limits;

    public OpenXmlImportReader(OpenXmlImportLimits? limits = null)
    {
        this.limits = limits ?? new OpenXmlImportLimits();
        if (this.limits.MaxWorkbookBytes <= 0 || this.limits.MaxPartBytes <= 0 ||
            this.limits.MaxRows <= 0 || this.limits.MaxRows > OpenXmlImportLimits.ExcelMaximumDataRows ||
            this.limits.MaxCellsPerRow <= 0 ||
            this.limits.MaxCharactersPerCell <= 0 || this.limits.MaxSharedStrings <= 0 ||
            this.limits.MaxCharactersPerSharedStringItem <= 0 || this.limits.MaxSharedStringCharacters <= 0 ||
            this.limits.MaxCompressionRatio <= 0 || this.limits.MaxArchiveEntries <= 0 ||
            this.limits.MaxTotalUncompressedBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limits));
        }
    }

    public async IAsyncEnumerable<ImportSourceRow> ReadAsync(
        Stream stream,
        ConfirmedImportColumnMapping mapping,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(mapping);
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ExcelImportException(ExcelImportErrorCode.InvalidWorkbook, "The workbook stream must be readable and seekable.");
        }

        await ValidatePackageAsync(stream, cancellationToken).ConfigureAwait(false);
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
            var workbookPart = ValidateDocument(document);

            var relationshipId = ReadFirstWorksheetRelationshipId(workbookPart);
            if (relationshipId is null || workbookPart.GetPartById(relationshipId) is not WorksheetPart worksheetPart)
            {
                throw new ExcelImportException(ExcelImportErrorCode.MissingWorksheet, "The workbook does not contain a readable worksheet.");
            }

            var sharedStrings = ReadSharedStrings(workbookPart.SharedStringTablePart, cancellationToken);
            using var reader = OpenXmlReader.Create(worksheetPart);
            var extractor = new BaiduShareUrlExtractor();
            var normalizer = new BaiduShareNormalizer();
            var physicalRowCount = 0;
            var dataRowCount = 0;
            var headerValidated = mapping.HeaderRowNumber is null;
            uint lastRowNumber = 0;
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.ElementType != typeof(Row) || !reader.IsStartElement)
                {
                    continue;
                }

                physicalRowCount++;
                var rawRowNumber = ReadRowNumber(reader, lastRowNumber);
                if (rawRowNumber is 0 or > OpenXmlImportLimits.ExcelMaximumRows)
                {
                    throw new ExcelImportException(ExcelImportErrorCode.RowLimitExceeded, "A worksheet row index exceeds the Excel format boundary.");
                }
                if (rawRowNumber <= lastRowNumber)
                {
                    throw new ExcelImportException(ExcelImportErrorCode.InvalidRowOrder, "Worksheet row indexes must be strictly increasing.");
                }
                lastRowNumber = rawRowNumber;
                var sourceRowNumber = (int)rawRowNumber;
                var values = ReadCells(reader, sharedStrings, cancellationToken);
                if (values.Keys.Any(columnIndex => columnIndex >= mapping.SourceColumnCount))
                {
                    throw new ExcelImportException(
                        ExcelImportErrorCode.ColumnMappingMismatch,
                        "A worksheet row contains a source column that was not present in the confirmed mapping.");
                }
                if (rawRowNumber == mapping.HeaderRowNumber)
                {
                    if (mapping.SourceColumns.Count == mapping.SourceColumnCount)
                    {
                        var actualHeaders = ToOrderedValues(values, mapping.SourceColumnCount);
                        if (mapping.SourceColumns.Any(column =>
                            !HeaderMatchesSnapshot(
                                actualHeaders[column.SourceColumnIndex],
                                column.Header,
                                column.SourceColumnIndex)))
                        {
                            throw new ExcelImportException(
                                ExcelImportErrorCode.ColumnMappingMismatch,
                                "The workbook header no longer matches the confirmed mapping snapshot.");
                        }
                    }
                    headerValidated = true;
                    continue;
                }

                dataRowCount++;
                if (dataRowCount > limits.MaxRows)
                {
                    throw new ExcelImportException(ExcelImportErrorCode.RowLimitExceeded, "The workbook contains more data rows than allowed.");
                }

                if (values.Count == 0 || values.Values.All(string.IsNullOrWhiteSpace)) continue;
                var rawValues = ToOrderedValues(values, Math.Max(mapping.SourceColumnCount, values.Count == 0 ? 0 : values.Keys.Max() + 1));
                var rawShareCell = Get(rawValues, mapping.ShareUrlColumnIndex);
                var originalExtractedUrl = extractor.ExtractSupportedUrl(rawShareCell);
                string? shareUrl = null;
                string? urlExtractionCode = null;
                if (originalExtractedUrl is not null)
                {
                    var normalized = normalizer.Normalize(originalExtractedUrl);
                    shareUrl = normalized.CanonicalUrl;
                    urlExtractionCode = normalized.ExtractionCode;
                }
                var extractionCode = BaiduShareUrlExtractor.NormalizeExtractionCode(
                    mapping.ExtractionCodeColumnIndex is int codeColumn ? Get(rawValues, codeColumn) : null)
                    ?? urlExtractionCode;
                yield return new ImportSourceRow(
                    sourceRowNumber,
                    shareUrl ?? string.Empty,
                    extractionCode,
                    NormalizeOptional(mapping.TargetPathColumnIndex is int targetColumn ? Get(rawValues, targetColumn) : null),
                    NormalizeOptional(mapping.OuterFolderNameColumnIndex is int folderColumn ? Get(rawValues, folderColumn) : null),
                    NormalizeOptional(mapping.RequestedAccountNameColumnIndex is int accountColumn ? Get(rawValues, accountColumn) : null),
                    Note: NormalizeOptional(mapping.NoteColumnIndex is int noteColumn ? Get(rawValues, noteColumn) : null),
                    ValidationError: shareUrl is null ? "The mapped share-link cell does not contain a supported Baidu URL." : null,
                    SourceTitle: mapping.SourceTitleColumnIndex is int titleColumn ? Get(rawValues, titleColumn) : null,
                    OriginalExtractedUrl: originalExtractedUrl,
                    RawValues: rawValues);
                if ((physicalRowCount & 255) == 0) await Task.Yield();
            }
            if (!headerValidated)
                throw new ExcelImportException(
                    ExcelImportErrorCode.ColumnMappingMismatch,
                    "The confirmed workbook header row is missing.");
        }
    }

    private static bool HeaderMatchesSnapshot(string actual, string? expected, int sourceColumnIndex)
    {
        if (string.Equals(actual, expected ?? string.Empty, StringComparison.Ordinal))
        {
            return true;
        }

        return actual.Length == 0 &&
               string.Equals(expected, $"第 {sourceColumnIndex + 1} 列", StringComparison.Ordinal);
    }

    private static string Get(IReadOnlyList<string> values, int index) =>
        index < values.Count ? values[index] : string.Empty;

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static IReadOnlyList<string> ToOrderedValues(IReadOnlyDictionary<int, string> values, int width)
    {
        var ordered = new string[width];
        Array.Fill(ordered, string.Empty);
        foreach (var (index, value) in values)
        {
            if (index >= 0 && index < ordered.Length) ordered[index] = value;
        }
        return ordered;
    }

    internal async Task ValidatePackageAsync(Stream stream, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (stream.Length > limits.MaxWorkbookBytes)
        {
            throw new ExcelImportException(ExcelImportErrorCode.UnsafePackage, "The workbook package exceeds the allowed size.");
        }

        try
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count > limits.MaxArchiveEntries)
            {
                throw new ExcelImportException(ExcelImportErrorCode.UnsafePackage, "The workbook package contains more entries than allowed.");
            }

            long declaredTotal = 0;
            long streamedTotal = 0;
            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry.Length > limits.MaxPartBytes ||
                        (entry.Length > 1_048_576 && entry.CompressedLength == 0) ||
                        (entry.CompressedLength > 0 && entry.Length / (double)entry.CompressedLength > limits.MaxCompressionRatio))
                    {
                        throw new ExcelImportException(ExcelImportErrorCode.UnsafePackage, "The workbook contains an oversized or suspiciously compressed part.");
                    }

                    declaredTotal = checked(declaredTotal + entry.Length);
                    if (declaredTotal > limits.MaxTotalUncompressedBytes)
                    {
                        throw new ExcelImportException(ExcelImportErrorCode.UnsafePackage, "The workbook package exceeds the allowed cumulative uncompressed size.");
                    }

                    await using var entryStream = entry.Open();
                    long streamedPart = 0;
                    int read;
                    while ((read = await entryStream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
                    {
                        streamedPart = checked(streamedPart + read);
                        streamedTotal = checked(streamedTotal + read);
                        if (streamedPart > limits.MaxPartBytes || streamedPart > entry.Length ||
                            streamedTotal > limits.MaxTotalUncompressedBytes)
                        {
                            throw new ExcelImportException(ExcelImportErrorCode.UnsafePackage, "The workbook exceeded an uncompressed-size limit while its parts were read.");
                        }
                    }

                    if (streamedPart != entry.Length)
                    {
                        throw new ExcelImportException(ExcelImportErrorCode.UnsafePackage, "A workbook part did not match its declared uncompressed size.");
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }
        }
        catch (OverflowException error)
        {
            throw new ExcelImportException(ExcelImportErrorCode.UnsafePackage, "The workbook package size metadata overflowed safe limits.", error);
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

    internal static WorkbookPart ValidateDocument(SpreadsheetDocument document)
    {
        if (document.DocumentType != SpreadsheetDocumentType.Workbook)
            throw new ExcelImportException(ExcelImportErrorCode.UnsafePackage, "Macro-enabled and template workbook types are not accepted.");
        var workbookPart = document.WorkbookPart
            ?? throw new ExcelImportException(ExcelImportErrorCode.InvalidWorkbook, "The workbook part is missing.");
        if (workbookPart.VbaProjectPart is not null || workbookPart.ExternalWorkbookParts.Any() || HasExternalRelationships(document))
            throw new ExcelImportException(ExcelImportErrorCode.UnsafePackage, "Workbooks containing macros or external relationships are not accepted.");
        return workbookPart;
    }

    private static bool HasExternalRelationships(OpenXmlPartContainer container)
    {
        const int maximumRelationshipEdges = 8_192;
        return HasUnsafeRelationshipGraph(
            container,
            current => current.ExternalRelationships.Any() || current.HyperlinkRelationships.Any(),
            current => current.Parts.Select(pair => (OpenXmlPartContainer)pair.OpenXmlPart),
            maximumRelationshipEdges);
    }

    internal static bool HasUnsafeRelationshipGraph<TNode>(
        TNode root,
        Func<TNode, bool> hasExternalRelationship,
        Func<TNode, IEnumerable<TNode>> getChildren,
        int maximumEdges)
        where TNode : class
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(hasExternalRelationship);
        ArgumentNullException.ThrowIfNull(getChildren);
        if (maximumEdges <= 0) throw new ArgumentOutOfRangeException(nameof(maximumEdges));

        var pending = new Stack<TNode>();
        var visited = new HashSet<TNode>(ReferenceEqualityComparer.Instance) { root };
        pending.Push(root);
        var edgeCount = 0;
        while (pending.TryPop(out var current))
        {
            if (hasExternalRelationship(current)) return true;
            foreach (var child in getChildren(current))
            {
                if (++edgeCount > maximumEdges) return true;
                if (visited.Add(child)) pending.Push(child);
            }
        }
        return false;
    }

    internal static string? ReadFirstWorksheetRelationshipId(WorkbookPart workbookPart)
    {
        using var reader = OpenXmlReader.Create(workbookPart);
        while (reader.Read())
        {
            if (reader.ElementType != typeof(Sheet) || !reader.IsStartElement) continue;
            var sheet = reader.LoadCurrentElement() as Sheet
                ?? throw new ExcelImportException(ExcelImportErrorCode.InvalidWorkbook, "A worksheet declaration could not be read.");
            if (!string.IsNullOrWhiteSpace(sheet.Id?.Value) &&
                workbookPart.GetPartById(sheet.Id!.Value) is WorksheetPart)
            {
                return sheet.Id.Value;
            }
        }

        return null;
    }

    internal IReadOnlyList<string> ReadSharedStrings(
        SharedStringTablePart? part,
        CancellationToken cancellationToken)
    {
        if (part is null) return Array.Empty<string>();
        var values = new List<string>();
        long totalCharacters = 0;
        StringBuilder? currentItem = null;
        using var reader = OpenXmlReader.Create(part);
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.ElementType == typeof(SharedStringItem) && reader.IsStartElement)
            {
                if (values.Count >= limits.MaxSharedStrings)
                    throw new ExcelImportException(ExcelImportErrorCode.SharedStringLimitExceeded, "The shared-string table exceeds the allowed entry count.");
                currentItem = new StringBuilder();
                continue;
            }
            if (currentItem is not null && reader.ElementType == typeof(Text) && reader.IsStartElement)
            {
                var text = reader.GetText();
                if (currentItem.Length > limits.MaxCharactersPerSharedStringItem - text.Length)
                    throw new ExcelImportException(ExcelImportErrorCode.SharedStringItemLimitExceeded, "A shared-string item exceeds the allowed text size.");
                currentItem.Append(text);
                totalCharacters = checked(totalCharacters + text.Length);
                if (totalCharacters > limits.MaxSharedStringCharacters)
                    throw new ExcelImportException(ExcelImportErrorCode.SharedStringLimitExceeded, "The shared-string table exceeds the allowed text size.");
                continue;
            }
            if (currentItem is not null && reader.ElementType == typeof(SharedStringItem) && reader.IsEndElement)
            {
                values.Add(currentItem.ToString());
                currentItem = null;
            }
        }

        if (currentItem is not null)
            throw new ExcelImportException(ExcelImportErrorCode.InvalidWorkbook, "A shared-string entry is incomplete.");

        return values;
    }

    internal static uint ReadRowNumber(OpenXmlReader reader, uint previousRowNumber)
    {
        var value = GetAttribute(reader, "r");
        if (value is null)
        {
            return checked(previousRowNumber + 1);
        }
        return uint.TryParse(value, out var rowNumber)
            ? rowNumber
            : throw new ExcelImportException(ExcelImportErrorCode.InvalidWorkbook, "A worksheet row contains an invalid row index.");
    }

    internal Dictionary<int, string> ReadCells(
        OpenXmlReader reader,
        IReadOnlyList<string> sharedStrings,
        CancellationToken cancellationToken)
    {
        var values = new Dictionary<int, string>();
        var fallbackColumn = 0;
        var cellCount = 0;
        var inCell = false;
        var currentColumn = 0;
        string? currentDataType = null;
        string? currentValue = null;
        StringBuilder? inlineText = null;

        void FinishCell()
        {
            if (!inCell) return;
            var value = ReadCellValue(currentDataType, currentValue, inlineText, sharedStrings);
            if (value.Length > limits.MaxCharactersPerCell)
                throw new ExcelImportException(ExcelImportErrorCode.CellTextLimitExceeded, "A worksheet cell exceeds the allowed text size.");
            values[currentColumn] = value;
            inCell = false;
            currentValue = null;
            inlineText = null;
        }

        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.ElementType == typeof(Row) && reader.IsEndElement)
            {
                FinishCell();
                break;
            }
            if (reader.ElementType == typeof(Cell) && reader.IsStartElement)
            {
                FinishCell();
                cellCount++;
                if (cellCount > limits.MaxCellsPerRow)
                {
                    throw new ExcelImportException(
                        ExcelImportErrorCode.CellLimitExceeded,
                        "A worksheet row contains more cells than allowed.",
                        observedCount: cellCount);
                }

                currentColumn = GetColumnIndex(GetAttribute(reader, "r")) ?? fallbackColumn;
                fallbackColumn = checked(currentColumn + 1);
                currentDataType = GetAttribute(reader, "t");
                inCell = true;
                continue;
            }
            if (!inCell) continue;
            if (reader.ElementType == typeof(Cell) && reader.IsEndElement)
            {
                FinishCell();
            }
            else if (reader.ElementType == typeof(CellValue) && reader.IsStartElement)
            {
                currentValue = reader.GetText();
                if (currentValue.Length > limits.MaxCharactersPerCell)
                    throw new ExcelImportException(ExcelImportErrorCode.CellTextLimitExceeded, "A worksheet cell exceeds the allowed text size.");
            }
            else if (reader.ElementType == typeof(Text) && reader.IsStartElement)
            {
                inlineText ??= new StringBuilder();
                var text = reader.GetText();
                if (inlineText.Length > limits.MaxCharactersPerCell - text.Length)
                    throw new ExcelImportException(ExcelImportErrorCode.CellTextLimitExceeded, "A worksheet cell exceeds the allowed text size.");
                inlineText.Append(text);
            }
        }

        return values;
    }

    private static string ReadCellValue(
        string? dataType,
        string? rawValue,
        StringBuilder? inlineText,
        IReadOnlyList<string> sharedStrings)
    {
        if (dataType == "inlineStr") return inlineText?.ToString() ?? string.Empty;
        var raw = rawValue ?? string.Empty;
        if (dataType != "s") return raw;
        return int.TryParse(raw, out var index) && index >= 0 && index < sharedStrings.Count
            ? sharedStrings[index]
            : throw new ExcelImportException(ExcelImportErrorCode.InvalidWorkbook, "A shared-string cell contains an invalid reference.");
    }

    private static string? GetAttribute(OpenXmlReader reader, string localName)
    {
        foreach (var attribute in reader.Attributes)
        {
            if (string.Equals(attribute.LocalName, localName, StringComparison.Ordinal)) return attribute.Value;
        }
        return null;
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
            if (value > OpenXmlImportLimits.ExcelMaximumColumns)
                throw new ExcelImportException(
                    ExcelImportErrorCode.CellLimitExceeded,
                    "A worksheet cell reference exceeds the Excel column boundary.",
                    observedCount: value);
        }

        return letters == 0 ? null : value - 1;
    }

    internal static void ValidateWorksheetDimensionReference(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return;
        var components = reference.Split(':');
        if (components.Length is < 1 or > 2)
            throw new ExcelImportException(ExcelImportErrorCode.InvalidWorkbook, "A worksheet dimension reference is invalid.");
        foreach (var component in components)
        {
            var columnIndex = GetColumnIndex(component);
            if (columnIndex is null)
                throw new ExcelImportException(ExcelImportErrorCode.InvalidWorkbook, "A worksheet dimension reference is invalid.");
            var digitOffset = 0;
            while (digitOffset < component.Length && char.IsLetter(component[digitOffset])) digitOffset++;
            if (digitOffset == component.Length ||
                !uint.TryParse(component.AsSpan(digitOffset), out var rowNumber) ||
                rowNumber is 0 or > OpenXmlImportLimits.ExcelMaximumRows)
            {
                throw new ExcelImportException(ExcelImportErrorCode.RowLimitExceeded, "A worksheet dimension exceeds the Excel row boundary.");
            }
        }
    }

}
