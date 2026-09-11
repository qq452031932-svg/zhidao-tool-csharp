using System.Diagnostics;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.App.Features.ManagedBrowser;

public sealed partial class ManagedChromiumProcessManager
{
    /// <summary>
    /// Returns only the already-owned exact handle. It never launches, reconnects, probes, or adopts a process.
    /// </summary>
    internal ManagedChromiumSessionHandle? FindExactOwnedTransferHandle(
        ManagedBrowserSessionReference expectedSession)
    {
        ArgumentNullException.ThrowIfNull(expectedSession);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!owned.TryGetValue(expectedSession.AccountId, out var entry) ||
                entry.Session is not { } handle || !entry.Process.IsAlive ||
                handle.Installation.InstallationId != expectedSession.InstallationId ||
                handle.Installation.BrowserFamily != expectedSession.BrowserFamily ||
                handle.Profile.AccountId != expectedSession.AccountId ||
                !string.Equals(handle.Profile.ProfileId.ToString("N"), expectedSession.ProfileId,
                    StringComparison.Ordinal) ||
                !string.Equals(handle.Profile.OwnershipId.ToString("N"),
                    expectedSession.ProfileOwnershipId, StringComparison.Ordinal))
                return null;
            return handle;
        }
    }

    internal bool IsExactOwnedTransferHandle(ManagedChromiumSessionHandle exactHandle)
    {
        ArgumentNullException.ThrowIfNull(exactHandle);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return owned.TryGetValue(exactHandle.Profile.AccountId, out var entry) &&
                entry.Process.IsAlive && ReferenceEquals(entry.Session, exactHandle);
        }
    }

    /// <summary>
    /// Proves that the exact held handle still owns the same process, loopback endpoint and target.
    /// This method is read-only and cannot discover/adopt a new target or acquire mutation authority.
    /// </summary>
    internal async Task<bool> VerifyTransferRouteAsync(
        ManagedChromiumSessionHandle exactSession,
        ManagedBrowserTargetActionBinding binding,
        ManagedTransferRouteExpectation expectedRoute,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exactSession);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(expectedRoute);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping.Token);
        var token = linked.Token;
        await serial.WaitAsync(token).ConfigureAwait(false);
        try
        {
            OwnedBrowser entry;
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (!owned.TryGetValue(exactSession.Profile.AccountId, out entry!) ||
                    !ReferenceEquals(entry.Session, exactSession))
                    return false;
            }
            if (binding.BrowserId != exactSession.BrowserId ||
                !string.Equals(binding.TargetId, exactSession.TargetId, StringComparison.Ordinal) ||
                binding.DevToolsEndpoint != exactSession.Endpoint ||
                entry.Port is null || entry.Port.BrowserId != exactSession.BrowserId ||
                entry.Port.Endpoint != exactSession.Endpoint ||
                !string.Equals(entry.TargetId, exactSession.TargetId, StringComparison.Ordinal))
                return false;

            var owner = RequireOwnership(entry);
            var bound = TimeSpan.FromMilliseconds(Math.Min(
                startupTimeout.TotalMilliseconds, binding.TimeoutMilliseconds));
            var elapsed = Stopwatch.StartNew();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(bound);
            Task<ManagedBrowserTargetSnapshot?>? probe = null;
            ManagedBrowserTargetSnapshot? target;
            try
            {
                probe = targets.ReadAsync(entry.Port, exactSession.TargetId, deadline.Token);
                target = await probe.WaitAsync(bound, token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                ObserveDetachedProbe(probe);
                return false;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                ObserveDetachedProbe(probe);
                return false;
            }
            finally
            {
                deadline.Cancel();
            }

            RequireOwnership(entry, owner);
            token.ThrowIfCancellationRequested();
            if (elapsed.Elapsed >= bound || target is null || !target.PageUri.IsAbsoluteUri ||
                target.BrowserId != exactSession.BrowserId ||
                !string.Equals(target.TargetId, exactSession.TargetId, StringComparison.Ordinal))
                return false;

            return expectedRoute.State switch
            {
                ManagedTransferRouteState.OriginalSource =>
                    IsExactSourceRoute(target.PageUri, binding.SourceUri),
                ManagedTransferRouteState.OfficialTargetRoot or
                    ManagedTransferRouteState.ExactTargetDirectory =>
                    ManagedBrowserRoutePolicy.Classify(target.PageUri) == ManagedBrowserRoute.PanMain,
                _ => false,
            };
        }
        finally
        {
            serial.Release();
        }
    }

    private static bool IsExactSourceRoute(Uri observed, string expected)
    {
        var value = observed.OriginalString;
        var queryless = expected.Split('?')[0];
        return string.Equals(value, expected, StringComparison.Ordinal) ||
            string.Equals(value, queryless, StringComparison.Ordinal);
    }
}
