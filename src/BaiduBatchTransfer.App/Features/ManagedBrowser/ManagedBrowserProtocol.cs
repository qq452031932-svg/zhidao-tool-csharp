using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.App.Features.ManagedBrowser;

public enum ManagedBrowserProtocolError
{
    FrameSizeInvalid = 1,
    UnexpectedEof = 2,
    MalformedUtf8 = 3,
    MalformedJson = 4,
    DuplicateField = 5,
    UnknownField = 6,
    ForbiddenField = 7,
    ProtocolVersionMismatch = 8,
    ExecutionEngineVersionMismatch = 9,
    UnknownAction = 10,
    InvalidMessage = 11,
    InvalidPayload = 12,
    CapabilityMismatch = 13,
    ResponseIdentityMismatch = 14,
}

public sealed class ManagedBrowserProtocolException : Exception
{
    public ManagedBrowserProtocolException(ManagedBrowserProtocolError error)
        : base($"Managed browser helper protocol failure: {error}.") => Error = error;

    public ManagedBrowserProtocolError Error { get; }

    public override string ToString() =>
        $"ManagedBrowserProtocolException {{ Error = {Error} }}";
}

public enum ManagedBrowserAction
{
    Handshake = 1,
    Health = 2,
    Shutdown = 3,
    ObserveAccountIdentity = 4,
    OpenTransferSource = 5,
    ReadTransferSource = 6,
    ReadTransferDirectory = 7,
    OpenTransferPicker = 8,
    OpenTransferDirectoryRoot = 9,
    EnterTransferDirectory = 10,
    BeginCreateTransferDirectory = 11,
    ConfirmCreateTransferDirectory = 12,
    CancelTransferPicker = 13,
    ObserveTransferPickerClosed = 14,
    OpenTransferTargetRoot = 15,
    EnterTransferTargetDirectory = 16,
    ReadTransferTargetDirectory = 17,
    OpenTransferSourceAfterTargetRead = 18,
    ObserveTransferSubmissionReady = 19,
    SubmitTransferOnce = 20,
    ObserveTransferSubmissionOutcome = 21,
    ReadShareOfficialListing = 22,
    SubmitShareOnce = 23,
    ObserveShareSubmissionOutcome = 24,
    SubmitExtractionCode = 25,
}

public abstract class ManagedBrowserRequestPayload
{
    private sealed class EmptyPayload : ManagedBrowserRequestPayload
    {
        public override string ToString() => "ManagedBrowserRequestPayload { }";
    }

    public static ManagedBrowserRequestPayload Empty { get; } = new EmptyPayload();
}

public sealed class ManagedBrowserHandshakeRequest : ManagedBrowserRequestPayload
{
    public ManagedBrowserHandshakeRequest(
        string nonce,
        int parentProcessId,
        IReadOnlyList<string> requiredCapabilities)
    {
        if (!ManagedBrowserProtocol.IsNonce(nonce))
            throw new ArgumentException("A 256-bit lowercase hexadecimal nonce is required.", nameof(nonce));
        if (parentProcessId <= 0) throw new ArgumentOutOfRangeException(nameof(parentProcessId));
        ArgumentNullException.ThrowIfNull(requiredCapabilities);
        if (!ManagedBrowserProtocol.CapabilitiesMatch(requiredCapabilities))
            throw new ArgumentException("The fixed capability set is required.", nameof(requiredCapabilities));
        Nonce = nonce;
        ParentProcessId = parentProcessId;
        RequiredCapabilities = requiredCapabilities.ToArray();
    }

    public string Nonce { get; }
    public int ParentProcessId { get; }
    public IReadOnlyList<string> RequiredCapabilities { get; }
    public override string ToString() =>
        $"ManagedBrowserHandshakeRequest {{ Nonce = [REDACTED], ParentProcessId = {ParentProcessId}, CapabilityCount = {RequiredCapabilities.Count} }}";
}

public sealed class ManagedBrowserObserveAccountIdentityRequest : ManagedBrowserRequestPayload
{
    public ManagedBrowserObserveAccountIdentityRequest(
        Guid browserId,
        string targetId,
        Uri devToolsEndpoint,
        int timeoutMilliseconds)
    {
        if (browserId == Guid.Empty) throw new ArgumentException("A browser id is required.", nameof(browserId));
        ManagedBrowserProtocol.ValidateBoundedText(targetId, nameof(targetId), 256);
        ArgumentNullException.ThrowIfNull(devToolsEndpoint);
        if (!ManagedBrowserProtocol.IsVerifiedLoopbackEndpoint(devToolsEndpoint))
            throw new ArgumentException("A verified loopback endpoint is required.", nameof(devToolsEndpoint));
        if (timeoutMilliseconds is < 1 or > ManagedBrowserProtocol.MaximumOperationTimeoutMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
        BrowserId = browserId;
        TargetId = targetId;
        DevToolsEndpoint = devToolsEndpoint;
        TimeoutMilliseconds = timeoutMilliseconds;
    }

    public Guid BrowserId { get; }
    public string TargetId { get; }
    public Uri DevToolsEndpoint { get; }
    public int TimeoutMilliseconds { get; }
    public override string ToString() =>
        $"ManagedBrowserObserveAccountIdentityRequest {{ BrowserId = [REDACTED], TargetId = [REDACTED], DevToolsEndpoint = [REDACTED], TimeoutMilliseconds = {TimeoutMilliseconds} }}";
}

public abstract class ManagedBrowserResult
{
}

public sealed class ManagedBrowserHandshakeResult : ManagedBrowserResult
{
    public ManagedBrowserHandshakeResult(
        string nonce,
        int parentProcessId,
        int helperProcessId,
        IReadOnlyList<string> capabilities)
    {
        if (!ManagedBrowserProtocol.IsNonce(nonce))
            throw new ArgumentException("A 256-bit lowercase hexadecimal nonce is required.", nameof(nonce));
        if (parentProcessId <= 0) throw new ArgumentOutOfRangeException(nameof(parentProcessId));
        if (helperProcessId <= 0) throw new ArgumentOutOfRangeException(nameof(helperProcessId));
        ArgumentNullException.ThrowIfNull(capabilities);
        if (!ManagedBrowserProtocol.CapabilitiesMatch(capabilities))
            throw new ArgumentException("The fixed capability set is required.", nameof(capabilities));
        Nonce = nonce;
        ParentProcessId = parentProcessId;
        HelperProcessId = helperProcessId;
        Capabilities = capabilities.ToArray();
    }

    public string Nonce { get; }
    public int ParentProcessId { get; }
    public int HelperProcessId { get; }
    public IReadOnlyList<string> Capabilities { get; }
    public override string ToString() =>
        $"ManagedBrowserHandshakeResult {{ Nonce = [REDACTED], ParentProcessId = {ParentProcessId}, HelperProcessId = {HelperProcessId}, CapabilityCount = {Capabilities.Count} }}";
}

public sealed class ManagedBrowserHealthResult : ManagedBrowserResult
{
    private ManagedBrowserHealthResult() { }
    public static ManagedBrowserHealthResult Healthy { get; } = new();
    public override string ToString() => "ManagedBrowserHealthResult { Kind = Healthy }";
}

public sealed class ManagedBrowserShutdownResult : ManagedBrowserResult
{
    private ManagedBrowserShutdownResult() { }
    public static ManagedBrowserShutdownResult ShuttingDown { get; } = new();
    public override string ToString() => "ManagedBrowserShutdownResult { Kind = ShuttingDown }";
}

public enum ManagedBrowserIdentityResultKind
{
    Observed = 1,
    LoginRequired = 2,
    BrowserUnavailable = 3,
    TimedOut = 4,
    Cancelled = 5,
}

public sealed class ManagedBrowserIdentityResult : ManagedBrowserResult
{
    public ManagedBrowserIdentityResult(
        ManagedBrowserIdentityResultKind kind,
        string? stableBaiduUserId = null)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (kind == ManagedBrowserIdentityResultKind.Observed)
            ManagedBrowserProtocol.ValidateBoundedText(stableBaiduUserId, nameof(stableBaiduUserId), 256);
        else if (stableBaiduUserId is not null)
            throw new ArgumentException("Only an observed result may contain a stable user id.", nameof(stableBaiduUserId));
        Kind = kind;
        StableBaiduUserId = stableBaiduUserId;
    }

