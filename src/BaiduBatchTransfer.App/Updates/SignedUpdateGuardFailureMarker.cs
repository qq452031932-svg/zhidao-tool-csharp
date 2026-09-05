using System.IO;

namespace BaiduBatchTransfer.App.Updates;

internal static class SignedUpdateGuardFailureMarker
{
    private static readonly byte[] Payload = "SuperPan signed updater guard failed/v1"u8.ToArray();

    internal static void TryWrite(string path)
    {
        try
        {
            var protectedBytes = new WindowsCurrentUserPendingReceiptProtector().Protect(Payload);
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(
                           temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                           4096, FileOptions.WriteThrough))
                {
                    stream.Write(protectedBytes);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                try { File.Delete(temporary); } catch { }
            }
        }
        catch { }
    }

    internal static bool TryConsume(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length is <= 0 or > 4096) return false;
            var plaintext = new WindowsCurrentUserPendingReceiptProtector().Unprotect(bytes);
            return plaintext.AsSpan().SequenceEqual(Payload);
        }
        catch { return false; }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}
