using BaiduBatchTransfer.App.Features.BrowserAutomation;
using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.BaiduAdapter.WebAutomation;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.App.Features.ManagedBrowser;

/// <summary>
/// Converts only the one-shot exact transfer handoff into a managed share session. Lookup and
/// ownership checks are read-only; no browser, target, directory, or item is rediscovered here.
/// </summary>
internal sealed class ManagedShareAutomationSessionFactory : IManagedShareAutomationSessionFactory
{
    private readonly ManagedTransferShareHandoffRegistry handoffs;
    private readonly Func<Guid, CancellationToken, Task<ManagedBrowserLifecycleSession?>> loadLifecycle;
    private readonly Func<ManagedBrowserSessionReference, ManagedChromiumSessionHandle?> findHandle;
    private readonly Func<ManagedChromiumSessionHandle, bool> isExactOwnedHandle;
    private readonly Func<Guid, CancellationToken, ValueTask<ManagedBrowserOperationHelper>> createHelper;
    private readonly IWebAutomationAttemptStore attempts;
    private readonly IShareAutomationAcceptanceEvidenceStore acceptances;
    private readonly TimeProvider timeProvider;
    private readonly int timeoutMilliseconds;

    internal ManagedShareAutomationSessionFactory(
        ManagedTransferShareHandoffRegistry handoffs,
        IManagedBrowserLifecycle lifecycle,
        ManagedChromiumProcessManager manager,
        ManagedBrowserHelperClientFactory helpers,
        IWebAutomationAttemptStore attempts,
        IShareAutomationAcceptanceEvidenceStore acceptances,
        int timeoutMilliseconds = ManagedBrowserProtocol.MaximumOperationTimeoutMilliseconds,
        TimeProvider? timeProvider = null)
        : this(
            handoffs,
            LifecycleLoader(lifecycle),
            RequireManager(manager).FindExactOwnedTransferHandle,
            RequireManager(manager).IsExactOwnedTransferHandle,
            HelperCreator(helpers),
            attempts,
            acceptances,
            timeoutMilliseconds,
            timeProvider)
    { }

