using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.App.Features.Acceptance;

public sealed class AcceptanceWriteLeaseCoordinator : IAdapterWriteAuthorizationGate
{
    public static readonly TimeSpan LeaseLifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan FormalEvidenceLifetime = TimeSpan.FromDays(30);

    private readonly object sync = new();
    private readonly AcceptanceWriteLeaseOptions options;
    private readonly IAdapterCapabilityStore store;
    private readonly TimeProvider timeProvider;
    private readonly Dictionary<AdapterWriteAuthorizationRequest, AcceptanceWriteLease> leases = [];
    private readonly HashSet<AdapterWriteAuthorizationRequest> consumedLeaseOperations = [];
    private readonly Dictionary<AdapterWriteAuthorizationRequest, string> completedEvidence = [];

    public AcceptanceWriteLeaseCoordinator(
        AcceptanceWriteLeaseOptions options,
        IAdapterCapabilityStore store,
        TimeProvider timeProvider)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public bool TryCreateLease(
        AdapterWriteAuthorizationRequest request,
        AcceptanceWriteLeaseConfirmation confirmation)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Capability);
        ArgumentNullException.ThrowIfNull(confirmation);
        if (!options.IsEnabled || !confirmation.IsComplete || !HasExactOperationIdentity(request))
            return false;

        var now = timeProvider.GetUtcNow();
        lock (sync)
        {
            RemoveExpiredLeases(now);
            leases[request] = new AcceptanceWriteLease(request, now + LeaseLifetime);
            return true;
        }
    }

    public async ValueTask<bool> TryAuthorizeAsync(
        AdapterWriteAuthorizationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Capability);
        cancellationToken.ThrowIfCancellationRequested();
        var now = timeProvider.GetUtcNow();
        try
        {
            var accepted = await store.GetAsync(
                request.Capability.AdapterKind,
                request.Capability.ProtocolVersion,
                request.Capability.SelectorVersion,
                request.Capability.BuildVersion,
                cancellationToken).ConfigureAwait(false);
            if (accepted is not null &&
                accepted.AcceptedAt <= now &&
                now - accepted.AcceptedAt <= FormalEvidenceLifetime)
                return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }

        if (!options.IsEnabled || !HasExactOperationIdentity(request)) return false;
        lock (sync)
        {
            RemoveExpiredLeases(now);
            if (!leases.Remove(request)) return false;
            consumedLeaseOperations.Add(request);
            return true;
        }
    }

    public bool RecordCompletedEvidence(
        AdapterWriteAuthorizationRequest request,
        bool remoteResultVerified,
        bool recoveryVerified,
        bool localExportVerified,
        string evidenceHash)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!remoteResultVerified || !recoveryVerified || !localExportVerified ||
            !IsUpperSha256(evidenceHash) || !HasExactOperationIdentity(request))
            return false;
        lock (sync)
        {
            if (!consumedLeaseOperations.Contains(request)) return false;
            completedEvidence[request] = evidenceHash;
            return true;
        }
    }

    public async Task<bool> AcceptVersionAsync(
        AdapterWriteAuthorizationRequest request,
        bool userConfirmed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!options.IsEnabled || !userConfirmed) return false;
        string evidenceHash;
        lock (sync)
        {
            if (!completedEvidence.TryGetValue(request, out evidenceHash!)) return false;
        }

        await store.AcceptAsync(new(
            request.Capability.AdapterKind,
            request.Capability.ProtocolVersion,
            request.Capability.SelectorVersion,
            request.Capability.BuildVersion,
            timeProvider.GetUtcNow(),
            evidenceHash), cancellationToken).ConfigureAwait(false);
        lock (sync)
        {
            completedEvidence.Remove(request);
            consumedLeaseOperations.Remove(request);
        }
        return true;
    }

    private static bool HasExactOperationIdentity(AdapterWriteAuthorizationRequest request) =>
        request.AccountId != Guid.Empty && request.OperationId != Guid.Empty && request.Generation > 0;

    private void RemoveExpiredLeases(DateTimeOffset now)
    {
        foreach (var key in leases.Where(pair => pair.Value.ExpiresAtUtc <= now).Select(pair => pair.Key).ToArray())
            leases.Remove(key);
    }

    private static bool IsUpperSha256(string value) =>
        value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');
}
