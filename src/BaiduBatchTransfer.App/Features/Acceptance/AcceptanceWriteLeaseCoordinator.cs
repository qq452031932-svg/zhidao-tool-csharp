using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Application.Jobs;
using System.Security.Cryptography;
using System.Text;

namespace BaiduBatchTransfer.App.Features.Acceptance;

public sealed class AcceptanceWriteLeaseCoordinator : IAdapterWriteAuthorizationGate,
    IAcceptanceOperationEvidenceSink, ILocalResultExportObserver
{
    public static readonly TimeSpan LeaseLifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan FormalEvidenceLifetime = TimeSpan.FromDays(30);

    private readonly object sync = new();
    private readonly AcceptanceWriteLeaseOptions options;
    private readonly IAdapterCapabilityStore store;
    private readonly TimeProvider timeProvider;
    private readonly IAcceptanceWriteLeasePresenter? presenter;
    private readonly Dictionary<AcceptanceWriteLeaseIdentity, AcceptanceWriteLease> leases = [];
    private readonly Dictionary<AcceptanceWriteLeaseIdentity, HashSet<AdapterWriteAction>> consumedLeaseOperations = [];
    private readonly Dictionary<AcceptanceWriteLeaseIdentity, EvidenceState> evidence = [];
    private readonly Dictionary<AcceptanceWriteLeaseIdentity, string> completedEvidence = [];

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
            if (leases.ContainsKey(key) || consumedLeaseOperations.ContainsKey(key) || completedEvidence.ContainsKey(key))
                return false;
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
                if (actions.Contains(request.Action) ||
                    request.Action != AdapterWriteAction.SubmitTransfer ||
                    !actions.Contains(AdapterWriteAction.EnsureDirectory)) return false;
                actions.Add(request.Action);
                return true;
            }
            if (!leases.Remove(key)) return false;
            consumedLeaseOperations[key] = [request.Action];
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
            static (state, fingerprint) => state with { Remote = true, RemoteFingerprint = fingerprint },
            cancellationToken);

    public Task RecordRecoveryAsync(
        AdapterWriteAuthorizationRequest request,
        string exactEvidenceFingerprint,
        CancellationToken cancellationToken = default) =>
        !request.IsRecovery
            ? Task.CompletedTask
            : RecordEvidenceAsync(request, exactEvidenceFingerprint,
                static (state, fingerprint) => string.Equals(
                    state.RemoteFingerprint, fingerprint, StringComparison.Ordinal)
                    ? state with { Recovery = true }
                    : state,
                cancellationToken);

    public async Task OnLocalResultExportCompletedAsync(
        LocalResultExportKey exportKey,
        CancellationToken cancellationToken = default)
    {
        AcceptanceWriteLeaseIdentity[] matches;
        lock (sync)
        {
            matches = consumedLeaseOperations.Keys.Where(key =>
                key.BatchId == exportKey.OwnerId && MatchesExportEvidence(key, exportKey)).ToArray();
        }
        foreach (var key in matches)
        {
            var request = new AdapterWriteAuthorizationRequest(
                key.Capability, key.AccountId, key.OperationId, key.Generation, key.BatchId);
            await RecordEvidenceAsync(
                request,
                Hash($"{exportKey.OwnerKind}|{exportKey.OwnerId:D}|{exportKey.ExportKind}|{exportKey.Generation}"),
                static (state, _) => state with { LocalExport = true },
                cancellationToken).ConfigureAwait(false);
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
            if (next is { Remote: true, Recovery: true, LocalExport: true } &&
                !completedEvidence.ContainsKey(key))
            {
                finalHash = next.CombinedFingerprint;
                completedEvidence[key] = finalHash;
            }
        }
        if (finalHash is not null && presenter is not null)
            await presenter.EnableFormalAcceptanceAsync(request, finalHash, cancellationToken).ConfigureAwait(false);
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
        string CombinedFingerprint = "");
}
