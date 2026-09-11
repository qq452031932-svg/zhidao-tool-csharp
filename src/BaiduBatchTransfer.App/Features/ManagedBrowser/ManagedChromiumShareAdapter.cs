using BaiduBatchTransfer.App.Features.BrowserAutomation;
using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.BaiduAdapter.WebAutomation;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.App.Features.ManagedBrowser;

internal interface IManagedShareAutomationSessionLease : IAsyncDisposable
{
    IShareAutomationSession Session { get; }
}

/// <summary>
/// Resolves only an already-proven operation/session/target tuple. Implementations must fail closed
/// when the exact held browser handle or the transferred target directory evidence is unavailable;
/// they must not discover a replacement target, search by name, or infer a directory.
/// </summary>
internal interface IManagedShareAutomationSessionFactory
{
    ValueTask<IManagedShareAutomationSessionLease> CreateAsync(
        ShareOperationAutomationContext context,
        ManagedBrowserSessionReference session,
        PermanentShareRequest request,
        bool recoveryOnly,
        WebAutomationBaseline? persistedBaseline,
        string? baselineObservationFingerprint,
        CancellationToken cancellationToken);

    void ReleaseExactHandoff(
        ShareOperationAutomationContext context,
        ManagedBrowserSessionReference session,
        PermanentShareRequest request);
}

internal sealed class ManagedChromiumShareAdapter : IManagedChromiumShareAdapter
{
    private readonly IManagedShareAutomationSessionFactory sessions;
    private readonly IWebAutomationAttemptStore attempts;
    private readonly IShareAutomationAcceptanceEvidenceStore acceptances;
    private readonly ShareAutomationFlow flow;
    private readonly TimeProvider timeProvider;

    internal ManagedChromiumShareAdapter(
        IManagedShareAutomationSessionFactory sessions,
        IWebAutomationAttemptStore attempts,
        IShareAutomationAcceptanceEvidenceStore acceptances,
        ShareAutomationFlow flow,
        TimeProvider? timeProvider = null)
    {
        this.sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        this.attempts = attempts ?? throw new ArgumentNullException(nameof(attempts));
        this.acceptances = acceptances ?? throw new ArgumentNullException(nameof(acceptances));
        this.flow = flow ?? throw new ArgumentNullException(nameof(flow));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<BaiduAdapterResult<ShareDiscoverySnapshot>> DiscoverPermanentSharesForOperationAsync(
        ShareOperationAutomationContext context,
        ManagedBrowserSessionReference session,
        PermanentShareRequest request,
        CancellationToken cancellationToken)
    {
        Validate(context, session, request, requireRecovery: false);
        try
        {
            await using var lease = await sessions.CreateAsync(
                context, session, request, recoveryOnly: true, null, null, cancellationToken)
                .ConfigureAwait(false);
            var automation = lease.Session ??
                throw new InvalidOperationException("The managed share session was not created.");
            await automation.SelectExactRemoteItemsAsSingleGroupAsync(request.RemoteItems, cancellationToken)
                .ConfigureAwait(false);
            await automation.ConfigurePermanentShareAsync(request, cancellationToken).ConfigureAwait(false);
            var listing = await automation.ReadCompleteStableOfficialShareListAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!listing.IsComplete || !listing.IsStable)
                return Failure<ShareDiscoverySnapshot>(SubmissionAcceptance.DefinitelyNotAccepted);
            var all = listing.Shares.Select(ToShareRecord).ToArray();
            var exact = all.Where(item => item.RemoteItems == request.RemoteItems).ToArray();
            return BaiduAdapterResult<ShareDiscoverySnapshot>.Success(
                new ShareDiscoverySnapshot(WebAutomationBaseline.Create(all.Select(item => item.ShareId)), exact, all),
                SubmissionAcceptance.DefinitelyNotAccepted);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return Failure<ShareDiscoverySnapshot>(SubmissionAcceptance.DefinitelyNotAccepted); }
    }

