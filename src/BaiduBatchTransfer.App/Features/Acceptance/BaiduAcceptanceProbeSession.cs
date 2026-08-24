using System.Text.Json;

namespace BaiduBatchTransfer.App.Features.Acceptance;

public sealed class BaiduAcceptanceProbeSession
{
    private const int MaximumPendingRequestCount = 512;
    private readonly object gate = new();
    private readonly int maximumEvidenceCount;
    private readonly Dictionary<string, PendingRequest> pending = new(StringComparer.Ordinal);
    private readonly List<BaiduAcceptanceEvidence> evidence = [];
    private readonly HashSet<string> evidenceKeys = new(StringComparer.Ordinal);

    public BaiduAcceptanceProbeSession(int maximumEvidenceCount = 256)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumEvidenceCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumEvidenceCount, 1_000);
        this.maximumEvidenceCount = maximumEvidenceCount;
    }

    public void ObserveRequest(string requestId, Uri destination, string? method)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentNullException.ThrowIfNull(destination);
        var shape = BaiduAcceptanceEvidenceSanitizer.TryCreate(destination, method, 0, null);
        if (shape is null) return;
        lock (gate)
        {
            if (pending.Count >= MaximumPendingRequestCount) return;
            pending[requestId] = new(shape, null, false);
        }
    }

    public void ObserveResponse(string requestId, int statusCode, string? mimeType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        lock (gate)
        {
            if (!pending.TryGetValue(requestId, out var request)) return;
            pending[requestId] = request with
            {
                StatusCode = statusCode is >= 100 and <= 599 ? statusCode : null,
                IsJson = IsJsonMimeType(mimeType),
            };
        }
    }

    public bool ShouldInspectBody(string requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        lock (gate) return pending.TryGetValue(requestId, out var request) && request.IsJson;
    }

    public void Complete(string requestId, string? responseBody)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        lock (gate)
        {
            if (!pending.Remove(requestId, out var request) || !request.IsJson ||
                evidence.Count >= maximumEvidenceCount)
            {
                return;
            }

            var completed = request.Shape with
            {
                StatusCode = request.StatusCode,
                JsonPropertyNames = BaiduAcceptanceEvidenceSanitizer.SafeJsonPropertyNames(responseBody),
            };
            var key = string.Join('\n',
                completed.Host,
                completed.Path,
                completed.Method,
                completed.StatusCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                string.Join(',', completed.JsonPropertyNames));
            if (evidenceKeys.Add(key)) evidence.Add(completed);
        }
    }

    public IReadOnlyList<BaiduAcceptanceEvidence> Snapshot()
    {
        lock (gate) return evidence.ToArray();
    }

    private static bool IsJsonMimeType(string? mimeType) =>
        !string.IsNullOrWhiteSpace(mimeType) &&
        (mimeType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) ||
         mimeType.EndsWith("+json", StringComparison.OrdinalIgnoreCase));

    private sealed record PendingRequest(
        BaiduAcceptanceEvidence Shape,
        int? StatusCode,
        bool IsJson);
}

public static class BaiduAcceptanceEvidenceDocument
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
    };

    public static string Serialize(IReadOnlyList<BaiduAcceptanceEvidence> evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            privacyMode = "shape-only",
            evidence,
        }, Options);
    }
}
