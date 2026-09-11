using System.Collections.Concurrent;
using BaiduBatchTransfer.App.Features.BrowserAutomation;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.BaiduAdapter.WebAutomation;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.App.Features.ManagedBrowser;

internal sealed record ManagedTransferPendingRegistration(
    ManagedTransferQuarantineKey Key,
    Func<ManagedTransferQuarantineRegistry, bool> Register)
{
    internal bool TryRegister(ManagedTransferQuarantineRegistry registry) =>
        (Register ?? throw new InvalidOperationException("A quarantine registration is required."))(registry);
}

/// <summary>
/// Process-free composition root for operation runtimes. Work begins only after an exact operation calls
/// <see cref="BeginOperationAsync"/>; live identity is proven before a helper connection is created.
/// </summary>
internal sealed class ManagedChromiumTransferRuntimeFactory
{
    private readonly Func<TransferOperationAutomationContext, ManagedBrowserSessionReference,
        CancellationToken, Task<ManagedChromiumExactRuntimeBinding?>> resolveExact;
    private readonly ManagedBrowserLifecycleCoordinator lifecycle;
    private readonly ManagedChromiumProcessManager manager;
    private readonly ManagedBrowserHelperClientFactory helpers;
    private readonly IAdapterWriteAuthorizationGate writeAuthorization;
    private readonly IBaiduCandidateActionAuthorizationSource mutationAuthorization;
    private readonly IWebAutomationAttemptStore attempts;
    private readonly ITransferRecoveryAuthorizationSource recoveryAuthorization;
    private readonly ManagedTransferShareHandoffRegistry shareHandoffs;
    private readonly AdapterCapabilityVersion capability;
    private readonly int timeoutMilliseconds;
    private readonly TimeProvider timeProvider;

    internal ManagedChromiumTransferRuntimeFactory(
        ManagedBrowserLifecycleCoordinator lifecycle,
        ManagedChromiumProcessManager manager,
        ManagedBrowserHelperClientFactory helpers,
        IAdapterWriteAuthorizationGate writeAuthorization,
        IBaiduCandidateActionAuthorizationSource mutationAuthorization,
        IWebAutomationAttemptStore attempts,
        ITransferRecoveryAuthorizationSource recoveryAuthorization,
        ManagedTransferShareHandoffRegistry shareHandoffs,
        string applicationBuildVersion,
        int timeoutMilliseconds = ManagedBrowserProtocol.MaximumOperationTimeoutMilliseconds,
        TimeProvider? timeProvider = null,
        Func<TransferOperationAutomationContext, ManagedBrowserSessionReference,
            CancellationToken, Task<ManagedChromiumExactRuntimeBinding?>>? exactBindingResolver = null)
    {
        this.lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        this.manager = manager ?? throw new ArgumentNullException(nameof(manager));
        this.helpers = helpers ?? throw new ArgumentNullException(nameof(helpers));
        this.writeAuthorization = writeAuthorization ??
            throw new ArgumentNullException(nameof(writeAuthorization));
        this.mutationAuthorization = mutationAuthorization ??
            throw new ArgumentNullException(nameof(mutationAuthorization));
        this.attempts = attempts ?? throw new ArgumentNullException(nameof(attempts));
        this.recoveryAuthorization = recoveryAuthorization ??
            throw new ArgumentNullException(nameof(recoveryAuthorization));
        this.shareHandoffs = shareHandoffs ?? throw new ArgumentNullException(nameof(shareHandoffs));
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationBuildVersion);
        capability = new AdapterCapabilityVersion("BaiduTransfer",
            BaiduSelectorCatalog.TransferProtocolVersion,
            BaiduSelectorCatalog.TransferSelectorVersion,
            applicationBuildVersion.Trim());
        if (timeoutMilliseconds is < 1 or > ManagedBrowserProtocol.MaximumOperationTimeoutMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
        this.timeoutMilliseconds = timeoutMilliseconds;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        resolveExact = exactBindingResolver ?? ResolveExactAsync;
    }

