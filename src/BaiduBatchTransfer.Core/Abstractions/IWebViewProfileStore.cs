namespace BaiduBatchTransfer.Core.Abstractions;

public interface IWebViewProfileStore
{
    Task CloseAsync(Guid accountId, CancellationToken cancellationToken);

    Task DeleteAsync(string exactProfilePath, CancellationToken cancellationToken);
}
