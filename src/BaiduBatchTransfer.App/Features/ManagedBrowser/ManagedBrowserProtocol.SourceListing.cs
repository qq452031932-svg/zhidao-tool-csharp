using System.Text.Json;

namespace BaiduBatchTransfer.App.Features.ManagedBrowser;

public static partial class ManagedBrowserProtocol
{
    private static ManagedBrowserRequestPayload ParseReadTransferSourceRequest(JsonElement value) =>
        new ManagedBrowserReadTransferSourceRequest((ManagedBrowserOpenTransferSourceRequest)ParseOpenTransferSourceRequest(value));

    private static ManagedBrowserResult ParseSourceListingResult(JsonElement value)
    {
        const ManagedBrowserProtocolError error = ManagedBrowserProtocolError.InvalidPayload;
        var text = RequireString(value, "kind", error);
        if (!Enum.TryParse(text, false, out ManagedBrowserSourceListingResultKind kind) || !Enum.IsDefined(kind) || kind.ToString() != text)
            throw Failure(error);
        ValidateObject(value, kind == ManagedBrowserSourceListingResultKind.Observed
            ? ["kind", "baiduUserId", "generationToken", "totalCount", "reachedEnd", "isStable", "items", "observationFingerprint"]
            : ["kind"], error);
        if (kind != ManagedBrowserSourceListingResultKind.Observed) return new ManagedBrowserSourceListingResult(kind);
        if (value.GetProperty("reachedEnd").ValueKind != JsonValueKind.True || value.GetProperty("isStable").ValueKind != JsonValueKind.True)
            throw Failure(error);
        var array = value.GetProperty("items");
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() is < 1 or > 32) throw Failure(error);
        try
        {
            var items = new List<ManagedBrowserSourceItem>();
            foreach (var item in array.EnumerateArray())
            {
                ValidateObject(item, ["exactId", "kind", "serverName"], error);
                var itemText = RequireString(item, "kind", error);
                if (!Enum.TryParse(itemText, false, out ManagedBrowserSourceItemKind itemKind) || !Enum.IsDefined(itemKind) || itemKind.ToString() != itemText)
                    throw Failure(error);
                items.Add(new ManagedBrowserSourceItem(RequireString(item, "exactId", error), itemKind, RequireString(item, "serverName", error)));
            }
            return new ManagedBrowserSourceListingResult(kind, new ManagedBrowserSourceSnapshot(
                RequireString(value, "baiduUserId", error), RequireString(value, "generationToken", error),
                RequireInt32(value, "totalCount", error), true, true, items, RequireString(value, "observationFingerprint", error)));
        }
        catch (ArgumentException) { throw Failure(error); }
    }

    private static void WriteSourceBinding(Utf8JsonWriter writer, ManagedBrowserOpenTransferSourceRequest source)
    {
        writer.WriteString("browserId", source.BrowserId);
        writer.WriteString("targetId", source.TargetId);
        writer.WriteString("devToolsEndpoint", source.DevToolsEndpoint.AbsoluteUri);
        writer.WriteNumber("timeoutMilliseconds", source.TimeoutMilliseconds);
        writer.WriteString("sourceUri", source.SourceUri);
        writer.WriteString("expectedBaiduUserId", source.ExpectedBaiduUserId);
    }

    private static void WriteSourceListing(Utf8JsonWriter writer, ManagedBrowserSourceListingResult result)
    {
        writer.WriteString("kind", result.Kind.ToString());
        if (result.Snapshot is not { } snapshot) return;
        writer.WriteString("baiduUserId", snapshot.BaiduUserId);
        writer.WriteString("generationToken", snapshot.GenerationToken);
        writer.WriteNumber("totalCount", snapshot.TotalCount);
        writer.WriteBoolean("reachedEnd", true);
        writer.WriteBoolean("isStable", true);
        writer.WritePropertyName("items");
        writer.WriteStartArray();
        foreach (var item in snapshot.Items)
        {
            writer.WriteStartObject();
            writer.WriteString("exactId", item.ExactId);
            writer.WriteString("kind", item.Kind.ToString());
            writer.WriteString("serverName", item.ServerName);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteString("observationFingerprint", snapshot.ObservationFingerprint);
    }
}
