using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Scheduling;

public sealed class ShareReconciliationPolicy
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    public bool IsCompatible(BaiduShareRecord existing, PermanentShareRequest requested)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(requested);
        if (existing.RemoteItems != requested.RemoteItems || !existing.IsPermanent || !existing.IsAccessUnlimited)
            return false;
        return requested.UsesRandomExtractionCode ||
               string.Equals(existing.ExtractionCode, requested.FixedExtractionCode, StringComparison.Ordinal);
    }

    public string ComputeBaselineFingerprint(IEnumerable<BaiduShareRecord> exactMatches)
    {
        ArgumentNullException.ThrowIfNull(exactMatches);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "share-baseline-v1");
        foreach (var share in exactMatches.OrderBy(value => value.ShareId, StringComparer.Ordinal))
        {
            Append(hash, share.ShareId);
            Append(hash, share.IsPermanent ? "1" : "0");
            Append(hash, share.IsAccessUnlimited ? "1" : "0");
            Append(hash, share.ExtractionCode);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    internal static void Append(IncrementalHash hash, string value)
    {
        var encoded = StrictUtf8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, encoded.Length);
        hash.AppendData(length);
        hash.AppendData(encoded);
    }
}
