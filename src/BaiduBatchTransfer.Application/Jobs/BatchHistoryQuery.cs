using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.Application.Jobs;

public sealed record BatchHistoryItem(
    Guid BatchJobId,
    string Name,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);

public sealed class BatchHistoryQuery(IUnitOfWorkFactory unitOfWorkFactory)
{
    private readonly IUnitOfWorkFactory unitOfWorkFactory =
        unitOfWorkFactory ?? throw new ArgumentNullException(nameof(unitOfWorkFactory));

    public async Task<IReadOnlyList<BatchHistoryItem>> GetRecentAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var unit = await unitOfWorkFactory.BeginAsync(cancellationToken).ConfigureAwait(false);
        var records = await unit.Jobs.GetRecentAsync(limit, cancellationToken).ConfigureAwait(false);
        await unit.RollbackAsync(cancellationToken).ConfigureAwait(false);
        return records.Select(record => new BatchHistoryItem(
            record.Id,
            record.Name,
            record.Status,
            record.CreatedAt,
            record.StartedAt,
            record.CompletedAt)).ToArray();
    }
}
