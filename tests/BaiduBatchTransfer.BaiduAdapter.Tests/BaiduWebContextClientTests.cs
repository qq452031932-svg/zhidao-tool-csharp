using System.Net;
using System.Text;
using BaiduBatchTransfer.BaiduAdapter.Http;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.BaiduAdapter.Tests;

public sealed class BaiduWebContextClientTests
{
    [Fact]
    public async Task ReadsObservedUidAndCsrfShapesAndRedactsContext()
    {
        var handler = new RouteHandler(new Dictionary<string, string>
        {
            ["identity"] = "{\"errno\":0,\"result\":{\"uk\":123,\"username\":\"private\"},\"request_id\":1}",
            ["web-context"] = "{\"errno\":0,\"result\":{\"bdstoken\":\"secret-token\"},\"request_id\":2}",
        });
        var transport = new BaiduSameOriginHttpClient(
            BaiduSameOriginRouteContract.ObservedReadOnlyRoutes, handler, TimeSpan.FromSeconds(1));

        var context = await new BaiduWebContextClient(transport).ReadAsync(Session("123"), default);

        Assert.Equal("123", context.UserId);
        Assert.Equal("secret-token", context.CsrfToken);
        Assert.Equal(64, context.RequestContextFingerprint.Length);
        Assert.DoesNotContain("123", context.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", context.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsUidDrift()
    {
        var handler = new RouteHandler(new Dictionary<string, string>
        {
            ["identity"] = "{\"errno\":0,\"result\":{\"uk\":999},\"request_id\":1}",
            ["web-context"] = "{\"errno\":0,\"result\":{\"bdstoken\":\"secret-token\"},\"request_id\":2}",
        });
        var transport = new BaiduSameOriginHttpClient(
            BaiduSameOriginRouteContract.ObservedReadOnlyRoutes, handler, TimeSpan.FromSeconds(1));

        var error = await Assert.ThrowsAsync<BaiduWebContextException>(() =>
            new BaiduWebContextClient(transport).ReadAsync(Session("123"), default));

        Assert.Equal("ACCOUNT_IDENTITY_MISMATCH", error.ErrorCode);
        Assert.DoesNotContain("999", error.Message, StringComparison.Ordinal);
    }

    private static AccountSession Session(string uid) => new(
        Guid.NewGuid(), uid,
        [new SessionCookie("BDUSS", "secret", ".baidu.com", "/", null, true, true)],
        DateTimeOffset.UtcNow);

    private sealed class RouteHandler(IReadOnlyDictionary<string, string> payloads) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var route = request.RequestUri!.Query.Contains("bdstoken", StringComparison.Ordinal)
                ? "web-context"
                : "identity";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payloads[route], Encoding.UTF8, "application/json"),
            });
        }
    }
}
