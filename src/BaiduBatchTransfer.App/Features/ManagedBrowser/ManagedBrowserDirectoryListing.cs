using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace BaiduBatchTransfer.App.Features.ManagedBrowser;

/// <summary>Reads an already-open picker. Does not open, enter, create, or grant write authority.</summary>
public sealed class ManagedBrowserReadTransferDirectoryRequest : ManagedBrowserRequestPayload
{
    public ManagedBrowserReadTransferDirectoryRequest(Guid browserId, string targetId, Uri devToolsEndpoint,
        int timeoutMilliseconds, string sourceUri, string expectedBaiduUserId,
        string? expectedParentExactId = null, string? expectedCanonicalPath = null)
        : this(new ManagedBrowserOpenTransferSourceRequest(browserId, targetId, devToolsEndpoint,
            timeoutMilliseconds, sourceUri, expectedBaiduUserId), expectedParentExactId, expectedCanonicalPath) { }

    internal ManagedBrowserReadTransferDirectoryRequest(ManagedBrowserOpenTransferSourceRequest binding,
        string? expectedParentExactId = null, string? expectedCanonicalPath = null)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if ((expectedParentExactId is null) != (expectedCanonicalPath is null))
            throw new ArgumentException("Expected directory identity and path must be paired.");
        if (expectedParentExactId is not null)
        {
            ManagedBrowserDirectorySnapshot.ValidateExactId(expectedParentExactId);
            ManagedBrowserDirectorySnapshot.ValidateCanonicalPath(expectedCanonicalPath!);
        }
        Binding = binding; ExpectedParentExactId = expectedParentExactId; ExpectedCanonicalPath = expectedCanonicalPath;
    }
    public ManagedBrowserOpenTransferSourceRequest Binding { get; }
    public string? ExpectedParentExactId { get; }
    public string? ExpectedCanonicalPath { get; }
    public override string ToString() => "ManagedBrowserReadTransferDirectoryRequest { Metadata = [REDACTED] }";
}

public enum ManagedBrowserDirectoryListingResultKind
{
    Observed = 1, LoginRequired, VerificationRequired, PageNotValidated,
    BrowserUnavailable, AccountIdentityMismatch, TimedOut, Cancelled,
}

/// <summary>Complete bounded picker FOLDER candidates; never a saved-file baseline or success proof.</summary>
public sealed class ManagedBrowserDirectorySnapshot
{
    private static readonly Regex ExactIdPattern = new(@"\A[A-Za-z0-9_-]{1,128}\z", RegexOptions.NonBacktracking);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public ManagedBrowserDirectorySnapshot(string baiduUserId, string generationToken, string parentExactId,
        string canonicalPath, IEnumerable<string> ancestorExactIds, int totalCount, bool reachedEnd, bool isStable,
        IEnumerable<ManagedBrowserSourceItem> items, string observationFingerprint)
    {
        ManagedBrowserProtocol.ValidateBoundedText(baiduUserId, nameof(baiduUserId), 256);
        ManagedBrowserProtocol.ValidateBoundedText(generationToken, nameof(generationToken), 256);
        ValidateExactId(parentExactId);
        var depth = ValidateCanonicalPath(canonicalPath);
        ArgumentNullException.ThrowIfNull(ancestorExactIds); ArgumentNullException.ThrowIfNull(items);
        var ancestors = ancestorExactIds.Take(34).ToArray();
        if (ancestors.Length is < 1 or > 33 || ancestors.Length != depth + 1 || ancestors[^1] != parentExactId)
            throw new ArgumentException("Complete root-to-current directory ancestry is required.");
        foreach (var id in ancestors) ValidateExactId(id);
        var seen = ancestors.ToHashSet(StringComparer.Ordinal);
        if (seen.Count != ancestors.Length) throw new ArgumentException("Ancestor identities must be unique.");
        var copy = items.Take(33).ToArray();
        if (copy.Length > 32 || totalCount != copy.Length || !reachedEnd || !isStable ||
            copy.Any(item => item is null || item.Kind != ManagedBrowserSourceItemKind.Folder || !seen.Add(item.ExactId)))
            throw new ArgumentException("A complete stable unique folder listing is required.");
        if (!ManagedBrowserProtocol.IsNonce(observationFingerprint))
            throw new ArgumentException("A lowercase SHA256 fingerprint is required.");
        BaiduUserId = baiduUserId; GenerationToken = generationToken; ParentExactId = parentExactId;
        CanonicalPath = canonicalPath; AncestorExactIds = ancestors.ToImmutableArray();
        Items = copy.OrderBy(item => item.ExactId, StringComparer.Ordinal).ToImmutableArray();
        ObservationFingerprint = observationFingerprint;
    }

    public string BaiduUserId { get; }
    public string GenerationToken { get; }
    public string ParentExactId { get; }
    public string CanonicalPath { get; }
    public ImmutableArray<string> AncestorExactIds { get; }
    public ImmutableArray<ManagedBrowserSourceItem> Items { get; }
    public int TotalCount => Items.Length;
    public string ObservationFingerprint { get; }

    internal static void ValidateExactId(string value)
    {
        if (value is null || !ExactIdPattern.IsMatch(value)) throw new ArgumentException("A bounded exact directory id is required.");
    }
    internal static int ValidateCanonicalPath(string value)
    {
        if (string.IsNullOrEmpty(value) || value != value.Trim() || value[0] != '/' || value.Contains('\\') ||
            value.Any(char.IsControl) || StrictUtf8.GetByteCount(value) > 4096)
            throw new ArgumentException("An exact canonical absolute directory path is required.");
        if (value == "/") return 0;
        var segments = value[1..].Split('/');
        if (segments.Length > 32 || segments.Any(segment => segment is "" or "." or ".." || StrictUtf8.GetByteCount(segment) > 256))
            throw new ArgumentException("A bounded canonical directory path is required.");
        return segments.Length;
    }

    internal string ComputeFingerprint(string sourceUri)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string value)
        {
            var bytes = StrictUtf8.GetBytes(value); Span<byte> size = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(size, (uint)bytes.Length); hash.AppendData(size); hash.AppendData(bytes);
        }
        Add("ManagedTransferDirectory-v1"); Add(sourceUri); Add(BaiduUserId); Add(GenerationToken);
        Add(ParentExactId); Add(CanonicalPath); Add(AncestorExactIds.Length.ToString(CultureInfo.InvariantCulture));
        foreach (var ancestor in AncestorExactIds) Add(ancestor);
        Add(TotalCount.ToString(CultureInfo.InvariantCulture));
        foreach (var item in Items) { Add(item.ExactId); Add(item.Kind.ToString()); Add(item.ServerName); }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
    public override string ToString() => $"ManagedBrowserDirectorySnapshot {{ FolderCount = {TotalCount}, Metadata = [REDACTED] }}";
}

public sealed class ManagedBrowserDirectoryListingResult : ManagedBrowserResult
{
    public ManagedBrowserDirectoryListingResult(ManagedBrowserDirectoryListingResultKind kind, ManagedBrowserDirectorySnapshot? snapshot = null)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if ((kind == ManagedBrowserDirectoryListingResultKind.Observed) != (snapshot is not null))
            throw new ArgumentException("Only observed results require a complete snapshot.");
        Kind = kind; Snapshot = snapshot;
    }
    public ManagedBrowserDirectoryListingResultKind Kind { get; }
    public ManagedBrowserDirectorySnapshot? Snapshot { get; }
    public override string ToString() => $"ManagedBrowserDirectoryListingResult {{ Kind = {Kind}, Metadata = [REDACTED] }}";
}
