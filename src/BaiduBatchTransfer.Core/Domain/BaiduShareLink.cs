namespace BaiduBatchTransfer.Core.Domain;

public static class BaiduShareLink
{
    public static string Normalize(string shareUrl, string extractionCode)
    {
        if (extractionCode is null || extractionCode.Length != 4 ||
            extractionCode.Any(character => !char.IsAsciiLetterOrDigit(character)))
            throw new ArgumentException("A four-character alphanumeric extraction code is required.", nameof(extractionCode));
        if (!Uri.TryCreate(shareUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(uri.Host) ||
            !Uri.IsWellFormedUriString(shareUrl, UriKind.Absolute))
            throw new ArgumentException("A valid absolute HTTPS share URL is required.", nameof(shareUrl));

        var hasMatchingCode = false;
        var query = uri.Query.Length == 0 ? string.Empty : uri.Query[1..];
        foreach (var parameter in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = parameter.Split('=', 2);
            if (!string.Equals(Uri.UnescapeDataString(parts[0]), "pwd", StringComparison.Ordinal)) continue;
            if (parts.Length != 2 ||
                !string.Equals(Uri.UnescapeDataString(parts[1]), extractionCode, StringComparison.Ordinal))
                throw new ArgumentException("The share URL extraction code is inconsistent.", nameof(shareUrl));
            hasMatchingCode = true;
        }
        if (hasMatchingCode) return shareUrl;

        var fragmentIndex = shareUrl.IndexOf('#');
        var link = fragmentIndex < 0 ? shareUrl : shareUrl[..fragmentIndex];
        var fragment = fragmentIndex < 0 ? string.Empty : shareUrl[fragmentIndex..];
        var separator = !link.Contains('?') ? "?" : query.Length == 0 || query.EndsWith('&') ? string.Empty : "&";
        return link + separator + "pwd=" + extractionCode + fragment;
    }
}
