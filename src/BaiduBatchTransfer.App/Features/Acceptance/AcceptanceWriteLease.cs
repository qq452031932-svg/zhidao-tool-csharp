using BaiduBatchTransfer.Application.Scheduling;

namespace BaiduBatchTransfer.App.Features.Acceptance;

public sealed record AcceptanceWriteLease(
    AdapterWriteAuthorizationRequest Request,
    DateTimeOffset ExpiresAtUtc)
{
    public override string ToString() =>
        $"AcceptanceWriteLease {{ AdapterKind = {Request.Capability.AdapterKind}, ExpiresAtUtc = {ExpiresAtUtc:O}, Identity = [REDACTED], Versions = [REDACTED] }}";
}

public sealed record AcceptanceWriteLeaseConfirmation(
    bool IsDisposableTestResource,
    bool ConsentsToRealOperation,
    bool UnderstandsSingleOperation)
{
    public bool IsComplete =>
        IsDisposableTestResource && ConsentsToRealOperation && UnderstandsSingleOperation;
}

public sealed class AcceptanceWriteLeaseOptions(bool isDebugBuild, IReadOnlyList<string> arguments)
{
    public const string Switch = "--webview-acceptance";

    private readonly IReadOnlyList<string> arguments =
        arguments ?? throw new ArgumentNullException(nameof(arguments));

    public bool IsEnabled => isDebugBuild && arguments.Any(argument =>
        string.Equals(argument, Switch, StringComparison.Ordinal));

    public static AcceptanceWriteLeaseOptions FromStartup(IReadOnlyList<string> arguments)
    {
#if DEBUG
        const bool isDebugBuild = true;
#else
        const bool isDebugBuild = false;
#endif
        return new(isDebugBuild, arguments);
    }
}
