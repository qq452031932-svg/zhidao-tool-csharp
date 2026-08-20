using System.Net;
using System.Text;
using BaiduBatchTransfer.BaiduAdapter.Http;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.BaiduAdapter.Tests;

public sealed class BaiduSessionIdentityClientTests
{
    [Fact]
    public async Task StableIdentityIsReadFromBoundedOfficialSameOriginResponse()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK,
            """{"errno":0,"result":{"uk":123456789,"username":"测试账号"}}""");
        using var client = new BaiduSessionIdentityClient(handler);

        var result = await client.ValidateAsync(Session(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("123456789", result.Value!.UserId);
        Assert.Equal("测试账号", result.Value.DisplayName);
        Assert.Equal("https", handler.RequestUri!.Scheme);
        Assert.Equal("pan.baidu.com", handler.RequestUri.IdnHost);
        Assert.Equal("/api/gettemplatevariable", handler.RequestUri.AbsolutePath);
        Assert.Contains("BDUSS=secret-a", handler.CookieHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("STOKEN=secret-b", handler.CookieHeader, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LiveAdapterUsesTheValidatedIdentityClientForAccountRegistration()
    {
        using var identityClient = new BaiduSessionIdentityClient(new RecordingHandler(
            HttpStatusCode.OK,
            """{"errno":0,"result":{"uk":"42","username":"账号甲"}}"""));
        var adapter = new LiveBaiduNetdiskAdapter(identityClient);

        var result = await adapter.ValidateSessionAsync(Session(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("42", result.Value!.UserId);
        Assert.Equal("账号甲", result.Value.DisplayName);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, BaiduErrorKind.ExpiredLogin)]
    [InlineData(HttpStatusCode.Forbidden, BaiduErrorKind.VerificationRequired)]
    [InlineData(HttpStatusCode.TooManyRequests, BaiduErrorKind.TransientService)]
    public async Task HttpFailuresMapToTypedSafeErrors(HttpStatusCode status, BaiduErrorKind expected)
    {
        using var client = new BaiduSessionIdentityClient(new RecordingHandler(status, "{}"));

        var result = await client.ValidateAsync(Session(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(expected, result.ErrorKind);
        Assert.DoesNotContain("secret", result.SafeMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("{\"errno\":0,\"result\":{\"username\":\"测试\"}}")]
    [InlineData("{\"errno\":0,\"result\":{\"uk\":123}}")]
    [InlineData("{\"errno\":-6,\"result\":{}}")]
    [InlineData("not-json")]
    public async Task MissingOrInvalidIdentityNeverSucceeds(string body)
    {
        using var client = new BaiduSessionIdentityClient(new RecordingHandler(HttpStatusCode.OK, body));

        var result = await client.ValidateAsync(Session(), CancellationToken.None);

        Assert.False(result.IsSuccess);
    }

    private static AccountSession Session() => new(
        Guid.NewGuid().ToString("D"),
        [
            new SessionCookie("BDUSS", "secret-a", ".baidu.com", "/", null, true, true),
            new SessionCookie("STOKEN", "secret-b", "passport.baidu.com", "/", null, true, true),
        ],
        DateTimeOffset.UtcNow);

    private sealed class RecordingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string CookieHeader { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            CookieHeader = request.Headers.TryGetValues("Cookie", out var values) ? string.Join(";", values) : string.Empty;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
