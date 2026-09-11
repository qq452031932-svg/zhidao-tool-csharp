using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.App.Features.ManagedBrowser;

public sealed class ManagedBrowserShareSubmissionContext
{
    public ManagedBrowserShareSubmissionContext(
        Guid browserId,
        string targetId,
        Uri devToolsEndpoint,
        int timeoutMilliseconds,
        string baiduUserId,
        string targetDirectoryExactId,
        string targetCanonicalPath,
        RemoteItemIdentitySet exactItems,
        ShareExtractionCodeBehavior codeMode,
        string? fixedCode)
    {
        var binding = new ManagedBrowserObserveAccountIdentityRequest(
            browserId, targetId, devToolsEndpoint, timeoutMilliseconds);
        ManagedBrowserProtocol.ValidateBoundedText(baiduUserId, nameof(baiduUserId), 256);
        ManagedBrowserTargetInventoryValidation.ValidateExactId(
            targetDirectoryExactId, nameof(targetDirectoryExactId));
        ManagedBrowserTargetInventoryValidation.ValidateCanonicalPath(targetCanonicalPath);
        ArgumentNullException.ThrowIfNull(exactItems);
        if (codeMode is not (ShareExtractionCodeBehavior.Random or ShareExtractionCodeBehavior.Fixed))
            throw new ArgumentOutOfRangeException(nameof(codeMode));
        if (codeMode == ShareExtractionCodeBehavior.Random && fixedCode is not null)
            throw new ArgumentException("Random extraction-code mode cannot contain a fixed code.", nameof(fixedCode));
        if (codeMode == ShareExtractionCodeBehavior.Fixed &&
            !PermanentShareRequest.IsValidFixedExtractionCode(fixedCode))
            throw new ArgumentException("A fixed four-character extraction code is required.", nameof(fixedCode));

        BrowserId = binding.BrowserId;
        TargetId = binding.TargetId;
        DevToolsEndpoint = binding.DevToolsEndpoint;
        TimeoutMilliseconds = binding.TimeoutMilliseconds;
        BaiduUserId = baiduUserId;
        TargetDirectoryExactId = targetDirectoryExactId;
        TargetCanonicalPath = targetCanonicalPath;
        ExactItems = exactItems;
        ExactItemSetFingerprint = exactItems.Fingerprint;
        CodeMode = codeMode;
        FixedCode = fixedCode;
    }

    public Guid BrowserId { get; }
    public string TargetId { get; }
    public Uri DevToolsEndpoint { get; }
    public int TimeoutMilliseconds { get; }
    public string BaiduUserId { get; }
    public string TargetDirectoryExactId { get; }
    public string TargetCanonicalPath { get; }
    public RemoteItemIdentitySet ExactItems { get; }
    public RemoteItemIdentity ExactItem => ExactItems.Items[0];
    public string ExactItemSetFingerprint { get; }
    public ShareExtractionCodeBehavior CodeMode { get; }
    public string? FixedCode { get; }
    public bool IsPermanent => true;
    public bool IsAccessUnlimited => true;

    internal bool EqualsEvidence(ManagedBrowserShareSubmissionContext other) =>
        BrowserId == other.BrowserId &&
        string.Equals(TargetId, other.TargetId, StringComparison.Ordinal) &&
        string.Equals(DevToolsEndpoint.AbsoluteUri, other.DevToolsEndpoint.AbsoluteUri, StringComparison.Ordinal) &&
        TimeoutMilliseconds == other.TimeoutMilliseconds &&
        string.Equals(BaiduUserId, other.BaiduUserId, StringComparison.Ordinal) &&
        string.Equals(TargetDirectoryExactId, other.TargetDirectoryExactId, StringComparison.Ordinal) &&
        string.Equals(TargetCanonicalPath, other.TargetCanonicalPath, StringComparison.Ordinal) &&
        ExactItems == other.ExactItems &&
        string.Equals(ExactItemSetFingerprint, other.ExactItemSetFingerprint, StringComparison.Ordinal) &&
        CodeMode == other.CodeMode &&
        string.Equals(FixedCode, other.FixedCode, StringComparison.Ordinal);

    public override string ToString() =>
        $"ManagedBrowserShareSubmissionContext {{ TimeoutMilliseconds = {TimeoutMilliseconds}, CodeMode = {CodeMode}, Binding = [REDACTED] }}";
}

public sealed class ManagedBrowserReadShareOfficialListingRequest(
    ManagedBrowserShareSubmissionContext context) : ManagedBrowserRequestPayload
{
    public ManagedBrowserShareSubmissionContext Context { get; } =
        context ?? throw new ArgumentNullException(nameof(context));
    public override string ToString() =>
        "ManagedBrowserReadShareOfficialListingRequest { Context = [REDACTED] }";
}

