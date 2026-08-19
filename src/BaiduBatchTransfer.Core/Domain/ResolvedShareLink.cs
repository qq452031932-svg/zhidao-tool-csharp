namespace BaiduBatchTransfer.Core.Domain;

public sealed record ResolvedShareLink
{
    public ResolvedShareLink(string finalUrl, string stableShareId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(finalUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(stableShareId);

        if (!Uri.TryCreate(finalUrl, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The resolved URL must be an absolute HTTPS URL.", nameof(finalUrl));
        }

        FinalUrl = uri.AbsoluteUri;
        StableShareId = stableShareId.Trim();
    }

    public string FinalUrl { get; }

    public string StableShareId { get; }

    public override string ToString() =>
        "ResolvedShareLink { FinalUrl = [REDACTED], StableShareId = [REDACTED] }";
}
