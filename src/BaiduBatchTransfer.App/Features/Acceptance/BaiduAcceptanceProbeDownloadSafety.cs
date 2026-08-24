using Microsoft.Web.WebView2.Core;

namespace BaiduBatchTransfer.App.Features.Acceptance;

public interface IBaiduAcceptanceProbeDownloadRequest { bool Cancel { set; } bool Handled { set; } }
public interface IBaiduAcceptanceProbeDownloadHost { IDisposable Register(Action<IBaiduAcceptanceProbeDownloadRequest> handler); }

public static class BaiduAcceptanceProbeDownloadSafety
{
    public static IDisposable Configure(IBaiduAcceptanceProbeDownloadHost host) =>
        host.Register(request => { request.Cancel = true; request.Handled = true; });
}

public sealed class WebView2BaiduAcceptanceProbeDownloadHost(CoreWebView2 core) : IBaiduAcceptanceProbeDownloadHost
{
    public IDisposable Register(Action<IBaiduAcceptanceProbeDownloadRequest> handler)
    {
        EventHandler<CoreWebView2DownloadStartingEventArgs> subscribed = (_, args) => handler(new Request(args));
        core.DownloadStarting += subscribed;
        return new Subscription(core, subscribed);
    }

    private sealed class Request(CoreWebView2DownloadStartingEventArgs args) : IBaiduAcceptanceProbeDownloadRequest
    {
        public bool Cancel { set => args.Cancel = value; }
        public bool Handled { set => args.Handled = value; }
    }
    private sealed class Subscription(CoreWebView2 core, EventHandler<CoreWebView2DownloadStartingEventArgs> handler) : IDisposable
    {
        public void Dispose() => core.DownloadStarting -= handler;
    }
}
