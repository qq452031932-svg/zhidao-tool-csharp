using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.Application.Jobs;

public sealed class ReplicationPreflightEstimator
{
    // Deliberately conservative logical row sizes. Index and WAL are accounted separately.
    public const long ResourceRowBytes = 2_048;
    public const long OperationRowBytes = 3_072;
    public const long AllocationRowBytes = 1_024;
    public const long FixedBatchAndAuditBytes = 8_192;
    public const long MinimumSafetyReserveBytes = 64L * 1024 * 1024;

    private readonly IDiskSpaceProbe diskSpaceProbe;
    private readonly string databasePath;

    public ReplicationPreflightEstimator(IDiskSpaceProbe diskSpaceProbe, string databasePath)
    {
        this.diskSpaceProbe = diskSpaceProbe ?? throw new ArgumentNullException(nameof(diskSpaceProbe));
        this.databasePath = string.IsNullOrWhiteSpace(databasePath)
            ? throw new ArgumentException("A database path is required.", nameof(databasePath))
            : databasePath;
    }

    public async Task<StorageGrowthEstimate> EstimateAsync(
        long resourceCount,
        long operationCount,
        int allocationCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(resourceCount);
        ArgumentOutOfRangeException.ThrowIfNegative(operationCount);
        ArgumentOutOfRangeException.ThrowIfNegative(allocationCount);

        long databaseBytes;
        long indexBytes;
        long walBytes;
        long safetyReserveBytes;
        long requiredFreeBytes;
        checked
        {
            databaseBytes =
                resourceCount * ResourceRowBytes +
                operationCount * OperationRowBytes +
                (long)allocationCount * AllocationRowBytes +
                FixedBatchAndAuditBytes;
            indexBytes = databaseBytes / 2 + databaseBytes % 2;
            walBytes = databaseBytes + indexBytes;
            safetyReserveBytes = Math.Max(MinimumSafetyReserveBytes, (databaseBytes + indexBytes) / 4);
            requiredFreeBytes = databaseBytes + indexBytes + walBytes + safetyReserveBytes;
        }

        var available = await diskSpaceProbe.GetAvailableBytesAsync(databasePath, cancellationToken).ConfigureAwait(false);
        if (available < 0) throw new InvalidOperationException("The disk probe returned an invalid free-space value.");
        return new StorageGrowthEstimate(
            resourceCount,
            operationCount,
            databaseBytes,
            indexBytes,
            walBytes,
            safetyReserveBytes,
            requiredFreeBytes,
            available,
            available >= requiredFreeBytes);
    }
}
