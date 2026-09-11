using System.Text.Json;

namespace BaiduBatchTransfer.App.Features.ManagedBrowser;

public sealed class ManagedBrowserTargetActionBinding
{
    public ManagedBrowserTargetActionBinding(Guid browserId, string targetId, Uri devToolsEndpoint,
        int timeoutMilliseconds, string sourceUri, string expectedBaiduUserId,
        ManagedBrowserSourceSnapshot expectedSource)
    {
        var source = new ManagedBrowserOpenTransferSourceRequest(browserId, targetId, devToolsEndpoint,
            timeoutMilliseconds, sourceUri, expectedBaiduUserId);
        ArgumentNullException.ThrowIfNull(expectedSource);
        if (!string.Equals(expectedSource.BaiduUserId, expectedBaiduUserId, StringComparison.Ordinal) ||
            !string.Equals(expectedSource.ComputeFingerprint(sourceUri), expectedSource.ObservationFingerprint, StringComparison.Ordinal))
            throw new ArgumentException("Exact source evidence is required.", nameof(expectedSource));
        BrowserId = browserId; TargetId = targetId; DevToolsEndpoint = devToolsEndpoint;
        TimeoutMilliseconds = timeoutMilliseconds; SourceUri = sourceUri;
        ExpectedBaiduUserId = expectedBaiduUserId; ExpectedSource = expectedSource;
    }

    public Guid BrowserId { get; }
    public string TargetId { get; }
    public Uri DevToolsEndpoint { get; }
    public int TimeoutMilliseconds { get; }
    public string SourceUri { get; }
    public string ExpectedBaiduUserId { get; }
    public ManagedBrowserSourceSnapshot ExpectedSource { get; }
    public override string ToString() =>
        $"ManagedBrowserTargetActionBinding {{ TimeoutMilliseconds = {TimeoutMilliseconds}, Binding = [REDACTED], Source = [REDACTED] }}";
}

public sealed class ManagedBrowserOpenTransferTargetRootRequest(ManagedBrowserTargetActionBinding binding) : ManagedBrowserRequestPayload
{
    public ManagedBrowserTargetActionBinding Binding { get; } = binding ?? throw new ArgumentNullException(nameof(binding));
    public override string ToString() => "ManagedBrowserOpenTransferTargetRootRequest { Binding = [REDACTED] }";
}

public sealed class ManagedBrowserEnterTransferTargetDirectoryRequest : ManagedBrowserRequestPayload
{
    public ManagedBrowserEnterTransferTargetDirectoryRequest(ManagedBrowserTargetActionBinding binding,
        ManagedBrowserTargetDirectoryHeader expectedParent, string childExactId, string childServerName,
        string childCanonicalPath)
    {
        ArgumentNullException.ThrowIfNull(binding); ArgumentNullException.ThrowIfNull(expectedParent);
        ManagedBrowserTargetInventoryValidation.ValidateExactId(childExactId, nameof(childExactId));
        ManagedBrowserTargetInventoryValidation.ValidateServerName(childServerName, nameof(childServerName));
        ManagedBrowserTargetInventoryValidation.ValidateCanonicalPath(childCanonicalPath);
        ValidateHeaderBinding(binding, expectedParent);
        var expectedPath = expectedParent.CanonicalPath == "/"
            ? "/" + childServerName : expectedParent.CanonicalPath + "/" + childServerName;
        if (!string.Equals(childCanonicalPath, expectedPath, StringComparison.Ordinal) ||
            expectedParent.AncestorExactIds.Contains(childExactId, StringComparer.Ordinal))
            throw new ArgumentException("The exact child tuple is inconsistent with the parent header.", nameof(childCanonicalPath));
        Binding = binding; ExpectedParent = expectedParent; ChildExactId = childExactId;
        ChildServerName = childServerName; ChildCanonicalPath = childCanonicalPath;
    }
    public ManagedBrowserTargetActionBinding Binding { get; }
    public ManagedBrowserTargetDirectoryHeader ExpectedParent { get; }
    public string ChildExactId { get; }
    public string ChildServerName { get; }
    public string ChildCanonicalPath { get; }
    public override string ToString() => "ManagedBrowserEnterTransferTargetDirectoryRequest { Binding = [REDACTED], Parent = [REDACTED], Child = [REDACTED] }";

