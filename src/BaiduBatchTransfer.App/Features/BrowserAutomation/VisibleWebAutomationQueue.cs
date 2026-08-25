using System.Threading.Channels;
using System.IO;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.BaiduAdapter.WebAutomation;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.App.Features.BrowserAutomation;

public sealed class VisibleWebAutomationQueue : IVisibleWebAutomationBroker, IDisposable, IAsyncDisposable
{
    private readonly IVisibleWebAutomationSessionFactory sessionFactory;
    private readonly IWebAutomationUiDispatcher dispatcher;
    private readonly IAccountProfilePathPolicy profilePathPolicy;
    private readonly WebView2ProfileCloser profileCloser;
    private readonly UserHandoffCoordinator handoffCoordinator;
    private readonly IWebAutomationBatchCancellation batchCancellation;
    private readonly IWebAutomationRequestOwnershipStore ownershipStore;
    private readonly IWebAutomationNavigationAuthorizer navigationAuthorizer;
    private readonly VisibleWebAutomationQueueOptions options;
    private readonly Channel<IQueuedOperation> channel;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task worker;
    private int disposed;

    public VisibleWebAutomationQueue(
        IVisibleWebAutomationSessionFactory sessionFactory,
        IWebAutomationUiDispatcher dispatcher,
        IAccountProfilePathPolicy profilePathPolicy,
        WebView2ProfileCloser profileCloser,
        UserHandoffCoordinator handoffCoordinator,
        IWebAutomationBatchCancellation batchCancellation,
        IWebAutomationRequestOwnershipStore ownershipStore,
        IWebAutomationNavigationAuthorizer navigationAuthorizer,
        VisibleWebAutomationQueueOptions? options = null)
    {
        this.sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        this.profilePathPolicy = profilePathPolicy ?? throw new ArgumentNullException(nameof(profilePathPolicy));
        this.profileCloser = profileCloser ?? throw new ArgumentNullException(nameof(profileCloser));
        this.handoffCoordinator = handoffCoordinator ?? throw new ArgumentNullException(nameof(handoffCoordinator));
        this.batchCancellation = batchCancellation ?? throw new ArgumentNullException(nameof(batchCancellation));
        this.ownershipStore = ownershipStore ?? throw new ArgumentNullException(nameof(ownershipStore));
        this.navigationAuthorizer = navigationAuthorizer ?? throw new ArgumentNullException(nameof(navigationAuthorizer));
        this.options = (options ?? new VisibleWebAutomationQueueOptions()).Validate();
        channel = Channel.CreateUnbounded<IQueuedOperation>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        worker = RunQueueAsync();
    }

    public Task<VisibleWebAutomationResult<T>> EnqueueAsync<T>(
        VisibleWebAutomationRequest<T> request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();
        ValidateRequest(request);
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<VisibleWebAutomationResult<T>>(cancellationToken);

        var item = new QueuedOperation<T>(request, cancellationToken);
        if (!channel.Writer.TryWrite(item))
            throw new ObjectDisposedException(nameof(VisibleWebAutomationQueue));
        return item.Completion;
    }

    public void Dispose()
    {
        BeginDispose();
        _ = ObserveAsync(worker);
    }

    public async ValueTask DisposeAsync()
    {
        BeginDispose();
        await worker.ConfigureAwait(false);
        lifetime.Dispose();
    }

