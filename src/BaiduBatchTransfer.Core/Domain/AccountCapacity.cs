namespace BaiduBatchTransfer.Core.Domain;

public sealed record AccountCapacity
{
    public AccountCapacity(long totalBytes, long usedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(totalBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(usedBytes);

        if (usedBytes > totalBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(usedBytes),
                usedBytes,
                "Used capacity cannot exceed total capacity.");
        }

        TotalBytes = totalBytes;
        UsedBytes = usedBytes;
    }

    public long TotalBytes { get; }

    public long UsedBytes { get; }

    public long AvailableBytes => TotalBytes - UsedBytes;
}
