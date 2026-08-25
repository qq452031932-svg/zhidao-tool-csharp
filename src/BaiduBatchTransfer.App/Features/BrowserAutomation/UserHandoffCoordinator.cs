using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.App.Features.BrowserAutomation;

public enum UserHandoffResolution
{
    Resumed,
    Skipped,
    BatchCancelled,
    Stale,
}

public sealed record UserHandoffLease(
    WebAutomationRequestIdentity Identity,
    WebAutomationAttemptState ResumeStage,
    long Revision,
    string? ObservationFingerprint);

public sealed record UserHandoffResult(
    UserHandoffResolution Resolution,
    WebAutomationAttemptState PersistentState);

public sealed class UserHandoffCoordinator
{
    private readonly IWebAutomationAttemptStore store;
    private readonly IWebAutomationBatchCancellation batchCancellation;
    private readonly TimeProvider timeProvider;

    public UserHandoffCoordinator(
        IWebAutomationAttemptStore store,
        IWebAutomationBatchCancellation batchCancellation,
        TimeProvider timeProvider)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.batchCancellation = batchCancellation ?? throw new ArgumentNullException(nameof(batchCancellation));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<WebAutomationAttemptRecord> EnsureStartedAsync(
        WebAutomationRequestIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        identity.Validate();
        var now = timeProvider.GetUtcNow();
        var created = WebAutomationAttemptRecord.Create(
            identity.OperationId,
            identity.AccountId,
            identity.Generation,
            identity.OwnerKind,
            now);
        if (await store.BeginAsync(created, cancellationToken).ConfigureAwait(false)) return created;

        var existing = await LoadExactAsync(identity, cancellationToken).ConfigureAwait(false);
        if (existing is null || existing.OwnerKind != identity.OwnerKind)
            throw new InvalidOperationException("浏览器操作的持久记录与当前请求不一致。");
        return existing;
    }

