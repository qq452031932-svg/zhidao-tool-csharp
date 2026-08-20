namespace BaiduBatchTransfer.Core.Domain;

public sealed record BaiduShareRecord
{
    public BaiduShareRecord(
        string shareId,
        RemoteItemIdentitySet remoteItems,
        string shareUrl,
        string extractionCode,
        bool isPermanent,
        bool isAccessUnlimited)
    {
        ShareId = RequireText(shareId, nameof(shareId));
        RemoteItems = remoteItems ?? throw new ArgumentNullException(nameof(remoteItems));
        ShareUrl = RequireHttpsUrl(shareUrl, nameof(shareUrl));
        ExtractionCode = RequireText(extractionCode, nameof(extractionCode));
        IsPermanent = isPermanent;
        IsAccessUnlimited = isAccessUnlimited;
    }

    public string ShareId { get; }

    public RemoteItemIdentitySet RemoteItems { get; }

    public string ShareUrl { get; }

    public string ExtractionCode { get; }

    public bool IsPermanent { get; }

    public bool IsAccessUnlimited { get; }

    public override string ToString() =>
        $"BaiduShareRecord {{ ShareId = [REDACTED], RemoteItems = [REDACTED], ShareUrl = [REDACTED], ExtractionCode = [REDACTED], IsPermanent = {IsPermanent}, IsAccessUnlimited = {IsAccessUnlimited} }}";

    private static string RequireText(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }

    private static string RequireHttpsUrl(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("A valid HTTPS share URL is required.", parameterName);
        }

        return uri.AbsoluteUri;
    }
}
