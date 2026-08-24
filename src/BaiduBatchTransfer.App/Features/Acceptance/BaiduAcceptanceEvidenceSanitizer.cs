using System.Text.Json;
using System.Text.RegularExpressions;

namespace BaiduBatchTransfer.App.Features.Acceptance;

public sealed record BaiduAcceptanceEvidence(
    string Host,
    string Path,
    string Method,
    int? StatusCode,
    IReadOnlyList<string> JsonPropertyNames)
{
    public override string ToString() => "BaiduAcceptanceEvidence { ShapeOnly = true }";
}

public static partial class BaiduAcceptanceEvidenceSanitizer
{
    private const int MaximumBodyCharacters = 256_000;
    private const int MaximumPropertyNames = 128;
    private const int MaximumPathSegments = 24;
    private static readonly HashSet<string> AllowedMethods = new(StringComparer.Ordinal)
    {
        "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS",
    };

    public static BaiduAcceptanceEvidence? TryCreate(
        Uri destination,
        string? method,
        int statusCode,
        string? responseBody)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!IsOfficialHttps(destination)) return null;

        var propertyNames = SafeJsonPropertyNames(responseBody);
        var normalizedMethod = string.IsNullOrWhiteSpace(method)
            ? "OTHER"
            : method.Trim().ToUpperInvariant();
        if (!AllowedMethods.Contains(normalizedMethod)) normalizedMethod = "OTHER";

        return new(
            destination.IdnHost.ToLowerInvariant(),
            SanitizePath(destination.AbsolutePath),
            normalizedMethod,
            statusCode is >= 100 and <= 599 ? statusCode : null,
            propertyNames);
    }

    public static IReadOnlyList<string> SafeJsonPropertyNames(string? responseBody) =>
        responseBody is { Length: <= MaximumBodyCharacters }
            ? ExtractPropertyNames(responseBody)
            : [];

    private static bool IsOfficialHttps(Uri destination) =>
        destination.IsAbsoluteUri &&
        destination.Scheme == Uri.UriSchemeHttps &&
        destination.IsDefaultPort &&
        string.IsNullOrEmpty(destination.UserInfo) &&
        (string.Equals(destination.IdnHost, "baidu.com", StringComparison.OrdinalIgnoreCase) ||
         destination.IdnHost.EndsWith(".baidu.com", StringComparison.OrdinalIgnoreCase));

    private static string SanitizePath(string absolutePath)
    {
        if (string.IsNullOrEmpty(absolutePath)) return "/";
        var segments = absolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Take(MaximumPathSegments)
            .Select(SanitizePathSegment);
        var sanitized = "/" + string.Join('/', segments);
        return sanitized.Length <= 512 ? sanitized : sanitized[..512];
    }

    private static string SanitizePathSegment(string segment)
    {
        if (segment.Length > 18 || segment.Contains('%') ||
            (segment.Length > 6 && segment.All(char.IsDigit)) ||
            !SafePathSegment().IsMatch(segment))
        {
            return "{dynamic}";
        }
        return segment;
    }

    private static IReadOnlyList<string> ExtractPropertyNames(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
            var names = new HashSet<string>(StringComparer.Ordinal);
            Visit(document.RootElement, names);
            return names.Order(StringComparer.Ordinal).ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void Visit(JsonElement element, HashSet<string> names)
    {
        if (names.Count >= MaximumPropertyNames) return;
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (names.Count >= MaximumPropertyNames) return;
                    if (SafePropertyName().IsMatch(property.Name)) names.Add(property.Name);
                    Visit(property.Value, names);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Visit(item, names);
                    if (names.Count >= MaximumPropertyNames) return;
                }
                break;
        }
    }

    [GeneratedRegex("^[A-Za-z0-9._~-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SafePathSegment();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_.-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafePropertyName();
}
