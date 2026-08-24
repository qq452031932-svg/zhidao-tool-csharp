using BaiduBatchTransfer.App.Features.Acceptance;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class BaiduAcceptanceProbeDownloadSafetyTests
{
    [Fact]
    public void ProductionDownloadConfigurationRegistersAHandlerThatCancelsAndHandles()
    {
        var host = new FakeHost();
        using var subscription = BaiduAcceptanceProbeDownloadSafety.Configure(host);
        var request = new FakeRequest();

        host.Handler!(request);

        Assert.True(request.Cancel);
        Assert.True(request.Handled);
    }
    private sealed class FakeHost : IBaiduAcceptanceProbeDownloadHost
    {
        public Action<IBaiduAcceptanceProbeDownloadRequest>? Handler { get; private set; }
        public IDisposable Register(Action<IBaiduAcceptanceProbeDownloadRequest> handler) { Handler = handler; return new Empty(); }
    }
    private sealed class FakeRequest : IBaiduAcceptanceProbeDownloadRequest { public bool Cancel { get; set; } public bool Handled { get; set; } }
    private sealed class Empty : IDisposable { public void Dispose() { } }
}
