namespace BaiduBatchTransfer.Core.Abstractions;

public interface IDiskSpaceProbe
{
    Task<long> GetAvailableBytesAsync(string path, CancellationToken cancellationToken = default);
}