    public async Task<BaiduAdapterResult<SharePreparationReceipt>> PreparePermanentShareForOperationAsync(
        ShareOperationAutomationContext context,
        ManagedBrowserSessionReference session,
        PermanentShareRequest request,
        CancellationToken cancellationToken)
    {
        Validate(context, session, request, requireRecovery: false);
        try
        {
            var attempt = await LoadAsync(context, cancellationToken).ConfigureAwait(false);
            if (attempt is null)
            {
                var created = WebAutomationAttemptRecord.Create(
                    context.OperationId, context.AccountId, context.Generation,
                    WebAutomationOwnerKind.TransferShare, timeProvider.GetUtcNow(), context.ExecutionProfile);
                if (!await attempts.BeginAsync(created, cancellationToken).ConfigureAwait(false))
                    return Failure<SharePreparationReceipt>(SubmissionAcceptance.DefinitelyNotAccepted);
                attempt = await LoadAsync(context, cancellationToken).ConfigureAwait(false);
            }
            if (!IsExactAttempt(attempt, context) || attempt!.State != WebAutomationAttemptState.Preparing ||
                attempt.Baseline is not null || attempt.AcceptedRemoteItems is not null)
                return Failure<SharePreparationReceipt>(SubmissionAcceptance.DefinitelyNotAccepted);

            await using var lease = await sessions.CreateAsync(
                context, session, request, recoveryOnly: false, null, null, cancellationToken)
                .ConfigureAwait(false);
            var result = await flow.PrepareAsync(lease.Session, request, cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess)
                return BaiduAdapterResult<SharePreparationReceipt>.Failure(
                    result.ErrorKind!.Value, null, result.RetryAfter, result.SubmissionAcceptance);
            var persisted = await LoadAsync(context, cancellationToken).ConfigureAwait(false);
            return IsExactAttempt(persisted, context) &&
                   persisted!.State == WebAutomationAttemptState.BaselinePersisted &&
                   persisted.Baseline is not null && persisted.ObservationFingerprint is not null
                ? BaiduAdapterResult<SharePreparationReceipt>.Success(
                    SharePreparationReceipt.Ready, SubmissionAcceptance.DefinitelyNotAccepted)
                : Failure<SharePreparationReceipt>(SubmissionAcceptance.DefinitelyNotAccepted);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return NotValidated<SharePreparationReceipt>(SubmissionAcceptance.DefinitelyNotAccepted); }
    }

