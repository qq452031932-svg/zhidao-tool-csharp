using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Core.Abstractions;

public interface IBaiduSessionStore
{
    Task SaveAsync(AccountSession session, CancellationToken cancellationToken);

    Task<AccountSession?> LoadAsync(string accountId, CancellationToken cancellationToken);

    Task RemoveAsync(string accountId, CancellationToken cancellationToken);
}