    private void BeginDispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        channel.Writer.TryComplete();
        lifetime.Cancel();
    }

    private async Task RunQueueAsync()
    {
        try
        {
            while (await channel.Reader.WaitToReadAsync(lifetime.Token).ConfigureAwait(false))
            {
                while (channel.Reader.TryRead(out var item))
                {
                    if (lifetime.IsCancellationRequested)
                    {
                        item.Cancel(lifetime.Token);
                        continue;
                    }
                    if (item.CancellationToken.IsCancellationRequested)
                    {
                        item.Cancel(item.CancellationToken);
                        continue;
                    }
                    await item.RunAsync(this, lifetime.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally
        {
            while (channel.Reader.TryRead(out var pending)) pending.Cancel(lifetime.Token);
        }
    }

    private async Task<VisibleWebAutomationResult<T>> ExecuteAsync<T>(
        VisibleWebAutomationRequest<T> request,
        CancellationToken requestCancellation,
        CancellationToken queueCancellation)
    {
        using var queueScope = CancellationTokenSource.CreateLinkedTokenSource(
            requestCancellation,
            queueCancellation);
        using var batchCancellationRequested = new CancellationTokenSource();
        using var batchRegistration = batchCancellation.Register(
            request.Identity.BatchId,
            request.Identity.OwnerKind,
            batchCancellationRequested.Cancel);
        ValidateRequest(request);
        if (!await ownershipStore.IsBoundAsync(
                request.Identity.OperationId,
                request.Identity.BatchId,
                request.Identity.AccountId,
                request.Identity.Generation,
                request.Identity.OwnerKind,
                queueScope.Token).ConfigureAwait(false))
        {
            throw new InvalidOperationException("浏览器操作与批次、账号或代次的持久绑定不一致。");
        }
        if (await batchCancellation.IsCancelledAsync(
                request.Identity.BatchId,
                request.Identity.OwnerKind,
                queueScope.Token).ConfigureAwait(false))
        {
            var persistedCancellationState = await handoffCoordinator
                .LoadPersistentStateAsync(request.Identity, CancellationToken.None).ConfigureAwait(false);
            return new(
                VisibleWebAutomationDisposition.BatchCancelled,
                default,
                persistedCancellationState ?? WebAutomationAttemptState.DefinitelyNotSubmitted);
        }
        var persistedAttempt = await handoffCoordinator
            .EnsureStartedAsync(request.Identity, queueScope.Token).ConfigureAwait(false);
        if (persistedAttempt.State is WebAutomationAttemptState.Succeeded or
            WebAutomationAttemptState.DefinitelyNotSubmitted or WebAutomationAttemptState.NeedsDecision)
        {
            throw new InvalidOperationException("当前代次的浏览器操作已经结束，不能重复执行。");
        }
        IVisibleWebAutomationSession? session = null;
        VisibleWebAutomationContext? context = null;
        Task? showTask = null;
        Task<T>? actionTask = null;
        IDisposable? profileLease = null;
        using var profileCloseRequested = new CancellationTokenSource();
        var operationStopped = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? completionException = null;
        VisibleWebAutomationResult<T>? completion = null;
        try
        {
            profileLease = profileCloser.Register(request.Identity.AccountId, async cancellationToken =>
            {
                profileCloseRequested.Cancel();
                var current = Volatile.Read(ref session);
                if (current is not null)
                    await dispatcher.InvokeAsync(
                        () => current.CloseAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
                await operationStopped.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            });

            using var profileScope = CancellationTokenSource.CreateLinkedTokenSource(
                queueScope.Token,
                profileCloseRequested.Token);
            session = await dispatcher.InvokeAsync(
                () => sessionFactory.CreateAsync(
                    request.Identity,
                    request.ProfilePath,
                    profileScope.Token),
                profileScope.Token).ConfigureAwait(false);
            context = new VisibleWebAutomationContext(
                request.Identity,
                session,
                dispatcher,
                handoffCoordinator,
                persistedAttempt.State == WebAutomationAttemptState.UserHandoff
                    ? persistedAttempt.ResumeStage!.Value
                    : persistedAttempt.State,
                persistedAttempt.ObservationFingerprint);
            using var sessionTimeout = new CancellationTokenSource(options.SessionTimeout);
            using var activeCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                queueScope.Token,
                session.UserClosed,
                profileCloseRequested.Token,
                batchCancellationRequested.Token,
                sessionTimeout.Token);
            try
            {
                showTask = dispatcher.InvokeAsync(
                    () => session.ShowAsync(request.Navigation.OfficialUri, activeCancellation.Token),
                    activeCancellation.Token);
                await showTask.WaitAsync(activeCancellation.Token).ConfigureAwait(false);
                if (persistedAttempt.State == WebAutomationAttemptState.UserHandoff)
                {
                    await context.RestoreUserHandoffAsync(
                        new UserHandoffLease(
                            request.Identity,
                            persistedAttempt.ResumeStage!.Value,
                            persistedAttempt.Revision,
                            persistedAttempt.ObservationFingerprint),
                        WebAutomationHandoffReason.UnknownPage,
                        activeCancellation.Token).ConfigureAwait(false);
                }
                actionTask = request.Action(session, context, activeCancellation.Token);
                var value = await actionTask.WaitAsync(activeCancellation.Token).ConfigureAwait(false);
                completion = new(
                    VisibleWebAutomationDisposition.ActionCompleted,
                    value,
                    context.CurrentStage);
            }
            catch (WebAutomationTerminalControlException terminal)
            {
                completion = new(
                    terminal.Result.Resolution == UserHandoffResolution.BatchCancelled
                        ? VisibleWebAutomationDisposition.BatchCancelled
                        : VisibleWebAutomationDisposition.Skipped,
                    default,
                    terminal.Result.PersistentState);
            }
            catch (OperationCanceledException exception) when (activeCancellation.IsCancellationRequested)
            {
                if (showTask is not null && !showTask.IsCompleted)
                    _ = ObserveLateCompletionAsync(showTask);
                if (actionTask is not null)
                    await AwaitOrObserveLateAsync(actionTask, options.ActionStopTimeout).ConfigureAwait(false);
                if (batchCancellationRequested.IsCancellationRequested)
                {
                    var persistedCancellationState = await handoffCoordinator
                        .LoadPersistentStateAsync(request.Identity, CancellationToken.None).ConfigureAwait(false)
                        ?? throw new InvalidOperationException(
                            "批次已经取消，但无法读取对应网页操作的持久状态。");
                    completion = new(
                        VisibleWebAutomationDisposition.BatchCancelled,
                        default,
                        persistedCancellationState);
                }
                else
                {
                    if (session.UserClosed.IsCancellationRequested)
                    {
                        var interrupted = await context.InterruptAsync(CancellationToken.None).ConfigureAwait(false);
                        completion = new(
                            VisibleWebAutomationDisposition.Skipped,
                            default,
                            interrupted.PersistentState);
                    }
                    else
                    {
                        await context.PersistInterruptionAsync(CancellationToken.None).ConfigureAwait(false);
                        if (sessionTimeout.IsCancellationRequested)
                        {
                            completionException = new TimeoutException(
                                "可见浏览器操作等待超时，当前项目已安全停止。",
                                exception);
                        }
                        else completionException = exception;
                    }
                }
            }
            catch (Exception exception)
            {
                completionException = exception;
                await context.PersistInterruptionAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            completionException ??= exception;
        }
        finally
        {
            context?.Deactivate();
            if (session is not null)
            {
                try
                {
                    using var closeTimeout = new CancellationTokenSource(options.WindowCloseTimeout);
                    var close = dispatcher.InvokeAsync(
                        () => session.CloseAsync(closeTimeout.Token), closeTimeout.Token);
                    await AwaitBoundedCleanupAsync(close, options.WindowCloseTimeout).ConfigureAwait(false);
                    var dispose = dispatcher.InvokeAsync(
                        async () => await session.DisposeAsync().ConfigureAwait(true),
                        closeTimeout.Token);
                    await AwaitBoundedCleanupAsync(dispose, options.WindowCloseTimeout).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    completionException ??= new InvalidOperationException(
                        "浏览器窗口未能安全关闭，后续网页操作已停止。", exception);
                    BeginDispose();
                }
            }
            profileLease?.Dispose();
            operationStopped.TrySetResult();
        }

        if (completionException is not null)
        {
            if (completionException is OperationCanceledException &&
                (requestCancellation.IsCancellationRequested || queueCancellation.IsCancellationRequested))
            {
                throw new OperationCanceledException(
                    "The visible browser operation was cancelled.",
                    completionException,
                    requestCancellation.IsCancellationRequested ? requestCancellation : queueCancellation);
            }
            throw completionException;
        }
        return completion ?? throw new InvalidOperationException("浏览器操作没有产生完成结果。");
    }

    private void ValidateRequest<T>(VisibleWebAutomationRequest<T> request)
    {
        request.Identity.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProfilePath);
        ArgumentNullException.ThrowIfNull(request.Navigation);
        ArgumentNullException.ThrowIfNull(request.Action);
        profilePathPolicy.EnsureSafeAccountPath(request.Identity.AccountId, request.ProfilePath);
        var expectedPath = profilePathPolicy.GetAccountProfilePath(request.Identity.AccountId);
        if (!string.Equals(request.ProfilePath, expectedPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("浏览器目录不属于当前账号。");
        if (!Directory.Exists(request.ProfilePath))
            throw new DirectoryNotFoundException("当前账号的浏览器目录不存在。");

        var webDecision = BaiduNavigationPolicy.Evaluate(request.Navigation.OfficialUri);
        if (!webDecision.IsAllowed)
            throw new WebAutomationAuthorizationException(
                BaiduAutomationDenialReason.OfficialUriNotAllowed,
                webDecision.SafeMessage ?? "已阻止不安全的页面跳转。");
        var protocolDecision = navigationAuthorizer.Authorize(request.Navigation);
        if (!protocolDecision.IsAllowed)
            throw new WebAutomationAuthorizationException(
                protocolDecision.DenialReason,
                "网页自动化协议尚未通过现场验证，未打开浏览器。");
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref disposed) != 0)
            throw new ObjectDisposedException(nameof(VisibleWebAutomationQueue));
    }

    private static async Task AwaitOrObserveLateAsync(Task task, TimeSpan timeout)
    {
        try { await task.WaitAsync(timeout).ConfigureAwait(false); }
        catch (TimeoutException) { _ = ObserveAsync(task); }
        catch (Exception) { }
    }

    private static async Task ObserveLateCompletionAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // The active request has already been fenced and its window is closing.
        }
    }

    private static async Task AwaitBoundedCleanupAsync(Task task, TimeSpan timeout)
    {
        try { await task.WaitAsync(timeout).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            _ = ObserveAsync(task);
            throw;
        }
    }

    private static async Task ObserveAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception) { }
    }

    private interface IQueuedOperation
    {
        CancellationToken CancellationToken { get; }
        Task RunAsync(VisibleWebAutomationQueue owner, CancellationToken queueCancellation);
        void Cancel(CancellationToken cancellationToken);
    }

    private sealed class QueuedOperation<T> : IQueuedOperation
    {
        private readonly VisibleWebAutomationRequest<T> request;
        private readonly TaskCompletionSource<VisibleWebAutomationResult<T>> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration cancellationRegistration;
        private int started;

        public QueuedOperation(
            VisibleWebAutomationRequest<T> request,
            CancellationToken cancellationToken)
        {
            this.request = request;
            CancellationToken = cancellationToken;
            cancellationRegistration = cancellationToken.Register(
                static state => ((QueuedOperation<T>)state!).CancelBeforeStart(),
                this);
        }

        public CancellationToken CancellationToken { get; }
        public Task<VisibleWebAutomationResult<T>> Completion => completion.Task;

        public async Task RunAsync(VisibleWebAutomationQueue owner, CancellationToken queueCancellation)
        {
            Interlocked.Exchange(ref started, 1);
            if (completion.Task.IsCompleted)
            {
                cancellationRegistration.Dispose();
                return;
            }
            try
            {
                var result = await owner.ExecuteAsync(request, CancellationToken, queueCancellation)
                    .ConfigureAwait(false);
                completion.TrySetResult(result);
            }
            catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(CancellationToken);
            }
            catch (OperationCanceledException) when (queueCancellation.IsCancellationRequested)
            {
                completion.TrySetCanceled(queueCancellation);
            }
            catch (Exception exception) { completion.TrySetException(exception); }
            finally { cancellationRegistration.Dispose(); }
        }

        public void Cancel(CancellationToken cancellationToken)
        {
            completion.TrySetCanceled(cancellationToken.IsCancellationRequested
                ? cancellationToken
                : new CancellationToken(canceled: true));
            cancellationRegistration.Dispose();
        }

        private void CancelBeforeStart()
        {
            if (Volatile.Read(ref started) != 0) return;
            completion.TrySetCanceled(CancellationToken);
        }
    }
}

