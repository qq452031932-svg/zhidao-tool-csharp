using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using BaiduBatchTransfer.App.Features.Accounts;
using BaiduBatchTransfer.App.Services;
using Microsoft.Web.WebView2.Core;
using Xunit;

namespace BaiduBatchTransfer.App.Tests.Features;

/// <summary>
/// 用户 10-16：另一位用户的登录窗口一片空白，只提示「请安装或更新网页运行组件」，
/// 装上 Microsoft Edge WebView2 Runtime 后就好了。现在没装组件、打不开网页、被跳走分开提示。
/// </summary>
public sealed class WebView2RuntimeTests
{
    [Fact]
    public void AMissingRuntimeSaysSoAndOffersTheDownload()
    {
        var model = new BaiduLoginViewModel();
        Assert.False(model.ShowRuntimeDownload);

        model.ReportRuntimeMissing();

        Assert.True(model.ShowRuntimeDownload);
        Assert.Contains("Microsoft Edge WebView2 Runtime", model.Status);
        Assert.Contains("下载 WebView2", model.Status);
        Assert.Contains("常青版引导程序", model.Status);
        Assert.StartsWith("https://developer.microsoft.com/", WebView2RuntimeText.DownloadPage);
        Assert.Contains("其他功能不受影响", WebView2RuntimeText.StartupReminder);
    }

    [Theory]
    [InlineData(CoreWebView2WebErrorStatus.HostNameNotResolved, "域名解析失败")]
    [InlineData(CoreWebView2WebErrorStatus.Timeout, "连接超时")]
    [InlineData(CoreWebView2WebErrorStatus.ConnectionReset, "网络连接中断")]
    [InlineData(CoreWebView2WebErrorStatus.CannotConnect, "连不上百度服务器")]
    [InlineData(CoreWebView2WebErrorStatus.CertificateIsInvalid, "安全证书有问题，可能被代理或安全软件拦截")]
    [InlineData(CoreWebView2WebErrorStatus.Unknown, "网络错误")]
    public void NetworkFailuresAreNotBlamedOnTheRuntime(CoreWebView2WebErrorStatus status, string reason)
    {
        Assert.Equal(reason, BaiduLoginWindow.DescribeNavigationError(status));
        var message = string.Format(BaiduLoginUiText.NavigationFailed, reason);
        Assert.Contains("网络、代理或安全软件", message);
        Assert.DoesNotContain("安装", message);
    }

    [Fact]
    public void TheLoginWindowShowsTheDownloadButtonOnlyWhenAsked()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "src"))) root = root.Parent;
        Assert.NotNull(root);
        var document = XDocument.Load(Path.Combine(
            root.FullName, "src", "BaiduBatchTransfer.App", "Features", "Accounts", "BaiduLoginWindow.xaml"));
        var x = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");

        var button = Assert.Single(document.Descendants(),
            node => node.Name.LocalName == "Button" && (string?)node.Attribute(x + "Name") == "DownloadWebView2Button");

        Assert.Equal("下载 WebView2", (string?)button.Attribute("Content"));
        Assert.Equal("OnDownloadWebView2Click", (string?)button.Attribute("Click"));
        Assert.Contains("ShowRuntimeDownload", (string?)button.Attribute("Visibility"));
    }

    [Fact]
    public void TheProbeNeverThrows()
    {
        var status = new WebView2RuntimeProbe().Check();

        // 开发机上装着组件；没装时 IsMissing 为 true，两者不会同时出现版本号。
        Assert.True(status.IsMissing ? status.Version is null : true);
    }
}
