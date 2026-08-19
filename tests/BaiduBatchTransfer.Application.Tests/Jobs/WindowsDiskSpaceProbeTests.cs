using BaiduBatchTransfer.Infrastructure.SystemIntegration;

namespace BaiduBatchTransfer.Application.Tests.Jobs;

public sealed class WindowsDiskSpaceProbeTests
{
    [Fact]
    public async Task ReportsTheContainingVolumeFreeSpace()
    {
        var value = await new WindowsDiskSpaceProbe().GetAvailableBytesAsync(
            Path.Combine(Path.GetTempPath(), "task11", "jobs.db"));

        Assert.True(value > 0);
    }

    [Fact]
    public async Task HonorsPreCancelledToken()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new WindowsDiskSpaceProbe().GetAvailableBytesAsync("jobs.db", cancellation.Token));
    }
}