    internal static void ValidateHeaderBinding(ManagedBrowserTargetActionBinding binding, ManagedBrowserTargetDirectoryHeader header)
    {
        if (binding.BrowserId != header.BrowserId ||
            !string.Equals(binding.TargetId, header.TargetId, StringComparison.Ordinal) ||
            binding.DevToolsEndpoint != header.DevToolsEndpoint ||
            !string.Equals(binding.ExpectedBaiduUserId, header.BaiduUserId, StringComparison.Ordinal))
            throw new ArgumentException("The target header binding does not match.", nameof(header));
    }
}

public sealed class ManagedBrowserReadTransferTargetDirectoryRequest : ManagedBrowserRequestPayload
{
    public ManagedBrowserReadTransferTargetDirectoryRequest(ManagedBrowserTargetActionBinding binding,
        string? expectedDirectoryExactId, string? expectedCanonicalPath,
        IReadOnlyList<string>? expectedAncestorExactIds)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var omitted = expectedDirectoryExactId is null && expectedCanonicalPath is null && expectedAncestorExactIds is null;
        if (!omitted)
        {
            if (expectedDirectoryExactId is null || expectedCanonicalPath is null || expectedAncestorExactIds is null)
                throw new ArgumentException("Expected target proof must be supplied as one exact group.");
            ManagedBrowserTargetInventoryValidation.ValidateDirectoryIdentity(
                expectedDirectoryExactId, expectedCanonicalPath, expectedAncestorExactIds);
        }
        Binding = binding; ExpectedDirectoryExactId = expectedDirectoryExactId;
        ExpectedCanonicalPath = expectedCanonicalPath;
        ExpectedAncestorExactIds = expectedAncestorExactIds is null
            ? null
            : Array.AsReadOnly(expectedAncestorExactIds.ToArray());
    }
    public ManagedBrowserTargetActionBinding Binding { get; }
    public string? ExpectedDirectoryExactId { get; }
    public string? ExpectedCanonicalPath { get; }
    public IReadOnlyList<string>? ExpectedAncestorExactIds { get; }
    public override string ToString() => "ManagedBrowserReadTransferTargetDirectoryRequest { Binding = [REDACTED], Target = [REDACTED] }";
}

public sealed class ManagedBrowserOpenTransferSourceAfterTargetReadRequest : ManagedBrowserRequestPayload
{
    public ManagedBrowserOpenTransferSourceAfterTargetReadRequest(ManagedBrowserTargetActionBinding binding,
        ManagedBrowserTargetDirectoryHeader expectedTarget)
    {
        ArgumentNullException.ThrowIfNull(binding); ArgumentNullException.ThrowIfNull(expectedTarget);
        ManagedBrowserEnterTransferTargetDirectoryRequest.ValidateHeaderBinding(binding, expectedTarget);
        Binding = binding; ExpectedTarget = expectedTarget;
    }
    public ManagedBrowserTargetActionBinding Binding { get; }
    public ManagedBrowserTargetDirectoryHeader ExpectedTarget { get; }
    public override string ToString() => "ManagedBrowserOpenTransferSourceAfterTargetReadRequest { Binding = [REDACTED], Target = [REDACTED] }";
}

public enum ManagedBrowserTargetNavigationState { KnownNotDispatched = 1, RouteOpened = 2, RouteOutcomeUnknown = 3 }

public enum ManagedBrowserOpenTransferTargetRootResultKind
{
    RootOpened = 1, PageNotValidated, AccountIdentityMismatch, LoginRequired, VerificationRequired,
    BrowserUnavailable, TimedOut, Cancelled,
}