    internal ManagedShareAutomationSessionFactory(
        ManagedTransferShareHandoffRegistry handoffs,
        Func<Guid, CancellationToken, Task<ManagedBrowserLifecycleSession?>> loadLifecycle,
        Func<ManagedBrowserSessionReference, ManagedChromiumSessionHandle?> findHandle,
        Func<ManagedChromiumSessionHandle, bool> isExactOwnedHandle,
        Func<Guid, CancellationToken, ValueTask<ManagedBrowserOperationHelper>> createHelper,
        IWebAutomationAttemptStore attempts,
        IShareAutomationAcceptanceEvidenceStore acceptances,
        int timeoutMilliseconds = ManagedBrowserProtocol.MaximumOperationTimeoutMilliseconds,
        TimeProvider? timeProvider = null)
    {
        this.handoffs = handoffs ?? throw new ArgumentNullException(nameof(handoffs));
        this.loadLifecycle = loadLifecycle ?? throw new ArgumentNullException(nameof(loadLifecycle));
        this.findHandle = findHandle ?? throw new ArgumentNullException(nameof(findHandle));
        this.isExactOwnedHandle = isExactOwnedHandle ?? throw new ArgumentNullException(nameof(isExactOwnedHandle));
        this.createHelper = createHelper ?? throw new ArgumentNullException(nameof(createHelper));
        this.attempts = attempts ?? throw new ArgumentNullException(nameof(attempts));
        this.acceptances = acceptances ?? throw new ArgumentNullException(nameof(acceptances));
        if (timeoutMilliseconds is < 1 or > ManagedBrowserProtocol.MaximumOperationTimeoutMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
        this.timeoutMilliseconds = timeoutMilliseconds;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<IManagedShareAutomationSessionLease> CreateAsync(
        ShareOperationAutomationContext context,
        ManagedBrowserSessionReference session,
        PermanentShareRequest request,
        bool recoveryOnly,
        WebAutomationBaseline? persistedBaseline,
        string? baselineObservationFingerprint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        context.Validate();
        var key = Key(context, request);
        if (!handoffs.TryGet(key, out var handoff) || handoff is null ||
            !Matches(handoff, context, session, request))
            throw new InvalidOperationException("The exact transfer/share handoff is unavailable.");

        var current = await loadLifecycle(context.AccountId, cancellationToken)
            .ConfigureAwait(false);
        if (current is null || !SameLifecycle(current, handoff.Lifecycle) ||
            !SameSession(current.Session, handoff.Session))
            throw new InvalidOperationException("The managed browser lifecycle snapshot changed.");
        var owned = findHandle(session);
        if (!ReferenceEquals(owned, handoff.ExactHandle) ||
            !isExactOwnedHandle(handoff.ExactHandle))
            throw new InvalidOperationException("The exact owned browser handle is unavailable.");

        ManagedBrowserOperationHelper? helper = null;
        try
        {
            helper = await createHelper(context.OperationId, cancellationToken)
                .ConfigureAwait(false);
            var codeMode = request.UsesRandomExtractionCode
                ? ShareExtractionCodeBehavior.Random
                : ShareExtractionCodeBehavior.Fixed;
            var protocolContext = new ManagedBrowserShareSubmissionContext(
                handoff.ExactHandle.BrowserId,
                handoff.ExactHandle.TargetId,
                handoff.ExactHandle.Endpoint,
                timeoutMilliseconds,
                handoff.Session.StableBaiduUserId,
                handoff.Destination.RemoteId,
                handoff.Destination.Path.Value,
                handoff.ExactItems,
                codeMode,
                request.FixedExtractionCode);
            var automation = new ManagedShareSubmissionSession(
                context.OperationId,
                context.AccountId,
                context.Generation,
                protocolContext,
                new PersistentBoundaries(
                    helper, attempts, acceptances, context, timeProvider),
                recoveryOnly,
                persistedBaseline,
                baselineObservationFingerprint);
            var lease = new SessionLease(helper, automation);
            helper = null;
            return lease;
        }
        catch
        {
            if (helper is not null) await helper.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public void ReleaseExactHandoff(
        ShareOperationAutomationContext context,
        ManagedBrowserSessionReference session,
        PermanentShareRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        var key = Key(context, request);
        if (handoffs.TryGet(key, out var handoff) && handoff is not null &&
            Matches(handoff, context, session, request))
            handoffs.TryRemove(key, handoff);
    }

    private static ManagedTransferShareHandoffKey Key(
        ShareOperationAutomationContext context, PermanentShareRequest request) =>
        new ManagedTransferShareHandoffKey(
            context.BatchId, context.AccountId, request.RemoteItems.Fingerprint).Validate();

    private static Func<Guid, CancellationToken, Task<ManagedBrowserLifecycleSession?>> LifecycleLoader(
        IManagedBrowserLifecycle lifecycle)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        return lifecycle.LoadActiveSessionAsync;
    }

    private static ManagedChromiumProcessManager RequireManager(ManagedChromiumProcessManager manager) =>
        manager ?? throw new ArgumentNullException(nameof(manager));

    private static Func<Guid, CancellationToken, ValueTask<ManagedBrowserOperationHelper>> HelperCreator(
        ManagedBrowserHelperClientFactory helpers)
    {
        ArgumentNullException.ThrowIfNull(helpers);
        return helpers.CreateAsync;
    }

    private static bool Matches(
        ManagedTransferShareHandoff handoff,
        ShareOperationAutomationContext context,
        ManagedBrowserSessionReference session,
        PermanentShareRequest request)
    {
        try { handoff.Validate(); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return false; }
        return context.OwnerKind == WebAutomationOwnerKind.TransferShare &&
            request.RemoteItems == context.ExactRemoteItems &&
            handoff.ExactItems == request.RemoteItems && SameSession(handoff.Session, session) &&
            context.ExecutionProfile.Engine == BaiduExecutionEngine.ManagedChromiumV1 &&
            context.ExecutionProfile.SessionId == session.SessionId &&
            context.ExecutionProfile.InstallationId == session.InstallationId &&
            context.ExecutionProfile.BrowserFamily == session.BrowserFamily &&
            string.Equals(context.ExecutionProfile.ProfileId, session.ProfileId, StringComparison.Ordinal);
    }

    private static bool SameLifecycle(
        ManagedBrowserLifecycleSession left, ManagedBrowserLifecycleSession right) =>
        left.InstallationRevision == right.InstallationRevision &&
        left.SessionRevision == right.SessionRevision &&
        string.Equals(left.CanonicalProfilePath, right.CanonicalProfilePath,
            StringComparison.OrdinalIgnoreCase) && SameSession(left.Session, right.Session);

    private static bool SameSession(
        ManagedBrowserSessionReference left, ManagedBrowserSessionReference right) =>
        left.SessionId == right.SessionId && left.InstallationId == right.InstallationId &&
        left.AccountId == right.AccountId && left.BrowserFamily == right.BrowserFamily &&
        string.Equals(left.StableBaiduUserId, right.StableBaiduUserId, StringComparison.Ordinal) &&
        string.Equals(left.ProfileId, right.ProfileId, StringComparison.Ordinal) &&
        string.Equals(left.ProfileOwnershipId, right.ProfileOwnershipId, StringComparison.Ordinal);

    private sealed class SessionLease(
        ManagedBrowserOperationHelper helper,
        ManagedShareSubmissionSession session) : IManagedShareAutomationSessionLease
    {
        private ManagedBrowserOperationHelper? helper = helper;
        public IShareAutomationSession Session { get; } = session;
        public ValueTask DisposeAsync()
        {
            var owned = Interlocked.Exchange(ref helper, null);
            return owned is null ? ValueTask.CompletedTask : owned.DisposeAsync();
        }
    }

    private sealed class PersistentBoundaries(
        ManagedBrowserOperationHelper helper,
        IWebAutomationAttemptStore attempts,
        IShareAutomationAcceptanceEvidenceStore acceptances,
        ShareOperationAutomationContext context,
        TimeProvider timeProvider) : IManagedShareSubmissionBoundaries
    {
        public Task<ManagedBrowserResponse> SendAsync(
            ManagedBrowserRequest request, CancellationToken cancellationToken) =>
            helper.SendAsync(request, cancellationToken);

        public async Task PersistBaselineAsync(
            WebAutomationBaseline baseline,
            string observationFingerprint,
            CancellationToken cancellationToken)
        {
            var attempt = await ExactAttemptAsync(
                WebAutomationAttemptState.Preparing, cancellationToken).ConfigureAwait(false);
            if (!await attempts.TryPersistBaselineAsync(
                    context.OperationId, context.AccountId, context.Generation, attempt.Revision,
                    baseline, observationFingerprint, timeProvider.GetUtcNow(), cancellationToken)
                .ConfigureAwait(false))
                throw new InvalidOperationException("The exact share baseline could not be persisted.");
        }

        public async Task MarkSubmittingAsync(CancellationToken cancellationToken)
        {
            var attempt = await ExactAttemptAsync(
                WebAutomationAttemptState.BaselinePersisted, cancellationToken).ConfigureAwait(false);
            if (!await attempts.TryAdvanceAsync(
                    context.OperationId, context.AccountId, context.Generation,
                    WebAutomationAttemptState.BaselinePersisted, attempt.Revision,
                    WebAutomationAttemptState.Submitting, attempt.ObservationFingerprint,
                    false, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("The exact share submission boundary could not be persisted.");
        }

        public async Task PersistOfficialAcceptanceAsync(
            ShareOfficialAcceptance acceptance,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(acceptance);
            var attempt = await ExactAttemptAsync(
                WebAutomationAttemptState.Submitting, cancellationToken).ConfigureAwait(false);
            await acceptances.PersistAsync(context, acceptance, cancellationToken).ConfigureAwait(false);
            if (!await attempts.TryPersistOfficialAcceptanceAsync(
                    context.OperationId, context.AccountId, context.Generation, attempt.Revision,
                    acceptance.ExactRemoteItems!, acceptance.ObservationFingerprint,
                    timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("The exact official share acceptance could not be persisted.");
        }

        private async Task<WebAutomationAttemptRecord> ExactAttemptAsync(
            WebAutomationAttemptState expectedState,
            CancellationToken cancellationToken)
        {
            var attempt = await attempts.GetAsync(
                    context.OperationId, context.AccountId, context.Generation, cancellationToken)
                .ConfigureAwait(false);
            if (attempt is null || attempt.OwnerKind != WebAutomationOwnerKind.TransferShare ||
                attempt.State != expectedState || attempt.ExecutionProfile != context.ExecutionProfile)
                throw new InvalidOperationException("The managed share attempt boundary changed.");
            return attempt;
        }
    }
}
