using BaiduBatchTransfer.Core.Domain;
using System.Text.Json;

namespace BaiduBatchTransfer.App.Features.ManagedBrowser;

public static partial class ManagedBrowserProtocol
{
    private static readonly string[] TransferSubmissionContextFields =
    [
        "browserId", "targetId", "devToolsEndpoint", "timeoutMilliseconds", "sourceUri", "baiduUserId",
        "sourceGenerationToken", "sourceObservationFingerprint", "sourceFingerprint",
        "sourceExactId", "sourceItemKind", "selectedTargetExactId",
        "selectedTargetCanonicalPath", "targetBaselineFingerprint", "duplicatePolicy",
    ];

    private static ManagedBrowserRequestPayload ParseObserveTransferSubmissionReadyRequest(JsonElement value)
    {
        ValidateObject(value, TransferSubmissionContextFields, ManagedBrowserProtocolError.InvalidPayload);
        return new ManagedBrowserObserveTransferSubmissionReadyRequest(ParseTransferSubmissionContext(value));
    }

    private static ManagedBrowserRequestPayload ParseSubmitTransferOnceRequest(JsonElement value)
    {
        ValidateObject(value, [.. TransferSubmissionContextFields, "readinessFingerprint"],
            ManagedBrowserProtocolError.InvalidPayload);
        try
        {
            return new ManagedBrowserSubmitTransferOnceRequest(ParseTransferSubmissionContext(value),
                RequireString(value, "readinessFingerprint", ManagedBrowserProtocolError.InvalidPayload));
        }
        catch (ArgumentException) { throw Failure(ManagedBrowserProtocolError.InvalidPayload); }
    }

    private static ManagedBrowserRequestPayload ParseObserveTransferSubmissionOutcomeRequest(JsonElement value)
    {
        ValidateObject(value, [.. TransferSubmissionContextFields, "readinessFingerprint"],
            ManagedBrowserProtocolError.InvalidPayload);
        try
        {
            return new ManagedBrowserObserveTransferSubmissionOutcomeRequest(ParseTransferSubmissionContext(value),
                RequireString(value, "readinessFingerprint", ManagedBrowserProtocolError.InvalidPayload));
        }
        catch (ArgumentException) { throw Failure(ManagedBrowserProtocolError.InvalidPayload); }
    }

    private static ManagedBrowserTransferSubmissionContext ParseTransferSubmissionContext(JsonElement value)
    {
        const ManagedBrowserProtocolError error = ManagedBrowserProtocolError.InvalidPayload;
        try
        {
            if (!Uri.TryCreate(RequireString(value, "devToolsEndpoint", error), UriKind.Absolute, out var endpoint))
                throw new ArgumentException("Invalid endpoint.");
            return new ManagedBrowserTransferSubmissionContext(
                RequireGuid(value, "browserId", error), RequireString(value, "targetId", error), endpoint,
                RequireInt32(value, "timeoutMilliseconds", error), RequireString(value, "sourceUri", error),
                RequireString(value, "baiduUserId", error),
                RequireString(value, "sourceGenerationToken", error),
                RequireString(value, "sourceObservationFingerprint", error),
                RequireString(value, "sourceFingerprint", error), RequireString(value, "sourceExactId", error),
                RequireExactEnum<ManagedBrowserSourceItemKind>(value, "sourceItemKind"),
                RequireString(value, "selectedTargetExactId", error),
                RequireString(value, "selectedTargetCanonicalPath", error),
                RequireString(value, "targetBaselineFingerprint", error),
                RequireExactEnum<DuplicatePolicy>(value, "duplicatePolicy"));
        }
        catch (ArgumentException) { throw Failure(error); }
    }

    private static ManagedBrowserResult ParseTransferSubmissionReadinessResult(JsonElement value)
    {
        const ManagedBrowserProtocolError error = ManagedBrowserProtocolError.InvalidPayload;
        var kind = RequireExactEnum<ManagedBrowserTransferSubmissionReadinessResultKind>(value, "kind");
        if (kind != ManagedBrowserTransferSubmissionReadinessResultKind.Ready)
        {
            ValidateObject(value, ["kind"], error);
            return new ManagedBrowserTransferSubmissionReadinessResult(kind);
        }

        ValidateObject(value,
            ["kind", .. TransferSubmissionContextFields, "eligibleControlCount", "readinessFingerprint"], error);
        try
        {
            var readiness = new ManagedBrowserTransferSubmissionReadiness(
                ParseTransferSubmissionContext(value), RequireInt32(value, "eligibleControlCount", error),
                RequireString(value, "readinessFingerprint", error));
            return new ManagedBrowserTransferSubmissionReadinessResult(kind, readiness);
        }
        catch (ArgumentException) { throw Failure(error); }
    }

    private static ManagedBrowserResult ParseSubmitTransferOnceResult(JsonElement value)
    {
        const ManagedBrowserProtocolError error = ManagedBrowserProtocolError.InvalidPayload;
        ValidateObject(value, ["kind", "dispatchState"], error);
        try
        {
            return new ManagedBrowserSubmitTransferOnceResult(
                RequireExactEnum<ManagedBrowserTransferSubmitResultKind>(value, "kind"),
                RequireExactEnum<ManagedBrowserTransferSubmitDispatchState>(value, "dispatchState"));
        }
        catch (ArgumentException) { throw Failure(error); }
    }

