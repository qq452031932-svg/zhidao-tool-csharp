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
            .OrderBy(item => item.ExactId, StringComparer.Ordinal)
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
        Fingerprint = ComputeFingerprint(snapshot);
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

    private static string ComputeFingerprint(ImmutableArray<RemoteItemIdentity> items)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[sizeof(int)];
        foreach (var item in items)
        {
            var encoded = StrictUtf8.GetBytes(item.ExactId);
            BinaryPrimitives.WriteInt32BigEndian(length, encoded.Length);
            hash.AppendData(length);
            hash.AppendData(encoded);
        }

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
}
