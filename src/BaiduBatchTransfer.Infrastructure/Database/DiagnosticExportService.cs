using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BaiduBatchTransfer.Infrastructure.Database;

public sealed record DiagnosticLogEntry(string Stage, string Level, string EventCode, string SanitizedMessage);
public sealed record DiagnosticExportRequest(
    string AppVersion,
    int SchemaVersion,
    IReadOnlyList<string> IntegrityOutput,
    IReadOnlyList<DiagnosticLogEntry> Logs,
    IReadOnlyDictionary<string, string> Metadata);

public sealed partial class DiagnosticExportService
{
    private static readonly HashSet<string> AllowedMetadata = new(StringComparer.OrdinalIgnoreCase)
    {
        "os", "architecture", "runtime", "appMode"
    };
    private readonly string diagnosticRoot;

    public DiagnosticExportService(string diagnosticRoot) =>
        this.diagnosticRoot = Path.GetFullPath(diagnosticRoot ?? throw new ArgumentNullException(nameof(diagnosticRoot)));

    public async Task<string> CreateAsync(DiagnosticExportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Directory.CreateDirectory(diagnosticRoot);
        var final = DatabaseBackupService.EnsureDirectChild(Path.Combine(diagnosticRoot, $"diagnostics-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}.zip"), diagnosticRoot);
        var temporary = DatabaseBackupService.EnsureDirectChild(Path.Combine(diagnosticRoot, $"diagnostics-{Guid.NewGuid():N}.tmp"), diagnosticRoot);
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, useAsync: true))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
            {
                await WriteJsonAsync(zip, "manifest.json", new { AppVersion = SafeMetadata(request.AppVersion), request.SchemaVersion, CreatedAt = DateTimeOffset.UtcNow }, cancellationToken).ConfigureAwait(false);
                await WriteTextAsync(zip, "integrity.txt", string.Join('\n', request.IntegrityOutput.Select(x =>
                    string.Equals(x, "ok", StringComparison.OrdinalIgnoreCase) ? "ok" : "non-ok")), cancellationToken).ConfigureAwait(false);
                var logs = request.Logs.Select((x, index) => new
                {
                    Sequence = index + 1,
                    Stage = SafeMetadata(x.Stage),
                    Level = SafeMetadata(x.Level),
                    EventCode = SafeMetadata(x.EventCode),
                    MessageOmitted = true
                });
                await WriteJsonAsync(zip, "logs.json", logs, cancellationToken).ConfigureAwait(false);
                var metadata = request.Metadata.Where(x => AllowedMetadata.Contains(x.Key)).ToDictionary(x => x.Key, x => SafeMetadata(x.Value), StringComparer.OrdinalIgnoreCase);
                await WriteJsonAsync(zip, "metadata.json", metadata, cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, final);
            return final;
        }
        catch
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            throw;
        }
    }

    private static string SafeMetadata(string value) => value is not null && SafeMetadataPattern().IsMatch(value) ? value : "[REDACTED]";

    private static async Task WriteJsonAsync<T>(ZipArchive zip, string name, T value, CancellationToken cancellationToken)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await JsonSerializer.SerializeAsync(stream, value, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteTextAsync(ZipArchive zip, string name, string value, CancellationToken cancellationToken)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open(); await using var writer = new StreamWriter(stream);
        cancellationToken.ThrowIfCancellationRequested(); await writer.WriteAsync(value).ConfigureAwait(false);
    }

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeMetadataPattern();
}
