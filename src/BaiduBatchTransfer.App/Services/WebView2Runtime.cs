using System.Diagnostics;
using Microsoft.Web.WebView2.Core;

namespace BaiduBatchTransfer.App.Services;

/// <param name="IsMissing">明确查到这台电脑没有安装 WebView2 运行组件。查不清时为 false，不误报。</param>
/// <param name="Version">已安装时的版本号。</param>
public sealed record WebView2RuntimeStatus(bool IsMissing, string? Version);

public interface IWebView2RuntimeProbe
{
    WebView2RuntimeStatus Check();
}

/// <summary>
/// 百度网盘账号登录窗口用的是微软 WebView2 运行组件。精简版、LTSC 版或较老的 Windows 可能没有装，
/// 登录窗口就只剩一片空白（用户 10-16：另一位用户装上 Microsoft Edge WebView2 Runtime 后就好了）。
/// </summary>
public sealed class WebView2RuntimeProbe : IWebView2RuntimeProbe
{
    public WebView2RuntimeStatus Check()
    {
        try
        {
            var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            return string.IsNullOrWhiteSpace(version) ? new(true, null) : new(false, version);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            return new(true, null);
        }
        catch (Exception)
        {
            // 查询本身出错（例如组件文件损坏）时不下结论，等真正打开登录窗口时再按具体错误提示。
            return new(false, null);
        }
    }
}

public static class WebView2RuntimeText
{
    /// <summary>微软官方 WebView2 下载页（选「常青版引导程序」）。</summary>
    public const string DownloadPage = "https://developer.microsoft.com/zh-cn/microsoft-edge/webview2/";

    public const string Missing =
        "这台电脑没有安装微软 WebView2 运行组件（Microsoft Edge WebView2 Runtime），登录百度网盘账号需要它。" +
        "请点「下载 WebView2」，在微软官网下载「常青版引导程序」并安装，装好后重新打开登录窗口。";

    public const string StartupReminder =
        "这台电脑没有安装微软 WebView2 运行组件（Microsoft Edge WebView2 Runtime）。\n\n" +
        "添加或重新登录百度网盘账号需要它，没装时登录窗口会是一片空白。\n" +
        "其他功能不受影响。\n\n" +
        "现在打开微软官网下载页吗？请下载「常青版引导程序」安装，装好后重新打开软件。";

    public static bool TryOpenDownloadPage()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(DownloadPage) { UseShellExecute = true });
            return true;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException
            or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
