using System.Text.Json;
using BaiduBatchTransfer.App.Features.Acceptance;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class BaiduAcceptanceProbeSessionTests
{
    [Fact]
    public void CompletesOnlyOfficialJsonResponsesAndDiscardsCorrelationState()
    {
        const string secret = "private-file-id-112233";
        var session = new BaiduAcceptanceProbeSession();

        session.ObserveRequest("request-secret", new Uri($"https://pan.baidu.com/api/list?fsid={secret}"), "GET");
        session.ObserveResponse("request-secret", 200, "application/json");
        Assert.True(session.ShouldInspectBody("request-secret"));

        session.Complete("request-secret", $"{{\"errno\":0,\"fs_id\":\"{secret}\"}}");

        var evidence = Assert.Single(session.Snapshot());
        Assert.Equal(["errno", "fs_id"], evidence.JsonPropertyNames);
        Assert.False(session.ShouldInspectBody("request-secret"));
        var serialized = BaiduAcceptanceEvidenceDocument.Serialize(session.Snapshot());
        Assert.DoesNotContain(secret, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("request-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("fsid", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void IgnoresForeignNonJsonAndUnknownResponses()
    {
        var session = new BaiduAcceptanceProbeSession();
        session.ObserveRequest("foreign", new Uri("https://example.com/api"), "POST");
        session.ObserveResponse("foreign", 200, "application/json");
        session.Complete("foreign", "{\"secret\":1}");
        session.ObserveResponse("unknown", 200, "application/json");
        session.Complete("unknown", "{\"secret\":1}");
        session.ObserveRequest("html", new Uri("https://pan.baidu.com/home"), "GET");
        session.ObserveResponse("html", 200, "text/html");
        session.Complete("html", "<html>secret</html>");

        Assert.Empty(session.Snapshot());
    }

    [Fact]
    public void RejectsWriteCapableRequestsBeforeAnyResponseBodyCanBeObserved()
    {
        var session = new BaiduAcceptanceProbeSession();
        session.ObserveRequest("write", new Uri("https://pan.baidu.com/api/create"), "POST");
        session.ObserveResponse("write", 200, "application/json");
        session.Complete("write", "{\"secret\":1}");

        Assert.False(session.ShouldInspectBody("write"));
        Assert.Empty(session.Snapshot());
    }

    [Fact]
    public void DeduplicatesEquivalentShapesAndBoundsTheEvidenceCount()
    {
        var session = new BaiduAcceptanceProbeSession(maximumEvidenceCount: 2);
        Complete(session, "one", "/api/one", "{\"errno\":0}");
        Complete(session, "duplicate", "/api/one", "{\"errno\":1}");
        Complete(session, "two", "/api/two", "{\"data\":{}}");
        Complete(session, "three", "/api/three", "{\"ignored\":true}");

        var evidence = session.Snapshot();
        Assert.Equal(2, evidence.Count);
        Assert.Equal(["/segments/2", "/segments/2"], evidence.Select(item => item.Path));
    }

    [Fact]
    public void EvidenceDocumentHasAStableNonsecretSchema()
    {
        var json = BaiduAcceptanceEvidenceDocument.Serialize(
        [
            new("pan.baidu.com", "/api/list", "POST", 200, ["errno", "list"]),
        ]);
        using var document = JsonDocument.Parse(json);

        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("shape-only", document.RootElement.GetProperty("privacyMode").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("evidence").GetArrayLength());
        Assert.DoesNotContain("cookie", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("authorization", json, StringComparison.OrdinalIgnoreCase);
    }

    private static void Complete(
        BaiduAcceptanceProbeSession session,
        string requestId,
        string path,
        string body)
    {
        session.ObserveRequest(requestId, new Uri("https://pan.baidu.com" + path), "GET");
        session.ObserveResponse(requestId, 200, "application/json; charset=utf-8");
        session.Complete(requestId, body);
    }
}
