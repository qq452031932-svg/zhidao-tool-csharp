using System.Text.Json;
using System.Text.RegularExpressions;

namespace BaiduBatchTransfer.App.Features.ManagedBrowser;

public enum ManagedBrowserExtractionCodeResultKind
{
    SourceOpened = 1,
    ExplicitlyRejected,
    PageNotValidated,
    AccountIdentityMismatch,
    LoginRequired,
    VerificationRequired,
    BrowserUnavailable,
    TimedOut,
    Cancelled,
    OutcomeUnknown,
}

public enum ManagedBrowserExtractionDispatchState
{
    KnownNotDispatched = 1,
    ClickDispatched,
    ClickOutcomeUnknown,
}

public sealed class ManagedBrowserSubmitExtractionCodeRequest : ManagedBrowserRequestPayload
{
    public ManagedBrowserSubmitExtractionCodeRequest(
        ManagedBrowserOpenTransferSourceRequest binding,
        string fixedCode)
    {
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        if (fixedCode is null || !Regex.IsMatch(fixedCode, "^[A-Za-z0-9]{4}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("The extraction code must be four ASCII letters or digits.", nameof(fixedCode));
        FixedCode = fixedCode;
    }

    public ManagedBrowserOpenTransferSourceRequest Binding { get; }
    public string FixedCode { get; }
    public override string ToString() =>
        "ManagedBrowserSubmitExtractionCodeRequest { Binding = [REDACTED], FixedCode = [REDACTED] }";
}

public sealed class ManagedBrowserSubmitExtractionCodeResult : ManagedBrowserResult
{
    public ManagedBrowserSubmitExtractionCodeResult(
        ManagedBrowserExtractionCodeResultKind kind,
        ManagedBrowserExtractionDispatchState dispatchState)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (!Enum.IsDefined(dispatchState)) throw new ArgumentOutOfRangeException(nameof(dispatchState));
        if (kind == ManagedBrowserExtractionCodeResultKind.SourceOpened &&
            dispatchState != ManagedBrowserExtractionDispatchState.ClickDispatched ||
            kind == ManagedBrowserExtractionCodeResultKind.OutcomeUnknown &&
            dispatchState != ManagedBrowserExtractionDispatchState.ClickOutcomeUnknown)
            throw new ArgumentException("The extraction result does not match its dispatch state.");
        Kind = kind;
        DispatchState = dispatchState;
    }

    public ManagedBrowserExtractionCodeResultKind Kind { get; }
    public ManagedBrowserExtractionDispatchState DispatchState { get; }
}

public static partial class ManagedBrowserProtocol
{
    private static readonly string[] ExtractionRequestFields =
    [
        "browserId", "targetId", "devToolsEndpoint", "timeoutMilliseconds",
        "sourceUri", "expectedBaiduUserId", "fixedCode",
    ];

    private static ManagedBrowserRequestPayload ParseSubmitExtractionCodeRequest(JsonElement value)
    {
        const ManagedBrowserProtocolError error = ManagedBrowserProtocolError.InvalidPayload;
        ValidateObject(value, ExtractionRequestFields, error);
        try
        {
            if (!Uri.TryCreate(RequireString(value, "devToolsEndpoint", error), UriKind.Absolute, out var endpoint))
                throw new ArgumentException("Invalid endpoint.");
            var binding = new ManagedBrowserOpenTransferSourceRequest(
                RequireGuid(value, "browserId", error), RequireString(value, "targetId", error), endpoint,
                RequireInt32(value, "timeoutMilliseconds", error), RequireString(value, "sourceUri", error),
                RequireString(value, "expectedBaiduUserId", error));
            return new ManagedBrowserSubmitExtractionCodeRequest(
                binding, RequireString(value, "fixedCode", error));
        }
        catch (ArgumentException) { throw Failure(error); }
    }

    private static ManagedBrowserResult ParseSubmitExtractionCodeResult(JsonElement value)
    {
        ValidateObject(value, ["kind", "dispatchState"], ManagedBrowserProtocolError.InvalidPayload);
        try
        {
            return new ManagedBrowserSubmitExtractionCodeResult(
                RequireExactEnum<ManagedBrowserExtractionCodeResultKind>(value, "kind"),
                RequireExactEnum<ManagedBrowserExtractionDispatchState>(value, "dispatchState"));
        }
        catch (ArgumentException) { throw Failure(ManagedBrowserProtocolError.InvalidPayload); }
    }

    private static void WriteExtractionRequest(Utf8JsonWriter writer,
        ManagedBrowserSubmitExtractionCodeRequest request)
    {
        WriteSourceBinding(writer, request.Binding);
        writer.WriteString("fixedCode", request.FixedCode);
    }

    private static void WriteExtractionResult(Utf8JsonWriter writer,
        ManagedBrowserSubmitExtractionCodeResult result)
    {
        writer.WriteString("kind", result.Kind.ToString());
        writer.WriteString("dispatchState", result.DispatchState.ToString());
    }
}