    public async Task<BaiduAdapterResult<ShareCreationReceipt>> SubmitPermanentShareForOperationAsync(
        ShareOperationAutomationContext context,
        ManagedBrowserSessionReference session,
        PermanentShareRequest request,
        CancellationToken cancellationToken)
    {
        Validate(context, session, request, requireRecovery: false);
        try
        {
            var attempt = await LoadAsync(context, cancellationToken).ConfigureAwait(false);
            if (!IsExactAttempt(attempt, context) || attempt!.State != WebAutomationAttemptState.BaselinePersisted ||
                attempt.Baseline is null || attempt.ObservationFingerprint is null ||
                attempt.AcceptedRemoteItems is not null)
                return Failure<ShareCreationReceipt>(SubmissionAcceptance.DefinitelyNotAccepted);

            await using var lease = await sessions.CreateAsync(
                context, session, request, recoveryOnly: false, attempt.Baseline,
                attempt.ObservationFingerprint, cancellationToken).ConfigureAwait(false);
            var result = await flow.SubmitAsync(lease.Session, request, attempt.Baseline, cancellationToken)
                .ConfigureAwait(false);
            return await MarkSucceededAfterExactAsync(context, session, request, result, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return Failure<ShareCreationReceipt>(SubmissionAcceptance.Unknown); }
    }

    public async Task<BaiduAdapterResult<ShareCreationReceipt>> ConfirmExactShareForOperationAsync(
        ShareOperationAutomationContext context,
        ManagedBrowserSessionReference session,
        PermanentShareRequest request,
        CancellationToken cancellationToken)
    {
        Validate(context, session, request, requireRecovery: true);
        try
        {
            var attempt = await LoadAsync(context, cancellationToken).ConfigureAwait(false);
            if (!IsExactAttempt(attempt, context) || attempt!.Baseline is null)
                return Failure<ShareCreationReceipt>(SubmissionAcceptance.Unknown);
            if (attempt.State == WebAutomationAttemptState.BaselinePersisted)
                return Failure<ShareCreationReceipt>(SubmissionAcceptance.DefinitelyNotAccepted);

            if (attempt.State == WebAutomationAttemptState.Submitting)
            {
                await using var recovery = await sessions.CreateAsync(
                    context, session, request, recoveryOnly: true, attempt.Baseline,
                    attempt.ObservationFingerprint, cancellationToken).ConfigureAwait(false);
                var recovered = await flow.RecoverWithoutAcceptanceAsync(
                    recovery.Session, request, attempt.Baseline, cancellationToken).ConfigureAwait(false);
                if (!recovered.IsSuccess || !await acceptances.TryPersistRecoveredAsync(
                        context, recovered.Value!.Acceptance, cancellationToken).ConfigureAwait(false))
                    return Failure<ShareCreationReceipt>(SubmissionAcceptance.Unknown);
                attempt = await LoadAsync(context, cancellationToken).ConfigureAwait(false);
            }

            if (!IsExactAttempt(attempt, context) || attempt!.State is not (
                    WebAutomationAttemptState.ResultPending or WebAutomationAttemptState.Succeeded) ||
                attempt.AcceptedRemoteItems != request.RemoteItems || attempt.Baseline is not { } baseline)
                return Failure<ShareCreationReceipt>(SubmissionAcceptance.Unknown);
            var evidence = await acceptances.GetAsync(context, cancellationToken).ConfigureAwait(false);
            if (!EvidenceMatches(evidence, request))
                return Failure<ShareCreationReceipt>(SubmissionAcceptance.Unknown);
            if (attempt.State == WebAutomationAttemptState.Succeeded)
            {
                try { sessions.ReleaseExactHandoff(context, session, request); }
                catch { /* Persisted exact success remains authoritative. */ }
                return Receipt(request, evidence!);
            }

            await using var confirmation = await sessions.CreateAsync(
                context, session, request, recoveryOnly: true, baseline,
                attempt.ObservationFingerprint, cancellationToken).ConfigureAwait(false);
            var result = await flow.ConfirmExactAsync(
                confirmation.Session, request, baseline, evidence!, cancellationToken)
                .ConfigureAwait(false);
            return await MarkSucceededAfterExactAsync(context, session, request, result, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return Failure<ShareCreationReceipt>(SubmissionAcceptance.Unknown); }
    }

    private async Task<BaiduAdapterResult<ShareCreationReceipt>> MarkSucceededAfterExactAsync(
        ShareOperationAutomationContext context,
        ManagedBrowserSessionReference session,
        PermanentShareRequest request,
        BaiduAdapterResult<ShareCreationReceipt> result,
        CancellationToken cancellationToken)
    {
        if (!result.IsSuccess || !result.Value!.IsFinalAcceptance ||
            result.Value.Request.RemoteItems != request.RemoteItems)
            return result;
        var attempt = await LoadAsync(context, cancellationToken).ConfigureAwait(false);
        if (!IsExactAttempt(attempt, context) || attempt!.State != WebAutomationAttemptState.ResultPending ||
            attempt.AcceptedRemoteItems != request.RemoteItems)
            return Failure<ShareCreationReceipt>(SubmissionAcceptance.Unknown);
        var advanced = await attempts.TryAdvanceAsync(
                context.OperationId, context.AccountId, context.Generation,
                WebAutomationAttemptState.ResultPending, attempt.Revision,
                WebAutomationAttemptState.Succeeded, attempt.ObservationFingerprint,
                false, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        if (!advanced) return Failure<ShareCreationReceipt>(SubmissionAcceptance.Unknown);
        try { sessions.ReleaseExactHandoff(context, session, request); }
        catch { /* Persisted exact success is authoritative; stale process-local handoff remains fail-closed. */ }
        return result;
    }

    private Task<WebAutomationAttemptRecord?> LoadAsync(
        ShareOperationAutomationContext context, CancellationToken cancellationToken) =>
        attempts.GetAsync(context.OperationId, context.AccountId, context.Generation, cancellationToken);

    private static void Validate(
        ShareOperationAutomationContext context,
        ManagedBrowserSessionReference session,
        PermanentShareRequest request,
        bool requireRecovery)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        context.Validate();
        if (context.OwnerKind != WebAutomationOwnerKind.TransferShare ||
            context.ExactRemoteItems != request.RemoteItems || !request.IsPermanent || !request.IsAccessUnlimited)
            throw new InvalidOperationException("Managed sharing requires one exact transfer-linked remote-item set.");
        if (requireRecovery != context.IsRecovery)
            throw new InvalidOperationException("The managed share phase has the wrong recovery authority.");
        var profile = context.ExecutionProfile;
        if (profile.Engine != BaiduExecutionEngine.ManagedChromiumV1 ||
            context.AccountId != session.AccountId || profile.SessionId != session.SessionId ||
            profile.InstallationId != session.InstallationId || profile.BrowserFamily != session.BrowserFamily ||
            !string.Equals(profile.ProfileId, session.ProfileId, StringComparison.Ordinal))
            throw new InvalidOperationException("The managed session does not match the share snapshot.");
    }

    private static bool IsExactAttempt(
        WebAutomationAttemptRecord? attempt, ShareOperationAutomationContext context) =>
        attempt is not null && attempt.OperationId == context.OperationId &&
        attempt.AccountId == context.AccountId && attempt.Generation == context.Generation &&
        attempt.OwnerKind == WebAutomationOwnerKind.TransferShare &&
        attempt.ExecutionProfile == context.ExecutionProfile;

    private static bool EvidenceMatches(
        ShareOfficialAcceptance? evidence, PermanentShareRequest request)
    {
        if (evidence is not { IsAccepted: true, EffectiveSettings: { } settings } ||
            evidence.ExactRemoteItems != request.RemoteItems || settings.IsPermanent != true ||
            settings.IsAccessUnlimited != true || string.IsNullOrWhiteSpace(settings.ExtractionCode))
            return false;
        return request.UsesRandomExtractionCode
            ? settings.ExtractionCodeBehavior == ShareExtractionCodeBehavior.Random
            : settings.ExtractionCodeBehavior == ShareExtractionCodeBehavior.Fixed &&
              string.Equals(settings.ExtractionCode, request.FixedExtractionCode, StringComparison.Ordinal);
    }

    private BaiduAdapterResult<ShareCreationReceipt> Receipt(
        PermanentShareRequest request, ShareOfficialAcceptance evidence) =>
        BaiduAdapterResult<ShareCreationReceipt>.Success(new ShareCreationReceipt(
            timeProvider.GetUtcNow(), evidence.AdapterRequestId!, request, evidence.ShareId!,
            evidence.ShareUrl!, evidence.EffectiveSettings!), SubmissionAcceptance.Unknown);

    private static BaiduShareRecord ToShareRecord(ShareOfficialListEntry share) => new(
        share.ShareId, share.ExactRemoteItems, share.ShareUrl,
        share.EffectiveSettings.ExtractionCode!,
        share.EffectiveSettings.IsPermanent == true,
        share.EffectiveSettings.IsAccessUnlimited == true);

    private static BaiduAdapterResult<T> Failure<T>(SubmissionAcceptance acceptance) =>
        BaiduAdapterResult<T>.Failure(
            BaiduErrorKind.UnknownResponse, null, submissionAcceptance: acceptance);

    private static BaiduAdapterResult<T> NotValidated<T>(SubmissionAcceptance acceptance) =>
        BaiduAdapterResult<T>.Failure(
            BaiduErrorKind.NotValidated, null, submissionAcceptance: acceptance);
}
