using System.Net;
using System.Text;
using BaiduBatchTransfer.BaiduAdapter.Http;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.BaiduAdapter.Tests;

public sealed class BaiduAccountShareAnalyticsClientTests
{
    [Fact]
    public async Task ParsesFrozenInlineStatisticsContract()
    {
        var handler = new ScriptedHandler(Json(Fixture()));
        using var client = new BaiduAccountShareAnalyticsClient(handler);

        var result = await client.ReadPageAsync(Session(), null, default);

        Assert.True(result.IsSuccess);
        var page = Assert.IsType<AccountSharePage>(result.Value);
        Assert.Null(page.NextCursor);
        Assert.Equal(2, page.Items.Count);
        var first = page.Items[0];
        Assert.Equal("10001", first.ShareId);
        Assert.Equal("示例资源甲", first.Title);
        Assert.Equal("https://pan.baidu.com/s/synthetic-a", first.ShareUri.AbsoluteUri);
        Assert.Equal(AccountShareRemoteStatus.Valid, first.Status);
        Assert.Equal(1200, first.Metrics.Single(value => value.Metric == AccountShareMetric.Transfer).HistoricalTotal);
        Assert.Equal(3400, first.Metrics.Single(value => value.Metric == AccountShareMetric.View).HistoricalTotal);
        Assert.Equal(560, first.Metrics.Single(value => value.Metric == AccountShareMetric.Download).HistoricalTotal);
        Assert.Equal(AccountShareRemoteStatus.Invalid, page.Items[1].Status);
    }

    [Fact]
    public async Task TypicalPathTakesPriorityAndUsesItsLastNonEmptyNormalizedSegment()
    {
        var handler = new ScriptedHandler(Json("""
            {"errno":0,"count":1,"nextpage":0,"list":[{
              "shareId":10001,"name":"lower-priority-name","typicalPath":"  /folder//nested\\preferred-title///  ",
              "shortlink":"https://pan.baidu.com/s/synthetic","ctime":1788278400,"status":0
            }]}
            """));
        using var client = new BaiduAccountShareAnalyticsClient(handler);

        var result = await client.ReadPageAsync(Session(), null, default);

        Assert.True(result.IsSuccess);
        Assert.Equal("preferred-title", Assert.Single(result.Value!.Items).Title);
    }

    [Fact]
    public async Task TypicalPathRemovesTrailingBackslashSeparators()
    {
        var handler = new ScriptedHandler(Json("""
            {"errno":0,"count":1,"nextpage":0,"list":[{
              "shareId":10001,"name":"lower-priority-name","typicalPath":"  \\folder\\preferred-title\\\\  ",
              "shortlink":"https://pan.baidu.com/s/synthetic","ctime":1788278400,"status":0
            }]}
            """));
        using var client = new BaiduAccountShareAnalyticsClient(handler);

        var result = await client.ReadPageAsync(Session(), null, default);

        Assert.True(result.IsSuccess);
        Assert.Equal("preferred-title", Assert.Single(result.Value!.Items).Title);
    }

    [Theory]
    [InlineData("{\"ignored\":true}")]
    [InlineData("[\"ignored\"]")]
    [InlineData("123")]
    [InlineData("\" 0 \"")]
    [InlineData("\"/0/\"")]
    [InlineData("\"bad\\u0001title\"")]
    public async Task InvalidTypicalPathFallsBackToValidName(string typicalPathJson)
    {
        var payload = $$"""
            {"errno":0,"count":1,"nextpage":0,"list":[{
              "shareId":10001,"name":"fallback-name","typicalPath":{{typicalPathJson}},
              "shortlink":"https://pan.baidu.com/s/synthetic","ctime":1788278400,"status":0
            }]}
            """;
        var handler = new ScriptedHandler(Json(payload));
        using var client = new BaiduAccountShareAnalyticsClient(handler);

        var result = await client.ReadPageAsync(Session(), null, default);

        Assert.True(result.IsSuccess);
        Assert.Equal("fallback-name", Assert.Single(result.Value!.Items).Title);
    }

