using BaiduBatchTransfer.Application.Jobs;

namespace BaiduBatchTransfer.App.Features.Common;

public interface IWorkspaceBatchHistorySource
{
    Task<IReadOnlyList<WorkspaceBatchItem>> LoadRecentAsync(
        int limit,
        CancellationToken cancellationToken);
}

public sealed class WorkspaceBatchHistorySource(BatchHistoryQuery query) : IWorkspaceBatchHistorySource
{
    public async Task<IReadOnlyList<WorkspaceBatchItem>> LoadRecentAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        var items = await query.GetRecentAsync(limit, cancellationToken).ConfigureAwait(false);
        return items.Select(item => new WorkspaceBatchItem(
            item.BatchJobId,
            string.IsNullOrWhiteSpace(item.Name) ? $"批次 {item.BatchJobId.ToString("N")[..8]}" : item.Name,
            item.Status,
            item.CreatedAt)).ToArray();
    }
}

internal sealed class EmptyWorkspaceBatchHistorySource : IWorkspaceBatchHistorySource
{
    public static EmptyWorkspaceBatchHistorySource Instance { get; } = new();
    public Task<IReadOnlyList<WorkspaceBatchItem>> LoadRecentAsync(int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<WorkspaceBatchItem>>([]);
}
