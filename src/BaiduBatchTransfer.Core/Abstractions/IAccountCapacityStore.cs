using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Core.Abstractions;

public interface IAccountCapacityStore
{
    Task<bool> TryUpdateAsync(
        Guid accountId,
        string expectedBaiduUserId,
        AccountCapacity capacity,
        DateTimeOffset validatedAt,
        CancellationToken cancellationToken = default);
}
