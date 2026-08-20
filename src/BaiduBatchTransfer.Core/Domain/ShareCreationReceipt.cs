namespace BaiduBatchTransfer.Core.Domain;

public enum ShareExtractionCodeBehavior
{
    Unknown,
    Random,
    Fixed,
}

public enum ShareSettingsMatchStatus
{
    Pending,
    Matched,
    Mismatched,
}

public sealed record EffectiveShareSettings
{
    public EffectiveShareSettings(
        bool? isPermanent,
        bool? isAccessUnlimited,
        ShareExtractionCodeBehavior extractionCodeBehavior,
        string? extractionCode)
    {
        if (!Enum.IsDefined(extractionCodeBehavior))
            throw new ArgumentOutOfRangeException(nameof(extractionCodeBehavior));
        if (extractionCode is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(extractionCode);

        IsPermanent = isPermanent;
        IsAccessUnlimited = isAccessUnlimited;
        ExtractionCodeBehavior = extractionCodeBehavior;
        ExtractionCode = extractionCode?.Trim();
    }

    public static EffectiveShareSettings Unconfirmed { get; } =
        new(null, null, ShareExtractionCodeBehavior.Unknown, null);

    public bool? IsPermanent { get; }

    public bool? IsAccessUnlimited { get; }

    public ShareExtractionCodeBehavior ExtractionCodeBehavior { get; }

    public string? ExtractionCode { get; }

    public bool IsFullyObserved =>
        IsPermanent.HasValue &&
        IsAccessUnlimited.HasValue &&
        ExtractionCodeBehavior != ShareExtractionCodeBehavior.Unknown &&
        ExtractionCode is not null;

    public override string ToString() =>
        $"EffectiveShareSettings {{ IsPermanent = {IsPermanent}, IsAccessUnlimited = {IsAccessUnlimited}, ExtractionCodeBehavior = {ExtractionCodeBehavior}, ExtractionCode = [REDACTED] }}";
}

public sealed record ShareCreationReceipt
{
    public ShareCreationReceipt(
        DateTimeOffset submittedAt,
        string adapterRequestId,
        PermanentShareRequest request,
        string shareId,
        string shareUrl,
        EffectiveShareSettings effectiveSettings)
    {
        SubmittedAtUtc = submittedAt.ToUniversalTime();
        AdapterRequestId = RequireText(adapterRequestId, nameof(adapterRequestId));
        Request = request ?? throw new ArgumentNullException(nameof(request));
        ShareId = RequireText(shareId, nameof(shareId));
        ShareUrl = RequireHttpsUrl(shareUrl, nameof(shareUrl));
        EffectiveSettings = effectiveSettings ?? throw new ArgumentNullException(nameof(effectiveSettings));
        SettingsMatchStatus = CompareSettings(request, effectiveSettings);
    }

    public DateTimeOffset SubmittedAtUtc { get; }

    public string AdapterRequestId { get; }

    public PermanentShareRequest Request { get; }

    public string ShareId { get; }

    public string ShareUrl { get; }

    public EffectiveShareSettings EffectiveSettings { get; }

    public string? ExtractionCode => EffectiveSettings.ExtractionCode;

    public ShareSettingsMatchStatus SettingsMatchStatus { get; }

    public bool IsFinalAcceptance => SettingsMatchStatus == ShareSettingsMatchStatus.Matched;

    public override string ToString() =>
        $"ShareCreationReceipt {{ SubmittedAtUtc = {SubmittedAtUtc:O}, AdapterRequestId = [REDACTED], Request = [REDACTED], ShareId = [REDACTED], ShareUrl = [REDACTED], EffectiveSettings = [REDACTED], SettingsMatchStatus = {SettingsMatchStatus} }}";

    private static ShareSettingsMatchStatus CompareSettings(
        PermanentShareRequest request,
        EffectiveShareSettings effectiveSettings)
    {
        if (!effectiveSettings.IsFullyObserved) return ShareSettingsMatchStatus.Pending;
        if (effectiveSettings.IsPermanent != true || effectiveSettings.IsAccessUnlimited != true)
            return ShareSettingsMatchStatus.Mismatched;

        if (request.UsesRandomExtractionCode)
        {
            return effectiveSettings.ExtractionCodeBehavior == ShareExtractionCodeBehavior.Random
                ? ShareSettingsMatchStatus.Matched
                : ShareSettingsMatchStatus.Mismatched;
        }

        return effectiveSettings.ExtractionCodeBehavior == ShareExtractionCodeBehavior.Fixed &&
               string.Equals(
                   effectiveSettings.ExtractionCode,
                   request.FixedExtractionCode,
                   StringComparison.Ordinal)
            ? ShareSettingsMatchStatus.Matched
            : ShareSettingsMatchStatus.Mismatched;
    }

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