public sealed class ManagedBrowserSubmitShareOnceRequest : ManagedBrowserRequestPayload
{
    public ManagedBrowserSubmitShareOnceRequest(
        ManagedBrowserShareSubmissionContext context,
        string baselineShareIdsFingerprint,
        string baselineObservationFingerprint)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        ValidateFingerprint(baselineShareIdsFingerprint, nameof(baselineShareIdsFingerprint));
        ValidateFingerprint(baselineObservationFingerprint, nameof(baselineObservationFingerprint));
        BaselineShareIdsFingerprint = baselineShareIdsFingerprint;
        BaselineObservationFingerprint = baselineObservationFingerprint;
    }

    public ManagedBrowserShareSubmissionContext Context { get; }
    public string BaselineShareIdsFingerprint { get; }
    public string BaselineObservationFingerprint { get; }
    public override string ToString() =>
        "ManagedBrowserSubmitShareOnceRequest { Context = [REDACTED], Baseline = [REDACTED] }";

    internal static void ValidateFingerprint(string value, string parameterName)
    {
        if (!ManagedBrowserProtocol.IsNonce(value))
            throw new ArgumentException("A lowercase SHA256 fingerprint is required.", parameterName);
    }
}

public sealed class ManagedBrowserObserveShareSubmissionOutcomeRequest : ManagedBrowserRequestPayload
{
    public ManagedBrowserObserveShareSubmissionOutcomeRequest(
        ManagedBrowserShareSubmissionContext context,
        string baselineShareIdsFingerprint,
        string baselineObservationFingerprint)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        ManagedBrowserSubmitShareOnceRequest.ValidateFingerprint(
            baselineShareIdsFingerprint, nameof(baselineShareIdsFingerprint));
        ManagedBrowserSubmitShareOnceRequest.ValidateFingerprint(
            baselineObservationFingerprint, nameof(baselineObservationFingerprint));
        BaselineShareIdsFingerprint = baselineShareIdsFingerprint;
        BaselineObservationFingerprint = baselineObservationFingerprint;
    }

    public ManagedBrowserShareSubmissionContext Context { get; }
    public string BaselineShareIdsFingerprint { get; }
    public string BaselineObservationFingerprint { get; }
    public override string ToString() =>
        "ManagedBrowserObserveShareSubmissionOutcomeRequest { Context = [REDACTED], Baseline = [REDACTED] }";
}

public sealed record ManagedBrowserShareOfficialEntry
{
    public ManagedBrowserShareOfficialEntry(
        string shareId,
        string shareUrl,
        string exactItemId,
        bool isPermanent,
        bool isAccessUnlimited,
        ShareExtractionCodeBehavior codeMode,
        string effectiveCode)
        : this(shareId, shareUrl,
            new RemoteItemIdentitySet([new RemoteItemIdentity(exactItemId, null)]),
            isPermanent, isAccessUnlimited, codeMode, effectiveCode) { }

    public ManagedBrowserShareOfficialEntry(
        string shareId,
        string shareUrl,
        RemoteItemIdentitySet exactItems,
        bool isPermanent,
        bool isAccessUnlimited,
        ShareExtractionCodeBehavior codeMode,
        string effectiveCode)
    {
        ManagedBrowserProtocol.ValidateBoundedText(shareId, nameof(shareId), 256);
        if (!IsCanonicalBaiduShareUrl(shareUrl))
            throw new ArgumentException("A canonical Baidu share URL is required.", nameof(shareUrl));
        ArgumentNullException.ThrowIfNull(exactItems);
        if (!isPermanent || !isAccessUnlimited)
            throw new ArgumentException("Only permanent unlimited shares are supported.");
        if (codeMode is not (ShareExtractionCodeBehavior.Random or ShareExtractionCodeBehavior.Fixed))
            throw new ArgumentOutOfRangeException(nameof(codeMode));
        if (!PermanentShareRequest.IsValidFixedExtractionCode(effectiveCode))
            throw new ArgumentException("A complete four-character effective code is required.", nameof(effectiveCode));
        ShareId = shareId;
        ShareUrl = shareUrl;
        ExactItems = exactItems;
        IsPermanent = true;
        IsAccessUnlimited = true;
        CodeMode = codeMode;
        EffectiveCode = effectiveCode;
    }

    public string ShareId { get; }
    public string ShareUrl { get; }
    public RemoteItemIdentitySet ExactItems { get; }
    public string ExactItemId => ExactItems.Items[0].ExactId;
    public bool IsPermanent { get; }
    public bool IsAccessUnlimited { get; }
    public ShareExtractionCodeBehavior CodeMode { get; }
    public string EffectiveCode { get; }

    private static bool IsCanonicalBaiduShareUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) &&
        string.Equals(uri.Host, "pan.baidu.com", StringComparison.Ordinal) &&
        uri.Port == 443 &&
        uri.AbsolutePath.StartsWith("/s/", StringComparison.Ordinal) &&
        uri.AbsolutePath.Length > 3 &&
        string.IsNullOrEmpty(uri.Query) &&
        string.IsNullOrEmpty(uri.Fragment) &&
        string.IsNullOrEmpty(uri.UserInfo);

    public override string ToString() =>
        "ManagedBrowserShareOfficialEntry { Share = [REDACTED], ExactItem = [REDACTED], Settings = [REDACTED] }";
}

public sealed class ManagedBrowserShareOfficialListing
{
    private const string ObservationDomain = "ManagedBrowserShareOfficialListing/v2";

    public ManagedBrowserShareOfficialListing(
        ManagedBrowserShareSubmissionContext context,
        bool reachedEnd,
        bool isStable,
        IReadOnlyList<ManagedBrowserShareOfficialEntry> entries,
        string? expectedShareIdsFingerprint = null,
        string? expectedObservationFingerprint = null)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        ArgumentNullException.ThrowIfNull(entries);
        if (!reachedEnd || !isStable)
            throw new ArgumentException("A complete stable official share listing is required.");
        var snapshot = entries.OrderBy(entry => entry.ShareId, StringComparer.Ordinal).ToImmutableArray();
        if (snapshot.Length > 1_000 || snapshot.Select(entry => entry.ShareId).Distinct(StringComparer.Ordinal).Count() != snapshot.Length)
            throw new ArgumentException("Official share identities must be unique and bounded.", nameof(entries));
        Entries = snapshot;
        ReachedEnd = true;
        IsStable = true;
        ShareIdsFingerprint = WebAutomationBaseline.Create(snapshot.Select(entry => entry.ShareId)).Fingerprint;
        ObservationFingerprint = ComputeObservationFingerprint(context, snapshot);
        ValidateExpected(expectedShareIdsFingerprint, ShareIdsFingerprint, nameof(expectedShareIdsFingerprint));
        ValidateExpected(expectedObservationFingerprint, ObservationFingerprint, nameof(expectedObservationFingerprint));
    }

    public ManagedBrowserShareSubmissionContext Context { get; }
    public bool ReachedEnd { get; }
    public bool IsStable { get; }
    public ImmutableArray<ManagedBrowserShareOfficialEntry> Entries { get; }
    public int TotalCount => Entries.Length;
    public string ShareIdsFingerprint { get; }
    public string ObservationFingerprint { get; }

    private static string ComputeObservationFingerprint(
        ManagedBrowserShareSubmissionContext context,
        IReadOnlyList<ManagedBrowserShareOfficialEntry> entries) =>
        Fingerprint(writer =>
        {
            writer(ObservationDomain);
            WriteContextFingerprint(writer, context);
            writer(entries.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (var entry in entries)
            {
                writer(entry.ShareId);
                writer(entry.ShareUrl);
                writer(entry.ExactItems.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                foreach (var exactItem in entry.ExactItems.Items) writer(exactItem.ExactId);
                writer(entry.IsPermanent ? "true" : "false");
                writer(entry.IsAccessUnlimited ? "true" : "false");
                writer(entry.CodeMode.ToString());
                writer(entry.EffectiveCode);
            }
        });

    private static void ValidateExpected(string? expected, string actual, string parameterName)
    {
        if (expected is null) return;
        ManagedBrowserSubmitShareOnceRequest.ValidateFingerprint(expected, parameterName);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual)))
            throw new ArgumentException("The listing fingerprint does not match its contents.", parameterName);
    }

    internal static string Fingerprint(Action<Action<string>> append)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        append(value =>
        {
            var bytes = new UTF8Encoding(false, true).GetBytes(value);
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)bytes.Length));
            hash.AppendData(length);
            hash.AppendData(bytes);
        });
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    internal static void WriteContextFingerprint(Action<string> writer, ManagedBrowserShareSubmissionContext context)
    {
        writer(context.BrowserId.ToString("D"));
        writer(context.TargetId);
        writer(context.DevToolsEndpoint.AbsoluteUri);
        writer(context.TimeoutMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        writer(context.BaiduUserId);
        writer(context.TargetDirectoryExactId);
        writer(context.TargetCanonicalPath);
        writer(context.ExactItems.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var exactItem in context.ExactItems.Items) writer(exactItem.ExactId);
        writer(context.ExactItemSetFingerprint);
        writer(context.CodeMode.ToString());
        writer(context.FixedCode ?? string.Empty);
    }

    public override string ToString() =>
        $"ManagedBrowserShareOfficialListing {{ TotalCount = {TotalCount}, IsStable = {IsStable}, Evidence = [REDACTED] }}";
}

