namespace BaiduBatchTransfer.Core.Domain;

public sealed record PermanentShareRequest
{
    public PermanentShareRequest(
        RemoteItemIdentitySet remoteItems,
        string? fixedExtractionCode = null)
    {
        ArgumentNullException.ThrowIfNull(remoteItems);
        if (fixedExtractionCode is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(fixedExtractionCode);

        RemoteItems = remoteItems;
        FixedExtractionCode = fixedExtractionCode?.Trim();
    }

    public RemoteItemIdentitySet RemoteItems { get; }

    public bool IsPermanent => true;

    public bool IsAccessUnlimited => true;

    public string? FixedExtractionCode { get; }

    public bool UsesRandomExtractionCode => FixedExtractionCode is null;

    public override string ToString() =>
        $"PermanentShareRequest {{ RemoteItems = [REDACTED], IsPermanent = {IsPermanent}, IsAccessUnlimited = {IsAccessUnlimited}, FixedExtractionCode = [REDACTED] }}";
}
