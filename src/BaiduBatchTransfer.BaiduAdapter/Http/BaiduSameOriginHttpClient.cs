using System.Collections.ObjectModel;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.BaiduAdapter.Http;

public enum BaiduSameOriginRoute
{
    Identity,
    WebContext,
    Quota,
}

public sealed record BaiduSameOriginRouteContract(HttpMethod Method, Uri Uri)
{
    public static IReadOnlyDictionary<BaiduSameOriginRoute, BaiduSameOriginRouteContract>
        ObservedReadOnlyRoutes { get; } = new ReadOnlyDictionary<BaiduSameOriginRoute, BaiduSameOriginRouteContract>(
            new Dictionary<BaiduSameOriginRoute, BaiduSameOriginRouteContract>
            {
                [BaiduSameOriginRoute.Identity] = new(
                    HttpMethod.Get,
                    new Uri("https://pan.baidu.com/api/gettemplatevariable?fields=%5B%22uk%22%2C%22username%22%2C%22avatar_url%22%2C%22photo_url%22%5D")),
                [BaiduSameOriginRoute.WebContext] = new(
                    HttpMethod.Get,
                    new Uri("https://pan.baidu.com/api/gettemplatevariable?fields=%5B%22bdstoken%22%5D")),
                [BaiduSameOriginRoute.Quota] = new(
                    HttpMethod.Get,
                    new Uri("https://pan.baidu.com/api/quota")),
            });
}

public sealed record BaiduSameOriginJsonResponse(int StatusCode, JsonElement Payload)
{
    public override string ToString() =>
        $"BaiduSameOriginJsonResponse {{ StatusCode = {StatusCode}, Payload = [REDACTED] }}";
}

public sealed class BaiduSameOriginRequestException(string errorCode, string safeMessage)
    : InvalidOperationException(safeMessage)
{
    public string ErrorCode { get; } = errorCode;
    public string SafeMessage { get; } = safeMessage;
}

public sealed class BaiduSameOriginHttpClient
{
    private const int MaximumResponseBytes = 64 * 1024;
    private readonly IReadOnlyDictionary<BaiduSameOriginRoute, BaiduSameOriginRouteContract> routes;
    private readonly HttpMessageInvoker http;
    private readonly TimeSpan requestTimeout;

    public BaiduSameOriginHttpClient(
        IReadOnlyDictionary<BaiduSameOriginRoute, BaiduSameOriginRouteContract> routes,
        HttpMessageHandler handler,
        TimeSpan? requestTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(handler);
        if (routes.Count == 0) throw new ArgumentException("At least one fixed route is required.", nameof(routes));
        foreach (var contract in routes.Values)
        {
            ArgumentNullException.ThrowIfNull(contract);
            ValidateUri(contract.Uri);
            if (contract.Method != HttpMethod.Get && contract.Method != HttpMethod.Head)
                throw new ArgumentException("The observed route matrix currently contains read-only routes only.", nameof(routes));
        }
        var timeout = requestTimeout ?? TimeSpan.FromSeconds(30);
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        if (handler is HttpClientHandler concrete)
        {
            concrete.AllowAutoRedirect = false;
            concrete.UseCookies = false;
            concrete.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
        }
        this.routes = new ReadOnlyDictionary<BaiduSameOriginRoute, BaiduSameOriginRouteContract>(
            routes.ToDictionary(pair => pair.Key, pair => pair.Value));
        http = new HttpMessageInvoker(handler, disposeHandler: false);
        this.requestTimeout = timeout;
    }

    public Task<BaiduSameOriginJsonResponse> SendJsonAsync(
        AccountSession session,
        BaiduSameOriginRoute route,
        CancellationToken cancellationToken = default)
    {
        if (!routes.TryGetValue(route, out var contract))
            throw new ArgumentException("The route is not present in the frozen matrix.", nameof(route));
        return SendJsonAsync(session, contract.Method, contract.Uri, null, cancellationToken);
    }

    public async Task<BaiduSameOriginJsonResponse> SendJsonAsync(
        AccountSession session,
        HttpMethod method,
        Uri uri,
        HttpContent? content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(uri);
        var allowed = routes.Values.Any(contract =>
            contract.Method == method && contract.Uri == uri);
        if (!allowed)
            throw new ArgumentException("The request is outside the frozen same-origin route matrix.", nameof(uri));
        if (content is not null)
            throw new ArgumentException("The observed read-only routes do not accept request bodies.", nameof(content));

        var cookie = BuildCookieHeader(session.SessionCookies, uri, DateTimeOffset.UtcNow);
        if (cookie.Length == 0)
            throw new BaiduSameOriginRequestException(
                "SESSION_UNAVAILABLE", "The selected account session is unavailable.");
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var deadline = new CancellationTokenSource(requestTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, deadline.Token);
        try
        {
            using var response = await http.SendAsync(request, linked.Token).ConfigureAwait(false);
            var payload = await ReadBoundedAsync(response.Content, linked.Token).ConfigureAwait(false);
            try
            {
                using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 64 });
                return new((int)response.StatusCode, document.RootElement.Clone());
            }
            catch (JsonException)
            {
                throw new BaiduSameOriginRequestException(
                    "INVALID_JSON", "The remote service returned an invalid response.");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BaiduSameOriginRequestException(
                "REQUEST_TIMEOUT", "The remote request timed out.");
        }
        catch (InvalidDataException)
        {
            throw new BaiduSameOriginRequestException(
                "RESPONSE_TOO_LARGE", "The remote response exceeded the safe size limit.");
        }
        catch (HttpRequestException)
        {
            throw new BaiduSameOriginRequestException(
                "NETWORK_ERROR", "The remote request could not be completed.");
        }
    }

    private static void ValidateUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort ||
            !string.Equals(uri.Host, "pan.baidu.com", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Only exact default-port pan.baidu.com HTTPS routes are allowed.");
    }

    private static string BuildCookieHeader(
        IEnumerable<SessionCookie> cookies,
        Uri uri,
        DateTimeOffset now) =>
        string.Join("; ", cookies
            .Where(cookie => cookie.ExpiresUtc is null || cookie.ExpiresUtc > now)
            .Where(cookie => !cookie.IsSecure || uri.Scheme == Uri.UriSchemeHttps)
            .Where(cookie => DomainMatches(uri.Host, cookie.Domain))
            .Where(cookie => uri.AbsolutePath.StartsWith(cookie.Path, StringComparison.Ordinal))
            .Select(cookie => $"{cookie.Name}={cookie.Value}"));

    private static bool DomainMatches(string host, string domain)
    {
        var normalized = domain.TrimStart('.');
        return string.Equals(host, normalized, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith('.' + normalized, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumResponseBytes)
            throw new InvalidDataException("Response is too large.");
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > MaximumResponseBytes)
                throw new InvalidDataException("Response is too large.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