    private static ManagedBrowserResult ParseTransferSubmissionOutcomeResult(JsonElement value)
    {
        const ManagedBrowserProtocolError error = ManagedBrowserProtocolError.InvalidPayload;
        var kind = RequireExactEnum<ManagedBrowserTransferOutcomeKind>(value, "kind");
        if (kind != ManagedBrowserTransferOutcomeKind.AcceptedBound)
        {
            ValidateObject(value, ["kind"], error);
            return new ManagedBrowserTransferSubmissionOutcomeResult(kind);
        }

        ValidateObject(value,
            ["kind", "officialRequestId", "exactTargetId", "sourceExactId", "destinationExactId",
             "officialObservationFingerprint"], error);
        try
        {
            return new ManagedBrowserTransferSubmissionOutcomeResult(kind,
                RequireString(value, "officialRequestId", error), RequireString(value, "exactTargetId", error),
                RequireString(value, "sourceExactId", error), RequireString(value, "destinationExactId", error),
                RequireString(value, "officialObservationFingerprint", error));
        }
        catch (ArgumentException) { throw Failure(error); }
    }

    private static T RequireExactEnum<T>(JsonElement value, string name) where T : struct, Enum
    {
        var text = RequireString(value, name, ManagedBrowserProtocolError.InvalidPayload);
        if (!Enum.TryParse<T>(text, false, out var result) || !Enum.IsDefined(result) || result.ToString() != text)
            throw Failure(ManagedBrowserProtocolError.InvalidPayload);
        return result;
    }

    private static void WriteTransferSubmissionContext(
        Utf8JsonWriter writer, ManagedBrowserTransferSubmissionContext context)
    {
        writer.WriteString("browserId", context.BrowserId);
        writer.WriteString("targetId", context.TargetId);
        writer.WriteString("devToolsEndpoint", context.DevToolsEndpoint.AbsoluteUri);
        writer.WriteNumber("timeoutMilliseconds", context.TimeoutMilliseconds);
        writer.WriteString("sourceUri", context.SourceUri);
        writer.WriteString("baiduUserId", context.BaiduUserId);
        writer.WriteString("sourceGenerationToken", context.SourceGenerationToken);
        writer.WriteString("sourceObservationFingerprint", context.SourceObservationFingerprint);
        writer.WriteString("sourceFingerprint", context.SourceFingerprint);
        writer.WriteString("sourceExactId", context.SourceExactId);
        writer.WriteString("sourceItemKind", context.SourceItemKind.ToString());
        writer.WriteString("selectedTargetExactId", context.SelectedTargetExactId);
        writer.WriteString("selectedTargetCanonicalPath", context.SelectedTargetCanonicalPath);
        writer.WriteString("targetBaselineFingerprint", context.TargetBaselineFingerprint);
        writer.WriteString("duplicatePolicy", context.DuplicatePolicy.ToString());
    }

    private static void WriteTransferSubmissionRequest(
        Utf8JsonWriter writer, ManagedBrowserRequestPayload payload)
    {
        switch (payload)
        {
            case ManagedBrowserObserveTransferSubmissionReadyRequest ready:
                WriteTransferSubmissionContext(writer, ready.Context);
                break;
            case ManagedBrowserSubmitTransferOnceRequest submit:
                WriteTransferSubmissionContext(writer, submit.Context);
                writer.WriteString("readinessFingerprint", submit.ReadinessFingerprint);
                break;
            case ManagedBrowserObserveTransferSubmissionOutcomeRequest outcome:
                WriteTransferSubmissionContext(writer, outcome.Context);
                writer.WriteString("readinessFingerprint", outcome.ReadinessFingerprint);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(payload));
        }
    }

    private static void WriteTransferSubmissionResult(Utf8JsonWriter writer, ManagedBrowserResult result)
    {
        switch (result)
        {
            case ManagedBrowserTransferSubmissionReadinessResult readiness:
                writer.WriteString("kind", readiness.Kind.ToString());
                if (readiness.Readiness is { } evidence)
                {
                    WriteTransferSubmissionContext(writer, evidence.Context);
                    writer.WriteNumber("eligibleControlCount", evidence.EligibleControlCount);
                    writer.WriteString("readinessFingerprint", evidence.ReadinessFingerprint);
                }
                break;
            case ManagedBrowserSubmitTransferOnceResult submit:
                writer.WriteString("kind", submit.Kind.ToString());
                writer.WriteString("dispatchState", submit.DispatchState.ToString());
                break;
            case ManagedBrowserTransferSubmissionOutcomeResult outcome:
                writer.WriteString("kind", outcome.Kind.ToString());
                if (outcome.Kind == ManagedBrowserTransferOutcomeKind.AcceptedBound)
                {
                    writer.WriteString("officialRequestId", outcome.OfficialRequestId);
                    writer.WriteString("exactTargetId", outcome.ExactTargetId);
                    writer.WriteString("sourceExactId", outcome.SourceExactId);
                    writer.WriteString("destinationExactId", outcome.DestinationExactId);
                    writer.WriteString("officialObservationFingerprint", outcome.OfficialObservationFingerprint);
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(result));
        }
    }
}
