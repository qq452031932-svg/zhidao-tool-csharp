using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.Application.Tests.Jobs;

public sealed class ReplicationPreflightEstimatorTests
{
    [Fact]
    public async Task EstimateSeparatesDatabaseIndexesWalAndSafetyReserve()
    {
        var probe = new StubDiskSpaceProbe(10L * 1024 * 1024 * 1024);
        var estimator = new ReplicationPreflightEstimator(probe, @"C:\data\jobs.db");

        var estimate = await estimator.EstimateAsync(1_000, 3_000, 3);

        Assert.Equal(1_000, estimate.ResourceCount);
        Assert.Equal(3_000, estimate.OperationCount);
        Assert.True(estimate.DatabaseBytes > 0);
        Assert.True(estimate.IndexBytes > 0);
        Assert.True(estimate.WalBytes > 0);
        Assert.True(estimate.SafetyReserveBytes >= 64L * 1024 * 1024);
        Assert.Equal(
            estimate.DatabaseBytes + estimate.IndexBytes + estimate.WalBytes + estimate.SafetyReserveBytes,
            estimate.RequiredFreeBytes);
        Assert.True(estimate.HasSufficientSpace);
        Assert.Equal(@"C:\data\jobs.db", probe.LastPath);
    }

    [Fact]
    public async Task EstimateReportsInsufficientSpaceWithoutHidingRequiredBytes()
    {
        var estimator = new ReplicationPreflightEstimator(new StubDiskSpaceProbe(1), "jobs.db");

        var estimate = await estimator.EstimateAsync(1, 3, 3);

        Assert.False(estimate.HasSufficientSpace);
        Assert.Equal(1, estimate.AvailableFreeBytes);
        Assert.True(estimate.RequiredFreeBytes > estimate.AvailableFreeBytes);
    }

    [Fact]
    public async Task EstimateRejectsOverflowBeforeCallingDiskProbe()
    {
        var probe = new StubDiskSpaceProbe(long.MaxValue);
        var estimator = new ReplicationPreflightEstimator(probe, "jobs.db");

        await Assert.ThrowsAsync<OverflowException>(() =>
            estimator.EstimateAsync(long.MaxValue, long.MaxValue, int.MaxValue));
        await Assert.ThrowsAsync<OverflowException>(() =>
            estimator.EstimateAsync(1, 1, 1, long.MaxValue, long.MaxValue, int.MaxValue, int.MaxValue));

        Assert.Null(probe.LastPath);
    }

    [Fact]
    public async Task EstimateIncludesShareOperationsResultsOutputSnapshotsIndexesWalAndReserve()
    {
        var estimator = new ReplicationPreflightEstimator(
            new StubDiskSpaceProbe(long.MaxValue), "jobs.db");

        var withoutSharing = await estimator.EstimateAsync(200_000, 600_000, 3);
        var withSharing = await estimator.EstimateAsync(
            200_000, 600_000, 3, shareOperationCount: 600_000,
            shareResultCount: 600_000, outputSnapshotCount: 3, resultColumnCount: 14);

        Assert.Equal(600_000, withSharing.ShareOperationCount);
        Assert.Equal(600_000, withSharing.ShareResultCount);
        Assert.Equal(3, withSharing.OutputSnapshotCount);
        Assert.True(withSharing.DatabaseBytes > withoutSharing.DatabaseBytes);
        Assert.True(withSharing.IndexBytes > withoutSharing.IndexBytes);
        Assert.True(withSharing.WalBytes > withoutSharing.WalBytes);
        Assert.True(withSharing.SafetyReserveBytes >= withoutSharing.SafetyReserveBytes);
    }

    internal sealed class StubDiskSpaceProbe(long availableBytes) : IDiskSpaceProbe
    {
        public string? LastPath { get; private set; }

        public Task<long> GetAvailableBytesAsync(string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastPath = path;
            return Task.FromResult(availableBytes);
        }
    }
}
