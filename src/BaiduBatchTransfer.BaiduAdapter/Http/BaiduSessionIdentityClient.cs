using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.BaiduAdapter.Http;

public sealed class BaiduSessionIdentityClient : IDisposable
{
    private const int MaximumResponseBytes = 64 * 1024;
    private static readonly Uri IdentityUri = new(
        "https://pan.baidu.com/api/gettemplatevariable?fields=%5B%22uk%22%2C%22username%22%2C%22avatar_url%22%2C%22photo_url%22%5D");
    private static readonly Uri UserInfoUri = new("https://pan.baidu.com/rest/2.0/xpan/nas?method=uinfo");
    private static readonly Uri CapacityUri = new("https://pan.baidu.com/api/quota");
    private readonly HttpMessageInvoker http;

    public BaiduSessionIdentityClient()
        : this(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            UseCookies = false,
        })
    {
    }

    public BaiduSessionIdentityClient(HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (handler is HttpClientHandler clientHandler) clientHandler.AllowAutoRedirect = false;
        http = new HttpMessageInvoker(handler, disposeHandler: true);
    }

    public async Task<BaiduAdapterResult<BaiduAccountIdentity>> ValidateAsync(
        AccountSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        using var request = new HttpRequestMessage(HttpMethod.Get, IdentityUri);
        var cookieHeader = BuildCookieHeader(session.SessionCookies, IdentityUri, DateTimeOffset.UtcNow);
        if (cookieHeader.Length == 0)
            return Failure(BaiduErrorKind.ExpiredLogin, "No applicable session cookies.");
        request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (HttpRequestException error)
        {
            return Failure(BaiduErrorKind.TransientService, error.GetType().Name);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                return Failure(MapStatus(response.StatusCode), response.StatusCode.ToString());
            byte[] payload;
            try { payload = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false); }
            catch (InvalidDataException error) { return Failure(BaiduErrorKind.UnknownResponse, error.GetType().Name); }
            try
            {
                using var document = JsonDocument.Parse(payload);
                if (!IsSuccess(document.RootElement) ||
                    !document.RootElement.TryGetProperty("result", out var result) ||
                    result.ValueKind != JsonValueKind.Object ||
                    !TryReadUserId(result, out var userId) ||
                    !TryReadDisplayName(result, out var displayName))
                {
                    return Failure(BaiduErrorKind.ExpiredLogin, "Identity fields were absent.");
                }
                var avatarUri = TryReadAvatarUri(result) ??
                    await TryReadUserInfoAvatarAsync(session, userId, cancellationToken).ConfigureAwait(false);
                return BaiduAdapterResult<BaiduAccountIdentity>.Success(new(userId, displayName, avatarUri));
            }
            catch (JsonException error)
            {
                return Failure(BaiduErrorKind.UnknownResponse, error.GetType().Name);
            }
        }
    }

    public async Task<BaiduAdapterResult<AccountCapacity>> GetCapacityAsync(
        AccountSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        using var request = new HttpRequestMessage(HttpMethod.Get, CapacityUri);
        var cookieHeader = BuildCookieHeader(session.SessionCookies, CapacityUri, DateTimeOffset.UtcNow);
        if (cookieHeader.Length == 0)
            return CapacityFailure(BaiduErrorKind.ExpiredLogin, "No applicable session cookies.");
        request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (HttpRequestException error)
        {
            return CapacityFailure(BaiduErrorKind.TransientService, error.GetType().Name);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                return CapacityFailure(MapStatus(response.StatusCode), response.StatusCode.ToString());
            byte[] payload;
            try { payload = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false); }
            catch (InvalidDataException error)
            {
                return CapacityFailure(BaiduErrorKind.UnknownResponse, error.GetType().Name);
            }
            try
            {
                using var document = JsonDocument.Parse(payload);
                var root = document.RootElement;
                if (!IsSuccess(root))
                {
                    var kind = TryReadErrorNumber(root, out var errno) && errno == -6
                        ? BaiduErrorKind.ExpiredLogin
                        : BaiduErrorKind.UnknownResponse;
                    return CapacityFailure(kind, "Capacity request was rejected.");
                }
                if (!TryReadNonNegativeInt64(root, "total", out var total) ||
                    !TryReadNonNegativeInt64(root, "used", out var used) ||
                    used > total)
                {
                    return CapacityFailure(BaiduErrorKind.UnknownResponse, "Capacity fields were invalid.");
                }
                return BaiduAdapterResult<AccountCapacity>.Success(new(total, used));
            }
            catch (JsonException error)
            {
                return CapacityFailure(BaiduErrorKind.UnknownResponse, error.GetType().Name);
            }
        }
    }


    public void Dispose() => http.Dispose();

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumResponseBytes)
            throw new InvalidDataException("The identity response is too large.");
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) return buffer.ToArray();
            if (buffer.Length + read > MaximumResponseBytes)
                throw new InvalidDataException("The identity response is too large.");
            buffer.Write(chunk, 0, read);
        }
    }

    private static string BuildCookieHeader(
        IEnumerable<SessionCookie> cookies,
        Uri destination,
        DateTimeOffset now)
    {
        var values = cookies
            .Where(cookie => CookieApplies(cookie, destination, now))
            .Select(cookie => $"{cookie.Name}={cookie.Value}")
            .ToArray();
        return string.Join("; ", values);
    }

    private static bool CookieApplies(SessionCookie cookie, Uri destination, DateTimeOffset now)
    {
        if (cookie.ExpiresUtc is { } expiry && expiry <= now) return false;
        if (cookie.IsSecure && destination.Scheme != Uri.UriSchemeHttps) return false;
        var domain = cookie.Domain.TrimStart('.');
        var domainMatches = string.Equals(destination.IdnHost, domain, StringComparison.OrdinalIgnoreCase) ||
            destination.IdnHost.EndsWith($".{domain}", StringComparison.OrdinalIgnoreCase);
        return domainMatches && destination.AbsolutePath.StartsWith(cookie.Path, StringComparison.Ordinal);
    }

    private static bool IsSuccess(JsonElement root)
    {
        if (!root.TryGetProperty("errno", out var errno)) return false;
        return errno.ValueKind switch
        {
            JsonValueKind.Number => errno.TryGetInt64(out var value) && value == 0,
            JsonValueKind.String => string.Equals(errno.GetString(), "0", StringComparison.Ordinal),
            _ => false,
        };
    }

    private static bool TryReadErrorNumber(JsonElement root, out long errno)
    {
        errno = 0;
        if (!root.TryGetProperty("errno", out var value)) return false;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt64(out errno),
            JsonValueKind.String => long.TryParse(
                value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out errno),
            _ => false,
        };
    }

    private static bool TryReadUserId(JsonElement result, out string userId)
    {
        userId = string.Empty;
        if (!result.TryGetProperty("uk", out var value)) return false;
        userId = value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) && number > 0 =>
                number.ToString(CultureInfo.InvariantCulture),
            JsonValueKind.String => value.GetString()?.Trim() ?? string.Empty,
            _ => string.Empty,
        };
        return userId.Length is > 0 and <= 256 && !userId.Any(char.IsControl);
    }

    private static bool TryReadDisplayName(JsonElement result, out string displayName)
    {
        displayName = string.Empty;
        if (!result.TryGetProperty("username", out var value) || value.ValueKind != JsonValueKind.String) return false;
        displayName = value.GetString()?.Trim() ?? string.Empty;
        return displayName.Length is > 0 and <= 256 && !displayName.Any(char.IsControl);
    }

    private static Uri? TryReadAvatarUri(JsonElement result)
    {
        foreach (var propertyName in new[] { "avatar_url", "photo_url" })
        {
            if (!result.TryGetProperty(propertyName, out var value) ||
                value.ValueKind != JsonValueKind.String ||
                !Uri.TryCreate(value.GetString(), UriKind.Absolute, out var uri) ||
                !BaiduAccountIdentity.IsTrustedAvatarUri(uri))
                continue;
            return uri;
        }
        return null;
    }

    private async Task<Uri?> TryReadUserInfoAvatarAsync(
        AccountSession session,
        string expectedUserId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UserInfoUri);
        var cookieHeader = BuildCookieHeader(session.SessionCookies, UserInfoUri, DateTimeOffset.UtcNow);
        if (cookieHeader.Length == 0) return null;
        request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (HttpRequestException) { return null; }

        using (response)
        {
            if (!response.IsSuccessStatusCode) return null;
            try
            {
                var payload = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
                using var document = JsonDocument.Parse(payload);
                var root = document.RootElement;
                if (!IsSuccess(root) ||
                    !TryReadUserId(root, out var userId) ||
                    !string.Equals(userId, expectedUserId, StringComparison.Ordinal))
                    return null;
                return TryReadAvatarUri(root);
            }
            catch (InvalidDataException) { return null; }
            catch (JsonException) { return null; }
        }
    }

    private static bool TryReadNonNegativeInt64(JsonElement root, string propertyName, out long value)
    {
        value = 0;
        return root.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt64(out value) &&
            value >= 0;
    }


    private static BaiduErrorKind MapStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => BaiduErrorKind.ExpiredLogin,
        HttpStatusCode.Forbidden => BaiduErrorKind.VerificationRequired,
        HttpStatusCode.TooManyRequests => BaiduErrorKind.TransientService,
        >= HttpStatusCode.InternalServerError => BaiduErrorKind.TransientService,
        _ => BaiduErrorKind.UnknownResponse,
    };

    private static BaiduAdapterResult<BaiduAccountIdentity> Failure(BaiduErrorKind kind, string diagnostic) =>
        BaiduAdapterResult<BaiduAccountIdentity>.Failure(kind, diagnostic);

    private static BaiduAdapterResult<AccountCapacity> CapacityFailure(BaiduErrorKind kind, string diagnostic) =>
        BaiduAdapterResult<AccountCapacity>.Failure(kind, diagnostic);

}
