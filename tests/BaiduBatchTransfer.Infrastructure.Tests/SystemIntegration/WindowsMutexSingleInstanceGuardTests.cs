using BaiduBatchTransfer.Infrastructure.SystemIntegration;

namespace BaiduBatchTransfer.Infrastructure.Tests.SystemIntegration;

public sealed class WindowsMutexSingleInstanceGuardTests
{
    [Fact]
    public async Task OnlyOneGuardOwnsTheApplicationMutexAndDisposeReleasesIt()
    {
        var name = $"BaiduBatchTransfer.Tests.{Guid.NewGuid():N}";
        using var first = new WindowsMutexSingleInstanceGuard(name);
        using var second = new WindowsMutexSingleInstanceGuard(name);

        Assert.True(await first.TryAcquireAsync());
        Assert.False(await second.TryAcquireAsync());

        first.Dispose();
        using var third = new WindowsMutexSingleInstanceGuard(name);
        Assert.True(await third.TryAcquireAsync());
    }
}
