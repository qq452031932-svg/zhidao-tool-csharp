using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace BaiduBatchTransfer.Application.Jobs;

internal sealed class CanonicalHashWriter : IDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private bool finalized;

    public CanonicalHashWriter(string domain)
    {
        WriteString("canonical-domain", domain ?? throw new ArgumentNullException(nameof(domain)));
    }

    public void BeginCollection(string tag, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        WriteHeader(tag, 0x20);
        WriteInt32Raw(count);
    }

    public void BeginObject(string tag, int fieldCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fieldCount);
        WriteHeader(tag, 0x21);
        WriteInt32Raw(fieldCount);
    }

    public void WriteString(string tag, string? value)
    {
        WriteHeader(tag, 0x01);
        if (value is null)
        {
            Append([0]);
            return;
        }

        Append([1]);
        var bytes = Encode(value, nameof(value));
        WriteInt32Raw(bytes.Length);
        Append(bytes);
    }

    public void WriteInt32(string tag, int value)
    {
        WriteHeader(tag, 0x02);
        WriteInt32Raw(value);
    }

    public void WriteInt64(string tag, long value)
    {
        WriteHeader(tag, 0x03);
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        Append(bytes);
    }

    public void WriteBoolean(string tag, bool value)
    {
        WriteHeader(tag, 0x04);
        Append([value ? (byte)1 : (byte)0]);
    }

    public void WriteGuid(string tag, Guid value)
    {
        WriteHeader(tag, 0x05);
        Span<byte> bytes = stackalloc byte[16];
        value.TryWriteBytes(bytes, bigEndian: true, out _);
        Append(bytes);
    }

    public void WriteNullableGuid(string tag, Guid? value)
    {
        WriteHeader(tag, 0x06);
        if (value is null)
        {
            Append([0]);
            return;
        }

        Append([1]);
        Span<byte> bytes = stackalloc byte[16];
        value.Value.TryWriteBytes(bytes, bigEndian: true, out _);
        Append(bytes);
    }

    public void WriteDateTimeOffset(string tag, DateTimeOffset value)
    {
        WriteHeader(tag, 0x07);
        Span<byte> bytes = stackalloc byte[sizeof(long) * 2];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value.Ticks);
        BinaryPrimitives.WriteInt64BigEndian(bytes[sizeof(long)..], value.Offset.Ticks);
        Append(bytes);
    }

    public string GetHashHex()
    {
        ObjectDisposedException.ThrowIf(finalized, this);
        finalized = true;
        var result = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        hash.Dispose();
        return result;
    }

    public void Dispose()
    {
        if (!finalized) hash.Dispose();
        finalized = true;
    }

    private void WriteHeader(string tag, byte type)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        var tagBytes = Encode(tag, nameof(tag));
        WriteInt32Raw(tagBytes.Length);
        Append(tagBytes);
        Append([type]);
    }

    private void WriteInt32Raw(int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        Append(bytes);
    }

    private void Append(ReadOnlySpan<byte> value)
    {
        ObjectDisposedException.ThrowIf(finalized, this);
        hash.AppendData(value);
    }

    private static byte[] Encode(string value, string parameterName)
    {
        try
        {
            return StrictUtf8.GetBytes(value);
        }
        catch (EncoderFallbackException error)
        {
            throw new ArgumentException("文本包含无效的 Unicode 序列。", parameterName, error);
        }
    }
}
