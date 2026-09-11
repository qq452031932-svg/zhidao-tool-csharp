using System.Net;
using System.Text;
using System.Text.Json;
using BaiduBatchTransfer.BaiduAdapter.Http;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.BaiduAdapter.Tests;

public sealed class DirectBackgroundContractFixtureTests
{
    private static readonly Uri AllowedUri = new("https://pan.baidu.com/api/quota");

    [Theory]
    [InlineData("POST", "https://pan.baidu.com/api/quota")]
    [InlineData("PUT", "https://pan.baidu.com/api/quota")]
    [InlineData("GET", "https://pan.baidu.com/api/other")]
    [InlineData("GET", "http://pan.baidu.com/api/quota")]
    [InlineData("GET", "https://example.com/api/quota")]
    public async Task RejectsEveryMutationOrNonAllowlistedRequestBeforeTransport(
        string method,
        string uri)
    {
        var handler = new RecordingHandler();
        using var request = new HttpRequestMessage(new HttpMethod(method), uri);
        var characterizer = new BaiduReadOnlyContractCharacterizer(
            new Dictionary<string, Uri> { ["quota"] = AllowedUri }, handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            characterizer.ObserveAsync("quota", request, Session(), default));

        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task RejectsABodyEvenOnAnAllowedGetBeforeTransport()
    {
        var handler = new RecordingHandler();
        using var request = new HttpRequestMessage(HttpMethod.Get, AllowedUri)
        {
            Content = new StringContent("must-not-send"),
        };
        var characterizer = new BaiduReadOnlyContractCharacterizer(
            new Dictionary<string, Uri> { ["quota"] = AllowedUri }, handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            characterizer.ObserveAsync("quota", request, Session(), default));

        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task EmitsOnlyRouteStatusAndJsonShapeWithoutSensitiveValues()
    {
        const string payload = """
            {"errno":0,"total":123,"used":12,"BDUSS":"secret-cookie","path":"/private/name"}
            """;
        var handler = new RecordingHandler(payload);
        using var request = new HttpRequestMessage(HttpMethod.Get, AllowedUri);
        var characterizer = new BaiduReadOnlyContractCharacterizer(
            new Dictionary<string, Uri> { ["quota"] = AllowedUri }, handler);

        var observation = await characterizer.ObserveAsync("quota", request, Session(), default);

        Assert.Equal("quota", observation.RouteId);
        Assert.Equal("GET", observation.Method);
        Assert.Equal(200, observation.StatusCode);
        Assert.Contains("$.errno:number", observation.JsonShape);
        Assert.Contains("$.total:number", observation.JsonShape);
        Assert.Contains("$.BDUSS:string", observation.JsonShape);
        var safe = observation.ToString();
        Assert.DoesNotContain("secret-cookie", safe);
        Assert.DoesNotContain("/private/name", safe);
        Assert.DoesNotContain(AllowedUri.AbsoluteUri, safe);
        Assert.Equal(1, handler.CallCount);
        Assert.True(handler.SawCookieHeader);
    }

    [Theory]
    [InlineData("identity.real.sanitized.json", "identity", "$.result.uk:number")]
    [InlineData("web-context.real.sanitized.json", "web-context", "$.result.bdstoken:string")]
    [InlineData("quota.real.sanitized.json", "quota", "$.used:number")]
    public void FrozenRealFixtureContainsOnlySanitizedShape(
        string fileName,
        string routeId,
        string requiredShape)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "DirectBackground", fileName);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        Assert.Equal(routeId, root.GetProperty("routeId").GetString());
        Assert.Equal("GET", root.GetProperty("method").GetString());
        Assert.Equal(200, root.GetProperty("statusCode").GetInt32());
        Assert.Contains(
            root.GetProperty("jsonShape").EnumerateArray().Select(item => item.GetString()),
            item => string.Equals(item, requiredShape, StringComparison.Ordinal));
        Assert.Equal("[REDACTED]", root.GetProperty("values").GetString());
        Assert.Equal(5, root.EnumerateObject().Count());
    }

    private static AccountSession Session() => new(
        Guid.NewGuid(),
        "123",
        [new SessionCookie("BDUSS", "secret-cookie", ".baidu.com", "/", null, true, true)],
        DateTimeOffset.UtcNow);

    private sealed class RecordingHandler(string json = "{}") : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public bool SawCookieHeader { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            SawCookieHeader = request.Headers.Contains("Cookie");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
