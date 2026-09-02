using System.Net;
using System.Text;
using System.Text.Json;
using BaiduBatchTransfer.BaiduAdapter.Http;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.BaiduAdapter.Tests;

public sealed class BaiduReadOnlyContractCharacterizerTests
{
    private static readonly BaiduReadOnlyPagedRoutePolicy Policy = new(
        "share-records",
        HttpMethod.Get,
        "/share/record",
        new Dictionary<string, BaiduReadOnlyQueryRule>(StringComparer.Ordinal)
        {
            ["page"] = BaiduReadOnlyQueryRule.PositiveInteger(),
            ["num"] = BaiduReadOnlyQueryRule.Fixed("100"),
        },
        MaximumPages: 3);

    [Fact]
    public async Task WalksAllPagesAndPrintsOnlySanitizedShapeAndCounts()
    {
        var handler = new SequenceHandler(
            Json("""{"errno":0,"list":[{"shareid":"private-one","title":"private-title"}],"next":"2"}"""),
            Json("""{"errno":0,"list":[{"shareid":"private-two"}],"next":null}"""));
        var characterizer = new BaiduReadOnlyContractCharacterizer(
            new Dictionary<string, Uri> { ["quota"] = new("https://pan.baidu.com/api/quota") },
            handler);

        var observations = await characterizer.ObservePagesAsync(
            Policy,
            cursor => new Uri($"https://pan.baidu.com/share/record?page={cursor ?? "1"}&num=100"),
            ReadState,
            Session(),
            default);

        Assert.Equal(2, observations.Count);
        Assert.Equal([1, 1], observations.Select(item => item.ItemCount));
        Assert.Equal([true, false], observations.Select(item => item.HasNextPage));
        Assert.Equal([1, 2], observations.Select(item => item.PageNumber));
        Assert.All(observations, item =>
        {
            Assert.Equal("share-records", item.RouteId);
            Assert.Equal("GET", item.Method);
            Assert.Equal(200, item.StatusCode);
            var safe = item.ToString();
            Assert.DoesNotContain("private-one", safe, StringComparison.Ordinal);
            Assert.DoesNotContain("private-two", safe, StringComparison.Ordinal);
            Assert.DoesNotContain("private-title", safe, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-cookie", safe, StringComparison.Ordinal);
            Assert.DoesNotContain("https://", safe, StringComparison.Ordinal);
        });
        Assert.Equal(["1", "2"], handler.PageValues);
    }

    [Theory]
    [InlineData("https://pan.baidu.com/share/record?page=0&num=100")]
    [InlineData("https://pan.baidu.com/share/record?page=999999999999999999999&num=100")]
    [InlineData("https://pan.baidu.com/share/record?page=1&num=100&unknown=1")]
    [InlineData("https://pan.baidu.com/other?page=1&num=100")]
    [InlineData("https://example.com/share/record?page=1&num=100")]
    [InlineData("http://pan.baidu.com/share/record?page=1&num=100")]
    [InlineData("https://pan.baidu.com:444/share/record?page=1&num=100")]
    public async Task RejectsEveryUriOutsideThePagedPolicyBeforeTransport(string uri)
    {
        var handler = new SequenceHandler(Json("{}"));
        var characterizer = Characterizer(handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            characterizer.ObservePagesAsync(
                Policy,
                _ => new Uri(uri),
                ReadState,
                Session(),
                default));

        Assert.StartsWith("READ_ONLY_", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task RejectsWriteMethodPolicyBeforeTransport()
    {
        var handler = new SequenceHandler(Json("{}"));
        var characterizer = Characterizer(handler);
        var writePolicy = Policy with { Method = HttpMethod.Post };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            characterizer.ObservePagesAsync(
                writePolicy,
                _ => new Uri("https://pan.baidu.com/share/record?page=1&num=100"),
                ReadState,
                Session(),
                default));

        Assert.Equal("READ_ONLY_METHOD_REQUIRED", error.Message);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task RejectsRepeatedContinuationWithoutRequestingItAgain()
    {
        var handler = new SequenceHandler(
            Json("""{"list":[],"next":"2"}"""),
            Json("""{"list":[],"next":"2"}"""));
        var characterizer = Characterizer(handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            characterizer.ObservePagesAsync(
                Policy,
                cursor => new Uri($"https://pan.baidu.com/share/record?page={cursor ?? "1"}&num=100"),
                ReadState,
                Session(),
                default));

        Assert.Equal("READ_ONLY_CURSOR_REPEATED", error.Message);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task RejectsPaginationBeyondTheBound()
    {
        var handler = new SequenceHandler(
            Json("""{"list":[],"next":"2"}"""),
            Json("""{"list":[],"next":"3"}"""),
            Json("""{"list":[],"next":"4"}"""));
        var characterizer = Characterizer(handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            characterizer.ObservePagesAsync(
                Policy,
                cursor => new Uri($"https://pan.baidu.com/share/record?page={cursor ?? "1"}&num=100"),
                ReadState,
                Session(),
                default));

        Assert.Equal("READ_ONLY_PAGE_LIMIT_EXCEEDED", error.Message);
        Assert.Equal(3, handler.CallCount);
    }

    [Fact]
    public async Task PagedProbeAllowsAJsonPageLargerThanTheSmallRouteLimit()
    {
        var payload = $$"""{"list":[],"padding":"{{new string('x', 80 * 1024)}}","next":null}""";
        var handler = new SequenceHandler(Json(payload));
        var characterizer = Characterizer(handler);

        var observations = await characterizer.ObservePagesAsync(
            Policy,
            _ => new Uri("https://pan.baidu.com/share/record?page=1&num=100"),
            ReadState,
            Session(),
            default);

        Assert.Single(observations);
    }

    [Fact]
    public async Task PagedProbeRejectsAJsonPageLargerThanOneMiB()
    {
        var payload = $$"""{"list":[],"padding":"{{new string('x', 1024 * 1024)}}","next":null}""";
        var handler = new SequenceHandler(Json(payload));
        var characterizer = Characterizer(handler);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            characterizer.ObservePagesAsync(
                Policy,
                _ => new Uri("https://pan.baidu.com/share/record?page=1&num=100"),
                ReadState,
                Session(),
                default));

        Assert.Equal("READ_ONLY_RESPONSE_TOO_LARGE", error.Message);
    }

    private static BaiduReadOnlyPageState ReadState(JsonElement root)
    {
        var items = root.TryGetProperty("list", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.GetArrayLength()
            : 0;
        var next = root.TryGetProperty("next", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
        return new(items, next);
    }

    private static BaiduReadOnlyContractCharacterizer Characterizer(HttpMessageHandler handler) => new(
        new Dictionary<string, Uri> { ["quota"] = new("https://pan.baidu.com/api/quota") },
        handler);

    private static AccountSession Session() => new(
        Guid.NewGuid(),
        "123",
        [new SessionCookie("BDUSS", "secret-cookie", ".baidu.com", "/", null, true, true)],
        DateTimeOffset.UtcNow);

    private static HttpResponseMessage Json(string payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(payload, Encoding.UTF8, "application/json"),
    };

    private sealed class SequenceHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> responses = new(responses);

        public int CallCount { get; private set; }
        public List<string> PageValues { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            var query = request.RequestUri!.Query.TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split('=', 2))
                .ToDictionary(part => Uri.UnescapeDataString(part[0]), part => Uri.UnescapeDataString(part[1]));
            if (query.TryGetValue("page", out var page)) PageValues.Add(page);
            if (responses.Count == 0) throw new InvalidOperationException("Unexpected request.");
            return Task.FromResult(responses.Dequeue());
        }
    }
}
