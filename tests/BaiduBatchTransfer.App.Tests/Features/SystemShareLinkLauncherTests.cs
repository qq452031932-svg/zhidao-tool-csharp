using System.Diagnostics;
using BaiduBatchTransfer.App.Features.ShareAnalytics;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class SystemShareLinkLauncherTests
{
    [Theory]
    [InlineData("http://pan.baidu.com/s/abc")]
    [InlineData("https://pan.baidu.com:444/s/abc")]
    [InlineData("https://user@pan.baidu.com/s/abc")]
    [InlineData("https://pan.baidu.com.evil.example/s/abc")]
    [InlineData("https://pan.baidu.com/disk/main")]
    [InlineData("https://pan.baidu.com/s/abc#fragment")]
    public void UnsafeAddressesAreRejectedWithoutStartingAProcess(string value)
    {
        var starter = new FakeStarter();
        Assert.False(new SystemShareLinkLauncher(starter).TryOpen(new Uri(value), out var message));
        Assert.Empty(starter.Starts);
        Assert.DoesNotContain(value, message, StringComparison.Ordinal);
    }

    [Fact]
    public void BaiduShareAddressOpensWithShellExecute()
    {
        var starter = new FakeStarter();
        Assert.True(new SystemShareLinkLauncher(starter).TryOpen(
            new Uri("https://pan.baidu.com/s/abc?pwd=1111"), out _));
        var start = Assert.Single(starter.Starts);
        Assert.True(start.UseShellExecute);
    }

    private sealed class FakeStarter : IExternalProcessStarter
    {
        public List<ProcessStartInfo> Starts { get; } = [];
        public void Start(ProcessStartInfo startInfo) => Starts.Add(startInfo);
    }
}
