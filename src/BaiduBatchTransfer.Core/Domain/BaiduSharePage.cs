using System.Collections.Immutable;

namespace BaiduBatchTransfer.Core.Domain;

public sealed class BaiduSharePage
{
    public BaiduSharePage(IEnumerable<BaiduShareRecord> items, string? nextCursor)
    {
        ArgumentNullException.ThrowIfNull(items);
        Items = items
            .Select(item => item ?? throw new ArgumentException("Share pages cannot contain null records.", nameof(items)))
            .ToImmutableArray();
        NextCursor = nextCursor;
    }

    public ImmutableArray<BaiduShareRecord> Items { get; }

    public string? NextCursor { get; }

    public override string ToString() =>
        $"BaiduSharePage {{ ItemCount = {Items.Length}, NextCursor = [REDACTED], Items = [REDACTED] }}";
}
