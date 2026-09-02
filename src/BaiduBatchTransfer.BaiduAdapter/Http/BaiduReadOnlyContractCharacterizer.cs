using System.Collections.Immutable;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.BaiduAdapter.Http;

public sealed record BaiduReadOnlyContractObservation(
    string RouteId,
    string Method,
    int StatusCode,
    ImmutableArray<string> JsonShape)
{
    public override string ToString() =>
        $"BaiduReadOnlyContractObservation {{ RouteId = {RouteId}, Method = {Method}, StatusCode = {StatusCode}, JsonShape = [{string.Join(", ", JsonShape)}], Values = [REDACTED] }}";
}

public enum BaiduReadOnlyQueryValueKind
{
    Fixed,
    PositiveInteger,
    Opaque,
}

public sealed record BaiduReadOnlyQueryRule(
    BaiduReadOnlyQueryValueKind Kind,
    string? FixedValue,
    int MaximumLength)
{
    public static BaiduReadOnlyQueryRule Fixed(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0 || value.Length > 2048)
            throw new ArgumentOutOfRangeException(nameof(value));
        return new(BaiduReadOnlyQueryValueKind.Fixed, value, value.Length);
    }

    public static BaiduReadOnlyQueryRule PositiveInteger() =>
        new(BaiduReadOnlyQueryValueKind.PositiveInteger, null, 10);

    public static BaiduReadOnlyQueryRule Opaque(int maximumLength = 512)
    {
        if (maximumLength is < 1 or > 2048)
            throw new ArgumentOutOfRangeException(nameof(maximumLength));
        return new(BaiduReadOnlyQueryValueKind.Opaque, null, maximumLength);
    }
}

public sealed record BaiduReadOnlyPagedRoutePolicy(
    string RouteId,
    HttpMethod Method,
    string Path,
    IReadOnlyDictionary<string, BaiduReadOnlyQueryRule> QueryRules,
    int MaximumPages);

public sealed record BaiduReadOnlyPageState(int ItemCount, string? NextCursor)
{
    public override string ToString() =>
        $"BaiduReadOnlyPageState {{ ItemCount = {ItemCount}, NextCursor = [REDACTED] }}";
}

public sealed record BaiduReadOnlyPageObservation(
    string RouteId,
    string Method,
    int StatusCode,
    int PageNumber,
    int ItemCount,
    bool HasNextPage,
    ImmutableArray<string> JsonShape)
{
    public override string ToString() =>
        $"BaiduReadOnlyPageObservation {{ RouteId = {RouteId}, Method = {Method}, StatusCode = {StatusCode}, PageNumber = {PageNumber}, ItemCount = {ItemCount}, HasNextPage = {HasNextPage}, JsonShape = [{string.Join(", ", JsonShape)}], Values = [REDACTED] }}";
}

public sealed class BaiduReadOnlyContractCharacterizer
{
    private const int MaximumResponseBytes = 64 * 1024;
    private const int MaximumPagedResponseBytes = 1024 * 1024;
    private readonly IReadOnlyDictionary<string, Uri> routes;
    private readonly HttpMessageInvoker http;

