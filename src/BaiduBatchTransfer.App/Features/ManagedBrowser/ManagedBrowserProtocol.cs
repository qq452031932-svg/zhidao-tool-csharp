using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;

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
}

public static class ManagedBrowserRoutePolicy
{
    public static ManagedBrowserRoute Classify(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !uri.IsDefaultPort ||
            !string.Equals(uri.AbsolutePath, "/", StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            return ManagedBrowserRoute.Rejected;
        }

        if (string.Equals(uri.IdnHost, "pan.baidu.com", StringComparison.Ordinal))
            return ManagedBrowserRoute.PanRoot;
        if (string.Equals(uri.IdnHost, "passport.baidu.com", StringComparison.Ordinal))
            return ManagedBrowserRoute.PassportRoot;
        return ManagedBrowserRoute.Rejected;
    }

    public static bool CanTransition(ManagedBrowserRoute from, ManagedBrowserRoute to) =>
        (from, to) is
            (ManagedBrowserRoute.PanRoot, ManagedBrowserRoute.PanRoot) or
            (ManagedBrowserRoute.PanRoot, ManagedBrowserRoute.PassportRoot) or
            (ManagedBrowserRoute.PassportRoot, ManagedBrowserRoute.PassportRoot) or
            (ManagedBrowserRoute.PassportRoot, ManagedBrowserRoute.PanRoot);

    public static bool CanObserveIdentity(ManagedBrowserRoute route) =>
        route == ManagedBrowserRoute.PanRoot;
}

