using System.Text.Json;
using System.Text.RegularExpressions;

namespace BaiduBatchTransfer.App.Features.ManagedBrowser;

public enum ManagedSessionOperationKind
{
    InspectSource = 1,
    EnsureDirectory = 2,
    SubmitTransfer = 3,
    ObserveTransfer = 4,
    SubmitShare = 5,
    ObserveShare = 6,
}

public sealed record ManagedSessionOperationBinding
{
    public ManagedSessionOperationBinding(
        Guid browserId,
        string targetId,
        Uri devToolsEndpoint,
        int timeoutMilliseconds,
        string baiduUserId)
    {
        if (browserId == Guid.Empty || !IsBoundedText(targetId, 256) ||
            !IsLoopbackEndpoint(devToolsEndpoint) || timeoutMilliseconds is < 1 or > 120_000 ||
            !IsBoundedText(baiduUserId, 256))
            throw new ArgumentException("The managed-session binding is invalid.");
        BrowserId = browserId;
        TargetId = targetId;
        DevToolsEndpoint = devToolsEndpoint;
        TimeoutMilliseconds = timeoutMilliseconds;
        BaiduUserId = baiduUserId;
    }

    public Guid BrowserId { get; }
    public string TargetId { get; }
    public Uri DevToolsEndpoint { get; }
    public int TimeoutMilliseconds { get; }
    public string BaiduUserId { get; }

    public override string ToString() =>
        $"ManagedSessionOperationBinding {{ TimeoutMilliseconds = {TimeoutMilliseconds}, Identity = [REDACTED] }}";

    private static bool IsLoopbackEndpoint(Uri? value) =>
        value is { IsAbsoluteUri: true, Scheme: "http", Host: "127.0.0.1" } &&
        value.Port is >= 1 and <= 65535 and not 80 &&
        value.AbsolutePath == "/" && string.IsNullOrEmpty(value.Query) && string.IsNullOrEmpty(value.Fragment) &&
        string.IsNullOrEmpty(value.UserInfo);

    internal static bool IsBoundedText(string? value, int maximumBytes) =>
        value is not null && value.Length > 0 && value == value.Trim() &&
        System.Text.Encoding.UTF8.GetByteCount(value) <= maximumBytes &&
        value.All(character => character is > '\u001f' and not '\u007f');
}

public sealed class ManagedSessionOperationRequest
{
    internal ManagedSessionOperationRequest(
        ManagedSessionOperationKind kind,
        Guid operationId,
        int attemptOrdinal,
        ManagedSessionOperationBinding binding,
        JsonElement arguments)
    {
        Kind = kind;
        OperationId = operationId;
        AttemptOrdinal = attemptOrdinal;
        Binding = binding;
        Arguments = arguments.Clone();
    }

    public ManagedSessionOperationKind Kind { get; }
    public Guid OperationId { get; }
    public int AttemptOrdinal { get; }
    public ManagedSessionOperationBinding Binding { get; }
    internal JsonElement Arguments { get; }

    public override string ToString() =>
        $"ManagedSessionOperationRequest {{ Kind = {Kind}, AttemptOrdinal = {AttemptOrdinal}, Binding = [REDACTED], Arguments = [REDACTED] }}";
}