    public ManagedBrowserIdentityResultKind Kind { get; }
    public string? StableBaiduUserId { get; }
    public override string ToString() =>
        $"ManagedBrowserIdentityResult {{ Kind = {Kind}, StableBaiduUserId = [REDACTED] }}";
}

public sealed class ManagedBrowserRequest
{
    public ManagedBrowserRequest(
        Guid correlationId,
        Guid operationId,
        Guid accountId,
        long generation,
        ManagedBrowserAction action,
        ManagedBrowserRequestPayload payload)
    {
        ManagedBrowserProtocol.ValidateIdentity(correlationId, operationId, accountId, generation, action);
        ArgumentNullException.ThrowIfNull(payload);
        ManagedBrowserProtocol.ValidateRequestPayload(action, payload);
        CorrelationId = correlationId;
        OperationId = operationId;
        AccountId = accountId;
        Generation = generation;
        Action = action;
        Payload = payload;
    }

    public Guid CorrelationId { get; }
    public Guid OperationId { get; }
    public Guid AccountId { get; }
    public long Generation { get; }
    public ManagedBrowserAction Action { get; }
    public ManagedBrowserRequestPayload Payload { get; }
    public override string ToString() =>
        $"ManagedBrowserRequest {{ CorrelationId = [REDACTED], OperationId = [REDACTED], AccountId = [REDACTED], Generation = {Generation}, Action = {Action}, Payload = [REDACTED] }}";
}

public sealed class ManagedBrowserResponse
{
    public ManagedBrowserResponse(
        Guid correlationId,
        Guid operationId,
        Guid accountId,
        long generation,
        ManagedBrowserAction action,
        ManagedBrowserResult result)
    {
        ManagedBrowserProtocol.ValidateIdentity(correlationId, operationId, accountId, generation, action);
        ArgumentNullException.ThrowIfNull(result);
        ManagedBrowserProtocol.ValidateResult(action, result);
        CorrelationId = correlationId;
        OperationId = operationId;
        AccountId = accountId;
        Generation = generation;
        Action = action;
        Result = result;
    }

    public Guid CorrelationId { get; }
    public Guid OperationId { get; }
    public Guid AccountId { get; }
    public long Generation { get; }
    public ManagedBrowserAction Action { get; }
    public ManagedBrowserResult Result { get; }

    public static ManagedBrowserResponse For(ManagedBrowserRequest request, ManagedBrowserResult result)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new ManagedBrowserResponse(
            request.CorrelationId, request.OperationId, request.AccountId,
            request.Generation, request.Action, result);
    }

    public override string ToString() =>
        $"ManagedBrowserResponse {{ CorrelationId = [REDACTED], OperationId = [REDACTED], AccountId = [REDACTED], Generation = {Generation}, Action = {Action}, Result = [REDACTED] }}";
}

public enum ManagedBrowserRoute
{
    Rejected = 0,
    PanRoot = 1,
    PassportRoot = 2,
    PanMain = 3,
}

public static class ManagedBrowserRoutePolicy
{
    public static ManagedBrowserRoute Classify(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !uri.IsDefaultPort)
        {
            return ManagedBrowserRoute.Rejected;
        }

        if (string.Equals(uri.AbsolutePath, "/", StringComparison.Ordinal) &&
            string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment))
        {
            if (string.Equals(uri.IdnHost, "pan.baidu.com", StringComparison.Ordinal))
                return ManagedBrowserRoute.PanRoot;
            if (string.Equals(uri.IdnHost, "passport.baidu.com", StringComparison.Ordinal))
                return ManagedBrowserRoute.PassportRoot;
        }
        // The screenshot-confirmed page location is not proof of a logged-in identity.
        if (string.Equals(uri.IdnHost, "pan.baidu.com", StringComparison.Ordinal) &&
            string.Equals(uri.AbsolutePath, "/disk/main", StringComparison.Ordinal) &&
            (string.IsNullOrEmpty(uri.Query) || string.Equals(uri.Query, "?from=homeFlow", StringComparison.Ordinal)) &&
            string.Equals(uri.Fragment, "#/index?category=all", StringComparison.Ordinal))
            return ManagedBrowserRoute.PanMain;
        return ManagedBrowserRoute.Rejected;
    }

    public static bool CanTransition(ManagedBrowserRoute from, ManagedBrowserRoute to) =>
        (from, to) is
            (ManagedBrowserRoute.PanRoot, ManagedBrowserRoute.PanRoot) or
            (ManagedBrowserRoute.PanRoot, ManagedBrowserRoute.PassportRoot) or
            (ManagedBrowserRoute.PanRoot, ManagedBrowserRoute.PanMain) or
            (ManagedBrowserRoute.PassportRoot, ManagedBrowserRoute.PassportRoot) or
            (ManagedBrowserRoute.PassportRoot, ManagedBrowserRoute.PanRoot) or
            (ManagedBrowserRoute.PassportRoot, ManagedBrowserRoute.PanMain) or
            (ManagedBrowserRoute.PanMain, ManagedBrowserRoute.PanRoot) or
            (ManagedBrowserRoute.PanMain, ManagedBrowserRoute.PassportRoot) or
            (ManagedBrowserRoute.PanMain, ManagedBrowserRoute.PanMain);

    public static bool CanObserveIdentity(ManagedBrowserRoute route) =>
        route is ManagedBrowserRoute.PanRoot or ManagedBrowserRoute.PanMain;
}

