namespace BaiduBatchTransfer.Core.Domain;

public sealed record PermanentShareRequest
{
    public PermanentShareRequest(
        RemoteItemIdentitySet remoteItems,
        string? fixedExtractionCode = null)
    {
        ArgumentNullException.ThrowIfNull(remoteItems);
        if (fixedExtractionCode is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(fixedExtractionCode);
            var candidate = fixedExtractionCode.Trim();
            if (!IsValidFixedExtractionCode(candidate))
                throw new ArgumentException(
                    "A fixed extraction code must contain exactly four ASCII letters or digits.",
                    nameof(fixedExtractionCode));
        }

        RemoteItems = remoteItems;
        FixedExtractionCode = fixedExtractionCode?.Trim();
    }

    public RemoteItemIdentitySet RemoteItems { get; }

    public bool IsPermanent => true;

    public bool IsAccessUnlimited => true;

    public string? FixedExtractionCode { get; }

    public bool UsesRandomExtractionCode => FixedExtractionCode is null;

    public static bool IsValidFixedExtractionCode(string? value)
    {
        if (value is null) return false;
        var candidate = value.Trim();
        return candidate.Length == 4 && candidate.All(character =>
            character is (>= 'a' and <= 'z') or
                (>= 'A' and <= 'Z') or
                (>= '0' and <= '9'));
    }

    public override string ToString() =>
        $"PermanentShareRequest {{ RemoteItems = [REDACTED], IsPermanent = {IsPermanent}, IsAccessUnlimited = {IsAccessUnlimited}, FixedExtractionCode = [REDACTED] }}";
}
