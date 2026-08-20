namespace BaiduBatchTransfer.Core.Domain;

public sealed record ShareCreationReceipt
{
    public ShareCreationReceipt(
        DateTimeOffset submittedAt,
        string adapterRequestId,
        PermanentShareRequest request,
        string shareId,
        string shareUrl,
        string extractionCode)
    {
        SubmittedAtUtc = submittedAt.ToUniversalTime();
        AdapterRequestId = RequireText(adapterRequestId, nameof(adapterRequestId));
        Request = request ?? throw new ArgumentNullException(nameof(request));
        ShareId = RequireText(shareId, nameof(shareId));
        ShareUrl = RequireHttpsUrl(shareUrl, nameof(shareUrl));
        ExtractionCode = RequireText(extractionCode, nameof(extractionCode));
    }

    public DateTimeOffset SubmittedAtUtc { get; }

    public string AdapterRequestId { get; }

    public PermanentShareRequest Request { get; }

    public string ShareId { get; }

    public string ShareUrl { get; }

    public string ExtractionCode { get; }

    public override string ToString() =>
        $"ShareCreationReceipt {{ SubmittedAtUtc = {SubmittedAtUtc:O}, AdapterRequestId = [REDACTED], Request = [REDACTED], ShareId = [REDACTED], ShareUrl = [REDACTED], ExtractionCode = [REDACTED] }}";

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