public sealed class VisibleWebAutomationContext
{
    private readonly IVisibleWebAutomationSession session;
    private readonly IWebAutomationUiDispatcher dispatcher;
    private readonly UserHandoffCoordinator coordinator;
    private int active = 1;
    private int handoffInProgress;
    private WebAutomationAttemptState currentStage = WebAutomationAttemptState.Preparing;
    private string? observationFingerprint;

    internal VisibleWebAutomationContext(
        WebAutomationRequestIdentity identity,
        IVisibleWebAutomationSession session,
        IWebAutomationUiDispatcher dispatcher,
        UserHandoffCoordinator coordinator,
        WebAutomationAttemptState initialStage,
        string? initialObservationFingerprint)
    {
        Identity = identity;
        this.session = session;
        this.dispatcher = dispatcher;
        this.coordinator = coordinator;
        if (!WebAutomationAttemptRecord.CanResumeStage(initialStage))
            throw new ArgumentOutOfRangeException(nameof(initialStage));
        WebAutomationAttemptRecord.ValidateFingerprint(
            initialObservationFingerprint,
            nameof(initialObservationFingerprint));
        currentStage = initialStage;
        observationFingerprint = initialObservationFingerprint;
    }

    public WebAutomationRequestIdentity Identity { get; }
    public WebAutomationAttemptState CurrentStage => currentStage;

