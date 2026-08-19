using System.IO.Compression;
using System.Text.Json;

namespace BaiduBatchTransfer.Infrastructure.Database;

public sealed record DiagnosticLogEntry(string Stage, string Level, string EventCode, string SanitizedMessage);
public sealed record DiagnosticExportRequest(
    string AppVersion,
    int SchemaVersion,
    IReadOnlyList<string> IntegrityOutput,
    IReadOnlyList<DiagnosticLogEntry> Logs,
    IReadOnlyDictionary<string, string> Metadata);

public sealed class DiagnosticExportService
{
    private static readonly HashSet<string> AllowedMetadata = new(StringComparer.OrdinalIgnoreCase)
    {
        "os", "architecture", "runtime", "appMode"
    };
    private static readonly IReadOnlyDictionary<string, string> Stages = CanonicalValues(
        "Import", "Precheck", "Allocation", "Transfer", "Confirmation", "Database", "Recovery", "Http");
    private static readonly IReadOnlyDictionary<string, string> Levels = CanonicalValues(
        "Trace", "Debug", "Information", "Warning", "Error", "Critical");
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
                await WriteJsonAsync(zip, "manifest.json", new { AppVersion = NormalizeVersion(request.AppVersion), request.SchemaVersion, CreatedAt = DateTimeOffset.UtcNow }, cancellationToken).ConfigureAwait(false);
                await WriteTextAsync(zip, "integrity.txt", string.Join('\n', request.IntegrityOutput.Select(x =>
                    string.Equals(x, "ok", StringComparison.OrdinalIgnoreCase) ? "ok" : "non-ok")), cancellationToken).ConfigureAwait(false);
                var logs = request.Logs.Select((x, index) => new
                {
                    Sequence = index + 1,
                    Stage = SemanticValue(x.Stage, Stages),
                    Level = SemanticValue(x.Level, Levels),
                    EventCodeOmitted = true,
                    MessageOmitted = true
                });
                await WriteJsonAsync(zip, "logs.json", logs, cancellationToken).ConfigureAwait(false);
                var metadata = request.Metadata.Where(x => AllowedMetadata.Contains(x.Key)).ToDictionary(x => x.Key, x => NormalizeMetadata(x.Key, x.Value), StringComparer.OrdinalIgnoreCase);
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

    private static IReadOnlyDictionary<string, string> CanonicalValues(params string[] values) =>
        values.ToDictionary(x => x, StringComparer.OrdinalIgnoreCase);

    private static string SemanticValue(string? value, IReadOnlyDictionary<string, string> allowed) =>
        value is not null && allowed.TryGetValue(value, out var canonical) ? canonical : "Other";

    private static string NormalizeVersion(string? value) =>
        Version.TryParse(value, out var version) ? version.ToString() : "unknown";

    private static string NormalizeMetadata(string key, string? value)
    {
        return key.ToLowerInvariant() switch
        {
            "os" => SemanticValue(value, CanonicalValues("Windows", "Linux", "macOS")),
            "architecture" => SemanticValue(value, CanonicalValues("X86", "X64", "Arm", "Arm64")),
            "runtime" => NormalizeVersion(value) is var runtime && runtime != "unknown" ? runtime : "Other",
            "appmode" => SemanticValue(value, CanonicalValues("Desktop", "Headless")),
            _ => "Other"
        };
    }

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
}
