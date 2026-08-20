using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Services;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace BaiduBatchTransfer.Infrastructure.Excel;

public sealed class OpenXmlWorkbookInspector
{
    public const int MaximumSampleRows = 20;

    private readonly OpenXmlImportReader workbookReader;

    public OpenXmlWorkbookInspector(OpenXmlImportLimits? limits = null)
    {
        workbookReader = new OpenXmlImportReader(limits);
    }

    public async Task<TabularWorkbookInspection> InspectAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
            throw new ExcelImportException(ExcelImportErrorCode.InvalidWorkbook, "The workbook stream must be readable and seekable.");

        await workbookReader.ValidatePackageAsync(stream, cancellationToken).ConfigureAwait(false);
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
            var workbookPart = OpenXmlImportReader.ValidateDocument(document);
            var relationshipId = OpenXmlImportReader.ReadFirstWorksheetRelationshipId(workbookPart);
            if (relationshipId is null || workbookPart.GetPartById(relationshipId) is not WorksheetPart worksheetPart)
                throw new ExcelImportException(ExcelImportErrorCode.MissingWorksheet, "The workbook does not contain a readable worksheet.");

            var sharedStrings = workbookReader.ReadSharedStrings(workbookPart.SharedStringTablePart, cancellationToken);
            var observed = new List<(int RowNumber, Dictionary<int, string> Values)>(MaximumSampleRows + 1);
            uint lastRowNumber = 0;
            var maximumObservedColumnIndex = -1;
            using var reader = OpenXmlReader.Create(worksheetPart);
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.ElementType == typeof(SheetDimension) && reader.IsStartElement)
                {
                    var dimension = reader.LoadCurrentElement() as SheetDimension;
                    OpenXmlImportReader.ValidateWorksheetDimensionReference(dimension?.Reference?.Value);
                    continue;
                }
                if (reader.ElementType != typeof(Row) || !reader.IsStartElement) continue;
                var rowNumber = OpenXmlImportReader.ReadRowNumber(reader, lastRowNumber);
                if (rowNumber is 0 or > OpenXmlImportLimits.ExcelMaximumRows)
                    throw new ExcelImportException(ExcelImportErrorCode.RowLimitExceeded, "A worksheet row index exceeds the Excel format boundary.");
                if (rowNumber <= lastRowNumber)
                    throw new ExcelImportException(ExcelImportErrorCode.InvalidRowOrder, "Worksheet row indexes must be strictly increasing.");
                lastRowNumber = rowNumber;
                var values = workbookReader.ReadCells(reader, sharedStrings, cancellationToken);
                if (values.Count != 0)
                    maximumObservedColumnIndex = Math.Max(maximumObservedColumnIndex, values.Keys.Max());
                if (values.Count == 0 || values.Values.All(string.IsNullOrWhiteSpace)) continue;
                if (observed.Count < MaximumSampleRows + 1)
                    observed.Add(((int)rowNumber, values));
            }

            if (observed.Count == 0) return new TabularWorkbookInspection(null, [], []);

            var width = checked(maximumObservedColumnIndex + 1);
            var firstValues = OpenXmlImportReader.ToOrderedValues(observed[0].Values, width);
            var headerRowNumber = InferHeaderRowNumber(observed, firstValues);
            var headerOffset = headerRowNumber is null ? 0 : 1;
            var columns = Enumerable.Range(0, width).Select(index => new TabularInspectionColumn(
                index,
                headerRowNumber is null || string.IsNullOrEmpty(firstValues[index]) ? null : firstValues[index]));
            var samples = observed.Skip(headerOffset).Take(MaximumSampleRows).Select(row =>
                new TabularInspectionRow(row.RowNumber, OpenXmlImportReader.ToOrderedValues(row.Values, width)));
            var observedRows = observed.Take(headerRowNumber is null ? MaximumSampleRows : MaximumSampleRows + 1).Select(row =>
                new TabularInspectionRow(row.RowNumber, OpenXmlImportReader.ToOrderedValues(row.Values, width)));
            return new TabularWorkbookInspection(headerRowNumber, columns, samples, observedRows);
        }
    }

    private static int? InferHeaderRowNumber(
        IReadOnlyList<(int RowNumber, Dictionary<int, string> Values)> observed,
        IReadOnlyList<string> firstValues)
    {
        if (ExcelColumnSuggestionEngine.ContainsKnownHeader(firstValues)) return observed[0].RowNumber;

        var extractor = new BaiduShareUrlExtractor();
        if (firstValues.Any(value => extractor.ExtractSupportedUrl(value) is not null)) return null;
        if (observed.Skip(1).Any(row => row.Values.Values.Any(value => extractor.ExtractSupportedUrl(value) is not null)))
            return observed[0].RowNumber;

        // This remains an inspect-time suggestion and never authorizes import.
        return observed[0].RowNumber;
    }
}