    public void TrackPersistentStage(
        WebAutomationAttemptState stage,
        string? sanitizedObservationFingerprint = null)
    {
        EnsureActive();
        if (!WebAutomationAttemptRecord.CanResumeStage(stage))
            throw new ArgumentOutOfRangeException(nameof(stage));
        WebAutomationAttemptRecord.ValidateFingerprint(
            sanitizedObservationFingerprint,
            nameof(sanitizedObservationFingerprint));
        currentStage = stage;
        observationFingerprint = sanitizedObservationFingerprint;
    }

    public async Task PersistBaselineAsync(
        WebAutomationBaseline baseline,
        string sanitizedObservationFingerprint,
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        await coordinator.PersistBaselineAsync(
            Identity,
            baseline,
            sanitizedObservationFingerprint,
            cancellationToken).ConfigureAwait(false);
        currentStage = WebAutomationAttemptState.BaselinePersisted;
        observationFingerprint = sanitizedObservationFingerprint;
    }

    public async Task AdvancePersistentStageAsync(
        WebAutomationAttemptState expectedState,
        WebAutomationAttemptState nextState,
        string? sanitizedObservationFingerprint,
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        if (currentStage != expectedState)
            throw new InvalidOperationException("当前浏览器阶段与持久化推进请求不一致。");
        await coordinator.AdvanceStageAsync(
            Identity,
            expectedState,
            nextState,
            sanitizedObservationFingerprint,
            cancellationToken).ConfigureAwait(false);
        currentStage = nextState;
        observationFingerprint = sanitizedObservationFingerprint;
    }

