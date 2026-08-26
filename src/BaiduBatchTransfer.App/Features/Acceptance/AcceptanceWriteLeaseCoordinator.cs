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
    IAcceptanceNavigationAuthorizationSource, IBaiduCandidateActionAuthorizationSource
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
    private readonly HashSet<AcceptanceWriteLeaseIdentity> createdBindingIdentities = [];
    private readonly Dictionary<AdapterWriteAuthorizationBinding, BindingState> bindingStates = [];

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

    public async ValueTask<AdapterWriteAuthorizationBinding?> TryCreateBindingAsync(
        AdapterWriteAuthorizationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Capability);
        var bootstrapAction = BootstrapAction(request.Capability.AdapterKind);
        if (bootstrapAction is null || request.Action != bootstrapAction || !HasExactOperationIdentity(request) ||
            request.BatchId == Guid.Empty)
            return null;

        cancellationToken.ThrowIfCancellationRequested();
        var now = timeProvider.GetUtcNow();
        var accepted = await GetFreshFormalAcceptanceAsync(request, now, cancellationToken)
            .ConfigureAwait(false);
        if (accepted is not null)
        {
            var expires = accepted.AcceptedAt + FormalEvidenceLifetime;
            RegisterNavigationScope(request, expires);
            var binding = new AdapterWriteAuthorizationBinding(
                AdapterWriteAuthorizationSource.ExactFormalRecord,
                request.Capability,
                request.AccountId,
                request.OperationId,
                request.Generation,
                request.BatchId,
                accepted.AcceptedAt,
                expires,
                request.IsRecovery,
                accepted.EvidenceHash);
            return TryRegisterBinding(binding, bootstrapAction.Value) ? binding : null;
        }

        if (!options.IsEnabled || request.IsRecovery) return null;
        if (!TryConsume(request, now))
        {
            if (presenter is null ||
                !await presenter.PresentAsync(request, cancellationToken).ConfigureAwait(false) ||
                !TryCreateLease(request, new(true, true, true)) ||
                !TryConsume(request, timeProvider.GetUtcNow()))
                return null;
        }

        var key = AcceptanceWriteLeaseIdentity.From(request);
        lock (sync)
        {
            if (!operationAuthorizationExpiries.TryGetValue(key, out var expires) ||
                expires <= timeProvider.GetUtcNow())
                return null;
            var binding = new AdapterWriteAuthorizationBinding(
                AdapterWriteAuthorizationSource.ConsumedLease,
                request.Capability,
                request.AccountId,
                request.OperationId,
                request.Generation,
                request.BatchId,
                now,
                expires,
                request.IsRecovery,
                null);
            return TryRegisterBindingLocked(binding, bootstrapAction.Value) ? binding : null;
        }
    }

    public async ValueTask<bool> TryValidateBindingAsync(
        AdapterWriteAuthorizationRequest request,
        AdapterWriteAuthorizationBinding binding,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(binding);
        cancellationToken.ThrowIfCancellationRequested();
        var now = timeProvider.GetUtcNow();
        if (!IsBindingStageAction(request.Capability.AdapterKind, request.Action) ||
            !BindingMatches(request, binding) || binding.ExpiresAtUtc <= now)
            return false;

        if (binding.Source == AdapterWriteAuthorizationSource.ConsumedLease)
        {
            if (!options.IsEnabled || request.IsRecovery || binding.IsRecovery ||
                binding.FormalEvidenceFingerprint is not null)
                return false;
            var key = AcceptanceWriteLeaseIdentity.From(request);
            lock (sync)
            {
                if (!operationAuthorizationExpiries.TryGetValue(key, out var expires) ||
                    expires != binding.ExpiresAtUtc || expires <= now ||
                    !consumedLeaseOperations.TryGetValue(key, out var actions) ||
                    !actions.Contains(BootstrapAction(request.Capability.AdapterKind)!.Value) ||
                    !TryAdvanceBindingLocked(binding, request.Action))
                    return false;
                actions.Add(request.Action);
                return true;
            }
        }

        if (binding.Source != AdapterWriteAuthorizationSource.ExactFormalRecord ||
            string.IsNullOrWhiteSpace(binding.FormalEvidenceFingerprint))
            return false;
        var accepted = await GetFreshFormalAcceptanceAsync(request, now, cancellationToken)
            .ConfigureAwait(false);
        if (accepted is null || accepted.AcceptedAt != binding.IssuedAtUtc ||
            accepted.AcceptedAt + FormalEvidenceLifetime != binding.ExpiresAtUtc ||
            !string.Equals(
                accepted.EvidenceHash,
                binding.FormalEvidenceFingerprint,
                StringComparison.Ordinal))
            return false;
        lock (sync) return TryAdvanceBindingLocked(binding, request.Action);
    }

    public async ValueTask ReleaseBindingAsync(
        AdapterWriteAuthorizationBinding binding,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        Task? drained = null;
        lock (sync)
        {
            if (!bindingStates.TryGetValue(binding, out var state)) return;
            state.IsRevoking = true;
            if (state.ActiveMutationPermits == 0)
                bindingStates.Remove(binding);
            else
                drained = (state.Drained ??= new(
                    TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
        if (drained is not null) await drained.ConfigureAwait(false);
    }

    private bool TryRegisterBinding(
        AdapterWriteAuthorizationBinding binding,
        AdapterWriteAction bootstrapAction)
    {
        lock (sync) return TryRegisterBindingLocked(binding, bootstrapAction);
    }

    private bool TryRegisterBindingLocked(
        AdapterWriteAuthorizationBinding binding,
        AdapterWriteAction bootstrapAction)
    {
        var identity = new AcceptanceWriteLeaseIdentity(
            binding.Capability,
            binding.AccountId,
            binding.OperationId,
            binding.Generation,
            binding.BatchId);
        if (!createdBindingIdentities.Add(identity)) return false;
        bindingStates.Add(binding, new BindingState(bootstrapAction));
        return true;
    }

    private bool TryAdvanceBindingLocked(
        AdapterWriteAuthorizationBinding binding,
        AdapterWriteAction requestedAction)
    {
        if (!bindingStates.TryGetValue(binding, out var state) || state.IsRevoking ||
            state.ActiveMutationPermits != 0)
            return false;
        var expected = state.Stage switch
        {
            AdapterWriteAction.EnsureDirectory => AdapterWriteAction.PrepareTransfer,
            AdapterWriteAction.PrepareTransfer => AdapterWriteAction.SubmitTransfer,
            AdapterWriteAction.PrepareShare => AdapterWriteAction.SubmitShare,
            _ => (AdapterWriteAction?)null,
        };
        if (expected != requestedAction) return false;
        state.Stage = requestedAction;
        return true;
    }

    private static AdapterWriteAction? BootstrapAction(string adapterKind) => adapterKind switch
    {
        "BaiduTransfer" => AdapterWriteAction.EnsureDirectory,
        "BaiduShare" => AdapterWriteAction.PrepareShare,
        _ => null,
    };

    private static bool IsBindingStageAction(string adapterKind, AdapterWriteAction action) =>
        adapterKind switch
        {
            "BaiduTransfer" => action is AdapterWriteAction.PrepareTransfer or AdapterWriteAction.SubmitTransfer,
            "BaiduShare" => action == AdapterWriteAction.SubmitShare,
            _ => false,
        };

    private async Task<AdapterCapabilityAcceptanceRecord?> GetFreshFormalAcceptanceAsync(
        AdapterWriteAuthorizationRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            var accepted = await store.GetAsync(
                request.Capability.AdapterKind,
                request.Capability.ProtocolVersion,
                request.Capability.SelectorVersion,
                request.Capability.BuildVersion,
                cancellationToken).ConfigureAwait(false);
            return accepted is not null && accepted.AcceptedAt <= now &&
                   now - accepted.AcceptedAt <= FormalEvidenceLifetime
                ? accepted
                : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static bool BindingMatches(
        AdapterWriteAuthorizationRequest request,
        AdapterWriteAuthorizationBinding binding) =>
        Equals(request.Capability, binding.Capability) &&
        request.AccountId == binding.AccountId &&
        request.OperationId == binding.OperationId &&
        request.Generation == binding.Generation &&
        request.BatchId == binding.BatchId &&
        request.IsRecovery == binding.IsRecovery &&
        binding.IssuedAtUtc < binding.ExpiresAtUtc;

    public async ValueTask<bool> IsAuthorizedAsync(
        BaiduCandidateDomRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var authorization = request.Authorization;
        var binding = request.Binding;
        var now = timeProvider.GetUtcNow();
        if (!IsCandidateActionAllowed(authorization.Capability.AdapterKind, request.Action) ||
            !BindingMatches(authorization, binding) || binding.ExpiresAtUtc <= now)
            return false;

        if (binding.Source == AdapterWriteAuthorizationSource.ConsumedLease)
        {
            if (!options.IsEnabled || authorization.IsRecovery || binding.IsRecovery ||
                binding.FormalEvidenceFingerprint is not null)
                return false;
            var key = AcceptanceWriteLeaseIdentity.From(authorization);
            lock (sync)
            {
                return operationAuthorizationExpiries.TryGetValue(key, out var expires) &&
                       expires == binding.ExpiresAtUtc && expires > now &&
                       consumedLeaseOperations.TryGetValue(key, out var actions) &&
                       actions.Contains(authorization.Capability.AdapterKind == "BaiduShare"
                           ? AdapterWriteAction.PrepareShare
                           : AdapterWriteAction.EnsureDirectory) &&
                       IsCandidateActionAllowedForActiveStageLocked(binding, request.Action);
            }
        }

        if (binding.Source != AdapterWriteAuthorizationSource.ExactFormalRecord ||
            string.IsNullOrWhiteSpace(binding.FormalEvidenceFingerprint))
            return false;
        var accepted = await GetFreshFormalAcceptanceAsync(authorization, now, cancellationToken)
            .ConfigureAwait(false);
        if (accepted is null || accepted.AcceptedAt != binding.IssuedAtUtc ||
            accepted.AcceptedAt + FormalEvidenceLifetime != binding.ExpiresAtUtc ||
            !string.Equals(
                accepted.EvidenceHash,
                binding.FormalEvidenceFingerprint,
                StringComparison.Ordinal))
            return false;
        lock (sync) return IsCandidateActionAllowedForActiveStageLocked(binding, request.Action);
    }

    public async ValueTask<IBaiduCandidateMutationPermit?> TryAcquireMutationPermitAsync(
        BaiduCandidateDomRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var authorization = request.Authorization;
        var binding = request.Binding;
        var now = timeProvider.GetUtcNow();
        if (!IsCandidateActionAllowed(authorization.Capability.AdapterKind, request.Action) ||
            !BindingMatches(authorization, binding) || binding.ExpiresAtUtc <= now)
            return null;

        if (binding.Source == AdapterWriteAuthorizationSource.ConsumedLease)
        {
            if (!options.IsEnabled || authorization.IsRecovery || binding.IsRecovery ||
                binding.FormalEvidenceFingerprint is not null)
                return null;
            var key = AcceptanceWriteLeaseIdentity.From(authorization);
            lock (sync)
            {
                now = timeProvider.GetUtcNow();
                if (!operationAuthorizationExpiries.TryGetValue(key, out var expires) ||
                    expires != binding.ExpiresAtUtc || expires <= now ||
                    !consumedLeaseOperations.TryGetValue(key, out var actions) ||
                    !actions.Contains(BootstrapAction(authorization.Capability.AdapterKind)!.Value))
                    return null;
                return TryAcquireMutationPermitLocked(binding, request.Action);
            }
        }

        if (binding.Source != AdapterWriteAuthorizationSource.ExactFormalRecord ||
            string.IsNullOrWhiteSpace(binding.FormalEvidenceFingerprint))
            return null;
        var accepted = await GetFreshFormalAcceptanceAsync(authorization, now, cancellationToken)
            .ConfigureAwait(false);
        if (accepted is null || accepted.AcceptedAt != binding.IssuedAtUtc ||
            accepted.AcceptedAt + FormalEvidenceLifetime != binding.ExpiresAtUtc ||
            !string.Equals(
                accepted.EvidenceHash,
                binding.FormalEvidenceFingerprint,
                StringComparison.Ordinal))
            return null;
        lock (sync)
        {
            if (binding.ExpiresAtUtc <= timeProvider.GetUtcNow()) return null;
            return TryAcquireMutationPermitLocked(binding, request.Action);
        }
    }

    public async ValueTask<IBaiduCandidateMutationPermit?> TryAcquireTransferMutationPermitAsync(
        BaiduCandidateDomMutationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var authorization = request.Authorization;
        var binding = request.Binding;
        var now = timeProvider.GetUtcNow();
        if (authorization.Capability.AdapterKind != "BaiduTransfer" ||
            authorization.IsRecovery || binding.IsRecovery ||
            !IsTransferV2ActionAllowed(request.Action) ||
            !BindingMatches(authorization, binding) || binding.ExpiresAtUtc <= now)
            return null;

        if (binding.Source == AdapterWriteAuthorizationSource.ConsumedLease)
        {
            if (!options.IsEnabled || binding.FormalEvidenceFingerprint is not null)
                return null;
            var key = AcceptanceWriteLeaseIdentity.From(authorization);
            lock (sync)
            {
                now = timeProvider.GetUtcNow();
                if (!operationAuthorizationExpiries.TryGetValue(key, out var expires) ||
                    expires != binding.ExpiresAtUtc || expires <= now ||
                    !consumedLeaseOperations.TryGetValue(key, out var actions) ||
                    !actions.Contains(AdapterWriteAction.EnsureDirectory))
                    return null;
                return TryAcquireTransferMutationPermitLocked(binding, request.Action);
            }
        }

        if (binding.Source != AdapterWriteAuthorizationSource.ExactFormalRecord ||
            string.IsNullOrWhiteSpace(binding.FormalEvidenceFingerprint))
            return null;
        var accepted = await GetFreshFormalAcceptanceAsync(authorization, now, cancellationToken)
            .ConfigureAwait(false);
        if (accepted is null || accepted.AcceptedAt != binding.IssuedAtUtc ||
            accepted.AcceptedAt + FormalEvidenceLifetime != binding.ExpiresAtUtc ||
            !string.Equals(accepted.EvidenceHash, binding.FormalEvidenceFingerprint, StringComparison.Ordinal))
            return null;
        lock (sync)
        {
            if (binding.ExpiresAtUtc <= timeProvider.GetUtcNow()) return null;
            return TryAcquireTransferMutationPermitLocked(binding, request.Action);
        }
    }

    private IBaiduCandidateMutationPermit? TryAcquireTransferMutationPermitLocked(
        AdapterWriteAuthorizationBinding binding,
        BaiduAutomationAction action)
    {
        if (!bindingStates.TryGetValue(binding, out var state) || state.IsRevoking ||
            !IsTransferV2ActionAllowedForStage(state.Stage, action))
            return null;
        state.ActiveMutationPermits++;
        return new MutationPermit(this, binding);
    }

    private IBaiduCandidateMutationPermit? TryAcquireMutationPermitLocked(
        AdapterWriteAuthorizationBinding binding,
        BaiduAutomationAction action)
    {
        if (!bindingStates.TryGetValue(binding, out var state) || state.IsRevoking ||
            !IsCandidateActionAllowedForStage(state.Stage, action))
            return null;
        state.ActiveMutationPermits++;
        return new MutationPermit(this, binding);
    }

    private void ReleaseMutationPermit(AdapterWriteAuthorizationBinding binding)
    {
        TaskCompletionSource? drained = null;
        lock (sync)
        {
            if (!bindingStates.TryGetValue(binding, out var state) || state.ActiveMutationPermits < 1)
                return;
            state.ActiveMutationPermits--;
            if (state.ActiveMutationPermits == 0 && state.IsRevoking)
            {
                bindingStates.Remove(binding);
                drained = state.Drained;
            }
        }
        drained?.TrySetResult();
    }

    private bool IsCandidateActionAllowedForActiveStageLocked(
        AdapterWriteAuthorizationBinding binding,
        BaiduAutomationAction action) =>
        bindingStates.TryGetValue(binding, out var state) && !state.IsRevoking &&
        IsCandidateActionAllowedForStage(state.Stage, action);

    private static bool IsCandidateActionAllowedForStage(
        AdapterWriteAction stage,
        BaiduAutomationAction action) => stage switch
        {
            AdapterWriteAction.PrepareTransfer => action is BaiduAutomationAction.SetExtractionCode or
                BaiduAutomationAction.SelectExactItems or BaiduAutomationAction.ChooseDestination,
            AdapterWriteAction.SubmitTransfer => action is BaiduAutomationAction.SelectExactItems or
                BaiduAutomationAction.SetExtractionCode or BaiduAutomationAction.ChooseDestination or
                BaiduAutomationAction.ClickSave,
            AdapterWriteAction.PrepareShare => action is BaiduAutomationAction.SelectExactItems or
                BaiduAutomationAction.ChoosePermanent or BaiduAutomationAction.SetExtractionCode,
            AdapterWriteAction.SubmitShare => action is BaiduAutomationAction.SelectExactItems or
                BaiduAutomationAction.ChoosePermanent or BaiduAutomationAction.SetExtractionCode or
                BaiduAutomationAction.ClickShare or BaiduAutomationAction.CreateLink or
                BaiduAutomationAction.ExportResult,
            _ => false,
        };

    private static bool IsTransferV2ActionAllowedForStage(
        AdapterWriteAction stage,
        BaiduAutomationAction action) => stage switch
        {
            AdapterWriteAction.EnsureDirectory => action is BaiduAutomationAction.SetExtractionCode or
                BaiduAutomationAction.SubmitExtraction or BaiduAutomationAction.OpenDestinationPicker or
                BaiduAutomationAction.EnterDirectory or BaiduAutomationAction.CreateDirectory or
                BaiduAutomationAction.ConfirmDirectoryCreation or
                BaiduAutomationAction.CancelDestinationPicker,
            AdapterWriteAction.PrepareTransfer => false,
            AdapterWriteAction.SubmitTransfer => action is BaiduAutomationAction.SetExtractionCode or
                BaiduAutomationAction.SubmitExtraction or BaiduAutomationAction.SelectExactItems or
                BaiduAutomationAction.OpenDestinationPicker or BaiduAutomationAction.EnterDirectory or
                BaiduAutomationAction.ClickConfirmTransfer,
            _ => false,
        };

    private static bool IsTransferV2ActionAllowed(BaiduAutomationAction action) =>
        action is BaiduAutomationAction.SetExtractionCode or BaiduAutomationAction.SubmitExtraction or
            BaiduAutomationAction.SelectExactItems or BaiduAutomationAction.OpenDestinationPicker or
            BaiduAutomationAction.EnterDirectory or BaiduAutomationAction.CreateDirectory or
            BaiduAutomationAction.ConfirmDirectoryCreation or
            BaiduAutomationAction.CancelDestinationPicker or BaiduAutomationAction.ClickConfirmTransfer;

    private static bool IsCandidateActionAllowed(string adapterKind, BaiduAutomationAction action) =>
        adapterKind switch
        {
            "BaiduTransfer" => action is BaiduAutomationAction.SetExtractionCode or
                BaiduAutomationAction.SubmitExtraction or BaiduAutomationAction.SelectExactItems or
                BaiduAutomationAction.OpenDestinationPicker or BaiduAutomationAction.EnterDirectory or
                BaiduAutomationAction.CreateDirectory or BaiduAutomationAction.ConfirmDirectoryCreation or
                BaiduAutomationAction.CancelDestinationPicker or BaiduAutomationAction.ClickConfirmTransfer or
                BaiduAutomationAction.ClickSave or BaiduAutomationAction.ChooseDestination,
            "BaiduShare" => action is BaiduAutomationAction.SelectExactItems or
                BaiduAutomationAction.ClickShare or BaiduAutomationAction.ChoosePermanent or
                BaiduAutomationAction.SetExtractionCode or BaiduAutomationAction.CreateLink or
                BaiduAutomationAction.ExportResult,
            _ => false,
        };

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

    private sealed class BindingState(AdapterWriteAction stage)
    {
        public AdapterWriteAction Stage { get; set; } = stage;
        public bool IsRevoking { get; set; }
        public int ActiveMutationPermits { get; set; }
        public TaskCompletionSource? Drained { get; set; }
    }

    private sealed class MutationPermit(
        AcceptanceWriteLeaseCoordinator owner,
        AdapterWriteAuthorizationBinding binding) : IBaiduCandidateMutationPermit
    {
        private int disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
                owner.ReleaseMutationPermit(binding);
            return ValueTask.CompletedTask;
        }
    }

    private sealed record EvidenceState(
        bool Remote = false,
        bool Recovery = false,
        bool LocalExport = false,
        string? RemoteFingerprint = null,
        bool RemoteMismatch = false,
        string CombinedFingerprint = "");
}
