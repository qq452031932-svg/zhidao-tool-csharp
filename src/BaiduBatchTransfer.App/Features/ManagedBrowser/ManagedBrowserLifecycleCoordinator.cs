using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Infrastructure.Security;
using System.Collections.Concurrent;
using System.IO;

namespace BaiduBatchTransfer.App.Features.ManagedBrowser;

public sealed class ManagedBrowserLifecycleCoordinator(IManagedBrowserStateStore store,
    ManagedChromiumProcessManager manager, string appDataRoot, IManagedBrowserProfilePolicy? profilePolicy = null) : IManagedBrowserAccountLifecycle
{
    private readonly IManagedBrowserStateStore store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly ManagedChromiumProcessManager manager = manager ?? throw new ArgumentNullException(nameof(manager));
    private readonly string appDataRoot = Path.GetFullPath(appDataRoot);
    private readonly IManagedBrowserProfilePolicy profiles = profilePolicy ?? new FileSystemManagedBrowserProfilePolicy(appDataRoot);
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> accountGates = new();
    private readonly ConcurrentDictionary<Guid, PendingBrowserLogin> pending = new();
    private readonly ConcurrentDictionary<Guid, PendingBrowserLogin> unboundProfiles = new();

    public async Task<IDisposable> AcquireAccountLeaseAsync(Guid accountId, CancellationToken token)
    {
        if (accountId == Guid.Empty) throw new ArgumentException("An account is required.", nameof(accountId));
        var gate = accountGates.GetOrAdd(accountId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token).ConfigureAwait(false);
        return new AccountLease(gate);
    }

    // These methods are called inside the account lease, including during account removal.
    public async Task<ManagedBrowserStopStatus> StopPendingAsync(Guid accountId, CancellationToken token)
    {
        if (!pending.TryGetValue(accountId, out var attempt)) return ManagedBrowserStopStatus.NotRunning;
        var result = await manager.StopOwnedProfileAsync(attempt.Profile, token).ConfigureAwait(false);
        if (result != ManagedBrowserStopStatus.Blocked) pending.TryRemove(accountId, out _);
        return result;
    }

    public async Task<ManagedBrowserStopStatus> PreparePendingRemovalAsync(Guid accountId, CancellationToken token)
    {
        var stopped = await StopPendingAsync(accountId, token).ConfigureAwait(false);
        if (stopped == ManagedBrowserStopStatus.Blocked) return stopped;
        // Task 6 cannot quarantine an identity which has never been bound. Keep its exact descriptor,
        // even after a family change, and fail closed instead of inventing a UID or reporting removal.
        if (unboundProfiles.Values.Any(x => x.Profile.AccountId == accountId)) return ManagedBrowserStopStatus.Blocked;
        var current = await LoadPersistedAsync(accountId, token).ConfigureAwait(false);
        foreach (var family in new[] { BrowserFamily.Chrome, BrowserFamily.Edge })
        {
            var presence = await profiles.ProbeExistingAsync(accountId, family, token).ConfigureAwait(false);
            if (presence == ManagedBrowserProfilePresence.Uncertain) return ManagedBrowserStopStatus.Blocked;
            if (presence == ManagedBrowserProfilePresence.Absent) continue;
            // Conservatively retain profiles not represented by Task 6's current-session cleanup,
            // including orphaned pending logins after restart and old-family retained directories.
            if (current is null || current.BrowserFamily != family ||
                (await profiles.OpenExistingAsync(Describe(current.Session), token).ConfigureAwait(false)).Status != ManagedBrowserProfileStatus.Ready)
                return ManagedBrowserStopStatus.Blocked;
        }
        return stopped;
    }

    public async Task<ManagedBrowserLifecycleSession?> LoadActiveSessionAsync(Guid accountId, CancellationToken token)
    {
        if (pending.ContainsKey(accountId) || unboundProfiles.Values.Any(x => x.Profile.AccountId == accountId))
            throw new InvalidOperationException("Pending browser ownership must be stopped before removal.");
        return await LoadPersistedAsync(accountId, token).ConfigureAwait(false);
    }

    internal async Task<ManagedBrowserLifecycleSession?> LoadPersistedAsync(Guid accountId, CancellationToken token)
    {
        var chrome = await store.LoadActiveSessionAsync(accountId, BrowserFamily.Chrome, token).ConfigureAwait(false);
        var edge = await store.LoadActiveSessionAsync(accountId, BrowserFamily.Edge, token).ConfigureAwait(false);
        if (chrome is not null && edge is not null) throw new InvalidOperationException("Ambiguous active browser identity.");
        var current = chrome ?? edge;
        if (current is null) return null;
        var installation = await store.LoadInstallationAsync(current.Identity.InstallationId, token).ConfigureAwait(false);
        if (installation is null || installation.Identity.BrowserFamily != current.Identity.BrowserFamily)
            throw new InvalidOperationException("The stored browser identity is unavailable.");
        return new(current.Identity, installation.Revision, current.Revision, Describe(current.Identity).CanonicalSourcePath);
    }

    public async Task<ManagedBrowserStopStatus> StopAsync(ManagedBrowserLifecycleSession session, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(session);
        var current = await LoadPersistedAsync(session.AccountId, token).ConfigureAwait(false);
        if (current is null || !SameSnapshot(current, session)) return ManagedBrowserStopStatus.Blocked;
        if (await StopPendingAsync(session.AccountId, token).ConfigureAwait(false) == ManagedBrowserStopStatus.Blocked)
            return ManagedBrowserStopStatus.Blocked;
        return await manager.StopOwnedProfileAsync(Describe(session.Session), token).ConfigureAwait(false);
    }

    internal ManagedBrowserProfileDescriptor Describe(ManagedBrowserSessionReference session) =>
        ManagedBrowserProfileDescriptor.FromStoredSession(session, appDataRoot);
    internal PendingBrowserLogin? Pending(Guid accountId) => pending.GetValueOrDefault(accountId);
    internal void Track(PendingBrowserLogin attempt, bool alreadyBound)
    {
        pending[attempt.Profile.AccountId] = attempt;
        if (!alreadyBound) unboundProfiles[attempt.Profile.OwnershipId] = attempt;
    }
    internal void Bound(ManagedBrowserProfileDescriptor profile) => unboundProfiles.TryRemove(profile.OwnershipId, out _);

    private static bool SameSnapshot(ManagedBrowserLifecycleSession x, ManagedBrowserLifecycleSession y) =>
        x.AccountId == y.AccountId && x.SessionId == y.SessionId && x.InstallationId == y.InstallationId &&
        x.BrowserFamily == y.BrowserFamily && x.ProfileId == y.ProfileId && x.ProfileOwnershipId == y.ProfileOwnershipId &&
        x.Session.StableBaiduUserId == y.Session.StableBaiduUserId && x.InstallationRevision == y.InstallationRevision &&
        x.SessionRevision == y.SessionRevision && string.Equals(x.CanonicalProfilePath, y.CanonicalProfilePath, StringComparison.OrdinalIgnoreCase);

    private sealed class AccountLease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? gate = gate;
        public void Dispose() => Interlocked.Exchange(ref gate, null)?.Release();
    }
}

internal sealed record PendingBrowserLogin(BrowserInstallationIdentity Installation,
    ManagedBrowserProfileDescriptor Profile, Guid SessionId)
{
    public override string ToString() => "PendingBrowserLogin { Identity = [REDACTED] }";
}
