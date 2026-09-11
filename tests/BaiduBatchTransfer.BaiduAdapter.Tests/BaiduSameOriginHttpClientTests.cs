using System.Net;
using System.Text;
using BaiduBatchTransfer.BaiduAdapter.Http;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.BaiduAdapter.Tests;

public sealed class BaiduSameOriginHttpClientTests
{
    private static readonly Uri Quota = new("https://pan.baidu.com/api/quota");

    [Theory]
    [InlineData("POST", "https://pan.baidu.com/api/quota")]
    [InlineData("GET", "https://pan.baidu.com/api/other")]
    [InlineData("GET", "http://pan.baidu.com/api/quota")]
    [InlineData("GET", "https://example.com/api/quota")]
    public async Task RejectsOutsideFrozenMatrixBeforeTransport(string method, string uri)
    {
        var handler = new RecordingHandler("{}");
        var client = Client(handler);

        await Assert.ThrowsAsync<ArgumentException>(() => client.SendJsonAsync(
            Session(), new HttpMethod(method), new Uri(uri), null, default));

        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task FiltersCookiesAndReturnsBoundedJsonWithoutLeakingValues()
    {
        var handler = new RecordingHandler("{\"errno\":0,\"used\":12}");
        var client = Client(handler);

        var response = await client.SendJsonAsync(Session(), HttpMethod.Get, Quota, null, default);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(0, response.Payload.GetProperty("errno").GetInt32());
        Assert.Equal("BDUSS=secret", handler.Cookie);
        Assert.Equal("application/json", handler.Accept);
        Assert.DoesNotContain("secret", response.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("12", response.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InternalDeadlineReturnsTypedRedactedTimeout()
    {
        var client = new BaiduSameOriginHttpClient(
            new Dictionary<BaiduSameOriginRoute, BaiduSameOriginRouteContract>
            {
                [BaiduSameOriginRoute.Quota] = new(HttpMethod.Get, Quota),
            },
            new NeverCompletingHandler(),
            TimeSpan.FromMilliseconds(20));

        var error = await Assert.ThrowsAsync<BaiduSameOriginRequestException>(() =>
            client.SendJsonAsync(Session(), BaiduSameOriginRoute.Quota, default));

        Assert.Equal("REQUEST_TIMEOUT", error.ErrorCode);
        Assert.DoesNotContain("secret", error.SafeMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BDUSS", error.SafeMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CallerCancellationRemainsCallerCancellation()
    {
        var client = Client(new NeverCompletingHandler());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.SendJsonAsync(Session(), BaiduSameOriginRoute.Quota, cancellation.Token));
    }

    [Fact]
    public async Task RejectsResponseLargerThanSixtyFourKibibytes()
    {
        var client = Client(new RecordingHandler(
            "{\"value\":\"" + new string('x', 70 * 1024) + "\"}"));

        var error = await Assert.ThrowsAsync<BaiduSameOriginRequestException>(() =>
            client.SendJsonAsync(Session(), BaiduSameOriginRoute.Quota, default));

        Assert.Equal("RESPONSE_TOO_LARGE", error.ErrorCode);
    }

    private static BaiduSameOriginHttpClient Client(HttpMessageHandler handler) => new(
        new Dictionary<BaiduSameOriginRoute, BaiduSameOriginRouteContract>
        {
            [BaiduSameOriginRoute.Quota] = new(HttpMethod.Get, Quota),
        }, handler, TimeSpan.FromSeconds(1));

    private static AccountSession Session() => new(
        Guid.NewGuid(), "123",
        [
            new SessionCookie("BDUSS", "secret", ".baidu.com", "/", null, true, true),
            new SessionCookie("OTHER", "wrong-path", ".baidu.com", "/disk", null, true, true),
            new SessionCookie("FOREIGN", "wrong-domain", ".passport.baidu.com", "/", null, true, true),
        ], DateTimeOffset.UtcNow);

    private sealed class RecordingHandler(string json) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public string? Cookie { get; private set; }
        public string? Accept { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Cookie = request.Headers.TryGetValues("Cookie", out var values) ? values.Single() : null;
            Accept = request.Headers.Accept.Single().MediaType;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class NeverCompletingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
