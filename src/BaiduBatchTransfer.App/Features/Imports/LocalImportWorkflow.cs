using System.IO;
using System.Security.Cryptography;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Application.Imports;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Infrastructure.Excel;

namespace BaiduBatchTransfer.App.Features.Imports;

public sealed class DeferredShareLinkResolver : IShareLinkResolver
{
    public Task<BaiduAdapterResult<ResolvedShareLink>> ResolveAsync(
        string rawUrl,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawUrl);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(BaiduAdapterResult<ResolvedShareLink>.Failure(
            BaiduErrorKind.TransientService,
            "Short-link resolution is deferred until the live resolver passes its gate."));
    }
}

public sealed class LocalImportWorkflow(
    ImportStagingService stagingService,
    TextImportParser textParser,
    OpenXmlImportReader excelReader,
    OpenXmlWorkbookInspector excelInspector,
    IExcelColumnMappingPresenter mappingPresenter,
    IDialogService dialogService,
    DraftRowEditingService editingService,
    IUnitOfWorkFactory unitOfWorkFactory) : IImportWorkflow
{
    // Replay content can include pasted links or absolute paths. Keep only a small LRU set of unfinished drafts.
    private const int MaxRetainedReplaySources = 8;
    private readonly object replayGate = new();
    private readonly Dictionary<Guid, ReplayEntry> replaySources = [];
    private long replaySequence;

    public Task<ImportWorkflowResult> ImportTextAsync(
        Guid sessionId,
        string text,
        CancellationToken cancellationToken) =>
        ImportTextAsync(sessionId, text, committedProgress: null, cancellationToken);

    public async Task<ImportWorkflowResult> ImportTextAsync(
        Guid sessionId,
        string text,
        IProgress<ImportCommittedProgress>? committedProgress,
        CancellationToken cancellationToken)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("导入会话不能为空。", nameof(sessionId));
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var source = ReplaySource.ForText(text);
        RememberReplaySource(sessionId, source);
        var replayProgress = new ReplayProgress(committedProgress);
        var stagingCompleted = false;
        try
        {
            await stagingService.StageAsync(
                new ImportRequest(sessionId, "Text", "[PASTED]"),
                ReadTextAsync(source.Text!, cancellationToken),
                replayProgress,
                cancellationToken).ConfigureAwait(false);
            stagingCompleted = true;
            RemoveReplaySource(sessionId);
            return await LoadResultAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is not ImportWorkflowException)
        {
            throw new ImportWorkflowException(ImportUiError.InvalidData, error);
        }
        finally
        {
            if (!stagingCompleted && !replayProgress.SessionCreated)
                RemoveReplaySource(sessionId);
        }
    }

    public Task<ImportWorkflowResult> ImportExcelAsync(
        Guid sessionId,
        string filePath,
        CancellationToken cancellationToken) =>
        ImportExcelAsync(sessionId, filePath, committedProgress: null, cancellationToken);

    public async Task<ImportWorkflowResult> ImportExcelAsync(
        Guid sessionId,
        string filePath,
        IProgress<ImportCommittedProgress>? committedProgress,
        CancellationToken cancellationToken)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("导入会话不能为空。", nameof(sessionId));
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var fullPath = Path.GetFullPath(filePath);
        var replayProgress = new ReplayProgress(committedProgress);
        var stagingCompleted = false;
        try
        {
            var contentFingerprint = await ComputeFileFingerprintAsync(fullPath, cancellationToken).ConfigureAwait(false);
            var inspection = await InspectExcelAsync(fullPath, cancellationToken).ConfigureAwait(false);
            var mapping = await mappingPresenter.ConfirmAsync(inspection, cancellationToken).ConfigureAwait(false);
            if (mapping is null) throw new OperationCanceledException("Excel column mapping was cancelled.");
            if (mapping.SourceColumns.Count != mapping.SourceColumnCount)
                throw new ImportWorkflowException(ImportUiError.InvalidData);
            await using var confirmedStream = OpenExcelStream(fullPath);
            var confirmedContentFingerprint = await ComputeStreamFingerprintAsync(confirmedStream, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(contentFingerprint, confirmedContentFingerprint, StringComparison.Ordinal))
                throw new ImportWorkflowException(ImportUiError.InvalidData);
            confirmedStream.Position = 0;
            var source = ReplaySource.ForExcel(fullPath, mapping, mapping.SourceColumns, contentFingerprint);
            RememberReplaySource(sessionId, source);
            await stagingService.StageAsync(
                new ImportRequest(
                    sessionId,
                    "Excel",
                    Path.GetFileName(fullPath),
                    ColumnMapping: mapping,
                    SourceColumns: mapping.SourceColumns,
                    SourceContentFingerprint: contentFingerprint),
                excelReader.ReadAsync(confirmedStream, mapping, cancellationToken),
                replayProgress,
                cancellationToken).ConfigureAwait(false);
            stagingCompleted = true;
            RemoveReplaySource(sessionId);
            return await LoadResultAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (ExcelImportException error)
        {
            throw new ImportWorkflowException(MapExcelError(error.Code), error);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new ImportWorkflowException(ImportUiError.InvalidWorkbook, error);
        }
        finally
        {
            if (!stagingCompleted && !replayProgress.SessionCreated)
                RemoveReplaySource(sessionId);
        }
    }

    public Task<ImportWorkflowResult> ResumeAsync(
        Guid sessionId,
        CancellationToken cancellationToken) =>
        ResumeAsync(sessionId, committedProgress: null, cancellationToken);

    public async Task<ImportWorkflowResult> ResumeAsync(
        Guid sessionId,
        IProgress<ImportCommittedProgress>? committedProgress,
        CancellationToken cancellationToken)
    {
        var source = TryGetReplaySource(sessionId, out var retained)
            ? retained
            : await RestoreReplaySourceAsync(sessionId, cancellationToken).ConfigureAwait(false);
        try
        {
            var authorization = new ImportResumeRequest(
                sessionId,
                source.Kind == ReplaySourceKind.Excel ? "Excel" : "Text",
                source.Mapping,
                source.ContentFingerprint);
            if (source.Kind == ReplaySourceKind.Excel)
            {
                await using var stream = OpenExcelStream(source.FilePath!);
                var actualFingerprint = await ComputeStreamFingerprintAsync(stream, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(actualFingerprint, source.ContentFingerprint, StringComparison.Ordinal))
                {
                    RemoveReplaySource(sessionId);
                    throw new ImportWorkflowException(ImportUiError.InvalidData);
                }
                RememberReplaySource(sessionId, source);
                stream.Position = 0;
                await stagingService.ResumeAsync(
                    authorization,
                    excelReader.ReadAsync(stream, source.Mapping!, cancellationToken),
                    TransferMode.Distribution,
                    committedProgress,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await stagingService.ResumeAsync(
                    authorization,
                    ReadTextAsync(source.Text!, cancellationToken),
                    TransferMode.Distribution,
                    committedProgress,
                    cancellationToken).ConfigureAwait(false);
            }
            RemoveReplaySource(sessionId);
            return await LoadResultAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (ExcelImportException error)
        {
            throw new ImportWorkflowException(MapExcelError(error.Code), error);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new ImportWorkflowException(ImportUiError.InvalidWorkbook, error);
        }
    }

    public void DiscardReplaySource(Guid sessionId) => RemoveReplaySource(sessionId);

    public bool HasResumableReplaySource(Guid sessionId)
    {
        lock (replayGate)
            return replaySources.ContainsKey(sessionId);
    }

    public async Task<Guid?> FindLatestResumableExcelSessionAsync(
        CancellationToken cancellationToken = default)
    {
        await using var unit = await unitOfWorkFactory.BeginAsync(cancellationToken).ConfigureAwait(false);
        var session = await unit.Imports.GetLatestResumableExcelSessionAsync(cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            await unit.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        var definitions = await unit.Imports.GetColumnDefinitionsAsync(session.Id, cancellationToken).ConfigureAwait(false);
        await unit.RollbackAsync(cancellationToken).ConfigureAwait(false);
        return TryReconstructMapping(session, definitions, out _) ? session.Id : null;
    }

    public async Task<bool> CanResumeAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty) return false;
        if (HasResumableReplaySource(sessionId)) return true;
        await using var unit = await unitOfWorkFactory.BeginAsync(cancellationToken).ConfigureAwait(false);
        var session = await unit.Imports.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var definitions = session is null
            ? []
            : await unit.Imports.GetColumnDefinitionsAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await unit.RollbackAsync(cancellationToken).ConfigureAwait(false);
        return session is not null && TryReconstructMapping(session, definitions, out _);
    }

    public async Task<ImportWorkflowResult> UpdateLinkAsync(
        Guid rowId,
        string shareUrl,
        string? extractionCode,
        CancellationToken cancellationToken)
    {
        await using var unit = await unitOfWorkFactory.BeginAsync(cancellationToken).ConfigureAwait(false);
        var row = await unit.Imports.GetRowByIdAsync(rowId, cancellationToken).ConfigureAwait(false)
            ?? throw new ImportWorkflowException(ImportUiError.InvalidData);
        await unit.RollbackAsync(cancellationToken).ConfigureAwait(false);
        await editingService.EditAsync(
            new DraftRowEditRequest(
                row.ImportSessionId,
                rowId,
                shareUrl,
                extractionCode,
                clearExtractionCode: string.IsNullOrWhiteSpace(extractionCode)),
            TransferMode.Distribution,
            cancellationToken).ConfigureAwait(false);
        return await LoadResultAsync(row.ImportSessionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ImportWorkflowResult> SetExcludedAsync(
        Guid rowId,
        bool excluded,
        CancellationToken cancellationToken)
    {
        await using var unit = await unitOfWorkFactory.BeginAsync(cancellationToken).ConfigureAwait(false);
        var row = await unit.Imports.GetRowByIdAsync(rowId, cancellationToken).ConfigureAwait(false)
            ?? throw new ImportWorkflowException(ImportUiError.InvalidData);
        await unit.RollbackAsync(cancellationToken).ConfigureAwait(false);
        if (excluded)
            await editingService.ExcludeAsync(
                row.ImportSessionId, rowId, TransferMode.Distribution, cancellationToken).ConfigureAwait(false);
        else
            await editingService.RestoreAsync(
                row.ImportSessionId, rowId, TransferMode.Distribution, cancellationToken).ConfigureAwait(false);
        return await LoadResultAsync(row.ImportSessionId, cancellationToken).ConfigureAwait(false);
    }

    private IAsyncEnumerable<ImportSourceRow> ReadTextAsync(string text, CancellationToken cancellationToken) =>
        textParser.ParseAsync(new StringReader(text), cancellationToken);

    private async IAsyncEnumerable<ImportSourceRow> ReadExcelAsync(
        string filePath,
        ConfirmedImportColumnMapping mapping,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await foreach (var row in excelReader.ReadAsync(stream, mapping, cancellationToken).ConfigureAwait(false))
            yield return row;
    }

    private async Task<TabularWorkbookInspection> InspectExcelAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await excelInspector.InspectAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ComputeFileFingerprintAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        await using var stream = OpenExcelStream(filePath);
        return await ComputeStreamFingerprintAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ComputeStreamFingerprintAsync(
        Stream stream,
        CancellationToken cancellationToken) =>
        Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
            .ToLowerInvariant();

    private static FileStream OpenExcelStream(string filePath) => new(
        filePath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        bufferSize: 64 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    private async Task<ImportWorkflowResult> LoadResultAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        await using var unit = await unitOfWorkFactory.BeginAsync(cancellationToken).ConfigureAwait(false);
        var counts = await unit.Imports.GetDispositionCountsAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var accountResolution = await unit.Imports.GetRequestedAccountResolutionCountsAsync(
            sessionId, cancellationToken).ConfigureAwait(false);
        await unit.RollbackAsync(cancellationToken).ConfigureAwait(false);
        return new ImportWorkflowResult(sessionId, new ImportDraftStatistics(
            counts.TotalRows,
            counts.ReadyRows,
            counts.DuplicateRows,
            counts.InvalidRows,
            counts.ExcludedRows,
            counts.ConflictRows,
            counts.NeedsResolutionRows,
            accountResolution.TotalGroups > 0,
            (int)Math.Min(int.MaxValue, accountResolution.UnresolvedGroups)));
    }

    private async Task<ReplaySource> RestoreReplaySourceAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        ImportSessionRecord session;
        IReadOnlyList<ImportColumnDefinitionRecord> definitions;
        await using (var unit = await unitOfWorkFactory.BeginAsync(cancellationToken).ConfigureAwait(false))
        {
            session = await unit.Imports.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
                ?? throw new ImportWorkflowException(ImportUiError.InvalidData);
            definitions = await unit.Imports.GetColumnDefinitionsAsync(sessionId, cancellationToken).ConfigureAwait(false);
            await unit.RollbackAsync(cancellationToken).ConfigureAwait(false);
        }
        if (!TryReconstructMapping(session, definitions, out var mapping))
            throw new ImportWorkflowException(ImportUiError.InvalidData);

        var selectedPath = await dialogService.PickExcelFileAsync(cancellationToken).ConfigureAwait(false);
        if (selectedPath is null) throw new OperationCanceledException("Excel resume source selection was cancelled.");
        var source = ReplaySource.ForExcel(
            Path.GetFullPath(selectedPath),
            mapping,
            mapping.SourceColumns,
            session.SourceContentFingerprint!);
        return source;
    }

    private static bool TryReconstructMapping(
        ImportSessionRecord session,
        IReadOnlyList<ImportColumnDefinitionRecord> definitions,
        out ConfirmedImportColumnMapping mapping)
    {
        mapping = null!;
        if (!string.Equals(session.SourceType, "Excel", StringComparison.Ordinal) ||
            !string.Equals(session.Status, "Parsing", StringComparison.Ordinal) ||
            session.ColumnMappingFingerprint is null ||
            session.SourceContentFingerprint is not { Length: 64 } ||
            definitions.Count == 0)
            return false;

        try
        {
            mapping = new ConfirmedImportColumnMapping(
                session.HeaderRowNumber,
                definitions.Select(definition => new TabularImportColumnMapping(
                    definition.ColumnOrdinal,
                    ParsePersistedRole(definition.MappedRole))),
                definitions.Select(definition => new TabularInspectionColumn(
                    definition.ColumnOrdinal,
                    definition.SourceHeader)));
        }
        catch (ArgumentException)
        {
            return false;
        }
        return string.Equals(mapping.Fingerprint, session.ColumnMappingFingerprint, StringComparison.Ordinal);
    }

    private static TabularImportColumnRole ParsePersistedRole(string role) => role switch
    {
        "Preserve" => TabularImportColumnRole.Preserve,
        "Title" => TabularImportColumnRole.SourceTitle,
        "ShareUrl" => TabularImportColumnRole.ShareUrl,
        "ExtractionCode" => TabularImportColumnRole.ExtractionCode,
        "TargetPath" => TabularImportColumnRole.TargetPath,
        "OuterFolderName" => TabularImportColumnRole.OuterFolderName,
        "RequestedAccountName" => TabularImportColumnRole.RequestedAccountName,
        "Note" => TabularImportColumnRole.Note,
        _ => throw new ArgumentException("The persisted mapping role is invalid.", nameof(role)),
    };

    private void RememberReplaySource(Guid sessionId, ReplaySource source)
    {
        lock (replayGate)
        {
            replaySources[sessionId] = new ReplayEntry(source, ++replaySequence);
            while (replaySources.Count > MaxRetainedReplaySources)
            {
                var oldest = replaySources.MinBy(pair => pair.Value.LastAccessSequence).Key;
                replaySources.Remove(oldest);
            }
        }
    }

    private bool TryGetReplaySource(Guid sessionId, out ReplaySource source)
    {
        lock (replayGate)
        {
            if (!replaySources.TryGetValue(sessionId, out var entry))
            {
                source = null!;
                return false;
            }

            source = entry.Source;
            replaySources[sessionId] = entry with { LastAccessSequence = ++replaySequence };
            return true;
        }
    }

    private void RemoveReplaySource(Guid sessionId)
    {
        lock (replayGate)
            replaySources.Remove(sessionId);
    }

    private static ImportUiError MapExcelError(ExcelImportErrorCode code) => code switch
    {
        ExcelImportErrorCode.UnsafePackage => ImportUiError.UnsafeWorkbook,
        ExcelImportErrorCode.MissingRequiredHeader or ExcelImportErrorCode.DuplicateHeader => ImportUiError.MissingHeader,
        ExcelImportErrorCode.InvalidWorkbook or ExcelImportErrorCode.MissingWorksheet => ImportUiError.InvalidWorkbook,
        _ => ImportUiError.InvalidData,
    };

    private enum ReplaySourceKind { Text, Excel }

    private sealed record ReplaySource(
        ReplaySourceKind Kind,
        string? Text,
        string? FilePath,
        ConfirmedImportColumnMapping? Mapping,
        IReadOnlyList<TabularInspectionColumn>? SourceColumns,
        string? ContentFingerprint)
    {
        public static ReplaySource ForText(string text) => new(ReplaySourceKind.Text, text, null, null, null, null);
        public static ReplaySource ForExcel(
            string path,
            ConfirmedImportColumnMapping mapping,
            IReadOnlyList<TabularInspectionColumn> sourceColumns,
            string contentFingerprint) =>
            new(ReplaySourceKind.Excel, null, path, mapping, sourceColumns.ToArray(), contentFingerprint);
        public override string ToString() => "ReplaySource { Content = [REDACTED] }";
    }

    private sealed record ReplayEntry(ReplaySource Source, long LastAccessSequence);

    private sealed class ReplayProgress(IProgress<ImportCommittedProgress>? inner)
        : IProgress<ImportCommittedProgress>
    {
        public bool SessionCreated { get; private set; }

        public void Report(ImportCommittedProgress value)
        {
            if (value.Kind == ImportProgressKind.SessionCreated)
                SessionCreated = true;
            inner?.Report(value);
        }
    }
}
