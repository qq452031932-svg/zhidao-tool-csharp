using System.IO;
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
        var source = ReplaySource.ForExcel(fullPath);
        RememberReplaySource(sessionId, source);
        var replayProgress = new ReplayProgress(committedProgress);
        var stagingCompleted = false;
        try
        {
            await stagingService.StageAsync(
                new ImportRequest(sessionId, "Excel", "[REDACTED]"),
                ReadExcelAsync(fullPath, cancellationToken),
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
        if (!TryGetReplaySource(sessionId, out var source))
            throw new ImportWorkflowException(ImportUiError.InvalidData);
        var rows = source.Kind == ReplaySourceKind.Text
            ? ReadTextAsync(source.Text!, cancellationToken)
            : ReadExcelAsync(source.FilePath!, cancellationToken);
        try
        {
            await stagingService.ResumeAsync(
                sessionId,
                rows,
                TransferMode.Distribution,
                committedProgress,
                cancellationToken).ConfigureAwait(false);
            RemoveReplaySource(sessionId);
            return await LoadResultAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (ExcelImportException error)
        {
            throw new ImportWorkflowException(MapExcelError(error.Code), error);
        }
    }

    public void DiscardReplaySource(Guid sessionId) => RemoveReplaySource(sessionId);

    public bool HasResumableReplaySource(Guid sessionId)
    {
        lock (replayGate)
            return replaySources.ContainsKey(sessionId);
    }

    public async Task UpdateLinkAsync(
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
    }

    public async Task SetExcludedAsync(
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
    }

    private IAsyncEnumerable<ImportSourceRow> ReadTextAsync(string text, CancellationToken cancellationToken) =>
        textParser.ParseAsync(new StringReader(text), cancellationToken);

    private async IAsyncEnumerable<ImportSourceRow> ReadExcelAsync(
        string filePath,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await foreach (var row in excelReader.ReadAsync(stream, cancellationToken).ConfigureAwait(false))
            yield return row;
    }

    private async Task<ImportWorkflowResult> LoadResultAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        await using var unit = await unitOfWorkFactory.BeginAsync(cancellationToken).ConfigureAwait(false);
        var counts = await unit.Imports.GetDispositionCountsAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var accountGroups = await unit.Imports.GetRequestedAccountNameGroupsPageAsync(
            sessionId, new PageQuery(1, 1), cancellationToken).ConfigureAwait(false);
        await unit.RollbackAsync(cancellationToken).ConfigureAwait(false);
        return new ImportWorkflowResult(sessionId, new ImportDraftStatistics(
            counts.TotalRows,
            counts.ReadyRows,
            counts.DuplicateRows,
            counts.InvalidRows,
            counts.ExcludedRows,
            counts.ConflictRows,
            counts.NeedsResolutionRows,
            accountGroups.TotalCount > 0,
            (int)Math.Min(int.MaxValue, accountGroups.TotalCount)));
    }

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

    private sealed record ReplaySource(ReplaySourceKind Kind, string? Text, string? FilePath)
    {
        public static ReplaySource ForText(string text) => new(ReplaySourceKind.Text, text, null);
        public static ReplaySource ForExcel(string path) => new(ReplaySourceKind.Excel, null, path);
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