public enum ManagedBrowserShareListingResultKind
{
    Observed = 1,
    PageNotValidated,
    AccountIdentityMismatch,
    LoginRequired,
    VerificationRequired,
    BrowserUnavailable,
    TimedOut,
    Cancelled,
}

public sealed class ManagedBrowserReadShareOfficialListingResult : ManagedBrowserResult
{
    public ManagedBrowserReadShareOfficialListingResult(
        ManagedBrowserShareListingResultKind kind,
        ManagedBrowserShareOfficialListing? listing = null)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if ((kind == ManagedBrowserShareListingResultKind.Observed) != (listing is not null))
            throw new ArgumentException("Only an observed result may contain a listing.", nameof(listing));
        Kind = kind;
        Listing = listing;
    }

    public ManagedBrowserShareListingResultKind Kind { get; }
    public ManagedBrowserShareOfficialListing? Listing { get; }
    public override string ToString() =>
        $"ManagedBrowserReadShareOfficialListingResult {{ Kind = {Kind}, Listing = [REDACTED] }}";
}

public enum ManagedBrowserShareSubmitDispatchState
{
    KnownNotDispatched = 1,
    ClickDispatched,
    DispatchOutcomeUnknown,
}

public enum ManagedBrowserShareSubmitResultKind
{
    ClickAttempted = 1,
    BaselineMismatch,
    PageNotValidated,
    AccountIdentityMismatch,
    LoginRequired,
    VerificationRequired,
    BrowserUnavailable,
    TimedOut,
    Cancelled,
}

public sealed class ManagedBrowserSubmitShareOnceResult : ManagedBrowserResult
{
    public ManagedBrowserSubmitShareOnceResult(
        ManagedBrowserShareSubmitResultKind kind,
        ManagedBrowserShareSubmitDispatchState dispatchState)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (!Enum.IsDefined(dispatchState)) throw new ArgumentOutOfRangeException(nameof(dispatchState));
        var valid = kind == ManagedBrowserShareSubmitResultKind.ClickAttempted
            ? dispatchState is ManagedBrowserShareSubmitDispatchState.ClickDispatched or ManagedBrowserShareSubmitDispatchState.DispatchOutcomeUnknown
            : dispatchState == ManagedBrowserShareSubmitDispatchState.KnownNotDispatched ||
              kind is ManagedBrowserShareSubmitResultKind.BrowserUnavailable or ManagedBrowserShareSubmitResultKind.TimedOut or ManagedBrowserShareSubmitResultKind.Cancelled &&
              dispatchState == ManagedBrowserShareSubmitDispatchState.DispatchOutcomeUnknown;
        if (!valid) throw new ArgumentException("The share-submit result and dispatch state are inconsistent.");
        Kind = kind;
        DispatchState = dispatchState;
    }

    public ManagedBrowserShareSubmitResultKind Kind { get; }
    public ManagedBrowserShareSubmitDispatchState DispatchState { get; }
    public override string ToString() =>
        $"ManagedBrowserSubmitShareOnceResult {{ Kind = {Kind}, DispatchState = {DispatchState} }}";
}

public enum ManagedBrowserShareOutcomeKind
{
    AcceptedBound = 1,
    UnboundAcknowledgement,
    ExplicitlyRejected,
    Conflict,
    PageNotValidated,
    AccountIdentityMismatch,
    LoginRequired,
    VerificationRequired,
    BrowserUnavailable,
    TimedOut,
    Cancelled,
}

public sealed class ManagedBrowserShareSubmissionOutcomeResult : ManagedBrowserResult
{
    private const string ObservationDomain = "ManagedBrowserShareSubmissionOutcome/v2";

    private ManagedBrowserShareSubmissionOutcomeResult(
        ManagedBrowserShareOutcomeKind kind,
        string? adapterRequestId,
        string? shareId,
        string? shareUrl,
        RemoteItemIdentitySet? exactItems,
        bool? isPermanent,
        bool? isAccessUnlimited,
        ShareExtractionCodeBehavior? codeMode,
        string? effectiveCode,
        string? expectedObservationFingerprint)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        Kind = kind;
        if (kind != ManagedBrowserShareOutcomeKind.AcceptedBound)
        {
            if (new object?[] { adapterRequestId, shareId, shareUrl, exactItems, isPermanent, isAccessUnlimited, codeMode, effectiveCode, expectedObservationFingerprint }.Any(value => value is not null))
                throw new ArgumentException("Only accepted official evidence may carry binding fields.");
            return;
        }

