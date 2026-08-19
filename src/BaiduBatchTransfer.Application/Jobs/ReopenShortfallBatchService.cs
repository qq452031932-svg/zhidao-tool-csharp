using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.Application.Jobs;

public sealed class ReopenShortfallBatchService(IUnitOfWorkFactory unitOfWorkFactory)
{
    private readonly IUnitOfWorkFactory unitOfWorkFactory =
        unitOfWorkFactory ?? throw new ArgumentNullException(nameof(unitOfWorkFactory));

    public async Task ReopenAsync(
        Guid batchJobId,
        bool userConfirmed,
        DateTimeOffset reopenedAt,
        CancellationToken cancellationToken = default)
    {
        if (!userConfirmed)
            throw new BatchConfirmationException("Explicit confirmation is required to reopen a shortfall batch.");
        await using var unit = await unitOfWorkFactory.BeginAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var batch = await unit.Jobs.GetAsync(batchJobId, cancellationToken).ConfigureAwait(false)
                ?? throw new BatchDraftValidationException("The shortfall batch does not exist.");
            if (batch.Status != "CompletedWithShortfall" || batch.CompletedAt is null)
                throw new BatchDraftValidationException("Only a completed shortfall batch can be reopened.");
            if (reopenedAt < batch.CompletedAt.Value)
                throw new ArgumentOutOfRangeException(nameof(reopenedAt), "The reopen time cannot precede completion.");
            var allocations = new List<AccountAllocationRecord>();
            for (var pageNumber = 1; ; pageNumber++)
            {
                var page = await unit.Jobs.GetAllocationsPageAsync(
                    batchJobId, new PageQuery(pageNumber, 1_000), cancellationToken).ConfigureAwait(false);
                allocations.AddRange(page.Items);
                if (allocations.Count >= page.TotalCount) break;
            }
            await unit.Jobs.UpdateAllocationsAsync(allocations.Select(allocation => allocation with
            {
                EffectiveTargetSuccessCount = allocation.TargetSuccessCount,
            }), cancellationToken).ConfigureAwait(false);
            await unit.Jobs.UpdateAsync(batch with { Status = "PausedReady", CompletedAt = null }, cancellationToken)
                .ConfigureAwait(false);
            await unit.Jobs.AddExecutionLogsAsync(
                [new ExecutionLogRecord(Guid.NewGuid(), batchJobId, null, null, null,
                    "Finalization", "Information", "SHORTFALL_REOPENED", "Shortfall batch reopen was confirmed.", reopenedAt)],
                cancellationToken).ConfigureAwait(false);
            await unit.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await CreateBatchService.TryRollbackAsync(unit).ConfigureAwait(false);
            throw;
        }
    }
}
