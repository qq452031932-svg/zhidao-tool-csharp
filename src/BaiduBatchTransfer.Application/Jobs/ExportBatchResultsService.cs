using System.Runtime.CompilerServices;
using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.Application.Jobs;

public sealed record ExportBatchResultsRequest(
    Guid BatchJobId,
    Guid? AccountId,
    string? Status,
    DateTimeOffset? UpdatedFrom,
    int PageSize = 500,
    DateTimeOffset? UpdatedTo = null);

public sealed record BatchResultExportRow(
    long SourceRowNumber,
    string ShareUrl,
    string Disposition,
    string? Account,
    string? TargetPath,
    string? Note,
    string? ErrorMessage)
{
    public override string ToString() =>
        $"BatchResultExportRow {{ SourceRowNumber = {SourceRowNumber}, Disposition = {Disposition}, UserContent = [REDACTED] }}";
}

public interface IBatchResultWriter
{
    Task WriteAsync(
        Stream stream,
        IAsyncEnumerable<BatchResultExportRow> rows,
        CancellationToken cancellationToken = default);
}

public sealed record ExportBatchResultsResult(long ExportedRows);

public sealed class ExportBatchResultsService(IUnitOfWorkFactory unitOfWorkFactory)
{
    private readonly IUnitOfWorkFactory unitOfWorkFactory =
        unitOfWorkFactory ?? throw new ArgumentNullException(nameof(unitOfWorkFactory));

    public async Task<ExportBatchResultsResult> ExportAsync(
        ExportBatchResultsRequest request,
        Stream output,
        IBatchResultWriter writer,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(writer);
        new OperationResultKeysetQuery(
            request.BatchJobId,
            request.AccountId,
            request.Status,
            request.UpdatedFrom,
            request.UpdatedTo,
            request.PageSize).Validate();
        long exportedRows = 0;
        await writer.WriteAsync(
            output,
            StreamRowsAsync(request, count =>
            {
                exportedRows = count;
                progress?.Report(count);
            }, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        return new(exportedRows);
    }

    private async IAsyncEnumerable<BatchResultExportRow> StreamRowsAsync(
        ExportBatchResultsRequest request,
        Action<long> report,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        long cursor = 0;
        long exported = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            KeysetPage<OperationResultRecord> page;
            await using (var unit = await unitOfWorkFactory.BeginAsync(cancellationToken).ConfigureAwait(false))
            {
                page = await unit.Jobs.GetOperationResultsKeysetPageAsync(
                    new(
                        request.BatchJobId,
                        request.AccountId,
                        request.Status,
                        request.UpdatedFrom,
                        request.UpdatedTo,
                        request.PageSize,
                        cursor,
                        IncludeTotalCount: false),
                    cancellationToken).ConfigureAwait(false);
            }

            foreach (var item in page.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new(
                    item.CandidateSequence,
                    item.ShareUrl,
                    item.OutcomeKind ?? item.Status,
                    item.AccountDisplayName,
                    item.TargetPath,
                    item.Note,
                    item.LastErrorMessage ?? item.LastErrorCode);
                exported = checked(exported + 1);
                report(exported);
            }
            if (page.NextCursor is not { } next) yield break;
            cursor = next;
        }
    }
}
