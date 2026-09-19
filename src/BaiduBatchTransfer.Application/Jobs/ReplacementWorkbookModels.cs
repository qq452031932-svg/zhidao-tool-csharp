using System.Collections.Immutable;

namespace BaiduBatchTransfer.Application.Jobs;

public sealed record ReplacementWorkbookExportRequest(Guid BatchJobId, int PageSize = 500);

public sealed record SplitReplacementWorkbookReceipt(string AccountDisplayName, string ResultKind, ReplacementWorkbookFilePublishReceipt Receipt);

public sealed record PerAccountResultWorkbookReceipt(string AccountDisplayName, ReplacementWorkbookFilePublishReceipt Receipt);

public sealed record ReplacementWorkbookExportResult(long ExportedRows, string SuggestedFileName)
{
    public override string ToString() =>
        $"ReplacementWorkbookExportResult {{ ExportedRows = {ExportedRows}, SuggestedFileName = [REDACTED] }}";
}

public sealed class ReplacementWorkbookLayout
{
    public ReplacementWorkbookLayout(IEnumerable<string> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        Headers = headers.ToImmutableArray();
    }

    public ImmutableArray<string> Headers { get; }

    public override string ToString() =>
        $"ReplacementWorkbookLayout {{ ColumnCount = {Headers.Length}, Headers = [REDACTED] }}";
}

public sealed class ReplacementWorkbookRow
{
    public ReplacementWorkbookRow(long candidateSequence, IEnumerable<string?> textCells)
    {
        if (candidateSequence < 1) throw new ArgumentOutOfRangeException(nameof(candidateSequence));
        ArgumentNullException.ThrowIfNull(textCells);
        CandidateSequence = candidateSequence;
        TextCells = textCells.Select(value => value ?? string.Empty).ToImmutableArray();
    }

    public long CandidateSequence { get; }
    public ImmutableArray<string> TextCells { get; }

    public override string ToString() =>
        $"ReplacementWorkbookRow {{ CandidateSequence = {CandidateSequence}, ColumnCount = {TextCells.Length}, UserContent = [REDACTED] }}";
}

public interface IReplacementWorkbookWriter
{
    Task WriteAsync(
        Stream output,
        ReplacementWorkbookLayout layout,
        IAsyncEnumerable<ReplacementWorkbookRow> rows,
        CancellationToken cancellationToken = default);
}

public sealed record ReplacementWorkbookFilePublishReceipt(
    string FinalPath,
    long ExportedRows,
    int SheetCount)
{
    public override string ToString() =>
        $"ReplacementWorkbookFilePublishReceipt {{ ExportedRows = {ExportedRows}, SheetCount = {SheetCount}, FinalPath = [REDACTED] }}";
}

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

public interface IReplacementWorkbookFilePublisher
{
    Task<ReplacementWorkbookFilePublishReceipt> PublishAsync(
        string destinationDirectory,
        string finalFileName,
        ReplacementWorkbookLayout layout,
        IAsyncEnumerable<ReplacementWorkbookRow> rows,
        CancellationToken cancellationToken = default);

    Task<ReplacementWorkbookFilePublishReceipt> PublishWithManifestAsync(
        string destinationDirectory,
        string finalFileName,
        ReplacementWorkbookLayout layout,
        IAsyncEnumerable<ReplacementWorkbookRow> rows,
        LocalResultWorkbookManifest manifest,
        CancellationToken cancellationToken = default) =>
        PublishAsync(destinationDirectory, finalFileName, layout, rows, cancellationToken);
}