        var entry = new ManagedBrowserShareOfficialEntry(
            shareId!, shareUrl!, exactItems!, isPermanent == true, isAccessUnlimited == true,
            codeMode ?? ShareExtractionCodeBehavior.Unknown, effectiveCode!);
        ManagedBrowserProtocol.ValidateBoundedText(adapterRequestId, nameof(adapterRequestId), 256);
        AdapterRequestId = adapterRequestId;
        ShareId = entry.ShareId;
        ShareUrl = entry.ShareUrl;
        ExactItems = entry.ExactItems;
        IsPermanent = entry.IsPermanent;
        IsAccessUnlimited = entry.IsAccessUnlimited;
        CodeMode = entry.CodeMode;
        EffectiveCode = entry.EffectiveCode;
        OfficialObservationFingerprint = ComputeFingerprint();
        if (expectedObservationFingerprint is not null)
        {
            ManagedBrowserSubmitShareOnceRequest.ValidateFingerprint(
                expectedObservationFingerprint, nameof(expectedObservationFingerprint));
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(expectedObservationFingerprint),
                    Encoding.UTF8.GetBytes(OfficialObservationFingerprint)))
                throw new ArgumentException("The outcome fingerprint does not match its contents.", nameof(expectedObservationFingerprint));
        }
    }

    public ManagedBrowserShareSubmissionOutcomeResult(ManagedBrowserShareOutcomeKind kind)
        : this(kind, null, null, null, null, null, null, null, null, null) { }

    public ManagedBrowserShareOutcomeKind Kind { get; }
    public string? AdapterRequestId { get; }
    public string? ShareId { get; }
    public string? ShareUrl { get; }
    public RemoteItemIdentitySet? ExactItems { get; }
    public string? ExactItemId => ExactItems?.Items[0].ExactId;
    public bool? IsPermanent { get; }
    public bool? IsAccessUnlimited { get; }
    public ShareExtractionCodeBehavior? CodeMode { get; }
    public string? EffectiveCode { get; }
    public string? OfficialObservationFingerprint { get; }

    public static ManagedBrowserShareSubmissionOutcomeResult CreateAcceptedBound(
        string adapterRequestId,
        string shareId,
        string shareUrl,
        string exactItemId,
        ShareExtractionCodeBehavior codeMode,
        string effectiveCode) =>
        CreateAcceptedBound(adapterRequestId, shareId, shareUrl,
            new RemoteItemIdentitySet([new RemoteItemIdentity(exactItemId, null)]), codeMode, effectiveCode);

    public static ManagedBrowserShareSubmissionOutcomeResult CreateAcceptedBound(
        string adapterRequestId,
        string shareId,
        string shareUrl,
        RemoteItemIdentitySet exactItems,
        ShareExtractionCodeBehavior codeMode,
        string effectiveCode) =>
        new(ManagedBrowserShareOutcomeKind.AcceptedBound, adapterRequestId, shareId, shareUrl,
            exactItems, true, true, codeMode, effectiveCode, null);

    internal static ManagedBrowserShareSubmissionOutcomeResult ParseAcceptedBound(
        string adapterRequestId,
        string shareId,
        string shareUrl,
        RemoteItemIdentitySet exactItems,
        bool isPermanent,
        bool isAccessUnlimited,
        ShareExtractionCodeBehavior codeMode,
        string effectiveCode,
        string observationFingerprint) =>
        new(ManagedBrowserShareOutcomeKind.AcceptedBound, adapterRequestId, shareId, shareUrl,
            exactItems, isPermanent, isAccessUnlimited, codeMode, effectiveCode, observationFingerprint);

    private string ComputeFingerprint() => ManagedBrowserShareOfficialListing.Fingerprint(writer =>
    {
        writer(ObservationDomain);
        writer(AdapterRequestId!);
        writer(ShareId!);
        writer(ShareUrl!);
        writer(ExactItems!.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var exactItem in ExactItems.Items) writer(exactItem.ExactId);
        writer(IsPermanent == true ? "true" : "false");
        writer(IsAccessUnlimited == true ? "true" : "false");
        writer(CodeMode!.Value.ToString());
        writer(EffectiveCode!);
    });

    public override string ToString() =>
        $"ManagedBrowserShareSubmissionOutcomeResult {{ Kind = {Kind}, Evidence = [REDACTED] }}";
}