public enum ManagedBrowserEnterTransferTargetDirectoryResultKind
{
    DirectoryEntered = 1, PageNotValidated, AccountIdentityMismatch, LoginRequired, VerificationRequired,
    BrowserUnavailable, TimedOut, Cancelled,
}

public enum ManagedBrowserOpenTransferSourceAfterTargetReadResultKind
{
    SourceRestored = 1, PageNotValidated, AccountIdentityMismatch, LoginRequired, VerificationRequired,
    BrowserUnavailable, TimedOut, Cancelled,
}

public sealed class ManagedBrowserOpenTransferTargetRootResult : ManagedBrowserResult
{
    public ManagedBrowserOpenTransferTargetRootResult(ManagedBrowserOpenTransferTargetRootResultKind kind,
        ManagedBrowserTargetNavigationState navigationState)
    { ValidateNavigation(kind == ManagedBrowserOpenTransferTargetRootResultKind.RootOpened, navigationState); Kind = kind; NavigationState = navigationState; }
    public ManagedBrowserOpenTransferTargetRootResultKind Kind { get; }
    public ManagedBrowserTargetNavigationState NavigationState { get; }
    public override string ToString() => $"ManagedBrowserOpenTransferTargetRootResult {{ Kind = {Kind}, NavigationState = {NavigationState} }}";
    internal static void ValidateNavigation(bool success, ManagedBrowserTargetNavigationState state)
    {
        if (!Enum.IsDefined(state) || (success ? state != ManagedBrowserTargetNavigationState.RouteOpened : state == ManagedBrowserTargetNavigationState.RouteOpened))
            throw new ArgumentException("The navigation state is inconsistent with the result.", nameof(state));
    }
}

public sealed class ManagedBrowserEnterTransferTargetDirectoryResult : ManagedBrowserResult
{
    public ManagedBrowserEnterTransferTargetDirectoryResult(ManagedBrowserEnterTransferTargetDirectoryResultKind kind,
        ManagedBrowserTargetNavigationState navigationState)
    { ManagedBrowserOpenTransferTargetRootResult.ValidateNavigation(kind == ManagedBrowserEnterTransferTargetDirectoryResultKind.DirectoryEntered, navigationState); Kind = kind; NavigationState = navigationState; }
    public ManagedBrowserEnterTransferTargetDirectoryResultKind Kind { get; }
    public ManagedBrowserTargetNavigationState NavigationState { get; }
    public override string ToString() => $"ManagedBrowserEnterTransferTargetDirectoryResult {{ Kind = {Kind}, NavigationState = {NavigationState} }}";
}

public sealed class ManagedBrowserOpenTransferSourceAfterTargetReadResult : ManagedBrowserResult
{
    public ManagedBrowserOpenTransferSourceAfterTargetReadResult(ManagedBrowserOpenTransferSourceAfterTargetReadResultKind kind,
        ManagedBrowserTargetNavigationState navigationState)
    { ManagedBrowserOpenTransferTargetRootResult.ValidateNavigation(kind == ManagedBrowserOpenTransferSourceAfterTargetReadResultKind.SourceRestored, navigationState); Kind = kind; NavigationState = navigationState; }
    public ManagedBrowserOpenTransferSourceAfterTargetReadResultKind Kind { get; }
    public ManagedBrowserTargetNavigationState NavigationState { get; }
    public override string ToString() => $"ManagedBrowserOpenTransferSourceAfterTargetReadResult {{ Kind = {Kind}, NavigationState = {NavigationState} }}";
}

public enum ManagedBrowserReadTransferTargetDirectoryResultKind
{
    Observed = 1, PageNotValidated, AccountIdentityMismatch, LoginRequired, VerificationRequired,
    BrowserUnavailable, TimedOut, Cancelled,
}

