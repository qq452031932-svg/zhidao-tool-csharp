namespace BaiduBatchTransfer.Core.Domain;

public sealed record ShareReference
{
    public ShareReference(string stableShareId, string originalUrl, string? extractionCode = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableShareId);
        ArgumentException.ThrowIfNullOrWhiteSpace(originalUrl);

        if (!Uri.TryCreate(originalUrl, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The share URL must be an absolute HTTPS URL.", nameof(originalUrl));
        }

        StableShareId = stableShareId.Trim();
        OriginalUrl = uri.AbsoluteUri;
        ExtractionCode = string.IsNullOrWhiteSpace(extractionCode) ? null : extractionCode.Trim();
    }

    public string StableShareId { get; }

    public string OriginalUrl { get; }

    public string? ExtractionCode { get; }

    public override string ToString() =>
        "ShareReference { StableShareId = [REDACTED], OriginalUrl = [REDACTED], ExtractionCode = [REDACTED] }";
}
