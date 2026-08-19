namespace BaiduBatchTransfer.Core.Abstractions;

public interface IWebViewProfileStore
{
    Task CloseAsync(Guid accountId, CancellationToken cancellationToken);

    Task DeleteAsync(Guid accountId, string exactProfilePath, CancellationToken cancellationToken);
}

public interface IWebViewProfileCloser
{
    Task CloseAsync(Guid accountId, CancellationToken cancellationToken);
}

public interface IAccountProfilePathPolicy
{
    string GetAccountProfilePath(Guid accountId);

    void EnsureSafeAccountPath(Guid accountId, string candidatePath);
}
