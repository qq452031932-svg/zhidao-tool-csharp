namespace BaiduBatchTransfer.Core.Domain;

public sealed record ShareMetadata
{
    public ShareMetadata(
        ShareReference share,
        string outerName,
        int itemCount,
        long totalBytes,
        string outerNameFingerprint)
    {
        ArgumentNullException.ThrowIfNull(share);
        ArgumentException.ThrowIfNullOrWhiteSpace(outerName);
        ArgumentOutOfRangeException.ThrowIfNegative(itemCount);
        ArgumentOutOfRangeException.ThrowIfNegative(totalBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(outerNameFingerprint);

        Share = share;
        OuterName = outerName.Trim();
        ItemCount = itemCount;
        TotalBytes = totalBytes;
        OuterNameFingerprint = outerNameFingerprint.Trim();
    }

    public ShareReference Share { get; }

    public string OuterName { get; }

    public int ItemCount { get; }

    public long TotalBytes { get; }

    public string OuterNameFingerprint { get; }

    public override string ToString() =>
        $"ShareMetadata {{ Share = [REDACTED], OuterName = [REDACTED], ItemCount = {ItemCount}, TotalBytes = {TotalBytes}, OuterNameFingerprint = [REDACTED] }}";
}