public static partial class ManagedSessionOperationProtocol
{
    private static readonly string[] RequestFields =
        ["operation", "operationId", "attemptOrdinal", "binding", "arguments"];
    private static readonly string[] BindingFields =
        ["browserId", "targetId", "devToolsEndpoint", "timeoutMilliseconds", "baiduUserId"];
    private static readonly HashSet<string> ForbiddenFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "selector", "cookie", "authorization", "token", "origin", "referer", "url", "verb",
        "pageUrl", "pageText", "html", "buttonText", "toast",
    };
    private static readonly Regex SourceUriPattern = new(
        "^https://pan\\.baidu\\.com/s/[A-Za-z0-9_-]{1,128}(?:\\?pwd=[A-Za-z0-9]{4})?$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static ManagedSessionOperationRequest DecodeRequest(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            using var document = JsonDocument.Parse(utf8Json.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            var root = document.RootElement;
            RejectDuplicateAndForbiddenFields(root);
            ValidateExactObject(root, RequestFields);

            var kind = ParseKind(root.GetProperty("operation"));
            var operationId = ReadGuid(root, "operationId");
            var attemptOrdinal = ReadInt32(root, "attemptOrdinal");
            if (attemptOrdinal is < 1 or > 3)
                throw Failure(ManagedBrowserProtocolError.InvalidPayload);
            var binding = ParseBinding(root.GetProperty("binding"));
            var arguments = root.GetProperty("arguments");
            ValidateArguments(kind, arguments);
            return new ManagedSessionOperationRequest(kind, operationId, attemptOrdinal, binding, arguments);
        }
        catch (ManagedBrowserProtocolException) { throw; }
        catch (JsonException) { throw Failure(ManagedBrowserProtocolError.MalformedJson); }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidOperationException or OverflowException)
        {
            throw Failure(ManagedBrowserProtocolError.InvalidPayload);
        }
    }

    private static ManagedSessionOperationKind ParseKind(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
            throw Failure(ManagedBrowserProtocolError.UnknownAction);
        var text = value.GetString();
        if (!Enum.TryParse<ManagedSessionOperationKind>(text, false, out var kind) ||
            !Enum.IsDefined(kind) || kind.ToString() != text)
            throw Failure(ManagedBrowserProtocolError.UnknownAction);
        return kind;
    }

    private static ManagedSessionOperationBinding ParseBinding(JsonElement value)
    {
        ValidateExactObject(value, BindingFields);
        var browserId = ReadGuid(value, "browserId");
        var targetId = ReadString(value, "targetId");
        var endpointText = ReadString(value, "devToolsEndpoint");
        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint))
            throw Failure(ManagedBrowserProtocolError.InvalidPayload);
        return new ManagedSessionOperationBinding(
            browserId, targetId, endpoint, ReadInt32(value, "timeoutMilliseconds"),
            ReadString(value, "baiduUserId"));
    }

    private static void ValidateArguments(ManagedSessionOperationKind kind, JsonElement value)
    {
        switch (kind)
        {
            case ManagedSessionOperationKind.InspectSource:
                ValidateExactObject(value, ["sourceUri"]);
                ValidateSourceUri(ReadString(value, "sourceUri"));
                break;
            case ManagedSessionOperationKind.EnsureDirectory:
                ValidateExactObject(value, ["targetCanonicalPath"]);
                ValidateCanonicalPath(ReadString(value, "targetCanonicalPath"));
                break;
            case ManagedSessionOperationKind.SubmitTransfer:
                ValidateExactObject(value, ["sourceUri", "targetCanonicalPath", "duplicatePolicy"]);
                ValidateSourceUri(ReadString(value, "sourceUri"));
                ValidateCanonicalPath(ReadString(value, "targetCanonicalPath"));
                if (ReadString(value, "duplicatePolicy") != "Rename")
                    throw Failure(ManagedBrowserProtocolError.InvalidPayload);
                break;
            case ManagedSessionOperationKind.ObserveTransfer:
            case ManagedSessionOperationKind.ObserveShare:
                ValidateExactObject(value, ["adapterRequestId"]);
                if (!ManagedSessionOperationBinding.IsBoundedText(ReadString(value, "adapterRequestId"), 256))
                    throw Failure(ManagedBrowserProtocolError.InvalidPayload);
                break;
            case ManagedSessionOperationKind.SubmitShare:
                ValidateExactObject(value, ["exactItemIds", "fixedCode"]);
                ValidateExactItemIds(value.GetProperty("exactItemIds"));
                var code = ReadString(value, "fixedCode");
                if (code.Length != 4 || code.Any(character => character is not (>= 'a' and <= 'z') and not (>= '0' and <= '9')))
                    throw Failure(ManagedBrowserProtocolError.InvalidPayload);
                break;
            default:
                throw Failure(ManagedBrowserProtocolError.UnknownAction);
        }
    }

    private static void ValidateExactItemIds(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is < 1 or > 10_000)
            throw Failure(ManagedBrowserProtocolError.InvalidPayload);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in value.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String ||
                !ManagedSessionOperationBinding.IsBoundedText(element.GetString(), 256) ||
                !seen.Add(element.GetString()!))
                throw Failure(ManagedBrowserProtocolError.InvalidPayload);
        }
    }

    private static void ValidateSourceUri(string value)
    {
        if (!SourceUriPattern.IsMatch(value))
            throw Failure(ManagedBrowserProtocolError.InvalidPayload);
    }

    private static void ValidateCanonicalPath(string value)
    {
        if (!value.StartsWith("/", StringComparison.Ordinal) ||
            !ManagedSessionOperationBinding.IsBoundedText(value, 4096) || value.Contains("//", StringComparison.Ordinal))
            throw Failure(ManagedBrowserProtocolError.InvalidPayload);
    }

    private static void RejectDuplicateAndForbiddenFields(JsonElement value, int depth = 1)
    {
        if (depth > 16) throw Failure(ManagedBrowserProtocolError.MalformedJson);
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Failure(ManagedBrowserProtocolError.DuplicateField);
                if (ForbiddenFields.Contains(property.Name)) throw Failure(ManagedBrowserProtocolError.ForbiddenField);
                RejectDuplicateAndForbiddenFields(property.Value, depth + 1);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                RejectDuplicateAndForbiddenFields(item, depth + 1);
        }
    }

    private static void ValidateExactObject(JsonElement value, IReadOnlyCollection<string> fields)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw Failure(ManagedBrowserProtocolError.InvalidPayload);
        var actual = value.EnumerateObject().Select(property => property.Name).ToArray();
        if (actual.Any(name => !fields.Contains(name, StringComparer.Ordinal)))
            throw Failure(ManagedBrowserProtocolError.UnknownField);
        if (actual.Length != fields.Count || fields.Any(name => !actual.Contains(name, StringComparer.Ordinal)))
            throw Failure(ManagedBrowserProtocolError.InvalidPayload);
    }

    private static string ReadString(JsonElement value, string name)
    {
        var element = value.GetProperty(name);
        if (element.ValueKind != JsonValueKind.String)
            throw Failure(ManagedBrowserProtocolError.InvalidPayload);
        return element.GetString()!;
    }

    private static Guid ReadGuid(JsonElement value, string name)
    {
        var text = ReadString(value, name);
        return Guid.TryParseExact(text, "D", out var result) && result != Guid.Empty
            ? result
            : throw Failure(ManagedBrowserProtocolError.InvalidPayload);
    }

    private static int ReadInt32(JsonElement value, string name)
    {
        var element = value.GetProperty(name);
        return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var result)
            ? result
            : throw Failure(ManagedBrowserProtocolError.InvalidPayload);
    }

    private static ManagedBrowserProtocolException Failure(ManagedBrowserProtocolError error) => new(error);
}
