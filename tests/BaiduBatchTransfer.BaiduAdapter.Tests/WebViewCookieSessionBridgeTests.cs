using BaiduBatchTransfer.BaiduAdapter.Authentication;

namespace BaiduBatchTransfer.BaiduAdapter.Tests;

public sealed class WebViewCookieSessionBridgeTests
{
    [Fact]
    public void CapturePreservesCookieScopeExpiryAndSecurityMetadata()
    {
        var captureId = Guid.NewGuid();
        var expires = new DateTimeOffset(2026, 8, 21, 8, 0, 0, TimeSpan.Zero);
        var bridge = new WebViewCookieSessionBridge();

        var session = bridge.CreateUnboundSession(
            captureId,
            [
                new("BDUSS", "secret-a", ".baidu.com", "/", expires, true, true),
                new("STOKEN", "secret-b", "pan.baidu.com", "/disk", null, true, false),
            ],
            new DateTimeOffset(2026, 8, 20, 8, 0, 0, TimeSpan.Zero),
            CancellationToken.None);

        Assert.Null(session.LocalAccountId);
        Assert.Null(session.BaiduUserId);
        Assert.Equal(captureId.ToString("D"), session.AccountId);
        Assert.Collection(session.SessionCookies,
            cookie =>
            {
                Assert.Equal(".baidu.com", cookie.Domain);
                Assert.Equal("/", cookie.Path);
                Assert.Equal(expires, cookie.ExpiresUtc);
                Assert.True(cookie.IsSecure);
                Assert.True(cookie.IsHttpOnly);
            },
            cookie =>
            {
                Assert.Equal("pan.baidu.com", cookie.Domain);
                Assert.Equal("/disk", cookie.Path);
                Assert.Null(cookie.ExpiresUtc);
                Assert.True(cookie.IsSecure);
                Assert.False(cookie.IsHttpOnly);
            });
        Assert.DoesNotContain("secret", session.ToString(), StringComparison.Ordinal);
    }
}