    public async Task PersistOfficialAcceptanceAsync(
        RemoteItemIdentitySet acceptedRemoteItems,
        string sanitizedObservationFingerprint,
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        if (currentStage != WebAutomationAttemptState.Submitting)
            throw new InvalidOperationException("当前浏览器阶段不能保存官方接受证据。");
        await coordinator.PersistOfficialAcceptanceAsync(
            Identity,
            acceptedRemoteItems,
            sanitizedObservationFingerprint,
            cancellationToken).ConfigureAwait(false);
        currentStage = WebAutomationAttemptState.ResultPending;
        observationFingerprint = sanitizedObservationFingerprint;
    }

    public async Task RequestUserHandoffAsync(
        WebAutomationAttemptState resumeStage,
        WebAutomationHandoffReason reason,
        string? sanitizedObservationFingerprint,
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        if (Interlocked.Exchange(ref handoffInProgress, 1) != 0)
            throw new InvalidOperationException("当前操作已经在等待人工处理。");
        try
        {
            TrackPersistentStage(resumeStage, sanitizedObservationFingerprint);
            var handoff = await coordinator.EnterAsync(
                Identity,
                resumeStage,
                sanitizedObservationFingerprint,
                cancellationToken).ConfigureAwait(false);
            await AwaitUserChoiceAsync(handoff, reason, cancellationToken).ConfigureAwait(false);
        }
        finally { Volatile.Write(ref handoffInProgress, 0); }
    }

    internal async Task RestoreUserHandoffAsync(
        UserHandoffLease handoff,
        WebAutomationHandoffReason reason,
        CancellationToken cancellationToken)
    {
        EnsureActive();
        ArgumentNullException.ThrowIfNull(handoff);
        if (handoff.Identity != Identity || handoff.ResumeStage != currentStage)
            throw new InvalidOperationException("持久化的人工接管不属于当前浏览器操作。");
        if (Interlocked.Exchange(ref handoffInProgress, 1) != 0)
            throw new InvalidOperationException("当前操作已经在等待人工处理。");
        try
        {
            await AwaitUserChoiceAsync(handoff, reason, cancellationToken).ConfigureAwait(false);
        }
        finally { Volatile.Write(ref handoffInProgress, 0); }
    }

