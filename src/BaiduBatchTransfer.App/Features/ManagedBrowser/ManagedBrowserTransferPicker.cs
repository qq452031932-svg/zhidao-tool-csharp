namespace BaiduBatchTransfer.App.Features.ManagedBrowser;

/// <summary>Fixed mutation payload; creation is internal to the permit-backed integration boundary.</summary>
internal sealed class ManagedBrowserOpenTransferPickerRequest : ManagedBrowserRequestPayload
{
    internal ManagedBrowserOpenTransferPickerRequest(ManagedBrowserOpenTransferSourceRequest binding,
        ManagedBrowserSourceSnapshot expectedSource)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(expectedSource);
        if (expectedSource.BaiduUserId != binding.ExpectedBaiduUserId ||
            expectedSource.ComputeFingerprint(binding.SourceUri) != expectedSource.ObservationFingerprint)
            throw new ArgumentException("The source evidence must match its immutable binding.");
        Binding = binding;
        ExpectedSource = expectedSource;
    }
    internal ManagedBrowserOpenTransferSourceRequest Binding { get; }
    internal ManagedBrowserSourceSnapshot ExpectedSource { get; }
    public override string ToString() => "ManagedBrowserOpenTransferPickerRequest { Evidence = [REDACTED] }";
}

internal enum ManagedBrowserTransferPickerResultKind
{
    PickerOpened = 1, PageNotValidated, AccountIdentityMismatch, LoginRequired, VerificationRequired,
    BrowserUnavailable, TimedOut, Cancelled, OutcomeUnknown,
}

internal sealed class ManagedBrowserTransferPickerResult : ManagedBrowserResult
{
    internal ManagedBrowserTransferPickerResult(ManagedBrowserTransferPickerResultKind kind)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        Kind = kind;
    }
    internal ManagedBrowserTransferPickerResultKind Kind { get; }
    public override string ToString() => $"ManagedBrowserTransferPickerResult {{ Kind = {Kind} }}";
}