public sealed class ManagedBrowserReadTransferTargetDirectoryResult : ManagedBrowserResult
{
    public ManagedBrowserReadTransferTargetDirectoryResult(ManagedBrowserReadTransferTargetDirectoryResultKind kind,
        ManagedBrowserTargetInventorySnapshot? snapshot = null)
    {
        if (!Enum.IsDefined(kind) || (kind == ManagedBrowserReadTransferTargetDirectoryResultKind.Observed) != (snapshot is not null))
            throw new ArgumentException("Only an observed result may contain a target inventory.", nameof(snapshot));
        Kind = kind; Snapshot = snapshot;
    }
    public ManagedBrowserReadTransferTargetDirectoryResultKind Kind { get; }
    public ManagedBrowserTargetInventorySnapshot? Snapshot { get; }
    public override string ToString() => $"ManagedBrowserReadTransferTargetDirectoryResult {{ Kind = {Kind}, Inventory = [REDACTED] }}";
}

public static partial class ManagedBrowserProtocol
{
    private static ManagedBrowserTargetActionBinding ParseTargetBinding(JsonElement value)
    {
        if (!Uri.TryCreate(RequireString(value, "devToolsEndpoint", ManagedBrowserProtocolError.InvalidPayload), UriKind.Absolute, out var endpoint))
            throw Failure(ManagedBrowserProtocolError.InvalidPayload);
        var source = (ManagedBrowserSourceListingResult)ParseSourceListingResult(value.GetProperty("expectedSource"));
        if (source.Kind != ManagedBrowserSourceListingResultKind.Observed) throw Failure(ManagedBrowserProtocolError.InvalidPayload);
        try { return new ManagedBrowserTargetActionBinding(RequireGuid(value, "browserId", ManagedBrowserProtocolError.InvalidPayload),
            RequireString(value, "targetId", ManagedBrowserProtocolError.InvalidPayload), endpoint,
            RequireInt32(value, "timeoutMilliseconds", ManagedBrowserProtocolError.InvalidPayload),
            RequireString(value, "sourceUri", ManagedBrowserProtocolError.InvalidPayload),
            RequireString(value, "expectedBaiduUserId", ManagedBrowserProtocolError.InvalidPayload), source.Snapshot!); }
        catch (ArgumentException) { throw Failure(ManagedBrowserProtocolError.InvalidPayload); }
    }

    private static ManagedBrowserRequestPayload ParseOpenTransferTargetRootRequest(JsonElement value)
    {
        ValidateObject(value, TargetBindingFields, ManagedBrowserProtocolError.InvalidPayload);
        return new ManagedBrowserOpenTransferTargetRootRequest(ParseTargetBinding(value));
    }

    private static ManagedBrowserRequestPayload ParseEnterTransferTargetDirectoryRequest(JsonElement value)
    {
        ValidateObject(value, [.. TargetBindingFields, "expectedParent", "childExactId", "childServerName", "childCanonicalPath"],
            ManagedBrowserProtocolError.InvalidPayload);
        try { return new ManagedBrowserEnterTransferTargetDirectoryRequest(ParseTargetBinding(value),
            ParseTargetHeader(RequireObject(value, "expectedParent", ManagedBrowserProtocolError.InvalidPayload)),
            RequireString(value, "childExactId", ManagedBrowserProtocolError.InvalidPayload),
            RequireString(value, "childServerName", ManagedBrowserProtocolError.InvalidPayload),
            RequireString(value, "childCanonicalPath", ManagedBrowserProtocolError.InvalidPayload)); }
        catch (ArgumentException) { throw Failure(ManagedBrowserProtocolError.InvalidPayload); }
    }

