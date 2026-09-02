using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Core.Abstractions;

public interface IAccountShareAnalyticsClient
{
    Task<BaiduAdapterResult<AccountSharePage>> ReadPageAsync(
        AccountSession session,
        string? cursor,
        CancellationToken cancellationToken);
}
