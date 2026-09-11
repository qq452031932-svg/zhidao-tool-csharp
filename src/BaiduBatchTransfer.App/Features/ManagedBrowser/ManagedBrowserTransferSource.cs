using System.Text.RegularExpressions;

namespace BaiduBatchTransfer.App.Features.ManagedBrowser;

/// <summary>One official navigation in a caller-verified, owned target; never write authorization.</summary>
public sealed class ManagedBrowserOpenTransferSourceRequest : ManagedBrowserRequestPayload
{
    private static readonly Regex CanonicalSource = new(
        @"\Ahttps://pan\.baidu\.com/s/[A-Za-z0-9_-]{1,128}(?:\?pwd=[A-Za-z0-9]{4})?\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public ManagedBrowserOpenTransferSourceRequest(
        Guid browserId, string targetId, Uri devToolsEndpoint, int timeoutMilliseconds,
        string sourceUri, string expectedBaiduUserId)
    {
        // Keep endpoint, target and time-budget validation identical to identity observation.
        var binding = new ManagedBrowserObserveAccountIdentityRequest(
            browserId, targetId, devToolsEndpoint, timeoutMilliseconds);
        if (sourceUri is null || sourceUri.Length > 161 || !CanonicalSource.IsMatch(sourceUri))
            throw new ArgumentException("A canonical official share URI is required.", nameof(sourceUri));
        ManagedBrowserProtocol.ValidateBoundedText(expectedBaiduUserId, nameof(expectedBaiduUserId), 256);
        BrowserId = binding.BrowserId;
        TargetId = binding.TargetId;
        DevToolsEndpoint = binding.DevToolsEndpoint;
        TimeoutMilliseconds = binding.TimeoutMilliseconds;
        SourceUri = sourceUri;
        ExpectedBaiduUserId = expectedBaiduUserId;
    }

    public Guid BrowserId { get; }
    public string TargetId { get; }
    public Uri DevToolsEndpoint { get; }
    public int TimeoutMilliseconds { get; }
    public string SourceUri { get; }
    public string ExpectedBaiduUserId { get; }
    public override string ToString() =>
        $"ManagedBrowserOpenTransferSourceRequest {{ Binding = [REDACTED], Source = [REDACTED], Identity = [REDACTED], TimeoutMilliseconds = {TimeoutMilliseconds} }}";
}

public enum ManagedBrowserTransferSourceResultKind
{
    SourceOpened = 1,
    ExtractionRequired = 2,
    LoginRequired = 3,
    VerificationRequired = 4,
    PageNotValidated = 5,
    BrowserUnavailable = 6,
    AccountIdentityMismatch = 7,
    TimedOut = 8,
    Cancelled = 9,
}

/// <summary>A page-stage hint only. Does not prove exact resources, transfer completion or permission to write.</summary>
public sealed class ManagedBrowserTransferSourceResult : ManagedBrowserResult
{
    public ManagedBrowserTransferSourceResult(ManagedBrowserTransferSourceResultKind kind)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        Kind = kind;
    }

    public ManagedBrowserTransferSourceResultKind Kind { get; }
    public override string ToString() => $"ManagedBrowserTransferSourceResult {{ Kind = {Kind} }}";
}
