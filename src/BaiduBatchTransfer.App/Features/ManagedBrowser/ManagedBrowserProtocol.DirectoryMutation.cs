using System.Text.Json;

namespace BaiduBatchTransfer.App.Features.ManagedBrowser;

public static partial class ManagedBrowserProtocol
{
    private static ManagedBrowserRequestPayload ParseOpenTransferDirectoryRootRequest(JsonElement value)
    {
        const ManagedBrowserProtocolError error = ManagedBrowserProtocolError.InvalidPayload;
        ValidateObject(value, ["browserId", "targetId", "devToolsEndpoint", "timeoutMilliseconds", "sourceUri",
            "expectedBaiduUserId", "expectedDirectory", "rootBreadcrumbExactId"], error);
        try
        {
            if (!Uri.TryCreate(RequireString(value, "devToolsEndpoint", error), UriKind.Absolute, out var endpoint)) throw Failure(error);
            var binding = new ManagedBrowserOpenTransferSourceRequest(RequireGuid(value, "browserId", error),
                RequireString(value, "targetId", error), endpoint, RequireInt32(value, "timeoutMilliseconds", error),
                RequireString(value, "sourceUri", error), RequireString(value, "expectedBaiduUserId", error));
            var listing = (ManagedBrowserDirectoryListingResult)ParseDirectoryListingResult(
                RequireNestedObject(value, "expectedDirectory", error));
            if (listing.Snapshot is null) throw Failure(error);
            return ManagedBrowserDirectoryMutationRequest.ForOpenRoot(binding, listing.Snapshot,
                RequireString(value, "rootBreadcrumbExactId", error));
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        { throw Failure(error); }
    }

    private static ManagedBrowserRequestPayload ParseEnterTransferDirectoryRequest(JsonElement value)
    {
        const ManagedBrowserProtocolError error = ManagedBrowserProtocolError.InvalidPayload;
        ValidateObject(value, ["browserId", "targetId", "devToolsEndpoint", "timeoutMilliseconds", "sourceUri",
            "expectedBaiduUserId", "expectedDirectory", "childExactId", "childCanonicalPath"], error);
        try
        {
            if (!Uri.TryCreate(RequireString(value, "devToolsEndpoint", error), UriKind.Absolute, out var endpoint)) throw Failure(error);
            var binding = new ManagedBrowserOpenTransferSourceRequest(RequireGuid(value, "browserId", error),
                RequireString(value, "targetId", error), endpoint, RequireInt32(value, "timeoutMilliseconds", error),
                RequireString(value, "sourceUri", error), RequireString(value, "expectedBaiduUserId", error));
            var listing = (ManagedBrowserDirectoryListingResult)ParseDirectoryListingResult(
                RequireNestedObject(value, "expectedDirectory", error));
            if (listing.Snapshot is null) throw Failure(error);
            return ManagedBrowserDirectoryMutationRequest.ForEnterDirectory(binding, listing.Snapshot,
                RequireString(value, "childExactId", error), RequireString(value, "childCanonicalPath", error));
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        { throw Failure(error); }
    }

    private static ManagedBrowserRequestPayload ParseBeginCreateTransferDirectoryRequest(JsonElement value)
        => ParseCreateTransferDirectoryRequest(value, ManagedBrowserAction.BeginCreateTransferDirectory);

    private static ManagedBrowserRequestPayload ParseConfirmCreateTransferDirectoryRequest(JsonElement value)
        => ParseCreateTransferDirectoryRequest(value, ManagedBrowserAction.ConfirmCreateTransferDirectory);

    private static ManagedBrowserRequestPayload ParseCreateTransferDirectoryRequest(JsonElement value, ManagedBrowserAction action)
    {
        const ManagedBrowserProtocolError error = ManagedBrowserProtocolError.InvalidPayload;
        ValidateObject(value, ["browserId", "targetId", "devToolsEndpoint", "timeoutMilliseconds", "sourceUri",
            "expectedBaiduUserId", "expectedDirectory", "newSegment"], error);
        try
        {
            if (!Uri.TryCreate(RequireString(value, "devToolsEndpoint", error), UriKind.Absolute, out var endpoint)) throw Failure(error);
            var binding = new ManagedBrowserOpenTransferSourceRequest(RequireGuid(value, "browserId", error), RequireString(value, "targetId", error), endpoint,
                RequireInt32(value, "timeoutMilliseconds", error), RequireString(value, "sourceUri", error), RequireString(value, "expectedBaiduUserId", error));
            var listing = (ManagedBrowserDirectoryListingResult)ParseDirectoryListingResult(
                RequireNestedObject(value, "expectedDirectory", error));
            if (listing.Snapshot is null) throw Failure(error);
            return action == ManagedBrowserAction.BeginCreateTransferDirectory
                ? ManagedBrowserDirectoryMutationRequest.ForBeginCreate(binding, listing.Snapshot, RequireString(value, "newSegment", error))
                : ManagedBrowserDirectoryMutationRequest.ForConfirmCreate(binding, listing.Snapshot, RequireString(value, "newSegment", error));
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException) { throw Failure(error); }
    }

    private static ManagedBrowserRequestPayload ParseCancelTransferPickerRequest(JsonElement value)
    {
        const ManagedBrowserProtocolError error = ManagedBrowserProtocolError.InvalidPayload;
        ValidateObject(value, ["browserId", "targetId", "devToolsEndpoint", "timeoutMilliseconds", "sourceUri", "expectedBaiduUserId", "expectedDirectory"], error);
        try
        {
            if (!Uri.TryCreate(RequireString(value, "devToolsEndpoint", error), UriKind.Absolute, out var endpoint)) throw Failure(error);
            var binding = new ManagedBrowserOpenTransferSourceRequest(RequireGuid(value, "browserId", error), RequireString(value, "targetId", error), endpoint,
                RequireInt32(value, "timeoutMilliseconds", error), RequireString(value, "sourceUri", error), RequireString(value, "expectedBaiduUserId", error));
            var listing = (ManagedBrowserDirectoryListingResult)ParseDirectoryListingResult(
                RequireNestedObject(value, "expectedDirectory", error));
            if (listing.Snapshot is null) throw Failure(error);
            return ManagedBrowserDirectoryMutationRequest.ForCancelPicker(binding, listing.Snapshot);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException) { throw Failure(error); }
    }

    private static ManagedBrowserRequestPayload ParseObserveTransferPickerClosedRequest(JsonElement value)
    {
        const ManagedBrowserProtocolError error = ManagedBrowserProtocolError.InvalidPayload;
        ValidateObject(value, ["browserId", "targetId", "devToolsEndpoint", "timeoutMilliseconds", "sourceUri", "expectedBaiduUserId", "expectedSource"], error);
        try
        {
            if (!Uri.TryCreate(RequireString(value, "devToolsEndpoint", error), UriKind.Absolute, out var endpoint)) throw Failure(error);
            var binding = new ManagedBrowserOpenTransferSourceRequest(RequireGuid(value, "browserId", error), RequireString(value, "targetId", error), endpoint,
                RequireInt32(value, "timeoutMilliseconds", error), RequireString(value, "sourceUri", error), RequireString(value, "expectedBaiduUserId", error));
            var listing = (ManagedBrowserSourceListingResult)ParseSourceListingResult(
                RequireNestedObject(value, "expectedSource", error));
            if (listing.Snapshot is null) throw Failure(error);
            return new ManagedBrowserObserveTransferPickerClosedRequest(binding, listing.Snapshot);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException) { throw Failure(error); }
    }

    private static ManagedBrowserResult ParseDirectoryMutationResult(ManagedBrowserAction action, JsonElement value)
    {
        const ManagedBrowserProtocolError error = ManagedBrowserProtocolError.InvalidPayload;
        ValidateObject(value, ["kind"], error);
        var text = RequireString(value, "kind", error);
        if (!Enum.TryParse(text, false, out ManagedBrowserDirectoryMutationResultKind kind) ||
            !Enum.IsDefined(kind) || kind.ToString() != text)
            throw Failure(error);
        var success = action switch
        {
            ManagedBrowserAction.OpenTransferDirectoryRoot => ManagedBrowserDirectoryMutationResultKind.RootOpened,
            ManagedBrowserAction.EnterTransferDirectory => ManagedBrowserDirectoryMutationResultKind.DirectoryEntered,
            ManagedBrowserAction.BeginCreateTransferDirectory => ManagedBrowserDirectoryMutationResultKind.CreateDialogOpened,
            ManagedBrowserAction.ConfirmCreateTransferDirectory => ManagedBrowserDirectoryMutationResultKind.DirectoryCreated,
            ManagedBrowserAction.CancelTransferPicker => ManagedBrowserDirectoryMutationResultKind.PickerCancelled,
            _ => throw Failure(error),
        };
        if ((kind is ManagedBrowserDirectoryMutationResultKind.RootOpened or
            ManagedBrowserDirectoryMutationResultKind.DirectoryEntered or
            ManagedBrowserDirectoryMutationResultKind.CreateDialogOpened or
            ManagedBrowserDirectoryMutationResultKind.DirectoryCreated or
            ManagedBrowserDirectoryMutationResultKind.PickerCancelled) && kind != success)
            throw Failure(error);
        return new ManagedBrowserDirectoryMutationResult(kind);
    }

    private static JsonElement RequireNestedObject(
        JsonElement value, string name, ManagedBrowserProtocolError error)
    {
        var nested = value.GetProperty(name);
        if (nested.ValueKind != JsonValueKind.Object) throw Failure(error);
        return nested;
    }

    private static ManagedBrowserResult ParseTransferPickerClosedResult(JsonElement value)
    {
        const ManagedBrowserProtocolError error = ManagedBrowserProtocolError.InvalidPayload;
        ValidateObject(value, ["kind"], error);
        var text = RequireString(value, "kind", error);
        if (!Enum.TryParse(text, false, out ManagedBrowserTransferPickerClosedResultKind kind) ||
            !Enum.IsDefined(kind) || kind.ToString() != text) throw Failure(error);
        return new ManagedBrowserTransferPickerClosedResult(kind);
    }

    private static void WriteDirectoryMutationPayload(Utf8JsonWriter writer, ManagedBrowserDirectoryMutationRequest request)
    {
        WriteSourceBinding(writer, request.Binding);
        writer.WritePropertyName("expectedDirectory");
        writer.WriteStartObject();
        WriteDirectoryListing(writer, new ManagedBrowserDirectoryListingResult(
            ManagedBrowserDirectoryListingResultKind.Observed, request.ExpectedDirectory));
        writer.WriteEndObject();
        if (request.Action == ManagedBrowserAction.OpenTransferDirectoryRoot)
            writer.WriteString("rootBreadcrumbExactId", request.RootBreadcrumbExactId);
        else if (request.Action == ManagedBrowserAction.EnterTransferDirectory)
        {
            writer.WriteString("childExactId", request.ChildExactId);
            writer.WriteString("childCanonicalPath", request.ChildCanonicalPath);
        }
        else if (request.Action is ManagedBrowserAction.BeginCreateTransferDirectory or ManagedBrowserAction.ConfirmCreateTransferDirectory)
            writer.WriteString("newSegment", request.NewSegment);
    }
}
