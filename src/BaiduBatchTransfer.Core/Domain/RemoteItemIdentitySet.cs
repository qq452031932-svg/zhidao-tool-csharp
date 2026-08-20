using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace BaiduBatchTransfer.Core.Domain;

public sealed class RemoteItemIdentitySet : IEquatable<RemoteItemIdentitySet>
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public RemoteItemIdentitySet(IEnumerable<RemoteItemIdentity> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var snapshot = items
            .Select(item => item ?? throw new ArgumentException("Remote items cannot contain null values.", nameof(items)))
            .Select(item => (Item: item, SortKey: GetCanonicalSortKey(item.ExactId)))
            .OrderBy(value => value.SortKey, CanonicalSortKeyComparer.Instance)
            .Select(value => value.Item)
            .ToImmutableArray();
        if (snapshot.IsEmpty)
            throw new ArgumentException("At least one exact remote item identity is required.", nameof(items));
        foreach (var item in snapshot) ValidateExactId(item.ExactId, nameof(items));
        if (snapshot.Zip(snapshot.Skip(1), (left, right) =>
                string.Equals(left.ExactId, right.ExactId, StringComparison.Ordinal)).Any(equal => equal))
        {
            throw new ArgumentException("Exact remote item identities must be unique.", nameof(items));
        }

        Items = snapshot;
        Fingerprint = ComputeFingerprintFromCanonicalOrder(snapshot.Select(item => item.ExactId));
    }

    public ImmutableArray<RemoteItemIdentity> Items { get; }

    public string Fingerprint { get; }

    public int Count => Items.Length;

    public bool Equals(RemoteItemIdentitySet? other) =>
        other is not null &&
        Items.Select(item => item.ExactId)
            .SequenceEqual(other.Items.Select(item => item.ExactId), StringComparer.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as RemoteItemIdentitySet);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in Items) hash.Add(item.ExactId, StringComparer.Ordinal);
        return hash.ToHashCode();
    }

    public static bool operator ==(RemoteItemIdentitySet? left, RemoteItemIdentitySet? right) =>
        object.Equals(left, right);

    public static bool operator !=(RemoteItemIdentitySet? left, RemoteItemIdentitySet? right) =>
        !object.Equals(left, right);

    public override string ToString() =>
        $"RemoteItemIdentitySet {{ Count = {Count}, Fingerprint = [REDACTED], Items = [REDACTED] }}";

    public static byte[] GetCanonicalSortKey(string exactId)
    {
        ArgumentNullException.ThrowIfNull(exactId);
        ValidateExactId(exactId, nameof(exactId));
        var key = new byte[checked(exactId.Length * sizeof(char))];
        for (var index = 0; index < exactId.Length; index++)
            BinaryPrimitives.WriteUInt16BigEndian(key.AsSpan(index * sizeof(char), sizeof(char)), exactId[index]);
        return key;
    }

    public static string ComputeFingerprintFromCanonicalOrder(IEnumerable<string> exactIds)
    {
        ArgumentNullException.ThrowIfNull(exactIds);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[sizeof(int)];
        byte[]? previous = null;
        var count = 0;
        foreach (var exactId in exactIds)
        {
            ArgumentNullException.ThrowIfNull(exactId);
            var sortKey = GetCanonicalSortKey(exactId);
            if (previous is not null && CanonicalSortKeyComparer.Instance.Compare(previous, sortKey) >= 0)
                throw new ArgumentException("Exact remote item identities must be in strict canonical order.", nameof(exactIds));
            var encoded = StrictUtf8.GetBytes(exactId);
            BinaryPrimitives.WriteInt32BigEndian(length, encoded.Length);
            hash.AppendData(length);
            hash.AppendData(encoded);
            previous = sortKey;
            count++;
        }
        if (count == 0)
            throw new ArgumentException("At least one exact remote item identity is required.", nameof(exactIds));

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static async Task<string> ComputeFingerprintFromCanonicalOrderAsync(
        IAsyncEnumerable<string> exactIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exactIds);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var length = new byte[sizeof(int)];
        byte[]? previous = null;
        var count = 0;
        await foreach (var exactId in exactIds.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            ArgumentNullException.ThrowIfNull(exactId);
            var sortKey = GetCanonicalSortKey(exactId);
            if (previous is not null && CanonicalSortKeyComparer.Instance.Compare(previous, sortKey) >= 0)
                throw new ArgumentException("Exact remote item identities must be in strict canonical order.", nameof(exactIds));
            var encoded = StrictUtf8.GetBytes(exactId);
            BinaryPrimitives.WriteInt32BigEndian(length, encoded.Length);
            hash.AppendData(length);
            hash.AppendData(encoded);
            previous = sortKey;
            count++;
        }
        if (count == 0)
            throw new ArgumentException("At least one exact remote item identity is required.", nameof(exactIds));
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void ValidateExactId(string exactId, string parameterName)
    {
        try
        {
            _ = StrictUtf8.GetByteCount(exactId);
        }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException(
                "Exact remote item identities must contain valid UTF-16 text.",
                parameterName);
        }
    }

    private sealed class CanonicalSortKeyComparer : IComparer<byte[]>
    {
        public static CanonicalSortKeyComparer Instance { get; } = new();

        public int Compare(byte[]? left, byte[]? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            return left.AsSpan().SequenceCompareTo(right);
        }
    }
}
