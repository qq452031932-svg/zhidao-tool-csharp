using System.Net;
using System.Net.Http;
using BaiduBatchTransfer.App.Features.Collect;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class ShareLinkCheckerTests
{
    private const string Link = "https://pan.baidu.com/s/1AbcdEfgh_ij-KL";
    private const string ListOk = "{\"errno\":0,\"title\":\"/资源\",\"list\":[{\"fs_id\":\"1\",\"server_filename\":\"资源\",\"isdir\":\"1\"}]}";

    [Fact]
    public async Task ShareWithoutPasswordIsAccessibleOnlyAfterTheFileListIsRead()
    {
        var server = new FakeBaidu { Info = "{\"errno\":0,\"shareid\":1}", List = ListOk };
        var result = await server.CheckAsync(Link, "");
        Assert.Equal(CollectLinkStatus.Accessible, result.Status);
        Assert.Equal(["shorturlinfo", "list"], server.Calls);
        Assert.Contains("shorturl=1AbcdEfgh_ij-KL", server.Requests[0].RequestUri!.Query);
        Assert.Contains("shorturl=AbcdEfgh_ij-KL&", server.Requests[1].RequestUri!.Query);
        Assert.False(server.Requests[1].Headers.Contains("Cookie"));
    }

    [Fact]
    public async Task CorrectPasswordIsVerifiedAndTheReturnedKeyIsUsedForTheList()
    {
        var server = new FakeBaidu
        {
            Info = "{\"errno\":-9,\"show_msg\":\"提取码验证失败，请重试\"}",
            Verify = _ => "{\"errno\":0,\"randsk\":\"key%2Fvalue%3D\"}",
            List = ListOk,
        };
        var result = await server.CheckAsync(Link, "ab12");
        Assert.Equal(CollectLinkStatus.Accessible, result.Status);
        Assert.Equal(["shorturlinfo", "verify", "list"], server.Calls);
        Assert.Contains("surl=AbcdEfgh_ij-KL&", server.Requests[1].RequestUri!.Query);
        Assert.Contains("pwd=ab12", server.VerifyBodies.Single());
        Assert.Equal("BDCLND=key%2Fvalue%3D", Assert.Single(server.Requests[2].Headers.GetValues("Cookie")));
    }

    [Fact]
    public async Task PasswordInTheLinkIsUsedWhenTheRecordHasNone()
    {
        var server = new FakeBaidu
        {
            Info = "{\"errno\":-9}",
            Verify = _ => "{\"errno\":0,\"randsk\":\"k\"}",
            List = ListOk,
        };
        Assert.Equal(CollectLinkStatus.Accessible, (await server.CheckAsync(Link + "?pwd=xy34", "")).Status);
        Assert.Contains("pwd=xy34", server.VerifyBodies.Single());
    }

    [Fact]
    public async Task WrongPasswordIsAPasswordProblemAndBothConflictingCodesAreTried()
    {
        var server = new FakeBaidu { Info = "{\"errno\":-9}", Verify = _ => "{\"errno\":-9,\"err_msg\":\"\"}" };
        var result = await server.CheckAsync(Link + "?pwd=zz99", "ab12");
        Assert.Equal(CollectLinkStatus.PasswordProblem, result.Status);
        Assert.Equal(2, server.VerifyBodies.Count);
        Assert.Contains("pwd=ab12", server.VerifyBodies[0]);
        Assert.Contains("pwd=zz99", server.VerifyBodies[1]);
        Assert.DoesNotContain("ab12", result.Reason);
        Assert.DoesNotContain("zz99", result.Reason);
    }

    [Fact]
    public async Task MissingPasswordIsAPasswordProblemWithoutSendingAnyCode()
    {
        var server = new FakeBaidu { Info = "{\"errno\":-9}" };
        var result = await server.CheckAsync(Link, " ");
        Assert.Equal(CollectLinkStatus.PasswordProblem, result.Status);
        Assert.Contains("没有提取码", result.Reason);
        Assert.Equal(["shorturlinfo"], server.Calls);
    }

    [Theory]
    // 2026-10-15 实测：编造的短链返回 errno 140「啊哦，链接出错了」。
    [InlineData("{\"errno\":140,\"show_msg\":\"啊哦，链接出错了\"}")]
    [InlineData("{\"errno\":140}")]
    [InlineData("{\"errno\":31066,\"show_msg\":\"啊哦，你来晚了，分享的文件已经被取消了\"}")]
    [InlineData("{\"errno\":31067,\"show_msg\":\"分享已过期\"}")]
    public async Task ExplicitBaiduEvidenceMarksTheShareInvalid(string info)
    {
        var server = new FakeBaidu { Info = info };
        var result = await server.CheckAsync(Link, "ab12");
        Assert.Equal(CollectLinkStatus.Invalid, result.Status);
        Assert.Equal(["shorturlinfo"], server.Calls);
    }

    [Theory]
    [InlineData("{\"errno\":-62}", true)]
    [InlineData("{\"errno\":9019}", true)]
    [InlineData("{\"errno\":2,\"show_msg\":\"参数错误\"}", false)]
    [InlineData("{\"errno\":99999}", false)]
    [InlineData("{\"shareid\":1}", false)]
    [InlineData("[]", true)]
    [InlineData("<html>安全验证</html>", true)]
    public async Task UnrecognizedOrVerificationResponsesAreNeverInvalid(string info, bool looksLimited)
    {
        var server = new FakeBaidu { Info = info };
        var result = await server.CheckAsync(Link, "ab12");
        Assert.Equal(CollectLinkStatus.Undetermined, result.Status);
        Assert.Equal(looksLimited, result.LooksRateLimited);
    }

    [Fact]
    public async Task VerificationDuringPasswordCheckIsUndetermined()
    {
        var server = new FakeBaidu { Info = "{\"errno\":-9}", Verify = _ => "{\"errno\":-62}" };
        var result = await server.CheckAsync(Link, "ab12");
        Assert.Equal(CollectLinkStatus.Undetermined, result.Status);
        Assert.True(result.LooksRateLimited);
    }

    [Theory]
    [InlineData("{\"errno\":0,\"list\":[]}")]
    [InlineData("{\"errno\":0}")]
    [InlineData("{\"errno\":-7}")]
    public async Task UnusableFileListsAreUndetermined(string list)
    {
        var server = new FakeBaidu { Info = "{\"errno\":0}", List = list };
        Assert.Equal(CollectLinkStatus.Undetermined, (await server.CheckAsync(Link, "")).Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.Found, true)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    public async Task HttpFailuresAreUndetermined(HttpStatusCode status, bool looksLimited)
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(
            new HttpResponseMessage(status) { Content = new StringContent("{\"errno\":140}") })));
        var result = await new ShareLinkChecker(client).CheckAsync(Link, "ab12", CancellationToken.None);
        Assert.Equal(CollectLinkStatus.Undetermined, result.Status);
        Assert.Equal(looksLimited, result.LooksRateLimited);
    }

    [Fact]
    public async Task NetworkErrorsAndTimeoutsAreUndeterminedButUserCancellationPropagates()
    {
        foreach (var error in new Exception[] { new HttpRequestException("down"), new TaskCanceledException("timeout") })
        {
            using var client = new HttpClient(new Handler((_, _) => throw error));
            var result = await new ShareLinkChecker(client).CheckAsync(Link, "ab12", CancellationToken.None);
            Assert.Equal(CollectLinkStatus.Undetermined, result.Status);
        }

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var blocking = new HttpClient(new Handler((_, token) => Task.FromCanceled<HttpResponseMessage>(token)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ShareLinkChecker(blocking).CheckAsync(Link, "ab12", cancelled.Token));
    }

    [Theory]
    [InlineData("https://example.com/s/1AbcdEfgh")]
    [InlineData("not a link")]
    [InlineData("")]
    [InlineData("file:///C:/Windows/notepad.exe")]
    public async Task NonBaiduLinksAreNotRequested(string link)
    {
        var server = new FakeBaidu();
        var result = await server.CheckAsync(link, "ab12");
        Assert.Equal(CollectLinkStatus.Undetermined, result.Status);
        Assert.Empty(server.Calls);
    }

    [Theory]
    [InlineData("https://pan.baidu.com/s/1AbcdEfgh", "1AbcdEfgh", "")]
    [InlineData("https://pan.baidu.com/s/1AbcdEfgh?pwd=ab12", "1AbcdEfgh", "ab12")]
    [InlineData("http://pan.baidu.com/s/1AbcdEfgh/", "1AbcdEfgh", "")]
    [InlineData("https://pan.baidu.com/share/init?surl=AbcdEfgh&pwd=x1y2", "1AbcdEfgh", "x1y2")]
    [InlineData("https://yun.baidu.com/s/1AbcdEfgh", "1AbcdEfgh", "")]
    [InlineData("https://pan.baidu.com/s/1AbcdEfgh?pwd=a&pwd=b", "1AbcdEfgh", "")]
    public void RecognizesBaiduShareLinkForms(string link, string expectedShortUrl, string expectedPwd)
    {
        Assert.True(BaiduShareLink.TryParse(link, out var shortUrl, out var pwd));
        Assert.Equal(expectedShortUrl, shortUrl);
        Assert.Equal(expectedPwd, pwd);
    }

    [Theory]
    [InlineData("https://pan.baidu.com/s/1AbcdEfgh", "ab12", "https://pan.baidu.com/s/1AbcdEfgh?pwd=ab12", false)]
    [InlineData("https://pan.baidu.com/s/1AbcdEfgh?pwd=ab12", "ab12", "https://pan.baidu.com/s/1AbcdEfgh?pwd=ab12", false)]
    [InlineData("https://pan.baidu.com/s/1AbcdEfgh?from=zd", "ab12", "https://pan.baidu.com/s/1AbcdEfgh?from=zd&pwd=ab12", false)]
    [InlineData("https://pan.baidu.com/s/1AbcdEfgh?pwd=zz99", "ab12", "https://pan.baidu.com/s/1AbcdEfgh?pwd=zz99", true)]
    [InlineData("https://pan.baidu.com/s/1AbcdEfgh", "", "https://pan.baidu.com/s/1AbcdEfgh", false)]
    [InlineData("http://pan.baidu.com/s/1AbcdEfgh", "ab12", "https://pan.baidu.com/s/1AbcdEfgh?pwd=ab12", false)]
    [InlineData("https://pan.baidu.com/share/init?surl=AbcdEfgh", "ab12", "https://pan.baidu.com/s/1AbcdEfgh?pwd=ab12", false)]
    [InlineData("https://pan.baidu.com/s/1AbcdEfgh", "a b&c", "https://pan.baidu.com/s/1AbcdEfgh", false)]
    public void OpenUriAddsTheRecordPasswordWithoutOverwriting(string link, string pwd, string expected, bool conflict)
    {
        var uri = BaiduShareLink.BuildOpenUri(link, pwd, out var hasConflict);
        Assert.Equal(expected, uri!.AbsoluteUri);
        Assert.Equal(conflict, hasConflict);
        Assert.True(BaiduBatchTransfer.App.Features.ShareAnalytics.SystemShareLinkLauncher.IsAllowed(uri));
    }

    [Theory]
    [InlineData("https://evil.example/s/1AbcdEfgh")]
    [InlineData("https://pan.baidu.com.evil.example/s/1AbcdEfgh")]
    [InlineData("https://pan.baidu.com/disk/home")]
    [InlineData("cmd.exe /c calc")]
    public void OpenUriRejectsAnythingElse(string link) =>
        Assert.Null(BaiduShareLink.BuildOpenUri(link, "ab12", out _));

    private sealed class FakeBaidu
    {
        public string Info { get; init; } = "{\"errno\":0}";
        public Func<string, string> Verify { get; init; } = _ => "{\"errno\":0,\"randsk\":\"k\"}";
        public string List { get; init; } = ListOk;
        public List<string> Calls { get; } = [];
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> VerifyBodies { get; } = [];

        public async Task<ShareLinkCheckResult> CheckAsync(string link, string pwd)
        {
            using var client = new HttpClient(new Handler(async (request, token) =>
            {
                Assert.Equal("pan.baidu.com", request.RequestUri!.Host);
                Assert.Equal(Uri.UriSchemeHttps, request.RequestUri.Scheme);
                Requests.Add(request);
                var path = request.RequestUri.AbsolutePath;
                string body;
                if (path == "/api/shorturlinfo") { Calls.Add("shorturlinfo"); body = Info; }
                else if (path == "/share/verify")
                {
                    Calls.Add("verify");
                    Assert.Equal(HttpMethod.Post, request.Method);
                    var form = await request.Content!.ReadAsStringAsync(token);
                    VerifyBodies.Add(form);
                    body = Verify(form);
                }
                else if (path == "/share/list") { Calls.Add("list"); body = List; }
                else throw new InvalidOperationException($"unexpected {path}");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
            }));
            return await new ShareLinkChecker(client).CheckAsync(link, pwd, CancellationToken.None);
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
