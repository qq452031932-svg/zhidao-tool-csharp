using System.Globalization;
using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Core.Abstractions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace BaiduBatchTransfer.Infrastructure.Excel;

public static class OpenXmlLocalResultWorkbookManifestCodec
{
    private static readonly string[] OperationHeaders =
        ["AdapterKind", "AccountId", "OperationId", "Generation", "ContentFingerprint"];

    public static void Add(SpreadsheetDocument document, LocalResultWorkbookManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(manifest);
        var workbookPart = document.WorkbookPart ?? throw new InvalidDataException("Missing workbook part.");
        var workbook = workbookPart.Workbook ?? throw new InvalidDataException("Missing workbook.");
        var sheets = workbook.Sheets ?? throw new InvalidDataException("Missing sheets.");
        if (sheets.Elements<Sheet>().Any(sheet =>
                string.Equals(sheet.Name?.Value, LocalResultWorkbookManifest.SheetName, StringComparison.Ordinal)))
            throw new InvalidDataException("Duplicate local result metadata sheet.");

        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        var rows = new List<Row>
        {
            Row(1, "format", LocalResultWorkbookManifest.Format),
            Row(2, "owner_kind", manifest.ExportKey.OwnerKind.ToString()),
            Row(3, "owner_id", manifest.ExportKey.OwnerId.ToString("D")),
            Row(4, "export_kind", manifest.ExportKey.ExportKind.ToString()),
            Row(5, "export_generation", manifest.ExportKey.Generation.ToString(CultureInfo.InvariantCulture)),
            Row(6, "layout_fingerprint", manifest.LayoutFingerprint),
            Row(7, OperationHeaders),
        };
        uint rowIndex = 7;
        foreach (var operation in manifest.Operations)
        {
            rows.Add(Row(++rowIndex,
                operation.AdapterKind,
                operation.AccountId.ToString("D"),
                operation.OperationId.ToString("D"),
                operation.Generation.ToString(CultureInfo.InvariantCulture),
                operation.ContentFingerprint));
        }
        worksheetPart.Worksheet = new Worksheet(new SheetData(rows));
        worksheetPart.Worksheet.Save();

        var sheetId = sheets.Elements<Sheet>().Select(sheet => sheet.SheetId?.Value ?? 0).DefaultIfEmpty().Max() + 1;
        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = sheetId,
            Name = LocalResultWorkbookManifest.SheetName,
            State = SheetStateValues.VeryHidden,
        });
        workbook.Save();
    }

    public static LocalResultWorkbookManifest? TryRead(SpreadsheetDocument document)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(document);
            var workbookPart = document.WorkbookPart ?? throw new InvalidDataException();
            var workbook = workbookPart.Workbook ?? throw new InvalidDataException();
            var sheets = workbook.Sheets?.Elements<Sheet>().ToArray() ?? [];
            var metadata = sheets.Where(sheet =>
                string.Equals(sheet.Name?.Value, LocalResultWorkbookManifest.SheetName, StringComparison.Ordinal)).ToArray();
            if (metadata.Length != 1 || metadata[0].State?.Value != SheetStateValues.VeryHidden ||
                sheets.Count(sheet => sheet.State?.Value == SheetStateValues.VeryHidden) != 1 ||
                metadata[0].Id?.Value is not { Length: > 0 } relationshipId ||
                workbookPart.GetPartById(relationshipId) is not WorksheetPart worksheetPart)
                return null;
            var worksheet = worksheetPart.Worksheet ?? throw new InvalidDataException();
            var rows = worksheet.GetFirstChild<SheetData>()?.Elements<Row>().ToArray() ?? [];
            if (rows.Length < 7) return null;
            var fixedValues = new[]
            {
                Pair(rows[0], 1, "format"),
                Pair(rows[1], 2, "owner_kind"),
                Pair(rows[2], 3, "owner_id"),
                Pair(rows[3], 4, "export_kind"),
                Pair(rows[4], 5, "export_generation"),
                Pair(rows[5], 6, "layout_fingerprint"),
            };
            if (!string.Equals(fixedValues[0], LocalResultWorkbookManifest.Format, StringComparison.Ordinal) ||
                !Enum.TryParse<LocalResultOwnerKind>(fixedValues[1], false, out var ownerKind) ||
                !Guid.TryParseExact(fixedValues[2], "D", out var ownerId) ||
                !Enum.TryParse<LocalResultExportKind>(fixedValues[3], false, out var exportKind) ||
                !int.TryParse(fixedValues[4], NumberStyles.None, CultureInfo.InvariantCulture, out var exportGeneration) ||
                exportGeneration < 1 || !LocalResultWorkbookManifest.IsFingerprint(fixedValues[5]))
                return null;
            var headers = Values(rows[6], 7, OperationHeaders.Length);
            if (!headers.SequenceEqual(OperationHeaders, StringComparer.Ordinal)) return null;

            var operations = new List<LocalResultOperationEvidence>(rows.Length - 7);
            for (var index = 7; index < rows.Length; index++)
            {
                var values = Values(rows[index], checked((uint)index + 1), OperationHeaders.Length);
                if (!Guid.TryParseExact(values[1], "D", out var accountId) ||
                    !Guid.TryParseExact(values[2], "D", out var operationId) ||
                    !int.TryParse(values[3], NumberStyles.None, CultureInfo.InvariantCulture, out var generation))
                    return null;
                operations.Add(new(values[0], accountId, operationId, generation, values[4]));
            }
            var manifest = new LocalResultWorkbookManifest(
                new(ownerKind, ownerId, exportKind, exportGeneration), fixedValues[5], operations);
            return operations.SequenceEqual(manifest.Operations) ? manifest : null;
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or FormatException or OverflowException)
        {
            return null;
        }
    }

    private static string Pair(Row row, uint rowIndex, string key)
    {
        var values = Values(row, rowIndex, 2);
        if (!string.Equals(values[0], key, StringComparison.Ordinal)) throw new InvalidDataException();
        return values[1];
    }

    private static string[] Values(Row row, uint rowIndex, int count)
    {
        if (row.RowIndex?.Value != rowIndex) throw new InvalidDataException();
        var cells = row.Elements<Cell>().ToArray();
        if (cells.Length != count) throw new InvalidDataException();
        var values = new string[count];
        for (var index = 0; index < count; index++)
        {
            if (cells[index].CellFormula is not null || cells[index].DataType?.Value != CellValues.InlineString)
                throw new InvalidDataException();
            values[index] = cells[index].InlineString?.Text?.Text ?? string.Empty;
        }
        return values;
    }

    private static Row Row(uint index, params string[] values)
    {
        var row = new Row { RowIndex = index };
        foreach (var value in values)
            row.Append(new Cell(new InlineString(new Text(value))) { DataType = CellValues.InlineString });
        return row;
    }
}
