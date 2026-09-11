using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace BaiduBatchTransfer.App.Features.ManagedBrowser;

public enum ManagedBrowserTargetInventoryScope { AllItems = 1 }

public enum ManagedBrowserTargetInventoryItemKind { File = 1, Folder = 2 }

public sealed class ManagedBrowserTargetInventoryItem
{
    public ManagedBrowserTargetInventoryItem(string exactId, ManagedBrowserTargetInventoryItemKind kind, string serverName)
    {
        ManagedBrowserTargetInventoryValidation.ValidateExactId(exactId, nameof(exactId));
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        ManagedBrowserTargetInventoryValidation.ValidateServerName(serverName, nameof(serverName));
        ExactId = exactId;
        Kind = kind;
        ServerName = serverName;
    }

    public string ExactId { get; }
    public ManagedBrowserTargetInventoryItemKind Kind { get; }
    public string ServerName { get; }
    public override string ToString() => $"ManagedBrowserTargetInventoryItem {{ Kind = {Kind}, Identity = [REDACTED], Name = [REDACTED] }}";
}

public sealed class ManagedBrowserTargetDirectoryHeader
{
    internal ManagedBrowserTargetDirectoryHeader(
        ManagedBrowserTargetInventoryScope scope, Guid browserId, string targetId, Uri devToolsEndpoint,
        string baiduUserId, string generationToken, string directoryExactId, string canonicalPath,
        IReadOnlyList<string> ancestorExactIds, int totalCount, bool reachedEnd, bool isStable,
        int enumerationStepCount, string parentObservationFingerprint)
    {
        if (scope != ManagedBrowserTargetInventoryScope.AllItems) throw new ArgumentOutOfRangeException(nameof(scope));
        if (browserId == Guid.Empty) throw new ArgumentException("A browser id is required.", nameof(browserId));
        ManagedBrowserTargetInventoryValidation.ValidateExactId(targetId, nameof(targetId), 256);
        ArgumentNullException.ThrowIfNull(devToolsEndpoint);
        if (!ManagedBrowserProtocol.IsVerifiedLoopbackEndpoint(devToolsEndpoint))
            throw new ArgumentException("A verified loopback endpoint is required.", nameof(devToolsEndpoint));
        ManagedBrowserProtocol.ValidateBoundedText(baiduUserId, nameof(baiduUserId), 256);
        ManagedBrowserProtocol.ValidateBoundedText(generationToken, nameof(generationToken), 256);
        ManagedBrowserTargetInventoryValidation.ValidateDirectoryIdentity(directoryExactId, canonicalPath, ancestorExactIds);
        if (totalCount is < 0 or > ManagedBrowserTargetInventorySnapshot.MaximumItems)
            throw new ArgumentOutOfRangeException(nameof(totalCount));
        if (!reachedEnd) throw new ArgumentException("The explicit end marker is required.", nameof(reachedEnd));
        if (!isStable) throw new ArgumentException("Stable enumeration is required.", nameof(isStable));
        if (enumerationStepCount is < 1 or > ManagedBrowserTargetInventorySnapshot.MaximumEnumerationSteps)
            throw new ArgumentOutOfRangeException(nameof(enumerationStepCount));
        if (!ManagedBrowserProtocol.IsNonce(parentObservationFingerprint))
            throw new ArgumentException("A lowercase SHA256 fingerprint is required.", nameof(parentObservationFingerprint));
        Scope = scope; BrowserId = browserId; TargetId = targetId; DevToolsEndpoint = devToolsEndpoint;
        BaiduUserId = baiduUserId; GenerationToken = generationToken; DirectoryExactId = directoryExactId;
        CanonicalPath = canonicalPath; AncestorExactIds = Array.AsReadOnly(ancestorExactIds.ToArray()); TotalCount = totalCount;
        ReachedEnd = reachedEnd; IsStable = isStable; EnumerationStepCount = enumerationStepCount;
        ParentObservationFingerprint = parentObservationFingerprint;
    }

    public ManagedBrowserTargetInventoryScope Scope { get; }
    public Guid BrowserId { get; }
    public string TargetId { get; }
    public Uri DevToolsEndpoint { get; }
    public string BaiduUserId { get; }
    public string GenerationToken { get; }
    public string DirectoryExactId { get; }
    public string CanonicalPath { get; }
    public IReadOnlyList<string> AncestorExactIds { get; }
    public int TotalCount { get; }
    public bool ReachedEnd { get; }
    public bool IsStable { get; }
    public int EnumerationStepCount { get; }
    public string ParentObservationFingerprint { get; }
    public override string ToString() =>
        $"ManagedBrowserTargetDirectoryHeader {{ Scope = {Scope}, TotalCount = {TotalCount}, ReachedEnd = {ReachedEnd}, IsStable = {IsStable}, EnumerationStepCount = {EnumerationStepCount}, Binding = [REDACTED], Directory = [REDACTED], Fingerprint = [REDACTED] }}";
}

