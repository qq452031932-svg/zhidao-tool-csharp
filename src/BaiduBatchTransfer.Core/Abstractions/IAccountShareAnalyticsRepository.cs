using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Core.Abstractions;

public interface IAccountShareAnalyticsRepository
{
    Task<long> ReserveGenerationAsync(
        Guid accountId,
        CancellationToken cancellationToken);

    Task<AccountShareCachedSnapshot> LoadCachedAsync(
        Guid accountId,
        CancellationToken cancellationToken);

    Task<bool> CommitCompleteSyncAsync(
        AccountShareSyncCommit commit,
        CancellationToken cancellationToken);
}