public static partial class ManagedBrowserProtocol
{
    private static readonly string[] ShareSubmissionContextFields =
    [
        "browserId", "targetId", "devToolsEndpoint", "timeoutMilliseconds", "baiduUserId",
        "targetDirectoryExactId", "targetCanonicalPath", "exactItemIds", "exactItemSetFingerprint",
        "codeMode", "fixedCode",
    ];

    private static ManagedBrowserRequestPayload ParseReadShareOfficialListingRequest(JsonElement value)
    {
        ValidateObject(value, ShareSubmissionContextFields, ManagedBrowserProtocolError.InvalidPayload);
        return new ManagedBrowserReadShareOfficialListingRequest(ParseShareSubmissionContext(value));
    }

    private static ManagedBrowserRequestPayload ParseSubmitShareOnceRequest(JsonElement value)
    {
        ValidateObject(value, [.. ShareSubmissionContextFields,
            "baselineShareIdsFingerprint", "baselineObservationFingerprint"],
            ManagedBrowserProtocolError.InvalidPayload);
        return new ManagedBrowserSubmitShareOnceRequest(
            ParseShareSubmissionContext(value),
            RequireString(value, "baselineShareIdsFingerprint", ManagedBrowserProtocolError.InvalidPayload),
            RequireString(value, "baselineObservationFingerprint", ManagedBrowserProtocolError.InvalidPayload));
    }

    private static ManagedBrowserRequestPayload ParseObserveShareSubmissionOutcomeRequest(JsonElement value)
    {
        ValidateObject(value, [.. ShareSubmissionContextFields,
            "baselineShareIdsFingerprint", "baselineObservationFingerprint"],
            ManagedBrowserProtocolError.InvalidPayload);
        return new ManagedBrowserObserveShareSubmissionOutcomeRequest(
            ParseShareSubmissionContext(value),
            RequireString(value, "baselineShareIdsFingerprint", ManagedBrowserProtocolError.InvalidPayload),
            RequireString(value, "baselineObservationFingerprint", ManagedBrowserProtocolError.InvalidPayload));
    }

    private static ManagedBrowserShareSubmissionContext ParseShareSubmissionContext(JsonElement value)
    {
        const ManagedBrowserProtocolError error = ManagedBrowserProtocolError.InvalidPayload;
        try
        {
            if (!Uri.TryCreate(RequireString(value, "devToolsEndpoint", error), UriKind.Absolute, out var endpoint))
                throw new ArgumentException("Invalid endpoint.");
            var items = ParseExactItems(value, "exactItemIds", error);
            if (!string.Equals(items.Fingerprint, RequireString(value, "exactItemSetFingerprint", error), StringComparison.Ordinal))
                throw new ArgumentException("Exact-item fingerprint mismatch.");
            var fixedCode = RequireNullableString(value, "fixedCode", error);
            return new ManagedBrowserShareSubmissionContext(
                RequireGuid(value, "browserId", error), RequireString(value, "targetId", error), endpoint,
                RequireInt32(value, "timeoutMilliseconds", error), RequireString(value, "baiduUserId", error),
                RequireString(value, "targetDirectoryExactId", error), RequireString(value, "targetCanonicalPath", error),
                items, RequireExactEnum<ShareExtractionCodeBehavior>(value, "codeMode"), fixedCode);
        }
        catch (ArgumentException) { throw Failure(error); }
    }

    private static ManagedBrowserResult ParseShareListingResult(JsonElement value)
    {
        const ManagedBrowserProtocolError error = ManagedBrowserProtocolError.InvalidPayload;
        var kind = RequireExactEnum<ManagedBrowserShareListingResultKind>(value, "kind");
        if (kind != ManagedBrowserShareListingResultKind.Observed)
        {
            ValidateObject(value, ["kind"], error);
            return new ManagedBrowserReadShareOfficialListingResult(kind);
        }
        ValidateObject(value,
        [
            "kind", .. ShareSubmissionContextFields, "totalCount", "reachedEnd", "isStable",
            "entries", "shareIdsFingerprint", "observationFingerprint",
        ], error);
        try
        {
            var context = ParseShareSubmissionContext(value);
            var entries = RequireArray(value, "entries", error)
                .EnumerateArray().Select(ParseShareEntry).ToArray();
            if (RequireInt32(value, "totalCount", error) != entries.Length ||
                !RequireShareBoolean(value, "reachedEnd", error) || !RequireShareBoolean(value, "isStable", error))
                throw new ArgumentException("Incomplete listing.");
            var listing = new ManagedBrowserShareOfficialListing(
                context, true, true, entries,
                RequireString(value, "shareIdsFingerprint", error),
                RequireString(value, "observationFingerprint", error));
            return new ManagedBrowserReadShareOfficialListingResult(kind, listing);
        }
        catch (ArgumentException) { throw Failure(error); }
    }