    internal async Task<IManagedChromiumTransferOperation> BeginOperationAsync(
        TransferOperationAutomationContext context,
        ManagedBrowserSessionReference exactSession,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(exactSession);
        context.Validate();

        var exact = await resolveExact(context, exactSession, cancellationToken).ConfigureAwait(false);
        if (exact is null) return ManagedChromiumDeniedTransferOperation.Instance;

        ManagedBrowserOperationHelper? helper = null;
        try
        {
            helper = await helpers.CreateAsync(context.OperationId, cancellationToken)
                .ConfigureAwait(false);
            return new ManagedChromiumProductionTransferOperation(
                context, exactSession, exact.Lifecycle, exact.Handle, helper, lifecycle, manager,
                writeAuthorization, mutationAuthorization, attempts, recoveryAuthorization,
                shareHandoffs, capability, timeoutMilliseconds, timeProvider);
        }
        catch
        {
            if (helper is not null) await helper.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<ManagedChromiumExactRuntimeBinding?> ResolveExactAsync(
        TransferOperationAutomationContext context, ManagedBrowserSessionReference exactSession,
        CancellationToken cancellationToken)
    {
        ManagedBrowserLifecycleSession? persisted;
        try
        {
            persisted = await lifecycle.LoadPersistedAsync(context.AccountId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException)
        {
            return null;
        }
        if (persisted is null || !SameSession(persisted.Session, exactSession))
            return null;
        var exactHandle = manager.FindExactOwnedTransferHandle(exactSession);
        if (exactHandle is null ||
            !string.Equals(persisted.CanonicalProfilePath, exactHandle.Profile.CanonicalSourcePath,
                StringComparison.OrdinalIgnoreCase))
            return null;

        // Preserve the resolver's exact reference because the final session deliberately requires reference
        // identity between the execution context and lifecycle snapshot.
        var lifecycleSnapshot = new ManagedBrowserLifecycleSession(exactSession,
            persisted.InstallationRevision, persisted.SessionRevision, persisted.CanonicalProfilePath);
        return new(lifecycleSnapshot, exactHandle);
    }

    private static bool SameSession(ManagedBrowserSessionReference left,
        ManagedBrowserSessionReference right) =>
        left.SessionId == right.SessionId && left.InstallationId == right.InstallationId &&
        left.AccountId == right.AccountId && left.BrowserFamily == right.BrowserFamily &&
        string.Equals(left.StableBaiduUserId, right.StableBaiduUserId, StringComparison.Ordinal) &&
        string.Equals(left.ProfileId, right.ProfileId, StringComparison.Ordinal) &&
        string.Equals(left.ProfileOwnershipId, right.ProfileOwnershipId, StringComparison.Ordinal);
}

internal sealed record ManagedChromiumExactRuntimeBinding(
    ManagedBrowserLifecycleSession Lifecycle,
    ManagedChromiumSessionHandle Handle);

internal sealed record ManagedTransferShareHandoffKey(
    Guid BatchId, Guid AccountId, string ExactItemsFingerprint)
{
    internal ManagedTransferShareHandoffKey Validate()
    {
        if (BatchId == Guid.Empty) throw new ArgumentException("A batch is required.", nameof(BatchId));
        if (AccountId == Guid.Empty) throw new ArgumentException("An account is required.", nameof(AccountId));
        WebAutomationAttemptRecord.ValidateFingerprint(ExactItemsFingerprint,
            nameof(ExactItemsFingerprint));
        return this;
    }
}

internal sealed record ManagedTransferShareHandoff(
    ManagedTransferShareHandoffKey Key,
    Guid TransferOperationId,
    int TransferGeneration,
    ManagedBrowserSessionReference Session,
    ManagedBrowserLifecycleSession Lifecycle,
    ManagedChromiumSessionHandle ExactHandle,
    RemoteDirectory Destination,
    RemoteItemIdentitySet ExactItems)
{
    internal ManagedTransferShareHandoff Validate()
    {
        Key.Validate();
        if (TransferOperationId == Guid.Empty)
            throw new ArgumentException("A transfer operation is required.", nameof(TransferOperationId));
        if (TransferGeneration < 1) throw new ArgumentOutOfRangeException(nameof(TransferGeneration));
        ArgumentNullException.ThrowIfNull(Session);
        ArgumentNullException.ThrowIfNull(Lifecycle);
        ArgumentNullException.ThrowIfNull(ExactHandle);
        ArgumentNullException.ThrowIfNull(Destination);
        ArgumentNullException.ThrowIfNull(ExactItems);
        if (Key.AccountId != Session.AccountId ||
            !ReferenceEquals(Lifecycle.Session, Session) ||
            ExactHandle.Profile.AccountId != Session.AccountId ||
            ExactHandle.Installation.InstallationId != Session.InstallationId ||
            !string.Equals(Key.ExactItemsFingerprint, ExactItems.Fingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("The managed transfer/share handoff is not exact.");
        return this;
    }
}

/// <summary>One-shot process-local exact ownership; it is intentionally empty after restart.</summary>
internal sealed class ManagedTransferShareHandoffRegistry
{
    private readonly object sync = new();
    private readonly Dictionary<ManagedTransferShareHandoffKey, ManagedTransferShareHandoff> entries = [];

    internal bool TryRegister(ManagedTransferShareHandoff handoff)
    {
        ArgumentNullException.ThrowIfNull(handoff);
        handoff.Validate();
        lock (sync)
        {
            if (entries.TryGetValue(handoff.Key, out var current)) return Same(current, handoff);
            entries.Add(handoff.Key, handoff);
            return true;
        }
    }

    internal bool TryConsume(ManagedTransferShareHandoffKey key,
        out ManagedTransferShareHandoff? handoff)
    {
        ArgumentNullException.ThrowIfNull(key);
        key.Validate();
        lock (sync) return entries.Remove(key, out handoff);
    }

    internal bool TryGet(ManagedTransferShareHandoffKey key,
        out ManagedTransferShareHandoff? handoff)
    {
        ArgumentNullException.ThrowIfNull(key);
        key.Validate();
        lock (sync) return entries.TryGetValue(key, out handoff);
    }

    internal bool TryRemove(ManagedTransferShareHandoffKey key,
        ManagedTransferShareHandoff expected)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(expected);
        key.Validate();
        if (expected.Key != key) return false;
        lock (sync)
        {
            if (!entries.TryGetValue(key, out var current) || !ReferenceEquals(current, expected))
                return false;
            return entries.Remove(key);
        }
    }

    internal bool Contains(ManagedTransferShareHandoffKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        key.Validate();
        lock (sync) return entries.ContainsKey(key);
    }

    private static bool Same(ManagedTransferShareHandoff left,
        ManagedTransferShareHandoff right) =>
        left.TransferOperationId == right.TransferOperationId &&
        left.TransferGeneration == right.TransferGeneration &&
        ReferenceEquals(left.Session, right.Session) &&
        ReferenceEquals(left.Lifecycle, right.Lifecycle) &&
        ReferenceEquals(left.ExactHandle, right.ExactHandle) &&
        left.Destination == right.Destination && left.ExactItems == right.ExactItems;
}

internal sealed class ManagedChromiumProductionTransferOperation : IManagedChromiumTransferOperation
{
    private readonly TransferOperationAutomationContext context;
    private readonly ManagedBrowserSessionReference session;
    private readonly ManagedBrowserLifecycleSession lifecycleSnapshot;
    private readonly ManagedChromiumSessionHandle exactHandle;
    private readonly ManagedBrowserOperationHelper helper;
    private readonly ManagedBrowserLifecycleCoordinator lifecycle;
    private readonly ManagedChromiumProcessManager manager;
    private readonly IAdapterWriteAuthorizationGate writeAuthorization;
    private readonly IBaiduCandidateActionAuthorizationSource mutationAuthorization;
    private readonly IWebAutomationAttemptStore attempts;
    private readonly ITransferRecoveryAuthorizationSource recoveryAuthorization;
    private readonly ManagedTransferShareHandoffRegistry shareHandoffs;
    private readonly AdapterCapabilityVersion capability;
    private readonly int timeoutMilliseconds;
    private readonly TimeProvider timeProvider;
    private readonly WebAutomationRequestIdentity identity;
    private readonly BaiduExecutionContext executionContext;
    private readonly List<AdapterWriteAuthorizationBinding> bindings = [];
    private ManagedPreparedDestinationEvidence? preparedDestination;
    private ManagedBrowserSourceSnapshot? immutableSource;
    private ManagedTransferFinalSubmissionRequest? finalRequest;
    private int disposed;

    internal ManagedChromiumProductionTransferOperation(
        TransferOperationAutomationContext context,
        ManagedBrowserSessionReference session,
        ManagedBrowserLifecycleSession lifecycleSnapshot,
        ManagedChromiumSessionHandle exactHandle,
        ManagedBrowserOperationHelper helper,
        ManagedBrowserLifecycleCoordinator lifecycle,
        ManagedChromiumProcessManager manager,
        IAdapterWriteAuthorizationGate writeAuthorization,
        IBaiduCandidateActionAuthorizationSource mutationAuthorization,
        IWebAutomationAttemptStore attempts,
        ITransferRecoveryAuthorizationSource recoveryAuthorization,
        ManagedTransferShareHandoffRegistry shareHandoffs,
        AdapterCapabilityVersion capability,
        int timeoutMilliseconds,
        TimeProvider timeProvider)
    {
        this.context = context;
        this.session = session;
        this.lifecycleSnapshot = lifecycleSnapshot;
        this.exactHandle = exactHandle;
        this.helper = helper;
        this.lifecycle = lifecycle;
        this.manager = manager;
        this.writeAuthorization = writeAuthorization;
        this.mutationAuthorization = mutationAuthorization;
        this.attempts = attempts;
        this.recoveryAuthorization = recoveryAuthorization;
        this.shareHandoffs = shareHandoffs;
        this.capability = capability;
        this.timeoutMilliseconds = timeoutMilliseconds;
        this.timeProvider = timeProvider;
        identity = new WebAutomationRequestIdentity(context.OperationId, context.BatchId,
            context.AccountId, context.Generation, WebAutomationOwnerKind.Transfer,
            context.ExecutionProfile).Validate();
        executionContext = BaiduExecutionContext.Managed(session);
    }

    public async Task<ManagedChromiumTransferPhaseResult<TransferDirectoryPreparationOutcome>> EnsureAsync(
        RemotePath path, CancellationToken cancellationToken)
    {
        if (!await EnsureAttemptAsync(cancellationToken).ConfigureAwait(false))
            return Definite<TransferDirectoryPreparationOutcome>(BaiduErrorKind.NotValidated);
        var candidate = await CandidateAsync(AdapterWriteAction.EnsureDirectory, cancellationToken)
            .ConfigureAwait(false);
        if (candidate is null)
            return Definite<TransferDirectoryPreparationOutcome>(BaiduErrorKind.NotValidated);

        var runner = new ManagedTransferDirectoryPreparationRunner(lifecycle, manager,
            helper.SendAsync, mutationAuthorization, timeProvider);
        var request = new TransferDirectoryPreparationRequest(context.OperationId, context.BatchId,
            context.AccountId, context.Generation, context.SourceShare, path, capability);
        var evidenceStore = new TransferDirectoryPreparationEvidenceStore(
            attempts, recoveryAuthorization);
        var result = await runner.RunAsync(identity, candidate, executionContext, exactHandle,
            context.SourceShare.OriginalUrl, timeoutMilliseconds, evidenceStore, request,
            cancellationToken).ConfigureAwait(false);
        if (result.Outcome.Status == TransferDirectoryPreparationStatus.Prepared)
        {
            preparedDestination = result.PreparedDestination ??
                throw new InvalidOperationException("Prepared destination evidence is missing.");
            immutableSource = result.ValidatedSourceSnapshot ??
                throw new InvalidOperationException("Validated source evidence is missing.");
        }

        ManagedTransferPendingRegistration? pending = null;
        if (result.Quarantine is { } quarantine)
        {
            var key = await KeyAsync(ManagedTransferQuarantineStage.DirectoryPreparation,
                cancellationToken).ConfigureAwait(false);
            pending = new(key, registry => registry.TryRegister(key, quarantine));
        }
        return new(BaiduAdapterResult<TransferDirectoryPreparationOutcome>.Success(
            result.Outcome, pending is null ? SubmissionAcceptance.DefinitelyNotAccepted :
                SubmissionAcceptance.Unknown), pending);
    }

    public async Task<ManagedChromiumTransferPhaseResult<TransferPreparationReceipt>> PrepareAsync(
        ShareReference share, RemoteDirectory destination, DuplicatePolicy duplicatePolicy,
        CancellationToken cancellationToken)
    {
        if (preparedDestination is null || immutableSource is null ||
            destination != preparedDestination.Destination || !share.Equals(context.SourceShare))
            return Definite<TransferPreparationReceipt>(BaiduErrorKind.NotValidated);
        var candidate = await CandidateAsync(AdapterWriteAction.EnsureDirectory, cancellationToken)
            .ConfigureAwait(false);
        if (candidate is null) return Definite<TransferPreparationReceipt>(BaiduErrorKind.NotValidated);

        var baseline = await new ManagedTransferTargetBaselineSession(lifecycle, manager,
                helper.SendAsync, new ManagedTransferRuntimeRouteProof(manager, helper.SendAsync),
                timeProvider)
            .RunAsync(identity, candidate, executionContext, exactHandle, session,
                context.SourceShare.OriginalUrl, immutableSource, preparedDestination,
                preparedDestination.OwningAttemptRevision, timeoutMilliseconds, attempts,
                cancellationToken).ConfigureAwait(false);
        if (baseline.Kind == ManagedTransferTargetBaselineResultKind.BaselinePersisted)
            return new(BaiduAdapterResult<TransferPreparationReceipt>.Success(
                TransferPreparationReceipt.Ready, SubmissionAcceptance.DefinitelyNotAccepted));
        if (baseline is { Kind: ManagedTransferTargetBaselineResultKind.RouteQuarantined,
                Quarantine: { } quarantine })
        {
            var key = await KeyAsync(ManagedTransferQuarantineStage.TargetBaseline,
                cancellationToken).ConfigureAwait(false);
            return new(BaiduAdapterResult<TransferPreparationReceipt>.Failure(
                    BaiduErrorKind.UnknownResponse, null,
                    submissionAcceptance: SubmissionAcceptance.Unknown),
                new(key, registry => registry.TryRegister(key, quarantine)));
        }
        return Definite<TransferPreparationReceipt>(MapBaselineError(baseline.Kind));
    }

    public async Task<ManagedChromiumTransferPhaseResult<TransferReceipt>> SaveAsync(
        ShareReference share, RemoteDirectory destination, DuplicatePolicy duplicatePolicy,
        CancellationToken cancellationToken)
    {
        if (preparedDestination is null || immutableSource is null ||
            destination != preparedDestination.Destination || !share.Equals(context.SourceShare))
            return Definite<TransferReceipt>(BaiduErrorKind.NotValidated);
        var candidate = await CandidateAsync(AdapterWriteAction.SubmitTransfer, cancellationToken)
            .ConfigureAwait(false);
        if (candidate is null) return Definite<TransferReceipt>(BaiduErrorKind.NotValidated);
        var attempt = await attempts.GetAsync(context.OperationId, context.AccountId,
            context.Generation, cancellationToken).ConfigureAwait(false);
        if (attempt is null) return Definite<TransferReceipt>(BaiduErrorKind.NotValidated);

        finalRequest = Request(candidate, duplicatePolicy, attempt.Revision);
        var result = await new ManagedTransferFinalSubmissionSession(timeProvider)
            .RunAsync(finalRequest, cancellationToken).ConfigureAwait(false);
        return await MapFinalAsync(result, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ManagedChromiumTransferPhaseResult<TransferConfirmationResult>> ConfirmAsync(
        TransferConfirmationQuery query, CancellationToken cancellationToken)
    {
        if (finalRequest is null ||
            !string.Equals(query.SourceShareId, context.SourceShare.StableShareId, StringComparison.Ordinal) ||
            query.TargetPath != preparedDestination!.Destination.Path)
            return new(BaiduAdapterResult<TransferConfirmationResult>.Success(
                TransferConfirmationResult.Ambiguous, SubmissionAcceptance.Unknown));
        var recovered = await new ManagedTransferFinalSubmissionSession(timeProvider)
            .RecoverAsync(finalRequest, cancellationToken).ConfigureAwait(false);
        if (recovered.Kind == ManagedTransferFinalSubmissionResultKind.ExactSuccess)
        {
            var exact = new RemoteItemIdentitySet([new RemoteItemIdentity(
                recovered.DestinationExactId!, immutableSource!.Items.Single().ServerName)]);
            RegisterShareHandoff(exact);
            return new(BaiduAdapterResult<TransferConfirmationResult>.Success(
                TransferConfirmationResult.ExactPresent(exact), SubmissionAcceptance.Unknown));
        }
        if (recovered.Kind == ManagedTransferFinalSubmissionResultKind.DefinitelyNotDispatched)
            return new(BaiduAdapterResult<TransferConfirmationResult>.Success(
                TransferConfirmationResult.Absent,
                SubmissionAcceptance.DefinitelyNotAccepted));
        return new(BaiduAdapterResult<TransferConfirmationResult>.Success(
            TransferConfirmationResult.Ambiguous, SubmissionAcceptance.Unknown));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        await helper.DisposeAsync().ConfigureAwait(false);
        foreach (var binding in bindings.Distinct())
        {
            try { await writeAuthorization.ReleaseBindingAsync(binding).ConfigureAwait(false); }
            catch (Exception) { }
        }
    }

    private async Task<bool> EnsureAttemptAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var initial = new WebAutomationAttemptRecord(context.OperationId, context.AccountId,
            context.Generation, WebAutomationOwnerKind.Transfer, WebAutomationAttemptState.Preparing,
            null, null, null, false, 0, now, now,
            executionProfile: context.ExecutionProfile);
        if (await attempts.BeginAsync(initial, cancellationToken).ConfigureAwait(false)) return true;
        var current = await attempts.GetAsync(context.OperationId, context.AccountId,
            context.Generation, cancellationToken).ConfigureAwait(false);
        return current is { OwnerKind: WebAutomationOwnerKind.Transfer,
                State: WebAutomationAttemptState.Preparing } &&
            current.ExecutionProfile.Equals(context.ExecutionProfile);
    }

    private async Task<BaiduCandidateSessionBinding?> CandidateAsync(
        AdapterWriteAction action, CancellationToken cancellationToken)
    {
        var authorization = new AdapterWriteAuthorizationRequest(capability, context.AccountId,
            context.OperationId, context.Generation, context.BatchId, action, context.IsRecovery);
        AdapterWriteAuthorizationBinding? binding = null;
        if (context.AuthorizationBinding is { } existing &&
            await writeAuthorization.TryValidateBindingAsync(authorization, existing,
                cancellationToken).ConfigureAwait(false))
            binding = existing;
        binding ??= await writeAuthorization.TryCreateBindingAsync(authorization, cancellationToken)
            .ConfigureAwait(false);
        if (binding is null) return null;
        bindings.Add(binding);
        return new BaiduCandidateSessionBinding(authorization, binding, session.StableBaiduUserId);
    }

    private ManagedTransferFinalSubmissionRequest Request(BaiduCandidateSessionBinding candidate,
        DuplicatePolicy duplicatePolicy, long expectedRevision)
    {
        var routeProof = new ManagedTransferRuntimeRouteProof(manager, helper.SendAsync);
        var boundaries = new ManagedTransferFinalSubmissionConcreteBoundaries(lifecycle, routeProof,
            new ManagedTransferPickerOpener(helper.SendAsync, mutationAuthorization,
                timeProvider: timeProvider),
            new ManagedTransferDirectoryReader(helper.SendAsync),
            new ManagedTransferDirectoryMutator(lifecycle, manager, helper.SendAsync,
                mutationAuthorization, timeProvider),
            new ManagedTransferSubmissionOperator(helper.SendAsync),
            new ManagedTransferTargetInventoryReader(helper.SendAsync), helper.SendAsync,
            mutationAuthorization, new ManagedTransferOwnedProcessObserver(manager));
        return new ManagedTransferFinalSubmissionRequest(identity, candidate, executionContext,
            exactHandle, lifecycleSnapshot, session, context.SourceShare.OriginalUrl,
            immutableSource!, preparedDestination!, duplicatePolicy, expectedRevision,
            timeoutMilliseconds, attempts, boundaries);
    }

    private async Task<ManagedChromiumTransferPhaseResult<TransferReceipt>> MapFinalAsync(
        ManagedTransferFinalSubmissionResult result, CancellationToken cancellationToken)
    {
        if (result.Kind == ManagedTransferFinalSubmissionResultKind.ExactSuccess)
        {
            var exact = new RemoteItemIdentitySet([new RemoteItemIdentity(result.DestinationExactId!,
                immutableSource!.Items.Single().ServerName)]);
            RegisterShareHandoff(exact);
            return new(BaiduAdapterResult<TransferReceipt>.Success(
                TransferReceipt.WithExactEvidence(timeProvider.GetUtcNow(), null, exact),
                SubmissionAcceptance.Unknown));
        }
        if (result.Kind == ManagedTransferFinalSubmissionResultKind.ResultPending &&
            result.Quarantine is { } quarantine)
        {
            var key = await KeyAsync(ManagedTransferQuarantineStage.FinalSubmission,
                cancellationToken).ConfigureAwait(false);
            return new(BaiduAdapterResult<TransferReceipt>.Failure(BaiduErrorKind.UnknownResponse,
                    null, submissionAcceptance: SubmissionAcceptance.Unknown),
                new(key, registry => registry.TryRegister(key, quarantine)));
        }
        return result.Kind switch
        {
            ManagedTransferFinalSubmissionResultKind.DefinitelyNotDispatched =>
                Definite<TransferReceipt>(BaiduErrorKind.TransientService),
            ManagedTransferFinalSubmissionResultKind.UserActionRequired =>
                Definite<TransferReceipt>(BaiduErrorKind.VerificationRequired),
            ManagedTransferFinalSubmissionResultKind.TimedOut or
                ManagedTransferFinalSubmissionResultKind.Cancelled =>
                Definite<TransferReceipt>(BaiduErrorKind.TransientService),
            _ => Definite<TransferReceipt>(BaiduErrorKind.NotValidated),
        };
    }

    private async Task<ManagedTransferQuarantineKey> KeyAsync(
        ManagedTransferQuarantineStage stage, CancellationToken cancellationToken)
    {
        var attempt = await attempts.GetAsync(context.OperationId, context.AccountId,
            context.Generation, cancellationToken).ConfigureAwait(false);
        return Key(stage, attempt?.Revision ?? 0, attempt?.SubmissionEvidence?.SubmissionId);
    }

    private ManagedTransferQuarantineKey Key(ManagedTransferQuarantineStage stage,
        long revision = 0, Guid? submissionId = null) =>
        new(stage, context.OperationId, context.AccountId, context.Generation,
            session.SessionId, revision, submissionId);

    private void RegisterShareHandoff(RemoteItemIdentitySet exactItems)
    {
        var key = new ManagedTransferShareHandoffKey(
            context.BatchId, context.AccountId, exactItems.Fingerprint);
        var handoff = new ManagedTransferShareHandoff(key, context.OperationId,
            context.Generation, session, lifecycleSnapshot, exactHandle,
            preparedDestination!.Destination, exactItems);
        if (!manager.IsExactOwnedTransferHandle(exactHandle) ||
            !shareHandoffs.TryRegister(handoff))
            throw new InvalidOperationException("The exact transfer/share handoff could not be retained.");
    }

    private static BaiduErrorKind MapBaselineError(ManagedTransferTargetBaselineResultKind kind) =>
        kind switch
        {
            ManagedTransferTargetBaselineResultKind.LoginRequired => BaiduErrorKind.ExpiredLogin,
            ManagedTransferTargetBaselineResultKind.VerificationRequired =>
                BaiduErrorKind.VerificationRequired,
            ManagedTransferTargetBaselineResultKind.BrowserUnavailable or
                ManagedTransferTargetBaselineResultKind.TimedOut or
                ManagedTransferTargetBaselineResultKind.Cancelled => BaiduErrorKind.TransientService,
            _ => BaiduErrorKind.NotValidated,
        };

    private static ManagedChromiumTransferPhaseResult<T> Definite<T>(BaiduErrorKind kind) =>
        new(BaiduAdapterResult<T>.Failure(kind, null,
            submissionAcceptance: SubmissionAcceptance.DefinitelyNotAccepted));
}

internal sealed class ManagedTransferOwnedProcessObserver(ManagedChromiumProcessManager manager)
    : IManagedTransferProcessExitObserver
{
    private readonly ManagedChromiumProcessManager manager = manager ??
        throw new ArgumentNullException(nameof(manager));

    public Task<ManagedTransferProcessExitObservation> ObserveAsync(
        ManagedChromiumSessionHandle exactHandle, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var observed = manager.IsExactOwnedTransferHandle(exactHandle);
        return Task.FromResult(new ManagedTransferProcessExitObservation(
            observed ? ManagedTransferProcessExitObservationKind.Observed :
                ManagedTransferProcessExitObservationKind.NotObserved,
            exactHandle.RootProcessId, exactHandle.BrowserId, exactHandle.TargetId));
    }
}

internal sealed class ManagedChromiumDeniedTransferOperation : IManagedChromiumTransferOperation
{
    internal static ManagedChromiumDeniedTransferOperation Instance { get; } = new();
    private ManagedChromiumDeniedTransferOperation() { }
    public Task<ManagedChromiumTransferPhaseResult<TransferDirectoryPreparationOutcome>> EnsureAsync(
        RemotePath path, CancellationToken cancellationToken) => Denied<TransferDirectoryPreparationOutcome>();
    public Task<ManagedChromiumTransferPhaseResult<TransferPreparationReceipt>> PrepareAsync(
        ShareReference share, RemoteDirectory destination, DuplicatePolicy duplicatePolicy,
        CancellationToken cancellationToken) => Denied<TransferPreparationReceipt>();
    public Task<ManagedChromiumTransferPhaseResult<TransferReceipt>> SaveAsync(
        ShareReference share, RemoteDirectory destination, DuplicatePolicy duplicatePolicy,
        CancellationToken cancellationToken) => Denied<TransferReceipt>();
    public Task<ManagedChromiumTransferPhaseResult<TransferConfirmationResult>> ConfirmAsync(
        TransferConfirmationQuery query, CancellationToken cancellationToken) =>
        Task.FromResult(new ManagedChromiumTransferPhaseResult<TransferConfirmationResult>(
            BaiduAdapterResult<TransferConfirmationResult>.Success(
                TransferConfirmationResult.Ambiguous, SubmissionAcceptance.Unknown)));
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    private static Task<ManagedChromiumTransferPhaseResult<T>> Denied<T>() =>
        Task.FromResult(new ManagedChromiumTransferPhaseResult<T>(BaiduAdapterResult<T>.Failure(
            BaiduErrorKind.NotValidated, null,
            submissionAcceptance: SubmissionAcceptance.DefinitelyNotAccepted)));
}

internal sealed record ManagedChromiumTransferPhaseResult<T>(
    BaiduAdapterResult<T> Result,
    ManagedTransferPendingRegistration? Pending = null)
{
    internal BaiduAdapterResult<T> RequireResult() =>
        Result ?? throw new InvalidOperationException("The managed transfer phase returned no result.");
}

/// <summary>
/// One operation-scoped runtime owns the helper and concrete managed-browser sessions. The adapter owns
/// cross-phase identity, ordering, quarantine-before-mapping and at-most-once final submission.
/// </summary>
internal interface IManagedChromiumTransferOperation : IAsyncDisposable
{
    Task<ManagedChromiumTransferPhaseResult<TransferDirectoryPreparationOutcome>> EnsureAsync(
        RemotePath path, CancellationToken cancellationToken);
    Task<ManagedChromiumTransferPhaseResult<TransferPreparationReceipt>> PrepareAsync(
        ShareReference share, RemoteDirectory destination, DuplicatePolicy duplicatePolicy,
        CancellationToken cancellationToken);
    Task<ManagedChromiumTransferPhaseResult<TransferReceipt>> SaveAsync(
        ShareReference share, RemoteDirectory destination, DuplicatePolicy duplicatePolicy,
        CancellationToken cancellationToken);
    Task<ManagedChromiumTransferPhaseResult<TransferConfirmationResult>> ConfirmAsync(
        TransferConfirmationQuery query, CancellationToken cancellationToken);
}

internal sealed class ManagedChromiumTransferAdapter : IManagedChromiumTransferAdapter, IAsyncDisposable
{
    private readonly Func<TransferOperationAutomationContext, ManagedBrowserSessionReference,
        CancellationToken, Task<IManagedChromiumTransferOperation>> beginOperation;
    private readonly ManagedTransferQuarantineRegistry quarantines;
    private readonly ConcurrentDictionary<Guid, Lazy<Task<State>>> active = new();
    private readonly ConcurrentDictionary<Guid, byte> submissionAttempted = new();
    private int disposed;

    internal ManagedChromiumTransferAdapter(
        Func<TransferOperationAutomationContext, ManagedBrowserSessionReference,
            CancellationToken, Task<IManagedChromiumTransferOperation>> beginOperation,
        ManagedTransferQuarantineRegistry quarantines)
    {
        this.beginOperation = beginOperation ?? throw new ArgumentNullException(nameof(beginOperation));
        this.quarantines = quarantines ?? throw new ArgumentNullException(nameof(quarantines));
    }

    public async Task<BaiduAdapterResult<TransferDirectoryPreparationOutcome>> EnsureDirectoryForOperationAsync(
        TransferOperationAutomationContext context, ManagedBrowserSessionReference session,
        RemotePath path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        var state = await StateAsync(context, session, cancellationToken).ConfigureAwait(false);
        await state.Serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (state.Stage != Stage.Initial) return Denied<TransferDirectoryPreparationOutcome>();
            var phase = await state.Runtime.EnsureAsync(path, cancellationToken).ConfigureAwait(false);
            RegisterPending(state, phase.Pending);
            var result = phase.RequireResult();
            if (result.IsSuccess && result.Value is { Status: TransferDirectoryPreparationStatus.Prepared,
                    Directory: { } destination })
            {
                state.Path = path;
                state.Destination = destination;
                state.Stage = Stage.DirectoryPrepared;
            }
            else if (phase.Pending is null && result.IsSuccess &&
                result.Value?.Status == TransferDirectoryPreparationStatus.NeedsAttention)
                await ReleaseAsync(context.OperationId, state).ConfigureAwait(false);
            return result;
        }
        finally { state.Serial.Release(); }
    }

    public async Task<BaiduAdapterResult<TransferPreparationReceipt>> PrepareWholeShareForOperationAsync(
        TransferOperationAutomationContext context, ManagedBrowserSessionReference session,
        ShareReference share, RemoteDirectory destination, DuplicatePolicy duplicatePolicy,
        CancellationToken cancellationToken)
    {
        ValidateTransferArguments(share, destination, duplicatePolicy);
        var state = await ExistingStateAsync(context, session, cancellationToken).ConfigureAwait(false);
        if (state is null) return Denied<TransferPreparationReceipt>();
        await state.Serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (state.Stage != Stage.DirectoryPrepared || state.Destination != destination ||
                !share.Equals(context.SourceShare))
                return Denied<TransferPreparationReceipt>();
            var phase = await state.Runtime.PrepareAsync(
                share, destination, duplicatePolicy, cancellationToken).ConfigureAwait(false);
            RegisterPending(state, phase.Pending);
            var result = phase.RequireResult();
            if (result.IsSuccess)
            {
                state.Share = share;
                state.Policy = duplicatePolicy;
                state.Stage = Stage.BaselinePrepared;
            }
            else if (phase.Pending is null)
                await ReleaseAsync(context.OperationId, state).ConfigureAwait(false);
            return result;
        }
        finally { state.Serial.Release(); }
    }

    public async Task<BaiduAdapterResult<TransferReceipt>> SaveWholeShareForOperationAsync(
        TransferOperationAutomationContext context, ManagedBrowserSessionReference session,
        ShareReference share, RemoteDirectory destination, DuplicatePolicy duplicatePolicy,
        CancellationToken cancellationToken)
    {
        ValidateTransferArguments(share, destination, duplicatePolicy);
        ValidateIdentity(context, session);
        if (!submissionAttempted.TryAdd(context.OperationId, 0)) return Denied<TransferReceipt>();
        var state = await ExistingStateAsync(context, session, cancellationToken).ConfigureAwait(false);
        if (state is null) return Denied<TransferReceipt>();
        await state.Serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (state.Stage != Stage.BaselinePrepared || state.Destination != destination ||
                !share.Equals(state.Share) || state.Policy != duplicatePolicy)
                return Denied<TransferReceipt>();
            state.Stage = Stage.SubmissionAttempted;
            var phase = await state.Runtime.SaveAsync(
                share, destination, duplicatePolicy, cancellationToken).ConfigureAwait(false);
            RegisterPending(state, phase.Pending);
            var result = phase.RequireResult();
            if (phase.Pending is null)
                await ReleaseAsync(context.OperationId, state).ConfigureAwait(false);
            return result;
        }
        finally { state.Serial.Release(); }
    }

    public async Task<BaiduAdapterResult<TransferConfirmationResult>> ConfirmExactTransferForOperationAsync(
        TransferOperationAutomationContext context, ManagedBrowserSessionReference session,
        TransferConfirmationQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var state = await StateAsync(context, session, cancellationToken).ConfigureAwait(false);
        await state.Serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (state.PendingKey is { } key)
            {
                var reconciled = await quarantines.ReconcileAsync(
                    key, ManagedBrowserProtocol.MaximumOperationTimeoutMilliseconds,
                    cancellationToken).ConfigureAwait(false);
                if (reconciled != ManagedTransferQuarantineReconciliation.Released)
                    return BaiduAdapterResult<TransferConfirmationResult>.Success(
                        TransferConfirmationResult.Ambiguous, SubmissionAcceptance.Unknown);
                state.PendingKey = null;
            }
            var phase = await state.Runtime.ConfirmAsync(query, cancellationToken).ConfigureAwait(false);
            RegisterPending(state, phase.Pending);
            var result = phase.RequireResult();
            if (phase.Pending is null && result.IsSuccess &&
                result.Value!.State != TransferConfirmationState.Ambiguous)
                await ReleaseAsync(context.OperationId, state).ConfigureAwait(false);
            return result;
        }
        finally { state.Serial.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        var states = active.Values.ToArray();
        foreach (var lazy in states)
        {
            if (!lazy.IsValueCreated) continue;
            try { await (await lazy.Value.ConfigureAwait(false)).Runtime.DisposeAsync().ConfigureAwait(false); }
            catch (Exception) { }
        }
        active.Clear();
    }

    private async Task<State> StateAsync(TransferOperationAutomationContext context,
        ManagedBrowserSessionReference session, CancellationToken cancellationToken)
    {
        ValidateIdentity(context, session);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var lazy = active.GetOrAdd(context.OperationId, _ => new Lazy<Task<State>>(
            async () => new State(context, session,
                await beginOperation(context, session, cancellationToken).ConfigureAwait(false) ??
                throw new InvalidOperationException("The managed operation runtime was not created.")),
            LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            var state = await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (!SameContext(state.Context, context) || !SameSession(state.Session, session))
                throw new InvalidOperationException("The managed operation identity changed between phases.");
            return state;
        }
        catch
        {
            active.TryRemove(new KeyValuePair<Guid, Lazy<Task<State>>>(context.OperationId, lazy));
            throw;
        }
    }

    private async Task<State?> ExistingStateAsync(TransferOperationAutomationContext context,
        ManagedBrowserSessionReference session, CancellationToken cancellationToken)
    {
        ValidateIdentity(context, session);
        if (!active.TryGetValue(context.OperationId, out var lazy)) return null;
        var state = await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        return SameContext(state.Context, context) && SameSession(state.Session, session) ? state : null;
    }

    private void RegisterPending(State state, ManagedTransferPendingRegistration? pending)
    {
        if (pending is null) return;
        if (pending.Key.OperationId != state.Context.OperationId ||
            pending.Key.AccountId != state.Context.AccountId ||
            pending.Key.Generation != state.Context.Generation ||
            pending.Key.SessionId != state.Session.SessionId)
            throw new InvalidOperationException("The quarantine identity does not match the managed operation.");
        if (!pending.TryRegister(quarantines) && !quarantines.IsAccountQuarantined(state.Context.AccountId))
            throw new InvalidOperationException("The managed quarantine could not be retained.");
        state.PendingKey = pending.Key;
    }

    private async Task ReleaseAsync(Guid operationId, State state)
    {
        if (active.TryGetValue(operationId, out var lazy) && lazy.IsValueCreated &&
            active.TryRemove(new KeyValuePair<Guid, Lazy<Task<State>>>(operationId, lazy)))
            await state.Runtime.DisposeAsync().ConfigureAwait(false);
    }

    private static void ValidateIdentity(TransferOperationAutomationContext context,
        ManagedBrowserSessionReference session)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(session);
        context.Validate();
        var profile = context.ExecutionProfile;
        if (profile.Engine != BaiduExecutionEngine.ManagedChromiumV1 ||
            context.AccountId != session.AccountId || profile.SessionId != session.SessionId ||
            profile.InstallationId != session.InstallationId || profile.BrowserFamily != session.BrowserFamily ||
            !string.Equals(profile.ProfileId, session.ProfileId, StringComparison.Ordinal))
            throw new InvalidOperationException("The managed session does not match the operation snapshot.");
    }

    private static void ValidateTransferArguments(ShareReference share, RemoteDirectory destination,
        DuplicatePolicy duplicatePolicy)
    {
        ArgumentNullException.ThrowIfNull(share);
        ArgumentNullException.ThrowIfNull(destination);
        if (!Enum.IsDefined(duplicatePolicy)) throw new ArgumentOutOfRangeException(nameof(duplicatePolicy));
    }

    private static bool SameContext(TransferOperationAutomationContext left,
        TransferOperationAutomationContext right) =>
        left.OperationId == right.OperationId && left.BatchId == right.BatchId &&
        left.AccountId == right.AccountId && left.Generation == right.Generation &&
        left.ExecutionProfile == right.ExecutionProfile && left.SourceShare.Equals(right.SourceShare);

    private static bool SameSession(ManagedBrowserSessionReference left,
        ManagedBrowserSessionReference right) =>
        left.SessionId == right.SessionId && left.InstallationId == right.InstallationId &&
        left.AccountId == right.AccountId && left.BrowserFamily == right.BrowserFamily &&
        string.Equals(left.ProfileId, right.ProfileId, StringComparison.Ordinal) &&
        string.Equals(left.ProfileOwnershipId, right.ProfileOwnershipId, StringComparison.Ordinal) &&
        string.Equals(left.StableBaiduUserId, right.StableBaiduUserId, StringComparison.Ordinal);

    private static BaiduAdapterResult<T> Denied<T>() =>
        BaiduAdapterResult<T>.Failure(BaiduErrorKind.NotValidated, null,
            submissionAcceptance: SubmissionAcceptance.DefinitelyNotAccepted);

    private enum Stage { Initial, DirectoryPrepared, BaselinePrepared, SubmissionAttempted }

    private sealed class State(TransferOperationAutomationContext context,
        ManagedBrowserSessionReference session, IManagedChromiumTransferOperation runtime)
    {
        internal TransferOperationAutomationContext Context { get; } = context;
        internal ManagedBrowserSessionReference Session { get; } = session;
        internal IManagedChromiumTransferOperation Runtime { get; } = runtime;
        internal SemaphoreSlim Serial { get; } = new(1, 1);
        internal Stage Stage { get; set; }
        internal RemotePath? Path { get; set; }
        internal RemoteDirectory? Destination { get; set; }
        internal ShareReference? Share { get; set; }
        internal DuplicatePolicy? Policy { get; set; }
        internal ManagedTransferQuarantineKey? PendingKey { get; set; }
    }
}