    private static ManagedBrowserRequestPayload ParseReadTransferTargetDirectoryRequest(JsonElement value)
    {
        ValidateObject(value, [.. TargetBindingFields, "expectedDirectoryExactId", "expectedCanonicalPath", "expectedAncestorExactIds"],
            ManagedBrowserProtocolError.InvalidPayload);
        try
        {
            var id = value.GetProperty("expectedDirectoryExactId").ValueKind == JsonValueKind.Null
                ? null : RequireString(value, "expectedDirectoryExactId", ManagedBrowserProtocolError.InvalidPayload);
            var path = value.GetProperty("expectedCanonicalPath").ValueKind == JsonValueKind.Null
                ? null : RequireString(value, "expectedCanonicalPath", ManagedBrowserProtocolError.InvalidPayload);
            IReadOnlyList<string>? ancestors = value.GetProperty("expectedAncestorExactIds").ValueKind == JsonValueKind.Null
                ? null : RequireStringArray(value, "expectedAncestorExactIds", ManagedBrowserProtocolError.InvalidPayload);
            return new ManagedBrowserReadTransferTargetDirectoryRequest(ParseTargetBinding(value), id, path, ancestors);
        }
        catch (ArgumentException) { throw Failure(ManagedBrowserProtocolError.InvalidPayload); }
    }

    private static ManagedBrowserRequestPayload ParseOpenTransferSourceAfterTargetReadRequest(JsonElement value)
    {
        ValidateObject(value, [.. TargetBindingFields, "expectedTarget"], ManagedBrowserProtocolError.InvalidPayload);
        try { return new ManagedBrowserOpenTransferSourceAfterTargetReadRequest(ParseTargetBinding(value),
            ParseTargetHeader(RequireObject(value, "expectedTarget", ManagedBrowserProtocolError.InvalidPayload))); }
        catch (ArgumentException) { throw Failure(ManagedBrowserProtocolError.InvalidPayload); }
    }

    private static ManagedBrowserResult ParseOpenTransferTargetRootResult(JsonElement value) =>
        ParseNavigationResult(value,
            (kind, state) => new ManagedBrowserOpenTransferTargetRootResult(
                ParseExactEnum<ManagedBrowserOpenTransferTargetRootResultKind>(kind), state));

    private static ManagedBrowserResult ParseEnterTransferTargetDirectoryResult(JsonElement value) =>
        ParseNavigationResult(value,
            (kind, state) => new ManagedBrowserEnterTransferTargetDirectoryResult(
                ParseExactEnum<ManagedBrowserEnterTransferTargetDirectoryResultKind>(kind), state));

    private static ManagedBrowserResult ParseOpenTransferSourceAfterTargetReadResult(JsonElement value) =>
        ParseNavigationResult(value,
            (kind, state) => new ManagedBrowserOpenTransferSourceAfterTargetReadResult(
                ParseExactEnum<ManagedBrowserOpenTransferSourceAfterTargetReadResultKind>(kind), state));

    private static ManagedBrowserResult ParseNavigationResult(JsonElement value,
        Func<string, ManagedBrowserTargetNavigationState, ManagedBrowserResult> factory)
    {
        var error = ManagedBrowserProtocolError.InvalidPayload;
        ValidateObject(value, ["kind", "navigationState"], error);
        try { return factory(RequireString(value, "kind", error),
            ParseExactEnum<ManagedBrowserTargetNavigationState>(RequireString(value, "navigationState", error))); }
        catch (ArgumentException) { throw Failure(error); }
    }

