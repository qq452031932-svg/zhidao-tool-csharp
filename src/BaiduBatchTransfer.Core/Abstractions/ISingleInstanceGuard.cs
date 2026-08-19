namespace BaiduBatchTransfer.Core.Abstractions;

public interface ISingleInstanceGuard
{
    Task<bool> TryAcquireAsync(CancellationToken cancellationToken = default);
}