    [Theory]
    [InlineData("{\"ignored\":true}")]
    [InlineData("[\"ignored\"]")]
    [InlineData("123")]
    public async Task InvalidNameJsonKindsUseStableShareId(string nameJson)
    {
        var payload = $$"""
            {"errno":0,"count":1,"nextpage":0,"list":[{
              "shareId":10001,"name":{{nameJson}},"typicalPath":null,
              "shortlink":"https://pan.baidu.com/s/synthetic","ctime":1788278400,"status":0
            }]}
            """;
        var handler = new ScriptedHandler(Json(payload));
        using var client = new BaiduAccountShareAnalyticsClient(handler);

        var result = await client.ReadPageAsync(Session(), null, default);

        Assert.True(result.IsSuccess);
        Assert.Equal("10001", Assert.Single(result.Value!.Items).Title);
    }

    [Fact]
    public async Task OversizedTypicalPathFallsBackToValidName()
    {
        var oversized = new string('x', 4097);
        var payload = $$"""
            {"errno":0,"count":1,"nextpage":0,"list":[{
              "shareId":10001,"name":"fallback-name","typicalPath":"{{oversized}}",
              "shortlink":"https://pan.baidu.com/s/synthetic","ctime":1788278400,"status":0
            }]}
            """;
        var handler = new ScriptedHandler(Json(payload));
        using var client = new BaiduAccountShareAnalyticsClient(handler);

        var result = await client.ReadPageAsync(Session(), null, default);

        Assert.True(result.IsSuccess);
        Assert.Equal("fallback-name", Assert.Single(result.Value!.Items).Title);
    }

    [Theory]
    [InlineData("\"00\"", "00")]
    [InlineData("\"42\"", "42")]
    [InlineData("\"/123/\"", "123")]
    public async Task NumericTitlesOtherThanExactZeroRemainValid(string typicalPathJson, string expected)
    {
        var payload = $$"""
            {"errno":0,"count":1,"nextpage":0,"list":[{
              "shareId":10001,"name":"fallback-name","typicalPath":{{typicalPathJson}},
              "shortlink":"https://pan.baidu.com/s/synthetic","ctime":1788278400,"status":0
            }]}
            """;
        var handler = new ScriptedHandler(Json(payload));
        using var client = new BaiduAccountShareAnalyticsClient(handler);

        var result = await client.ReadPageAsync(Session(), null, default);

        Assert.True(result.IsSuccess);
        Assert.Equal(expected, Assert.Single(result.Value!.Items).Title);
    }

    [Fact]
    public async Task InvalidTitleFieldsUseStableShareIdWithoutDiscardingOtherPageItems()
    {
        var handler = new ScriptedHandler(Json("""
            {"errno":0,"count":2,"nextpage":0,"list":[
              {
                "shareId":10001,"name":" 0 ","typicalPath":{"ignored":true},
                "shortlink":"https://pan.baidu.com/s/invalid-title","ctime":1788278400,"status":0
              },
              {
                "shareId":10002,"name":"valid-title","typicalPath":["ignored"],
                "shortlink":"https://pan.baidu.com/s/valid-title","ctime":1788278400,"status":0
              }
            ]}
            """));
        using var client = new BaiduAccountShareAnalyticsClient(handler);

        var result = await client.ReadPageAsync(Session(), null, default);

        Assert.True(result.IsSuccess);
        Assert.Collection(
            result.Value!.Items,
            item => Assert.Equal("10001", item.Title),
            item => Assert.Equal("valid-title", item.Title));
    }

