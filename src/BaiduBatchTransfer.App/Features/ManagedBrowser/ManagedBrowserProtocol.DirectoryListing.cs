using System.Text.Json;

namespace BaiduBatchTransfer.App.Features.ManagedBrowser;

public static partial class ManagedBrowserProtocol
{
    private static ManagedBrowserRequestPayload ParseReadTransferDirectoryRequest(JsonElement value)
    {
        const ManagedBrowserProtocolError error = ManagedBrowserProtocolError.InvalidPayload;
        ValidateObject(value, ["browserId", "targetId", "devToolsEndpoint", "timeoutMilliseconds", "sourceUri",
            "expectedBaiduUserId", "expectedParentExactId", "expectedCanonicalPath"], error);
        try
        {
            if (!Uri.TryCreate(RequireString(value, "devToolsEndpoint", error), UriKind.Absolute, out var endpoint)) throw Failure(error);
            return new ManagedBrowserReadTransferDirectoryRequest(RequireGuid(value, "browserId", error),
                RequireString(value, "targetId", error), endpoint, RequireInt32(value, "timeoutMilliseconds", error),
                RequireString(value, "sourceUri", error), RequireString(value, "expectedBaiduUserId", error),
                value.GetProperty("expectedParentExactId").ValueKind == JsonValueKind.Null ? null : RequireString(value, "expectedParentExactId", error),
                value.GetProperty("expectedCanonicalPath").ValueKind == JsonValueKind.Null ? null : RequireString(value, "expectedCanonicalPath", error));
        }
        catch (ArgumentException) { throw Failure(error); }
    }

    private static ManagedBrowserResult ParseDirectoryListingResult(JsonElement value)
    {
        const ManagedBrowserProtocolError error = ManagedBrowserProtocolError.InvalidPayload;
        var text = RequireString(value, "kind", error);
        if (!Enum.TryParse(text, false, out ManagedBrowserDirectoryListingResultKind kind) || !Enum.IsDefined(kind) || kind.ToString() != text) throw Failure(error);
        ValidateObject(value, kind == ManagedBrowserDirectoryListingResultKind.Observed
            ? ["kind", "baiduUserId", "generationToken", "parentExactId", "canonicalPath", "ancestorExactIds", "totalCount", "reachedEnd", "isStable", "items", "observationFingerprint"]
            : ["kind"], error);
        if (kind != ManagedBrowserDirectoryListingResultKind.Observed) return new ManagedBrowserDirectoryListingResult(kind);
        if (value.GetProperty("reachedEnd").ValueKind != JsonValueKind.True || value.GetProperty("isStable").ValueKind != JsonValueKind.True) throw Failure(error);
        var array = value.GetProperty("items"); var ancestors = value.GetProperty("ancestorExactIds");
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 32 || ancestors.ValueKind != JsonValueKind.Array || ancestors.GetArrayLength() is < 1 or > 33) throw Failure(error);
        try
        {
            var items = new List<ManagedBrowserSourceItem>();
            foreach (var item in array.EnumerateArray())
            {
                ValidateObject(item, ["exactId", "kind", "serverName"], error);
                if (RequireString(item, "kind", error) != "Folder") throw Failure(error);
                items.Add(new ManagedBrowserSourceItem(RequireString(item, "exactId", error), ManagedBrowserSourceItemKind.Folder, RequireString(item, "serverName", error)));
            }
            return new ManagedBrowserDirectoryListingResult(kind, new ManagedBrowserDirectorySnapshot(
                RequireString(value, "baiduUserId", error), RequireString(value, "generationToken", error),
                RequireString(value, "parentExactId", error), RequireString(value, "canonicalPath", error),
                RequireStringArray(value, "ancestorExactIds", error), RequireInt32(value, "totalCount", error), true, true,
                items, RequireString(value, "observationFingerprint", error)));
        }
        catch (ArgumentException) { throw Failure(error); }
    }

    private static void WriteDirectoryListing(Utf8JsonWriter writer, ManagedBrowserDirectoryListingResult result)
    {
        writer.WriteString("kind", result.Kind.ToString());
        if (result.Snapshot is not { } snapshot) return;
        writer.WriteString("baiduUserId", snapshot.BaiduUserId); writer.WriteString("generationToken", snapshot.GenerationToken);
        writer.WriteString("parentExactId", snapshot.ParentExactId); writer.WriteString("canonicalPath", snapshot.CanonicalPath);
        writer.WritePropertyName("ancestorExactIds"); writer.WriteStartArray();
        foreach (var ancestor in snapshot.AncestorExactIds) writer.WriteStringValue(ancestor);
        writer.WriteEndArray(); writer.WriteNumber("totalCount", snapshot.TotalCount);
        writer.WriteBoolean("reachedEnd", true); writer.WriteBoolean("isStable", true);
        writer.WritePropertyName("items"); writer.WriteStartArray();
        foreach (var item in snapshot.Items)
        {
            writer.WriteStartObject(); writer.WriteString("exactId", item.ExactId); writer.WriteString("kind", "Folder");
            writer.WriteString("serverName", item.ServerName); writer.WriteEndObject();
        }
        writer.WriteEndArray(); writer.WriteString("observationFingerprint", snapshot.ObservationFingerprint);
    }
}