    private static ManagedBrowserShareOfficialEntry ParseShareEntry(JsonElement value)
    {
        const ManagedBrowserProtocolError error = ManagedBrowserProtocolError.InvalidPayload;
        ValidateObject(value,
            ["shareId", "shareUrl", "exactItemIds", "isPermanent", "isAccessUnlimited", "codeMode", "effectiveCode"], error);
        return new ManagedBrowserShareOfficialEntry(
            RequireString(value, "shareId", error), RequireString(value, "shareUrl", error),
            ParseExactItems(value, "exactItemIds", error), RequireShareBoolean(value, "isPermanent", error),
            RequireShareBoolean(value, "isAccessUnlimited", error),
            RequireExactEnum<ShareExtractionCodeBehavior>(value, "codeMode"),
            RequireString(value, "effectiveCode", error));
    }

    private static ManagedBrowserResult ParseSubmitShareOnceResult(JsonElement value)
    {
        ValidateObject(value, ["kind", "dispatchState"], ManagedBrowserProtocolError.InvalidPayload);
        try
        {
            return new ManagedBrowserSubmitShareOnceResult(
                RequireExactEnum<ManagedBrowserShareSubmitResultKind>(value, "kind"),
                RequireExactEnum<ManagedBrowserShareSubmitDispatchState>(value, "dispatchState"));
        }
        catch (ArgumentException) { throw Failure(ManagedBrowserProtocolError.InvalidPayload); }
    }

    private static ManagedBrowserResult ParseShareSubmissionOutcomeResult(JsonElement value)
    {
        const ManagedBrowserProtocolError error = ManagedBrowserProtocolError.InvalidPayload;
        var kind = RequireExactEnum<ManagedBrowserShareOutcomeKind>(value, "kind");
        if (kind != ManagedBrowserShareOutcomeKind.AcceptedBound)
        {
            ValidateObject(value, ["kind"], error);
            return new ManagedBrowserShareSubmissionOutcomeResult(kind);
        }
        ValidateObject(value,
        [
            "kind", "adapterRequestId", "shareId", "shareUrl", "exactItemIds", "isPermanent",
            "isAccessUnlimited", "codeMode", "effectiveCode", "officialObservationFingerprint",
        ], error);
        try
        {
            return ManagedBrowserShareSubmissionOutcomeResult.ParseAcceptedBound(
                RequireString(value, "adapterRequestId", error), RequireString(value, "shareId", error),
                RequireString(value, "shareUrl", error), ParseExactItems(value, "exactItemIds", error),
                RequireShareBoolean(value, "isPermanent", error), RequireShareBoolean(value, "isAccessUnlimited", error),
                RequireExactEnum<ShareExtractionCodeBehavior>(value, "codeMode"),
                RequireString(value, "effectiveCode", error),
                RequireString(value, "officialObservationFingerprint", error));
        }
        catch (ArgumentException) { throw Failure(error); }
    }

    private static void WriteShareSubmissionContext(Utf8JsonWriter writer, ManagedBrowserShareSubmissionContext context)
    {
        writer.WriteString("browserId", context.BrowserId);
        writer.WriteString("targetId", context.TargetId);
        writer.WriteString("devToolsEndpoint", context.DevToolsEndpoint.AbsoluteUri);
        writer.WriteNumber("timeoutMilliseconds", context.TimeoutMilliseconds);
        writer.WriteString("baiduUserId", context.BaiduUserId);
        writer.WriteString("targetDirectoryExactId", context.TargetDirectoryExactId);
        writer.WriteString("targetCanonicalPath", context.TargetCanonicalPath);
        WriteExactItemIds(writer, "exactItemIds", context.ExactItems);
        writer.WriteString("exactItemSetFingerprint", context.ExactItemSetFingerprint);
        writer.WriteString("codeMode", context.CodeMode.ToString());
        if (context.FixedCode is null) writer.WriteNull("fixedCode");
        else writer.WriteString("fixedCode", context.FixedCode);
    }