    [Fact]
    public async Task GeneratesOnlyTheFrozenBodyFreeGetRouteAndApplicableCookies()
    {
        var handler = new ScriptedHandler(Json("""{"errno":0,"count":0,"nextpage":0,"list":[]}"""));
        using var client = new BaiduAccountShareAnalyticsClient(handler);
        var session = new AccountSession(
            Guid.NewGuid(),
            "123",
            [
                new SessionCookie("BDUSS", "private-cookie", ".baidu.com", "/", null, true, true),
                new SessionCookie("OTHER", "wrong-path", ".baidu.com", "/other", null, true, true),
                new SessionCookie("EXPIRED", "expired", ".baidu.com", "/", DateTimeOffset.UtcNow.AddDays(-1), true, true),
            ],
            DateTimeOffset.UtcNow);

        var result = await client.ReadPageAsync(session, "2", default);

        Assert.True(result.IsSuccess);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Null(request.Content);
        Assert.Equal("pan.baidu.com", request.Uri.Host);
        Assert.Equal("/share/record", request.Uri.AbsolutePath);
        Assert.Equal("?page=2&desc=1&order=time", request.Uri.Query);
        Assert.Contains("BDUSS=private-cookie", request.Cookie, StringComparison.Ordinal);
        Assert.DoesNotContain("wrong-path", request.Cookie, StringComparison.Ordinal);
        Assert.DoesNotContain("expired", request.Cookie, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1&host=example.com")]
    [InlineData("10001")]
    public async Task RejectsInvalidCursorsBeforeTransport(string cursor)
    {
        var handler = new ScriptedHandler(Json("{}"));
        using var client = new BaiduAccountShareAnalyticsClient(handler);

        var result = await client.ReadPageAsync(Session(), cursor, default);

        Assert.False(result.IsSuccess);
        Assert.Equal(BaiduErrorKind.UnknownResponse, result.ErrorKind);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RetriesOneTransientReadAndNoMore()
    {
        var handler = new ScriptedHandler(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            Json("""{"errno":0,"count":0,"nextpage":0,"list":[]}"""));
        using var client = new BaiduAccountShareAnalyticsClient(handler);

        var result = await client.ReadPageAsync(Session(), null, default);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ExpiredLoginIsNotRetried()
    {
        var handler = new ScriptedHandler(Json("""{"errno":-6}"""));
        using var client = new BaiduAccountShareAnalyticsClient(handler);

        var result = await client.ReadPageAsync(Session(), null, default);

        Assert.False(result.IsSuccess);
        Assert.Equal(BaiduErrorKind.ExpiredLogin, result.ErrorKind);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task MissingOrInvalidIndividualStatisticsBecomeUnavailable()
    {
        var handler = new ScriptedHandler(Json("""
            {"errno":0,"count":1,"nextpage":0,"list":[{
              "shareId":10001,"name":"示例","shortlink":"https://pan.baidu.com/s/synthetic",
              "ctime":1788278400,"status":0,"tCnt":-1,"vCnt":"bad"
            }]}
            """));
        using var client = new BaiduAccountShareAnalyticsClient(handler);

        var result = await client.ReadPageAsync(Session(), null, default);

        Assert.True(result.IsSuccess);
        var metrics = Assert.Single(result.Value!.Items).Metrics;
        Assert.Null(metrics.Single(value => value.Metric == AccountShareMetric.Transfer).HistoricalTotal);
        Assert.Null(metrics.Single(value => value.Metric == AccountShareMetric.View).HistoricalTotal);
        Assert.Null(metrics.Single(value => value.Metric == AccountShareMetric.Download).HistoricalTotal);
    }

    [Fact]
    public async Task DuplicateStableIdentityRejectsTheWholePage()
    {
        var item = """{"shareId":10001,"name":"示例","shortlink":"https://pan.baidu.com/s/synthetic","ctime":1788278400,"status":0,"tCnt":1,"vCnt":2,"dCnt":3}""";
        var handler = new ScriptedHandler(Json($$"""{"errno":0,"count":2,"nextpage":0,"list":[{{item}},{{item}}]}"""));
        using var client = new BaiduAccountShareAnalyticsClient(handler);

        var result = await client.ReadPageAsync(Session(), null, default);

        Assert.False(result.IsSuccess);
        Assert.Equal(BaiduErrorKind.UnknownResponse, result.ErrorKind);
    }

    [Fact]
    public async Task OversizedResponseIsRejected()
    {
        var handler = new ScriptedHandler(Json(new string('x', 1024 * 1024 + 1)));
        using var client = new BaiduAccountShareAnalyticsClient(handler);

        var result = await client.ReadPageAsync(Session(), null, default);

        Assert.False(result.IsSuccess);
        Assert.Equal(BaiduErrorKind.UnknownResponse, result.ErrorKind);
    }

    [Fact]
    public async Task RejectedPageReportsPageNumberAndErrnoWithoutCookie()
    {
        var reports = new List<string>();
        var handler = new ScriptedHandler(Json("""{"errno":9019,"list":[]}"""));
        using var client = new BaiduAccountShareAnalyticsClient(handler, reportFailure: reports.Add);

        var result = await client.ReadPageAsync(Session(), "7", default);

        Assert.False(result.IsSuccess);
        var report = Assert.Single(reports);
        Assert.Contains("第 7 页", report);
        Assert.Contains("errno=9019", report);
        Assert.DoesNotContain("private-cookie", report);
    }

    [Fact]
    public async Task InvalidItemReportsWhichFieldWithoutItsContent()
    {
        var reports = new List<string>();
        var handler = new ScriptedHandler(Json("""
            {"errno":0,"count":2,"nextpage":0,"list":[
              {"shareId":10001,"shortlink":"https://pan.baidu.com/s/synthetic-a","ctime":1788278400,"status":0},
              {"shareId":10002,"shortlink":"http://example.invalid/secret-path","ctime":1788278400,"status":0}
            ]}
            """));
        using var client = new BaiduAccountShareAnalyticsClient(handler, reportFailure: reports.Add);

        var result = await client.ReadPageAsync(Session(), null, default);

        Assert.False(result.IsSuccess);
        var report = Assert.Single(reports);
        Assert.Contains("第 1 页", report);
        Assert.Contains("本页第 2 条", report);
        Assert.Contains("shortlink", report);
        Assert.DoesNotContain("secret-path", report);
    }

    [Fact]
    public async Task SuccessfulPageReportsNothing()
    {
        var reports = new List<string>();
        var handler = new ScriptedHandler(Json(Fixture()));
        using var client = new BaiduAccountShareAnalyticsClient(handler, reportFailure: reports.Add);

        var result = await client.ReadPageAsync(Session(), null, default);

        Assert.True(result.IsSuccess);
        Assert.Empty(reports);
    }

    private static string Fixture() => File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "ShareAnalytics", "share-page.synthetic.json"));

    [Fact]
    public async Task UnansweredRequestTimesOutAsTransientAfterOneRetry()
    {
        var handler = new HangingHandler();
        using var client = new BaiduAccountShareAnalyticsClient(handler, TimeSpan.FromMilliseconds(50));

        var result = await client.ReadPageAsync(Session(), null, default).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(result.IsSuccess);
        Assert.Equal(BaiduErrorKind.TransientService, result.ErrorKind);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task CallerCancellationIsNotReportedAsTimeout()
    {
        using var client = new BaiduAccountShareAnalyticsClient(new HangingHandler(), TimeSpan.FromMinutes(5));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.ReadPageAsync(Session(), null, cancel.Token).WaitAsync(TimeSpan.FromSeconds(10)));
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private static AccountSession Session() => new(
        Guid.NewGuid(),
        "123",
        [new SessionCookie("BDUSS", "private-cookie", ".baidu.com", "/", null, true, true)],
        DateTimeOffset.UtcNow);

    private static HttpResponseMessage Json(string payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(payload, Encoding.UTF8, "application/json"),
    };

    private sealed class ScriptedHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> responses = new(responses);
        public List<CapturedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new(
                request.Method,
                request.RequestUri!,
                request.Content,
                request.Headers.TryGetValues("Cookie", out var values)
                    ? string.Join(";", values)
                    : string.Empty));
            if (responses.Count == 0) throw new HttpRequestException("synthetic network failure");
            return Task.FromResult(responses.Dequeue());
        }
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        Uri Uri,
        HttpContent? Content,
        string Cookie);
}