    private static ManagedBrowserResult ParseReadTransferTargetDirectoryResult(JsonElement value)
    {
        var error = ManagedBrowserProtocolError.InvalidPayload;
        var kind = ParseExactEnum<ManagedBrowserReadTransferTargetDirectoryResultKind>(
            RequireString(value, "kind", error));
        if (kind != ManagedBrowserReadTransferTargetDirectoryResultKind.Observed)
        {
            ValidateObject(value, ["kind"], error);
            return new ManagedBrowserReadTransferTargetDirectoryResult(kind);
        }
        ValidateObject(value, TargetInventoryFields, error);
        try
        {
            if (!Uri.TryCreate(RequireString(value, "devToolsEndpoint", error), UriKind.Absolute, out var endpoint))
                throw new ArgumentException();
            var items = new List<ManagedBrowserTargetInventoryItem>();
            var array = value.GetProperty("items");
            if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > ManagedBrowserTargetInventorySnapshot.MaximumItems)
                throw new ArgumentException();
            foreach (var item in array.EnumerateArray())
            {
                ValidateObject(item, ["exactId", "kind", "serverName"], error);
                items.Add(new ManagedBrowserTargetInventoryItem(
                    RequireString(item, "exactId", error),
                    ParseExactEnum<ManagedBrowserTargetInventoryItemKind>(RequireString(item, "kind", error)),
                    RequireString(item, "serverName", error)));
            }
            var snapshot = new ManagedBrowserTargetInventorySnapshot(
                RequireGuid(value, "browserId", error), RequireString(value, "targetId", error), endpoint,
                RequireString(value, "baiduUserId", error), RequireString(value, "generationToken", error),
                RequireString(value, "directoryExactId", error), RequireString(value, "canonicalPath", error),
                RequireStringArray(value, "ancestorExactIds", error), RequireInt32(value, "totalCount", error),
                RequireBoolean(value, "reachedEnd", error), RequireBoolean(value, "isStable", error),
                RequireInt32(value, "enumerationStepCount", error), items,
                RequireString(value, "observationFingerprint", error));
            return new ManagedBrowserReadTransferTargetDirectoryResult(kind, snapshot);
        }
        catch (ArgumentException) { throw Failure(error); }
    }

    private static T ParseExactEnum<T>(string value) where T : struct, Enum
    {
        if (!Enum.TryParse<T>(value, false, out var parsed) || !Enum.IsDefined(parsed) || parsed.ToString() != value)
            throw Failure(ManagedBrowserProtocolError.InvalidPayload);
        return parsed;
    }

    private static ManagedBrowserTargetDirectoryHeader ParseTargetHeader(JsonElement value)
    {
        var error = ManagedBrowserProtocolError.InvalidPayload;
        ValidateObject(value, TargetHeaderFields, error);
        if (!Uri.TryCreate(RequireString(value, "devToolsEndpoint", error), UriKind.Absolute, out var endpoint))
            throw Failure(error);
        try { return new ManagedBrowserTargetDirectoryHeader(
            ParseExactEnum<ManagedBrowserTargetInventoryScope>(RequireString(value, "scope", error)),
            RequireGuid(value, "browserId", error), RequireString(value, "targetId", error), endpoint,
            RequireString(value, "baiduUserId", error), RequireString(value, "generationToken", error),
            RequireString(value, "directoryExactId", error), RequireString(value, "canonicalPath", error),
            RequireStringArray(value, "ancestorExactIds", error), RequireInt32(value, "totalCount", error),
            RequireBoolean(value, "reachedEnd", error), RequireBoolean(value, "isStable", error),
            RequireInt32(value, "enumerationStepCount", error),
            RequireString(value, "parentObservationFingerprint", error)); }
        catch (ArgumentException) { throw Failure(error); }
    }

    private static readonly string[] TargetBindingFields =
        ["browserId", "targetId", "devToolsEndpoint", "timeoutMilliseconds", "sourceUri", "expectedBaiduUserId", "expectedSource"];
    private static readonly string[] TargetHeaderFields =
        ["scope", "browserId", "targetId", "devToolsEndpoint", "baiduUserId", "generationToken", "directoryExactId",
         "canonicalPath", "ancestorExactIds", "totalCount", "reachedEnd", "isStable", "enumerationStepCount", "parentObservationFingerprint"];
    private static readonly string[] TargetInventoryFields =
        ["kind", "scope", "browserId", "targetId", "devToolsEndpoint", "baiduUserId", "generationToken", "directoryExactId",
         "canonicalPath", "ancestorExactIds", "totalCount", "reachedEnd", "isStable", "enumerationStepCount", "items", "observationFingerprint"];

    private static void WriteTargetBinding(Utf8JsonWriter writer, ManagedBrowserTargetActionBinding binding)
    {
        writer.WriteString("browserId", binding.BrowserId); writer.WriteString("targetId", binding.TargetId);
        writer.WriteString("devToolsEndpoint", binding.DevToolsEndpoint.AbsoluteUri);
        writer.WriteNumber("timeoutMilliseconds", binding.TimeoutMilliseconds); writer.WriteString("sourceUri", binding.SourceUri);
        writer.WriteString("expectedBaiduUserId", binding.ExpectedBaiduUserId);
        writer.WritePropertyName("expectedSource"); writer.WriteStartObject();
        WriteSourceListing(writer, new ManagedBrowserSourceListingResult(ManagedBrowserSourceListingResultKind.Observed, binding.ExpectedSource));
        writer.WriteEndObject();
    }

    private static void WriteTargetHeader(Utf8JsonWriter writer, ManagedBrowserTargetDirectoryHeader header)
    {
        writer.WriteString("scope", header.Scope.ToString()); writer.WriteString("browserId", header.BrowserId);
        writer.WriteString("targetId", header.TargetId); writer.WriteString("devToolsEndpoint", header.DevToolsEndpoint.AbsoluteUri);
        writer.WriteString("baiduUserId", header.BaiduUserId); writer.WriteString("generationToken", header.GenerationToken);
        writer.WriteString("directoryExactId", header.DirectoryExactId); writer.WriteString("canonicalPath", header.CanonicalPath);
        writer.WritePropertyName("ancestorExactIds"); WriteStrings(writer, header.AncestorExactIds);
        writer.WriteNumber("totalCount", header.TotalCount); writer.WriteBoolean("reachedEnd", header.ReachedEnd);
        writer.WriteBoolean("isStable", header.IsStable); writer.WriteNumber("enumerationStepCount", header.EnumerationStepCount);
        writer.WriteString("parentObservationFingerprint", header.ParentObservationFingerprint);
    }

    private static void WriteTargetInventory(Utf8JsonWriter writer, ManagedBrowserReadTransferTargetDirectoryResult result)
    {
        writer.WriteString("kind", result.Kind.ToString());
        if (result.Snapshot is not { } snapshot) return;
        writer.WriteString("scope", snapshot.Scope.ToString()); writer.WriteString("browserId", snapshot.BrowserId);
        writer.WriteString("targetId", snapshot.TargetId); writer.WriteString("devToolsEndpoint", snapshot.DevToolsEndpoint.AbsoluteUri);
        writer.WriteString("baiduUserId", snapshot.BaiduUserId); writer.WriteString("generationToken", snapshot.GenerationToken);
        writer.WriteString("directoryExactId", snapshot.DirectoryExactId); writer.WriteString("canonicalPath", snapshot.CanonicalPath);
        writer.WritePropertyName("ancestorExactIds"); WriteStrings(writer, snapshot.AncestorExactIds);
        writer.WriteNumber("totalCount", snapshot.TotalCount); writer.WriteBoolean("reachedEnd", snapshot.ReachedEnd);
        writer.WriteBoolean("isStable", snapshot.IsStable); writer.WriteNumber("enumerationStepCount", snapshot.EnumerationStepCount);
        writer.WritePropertyName("items"); writer.WriteStartArray();
        foreach (var item in snapshot.Items)
        {
            writer.WriteStartObject(); writer.WriteString("exactId", item.ExactId); writer.WriteString("kind", item.Kind.ToString());
            writer.WriteString("serverName", item.ServerName); writer.WriteEndObject();
        }
        writer.WriteEndArray(); writer.WriteString("observationFingerprint", snapshot.ObservationFingerprint);
    }

    private static bool RequireBoolean(JsonElement value, string name, ManagedBrowserProtocolError error)
    {
        var property = value.GetProperty(name);
        if (property.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Failure(error);
        return property.GetBoolean();
    }
}
