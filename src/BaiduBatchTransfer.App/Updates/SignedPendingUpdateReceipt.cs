using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace BaiduBatchTransfer.App.Updates;

internal interface ISignedPendingUpdateReceiptProtector
{
    byte[] Protect(ReadOnlySpan<byte> plaintext);

    byte[] Unprotect(ReadOnlySpan<byte> protectedData);
}

internal sealed class WindowsCurrentUserPendingReceiptProtector : ISignedPendingUpdateReceiptProtector
{
    private static readonly byte[] Entropy =
        "SuperPan.BaiduBatchTransfer/SignedPendingUpdateReceipt/v1"u8.ToArray();

    public byte[] Protect(ReadOnlySpan<byte> plaintext)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("OTA pending receipts require Windows DPAPI.");
        return ProtectedData.Protect(plaintext.ToArray(), Entropy, DataProtectionScope.CurrentUser);
    }

    public byte[] Unprotect(ReadOnlySpan<byte> protectedData)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("OTA pending receipts require Windows DPAPI.");
        return ProtectedData.Unprotect(protectedData.ToArray(), Entropy, DataProtectionScope.CurrentUser);
    }
}

internal sealed record SignedPendingUpdateReceipt(
    string FeedIdentity,
    string PackageRoot,
    string PackageId,
    string Channel,
    string Version,
    string Type,
    string FileName,
    long Size,
    string Sha256,
    string ManifestSha256,
    long AuthorizedAtUnixSeconds);

internal sealed class SignedPendingUpdateReceiptStore
{
    private const int SchemaVersion = 1;
    private const int MaxProtectedBytes = 16 * 1024;
    private const int MaxPlaintextBytes = 8 * 1024;
    private const uint MoveFileReplaceExisting = 0x1;
    private const uint MoveFileWriteThrough = 0x8;

    private readonly string path;
    private readonly ISignedPendingUpdateReceiptProtector protector;

    internal SignedPendingUpdateReceiptStore(
        string path,
        ISignedPendingUpdateReceiptProtector protector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("The pending receipt path must be absolute.", nameof(path));
        this.path = Path.GetFullPath(path);
        this.protector = protector ?? throw new ArgumentNullException(nameof(protector));
    }

    internal void Write(SignedPendingUpdateReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = SchemaVersion,
            feedIdentity = receipt.FeedIdentity,
            packageRoot = receipt.PackageRoot,
            packageId = receipt.PackageId,
            channel = receipt.Channel,
            version = receipt.Version,
            type = receipt.Type,
            fileName = receipt.FileName,
            size = receipt.Size,
            sha256 = receipt.Sha256,
            manifestSha256 = receipt.ManifestSha256,
            authorizedAtUnixSeconds = receipt.AuthorizedAtUnixSeconds
        });
        if (plaintext.Length > MaxPlaintextBytes)
            throw new InvalidDataException("The pending update receipt is too large.");
        var protectedBytes = protector.Protect(plaintext);
        if (protectedBytes.Length is <= 0 or > MaxProtectedBytes)
            throw new InvalidDataException("The protected pending update receipt has an invalid size.");

        var parent = Path.GetDirectoryName(path)
            ?? throw new InvalidDataException("The pending update receipt path is invalid.");
        Directory.CreateDirectory(parent);
        var temporary = Path.Combine(parent, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(protectedBytes);
                stream.Flush(flushToDisk: true);
            }

            ReplaceDurably(temporary, path);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    internal SignedPendingUpdateReceipt Read()
    {
        byte[] protectedBytes;
        using (var stream = new FileStream(
                   path,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read,
                   4096,
                   FileOptions.SequentialScan))
        {
            if (stream.Length is <= 0 or > MaxProtectedBytes)
                throw new InvalidDataException("The protected pending update receipt has an invalid size.");
            protectedBytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(protectedBytes);
        }

        var plaintext = protector.Unprotect(protectedBytes);
        try
        {
            if (plaintext.Length is <= 0 or > MaxPlaintextBytes)
                throw new InvalidDataException("The pending update receipt has an invalid size.");
            return Parse(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    internal void Delete()
    {
        File.Delete(path);
        if (File.Exists(path))
            throw new IOException("The pending update receipt could not be removed.");
    }

    private static SignedPendingUpdateReceipt Parse(byte[] bytes)
    {
        try
        {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 3
            });
            using var document = JsonDocument.ParseValue(ref reader);
            if (reader.BytesConsumed != bytes.Length || document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("The pending update receipt is malformed.");

            var root = document.RootElement;
            string[] expected =
            [
                "schemaVersion", "feedIdentity", "packageRoot", "packageId", "channel", "version", "type",
                "fileName", "size", "sha256", "manifestSha256", "authorizedAtUnixSeconds"
            ];
            var names = root.EnumerateObject().Select(property => property.Name).ToArray();
            if (names.Length != expected.Length ||
                names.Distinct(StringComparer.Ordinal).Count() != expected.Length ||
                expected.Any(name => !names.Contains(name, StringComparer.Ordinal)) ||
                root.GetProperty("schemaVersion").ValueKind != JsonValueKind.Number ||
                root.GetProperty("schemaVersion").GetInt32() != SchemaVersion)
            {
                throw new InvalidDataException("The pending update receipt schema is invalid.");
            }

            return new SignedPendingUpdateReceipt(
                RequiredString(root, "feedIdentity", 2048),
                RequiredString(root, "packageRoot", 1024),
                RequiredString(root, "packageId", 128),
                RequiredString(root, "channel", 128),
                RequiredString(root, "version", 32),
                RequiredString(root, "type", 32),
                RequiredString(root, "fileName", 260),
                root.GetProperty("size").GetInt64(),
                RequiredString(root, "sha256", 64),
                RequiredString(root, "manifestSha256", 64),
                root.GetProperty("authorizedAtUnixSeconds").GetInt64());
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new InvalidDataException("The pending update receipt is malformed.", exception);
        }
    }

    private static string RequiredString(JsonElement root, string name, int maxLength)
    {
        if (root.GetProperty(name).ValueKind != JsonValueKind.String ||
            root.GetProperty(name).GetString() is not { Length: > 0 } value ||
            value.Length > maxLength)
        {
            throw new InvalidDataException("The pending update receipt contains an invalid value.");
        }
        return value;
    }

    private static void ReplaceDurably(string temporary, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!MoveFileEx(temporary, target, MoveFileReplaceExisting | MoveFileWriteThrough))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The pending update receipt could not be durably replaced.");
            return;
        }

        File.Move(temporary, target, overwrite: true);
        using var stream = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 1, FileOptions.WriteThrough);
        stream.Flush(flushToDisk: true);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existingFileName, string newFileName, uint flags);
}
