using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.Application.Jobs;

public sealed record ResourcePageRequest(
    Guid BatchJobId,
    string? CandidateStatus,
    string? GlobalValidityStatus,
    int PageSize,
    long? AfterCandidateSequence = null);

public sealed record ResourcePageResult(
    IReadOnlyList<ResourceItemRecord> Items,
    long TotalCount,
    long? NextCursor);

public sealed record OperationPageRequest(
    Guid BatchJobId,
    Guid? AccountId,
    string? Status,
    DateTimeOffset? UpdatedFrom,
    DateTimeOffset? UpdatedTo,
    int PageSize,
    long? AfterAllocationOrdinal = null);

public sealed record OperationPageResult(
    IReadOnlyList<OperationResultRecord> Items,
    long TotalCount,
    long? NextCursor);

public sealed class JobPageQuery(IUnitOfWorkFactory unitOfWorkFactory)
{
    private readonly IUnitOfWorkFactory unitOfWorkFactory =
        unitOfWorkFactory ?? throw new ArgumentNullException(nameof(unitOfWorkFactory));

    public async Task<ResourcePageResult> GetResourcesAsync(
        ResourcePageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var persistenceQuery = new ResourceResultKeysetQuery(
            request.BatchJobId,
            request.CandidateStatus,
            request.GlobalValidityStatus,
            request.PageSize,
            request.AfterCandidateSequence ?? 0);
        persistenceQuery.Validate();
        await using var unit = await unitOfWorkFactory.BeginAsync(cancellationToken).ConfigureAwait(false);
        var page = await unit.Jobs.GetResourceResultsKeysetPageAsync(persistenceQuery, cancellationToken)
            .ConfigureAwait(false);
        return new(
            page.Items,
            page.TotalCount ?? throw new InvalidOperationException("The resource query did not return its total count."),
            page.NextCursor);
    }

    public async Task<OperationPageResult> GetOperationsAsync(
        OperationPageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var persistenceQuery = new OperationResultKeysetQuery(
            request.BatchJobId,
            request.AccountId,
            request.Status,
            request.UpdatedFrom,
            request.UpdatedTo,
            request.PageSize,
            request.AfterAllocationOrdinal ?? 0);
        persistenceQuery.Validate();
        await using var unit = await unitOfWorkFactory.BeginAsync(cancellationToken).ConfigureAwait(false);
        var page = await unit.Jobs.GetOperationResultsKeysetPageAsync(persistenceQuery, cancellationToken)
            .ConfigureAwait(false);
        return new(
            page.Items,
            page.TotalCount ?? throw new InvalidOperationException("The operation query did not return its total count."),
            page.NextCursor);
    }
}