    private async Task AwaitUserChoiceAsync(
        UserHandoffLease handoff,
        WebAutomationHandoffReason reason,
        CancellationToken cancellationToken)
    {
        var prompt = new WebAutomationHandoffPrompt(Identity, reason, SafeMessage(reason));
        await dispatcher.InvokeAsync(
            () => session.ShowHandoffAsync(prompt, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        WebAutomationUserChoice choice;
        try
        {
            choice = await dispatcher.InvokeAsync(
                () => session.WaitForHandoffChoiceAsync(cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (session.UserClosed.IsCancellationRequested)
        {
            var interrupted = await coordinator.ResolveAsync(
                Identity,
                handoff,
                WebAutomationUserChoice.WindowClosed,
                CancellationToken.None).ConfigureAwait(false);
            throw new WebAutomationTerminalControlException(interrupted);
        }

        var result = await coordinator.ResolveAsync(
            Identity,
            handoff,
            choice,
            cancellationToken).ConfigureAwait(false);
        if (result.Resolution == UserHandoffResolution.Resumed)
        {
            currentStage = result.PersistentState;
            return;
        }
        if (result.Resolution == UserHandoffResolution.Stale)
            throw new InvalidOperationException("人工接管已过期，未继续网页操作。");
        currentStage = result.PersistentState;
        throw new WebAutomationTerminalControlException(result);
    }

    internal async Task<UserHandoffResult> InterruptAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref active, 1, 1) == 0 || Volatile.Read(ref handoffInProgress) != 0)
            return new(UserHandoffResolution.Stale, currentStage);
        var result = await coordinator.InterruptAsync(
            Identity,
            currentStage,
            observationFingerprint,
            cancellationToken).ConfigureAwait(false);
        if (result.Resolution != UserHandoffResolution.Stale) currentStage = result.PersistentState;
        return result;
    }

    internal async Task PersistInterruptionAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref active, 1, 1) == 0) return;
        var persisted = await coordinator.PersistInterruptionAsync(
            Identity,
            currentStage,
            observationFingerprint,
            cancellationToken).ConfigureAwait(false);
        currentStage = persisted;
    }

    internal void Deactivate() => Interlocked.Exchange(ref active, 0);

    private void EnsureActive()
    {
        if (Volatile.Read(ref active) == 0)
            throw new InvalidOperationException("该浏览器操作已经结束，迟到事件已被忽略。");
    }

    private static string SafeMessage(WebAutomationHandoffReason reason) => reason switch
    {
        WebAutomationHandoffReason.LoginExpired => "登录状态已失效，请在当前窗口重新登录。",
        WebAutomationHandoffReason.Verification => "页面需要安全验证，请在当前窗口处理。",
        WebAutomationHandoffReason.Captcha => "页面出现验证码，请在当前窗口处理。",
        WebAutomationHandoffReason.RiskControl => "页面触发安全保护，请在当前窗口处理。",
        WebAutomationHandoffReason.UnknownPage => "页面状态无法确认，请检查当前窗口。",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };
}

internal sealed class WebAutomationTerminalControlException(UserHandoffResult result) : Exception
{
    public UserHandoffResult Result { get; } = result;
}

public sealed class WpfWebAutomationUiDispatcher : IWebAutomationUiDispatcher
{
    public async Task<T> InvokeAsync<T>(
        Func<Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        var application = System.Windows.Application.Current ??
            throw new InvalidOperationException("浏览器窗口只能从桌面应用中打开。");
        if (application.Dispatcher.CheckAccess()) return await action().ConfigureAwait(true);
        var nested = await application.Dispatcher.InvokeAsync(
            action,
            System.Windows.Threading.DispatcherPriority.Normal,
            cancellationToken).Task.ConfigureAwait(false);
        return await nested.ConfigureAwait(false);
    }

    public async Task InvokeAsync(
        Func<Task> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        var application = System.Windows.Application.Current ??
            throw new InvalidOperationException("浏览器窗口只能从桌面应用中打开。");
        if (application.Dispatcher.CheckAccess())
        {
            await action().ConfigureAwait(true);
            return;
        }
        var nested = await application.Dispatcher.InvokeAsync(
            action,
            System.Windows.Threading.DispatcherPriority.Normal,
            cancellationToken).Task.ConfigureAwait(false);
        await nested.ConfigureAwait(false);
    }
}
