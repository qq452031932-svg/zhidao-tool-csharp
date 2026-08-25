using System.IO;
using BaiduBatchTransfer.App.Features.BrowserAutomation;
using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Infrastructure.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace BaiduBatchTransfer.App.Features.Results;

public sealed class LocalResultExportArtifactService(
    ReplacementWorkbookExportService replacementExports,
    ExportStandaloneShareResultsService standaloneExports,
    IReplacementWorkbookFilePublisher publisher,
    IDownloadedWorkbookValidator validator,
    ILocalResultExportOwnerRepository membershipRepository) : ILocalResultExportArtifactService
{
    private const int MaximumSuffix = 10_000;
    private readonly ReplacementWorkbookExportService replacementExports =
        replacementExports ?? throw new ArgumentNullException(nameof(replacementExports));
    private readonly ExportStandaloneShareResultsService standaloneExports =
        standaloneExports ?? throw new ArgumentNullException(nameof(standaloneExports));
    private readonly IReplacementWorkbookFilePublisher publisher =
        publisher ?? throw new ArgumentNullException(nameof(publisher));
    private readonly IDownloadedWorkbookValidator validator =
        validator ?? throw new ArgumentNullException(nameof(validator));
    private readonly ILocalResultExportOwnerRepository membershipRepository =
        membershipRepository ?? throw new ArgumentNullException(nameof(membershipRepository));

    public async Task<string> ChooseTargetFileNameAsync(
        LocalResultExportOwnerSnapshot owner,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        cancellationToken.ThrowIfCancellationRequested();
        var suggested = owner.Key.ExportKind switch
        {
            LocalResultExportKind.ReplacementWorkbook =>
                await replacementExports.GetSuggestedFileNameAsync(owner.Key.OwnerId, cancellationToken)
                    .ConfigureAwait(false),
            LocalResultExportKind.StandaloneShareWorkbook => standaloneExports.GetSuggestedFileName(),
            _ => throw new InvalidOperationException("This local export kind is not a summary workbook."),
        };
        LocalResultExportValidation.SafeFileName(suggested, nameof(suggested));
        return LocalResultFileNameAllocator.Choose(
            owner.DestinationDirectory, suggested, MaximumSuffix, cancellationToken);
    }

    public async Task<LocalResultArtifactState> InspectAsync(
        LocalResultExportOwnerSnapshot owner,
        string targetFileName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var path = ResolveExactPath(owner.DestinationDirectory, targetFileName);
        if (!File.Exists(path)) return LocalResultArtifactState.Missing;
        if (!await validator.IsValidAsync(path, cancellationToken).ConfigureAwait(false))
            return LocalResultArtifactState.Invalid;
        var layout = owner.Key.ExportKind switch
        {
            LocalResultExportKind.ReplacementWorkbook =>
                await replacementExports.GetExpectedLayoutAsync(owner.Key.OwnerId, cancellationToken)
                    .ConfigureAwait(false),
            LocalResultExportKind.StandaloneShareWorkbook => standaloneExports.GetExpectedLayout(),
            _ => throw new InvalidOperationException("This local export kind is not a summary workbook."),
        };
        return HasExpectedShape(path, layout, owner.ExpectedRowCount, cancellationToken)
            ? LocalResultArtifactState.Valid
            : LocalResultArtifactState.Invalid;
    }

    public async Task<LocalResultArtifactPublishReceipt> PublishAsync(
        LocalResultExportOwnerSnapshot owner,
        string targetFileName,
        CancellationToken cancellationToken = default,
        IReadOnlyList<LocalResultOperationEvidence>? provenOperations = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        LocalResultExportValidation.SafeFileName(targetFileName, nameof(targetFileName));
        var layout = owner.Key.ExportKind switch
        {
            LocalResultExportKind.ReplacementWorkbook =>
                await replacementExports.GetExpectedLayoutAsync(owner.Key.OwnerId, cancellationToken)
                    .ConfigureAwait(false),
            LocalResultExportKind.StandaloneShareWorkbook => standaloneExports.GetExpectedLayout(),
            _ => throw new InvalidOperationException("This local export kind is not a summary workbook."),
        };
        var manifest = new LocalResultWorkbookManifest(
            owner.Key,
            LayoutFingerprint(layout),
            provenOperations ?? []);
        var receipt = owner.Key.ExportKind switch
        {
            LocalResultExportKind.ReplacementWorkbook =>
                await replacementExports.ExportToDirectoryAsync(
                    new ReplacementWorkbookExportRequest(owner.Key.OwnerId),
                    owner.DestinationDirectory, targetFileName, publisher,
                    cancellationToken: cancellationToken,
                    manifest: manifest).ConfigureAwait(false),
            LocalResultExportKind.StandaloneShareWorkbook =>
                await standaloneExports.ExportToDirectoryAsync(
                    new StandaloneShareResultExportRequest(owner.Key.OwnerId), owner.DestinationDirectory,
                    targetFileName, publisher, cancellationToken, manifest)
                    .ConfigureAwait(false),
            _ => throw new InvalidOperationException("This local export kind is not a summary workbook."),
        };
        var finalName = Path.GetFileName(receipt.FinalPath)
            ?? throw new InvalidDataException("The published result file name is unavailable.");
        if (!string.Equals(finalName, targetFileName, StringComparison.Ordinal))
            throw new InvalidDataException("The published result does not match its durable target.");
        return new(finalName, receipt.ExportedRows);
    }

    public async Task<LocalResultExportCompletedEvidence?> GetVerifiedEvidenceAsync(
        LocalResultExportOwnerSnapshot owner,
        string targetFileName,
        IReadOnlyList<LocalResultOperationIdentity> candidates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(candidates);
        var path = ResolveExactPath(owner.DestinationDirectory,
            LocalResultExportValidation.SafeFileName(targetFileName, nameof(targetFileName)));
        cancellationToken.ThrowIfCancellationRequested();
        if (candidates.Count == 0 || !File.Exists(path)) return null;
        var layout = owner.Key.ExportKind switch
        {
            LocalResultExportKind.ReplacementWorkbook =>
                await replacementExports.GetExpectedLayoutAsync(owner.Key.OwnerId, cancellationToken)
                    .ConfigureAwait(false),
            LocalResultExportKind.StandaloneShareWorkbook => standaloneExports.GetExpectedLayout(),
            _ => throw new InvalidOperationException("This local export kind is not a summary workbook."),
        };
        var layoutFingerprint = LayoutFingerprint(layout);
        var exactCandidates = candidates.Distinct().ToArray();
        if (exactCandidates.Length != candidates.Count || exactCandidates.Any(candidate =>
                candidate.AdapterKind is not ("BaiduTransfer" or "BaiduShare") ||
                candidate.AccountId == Guid.Empty || candidate.OperationId == Guid.Empty ||
                candidate.Generation < 1)) return null;
        IReadOnlyList<LocalResultOperationEvidence> currentProof;
        try
        {
            currentProof = await membershipRepository.GetProvenOperationEvidenceAsync(
                owner.Key, exactCandidates, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
        LocalResultWorkbookManifest canonicalProof;
        try
        {
            canonicalProof = new(owner.Key, layoutFingerprint, currentProof);
        }
        catch (ArgumentException)
        {
            return null;
        }
        var proofIdentities = canonicalProof.Operations.Select(operation => new LocalResultOperationIdentity(
            operation.AdapterKind, operation.AccountId, operation.OperationId, operation.Generation)).ToHashSet();
        if (proofIdentities.Count != exactCandidates.Length ||
            !proofIdentities.SetEquals(exactCandidates)) return null;
        try
        {
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 64 * 1024, useAsync: true);
            var artifactFingerprint = Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
            stream.Position = 0;
            using var document = SpreadsheetDocument.Open(stream, false, new OpenSettings { AutoSave = false });
            if (HasExternalRelationships(document, new HashSet<Uri>()) ||
                !HasExpectedShape(document, layout, owner.ExpectedRowCount, cancellationToken)) return null;
            var manifest = OpenXmlLocalResultWorkbookManifestCodec.TryRead(document);
            if (manifest is null || manifest.ExportKey != owner.Key ||
                !string.Equals(manifest.LayoutFingerprint, layoutFingerprint, StringComparison.Ordinal)) return null;
            if (!manifest.Operations.SequenceEqual(canonicalProof.Operations)) return null;
            return new(owner.Key, manifest.Operations, layoutFingerprint, artifactFingerprint);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                                      OpenXmlPackageException or InvalidDataException or FormatException or
                                      XmlException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string LayoutFingerprint(ReplacementWorkbookLayout layout) =>
        Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(string.Join("\u001F", layout.Headers)))).ToLowerInvariant();

    private static bool HasExternalRelationships(
        OpenXmlPartContainer part,
        HashSet<Uri> visitedParts)
    {
        if (part.ExternalRelationships.Any() || part.HyperlinkRelationships.Any()) return true;
        return part.Parts.Any(child =>
            visitedParts.Add(child.OpenXmlPart.Uri) &&
            HasExternalRelationships(child.OpenXmlPart, visitedParts));
    }

    private static string ResolveExactPath(string directory, string fileName)
    {
        var normalizedDirectory = NormalizeDirectory(directory);
        var safeName = LocalResultExportValidation.SafeFileName(fileName, nameof(fileName));
        var path = Path.GetFullPath(Path.Combine(normalizedDirectory, safeName));
        var prefix = normalizedDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedDirectory
            : normalizedDirectory + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The local result path escapes its selected directory.");
        return path;
    }

    private static bool HasExpectedShape(
        string path,
        ReplacementWorkbookLayout layout,
        long expectedRowCount,
        CancellationToken cancellationToken)
    {
        try
        {
            using var document = SpreadsheetDocument.Open(path, false, new OpenSettings { AutoSave = false });
            return HasExpectedShape(document, layout, expectedRowCount, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (error is OpenXmlPackageException or InvalidDataException or IOException or
                                      FormatException or XmlException or KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool HasExpectedShape(
        SpreadsheetDocument document,
        ReplacementWorkbookLayout layout,
        long expectedRowCount,
        CancellationToken cancellationToken)
    {
        try
        {
            var workbookPart = document.WorkbookPart;
            var sheets = workbookPart?.Workbook?.Sheets?.Elements<Sheet>().ToArray();
            if (workbookPart is null || sheets is null || sheets.Length == 0) return false;
            var publicSheets = sheets.Where(sheet => !string.Equals(
                sheet.Name?.Value, LocalResultWorkbookManifest.SheetName, StringComparison.Ordinal)).ToArray();
            if (publicSheets.Length == 0) return false;
            for (var sheetIndex = 0; sheetIndex < publicSheets.Length; sheetIndex++)
            {
                var expectedName = sheetIndex == 0 ? "替换结果" : $"替换结果 {sheetIndex + 1}";
                if (!string.Equals(publicSheets[sheetIndex].Name?.Value, expectedName, StringComparison.Ordinal))
                    return false;
            }
            var expectedHeaders = layout.Headers.Select(ExcelTextSanitizer.Sanitize).ToArray();
            long dataRows = 0;
            foreach (var sheet in publicSheets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (sheet.Id?.Value is not { Length: > 0 } relationshipId ||
                    workbookPart.GetPartById(relationshipId) is not WorksheetPart worksheetPart)
                    return false;
                using var reader = OpenXmlReader.Create(worksheetPart);
                long sheetRows = 0;
                var columns = 0;
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (reader.ElementType == typeof(Row) && reader.IsStartElement)
                    {
                        sheetRows = checked(sheetRows + 1);
                        columns = 0;
                        continue;
                    }
                    if (reader.ElementType == typeof(Cell) && reader.IsStartElement)
                    {
                        if (columns >= expectedHeaders.Length) return false;
                        var text = ReadInlineCell(reader);
                        if (sheetRows == 1 && !string.Equals(
                                text, expectedHeaders[columns], StringComparison.Ordinal))
                            return false;
                        columns++;
                        continue;
                    }
                    if (reader.ElementType == typeof(Row) && reader.IsEndElement &&
                        columns != expectedHeaders.Length)
                        return false;
                }
                if (sheetRows == 0) return false;
                dataRows = checked(dataRows + sheetRows - 1);
            }
            return dataRows == expectedRowCount;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (error is OpenXmlPackageException or InvalidDataException or IOException or
                                      FormatException or XmlException or KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }

    private static string ReadInlineCell(OpenXmlReader reader)
    {
        if (!string.Equals(
                reader.Attributes.FirstOrDefault(attribute => attribute.LocalName == "t").Value,
                "inlineStr",
                StringComparison.Ordinal))
            throw new InvalidDataException("The result workbook cell encoding is invalid.");
        var text = new System.Text.StringBuilder();
        while (reader.Read())
        {
            if (reader.ElementType == typeof(Cell) && reader.IsEndElement) return text.ToString();
            if (reader.ElementType == typeof(Text) && reader.IsStartElement) text.Append(reader.GetText());
        }
        throw new InvalidDataException("The result workbook cell is incomplete.");
    }

    private static string NormalizeDirectory(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return Path.GetFullPath(value.Trim());
    }
}

public static class LocalResultFileNameAllocator
{
    public static string Choose(
        string destinationDirectory,
        string suggestedFileName,
        int maximumCandidates = 10_000,
        CancellationToken cancellationToken = default)
    {
        if (maximumCandidates < 1) throw new ArgumentOutOfRangeException(nameof(maximumCandidates));
        var directory = Path.GetFullPath(
            string.IsNullOrWhiteSpace(destinationDirectory)
                ? throw new ArgumentException("A destination directory is required.", nameof(destinationDirectory))
                : destinationDirectory.Trim());
        var suggested = LocalResultExportValidation.SafeFileName(
            suggestedFileName, nameof(suggestedFileName));
        var stem = Path.GetFileNameWithoutExtension(suggested);
        var extension = Path.GetExtension(suggested);
        for (var suffix = 0; suffix < maximumCandidates; suffix++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = suffix == 0 ? suggested : $"{stem} ({suffix}){extension}";
            if (!File.Exists(Path.Combine(directory, candidate))) return candidate;
        }
        throw new IOException("No available local result file name could be reserved.");
    }
}