public static partial class ManagedBrowserProtocol
{
    public const int ProtocolVersion = 3;
    public const string ExecutionEngineVersion = "ManagedChromium-v3";
    public const int MaximumFrameBytes = 1_048_576;
    public const int MaximumTargetEnterFrameBytes = 65_535;
    public const int MaximumOperationTimeoutMilliseconds = 120_000;
    public const int MaximumRequestsPerSession = 1_024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] Capabilities =
        ["Health", "Shutdown", "ObserveAccountIdentity", "OpenTransferSource", "ReadTransferSource", "ReadTransferDirectory", "OpenTransferPicker", "OpenTransferDirectoryRoot", "EnterTransferDirectory", "BeginCreateTransferDirectory", "ConfirmCreateTransferDirectory", "CancelTransferPicker", "ObserveTransferPickerClosed", "OpenTransferTargetRoot", "EnterTransferTargetDirectory", "ReadTransferTargetDirectory", "OpenTransferSourceAfterTargetRead", "ObserveTransferSubmissionReady", "SubmitTransferOnce", "ObserveTransferSubmissionOutcome", "ReadShareOfficialListing", "SubmitShareOnce", "ObserveShareSubmissionOutcome", "SubmitExtractionCode"];
    private static readonly HashSet<string> ForbiddenNames = new(
        ["cookie", "authorization", "extractionCode", "pageUrl", "pageText", "html",
         "selector", "script", "url", "buttonText", "name", "toast"],
        StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> RequiredCapabilities { get; } = Array.AsReadOnly(Capabilities);

    public static byte[] EncodeRequest(ManagedBrowserRequest request) => Frame(EncodeRequestPayload(request));
    public static byte[] EncodeResponse(ManagedBrowserResponse response) => Frame(EncodeResponsePayload(response));

    public static byte[] EncodeRequestPayload(ManagedBrowserRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            WriteEnvelope(writer, request.CorrelationId, request.OperationId, request.AccountId,
                request.Generation, request.Action, "payload", () => WritePayload(writer, request.Payload));
        }
        var bytes = stream.ToArray();
        if (request.Action == ManagedBrowserAction.EnterTransferTargetDirectory &&
            bytes.Length + sizeof(int) > MaximumTargetEnterFrameBytes)
            throw Failure(ManagedBrowserProtocolError.FrameSizeInvalid);
        return bytes;
    }

    public static byte[] EncodeResponsePayload(ManagedBrowserResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            WriteEnvelope(writer, response.CorrelationId, response.OperationId, response.AccountId,
                response.Generation, response.Action, "result", () => WriteResult(writer, response.Result));
        }
        return stream.ToArray();
    }

    public static ManagedBrowserRequest DecodeRequest(ReadOnlySpan<byte> payload)
    {
        using var document = Parse(payload);
        var root = document.RootElement;
        ValidateObject(root, ["protocolVersion", "executionEngineVersion", "correlationId",
            "operationId", "accountId", "generation", "action", "payload"]);
        ValidateCommon(root, out var correlationId, out var operationId, out var accountId,
            out var generation, out var action);
        if (action == ManagedBrowserAction.EnterTransferTargetDirectory &&
            payload.Length + sizeof(int) > MaximumTargetEnterFrameBytes)
            throw Failure(ManagedBrowserProtocolError.FrameSizeInvalid);
        var value = RequireObject(root, "payload", ManagedBrowserProtocolError.InvalidPayload);
        ManagedBrowserRequestPayload requestPayload = action switch
        {
            ManagedBrowserAction.Handshake => ParseHandshakeRequest(value),
            ManagedBrowserAction.Health or ManagedBrowserAction.Shutdown => ParseEmptyPayload(value),
            ManagedBrowserAction.ObserveAccountIdentity => ParseObserveRequest(value),
            ManagedBrowserAction.OpenTransferSource => ParseOpenTransferSourceRequest(value),
            ManagedBrowserAction.ReadTransferSource => ParseReadTransferSourceRequest(value),
            ManagedBrowserAction.ReadTransferDirectory => ParseReadTransferDirectoryRequest(value),
            ManagedBrowserAction.OpenTransferPicker => ParseOpenTransferPickerRequest(value),
            ManagedBrowserAction.OpenTransferDirectoryRoot => ParseOpenTransferDirectoryRootRequest(value),
            ManagedBrowserAction.EnterTransferDirectory => ParseEnterTransferDirectoryRequest(value),
            ManagedBrowserAction.BeginCreateTransferDirectory => ParseBeginCreateTransferDirectoryRequest(value),
            ManagedBrowserAction.ConfirmCreateTransferDirectory => ParseConfirmCreateTransferDirectoryRequest(value),
            ManagedBrowserAction.CancelTransferPicker => ParseCancelTransferPickerRequest(value),
            ManagedBrowserAction.ObserveTransferPickerClosed => ParseObserveTransferPickerClosedRequest(value),
            ManagedBrowserAction.OpenTransferTargetRoot => ParseOpenTransferTargetRootRequest(value),
            ManagedBrowserAction.EnterTransferTargetDirectory => ParseEnterTransferTargetDirectoryRequest(value),
            ManagedBrowserAction.ReadTransferTargetDirectory => ParseReadTransferTargetDirectoryRequest(value),
            ManagedBrowserAction.OpenTransferSourceAfterTargetRead => ParseOpenTransferSourceAfterTargetReadRequest(value),
            ManagedBrowserAction.ObserveTransferSubmissionReady => ParseObserveTransferSubmissionReadyRequest(value),
            ManagedBrowserAction.SubmitTransferOnce => ParseSubmitTransferOnceRequest(value),
            ManagedBrowserAction.ObserveTransferSubmissionOutcome => ParseObserveTransferSubmissionOutcomeRequest(value),
            ManagedBrowserAction.ReadShareOfficialListing => ParseReadShareOfficialListingRequest(value),
            ManagedBrowserAction.SubmitShareOnce => ParseSubmitShareOnceRequest(value),
            ManagedBrowserAction.ObserveShareSubmissionOutcome => ParseObserveShareSubmissionOutcomeRequest(value),
            ManagedBrowserAction.SubmitExtractionCode => ParseSubmitExtractionCodeRequest(value),
            _ => throw Failure(ManagedBrowserProtocolError.UnknownAction),
        };
        return new ManagedBrowserRequest(
            correlationId, operationId, accountId, generation, action, requestPayload);
    }

    public static ManagedBrowserResponse DecodeResponse(ReadOnlySpan<byte> payload)
    {
        using var document = Parse(payload);
        var root = document.RootElement;
        ValidateObject(root, ["protocolVersion", "executionEngineVersion", "correlationId",
            "operationId", "accountId", "generation", "action", "result"]);
        ValidateCommon(root, out var correlationId, out var operationId, out var accountId,
            out var generation, out var action);
        var value = RequireObject(root, "result", ManagedBrowserProtocolError.InvalidPayload);
        ManagedBrowserResult result = action switch
        {
            ManagedBrowserAction.Handshake => ParseHandshakeResult(value),
            ManagedBrowserAction.Health => ParseSingletonResult(value, "Healthy", ManagedBrowserHealthResult.Healthy),
            ManagedBrowserAction.Shutdown => ParseSingletonResult(value, "ShuttingDown", ManagedBrowserShutdownResult.ShuttingDown),
            ManagedBrowserAction.ObserveAccountIdentity => ParseIdentityResult(value),
            ManagedBrowserAction.OpenTransferSource => ParseTransferSourceResult(value),
            ManagedBrowserAction.ReadTransferSource => ParseSourceListingResult(value),
            ManagedBrowserAction.ReadTransferDirectory => ParseDirectoryListingResult(value),
            ManagedBrowserAction.OpenTransferPicker => ParseTransferPickerResult(value),
            ManagedBrowserAction.OpenTransferDirectoryRoot => ParseDirectoryMutationResult(ManagedBrowserAction.OpenTransferDirectoryRoot, value),
            ManagedBrowserAction.EnterTransferDirectory => ParseDirectoryMutationResult(ManagedBrowserAction.EnterTransferDirectory, value),
            ManagedBrowserAction.BeginCreateTransferDirectory => ParseDirectoryMutationResult(ManagedBrowserAction.BeginCreateTransferDirectory, value),
            ManagedBrowserAction.ConfirmCreateTransferDirectory => ParseDirectoryMutationResult(ManagedBrowserAction.ConfirmCreateTransferDirectory, value),
            ManagedBrowserAction.CancelTransferPicker => ParseDirectoryMutationResult(ManagedBrowserAction.CancelTransferPicker, value),
            ManagedBrowserAction.ObserveTransferPickerClosed => ParseTransferPickerClosedResult(value),
            ManagedBrowserAction.OpenTransferTargetRoot => ParseOpenTransferTargetRootResult(value),
            ManagedBrowserAction.EnterTransferTargetDirectory => ParseEnterTransferTargetDirectoryResult(value),
            ManagedBrowserAction.ReadTransferTargetDirectory => ParseReadTransferTargetDirectoryResult(value),
            ManagedBrowserAction.OpenTransferSourceAfterTargetRead => ParseOpenTransferSourceAfterTargetReadResult(value),
            ManagedBrowserAction.ObserveTransferSubmissionReady => ParseTransferSubmissionReadinessResult(value),
            ManagedBrowserAction.SubmitTransferOnce => ParseSubmitTransferOnceResult(value),
            ManagedBrowserAction.ObserveTransferSubmissionOutcome => ParseTransferSubmissionOutcomeResult(value),
            ManagedBrowserAction.ReadShareOfficialListing => ParseShareListingResult(value),
            ManagedBrowserAction.SubmitShareOnce => ParseSubmitShareOnceResult(value),
            ManagedBrowserAction.ObserveShareSubmissionOutcome => ParseShareSubmissionOutcomeResult(value),
            ManagedBrowserAction.SubmitExtractionCode => ParseSubmitExtractionCodeResult(value),
            _ => throw Failure(ManagedBrowserProtocolError.UnknownAction),
        };
        return new ManagedBrowserResponse(
            correlationId, operationId, accountId, generation, action, result);
    }

    public static async Task WriteRequestAsync(
        Stream stream,
        ManagedBrowserRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        await stream.WriteAsync(EncodeRequest(request), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task WriteResponseAsync(
        Stream stream,
        ManagedBrowserResponse response,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        await stream.WriteAsync(EncodeResponse(response), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<ManagedBrowserRequest?> ReadRequestAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var bytes = await ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
        return bytes is null ? null : DecodeRequest(bytes);
    }

    public static async Task<ManagedBrowserResponse?> ReadResponseAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var bytes = await ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
        return bytes is null ? null : DecodeResponse(bytes);
    }

    public static void ValidateResponseFor(
        ManagedBrowserRequest request,
        ManagedBrowserResponse response)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);
        if (request.CorrelationId != response.CorrelationId ||
            request.OperationId != response.OperationId ||
            request.AccountId != response.AccountId ||
            request.Generation != response.Generation ||
            request.Action != response.Action)
        {
            throw Failure(ManagedBrowserProtocolError.ResponseIdentityMismatch);
        }
        if (request.Payload is ManagedBrowserReadTransferTargetDirectoryRequest targetRequest &&
            response.Result is ManagedBrowserReadTransferTargetDirectoryResult
            {
                Kind: ManagedBrowserReadTransferTargetDirectoryResultKind.Observed,
                Snapshot: { } snapshot,
            } &&
            (snapshot.BrowserId != targetRequest.Binding.BrowserId ||
             !string.Equals(snapshot.TargetId, targetRequest.Binding.TargetId, StringComparison.Ordinal) ||
             !string.Equals(snapshot.DevToolsEndpoint.AbsoluteUri,
                 targetRequest.Binding.DevToolsEndpoint.AbsoluteUri, StringComparison.Ordinal) ||
             !string.Equals(snapshot.BaiduUserId, targetRequest.Binding.ExpectedBaiduUserId, StringComparison.Ordinal) ||
             (targetRequest.ExpectedDirectoryExactId is null
                 ? !string.Equals(snapshot.CanonicalPath, "/", StringComparison.Ordinal) ||
                   snapshot.AncestorExactIds.Count != 1 ||
                   !string.Equals(snapshot.AncestorExactIds[0], snapshot.DirectoryExactId, StringComparison.Ordinal)
                 : !string.Equals(snapshot.DirectoryExactId, targetRequest.ExpectedDirectoryExactId, StringComparison.Ordinal) ||
                   !string.Equals(snapshot.CanonicalPath, targetRequest.ExpectedCanonicalPath, StringComparison.Ordinal) ||
                   !snapshot.AncestorExactIds.SequenceEqual(
                       targetRequest.ExpectedAncestorExactIds!, StringComparer.Ordinal))))
        {
            throw Failure(ManagedBrowserProtocolError.ResponseIdentityMismatch);
        }
        if (request.Payload is ManagedBrowserObserveTransferSubmissionReadyRequest readinessRequest &&
            response.Result is ManagedBrowserTransferSubmissionReadinessResult
            {
                Kind: ManagedBrowserTransferSubmissionReadinessResultKind.Ready,
                Readiness: { } readiness,
            } && !readinessRequest.Context.EqualsEvidence(readiness.Context))
            throw Failure(ManagedBrowserProtocolError.ResponseIdentityMismatch);
        if (request.Payload is ManagedBrowserObserveTransferSubmissionOutcomeRequest outcomeRequest &&
            response.Result is ManagedBrowserTransferSubmissionOutcomeResult
            {
                Kind: ManagedBrowserTransferOutcomeKind.AcceptedBound,
            } outcome &&
            (!string.Equals(outcome.ExactTargetId, outcomeRequest.Context.SelectedTargetExactId, StringComparison.Ordinal) ||
             !string.Equals(outcome.SourceExactId, outcomeRequest.Context.SourceExactId, StringComparison.Ordinal)))
            throw Failure(ManagedBrowserProtocolError.ResponseIdentityMismatch);
        if (request.Payload is ManagedBrowserReadShareOfficialListingRequest shareListingRequest &&
            response.Result is ManagedBrowserReadShareOfficialListingResult
            {
                Kind: ManagedBrowserShareListingResultKind.Observed,
                Listing: { } shareListing,
            } && !shareListingRequest.Context.EqualsEvidence(shareListing.Context))
            throw Failure(ManagedBrowserProtocolError.ResponseIdentityMismatch);
        if (request.Payload is ManagedBrowserObserveShareSubmissionOutcomeRequest shareOutcomeRequest &&
            response.Result is ManagedBrowserShareSubmissionOutcomeResult
            {
                Kind: ManagedBrowserShareOutcomeKind.AcceptedBound,
            } shareOutcome &&
            (shareOutcome.ExactItems != shareOutcomeRequest.Context.ExactItems ||
             shareOutcome.CodeMode != shareOutcomeRequest.Context.CodeMode ||
             shareOutcome.IsPermanent != true || shareOutcome.IsAccessUnlimited != true ||
             shareOutcomeRequest.Context.CodeMode == ShareExtractionCodeBehavior.Fixed &&
             !string.Equals(shareOutcome.EffectiveCode, shareOutcomeRequest.Context.FixedCode, StringComparison.Ordinal)))
            throw Failure(ManagedBrowserProtocolError.ResponseIdentityMismatch);
    }

    internal static bool IsNonce(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static bool CapabilitiesMatch(IEnumerable<string> values) =>
        values.SequenceEqual(Capabilities, StringComparer.Ordinal);

    internal static void ValidateBoundedText(string? value, string parameterName, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value != value.Trim() ||
            HasForbiddenScalar(value) ||
            !TryGetUtf8ByteCount(value, out var byteCount) ||
            byteCount > maximumLength)
        {
            throw new ArgumentException("A bounded non-empty value is required.", parameterName);
        }
    }

    internal static bool IsVerifiedLoopbackEndpoint(Uri uri) =>
        uri.IsAbsoluteUri &&
        string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) &&
        string.Equals(uri.Host, "127.0.0.1", StringComparison.Ordinal) &&
        uri.Port is >= 1 and <= 65535 and not 80 &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        string.Equals(uri.AbsolutePath, "/", StringComparison.Ordinal) &&
        string.IsNullOrEmpty(uri.Query) &&
        string.IsNullOrEmpty(uri.Fragment) &&
        string.Equals(uri.OriginalString, $"http://127.0.0.1:{uri.Port}/", StringComparison.Ordinal);

    private static bool TryGetUtf8ByteCount(string value, out int byteCount)
    {
        try
        {
            byteCount = StrictUtf8.GetByteCount(value);
            return true;
        }
        catch (EncoderFallbackException)
        {
            byteCount = 0;
            return false;
        }
    }

    private static bool HasForbiddenScalar(string value)
    {
        foreach (var rune in value.EnumerateRunes())
        {
            if (rune.Value is <= 0x1f or >= 0x7f and <= 0x9f)
                return true;
        }
        return false;
    }

    internal static void ValidateIdentity(
        Guid correlationId, Guid operationId, Guid accountId, long generation,
        ManagedBrowserAction action)
    {
        if (correlationId == Guid.Empty) throw new ArgumentException("A correlation id is required.", nameof(correlationId));
        if (operationId == Guid.Empty) throw new ArgumentException("An operation id is required.", nameof(operationId));
        if (accountId == Guid.Empty) throw new ArgumentException("An account id is required.", nameof(accountId));
        if (generation <= 0) throw new ArgumentOutOfRangeException(nameof(generation));
        if (!Enum.IsDefined(action)) throw new ArgumentOutOfRangeException(nameof(action));
    }

    internal static void ValidateRequestPayload(
        ManagedBrowserAction action,
        ManagedBrowserRequestPayload payload)
    {
        var valid = action switch
        {
            ManagedBrowserAction.Handshake => payload is ManagedBrowserHandshakeRequest,
            ManagedBrowserAction.Health or ManagedBrowserAction.Shutdown =>
                ReferenceEquals(payload, ManagedBrowserRequestPayload.Empty),
            ManagedBrowserAction.ObserveAccountIdentity =>
                payload is ManagedBrowserObserveAccountIdentityRequest,
            ManagedBrowserAction.OpenTransferSource => payload is ManagedBrowserOpenTransferSourceRequest,
            ManagedBrowserAction.ReadTransferSource => payload is ManagedBrowserReadTransferSourceRequest,
            ManagedBrowserAction.ReadTransferDirectory => payload is ManagedBrowserReadTransferDirectoryRequest,
            ManagedBrowserAction.OpenTransferPicker => payload is ManagedBrowserOpenTransferPickerRequest,
            ManagedBrowserAction.OpenTransferDirectoryRoot or ManagedBrowserAction.EnterTransferDirectory or ManagedBrowserAction.BeginCreateTransferDirectory or ManagedBrowserAction.ConfirmCreateTransferDirectory or ManagedBrowserAction.CancelTransferPicker =>
                payload is ManagedBrowserDirectoryMutationRequest mutation && mutation.Action == action,
            ManagedBrowserAction.ObserveTransferPickerClosed => payload is ManagedBrowserObserveTransferPickerClosedRequest,
            ManagedBrowserAction.OpenTransferTargetRoot => payload is ManagedBrowserOpenTransferTargetRootRequest,
            ManagedBrowserAction.EnterTransferTargetDirectory => payload is ManagedBrowserEnterTransferTargetDirectoryRequest,
            ManagedBrowserAction.ReadTransferTargetDirectory => payload is ManagedBrowserReadTransferTargetDirectoryRequest,
            ManagedBrowserAction.OpenTransferSourceAfterTargetRead => payload is ManagedBrowserOpenTransferSourceAfterTargetReadRequest,
            ManagedBrowserAction.ObserveTransferSubmissionReady => payload is ManagedBrowserObserveTransferSubmissionReadyRequest,
            ManagedBrowserAction.SubmitTransferOnce => payload is ManagedBrowserSubmitTransferOnceRequest,
            ManagedBrowserAction.ObserveTransferSubmissionOutcome => payload is ManagedBrowserObserveTransferSubmissionOutcomeRequest,
            ManagedBrowserAction.ReadShareOfficialListing => payload is ManagedBrowserReadShareOfficialListingRequest,
            ManagedBrowserAction.SubmitShareOnce => payload is ManagedBrowserSubmitShareOnceRequest,
            ManagedBrowserAction.ObserveShareSubmissionOutcome => payload is ManagedBrowserObserveShareSubmissionOutcomeRequest,
            ManagedBrowserAction.SubmitExtractionCode => payload is ManagedBrowserSubmitExtractionCodeRequest,
            _ => false,
        };
        if (!valid) throw new ArgumentException("The payload does not match the action.", nameof(payload));
    }

    internal static void ValidateResult(ManagedBrowserAction action, ManagedBrowserResult result)
    {
        var valid = action switch
        {
            ManagedBrowserAction.Handshake => result is ManagedBrowserHandshakeResult,
            ManagedBrowserAction.Health => result is ManagedBrowserHealthResult,
            ManagedBrowserAction.Shutdown => result is ManagedBrowserShutdownResult,
            ManagedBrowserAction.ObserveAccountIdentity => result is ManagedBrowserIdentityResult,
            ManagedBrowserAction.OpenTransferSource => result is ManagedBrowserTransferSourceResult,
            ManagedBrowserAction.ReadTransferSource => result is ManagedBrowserSourceListingResult,
            ManagedBrowserAction.ReadTransferDirectory => result is ManagedBrowserDirectoryListingResult,
            ManagedBrowserAction.OpenTransferPicker => result is ManagedBrowserTransferPickerResult,
            ManagedBrowserAction.OpenTransferDirectoryRoot or ManagedBrowserAction.EnterTransferDirectory or ManagedBrowserAction.BeginCreateTransferDirectory or ManagedBrowserAction.ConfirmCreateTransferDirectory or ManagedBrowserAction.CancelTransferPicker =>
                result is ManagedBrowserDirectoryMutationResult mutation && IsAllowedDirectoryMutationResult(action, mutation.Kind),
            ManagedBrowserAction.ObserveTransferPickerClosed => result is ManagedBrowserTransferPickerClosedResult,
            ManagedBrowserAction.OpenTransferTargetRoot => result is ManagedBrowserOpenTransferTargetRootResult,
            ManagedBrowserAction.EnterTransferTargetDirectory => result is ManagedBrowserEnterTransferTargetDirectoryResult,
            ManagedBrowserAction.ReadTransferTargetDirectory => result is ManagedBrowserReadTransferTargetDirectoryResult,
            ManagedBrowserAction.OpenTransferSourceAfterTargetRead => result is ManagedBrowserOpenTransferSourceAfterTargetReadResult,
            ManagedBrowserAction.ObserveTransferSubmissionReady => result is ManagedBrowserTransferSubmissionReadinessResult,
            ManagedBrowserAction.SubmitTransferOnce => result is ManagedBrowserSubmitTransferOnceResult,
            ManagedBrowserAction.ObserveTransferSubmissionOutcome => result is ManagedBrowserTransferSubmissionOutcomeResult,
            ManagedBrowserAction.ReadShareOfficialListing => result is ManagedBrowserReadShareOfficialListingResult,
            ManagedBrowserAction.SubmitShareOnce => result is ManagedBrowserSubmitShareOnceResult,
            ManagedBrowserAction.ObserveShareSubmissionOutcome => result is ManagedBrowserShareSubmissionOutcomeResult,
            ManagedBrowserAction.SubmitExtractionCode => result is ManagedBrowserSubmitExtractionCodeResult,
            _ => false,
        };
        if (!valid) throw new ArgumentException("The result does not match the action.", nameof(result));
    }

    private static bool IsAllowedDirectoryMutationResult(ManagedBrowserAction action,
        ManagedBrowserDirectoryMutationResultKind kind) => kind switch
    {
        ManagedBrowserDirectoryMutationResultKind.RootOpened => action == ManagedBrowserAction.OpenTransferDirectoryRoot,
        ManagedBrowserDirectoryMutationResultKind.DirectoryEntered => action == ManagedBrowserAction.EnterTransferDirectory,
        ManagedBrowserDirectoryMutationResultKind.CreateDialogOpened => action == ManagedBrowserAction.BeginCreateTransferDirectory,
        ManagedBrowserDirectoryMutationResultKind.DirectoryCreated => action == ManagedBrowserAction.ConfirmCreateTransferDirectory,
        ManagedBrowserDirectoryMutationResultKind.PickerCancelled => action == ManagedBrowserAction.CancelTransferPicker,
        _ => true,
    };

    private static byte[] Frame(byte[] payload)
    {
        if (payload.Length is < 1 or > MaximumFrameBytes)
            throw Failure(ManagedBrowserProtocolError.FrameSizeInvalid);
        var result = new byte[payload.Length + 4];
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(0, 4), payload.Length);
        payload.CopyTo(result, 4);
        return result;
    }

    private static async Task<byte[]?> ReadFrameAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[4];
        var first = await stream.ReadAsync(header.AsMemory(0, 4), cancellationToken).ConfigureAwait(false);
        if (first == 0) return null;
        await ReadExactlyAsync(stream, header.AsMemory(first), cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is < 1 or > MaximumFrameBytes)
            throw Failure(ManagedBrowserProtocolError.FrameSizeInvalid);
        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0) throw Failure(ManagedBrowserProtocolError.UnexpectedEof);
            offset += read;
        }
    }

    private static JsonDocument Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length is < 1 or > MaximumFrameBytes)
            throw Failure(ManagedBrowserProtocolError.FrameSizeInvalid);
        string json;
        try { json = StrictUtf8.GetString(payload); }
        catch (DecoderFallbackException) { throw Failure(ManagedBrowserProtocolError.MalformedUtf8); }
        try
        {
            var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            try
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    throw Failure(ManagedBrowserProtocolError.InvalidMessage);
                ScanForbidden(document.RootElement);
                return document;
            }
            catch
            {
                document.Dispose();
                throw;
            }
        }
        catch (ManagedBrowserProtocolException) { throw; }
        catch (JsonException) { throw Failure(ManagedBrowserProtocolError.MalformedJson); }
    }

    private static void ScanForbidden(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw Failure(ManagedBrowserProtocolError.DuplicateField);
                if (ForbiddenNames.Contains(property.Name))
                    throw Failure(ManagedBrowserProtocolError.ForbiddenField);
                ScanForbidden(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) ScanForbidden(item);
        }
    }

    private static void ValidateObject(
        JsonElement element,
        string[] allowed,
        ManagedBrowserProtocolError structureError = ManagedBrowserProtocolError.InvalidMessage)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw Failure(structureError);
        var allowedSet = new HashSet<string>(allowed, StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!allowedSet.Contains(property.Name))
                throw Failure(ManagedBrowserProtocolError.UnknownField);
        if (element.EnumerateObject().Count() != allowed.Length)
            throw Failure(structureError);
    }

    private static void ValidateCommon(
        JsonElement root,
        out Guid correlationId,
        out Guid operationId,
        out Guid accountId,
        out long generation,
        out ManagedBrowserAction action)
    {
        if (!root.TryGetProperty("protocolVersion", out var protocolProperty) ||
            protocolProperty.ValueKind != JsonValueKind.Number ||
            !string.Equals(protocolProperty.GetRawText(), ProtocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal))
            throw Failure(ManagedBrowserProtocolError.ProtocolVersionMismatch);
        if (!root.TryGetProperty("executionEngineVersion", out var engineProperty) ||
            engineProperty.ValueKind != JsonValueKind.String ||
            !string.Equals(engineProperty.GetString(), ExecutionEngineVersion, StringComparison.Ordinal))
            throw Failure(ManagedBrowserProtocolError.ExecutionEngineVersionMismatch);
        correlationId = RequireGuid(root, "correlationId");
        operationId = RequireGuid(root, "operationId");
        accountId = RequireGuid(root, "accountId");
        generation = RequireInt64(root, "generation");
        if (!root.TryGetProperty("action", out var actionProperty) ||
            actionProperty.ValueKind != JsonValueKind.String)
            throw Failure(ManagedBrowserProtocolError.UnknownAction);
        var actionText = actionProperty.GetString()!;
        if (!Enum.TryParse(actionText, ignoreCase: false, out action) ||
            !Enum.IsDefined(action) || action.ToString() != actionText)
        {
            throw Failure(ManagedBrowserProtocolError.UnknownAction);
        }
        try { ValidateIdentity(correlationId, operationId, accountId, generation, action); }
        catch (ArgumentException) { throw Failure(ManagedBrowserProtocolError.InvalidMessage); }
    }

    private static ManagedBrowserRequestPayload ParseHandshakeRequest(JsonElement value)
    {
        ValidateObject(value, ["nonce", "parentProcessId", "requiredCapabilities"], ManagedBrowserProtocolError.InvalidPayload);
        var nonce = RequireString(value, "nonce", ManagedBrowserProtocolError.InvalidPayload);
        var parentProcessId = RequireInt32(
            value, "parentProcessId", ManagedBrowserProtocolError.InvalidPayload);
        var capabilities = RequireStringArray(
            value, "requiredCapabilities", ManagedBrowserProtocolError.InvalidPayload);
        if (!IsNonce(nonce) || parentProcessId <= 0)
            throw Failure(ManagedBrowserProtocolError.InvalidPayload);
        if (!CapabilitiesMatch(capabilities))
            throw Failure(ManagedBrowserProtocolError.CapabilityMismatch);
        return new ManagedBrowserHandshakeRequest(nonce, parentProcessId, capabilities);
    }

    private static ManagedBrowserRequestPayload ParseEmptyPayload(JsonElement value)
    {
        ValidateObject(value, [], ManagedBrowserProtocolError.InvalidPayload);
        return ManagedBrowserRequestPayload.Empty;
    }

    private static ManagedBrowserRequestPayload ParseObserveRequest(JsonElement value)
    {
        ValidateObject(value, ["browserId", "targetId", "devToolsEndpoint", "timeoutMilliseconds"], ManagedBrowserProtocolError.InvalidPayload);
        try
        {
            var endpointText = RequireString(
                value, "devToolsEndpoint", ManagedBrowserProtocolError.InvalidPayload);
            if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint))
                throw new ArgumentException("Invalid endpoint.");
            return new ManagedBrowserObserveAccountIdentityRequest(
                RequireGuid(value, "browserId", ManagedBrowserProtocolError.InvalidPayload),
                RequireString(value, "targetId", ManagedBrowserProtocolError.InvalidPayload), endpoint,
                RequireInt32(value, "timeoutMilliseconds", ManagedBrowserProtocolError.InvalidPayload));
        }
        catch (ArgumentException) { throw Failure(ManagedBrowserProtocolError.InvalidPayload); }
    }

    private static ManagedBrowserRequestPayload ParseOpenTransferSourceRequest(JsonElement value)
    {
        ValidateObject(value, ["browserId", "targetId", "devToolsEndpoint", "timeoutMilliseconds",
            "sourceUri", "expectedBaiduUserId"], ManagedBrowserProtocolError.InvalidPayload);
        try
        {
            if (!Uri.TryCreate(RequireString(value, "devToolsEndpoint", ManagedBrowserProtocolError.InvalidPayload),
                    UriKind.Absolute, out var endpoint))
                throw new ArgumentException("Invalid endpoint.");
            return new ManagedBrowserOpenTransferSourceRequest(
                RequireGuid(value, "browserId", ManagedBrowserProtocolError.InvalidPayload),
                RequireString(value, "targetId", ManagedBrowserProtocolError.InvalidPayload), endpoint,
                RequireInt32(value, "timeoutMilliseconds", ManagedBrowserProtocolError.InvalidPayload),
                RequireString(value, "sourceUri", ManagedBrowserProtocolError.InvalidPayload),
                RequireString(value, "expectedBaiduUserId", ManagedBrowserProtocolError.InvalidPayload));
        }
        catch (ArgumentException) { throw Failure(ManagedBrowserProtocolError.InvalidPayload); }
    }

    private static ManagedBrowserResult ParseTransferSourceResult(JsonElement value)
    {
        ValidateObject(value, ["kind"], ManagedBrowserProtocolError.InvalidPayload);
        var text = RequireString(value, "kind", ManagedBrowserProtocolError.InvalidPayload);
        if (!Enum.TryParse(text, ignoreCase: false, out ManagedBrowserTransferSourceResultKind kind) ||
            !Enum.IsDefined(kind) || kind.ToString() != text)
            throw Failure(ManagedBrowserProtocolError.InvalidPayload);
        return new ManagedBrowserTransferSourceResult(kind);
    }

    private static ManagedBrowserResult ParseHandshakeResult(JsonElement value)
    {
        ValidateObject(value, ["kind", "nonce", "parentProcessId", "helperProcessId", "capabilities"], ManagedBrowserProtocolError.InvalidPayload);
        var kind = RequireString(value, "kind", ManagedBrowserProtocolError.InvalidPayload);
        var nonce = RequireString(value, "nonce", ManagedBrowserProtocolError.InvalidPayload);
        var parentProcessId = RequireInt32(
            value, "parentProcessId", ManagedBrowserProtocolError.InvalidPayload);
        var helperProcessId = RequireInt32(
            value, "helperProcessId", ManagedBrowserProtocolError.InvalidPayload);
        var capabilities = RequireStringArray(
            value, "capabilities", ManagedBrowserProtocolError.InvalidPayload);
        if (!string.Equals(kind, "HandshakeAccepted", StringComparison.Ordinal) ||
            !IsNonce(nonce) || parentProcessId <= 0 || helperProcessId <= 0)
            throw Failure(ManagedBrowserProtocolError.InvalidPayload);
        if (!CapabilitiesMatch(capabilities))
            throw Failure(ManagedBrowserProtocolError.CapabilityMismatch);
        return new ManagedBrowserHandshakeResult(
            nonce, parentProcessId, helperProcessId, capabilities);
    }

    private static ManagedBrowserResult ParseSingletonResult(
        JsonElement value, string expectedKind, ManagedBrowserResult result)
    {
        ValidateObject(value, ["kind"], ManagedBrowserProtocolError.InvalidPayload);
        if (!string.Equals(RequireString(
                value, "kind", ManagedBrowserProtocolError.InvalidPayload),
                expectedKind, StringComparison.Ordinal))
            throw Failure(ManagedBrowserProtocolError.InvalidPayload);
        return result;
    }

    private static ManagedBrowserResult ParseIdentityResult(JsonElement value)
    {
        var kindText = RequireString(value, "kind", ManagedBrowserProtocolError.InvalidPayload);
        if (!Enum.TryParse(kindText, ignoreCase: false, out ManagedBrowserIdentityResultKind kind) ||
            !Enum.IsDefined(kind) || kind.ToString() != kindText)
            throw Failure(ManagedBrowserProtocolError.InvalidPayload);
        ValidateObject(value, kind == ManagedBrowserIdentityResultKind.Observed
            ? ["kind", "stableBaiduUserId"] : ["kind"], ManagedBrowserProtocolError.InvalidPayload);
        try { return new ManagedBrowserIdentityResult(
            kind, kind == ManagedBrowserIdentityResultKind.Observed
                ? RequireString(value, "stableBaiduUserId", ManagedBrowserProtocolError.InvalidPayload)
                : null); }
        catch (ArgumentException) { throw Failure(ManagedBrowserProtocolError.InvalidPayload); }
    }

    private static void WriteEnvelope(
        Utf8JsonWriter writer,
        Guid correlationId,
        Guid operationId,
        Guid accountId,
        long generation,
        ManagedBrowserAction action,
        string bodyName,
        Action writeBody)
    {
        writer.WriteStartObject();
        writer.WriteNumber("protocolVersion", ProtocolVersion);
        writer.WriteString("executionEngineVersion", ExecutionEngineVersion);
        writer.WriteString("correlationId", correlationId);
        writer.WriteString("operationId", operationId);
        writer.WriteString("accountId", accountId);
        writer.WriteNumber("generation", generation);
        writer.WriteString("action", action.ToString());
        writer.WritePropertyName(bodyName);
        writeBody();
        writer.WriteEndObject();
    }

    private static void WritePayload(Utf8JsonWriter writer, ManagedBrowserRequestPayload payload)
    {
        writer.WriteStartObject();
        switch (payload)
        {
            case ManagedBrowserHandshakeRequest handshake:
                writer.WriteString("nonce", handshake.Nonce);
                writer.WriteNumber("parentProcessId", handshake.ParentProcessId);
                writer.WritePropertyName("requiredCapabilities");
                WriteStrings(writer, handshake.RequiredCapabilities);
                break;
            case ManagedBrowserObserveAccountIdentityRequest observe:
                writer.WriteString("browserId", observe.BrowserId);
                writer.WriteString("targetId", observe.TargetId);
                writer.WriteString("devToolsEndpoint", observe.DevToolsEndpoint.AbsoluteUri);
                writer.WriteNumber("timeoutMilliseconds", observe.TimeoutMilliseconds);
                break;
            case ManagedBrowserOpenTransferSourceRequest source:
                writer.WriteString("browserId", source.BrowserId);
                writer.WriteString("targetId", source.TargetId);
                writer.WriteString("devToolsEndpoint", source.DevToolsEndpoint.AbsoluteUri);
                writer.WriteNumber("timeoutMilliseconds", source.TimeoutMilliseconds);
                writer.WriteString("sourceUri", source.SourceUri);
                writer.WriteString("expectedBaiduUserId", source.ExpectedBaiduUserId);
                break;
            case ManagedBrowserReadTransferSourceRequest read:
                WriteSourceBinding(writer, read.Binding);
                break;
            case ManagedBrowserReadTransferDirectoryRequest directory:
                WriteSourceBinding(writer, directory.Binding);
                writer.WriteString("expectedParentExactId", directory.ExpectedParentExactId);
                writer.WriteString("expectedCanonicalPath", directory.ExpectedCanonicalPath);
                break;
            case ManagedBrowserOpenTransferPickerRequest picker:
                WriteSourceBinding(writer, picker.Binding);
                writer.WritePropertyName("expectedSource");
                writer.WriteStartObject();
                WriteSourceListing(writer, new ManagedBrowserSourceListingResult(
                    ManagedBrowserSourceListingResultKind.Observed, picker.ExpectedSource));
                writer.WriteEndObject();
                break;
            case ManagedBrowserDirectoryMutationRequest mutation:
                WriteDirectoryMutationPayload(writer, mutation);
                break;
            case ManagedBrowserObserveTransferPickerClosedRequest closed:
                WriteSourceBinding(writer, closed.Binding);
                writer.WritePropertyName("expectedSource"); writer.WriteStartObject();
                WriteSourceListing(writer, new ManagedBrowserSourceListingResult(ManagedBrowserSourceListingResultKind.Observed, closed.ExpectedSource));
                writer.WriteEndObject();
                break;
            case ManagedBrowserOpenTransferTargetRootRequest targetRoot:
                WriteTargetBinding(writer, targetRoot.Binding);
                break;
            case ManagedBrowserEnterTransferTargetDirectoryRequest targetEnter:
                WriteTargetBinding(writer, targetEnter.Binding);
                writer.WritePropertyName("expectedParent"); writer.WriteStartObject();
                WriteTargetHeader(writer, targetEnter.ExpectedParent); writer.WriteEndObject();
                writer.WriteString("childExactId", targetEnter.ChildExactId);
                writer.WriteString("childServerName", targetEnter.ChildServerName);
                writer.WriteString("childCanonicalPath", targetEnter.ChildCanonicalPath);
                break;
            case ManagedBrowserReadTransferTargetDirectoryRequest targetRead:
                WriteTargetBinding(writer, targetRead.Binding);
                writer.WriteString("expectedDirectoryExactId", targetRead.ExpectedDirectoryExactId);
                writer.WriteString("expectedCanonicalPath", targetRead.ExpectedCanonicalPath);
                writer.WritePropertyName("expectedAncestorExactIds");
                if (targetRead.ExpectedAncestorExactIds is null) writer.WriteNullValue();
                else WriteStrings(writer, targetRead.ExpectedAncestorExactIds);
                break;
            case ManagedBrowserOpenTransferSourceAfterTargetReadRequest restore:
                WriteTargetBinding(writer, restore.Binding);
                writer.WritePropertyName("expectedTarget"); writer.WriteStartObject();
                WriteTargetHeader(writer, restore.ExpectedTarget); writer.WriteEndObject();
                break;
            case ManagedBrowserObserveTransferSubmissionReadyRequest or
                 ManagedBrowserSubmitTransferOnceRequest or
                 ManagedBrowserObserveTransferSubmissionOutcomeRequest:
                WriteTransferSubmissionRequest(writer, payload);
                break;
            case ManagedBrowserReadShareOfficialListingRequest or
                 ManagedBrowserSubmitShareOnceRequest or
                 ManagedBrowserObserveShareSubmissionOutcomeRequest:
                WriteShareSubmissionRequest(writer, payload);
                break;
            case ManagedBrowserSubmitExtractionCodeRequest extraction:
                WriteExtractionRequest(writer, extraction);
                break;
            default:
                if (!ReferenceEquals(payload, ManagedBrowserRequestPayload.Empty))
                    throw new ArgumentOutOfRangeException(nameof(payload));
                break;
        }
        writer.WriteEndObject();
    }

    private static void WriteResult(Utf8JsonWriter writer, ManagedBrowserResult result)
    {
        writer.WriteStartObject();
        switch (result)
        {
            case ManagedBrowserHandshakeResult handshake:
                writer.WriteString("kind", "HandshakeAccepted");
                writer.WriteString("nonce", handshake.Nonce);
                writer.WriteNumber("parentProcessId", handshake.ParentProcessId);
                writer.WriteNumber("helperProcessId", handshake.HelperProcessId);
                writer.WritePropertyName("capabilities");
                WriteStrings(writer, handshake.Capabilities);
                break;
            case ManagedBrowserHealthResult:
                writer.WriteString("kind", "Healthy");
                break;
            case ManagedBrowserShutdownResult:
                writer.WriteString("kind", "ShuttingDown");
                break;
            case ManagedBrowserIdentityResult identity:
                writer.WriteString("kind", identity.Kind.ToString());
                if (identity.Kind == ManagedBrowserIdentityResultKind.Observed)
                    writer.WriteString("stableBaiduUserId", identity.StableBaiduUserId);
                break;
            case ManagedBrowserTransferSourceResult source:
                writer.WriteString("kind", source.Kind.ToString());
                break;
            case ManagedBrowserSourceListingResult listing:
                WriteSourceListing(writer, listing);
                break;
            case ManagedBrowserDirectoryListingResult directory:
                WriteDirectoryListing(writer, directory);
                break;
            case ManagedBrowserTransferPickerResult picker:
                writer.WriteString("kind", picker.Kind.ToString());
                break;
            case ManagedBrowserDirectoryMutationResult mutation:
                writer.WriteString("kind", mutation.Kind.ToString());
                break;
            case ManagedBrowserTransferPickerClosedResult closed:
                writer.WriteString("kind", closed.Kind.ToString());
                break;
            case ManagedBrowserOpenTransferTargetRootResult targetRoot:
                writer.WriteString("kind", targetRoot.Kind.ToString());
                writer.WriteString("navigationState", targetRoot.NavigationState.ToString());
                break;
            case ManagedBrowserEnterTransferTargetDirectoryResult targetEnter:
                writer.WriteString("kind", targetEnter.Kind.ToString());
                writer.WriteString("navigationState", targetEnter.NavigationState.ToString());
                break;
            case ManagedBrowserReadTransferTargetDirectoryResult targetRead:
                WriteTargetInventory(writer, targetRead);
                break;
            case ManagedBrowserOpenTransferSourceAfterTargetReadResult restore:
                writer.WriteString("kind", restore.Kind.ToString());
                writer.WriteString("navigationState", restore.NavigationState.ToString());
                break;
            case ManagedBrowserTransferSubmissionReadinessResult or
                 ManagedBrowserSubmitTransferOnceResult or
                 ManagedBrowserTransferSubmissionOutcomeResult:
                WriteTransferSubmissionResult(writer, result);
                break;
            case ManagedBrowserReadShareOfficialListingResult or
                 ManagedBrowserSubmitShareOnceResult or
                 ManagedBrowserShareSubmissionOutcomeResult:
                WriteShareSubmissionResult(writer, result);
                break;
            case ManagedBrowserSubmitExtractionCodeResult extraction:
                WriteExtractionResult(writer, extraction);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(result));
        }
        writer.WriteEndObject();
    }

    private static void WriteStrings(Utf8JsonWriter writer, IEnumerable<string> values)
    {
        writer.WriteStartArray();
        foreach (var value in values) writer.WriteStringValue(value);
        writer.WriteEndArray();
    }

    private static string RequireString(
        JsonElement value,
        string name,
        ManagedBrowserProtocolError bodyError = ManagedBrowserProtocolError.InvalidMessage)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
            throw Failure(bodyError);
        // System.Text.Json parses lone escaped surrogates, but GetString refuses them.
        // Preserve the redacted protocol boundary rather than leaking decoder exceptions.
        try { return property.GetString()!; }
        catch (InvalidOperationException) { throw Failure(bodyError); }
    }

    private static Guid RequireGuid(
        JsonElement value,
        string name,
        ManagedBrowserProtocolError bodyError = ManagedBrowserProtocolError.InvalidMessage)
    {
        var text = RequireString(value, name, bodyError);
        if (!Guid.TryParseExact(text, "D", out var result) ||
            !string.Equals(text, result.ToString("D"), StringComparison.Ordinal))
            throw Failure(bodyError);
        return result;
    }

    private static int RequireInt32(
        JsonElement value,
        string name,
        ManagedBrowserProtocolError bodyError = ManagedBrowserProtocolError.InvalidMessage)
    {
        if (!value.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.Number ||
            !IsIntegerToken(property) ||
            !property.TryGetInt32(out var result))
            throw Failure(bodyError);
        return result;
    }

    private static long RequireInt64(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.Number ||
            !IsIntegerToken(property) ||
            !property.TryGetInt64(out var result))
            throw Failure(ManagedBrowserProtocolError.InvalidMessage);
        return result;
    }

    private static bool IsIntegerToken(JsonElement value)
    {
        var raw = value.GetRawText();
        return !raw.Contains('.') && !raw.Contains('e') && !raw.Contains('E');
    }

    private static JsonElement RequireObject(
        JsonElement value,
        string name,
        ManagedBrowserProtocolError structureError)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Object)
            throw Failure(structureError);
        return property;
    }

    private static string[] RequireStringArray(
        JsonElement value,
        string name,
        ManagedBrowserProtocolError bodyError = ManagedBrowserProtocolError.InvalidMessage)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Array)
            throw Failure(bodyError);
        var result = new List<string>();
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw Failure(bodyError);
            result.Add(item.GetString()!);
        }
        return result.ToArray();
    }

    private static ManagedBrowserProtocolException Failure(ManagedBrowserProtocolError error) => new(error);
}