public static class ManagedBrowserProtocol
{
    public const int ProtocolVersion = 1;
    public const string ExecutionEngineVersion = "ManagedChromium-v1";
    public const int MaximumFrameBytes = 65_536;
    public const int MaximumOperationTimeoutMilliseconds = 120_000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] Capabilities =
        ["Health", "Shutdown", "ObserveAccountIdentity"];
    private static readonly HashSet<string> ForbiddenNames = new(
        ["cookie", "authorization", "extractionCode", "pageUrl", "pageText", "html"],
        StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> RequiredCapabilities { get; } = Array.AsReadOnly(Capabilities);

    public static byte[] EncodeRequest(ManagedBrowserRequest request) => Frame(EncodeRequestPayload(request));
    public static byte[] EncodeResponse(ManagedBrowserResponse response) => Frame(EncodeResponsePayload(response));

    public static byte[] EncodeRequestPayload(ManagedBrowserRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteEnvelope(writer, request.CorrelationId, request.OperationId, request.AccountId,
                request.Generation, request.Action, "payload", () => WritePayload(writer, request.Payload));
        }
        return stream.ToArray();
    }

    public static byte[] EncodeResponsePayload(ManagedBrowserResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
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
        var value = RequireObject(root, "payload");
        ManagedBrowserRequestPayload requestPayload = action switch
        {
            ManagedBrowserAction.Handshake => ParseHandshakeRequest(value),
            ManagedBrowserAction.Health or ManagedBrowserAction.Shutdown => ParseEmptyPayload(value),
            ManagedBrowserAction.ObserveAccountIdentity => ParseObserveRequest(value),
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
        var value = RequireObject(root, "result");
        ManagedBrowserResult result = action switch
        {
            ManagedBrowserAction.Handshake => ParseHandshakeResult(value),
            ManagedBrowserAction.Health => ParseSingletonResult(value, "Healthy", ManagedBrowserHealthResult.Healthy),
            ManagedBrowserAction.Shutdown => ParseSingletonResult(value, "ShuttingDown", ManagedBrowserShutdownResult.ShuttingDown),
            ManagedBrowserAction.ObserveAccountIdentity => ParseIdentityResult(value),
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
            value.Length > maximumLength ||
            value.Any(char.IsControl))
        {
            throw new ArgumentException("A bounded non-empty value is required.", parameterName);
        }
    }

    internal static bool IsVerifiedLoopbackEndpoint(Uri uri) =>
        uri.IsAbsoluteUri &&
        string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) &&
        IPAddress.TryParse(uri.Host, out var address) &&
        IPAddress.IsLoopback(address) &&
        uri.Port is >= 1 and <= 65535 &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        string.Equals(uri.AbsolutePath, "/", StringComparison.Ordinal) &&
        string.IsNullOrEmpty(uri.Query) &&
        string.IsNullOrEmpty(uri.Fragment);

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
            _ => false,
        };
        if (!valid) throw new ArgumentException("The result does not match the action.", nameof(result));
    }

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
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                throw Failure(ManagedBrowserProtocolError.InvalidMessage);
            }
            ScanForbidden(document.RootElement);
            return document;
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

    private static void ValidateObject(JsonElement element, string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw Failure(ManagedBrowserProtocolError.InvalidMessage);
        var allowedSet = new HashSet<string>(allowed, StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!allowedSet.Contains(property.Name))
                throw Failure(ManagedBrowserProtocolError.UnknownField);
        if (element.EnumerateObject().Count() != allowed.Length)
            throw Failure(ManagedBrowserProtocolError.InvalidMessage);
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
            !protocolProperty.TryGetInt32(out var protocolVersion) ||
            protocolVersion != ProtocolVersion)
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
        ValidateObject(value, ["nonce", "parentProcessId", "requiredCapabilities"]);
        var capabilities = RequireStringArray(value, "requiredCapabilities");
        if (!CapabilitiesMatch(capabilities))
            throw Failure(ManagedBrowserProtocolError.CapabilityMismatch);
        try { return new ManagedBrowserHandshakeRequest(
            RequireString(value, "nonce"), RequireInt32(value, "parentProcessId"), capabilities); }
        catch (ArgumentException) { throw Failure(ManagedBrowserProtocolError.InvalidPayload); }
    }

    private static ManagedBrowserRequestPayload ParseEmptyPayload(JsonElement value)
    {
        ValidateObject(value, []);
        return ManagedBrowserRequestPayload.Empty;
    }

    private static ManagedBrowserRequestPayload ParseObserveRequest(JsonElement value)
    {
        ValidateObject(value, ["browserId", "targetId", "devToolsEndpoint", "timeoutMilliseconds"]);
        try
        {
            var endpointText = RequireString(value, "devToolsEndpoint");
            if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint))
                throw new ArgumentException("Invalid endpoint.");
            return new ManagedBrowserObserveAccountIdentityRequest(
                RequireGuid(value, "browserId"), RequireString(value, "targetId"), endpoint,
                RequireInt32(value, "timeoutMilliseconds"));
        }
        catch (ArgumentException) { throw Failure(ManagedBrowserProtocolError.InvalidPayload); }
    }

    private static ManagedBrowserResult ParseHandshakeResult(JsonElement value)
    {
        ValidateObject(value, ["kind", "nonce", "parentProcessId", "helperProcessId", "capabilities"]);
        if (!string.Equals(RequireString(value, "kind"), "HandshakeAccepted", StringComparison.Ordinal))
            throw Failure(ManagedBrowserProtocolError.InvalidPayload);
        var capabilities = RequireStringArray(value, "capabilities");
        if (!CapabilitiesMatch(capabilities))
            throw Failure(ManagedBrowserProtocolError.CapabilityMismatch);
        try { return new ManagedBrowserHandshakeResult(
            RequireString(value, "nonce"), RequireInt32(value, "parentProcessId"),
            RequireInt32(value, "helperProcessId"), capabilities); }
        catch (ArgumentException) { throw Failure(ManagedBrowserProtocolError.InvalidPayload); }
    }

    private static ManagedBrowserResult ParseSingletonResult(
        JsonElement value, string expectedKind, ManagedBrowserResult result)
    {
        ValidateObject(value, ["kind"]);
        if (!string.Equals(RequireString(value, "kind"), expectedKind, StringComparison.Ordinal))
            throw Failure(ManagedBrowserProtocolError.InvalidPayload);
        return result;
    }

    private static ManagedBrowserResult ParseIdentityResult(JsonElement value)
    {
        var kindText = RequireString(value, "kind");
        if (!Enum.TryParse(kindText, ignoreCase: false, out ManagedBrowserIdentityResultKind kind) ||
            !Enum.IsDefined(kind) || kind.ToString() != kindText)
            throw Failure(ManagedBrowserProtocolError.InvalidPayload);
        ValidateObject(value, kind == ManagedBrowserIdentityResultKind.Observed
            ? ["kind", "stableBaiduUserId"] : ["kind"]);
        try { return new ManagedBrowserIdentityResult(
            kind, kind == ManagedBrowserIdentityResultKind.Observed
                ? RequireString(value, "stableBaiduUserId") : null); }
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

    private static string RequireString(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
            throw Failure(ManagedBrowserProtocolError.InvalidMessage);
        return property.GetString()!;
    }

    private static Guid RequireGuid(JsonElement value, string name)
    {
        var text = RequireString(value, name);
        if (!Guid.TryParseExact(text, "D", out var result) ||
            !string.Equals(text, result.ToString("D"), StringComparison.Ordinal))
            throw Failure(ManagedBrowserProtocolError.InvalidMessage);
        return result;
    }

    private static int RequireInt32(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out var result))
            throw Failure(ManagedBrowserProtocolError.InvalidMessage);
        return result;
    }

    private static long RequireInt64(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.Number || !property.TryGetInt64(out var result))
            throw Failure(ManagedBrowserProtocolError.InvalidMessage);
        return result;
    }

    private static JsonElement RequireObject(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Object)
            throw Failure(ManagedBrowserProtocolError.InvalidMessage);
        return property;
    }

    private static string[] RequireStringArray(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Array)
            throw Failure(ManagedBrowserProtocolError.InvalidMessage);
        var result = new List<string>();
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw Failure(ManagedBrowserProtocolError.InvalidMessage);
            result.Add(item.GetString()!);
        }
        return result.ToArray();
    }

    private static ManagedBrowserProtocolException Failure(ManagedBrowserProtocolError error) => new(error);
}