public sealed class ManagedBrowserTargetInventorySnapshot
{
    public const int MaximumItems = 1_024;
    public const int MaximumEnumerationSteps = 64;
    private const string FingerprintDomain = "ManagedBrowserTargetInventory/v2";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public ManagedBrowserTargetInventorySnapshot(
        Guid browserId, string targetId, Uri devToolsEndpoint, string baiduUserId, string generationToken,
        string directoryExactId, string canonicalPath, IReadOnlyList<string> ancestorExactIds,
        int totalCount, bool reachedEnd, bool isStable, int enumerationStepCount,
        IEnumerable<ManagedBrowserTargetInventoryItem> items)
        : this(browserId, targetId, devToolsEndpoint, baiduUserId, generationToken, directoryExactId,
            canonicalPath, ancestorExactIds, totalCount, reachedEnd, isStable, enumerationStepCount, items, null) { }

    internal ManagedBrowserTargetInventorySnapshot(
        Guid browserId, string targetId, Uri devToolsEndpoint, string baiduUserId, string generationToken,
        string directoryExactId, string canonicalPath, IReadOnlyList<string> ancestorExactIds,
        int totalCount, bool reachedEnd, bool isStable, int enumerationStepCount,
        IEnumerable<ManagedBrowserTargetInventoryItem> items, string? expectedFingerprint)
    {
        if (browserId == Guid.Empty) throw new ArgumentException("A browser id is required.", nameof(browserId));
        ManagedBrowserTargetInventoryValidation.ValidateExactId(targetId, nameof(targetId), 256);
        ArgumentNullException.ThrowIfNull(devToolsEndpoint);
        if (!ManagedBrowserProtocol.IsVerifiedLoopbackEndpoint(devToolsEndpoint))
            throw new ArgumentException("A verified loopback endpoint is required.", nameof(devToolsEndpoint));
        ManagedBrowserProtocol.ValidateBoundedText(baiduUserId, nameof(baiduUserId), 256);
        ManagedBrowserProtocol.ValidateBoundedText(generationToken, nameof(generationToken), 256);
        ManagedBrowserTargetInventoryValidation.ValidateDirectoryIdentity(directoryExactId, canonicalPath, ancestorExactIds);
        ArgumentNullException.ThrowIfNull(items);
        var materialized = items.ToArray();
        if (materialized.Length > MaximumItems) throw new ArgumentException("Too many inventory items.", nameof(items));
        if (totalCount != materialized.Length) throw new ArgumentException("The explicit count must equal the complete item set.", nameof(totalCount));
        if (!reachedEnd) throw new ArgumentException("The explicit end marker is required.", nameof(reachedEnd));
        if (!isStable) throw new ArgumentException("Stable enumeration is required.", nameof(isStable));
        if (enumerationStepCount is < 1 or > MaximumEnumerationSteps)
            throw new ArgumentOutOfRangeException(nameof(enumerationStepCount));
        if (materialized.Select(item => item.ExactId).Distinct(StringComparer.Ordinal).Count() != materialized.Length ||
            materialized.Any(item => ancestorExactIds.Contains(item.ExactId, StringComparer.Ordinal)))
            throw new ArgumentException("Inventory identities must be unique and outside the ancestry.", nameof(items));

        Scope = ManagedBrowserTargetInventoryScope.AllItems; BrowserId = browserId; TargetId = targetId;
        DevToolsEndpoint = devToolsEndpoint; BaiduUserId = baiduUserId; GenerationToken = generationToken;
        DirectoryExactId = directoryExactId; CanonicalPath = canonicalPath;
        AncestorExactIds = Array.AsReadOnly(ancestorExactIds.ToArray());
        TotalCount = totalCount; ReachedEnd = reachedEnd; IsStable = isStable; EnumerationStepCount = enumerationStepCount;
        Items = Array.AsReadOnly(materialized.OrderBy(item => item.ExactId, StringComparer.Ordinal).ToArray());
        ObservationFingerprint = ComputeFingerprint();
        if (expectedFingerprint is not null && !CryptographicOperations.FixedTimeEquals(
                StrictUtf8.GetBytes(ObservationFingerprint), StrictUtf8.GetBytes(expectedFingerprint)))
            throw new ArgumentException("The inventory fingerprint does not match.", nameof(expectedFingerprint));
    }