    private static void WriteShareSubmissionRequest(Utf8JsonWriter writer, ManagedBrowserRequestPayload payload)
    {
        switch (payload)
        {
            case ManagedBrowserReadShareOfficialListingRequest listing:
                WriteShareSubmissionContext(writer, listing.Context);
                break;
            case ManagedBrowserSubmitShareOnceRequest submit:
                WriteShareSubmissionContext(writer, submit.Context);
                writer.WriteString("baselineShareIdsFingerprint", submit.BaselineShareIdsFingerprint);
                writer.WriteString("baselineObservationFingerprint", submit.BaselineObservationFingerprint);
                break;
            case ManagedBrowserObserveShareSubmissionOutcomeRequest outcome:
                WriteShareSubmissionContext(writer, outcome.Context);
                writer.WriteString("baselineShareIdsFingerprint", outcome.BaselineShareIdsFingerprint);
                writer.WriteString("baselineObservationFingerprint", outcome.BaselineObservationFingerprint);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(payload));
        }
    }

    private static void WriteShareSubmissionResult(Utf8JsonWriter writer, ManagedBrowserResult result)
    {
        switch (result)
        {
            case ManagedBrowserReadShareOfficialListingResult listing:
                writer.WriteString("kind", listing.Kind.ToString());
                if (listing.Listing is { } observed)
                {
                    WriteShareSubmissionContext(writer, observed.Context);
                    writer.WriteNumber("totalCount", observed.TotalCount);
                    writer.WriteBoolean("reachedEnd", observed.ReachedEnd);
                    writer.WriteBoolean("isStable", observed.IsStable);
                    writer.WritePropertyName("entries");
                    writer.WriteStartArray();
                    foreach (var entry in observed.Entries)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("shareId", entry.ShareId);
                        writer.WriteString("shareUrl", entry.ShareUrl);
                        WriteExactItemIds(writer, "exactItemIds", entry.ExactItems);
                        writer.WriteBoolean("isPermanent", entry.IsPermanent);
                        writer.WriteBoolean("isAccessUnlimited", entry.IsAccessUnlimited);
                        writer.WriteString("codeMode", entry.CodeMode.ToString());
                        writer.WriteString("effectiveCode", entry.EffectiveCode);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                    writer.WriteString("shareIdsFingerprint", observed.ShareIdsFingerprint);
                    writer.WriteString("observationFingerprint", observed.ObservationFingerprint);
                }
                break;
            case ManagedBrowserSubmitShareOnceResult submit:
                writer.WriteString("kind", submit.Kind.ToString());
                writer.WriteString("dispatchState", submit.DispatchState.ToString());
                break;
            case ManagedBrowserShareSubmissionOutcomeResult outcome:
                writer.WriteString("kind", outcome.Kind.ToString());
                if (outcome.Kind == ManagedBrowserShareOutcomeKind.AcceptedBound)
                {
                    writer.WriteString("adapterRequestId", outcome.AdapterRequestId);
                    writer.WriteString("shareId", outcome.ShareId);
                    writer.WriteString("shareUrl", outcome.ShareUrl);
                    WriteExactItemIds(writer, "exactItemIds", outcome.ExactItems!);
                    writer.WriteBoolean("isPermanent", outcome.IsPermanent!.Value);
                    writer.WriteBoolean("isAccessUnlimited", outcome.IsAccessUnlimited!.Value);
                    writer.WriteString("codeMode", outcome.CodeMode!.Value.ToString());
                    writer.WriteString("effectiveCode", outcome.EffectiveCode);
                    writer.WriteString("officialObservationFingerprint", outcome.OfficialObservationFingerprint);
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(result));
        }
    }

    private static JsonElement RequireArray(JsonElement value, string name, ManagedBrowserProtocolError error)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Array)
            throw Failure(error);
        return property;
    }

    private static RemoteItemIdentitySet ParseExactItems(
        JsonElement value, string name, ManagedBrowserProtocolError error)
    {
        try
        {
            var items = RequireArray(value, name, error).EnumerateArray()
                .Select(element => element.ValueKind == JsonValueKind.String
                    ? new RemoteItemIdentity(element.GetString()!, null)
                    : throw Failure(error))
                .ToArray();
            if (items.Length > 10_000) throw Failure(error);
            return new RemoteItemIdentitySet(items);
        }
        catch (ArgumentException) { throw Failure(error); }
    }

    private static void WriteExactItemIds(
        Utf8JsonWriter writer, string propertyName, RemoteItemIdentitySet items)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartArray();
        foreach (var item in items.Items) writer.WriteStringValue(item.ExactId);
        writer.WriteEndArray();
    }

    private static bool RequireShareBoolean(JsonElement value, string name, ManagedBrowserProtocolError error)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw Failure(error);
        return property.GetBoolean();
    }

    private static string? RequireNullableString(JsonElement value, string name, ManagedBrowserProtocolError error)
    {
        if (!value.TryGetProperty(name, out var property)) throw Failure(error);
        return property.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => property.GetString(),
            _ => throw Failure(error),
        };
    }
}
