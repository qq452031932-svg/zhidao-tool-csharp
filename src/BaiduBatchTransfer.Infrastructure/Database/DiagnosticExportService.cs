using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    private static readonly HashSet<string> SensitiveJsonKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "userId", "accountId", "extractionCode", "accessToken", "token", "Cookie", "Authorization", "BDUSS", "STOKEN",
        "responseBody", "response_body", "body"
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
                await WriteTextAsync(zip, "integrity.txt", string.Join('\n', request.IntegrityOutput.Select(Sanitize)), cancellationToken).ConfigureAwait(false);
                var logs = request.Logs.Select(x => new { Stage = SafeMetadata(x.Stage), Level = SafeMetadata(x.Level), EventCode = SafeMetadata(x.EventCode), SanitizedMessage = Sanitize(x.SanitizedMessage) });
                await WriteJsonAsync(zip, "logs.json", logs, cancellationToken).ConfigureAwait(false);
                var metadata = request.Metadata.Where(x => AllowedMetadata.Contains(x.Key)).ToDictionary(x => x.Key, x => Sanitize(x.Value), StringComparer.OrdinalIgnoreCase);
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

    private static string Sanitize(string value)
    {
        var result = SanitizeJson(value ?? string.Empty);
        result = ResponseBody().Replace(result, "[REDACTED]");
        result = AnyUrl().Replace(result, "[REDACTED_URL]");
        result = WindowsPath().Replace(result, "[REDACTED_PATH]");
        result = SensitiveJsonField().Replace(result, "$1[REDACTED]");
        result = Cookie().Replace(result, "$1[REDACTED]");
        result = Authorization().Replace(result, "$1[REDACTED]");
        result = SessionToken().Replace(result, "$1[REDACTED]");
        result = UserId().Replace(result, "$1[REDACTED]");
        result = ExtractionCode().Replace(result, "$1[REDACTED]");
        result = GuidIdentifier().Replace(result, "[REDACTED_ID]");
        return result;
    }

    private static string SanitizeJson(string value)
    {
        try
        {
            var node = JsonNode.Parse(value);
            if (node is null) return value;
            RedactNode(node);
            return node.ToJsonString();
        }
        catch (JsonException) { return value; }
    }

    private static void RedactNode(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(x => x.Key).ToArray())
            {
                if (SensitiveJsonKeys.Contains(key)) obj[key] = "[REDACTED]";
                else if (obj[key] is { } child) RedactNode(child);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array.OfType<JsonNode>()) RedactNode(child);
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

    [GeneratedRegex("(?i)response\\s*body\\s*:[^\\r\\n]*")]
    private static partial Regex ResponseBody();
    [GeneratedRegex("(?i)[A-Z][A-Z0-9+.-]*://[^\\s\"'<>]+")]
    private static partial Regex AnyUrl();
    [GeneratedRegex("(?i)(?:\\b[A-Z]:\\\\|\\\\\\\\)[^\"'\\r\\n]+")]
    private static partial Regex WindowsPath();
    [GeneratedRegex("(?i)([\"']?(?:userid|accountid|extractioncode|accesstoken|token|cookie|authorization|bduss|stoken)[\"']?\\s*:\\s*)(?:\\[[^\\]\\r\\n]*\\]|[\"'][^\"'\\r\\n]*[\"']|[^,}\\r\\n]+)")]
    private static partial Regex SensitiveJsonField();
    [GeneratedRegex("(?i)([\"']?cookie[\"']?\\s*[=:]\\s*(?:\\[\\s*)?[\"']?)[^\"'\\r\\n}]+")]
    private static partial Regex Cookie();
    [GeneratedRegex("(?i)([\"']?authorization[\"']?\\s*[=:]\\s*(?:\\[\\s*)?[\"']?)[^\"'\\r\\n}]+")]
    private static partial Regex Authorization();
    [GeneratedRegex("(?i)((?:bduss|stoken)\\s*[=:]\\s*)[^\\s,;]+")]
    private static partial Regex SessionToken();
    [GeneratedRegex("(?i)((?:baidu)?user_?id\\s*[=:]\\s*)[^\\s,;]+")]
    private static partial Regex UserId();
    [GeneratedRegex("(?i)((?:pwd|code|extractioncode|提取码)\\s*[=:：]\\s*)[a-z0-9]{4,}")]
    private static partial Regex ExtractionCode();
    [GeneratedRegex("(?i)\\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\\b")]
    private static partial Regex GuidIdentifier();
    [GeneratedRegex("^[A-Za-z0-9_.-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeMetadataPattern();
}
