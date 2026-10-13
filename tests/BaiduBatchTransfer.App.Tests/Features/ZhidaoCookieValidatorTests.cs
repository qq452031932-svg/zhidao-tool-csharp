using System.Net;
using System.Net.Http;
using BaiduBatchTransfer.App.Features.Collect;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class ZhidaoCookieValidatorTests
{
    [Theory]
    [InlineData("{\"code\":200,\"data\":{\"hasPermission\":true}}", true)]
    [InlineData("{\"code\":200,\"data\":{\"hasPermission\":false}}", true)]
    [InlineData("{\"code\":401,\"message\":\"未登录\"}", false)]
    [InlineData("{\"code\":403}", false)]
    [InlineData("{\"code\":-1}", false)]
    [InlineData("{\"code\":200,\"data\":{\"isLogin\":false,\"hasPermission\":false}}", false)]
    [InlineData("{\"code\":200,\"data\":{}}", false)]
    [InlineData("{\"code\":200,\"data\":null}", false)]
    [InlineData("{}", false)]
    [InlineData("[]", false)]
    [InlineData("<html>请完成安全验证</html>", false)]
    public async Task ValidationRequiresARecognizedAccountResponse(string body, bool expectedValid)
    {
        using var client = new HttpClient(new Handler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://zhidao.baidu.com/qa/api/bulkqa/userinfo", request.RequestUri!.AbsoluteUri);
            Assert.Equal("BDUSS=private-test-cookie", Assert.Single(request.Headers.GetValues("Cookie")));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }));

        var result = await new ZhidaoCookieValidator(client)
            .ValidateAsync("BDUSS=private-test-cookie", CancellationToken.None);

        Assert.Equal(expectedValid, result.IsValid);
        Assert.Equal(expectedValid, result.Message.Contains("验证通过", StringComparison.Ordinal));
        Assert.DoesNotContain("private-test-cookie", result.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task HttpFailuresCannotBecomeSuccessfulValidation(HttpStatusCode status)
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(
            new HttpResponseMessage(status)
            {
                Content = new StringContent("{\"code\":200,\"data\":{\"hasPermission\":true}}"),
            })));
        var result = await new ZhidaoCookieValidator(client).ValidateAsync("BDUSS=test", CancellationToken.None);
        Assert.False(result.IsValid);
        Assert.DoesNotContain("验证通过", result.Message);
    }

    [Fact]
    public async Task NetworkErrorsAndTimeoutsDoNotExposeCookieOrClaimSuccess()
    {
        foreach (var error in new Exception[]
                 { new HttpRequestException("BDUSS=private-test-cookie"), new TaskCanceledException() })
        {
            using var client = new HttpClient(new Handler((_, _) => Task.FromException<HttpResponseMessage>(error)));
            var result = await new ZhidaoCookieValidator(client)
                .ValidateAsync("BDUSS=private-test-cookie", CancellationToken.None);
            Assert.False(result.IsValid);
            Assert.DoesNotContain("private-test-cookie", result.ToString());
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("BDUSS=test\r\nInjected: header")]
    public async Task InvalidInputNeverSendsARequest(string cookie)
    {
        using var client = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("Must not send")));
        Assert.False((await new ZhidaoCookieValidator(client).ValidateAsync(cookie, CancellationToken.None)).IsValid);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
