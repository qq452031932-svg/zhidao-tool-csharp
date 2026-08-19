using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.Application.Imports;

public sealed record ImportPageRequest(
    Guid ImportSessionId,
    string? Disposition,
    string? PrecheckStatus,
    int PageSize,
    long? AfterSourceRowNumber = null);

public sealed record ImportPageResult(
    IReadOnlyList<StagedImportRowRecord> Items,
    long TotalCount,
    long? NextSourceRowNumber);

public sealed class ImportPageQuery(IUnitOfWorkFactory unitOfWorkFactory)
{
    private readonly IUnitOfWorkFactory unitOfWorkFactory =
        unitOfWorkFactory ?? throw new ArgumentNullException(nameof(unitOfWorkFactory));

    public async Task<ImportPageResult> ExecuteAsync(
        ImportPageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var persistenceQuery = new StagedImportKeysetQuery(
            request.ImportSessionId,
            request.Disposition,
            request.PrecheckStatus,
            request.PageSize,
            request.AfterSourceRowNumber ?? 0);
        persistenceQuery.Validate();
        await using var unit = await unitOfWorkFactory.BeginAsync(cancellationToken).ConfigureAwait(false);
        var page = await unit.Imports.GetRowsKeysetPageAsync(persistenceQuery, cancellationToken).ConfigureAwait(false);
        return new(
            page.Items,
            page.TotalCount ?? throw new InvalidOperationException("The import query did not return its total count."),
            page.NextCursor);
    }
}
