using System.Diagnostics;

namespace BaiduBatchTransfer.App.Features.ManagedBrowser;

public sealed partial class ManagedChromiumProcessManager
{
    /// <summary>Read-only source proof for the exact held handle, independent of login-route policy.</summary>
    internal async Task<bool> VerifyTransferSourceAsync(ManagedChromiumSessionHandle exactSession,
        ManagedBrowserOpenTransferSourceRequest binding, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exactSession);
        ArgumentNullException.ThrowIfNull(binding);
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
                    !ReferenceEquals(entry.Session, exactSession)) return false;
            }
            if (binding.BrowserId != exactSession.BrowserId || binding.TargetId != exactSession.TargetId ||
                binding.DevToolsEndpoint != exactSession.Endpoint || entry.Port?.BrowserId != binding.BrowserId ||
                entry.Port.Endpoint != binding.DevToolsEndpoint || entry.TargetId != binding.TargetId) return false;

            var owner = RequireOwnership(entry);
            var bound = TimeSpan.FromMilliseconds(Math.Min(startupTimeout.TotalMilliseconds, binding.TimeoutMilliseconds));
            var elapsed = Stopwatch.StartNew();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(bound);
            ManagedBrowserTargetSnapshot? target;
            Task<ManagedBrowserTargetSnapshot?>? probe = null;
            try
            {
                // Supplying only the existing target and no callback prevents target adoption/discovery.
                probe = targets.ReadAsync(entry.Port, exactSession.TargetId, deadline.Token);
                target = await probe.WaitAsync(bound, token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                ObserveDetachedProbe(probe);
                target = null;
            }
            catch (OperationCanceledException)
            {
                ObserveDetachedProbe(probe);
                if (token.IsCancellationRequested) throw;
                target = null;
            }
            finally { deadline.Cancel(); }

            RequireOwnership(entry, owner);
            token.ThrowIfCancellationRequested();
            if (elapsed.Elapsed >= bound || target is null || !target.PageUri.IsAbsoluteUri ||
                target.BrowserId != binding.BrowserId || target.TargetId != binding.TargetId) return false;
            var page = target.PageUri.OriginalString;
            var queryless = binding.SourceUri.Split('?')[0];
            return string.Equals(page, binding.SourceUri, StringComparison.Ordinal) ||
                string.Equals(page, queryless, StringComparison.Ordinal);
        }
        finally { serial.Release(); }
    }

    private static void ObserveDetachedProbe(Task? probe)
    {
        if (probe is null || probe.IsCompletedSuccessfully || probe.IsCanceled) return;
        // WaitAsync bounds this proof only. It does not stop or serialize an uncooperative probe;
        // this non-faulting observer solely consumes any eventual private late failure.
        _ = ObserveAsync(probe);

        static async Task ObserveAsync(Task pending)
        {
            try { await pending.ConfigureAwait(false); }
            catch (Exception) { /* Late target-probe details remain redacted. */ }
        }
    }
}
