namespace BaiduBatchTransfer.Core.Abstractions;

public interface IShareAnalyticsPreferenceStore
{
    Task<Guid?> LoadLastViewedAccountAsync(CancellationToken cancellationToken = default);
    Task SaveLastViewedAccountAsync(Guid? accountId, CancellationToken cancellationToken = default);
}
