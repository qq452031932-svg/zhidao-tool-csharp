using System.Collections.Concurrent;

namespace BaiduBatchTransfer.App.Features.Acceptance;

public interface IBaiduAcceptanceProbeDeferredCleanupRegistry
{
    void Register(BaiduAcceptanceProbeProfileLease lease, Task eventuallyClosed);
}

public sealed class BaiduAcceptanceProbeDeferredCleanupRegistry : IBaiduAcceptanceProbeDeferredCleanupRegistry
{
    private readonly ConcurrentDictionary<Guid, BaiduAcceptanceProbeProfileLease> pending = new();

    public void Register(BaiduAcceptanceProbeProfileLease lease, Task eventuallyClosed)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(eventuallyClosed);
        var registrationId = Guid.NewGuid();
        if (!pending.TryAdd(registrationId, lease)) return;
        _ = CleanupAfterCloseAsync(registrationId, lease, eventuallyClosed);
    }

    private async Task CleanupAfterCloseAsync(
        Guid registrationId,
        BaiduAcceptanceProbeProfileLease lease,
        Task eventuallyClosed)
    {
        try
        {
            await eventuallyClosed.ConfigureAwait(false);
        }
        catch (Exception)
        {
            lease.MarkCleanupDeferred();
            return;
        }
        try
        {
            await lease.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            lease.MarkCleanupDeferred();
            return;
        }
        pending.TryRemove(registrationId, out _);
    }
}
