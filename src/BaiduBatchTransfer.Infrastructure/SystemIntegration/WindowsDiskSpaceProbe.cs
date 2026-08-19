using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.Infrastructure.SystemIntegration;

public sealed class WindowsDiskSpaceProbe : IDiskSpaceProbe
{
    public Task<long> GetAvailableBytesAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("The path does not identify a storage volume.", nameof(path));
        return Task.FromResult(new DriveInfo(root).AvailableFreeSpace);
    }
}
