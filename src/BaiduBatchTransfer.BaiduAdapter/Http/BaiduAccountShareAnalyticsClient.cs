using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.BaiduAdapter.Http;

public sealed class BaiduAccountShareAnalyticsClient : IAccountShareAnalyticsClient, IDisposable
{
    private const int MaximumResponseBytes = 1024 * 1024;
    private const int MaximumPage = 10_000;
    private const int PageSize = 100;
    private readonly HttpMessageInvoker http;

    public BaiduAccountShareAnalyticsClient()
        : this(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            UseCookies = false,
        })
    {
    }

    public BaiduAccountShareAnalyticsClient(HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (handler is HttpClientHandler concrete)
        {
            concrete.AllowAutoRedirect = false;
            concrete.UseCookies = false;
            concrete.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
        }
        http = new HttpMessageInvoker(handler, disposeHandler: true);
    }

    public async Task<BaiduAdapterResult<AccountSharePage>> ReadPageAsync(
        AccountSession session,
        string? cursor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!TryReadPage(cursor, out var pageNumber))
            return Failure(BaiduErrorKind.UnknownResponse, "Invalid page cursor.");

        var uri = BuildPageUri(pageNumber);
        var cookie = BuildCookieHeader(session.SessionCookies, uri, DateTimeOffset.UtcNow);
        if (cookie.Length == 0)
            return Failure(BaiduErrorKind.ExpiredLogin, "No applicable session cookies.");

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException error)
            {
                if (attempt == 0) continue;
                return Failure(BaiduErrorKind.TransientService, error.GetType().Name);
            }

            using (response)
            {
                if (IsTransient(response.StatusCode))
                {
                    if (attempt == 0) continue;
                    return BaiduAdapterResult<AccountSharePage>.Failure(
                        BaiduErrorKind.TransientService,
                        response.StatusCode.ToString(),
                        ReadRetryAfter(response.Headers.RetryAfter));
                }
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                    return Failure(BaiduErrorKind.ExpiredLogin, "Unauthorized.");
                if (response.StatusCode == HttpStatusCode.Forbidden)
                    return Failure(BaiduErrorKind.VerificationRequired, "Forbidden.");
                if (!response.IsSuccessStatusCode)
                    return Failure(BaiduErrorKind.UnknownResponse, response.StatusCode.ToString());

                byte[] payload;
                try
                {
                    payload = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidDataException error)
                {
                    return Failure(BaiduErrorKind.UnknownResponse, error.GetType().Name);
                }

                try
                {
                    using var document = JsonDocument.Parse(
                        payload,
                        new JsonDocumentOptions { MaxDepth = 64 });
                    return ParsePage(document.RootElement, pageNumber);
                }
                catch (JsonException error)
                {
                    return Failure(BaiduErrorKind.UnknownResponse, error.GetType().Name);
                }
                catch (ArgumentException error)
                {
                    return Failure(BaiduErrorKind.UnknownResponse, error.GetType().Name);
                }
                catch (InvalidOperationException error)
                {
                    return Failure(BaiduErrorKind.UnknownResponse, error.GetType().Name);
                }
            }
        }

        return Failure(BaiduErrorKind.TransientService, "Retry exhausted.");
    }

    public void Dispose() => http.Dispose();

    private static BaiduAdapterResult<AccountSharePage> ParsePage(
        JsonElement root,
        int pageNumber)
    {
        if (!TryReadInt64(root, "errno", out var errno))
            return Failure(BaiduErrorKind.UnknownResponse, "Missing errno.");
        if (errno != 0)
        {
            var kind = errno switch
            {
                -6 => BaiduErrorKind.ExpiredLogin,
                -62 or -20 => BaiduErrorKind.VerificationRequired,
                _ => BaiduErrorKind.UnknownResponse,
            };
            return Failure(kind, "Remote read was rejected.");
        }
        if (!root.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array ||
            list.GetArrayLength() > PageSize ||
            !TryReadInt64(root, "count", out var count) || count != list.GetArrayLength() ||
            !TryReadInt64(root, "nextpage", out var nextMarker))
            return Failure(BaiduErrorKind.UnknownResponse, "Invalid page envelope.");

        var items = new List<AccountShareObservation>(list.GetArrayLength());
        foreach (var element in list.EnumerateArray())
        {
            if (!TryParseItem(element, out var item))
                return Failure(BaiduErrorKind.UnknownResponse, "Invalid share identity fields.");
            items.Add(item!);
        }

        string? nextCursor = null;
        if (list.GetArrayLength() == PageSize && nextMarker > 0)
        {
            if (pageNumber >= MaximumPage)
                return Failure(BaiduErrorKind.UnknownResponse, "Page limit exceeded.");
            nextCursor = checked(pageNumber + 1).ToString(CultureInfo.InvariantCulture);
        }

        try
        {
            return BaiduAdapterResult<AccountSharePage>.Success(new(items, nextCursor));
        }
        catch (ArgumentException error)
        {
            return Failure(BaiduErrorKind.UnknownResponse, error.GetType().Name);
        }
    }

    private static bool TryParseItem(JsonElement element, out AccountShareObservation? item)
    {
        item = null;
        if (element.ValueKind != JsonValueKind.Object ||
            !TryReadPositiveIdentifier(element, "shareId", out var shareId))
            return false;

        var title = ReadTitle(element, shareId);
        if (!TryReadShareUri(element, out var shareUri) ||
            !TryReadInt64(element, "ctime", out var seconds) || seconds < 0 ||
            !TryReadInt64(element, "status", out var statusNumber))
            return false;

        DateTimeOffset sharedAtUtc;
        try { sharedAtUtc = DateTimeOffset.FromUnixTimeSeconds(seconds); }
        catch (ArgumentOutOfRangeException) { return false; }

        var status = statusNumber == 0
            ? AccountShareRemoteStatus.Valid
            : AccountShareRemoteStatus.Invalid;
        var metrics = new[]
        {
            new AccountShareMetricObservation(AccountShareMetric.Transfer, ReadOptionalCount(element, "tCnt")),
            new AccountShareMetricObservation(AccountShareMetric.View, ReadOptionalCount(element, "vCnt")),
            new AccountShareMetricObservation(AccountShareMetric.Download, ReadOptionalCount(element, "dCnt")),
        };
        try
        {
            item = new(shareId, title, shareUri!, sharedAtUtc, status, metrics);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryReadPositiveIdentifier(
        JsonElement root,
        string propertyName,
        out string identifier)
    {
        identifier = string.Empty;
        if (!root.TryGetProperty(propertyName, out var value)) return false;
        identifier = value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) && number > 0 =>
                number.ToString(CultureInfo.InvariantCulture),
            JsonValueKind.String => value.GetString()?.Trim() ?? string.Empty,
            _ => string.Empty,
        };
        return identifier.Length is > 0 and <= 256 &&
            identifier.All(character => !char.IsControl(character)) &&
            (value.ValueKind != JsonValueKind.String || identifier.All(char.IsDigit));
    }

    private static string ReadTitle(JsonElement root, string shareId)
    {
        var typicalPath = ReadBoundedString(root, "typicalPath");
        if (typicalPath is not null)
        {
            var segment = typicalPath.Split(
                ['/', '\\'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
            if (IsValidTitleCandidate(segment)) return segment!;
        }

        var name = ReadBoundedString(root, "name");
        return IsValidTitleCandidate(name) ? name! : shareId;
    }

    private static bool IsValidTitleCandidate(string? candidate) =>
        candidate is not null && !string.Equals(candidate.Trim(), "0", StringComparison.Ordinal);

    private static bool TryReadShareUri(JsonElement root, out Uri? uri)
    {
        uri = null;
        var raw = ReadBoundedString(root, "shortlink");
        if (raw is null)
        {
            var shortUrl = ReadBoundedString(root, "shorturl");
            if (shortUrl is null) return false;
            raw = $"https://pan.baidu.com/s/{shortUrl.TrimStart('/')}";
        }
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var candidate) ||
            candidate.Scheme != Uri.UriSchemeHttps || !candidate.IsDefaultPort ||
            !string.Equals(candidate.Host, "pan.baidu.com", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(candidate.UserInfo) || !string.IsNullOrEmpty(candidate.Fragment))
            return false;
        uri = candidate;
        return true;
    }

    private static string? ReadBoundedString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
            return null;
        var text = value.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(text) || text.Length > 4096 || text.Any(char.IsControl)
            ? null
            : text;
    }

    private static long? ReadOptionalCount(JsonElement root, string propertyName) =>
        TryReadInt64(root, propertyName, out var value) && value >= 0 ? value : null;

    private static bool TryReadInt64(JsonElement root, string propertyName, out long value)
    {
        value = 0;
        if (!root.TryGetProperty(propertyName, out var element)) return false;
        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt64(out value),
            JsonValueKind.String => long.TryParse(
                element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value),
            _ => false,
        };
    }

    private static bool TryReadPage(string? cursor, out int pageNumber)
    {
        if (cursor is null)
        {
            pageNumber = 1;
            return true;
        }
        return int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out pageNumber) &&
            pageNumber is >= 1 and <= MaximumPage;
    }

    private static Uri BuildPageUri(int pageNumber) => new(
        $"https://pan.baidu.com/share/record?page={pageNumber.ToString(CultureInfo.InvariantCulture)}&desc=1&order=time");

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.TooManyRequests || (int)statusCode >= 500;

    private static TimeSpan? ReadRetryAfter(RetryConditionHeaderValue? value)
    {
        if (value?.Delta is { } delta && delta >= TimeSpan.Zero) return delta;
        if (value?.Date is { } date)
        {
            var delay = date - DateTimeOffset.UtcNow;
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }
        return null;
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
            throw new InvalidDataException("SHARE_ANALYTICS_RESPONSE_TOO_LARGE");
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0) return buffer.ToArray();
            if (buffer.Length + read > MaximumResponseBytes)
                throw new InvalidDataException("SHARE_ANALYTICS_RESPONSE_TOO_LARGE");
            buffer.Write(chunk, 0, read);
        }
    }

    private static BaiduAdapterResult<AccountSharePage> Failure(
        BaiduErrorKind kind,
        string diagnostic) =>
        BaiduAdapterResult<AccountSharePage>.Failure(kind, diagnostic);
}
