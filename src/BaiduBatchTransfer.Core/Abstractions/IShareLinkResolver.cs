using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Core.Abstractions;

public interface IShareLinkResolver
{
    Task<BaiduAdapterResult<ResolvedShareLink>> ResolveAsync(
        string rawUrl,
        CancellationToken cancellationToken);
}
