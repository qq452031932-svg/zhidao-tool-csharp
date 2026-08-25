using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.App.Features.BrowserAutomation;
using BaiduBatchTransfer.BaiduAdapter.WebAutomation;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Application.Jobs;
using System.Security.Cryptography;
using System.Text;

namespace BaiduBatchTransfer.App.Features.Acceptance;

public sealed class AcceptanceWriteLeaseCoordinator : IAdapterWriteAuthorizationGate,
    IAcceptanceOperationEvidenceSink, ILocalResultExportObserver,
    IAcceptanceNavigationAuthorizationSource
{
    public static readonly TimeSpan LeaseLifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan FormalEvidenceLifetime = TimeSpan.FromDays(30);

    private readonly object sync = new();
    private readonly AcceptanceWriteLeaseOptions options;
    private readonly IAdapterCapabilityStore store;
    private readonly TimeProvider timeProvider;
    private readonly IAcceptanceWriteLeasePresenter? presenter;
    private readonly Dictionary<AcceptanceWriteLeaseIdentity, AcceptanceWriteLease> leases = [];
    private readonly HashSet<AcceptanceWriteLeaseIdentity> createdLeaseIdentities = [];
    private readonly Dictionary<AcceptanceWriteLeaseIdentity, HashSet<AdapterWriteAction>> consumedLeaseOperations = [];
    private readonly Dictionary<AcceptanceWriteLeaseIdentity, DateTimeOffset> operationAuthorizationExpiries = [];
    private readonly Dictionary<AcceptanceWriteLeaseIdentity, EvidenceState> evidence = [];
    private readonly Dictionary<AcceptanceWriteLeaseIdentity, string> completedEvidence = [];
    private readonly Dictionary<AcceptanceWriteLeaseIdentity, DateTimeOffset> navigationScopes = [];

    public AcceptanceWriteLeaseCoordinator(
        AcceptanceWriteLeaseOptions options,
        IAdapterCapabilityStore store,
        TimeProvider timeProvider,
        IAcceptanceWriteLeasePresenter? presenter = null)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.presenter = presenter;
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
        var key = AcceptanceWriteLeaseIdentity.From(request);
        lock (sync)
        {
            RemoveExpiredLeases(now);
            if (createdLeaseIdentities.Contains(key) || consumedLeaseOperations.ContainsKey(key) ||
                completedEvidence.ContainsKey(key))
                return false;
            createdLeaseIdentities.Add(key);
            leases[key] = new AcceptanceWriteLease(request, now + LeaseLifetime);
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
            {
                RegisterNavigationScope(request, accepted.AcceptedAt + FormalEvidenceLifetime);
                return true;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }

        if (!options.IsEnabled || !HasExactOperationIdentity(request) || request.IsRecovery) return false;
        if (TryConsume(request, now)) return true;
        if (presenter is null || !await presenter.PresentAsync(request, cancellationToken).ConfigureAwait(false))
            return false;
        if (!TryCreateLease(request, new(true, true, true))) return false;
        return TryConsume(request, timeProvider.GetUtcNow());
    }

    private bool TryConsume(AdapterWriteAuthorizationRequest request, DateTimeOffset now)
    {
        var key = AcceptanceWriteLeaseIdentity.From(request);
        lock (sync)
        {
            RemoveExpiredLeases(now);
            if (consumedLeaseOperations.TryGetValue(key, out var actions))
            {
                if (!operationAuthorizationExpiries.TryGetValue(key, out var operationExpires) ||
                    operationExpires <= now) return false;
                if (actions.Contains(request.Action) ||
                    request.Action != AdapterWriteAction.SubmitTransfer ||
                    !actions.Contains(AdapterWriteAction.EnsureDirectory)) return false;
                actions.Add(request.Action);
                return true;
            }
            if (!leases.Remove(key, out var lease)) return false;
            consumedLeaseOperations[key] = [request.Action];
            operationAuthorizationExpiries[key] = lease.ExpiresAtUtc;
            RegisterNavigationScopeLocked(key, lease.ExpiresAtUtc);
            return true;
        }
    }

    public bool IsAuthorized(BaiduNavigationAuthorizationRequest request)
    {
        if (request.AcceptanceScope is not { } scope ||
            string.IsNullOrWhiteSpace(scope.AdapterKind) ||
            scope.AccountId == Guid.Empty || scope.OperationId == Guid.Empty ||
            scope.Generation < 1 || scope.BatchId == Guid.Empty)
            return false;
        var key = new AcceptanceWriteLeaseIdentity(
            new AdapterCapabilityVersion(
                scope.AdapterKind,
                request.ProtocolVersion,
                request.SelectorVersion,
                request.ApplicationBuildVersion),
            scope.AccountId,
            scope.OperationId,
            scope.Generation,
            scope.BatchId);
        var now = timeProvider.GetUtcNow();
        lock (sync)
        {
            RemoveExpiredNavigationScopes(now);
            return navigationScopes.TryGetValue(key, out var expires) && expires > now;
        }
    }

    private void RegisterNavigationScope(
        AdapterWriteAuthorizationRequest request,
        DateTimeOffset expiresAt)
    {
        lock (sync) RegisterNavigationScopeLocked(AcceptanceWriteLeaseIdentity.From(request), expiresAt);
    }

    private void RegisterNavigationScopeLocked(
        AcceptanceWriteLeaseIdentity key,
        DateTimeOffset expiresAt) => navigationScopes[key] = expiresAt;

    private void RemoveExpiredNavigationScopes(DateTimeOffset now)
    {
        foreach (var key in navigationScopes.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToArray())
            navigationScopes.Remove(key);
    }

    public async Task<bool> AcceptVersionAsync(
        AdapterWriteAuthorizationRequest request,
        bool userConfirmed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!options.IsEnabled || !userConfirmed) return false;
        string evidenceHash;
        var key = AcceptanceWriteLeaseIdentity.From(request);
        lock (sync)
        {
            if (!completedEvidence.TryGetValue(key, out evidenceHash!)) return false;
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
            completedEvidence.Remove(key);
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

    public Task RecordRemoteResultAsync(
        AdapterWriteAuthorizationRequest request,
        string exactEvidenceFingerprint,
        CancellationToken cancellationToken = default) =>
        RecordEvidenceAsync(request, exactEvidenceFingerprint,
            static (state, fingerprint) => state.RemoteFingerprint switch
            {
                null when !state.RemoteMismatch => state with
                {
                    Remote = true,
                    RemoteFingerprint = fingerprint,
                },
                { } existing when string.Equals(existing, fingerprint, StringComparison.Ordinal) => state,
                _ => state with { RemoteMismatch = true, Remote = false, Recovery = false },
            },
            cancellationToken);

    public Task RecordRecoveryAsync(
        AdapterWriteAuthorizationRequest request,
        string exactEvidenceFingerprint,
        CancellationToken cancellationToken = default) =>
        !request.IsRecovery
            ? Task.CompletedTask
            : RecordEvidenceAsync(request, exactEvidenceFingerprint,
                static (state, fingerprint) => string.Equals(
                    state.RemoteFingerprint, fingerprint, StringComparison.Ordinal) &&
                    !state.RemoteMismatch
                    ? state with { Recovery = true }
                    : state with { RemoteMismatch = true, Recovery = false },
                cancellationToken);

    public async Task OnLocalResultExportCompletedAsync(
        LocalResultExportCompletedEvidence exportEvidence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exportEvidence);
        var exportKey = exportEvidence.ExportKey;
        if (!LocalResultWorkbookManifest.IsFingerprint(exportEvidence.LayoutFingerprint) ||
            !LocalResultWorkbookManifest.IsFingerprint(exportEvidence.ArtifactFingerprint)) return;
        AcceptanceWriteLeaseIdentity[] matches;
        lock (sync)
        {
            matches = consumedLeaseOperations.Keys.Where(key =>
                key.BatchId == exportKey.OwnerId && MatchesExportEvidence(key, exportKey) &&
                exportEvidence.Operations.Any(operation =>
                    string.Equals(operation.AdapterKind, key.Capability.AdapterKind, StringComparison.Ordinal) &&
                    operation.AccountId == key.AccountId &&
                    operation.OperationId == key.OperationId &&
                    operation.Generation == key.Generation &&
                    LocalResultWorkbookManifest.IsFingerprint(operation.ContentFingerprint) &&
                    evidence.TryGetValue(key, out var state) && !state.RemoteMismatch &&
                    string.Equals(
                        operation.ContentFingerprint, state.RemoteFingerprint,
                        StringComparison.Ordinal))).ToArray();
        }
        foreach (var key in matches)
        {
            var request = new AdapterWriteAuthorizationRequest(
                key.Capability, key.AccountId, key.OperationId, key.Generation, key.BatchId);
            await RecordEvidenceAsync(
                request,
                Hash($"{exportKey.OwnerKind}|{exportKey.OwnerId:D}|{exportKey.ExportKind}|{exportKey.Generation}|{exportEvidence.LayoutFingerprint}|{exportEvidence.ArtifactFingerprint}"),
                static (state, _) => state with { LocalExport = true },
                cancellationToken).ConfigureAwait(false);
        }
    }

    public IReadOnlyList<LocalResultOperationIdentity> GetTrackedOperations(LocalResultExportKey exportKey)
    {
        lock (sync)
        {
            return consumedLeaseOperations.Keys
                .Where(key => key.BatchId == exportKey.OwnerId && MatchesExportEvidence(key, exportKey))
                .Select(key => new LocalResultOperationIdentity(
                    key.Capability.AdapterKind, key.AccountId, key.OperationId, key.Generation))
                .ToArray();
        }
    }

    private async Task RecordEvidenceAsync(
        AdapterWriteAuthorizationRequest request,
        string fingerprint,
        Func<EvidenceState, string, EvidenceState> apply,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        var key = AcceptanceWriteLeaseIdentity.From(request);
        string? finalHash = null;
        lock (sync)
        {
            if (!consumedLeaseOperations.ContainsKey(key)) return;
            var current = evidence.TryGetValue(key, out var recorded) ? recorded : new EvidenceState();
            var next = apply(current, fingerprint);
            next = next with { CombinedFingerprint = Hash($"{next.CombinedFingerprint}|{fingerprint}") };
            evidence[key] = next;
            if (next is { Remote: true, Recovery: true, LocalExport: true, RemoteMismatch: false } &&
                !completedEvidence.ContainsKey(key))
            {
                finalHash = next.CombinedFingerprint;
                completedEvidence[key] = finalHash;
            }
        }
        if (finalHash is not null && presenter is not null)
        {
            try
            {
                await presenter.EnableFormalAcceptanceAsync(request, finalHash, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Evidence is already captured; presentation telemetry cannot change an exact result.
            }
        }
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool MatchesExportEvidence(
        AcceptanceWriteLeaseIdentity lease,
        LocalResultExportKey export) =>
        lease.Capability.AdapterKind switch
        {
            "BaiduTransfer" => export is
            {
                OwnerKind: LocalResultOwnerKind.TransferBatch,
                ExportKind: LocalResultExportKind.ReplacementWorkbook,
            },
            "BaiduShare" => export is
            {
                OwnerKind: LocalResultOwnerKind.TransferBatch,
                ExportKind: LocalResultExportKind.ReplacementWorkbook,
            } or
            {
                OwnerKind: LocalResultOwnerKind.StandaloneShareBatch,
                ExportKind: LocalResultExportKind.StandaloneShareWorkbook,
            },
            _ => false,
        };

    private sealed record EvidenceState(
        bool Remote = false,
        bool Recovery = false,
        bool LocalExport = false,
        string? RemoteFingerprint = null,
        bool RemoteMismatch = false,
        string CombinedFingerprint = "");
}