    public ManagedBrowserTargetInventoryScope Scope { get; }
    public Guid BrowserId { get; }
    public string TargetId { get; }
    public Uri DevToolsEndpoint { get; }
    public string BaiduUserId { get; }
    public string GenerationToken { get; }
    public string DirectoryExactId { get; }
    public string CanonicalPath { get; }
    public IReadOnlyList<string> AncestorExactIds { get; }
    public int TotalCount { get; }
    public bool ReachedEnd { get; }
    public bool IsStable { get; }
    public int EnumerationStepCount { get; }
    public IReadOnlyList<ManagedBrowserTargetInventoryItem> Items { get; }
    public string ObservationFingerprint { get; }

    public ManagedBrowserTargetDirectoryHeader ToHeader() => new(Scope, BrowserId, TargetId, DevToolsEndpoint,
        BaiduUserId, GenerationToken, DirectoryExactId, CanonicalPath, AncestorExactIds, TotalCount,
        ReachedEnd, IsStable, EnumerationStepCount, ObservationFingerprint);

    public override string ToString() =>
        $"ManagedBrowserTargetInventorySnapshot {{ Scope = {Scope}, TotalCount = {TotalCount}, ReachedEnd = {ReachedEnd}, IsStable = {IsStable}, EnumerationStepCount = {EnumerationStepCount}, ItemCount = {Items.Count}, Binding = [REDACTED], Directory = [REDACTED], Fingerprint = [REDACTED] }}";

    internal string ComputeFingerprint()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Field(string value)
        {
            var bytes = StrictUtf8.GetBytes(value);
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)bytes.Length));
            hash.AppendData(length); hash.AppendData(bytes);
        }
        Field(FingerprintDomain); Field(Scope.ToString()); Field(BrowserId.ToString("D")); Field(TargetId);
        Field(DevToolsEndpoint.AbsoluteUri); Field(BaiduUserId); Field(GenerationToken); Field(DirectoryExactId);
        Field(CanonicalPath); Field(AncestorExactIds.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var ancestor in AncestorExactIds) Field(ancestor);
        Field(TotalCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Field("true"); Field("true");
        Field(EnumerationStepCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var item in Items) { Field(item.ExactId); Field(item.Kind.ToString()); Field(item.ServerName); }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}

internal static class ManagedBrowserTargetInventoryValidation
{
    internal const int MaximumCanonicalPathBytes = 4_096;
    internal static void ValidateExactId(string? value, string parameterName, int maximumBytes = 128)
    {
        ManagedBrowserProtocol.ValidateBoundedText(value, parameterName, maximumBytes);
        if (value!.Any(character => !(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-')))
            throw new ArgumentException("An exact opaque identifier is required.", parameterName);
    }

    internal static void ValidateServerName(string? value, string parameterName)
    {
        if (string.IsNullOrEmpty(value)) throw new ArgumentException("An exact server name is required.", parameterName);
        byte[] bytes;
        try { bytes = new UTF8Encoding(false, true).GetBytes(value); }
        catch (EncoderFallbackException error) { throw new ArgumentException("A strict UTF8 server name is required.", parameterName, error); }
        if (bytes.Length > 256 || value.Any(character => character is <= '\u001f' or >= '\u007f' and <= '\u009f' or '/' or '\\'))
            throw new ArgumentException("An exact bounded server name is required.", parameterName);
    }

    internal static void ValidateDirectoryIdentity(string directoryExactId, string canonicalPath, IReadOnlyList<string> ancestorExactIds)
    {
        ValidateExactId(directoryExactId, nameof(directoryExactId));
        ArgumentNullException.ThrowIfNull(ancestorExactIds);
        var depth = ValidateCanonicalPath(canonicalPath);
        if (ancestorExactIds.Count != depth + 1 || ancestorExactIds.Count is < 1 or > 65)
            throw new ArgumentException("Root-inclusive ancestry must match the canonical path.", nameof(ancestorExactIds));
        foreach (var ancestor in ancestorExactIds) ValidateExactId(ancestor, nameof(ancestorExactIds));
        if (ancestorExactIds.Distinct(StringComparer.Ordinal).Count() != ancestorExactIds.Count ||
            !string.Equals(ancestorExactIds[^1], directoryExactId, StringComparison.Ordinal))
            throw new ArgumentException("Root-inclusive ancestry is inconsistent.", nameof(ancestorExactIds));
    }

    internal static int ValidateCanonicalPath(string value)
    {
        ManagedBrowserProtocol.ValidateBoundedText(value, nameof(value), MaximumCanonicalPathBytes);
        if (!value.StartsWith("/", StringComparison.Ordinal) || value.Contains('\\'))
            throw new ArgumentException("A canonical absolute path is required.", nameof(value));
        if (value == "/") return 0;
        var segments = value[1..].Split('/');
        if (segments.Length > 64 || segments.Any(segment => segment is "" or "." or ".."))
            throw new ArgumentException("A bounded canonical absolute path is required.", nameof(value));
        foreach (var segment in segments) ValidateServerName(segment, nameof(value));
        return segments.Length;
    }
}