    public BaiduReadOnlyContractCharacterizer(
        IReadOnlyDictionary<string, Uri> routes,
        HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(handler);
        if (routes.Count == 0) throw new ArgumentException("At least one route is required.", nameof(routes));
        foreach (var (routeId, uri) in routes)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(routeId);
            ValidateAllowedUri(uri);
        }
        this.routes = routes.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        http = new HttpMessageInvoker(handler, disposeHandler: false);
    }

    public async Task<BaiduReadOnlyContractObservation> ObserveAsync(
        string routeId,
        HttpRequestMessage request,
        AccountSession session,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routeId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(session);
        if (request.Method != HttpMethod.Get && request.Method != HttpMethod.Head)
            throw new InvalidOperationException("READ_ONLY_METHOD_REQUIRED");
        if (request.Content is not null)
            throw new InvalidOperationException("READ_ONLY_BODY_FORBIDDEN");
        if (!routes.TryGetValue(routeId, out var allowed) || request.RequestUri != allowed)
            throw new InvalidOperationException("READ_ONLY_ROUTE_NOT_ALLOWED");

        var cookieHeader = BuildCookieHeader(session.SessionCookies, allowed, DateTimeOffset.UtcNow);
        if (cookieHeader.Length == 0)
            throw new InvalidOperationException("READ_ONLY_SESSION_UNAVAILABLE");
        request.Headers.Remove("Cookie");
        request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var payload = await ReadBoundedAsync(
            response.Content, MaximumResponseBytes, cancellationToken).ConfigureAwait(false);
        var shape = ImmutableArray<string>.Empty;
        if (payload.Length > 0)
        {
            using var document = JsonDocument.Parse(payload);
            var builder = ImmutableArray.CreateBuilder<string>();
            AddShape(document.RootElement, "$", builder);
            shape = builder.ToImmutable();
        }
        return new(routeId, request.Method.Method, (int)response.StatusCode, shape);
    }

    public async Task<IReadOnlyList<BaiduReadOnlyPageObservation>> ObservePagesAsync(
        BaiduReadOnlyPagedRoutePolicy policy,
        Func<string?, Uri> routeBuilder,
        Func<JsonElement, BaiduReadOnlyPageState> readPageState,
        AccountSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(routeBuilder);
        ArgumentNullException.ThrowIfNull(readPageState);
        ArgumentNullException.ThrowIfNull(session);
        ValidatePagedPolicy(policy);

        var observations = new List<BaiduReadOnlyPageObservation>();
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        var seenUris = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;

        for (var pageNumber = 1; pageNumber <= policy.MaximumPages; pageNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var route = routeBuilder(cursor);
            ValidatePagedUri(policy, route);
            if (!seenUris.Add(route.AbsoluteUri))
                throw new InvalidOperationException("READ_ONLY_CURSOR_REPEATED");

            using var request = new HttpRequestMessage(policy.Method, route);
            var cookieHeader = BuildCookieHeader(session.SessionCookies, route, DateTimeOffset.UtcNow);
            if (cookieHeader.Length == 0)
                throw new InvalidOperationException("READ_ONLY_SESSION_UNAVAILABLE");
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var payload = await ReadBoundedAsync(
                response.Content, MaximumPagedResponseBytes, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(payload);
            var shapeBuilder = ImmutableArray.CreateBuilder<string>();
            AddShape(document.RootElement, "$", shapeBuilder);
            var pageState = readPageState(document.RootElement);
            ValidatePageState(pageState);

            observations.Add(new(
                policy.RouteId,
                policy.Method.Method,
                (int)response.StatusCode,
                pageNumber,
                pageState.ItemCount,
                pageState.NextCursor is not null,
                shapeBuilder.ToImmutable()));

            if (pageState.NextCursor is null)
                return observations.AsReadOnly();
            if (!seenCursors.Add(pageState.NextCursor))
                throw new InvalidOperationException("READ_ONLY_CURSOR_REPEATED");
            cursor = pageState.NextCursor;
        }

        throw new InvalidOperationException("READ_ONLY_PAGE_LIMIT_EXCEEDED");
    }

    private static void ValidateAllowedUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort ||
            !string.Equals(uri.Host, "pan.baidu.com", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Only exact default-port pan.baidu.com HTTPS routes are allowed.", nameof(uri));
    }

    private static void ValidatePagedPolicy(BaiduReadOnlyPagedRoutePolicy policy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policy.RouteId);
        ArgumentNullException.ThrowIfNull(policy.Method);
        ArgumentException.ThrowIfNullOrWhiteSpace(policy.Path);
        ArgumentNullException.ThrowIfNull(policy.QueryRules);
        if (policy.Method != HttpMethod.Get && policy.Method != HttpMethod.Head)
            throw new InvalidOperationException("READ_ONLY_METHOD_REQUIRED");
        if (!policy.Path.StartsWith("/", StringComparison.Ordinal) ||
            policy.Path.Contains("?", StringComparison.Ordinal) ||
            policy.Path.Contains("#", StringComparison.Ordinal))
            throw new ArgumentException("A fixed absolute path is required.", nameof(policy));
        if (policy.QueryRules.Count == 0)
            throw new ArgumentException("At least one query rule is required.", nameof(policy));
        if (policy.MaximumPages is < 1 or > 10_000)
            throw new ArgumentOutOfRangeException(nameof(policy));

        foreach (var (name, rule) in policy.QueryRules)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(rule);
            if (name.IndexOfAny(['&', '=', '#']) >= 0 ||
                rule.MaximumLength is < 1 or > 2048 ||
                (rule.Kind == BaiduReadOnlyQueryValueKind.Fixed && rule.FixedValue is null))
                throw new ArgumentException("Invalid query rule.", nameof(policy));
        }
    }

    private static void ValidatePagedUri(BaiduReadOnlyPagedRoutePolicy policy, Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort ||
            !string.Equals(uri.Host, "pan.baidu.com", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            !string.Equals(uri.AbsolutePath, policy.Path, StringComparison.Ordinal))
            throw new InvalidOperationException("READ_ONLY_ROUTE_NOT_ALLOWED");

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var query = uri.Query.TrimStart('?');
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            var name = Uri.UnescapeDataString(separator < 0 ? part : part[..separator]);
            var value = Uri.UnescapeDataString(separator < 0 ? string.Empty : part[(separator + 1)..]);
            if (!values.TryAdd(name, value))
                throw new InvalidOperationException("READ_ONLY_QUERY_NOT_ALLOWED");
        }

        if (values.Count != policy.QueryRules.Count)
            throw new InvalidOperationException("READ_ONLY_QUERY_NOT_ALLOWED");
        foreach (var (name, rule) in policy.QueryRules)
        {
            if (!values.TryGetValue(name, out var value) || !QueryValueMatches(rule, value))
                throw new InvalidOperationException("READ_ONLY_QUERY_NOT_ALLOWED");
        }
    }

    private static bool QueryValueMatches(BaiduReadOnlyQueryRule rule, string value)
    {
        if (value.Length == 0 || value.Length > rule.MaximumLength || value.Any(char.IsControl))
            return false;
        return rule.Kind switch
        {
            BaiduReadOnlyQueryValueKind.Fixed => string.Equals(value, rule.FixedValue, StringComparison.Ordinal),
            BaiduReadOnlyQueryValueKind.PositiveInteger =>
                int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0,
            BaiduReadOnlyQueryValueKind.Opaque => true,
            _ => false,
        };
    }

    private static void ValidatePageState(BaiduReadOnlyPageState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.ItemCount is < 0 or > 1_000_000)
            throw new InvalidDataException("READ_ONLY_ITEM_COUNT_INVALID");
        if (state.NextCursor is not null &&
            (string.IsNullOrWhiteSpace(state.NextCursor) ||
             state.NextCursor.Length > 2048 ||
             state.NextCursor.Any(char.IsControl)))
            throw new InvalidDataException("READ_ONLY_CURSOR_INVALID");
    }

    private static string BuildCookieHeader(
        IEnumerable<SessionCookie> cookies,
        Uri uri,
        DateTimeOffset now)
    {
        return string.Join("; ", cookies
            .Where(cookie => cookie.ExpiresUtc is null || cookie.ExpiresUtc > now)
            .Where(cookie => !cookie.IsSecure || uri.Scheme == Uri.UriSchemeHttps)
            .Where(cookie => DomainMatches(uri.Host, cookie.Domain))
            .Where(cookie => uri.AbsolutePath.StartsWith(cookie.Path, StringComparison.Ordinal))
            .Select(cookie => $"{cookie.Name}={cookie.Value}"));
    }

    private static bool DomainMatches(string host, string domain)
    {
        var normalized = domain.TrimStart('.');
        return string.Equals(host, normalized, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith('.' + normalized, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maximumBytes)
            throw new InvalidDataException("READ_ONLY_RESPONSE_TOO_LARGE");
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > maximumBytes)
                throw new InvalidDataException("READ_ONLY_RESPONSE_TOO_LARGE");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static void AddShape(
        JsonElement element,
        string path,
        ImmutableArray<string>.Builder builder)
    {
        builder.Add($"{path}:{Kind(element.ValueKind)}");
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
                AddShape(property.Value, $"{path}.{property.Name}", builder);
        }
        else if (element.ValueKind == JsonValueKind.Array && element.GetArrayLength() > 0)
        {
            AddShape(element[0], $"{path}[]", builder);
        }
    }

    private static string Kind(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Null => "null",
        _ => "undefined",
    };
}
