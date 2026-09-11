using System.Text.Json;

namespace BaiduBatchTransfer.App.Features.ManagedBrowser;

public static partial class ManagedBrowserProtocol
{
    private static ManagedBrowserRequestPayload ParseOpenTransferPickerRequest(JsonElement value)
    {
        const ManagedBrowserProtocolError error = ManagedBrowserProtocolError.InvalidPayload;
        ValidateObject(value, ["browserId", "targetId", "devToolsEndpoint", "timeoutMilliseconds",
            "sourceUri", "expectedBaiduUserId", "expectedSource"], error);
        try
        {
            var binding = new ManagedBrowserOpenTransferSourceRequest(
                RequireGuid(value, "browserId", error), RequireString(value, "targetId", error),
                new Uri(RequireString(value, "devToolsEndpoint", error), UriKind.Absolute),
                RequireInt32(value, "timeoutMilliseconds", error), RequireString(value, "sourceUri", error),
                RequireString(value, "expectedBaiduUserId", error));
            var source = (ManagedBrowserSourceListingResult)ParseSourceListingResult(value.GetProperty("expectedSource"));
            if (source.Snapshot is null) throw Failure(error);
            return new ManagedBrowserOpenTransferPickerRequest(binding, source.Snapshot);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        { throw Failure(error); }
    }

    private static ManagedBrowserResult ParseTransferPickerResult(JsonElement value)
    {
        const ManagedBrowserProtocolError error = ManagedBrowserProtocolError.InvalidPayload;
        ValidateObject(value, ["kind"], error);
        var text = RequireString(value, "kind", error);
        if (!Enum.TryParse(text, false, out ManagedBrowserTransferPickerResultKind kind) ||
            !Enum.IsDefined(kind) || text != kind.ToString()) throw Failure(error);
        return new ManagedBrowserTransferPickerResult(kind);
    }
}
