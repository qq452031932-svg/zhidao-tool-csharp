using System.Net;
using System.IO;
using System.Globalization;
using System.Text;
using System.Diagnostics;
using BaiduBatchTransfer.Infrastructure.Security;

namespace BaiduBatchTransfer.App.Features.ManagedBrowser;

public sealed record DevToolsActivePort(int Port, Guid BrowserId, Uri Endpoint, Uri WebSocketEndpoint);

public sealed class DevToolsActivePortReader
{
    public const int MaximumBytes = 512;
    internal async Task<(DevToolsActivePort Port, IManagedBrowserPortFileLease File)> ReadAsync(
        IManagedBrowserProfileLease profile, IManagedChromiumProcess process, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(timeout));
        var elapsed = Stopwatch.StartNew();
        var observedFile = false;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!process.IsAlive || !profile.Validate()) throw new IOException("The owned browser or profile is no longer available.");
            IManagedBrowserPortFileLease? file = null;
            try
            {
                file = profile.OpenDevToolsActivePort();
                if (file is not null)
                {
                    observedFile = true;
                    var port = Parse(file.ReadBounded(MaximumBytes), file.LastWriteTimeUtc, process.StartedAtUtc, IPAddress.Loopback);
                    if (!file.Validate()) throw new IOException("The port file changed.");
                    return (port, file);
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException) { file?.Dispose(); }
            catch { file?.Dispose(); throw; }
            var remaining = timeout - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero) break;
            await Task.Delay(remaining < TimeSpan.FromMilliseconds(50) ? remaining : TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
        } while (elapsed.Elapsed < timeout);
        if (!observedFile) throw new TimeoutException("The owned browser has not published its port file yet.");
        throw new IOException("The owned browser did not publish a valid port file in time.");
    }
    public static DevToolsActivePort Parse(ReadOnlySpan<byte> bytes, DateTimeOffset lastWrite,
        DateTimeOffset launchedAt, IPAddress address)
    {
        if (bytes.Length is 0 or > MaximumBytes || lastWrite < launchedAt ||
            !address.Equals(IPAddress.Loopback))
            throw new InvalidDataException("The managed browser port file is not valid.");
        foreach (var value in bytes)
            if (value > 127 || value == 0) throw new InvalidDataException("Invalid port file encoding.");
        var text = Encoding.ASCII.GetString(bytes).Replace("\r\n", "\n", StringComparison.Ordinal);
        if (text.EndsWith('\n')) text = text[..^1];
        var lines = text.Split('\n');
        const string prefix = "/devtools/browser/";
        if (lines.Length != 2 || !int.TryParse(lines[0], NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
            port is < 1 or > 65535 or 80 || lines[0] != port.ToString(CultureInfo.InvariantCulture) ||
            !lines[1].StartsWith(prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(lines[1][prefix.Length..], "D", out var browserId) || browserId == Guid.Empty ||
            lines[1] != prefix + browserId.ToString("D"))
            throw new InvalidDataException("The managed browser port file is not valid.");
        return new(port, browserId, new Uri($"http://127.0.0.1:{port}/"),
            new Uri($"ws://127.0.0.1:{port}{lines[1]}"));
    }
}
