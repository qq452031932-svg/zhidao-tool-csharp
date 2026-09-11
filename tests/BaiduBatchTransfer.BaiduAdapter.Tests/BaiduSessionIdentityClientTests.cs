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
            """{"errno":0,"result":{"uk":123456789,"username":"测试账号","avatar_url":"https://himg.bdimg.com/sys/portrait/item/test.jpg"}}""");
        using var client = new BaiduSessionIdentityClient(handler);

        var result = await client.ValidateAsync(Session(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("123456789", result.Value!.UserId);
        Assert.Equal("测试账号", result.Value.DisplayName);
        Assert.Equal(new Uri("https://himg.bdimg.com/sys/portrait/item/test.jpg"), result.Value.AvatarUri);
        Assert.Equal("https", handler.RequestUri!.Scheme);
        Assert.Equal("pan.baidu.com", handler.RequestUri.IdnHost);
        Assert.Equal("/api/gettemplatevariable", handler.RequestUri.AbsolutePath);
        Assert.Contains("avatar_url", Uri.UnescapeDataString(handler.RequestUri.Query), StringComparison.Ordinal);
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

    [Fact]
    public async Task UnsafeAvatarUrlIsIgnoredWithoutRejectingTheBoundIdentity()
    {
        using var client = new BaiduSessionIdentityClient(new RecordingHandler(
            HttpStatusCode.OK,
            """{"errno":0,"result":{"uk":"42","username":"账号甲","avatar_url":"http://example.com/avatar.jpg"}}"""));

        var result = await client.ValidateAsync(Session(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value!.AvatarUri);
    }

    [Fact]
    public async Task MissingTemplateAvatarFallsBackToOfficialUserInfoForTheSameAccount()
    {
        var handler = new SequenceHandler(
            (HttpStatusCode.OK, """{"errno":0,"result":{"uk":"42","username":"账号甲"}}"""),
            (HttpStatusCode.OK, """{"errno":0,"uk":"42","avatar_url":"https://himg.bdimg.com/sys/portrait/item/test.jpg"}"""));
        using var client = new BaiduSessionIdentityClient(handler);

        var result = await client.ValidateAsync(Session(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(new Uri("https://himg.bdimg.com/sys/portrait/item/test.jpg"), result.Value!.AvatarUri);
        Assert.Equal(2, handler.RequestUris.Count);
        Assert.Equal("/rest/2.0/xpan/nas", handler.RequestUris[1].AbsolutePath);
        Assert.Contains("method=uinfo", handler.RequestUris[1].Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StableCapacityIsReadFromBoundedOfficialSameOriginResponse()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK,
            """{"errno":0,"total":2199023255552,"used":549755813888}""");
        using var client = new BaiduSessionIdentityClient(handler);
        var adapter = new LiveBaiduNetdiskAdapter(client);

        var result = await adapter.GetCapacityAsync(Session(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2199023255552, result.Value!.TotalBytes);
        Assert.Equal(549755813888, result.Value.UsedBytes);
        Assert.Equal("https", handler.RequestUri!.Scheme);
        Assert.Equal("pan.baidu.com", handler.RequestUri.IdnHost);
        Assert.Equal("/api/quota", handler.RequestUri.AbsolutePath);
        Assert.Contains("BDUSS=secret-a", handler.CookieHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("STOKEN=secret-b", handler.CookieHeader, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"errno\":0,\"used\":1}")]
    [InlineData("{\"errno\":0,\"total\":2}")]
    [InlineData("{\"errno\":0,\"total\":-1,\"used\":0}")]
    [InlineData("{\"errno\":0,\"total\":1,\"used\":2}")]
    [InlineData("{\"errno\":-6,\"total\":2,\"used\":1}")]
    [InlineData("not-json")]
    public async Task MissingOrInvalidCapacityNeverSucceeds(string body)
    {
        using var client = new BaiduSessionIdentityClient(new RecordingHandler(HttpStatusCode.OK, body));
        var adapter = new LiveBaiduNetdiskAdapter(client);

        var result = await adapter.GetCapacityAsync(Session(), CancellationToken.None);

        Assert.False(result.IsSuccess);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, BaiduErrorKind.ExpiredLogin)]
    [InlineData(HttpStatusCode.Forbidden, BaiduErrorKind.VerificationRequired)]
    [InlineData(HttpStatusCode.TooManyRequests, BaiduErrorKind.TransientService)]
    [InlineData(HttpStatusCode.InternalServerError, BaiduErrorKind.TransientService)]
    public async Task CapacityHttpFailuresMapToTypedSafeErrors(HttpStatusCode status, BaiduErrorKind expected)
    {
        using var client = new BaiduSessionIdentityClient(new RecordingHandler(status, "secret-response"));
        var adapter = new LiveBaiduNetdiskAdapter(client);

        var result = await adapter.GetCapacityAsync(Session(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(expected, result.ErrorKind);
        Assert.DoesNotContain("secret", result.SafeMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CapacityBusinessLoginFailureMapsToExpiredLogin()
    {
        using var client = new BaiduSessionIdentityClient(new RecordingHandler(
            HttpStatusCode.OK,
            """{"errno":-6,"total":2,"used":1}"""));
        var adapter = new LiveBaiduNetdiskAdapter(client);

        var result = await adapter.GetCapacityAsync(Session(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(BaiduErrorKind.ExpiredLogin, result.ErrorKind);
    }

    [Fact]
    public async Task OversizedCapacityResponseIsRejected()
    {
        using var client = new BaiduSessionIdentityClient(new RecordingHandler(
            HttpStatusCode.OK,
            new string('x', 64 * 1024 + 1)));
        var adapter = new LiveBaiduNetdiskAdapter(client);

        var result = await adapter.GetCapacityAsync(Session(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(BaiduErrorKind.UnknownResponse, result.ErrorKind);
    }

    [Fact]
    public async Task CapacityReadHonorsCallerCancellation()
    {
        using var client = new BaiduSessionIdentityClient(new RecordingHandler(
            HttpStatusCode.OK,
            """{"errno":0,"total":2,"used":1}"""));
        var adapter = new LiveBaiduNetdiskAdapter(client);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            adapter.GetCapacityAsync(Session(), cancellation.Token));
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
            cancellationToken.ThrowIfCancellationRequested();
            RequestUri = request.RequestUri;
            CookieHeader = request.Headers.TryGetValues("Cookie", out var values) ? string.Join(";", values) : string.Empty;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class SequenceHandler(params (HttpStatusCode Status, string Body)[] responses) : HttpMessageHandler
    {
        private int _responseIndex;

        public List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestUris.Add(request.RequestUri!);
            var response = responses[Math.Min(_responseIndex++, responses.Length - 1)];
            return Task.FromResult(new HttpResponseMessage(response.Status)
            {
                Content = new StringContent(response.Body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