    public async Task<UserHandoffLease> EnterAsync(
        WebAutomationRequestIdentity identity,
        WebAutomationAttemptState resumeStage,
        string? observationFingerprint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        identity.Validate();
        if (!WebAutomationAttemptRecord.CanResumeStage(resumeStage))
            throw new ArgumentOutOfRangeException(nameof(resumeStage));
        WebAutomationAttemptRecord.ValidateFingerprint(observationFingerprint, nameof(observationFingerprint));

        var current = await LoadExactAsync(identity, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("浏览器操作的持久记录不存在。");
        if (current.OwnerKind != identity.OwnerKind || current.State != resumeStage)
            throw new InvalidOperationException("浏览器操作阶段已经变化，不能创建旧的人工接管。");
        var changed = await store.TryAdvanceAsync(
            identity.OperationId,
            identity.AccountId,
            identity.Generation,
            resumeStage,
            current.Revision,
            WebAutomationAttemptState.UserHandoff,
            observationFingerprint,
            true,
            timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
        if (!changed) throw new InvalidOperationException("浏览器操作阶段已经变化，人工接管未生效。");
        return new(identity, resumeStage, checked(current.Revision + 1), observationFingerprint);
    }

    public async Task PersistBaselineAsync(
        WebAutomationRequestIdentity identity,
        WebAutomationBaseline baseline,
        string observationFingerprint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(baseline);
        identity.Validate();
        WebAutomationAttemptRecord.ValidateFingerprint(observationFingerprint, nameof(observationFingerprint));
        var current = await LoadExactAsync(identity, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("浏览器操作的持久记录不存在。");
        if (current.OwnerKind != identity.OwnerKind || current.State != WebAutomationAttemptState.Preparing)
            throw new InvalidOperationException("浏览器操作阶段已经变化，不能保存旧的基线。");
        var changed = await store.TryPersistBaselineAsync(
            identity.OperationId,
            identity.AccountId,
            identity.Generation,
            current.Revision,
            baseline,
            observationFingerprint,
            timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
        if (!changed) throw new InvalidOperationException("浏览器操作阶段已经变化，基线未保存。");
    }

    public async Task AdvanceStageAsync(
        WebAutomationRequestIdentity identity,
        WebAutomationAttemptState expectedState,
        WebAutomationAttemptState nextState,
        string? observationFingerprint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        identity.Validate();
        if ((expectedState, nextState) is not
            (WebAutomationAttemptState.BaselinePersisted, WebAutomationAttemptState.Submitting) and not
            (WebAutomationAttemptState.Submitting, WebAutomationAttemptState.ResultPending))
            throw new ArgumentException("The requested web automation stage transition is not allowed.", nameof(nextState));
        WebAutomationAttemptRecord.ValidateFingerprint(observationFingerprint, nameof(observationFingerprint));
        var current = await LoadExactAsync(identity, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("浏览器操作的持久记录不存在。");
        if (current.OwnerKind != identity.OwnerKind || current.State != expectedState)
            throw new InvalidOperationException("浏览器操作阶段已经变化，不能推进旧的阶段。");
        var changed = await store.TryAdvanceAsync(
            identity.OperationId,
            identity.AccountId,
            identity.Generation,
            expectedState,
            current.Revision,
            nextState,
            observationFingerprint,
            false,
            timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
        if (!changed) throw new InvalidOperationException("浏览器操作阶段已经变化，阶段未推进。");
    }

    public async Task<UserHandoffResult> ResolveAsync(
        WebAutomationRequestIdentity identity,
        UserHandoffLease handoff,
        WebAutomationUserChoice choice,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(handoff);
        identity.Validate();
        if (identity != handoff.Identity || choice is not (WebAutomationUserChoice.Resume or
            WebAutomationUserChoice.Skip or WebAutomationUserChoice.CancelBatch or
            WebAutomationUserChoice.WindowClosed))
        {
            return new(UserHandoffResolution.Stale, WebAutomationAttemptState.UserHandoff);
        }

        if (choice == WebAutomationUserChoice.Resume)
        {
            var resumed = await store.TryResumeFromUserHandoffAsync(
                identity.OperationId,
                identity.AccountId,
                identity.Generation,
                handoff.Revision,
                handoff.ResumeStage,
                handoff.ObservationFingerprint,
                timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
            return resumed
                ? new(UserHandoffResolution.Resumed, handoff.ResumeStage)
                : new(UserHandoffResolution.Stale, WebAutomationAttemptState.UserHandoff);
        }

        var terminal = SafeTerminalState(handoff.ResumeStage);
        if (choice == WebAutomationUserChoice.CancelBatch)
        {
            var cancelled = await batchCancellation.CancelAsync(
                identity,
                handoff.Revision,
                handoff.ResumeStage,
                cancellationToken).ConfigureAwait(false);
            return cancelled.IsCancelled
                ? new(UserHandoffResolution.BatchCancelled, cancelled.PersistentState)
                : new(UserHandoffResolution.Stale, WebAutomationAttemptState.UserHandoff);
        }
        var resolved = await store.TryResolveUserHandoffAsync(
            identity.OperationId,
            identity.AccountId,
            identity.Generation,
            handoff.Revision,
            handoff.ResumeStage,
            terminal,
            handoff.ObservationFingerprint,
            timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
        if (!resolved) return new(UserHandoffResolution.Stale, WebAutomationAttemptState.UserHandoff);

        return new(UserHandoffResolution.Skipped, terminal);
    }

    public async Task<UserHandoffResult> InterruptAsync(
        WebAutomationRequestIdentity identity,
        WebAutomationAttemptState resumeStage,
        string? observationFingerprint,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var handoff = await EnterAsync(identity, resumeStage, observationFingerprint, cancellationToken)
                .ConfigureAwait(false);
            return await ResolveAsync(identity, handoff, WebAutomationUserChoice.WindowClosed, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return new(UserHandoffResolution.Stale, WebAutomationAttemptState.UserHandoff);
        }
    }

    public async Task<WebAutomationAttemptState> PersistInterruptionAsync(
        WebAutomationRequestIdentity identity,
        WebAutomationAttemptState resumeStage,
        string? observationFingerprint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        identity.Validate();
        if (!WebAutomationAttemptRecord.CanResumeStage(resumeStage))
            throw new ArgumentOutOfRangeException(nameof(resumeStage));
        WebAutomationAttemptRecord.ValidateFingerprint(observationFingerprint, nameof(observationFingerprint));

        var current = await LoadExactAsync(identity, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("浏览器操作的持久记录不存在。");
        if (current.OwnerKind != identity.OwnerKind)
            throw new InvalidOperationException("浏览器操作的持久记录与当前请求不一致。");
        if (current.State == WebAutomationAttemptState.UserHandoff &&
            current.ResumeStage == resumeStage)
        {
            return WebAutomationAttemptState.UserHandoff;
        }
        if (current.State != resumeStage)
            throw new InvalidOperationException("浏览器操作阶段已经变化，无法保存安全中断状态。");

        await EnterAsync(identity, resumeStage, observationFingerprint, cancellationToken)
            .ConfigureAwait(false);
        return WebAutomationAttemptState.UserHandoff;
    }

    public async Task<WebAutomationAttemptState?> LoadPersistentStateAsync(
        WebAutomationRequestIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        identity.Validate();
        var attempt = await LoadExactAsync(identity, cancellationToken).ConfigureAwait(false);
        if (attempt is null) return null;
        if (attempt.OwnerKind != identity.OwnerKind)
            throw new InvalidOperationException("浏览器操作的持久记录与当前请求不一致。");
        return attempt.State;
    }

    private Task<WebAutomationAttemptRecord?> LoadExactAsync(
        WebAutomationRequestIdentity identity,
        CancellationToken cancellationToken) =>
        store.GetAsync(identity.OperationId, identity.AccountId, identity.Generation, cancellationToken);

    private static WebAutomationAttemptState SafeTerminalState(WebAutomationAttemptState resumeStage) =>
        resumeStage is WebAutomationAttemptState.Preparing or WebAutomationAttemptState.BaselinePersisted
            ? WebAutomationAttemptState.DefinitelyNotSubmitted
            : WebAutomationAttemptState.NeedsDecision;
}
