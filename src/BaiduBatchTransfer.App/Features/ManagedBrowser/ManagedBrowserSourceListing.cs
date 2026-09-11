using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace BaiduBatchTransfer.App.Features.ManagedBrowser;

/// <summary>Read-only request in a caller-verified target; does not navigate or grant write authority.</summary>
public sealed class ManagedBrowserReadTransferSourceRequest : ManagedBrowserRequestPayload
{
    public ManagedBrowserReadTransferSourceRequest(Guid browserId, string targetId, Uri devToolsEndpoint,
        int timeoutMilliseconds, string sourceUri, string expectedBaiduUserId)
        : this(new ManagedBrowserOpenTransferSourceRequest(browserId, targetId, devToolsEndpoint,
            timeoutMilliseconds, sourceUri, expectedBaiduUserId)) { }

    internal ManagedBrowserReadTransferSourceRequest(ManagedBrowserOpenTransferSourceRequest binding) => Binding = binding;
    public ManagedBrowserOpenTransferSourceRequest Binding { get; }
    public override string ToString() => "ManagedBrowserReadTransferSourceRequest { Binding = [REDACTED] }";
}

public enum ManagedBrowserSourceListingResultKind
{
    Observed = 1, ExtractionRequired, LoginRequired, VerificationRequired, PageNotValidated,
    BrowserUnavailable, AccountIdentityMismatch, TimedOut, Cancelled,
}

public enum ManagedBrowserSourceItemKind { File = 1, Folder = 2 }

public sealed class ManagedBrowserSourceItem
{
    private static readonly Regex ExactIdPattern = new(@"\A[A-Za-z0-9_-]{1,128}\z", RegexOptions.NonBacktracking);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public ManagedBrowserSourceItem(string exactId, ManagedBrowserSourceItemKind kind, string serverName)
    {
        if (exactId is null || !ExactIdPattern.IsMatch(exactId))
            throw new ArgumentException("A bounded exact resource id is required.", nameof(exactId));
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (string.IsNullOrEmpty(serverName) || serverName.Any(char.IsControl) || StrictUtf8.GetByteCount(serverName) > 256)
            throw new ArgumentException("A bounded exact resource name is required.", nameof(serverName));
        ExactId = exactId;
        Kind = kind;
        ServerName = serverName;
    }

    public string ExactId { get; }
    public ManagedBrowserSourceItemKind Kind { get; }
    public string ServerName { get; }
    public override string ToString() => $"ManagedBrowserSourceItem {{ Kind = {Kind}, Metadata = [REDACTED] }}";
}

/// <summary>Complete bounded candidate snapshot. Never claims live validation or verified ownership.</summary>
public sealed class ManagedBrowserSourceSnapshot
{
    public ManagedBrowserSourceSnapshot(string baiduUserId, string generationToken, int totalCount,
        bool reachedEnd, bool isStable, IEnumerable<ManagedBrowserSourceItem> items, string observationFingerprint)
    {
        ManagedBrowserProtocol.ValidateBoundedText(baiduUserId, nameof(baiduUserId), 256);
        ManagedBrowserProtocol.ValidateBoundedText(generationToken, nameof(generationToken), 256);
        ArgumentNullException.ThrowIfNull(items);
        // Bound enumeration as well as the wire frame; never silently truncate an oversized list.
        var copy = items.Take(33).ToArray();
        if (copy.Length is < 1 or > 32 || copy.Any(item => item is null) || totalCount != copy.Length || !reachedEnd || !isStable)
            throw new ArgumentException("A complete, stable bounded source listing is required.");
        if (copy.Select(item => item.ExactId).Distinct(StringComparer.Ordinal).Count() != copy.Length)
            throw new ArgumentException("Source resource ids must be unique.");
        if (!ManagedBrowserProtocol.IsNonce(observationFingerprint))
            throw new ArgumentException("A lowercase SHA256 fingerprint is required.", nameof(observationFingerprint));
        BaiduUserId = baiduUserId;
        GenerationToken = generationToken;
        Items = copy.OrderBy(item => item.ExactId, StringComparer.Ordinal).ToImmutableArray();
        ObservationFingerprint = observationFingerprint;
    }

    public string BaiduUserId { get; }
    public string GenerationToken { get; }
    public ImmutableArray<ManagedBrowserSourceItem> Items { get; }
    public int TotalCount => Items.Length;
    public string ObservationFingerprint { get; }

    internal string ComputeFingerprint(string sourceUri)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            Span<byte> size = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(size, (uint)bytes.Length);
            hash.AppendData(size);
            hash.AppendData(bytes);
        }
        Add("ManagedTransferSource-v1");
        Add(sourceUri);
        Add(BaiduUserId);
        Add(GenerationToken);
        Add(TotalCount.ToString(CultureInfo.InvariantCulture));
        foreach (var item in Items) { Add(item.ExactId); Add(item.Kind.ToString()); Add(item.ServerName); }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public override string ToString() => $"ManagedBrowserSourceSnapshot {{ Count = {TotalCount}, Metadata = [REDACTED] }}";
}

public sealed class ManagedBrowserSourceListingResult : ManagedBrowserResult
{
    public ManagedBrowserSourceListingResult(ManagedBrowserSourceListingResultKind kind, ManagedBrowserSourceSnapshot? snapshot = null)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if ((kind == ManagedBrowserSourceListingResultKind.Observed) != (snapshot is not null))
            throw new ArgumentException("Only observed results require a complete snapshot.", nameof(snapshot));
        Kind = kind;
        Snapshot = snapshot;
    }
    public ManagedBrowserSourceListingResultKind Kind { get; }
    public ManagedBrowserSourceSnapshot? Snapshot { get; }
    public override string ToString() => $"ManagedBrowserSourceListingResult {{ Kind = {Kind}, Metadata = [REDACTED] }}";
}
