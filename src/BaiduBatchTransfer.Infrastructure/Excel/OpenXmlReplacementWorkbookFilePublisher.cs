using BaiduBatchTransfer.Application.Jobs;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using System.Security;

namespace BaiduBatchTransfer.Infrastructure.Excel;

public enum ReplacementWorkbookFilePublishErrorCode
{
    InvalidDestination,
    InvalidFileName,
    InvalidWorkbook,
    DestinationExists,
    PublishFailed,
}

public sealed class ReplacementWorkbookFilePublishException : IOException
{
    public ReplacementWorkbookFilePublishException(
        ReplacementWorkbookFilePublishErrorCode code,
        string safeMessage) : base(safeMessage)
    {
        Code = code;
    }

    public ReplacementWorkbookFilePublishErrorCode Code { get; }
}

public sealed class OpenXmlReplacementWorkbookFilePublisher(
    IReplacementWorkbookWriter writer) : IReplacementWorkbookFilePublisher
{
    private readonly IReplacementWorkbookWriter writer =
        writer ?? throw new ArgumentNullException(nameof(writer));

    public async Task<ReplacementWorkbookFilePublishReceipt> PublishAsync(
        string destinationDirectory,
        string finalFileName,
        ReplacementWorkbookLayout layout,
        IAsyncEnumerable<ReplacementWorkbookRow> rows,
        CancellationToken cancellationToken = default) =>
        await PublishCoreAsync(
            destinationDirectory, finalFileName, layout, rows, null, cancellationToken).ConfigureAwait(false);

    public async Task<ReplacementWorkbookFilePublishReceipt> PublishWithManifestAsync(
        string destinationDirectory,
        string finalFileName,
        ReplacementWorkbookLayout layout,
        IAsyncEnumerable<ReplacementWorkbookRow> rows,
        LocalResultWorkbookManifest manifest,
        CancellationToken cancellationToken = default) =>
        await PublishCoreAsync(
            destinationDirectory, finalFileName, layout, rows,
            manifest ?? throw new ArgumentNullException(nameof(manifest)), cancellationToken).ConfigureAwait(false);

    private async Task<ReplacementWorkbookFilePublishReceipt> PublishCoreAsync(
        string destinationDirectory,
        string finalFileName,
        ReplacementWorkbookLayout layout,
        IAsyncEnumerable<ReplacementWorkbookRow> rows,
        LocalResultWorkbookManifest? manifest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(rows);
        cancellationToken.ThrowIfCancellationRequested();
        var paths = ResolvePaths(destinationDirectory, finalFileName);
        var published = false;

        try
        {
            await using (var output = new FileStream(
                paths.TemporaryPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await writer.WriteAsync(output, layout, rows, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }

            if (manifest is not null)
            {
                using var document = SpreadsheetDocument.Open(paths.TemporaryPath, true);
                OpenXmlLocalResultWorkbookManifestCodec.Add(document, manifest);
            }
            var validation = ValidateWorkbook(paths.TemporaryPath, layout, manifest, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Move(paths.TemporaryPath, paths.FinalPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(paths.FinalPath))
            {
                throw new ReplacementWorkbookFilePublishException(
                    ReplacementWorkbookFilePublishErrorCode.DestinationExists,
                    "The replacement workbook destination already exists.");
            }

            published = true;
            return new(paths.FinalPath, validation.ExportedRows, validation.SheetCount);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ReplacementWorkbookFilePublishException)
        {
            throw;
        }
        catch (ReplacementWorkbookWriteException)
        {
            throw;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            throw new ReplacementWorkbookFilePublishException(
                ReplacementWorkbookFilePublishErrorCode.PublishFailed,
                "The replacement workbook could not be published.");
        }
        finally
        {
            if (!published && File.Exists(paths.TemporaryPath))
            {
                try
                {
                    File.Delete(paths.TemporaryPath);
                }
                catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException)
                {
                    // Preserve the original publish failure or cancellation. The opaque sibling name avoids user data leakage.
                }
            }
        }
    }

    private static PublishPaths ResolvePaths(string destinationDirectory, string finalFileName)
    {
        if (string.IsNullOrWhiteSpace(destinationDirectory))
            throw new ReplacementWorkbookFilePublishException(
                ReplacementWorkbookFilePublishErrorCode.InvalidDestination,
                "A replacement workbook destination directory is required.");
        if (string.IsNullOrWhiteSpace(finalFileName) ||
            !string.Equals(finalFileName, finalFileName.Trim(), StringComparison.Ordinal) ||
            finalFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            Path.IsPathFullyQualified(finalFileName) ||
            !string.Equals(Path.GetFileName(finalFileName), finalFileName, StringComparison.Ordinal) ||
            !string.Equals(Path.GetExtension(finalFileName), ".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new ReplacementWorkbookFilePublishException(
                ReplacementWorkbookFilePublishErrorCode.InvalidFileName,
                "The replacement workbook file name is invalid.");

        string directoryPath;
        try
        {
            directoryPath = Path.GetFullPath(destinationDirectory);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ReplacementWorkbookFilePublishException(
                ReplacementWorkbookFilePublishErrorCode.InvalidDestination,
                "The replacement workbook destination directory is invalid.");
        }
        if (!Directory.Exists(directoryPath))
            throw new ReplacementWorkbookFilePublishException(
                ReplacementWorkbookFilePublishErrorCode.InvalidDestination,
                "The replacement workbook destination directory does not exist.");

        var finalPath = Path.Combine(directoryPath, finalFileName);
        var temporaryPath = Path.Combine(
            directoryPath,
            $".{Guid.NewGuid():N}.tmp");
        return new(finalPath, temporaryPath);
    }

    private static WorkbookValidation ValidateWorkbook(
        string path,
        ReplacementWorkbookLayout layout,
        LocalResultWorkbookManifest? expectedManifest,
        CancellationToken cancellationToken)
    {
        try
        {
            using var document = SpreadsheetDocument.Open(
                path,
                false,
                new OpenSettings { AutoSave = false });
            cancellationToken.ThrowIfCancellationRequested();
            RejectExternalRelationships(document);
            var workbookPart = document.WorkbookPart
                ?? throw new InvalidDataException("Missing workbook part.");
            var sheets = workbookPart.Workbook?.Sheets?.Elements<Sheet>().ToArray()
                ?? throw new InvalidDataException("Missing sheets.");
            if (sheets.Length == 0 || workbookPart.WorksheetParts.Count() != sheets.Length)
                throw new InvalidDataException("Invalid worksheet layout.");
            var actualManifest = OpenXmlLocalResultWorkbookManifestCodec.TryRead(document);
            if (expectedManifest is null && sheets.Any(sheet =>
                    string.Equals(sheet.Name?.Value, LocalResultWorkbookManifest.SheetName, StringComparison.Ordinal) ||
                    sheet.State?.Value == SheetStateValues.VeryHidden))
                throw new InvalidDataException("Unexpected local result manifest.");
            if (!ManifestEquals(expectedManifest, actualManifest))
                throw new InvalidDataException("Invalid local result manifest.");
            var resultSheets = sheets.Where(sheet =>
                !string.Equals(sheet.Name?.Value, LocalResultWorkbookManifest.SheetName, StringComparison.Ordinal)).ToArray();
            if (resultSheets.Length == 0) throw new InvalidDataException("Missing result sheets.");

            var expectedHeaders = layout.Headers.Select(ExcelTextSanitizer.Sanitize).ToArray();
            var relationshipIds = new HashSet<string>(StringComparer.Ordinal);
            long exportedRows = 0;
            for (var sheetIndex = 0; sheetIndex < resultSheets.Length; sheetIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sheet = resultSheets[sheetIndex];
                var relationshipId = sheet.Id?.Value;
                if (string.IsNullOrEmpty(relationshipId) || !relationshipIds.Add(relationshipId) ||
                    workbookPart.GetPartById(relationshipId) is not WorksheetPart worksheetPart)
                    throw new InvalidDataException("Invalid worksheet relationship.");
                var expectedName = sheetIndex == 0 ? "替换结果" : $"替换结果 {sheetIndex + 1}";
                if (!string.Equals(sheet.Name?.Value, expectedName, StringComparison.Ordinal))
                    throw new InvalidDataException("Invalid worksheet name.");

                var rowCount = ValidateWorksheet(worksheetPart, expectedHeaders, cancellationToken);
                exportedRows = checked(exportedRows + rowCount - 1);
            }

            return new(exportedRows, resultSheets.Length);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (error is OpenXmlPackageException or InvalidDataException or IOException or FormatException)
        {
            throw new ReplacementWorkbookFilePublishException(
                ReplacementWorkbookFilePublishErrorCode.InvalidWorkbook,
                "The generated replacement workbook failed validation.");
        }
    }

    private static bool ManifestEquals(
        LocalResultWorkbookManifest? expected,
        LocalResultWorkbookManifest? actual) =>
        expected is null
            ? actual is null
            : actual is not null && expected.ExportKey == actual.ExportKey &&
              string.Equals(expected.LayoutFingerprint, actual.LayoutFingerprint, StringComparison.Ordinal) &&
              expected.Operations.SequenceEqual(actual.Operations);

    private static long ValidateWorksheet(
        WorksheetPart worksheetPart,
        IReadOnlyList<string> expectedHeaders,
        CancellationToken cancellationToken)
    {
        long rowCount = 0;
        var columnCount = 0;
        var inRow = false;
        using var reader = OpenXmlReader.Create(worksheetPart);
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.ElementType == typeof(Hyperlinks))
                throw new InvalidDataException("Hyperlinks are not allowed.");
            if (reader.ElementType == typeof(Row))
            {
                if (reader.IsStartElement)
                {
                    if (inRow) throw new InvalidDataException("Invalid nested worksheet row.");
                    inRow = true;
                    columnCount = 0;
                    rowCount = checked(rowCount + 1);
                    if (!uint.TryParse(GetAttribute(reader, "r"), out var rowIndex) || rowIndex != checked((uint)rowCount))
                        throw new InvalidDataException("Invalid worksheet row order.");
                }
                else if (reader.IsEndElement)
                {
                    if (!inRow || columnCount != expectedHeaders.Count)
                        throw new InvalidDataException("Invalid worksheet column count.");
                    inRow = false;
                }
                continue;
            }
            if (reader.ElementType == typeof(Cell) && reader.IsStartElement)
            {
                if (!inRow || columnCount >= expectedHeaders.Count)
                    throw new InvalidDataException("Invalid worksheet column count.");
                ValidateCell(
                    reader,
                    rowCount == 1 ? expectedHeaders[columnCount] : null,
                    cancellationToken);
                columnCount++;
            }
        }

        if (rowCount == 0 || inRow) throw new InvalidDataException("Missing or incomplete worksheet header.");
        return rowCount;
    }

    private static void ValidateCell(
        OpenXmlReader reader,
        string? expectedHeader,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(GetAttribute(reader, "t"), "inlineStr", StringComparison.Ordinal))
            throw new InvalidDataException("Invalid worksheet cell encoding.");
        var inlineStringCount = 0;
        var inInlineString = false;
        var text = new System.Text.StringBuilder();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.ElementType == typeof(Cell) && reader.IsEndElement) break;
            if (reader.ElementType == typeof(InlineString))
            {
                if (reader.IsStartElement)
                {
                    if (inInlineString || ++inlineStringCount != 1)
                        throw new InvalidDataException("Invalid worksheet cell encoding.");
                    inInlineString = true;
                }
                else if (reader.IsEndElement)
                {
                    if (!inInlineString) throw new InvalidDataException("Invalid worksheet cell encoding.");
                    inInlineString = false;
                }
                continue;
            }
            if (reader.ElementType == typeof(Text) && reader.IsStartElement && inInlineString)
            {
                text.Append(reader.GetText());
                continue;
            }
            if (reader.IsStartElement)
                throw new InvalidDataException("Invalid worksheet cell encoding.");
        }
        if (inlineStringCount != 1 || inInlineString)
            throw new InvalidDataException("Invalid worksheet cell encoding.");
        if (expectedHeader is not null && !string.Equals(text.ToString(), expectedHeader, StringComparison.Ordinal))
            throw new InvalidDataException("Invalid worksheet header.");
    }

    private static string? GetAttribute(OpenXmlReader reader, string localName)
    {
        foreach (var attribute in reader.Attributes)
        {
            if (string.Equals(attribute.LocalName, localName, StringComparison.Ordinal)) return attribute.Value;
        }
        return null;
    }

    private static void RejectExternalRelationships(OpenXmlPartContainer container)
    {
        var visited = new HashSet<Uri>();
        RejectExternalRelationships(container, visited);
    }

    private static void RejectExternalRelationships(OpenXmlPartContainer container, ISet<Uri> visited)
    {
        if (container.ExternalRelationships.Any() || container.HyperlinkRelationships.Any())
            throw new InvalidDataException("External relationships are not allowed.");
        foreach (var relationship in container.Parts)
        {
            if (visited.Add(relationship.OpenXmlPart.Uri))
                RejectExternalRelationships(relationship.OpenXmlPart, visited);
        }
    }

    private sealed record PublishPaths(string FinalPath, string TemporaryPath);
    private sealed record WorkbookValidation(long ExportedRows, int SheetCount);
}
