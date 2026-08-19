using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.Application.Jobs;

public sealed class BatchStatisticsQuery(IUnitOfWorkFactory unitOfWorkFactory)
{
    private readonly IUnitOfWorkFactory unitOfWorkFactory =
        unitOfWorkFactory ?? throw new ArgumentNullException(nameof(unitOfWorkFactory));

    public async Task<BatchStatisticsRecord> ExecuteAsync(
        Guid batchJobId,
        CancellationToken cancellationToken = default)
    {
        if (batchJobId == Guid.Empty) throw new ArgumentException("A batch is required.", nameof(batchJobId));
        await using var unit = await unitOfWorkFactory.BeginAsync(cancellationToken).ConfigureAwait(false);
        return await unit.Jobs.GetBatchStatisticsAsync(batchJobId, cancellationToken).ConfigureAwait(false);
    }
}
