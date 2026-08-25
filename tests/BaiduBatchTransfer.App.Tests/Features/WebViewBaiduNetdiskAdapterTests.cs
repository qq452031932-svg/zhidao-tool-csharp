using System.Collections.Immutable;
using System.Reflection;
using BaiduBatchTransfer.App.Features.BrowserAutomation;
using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.BaiduAdapter.WebAutomation;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class WebViewBaiduNetdiskAdapterTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task OperationBoundPrepareQueuesExactAuthoritativeIdentityAndCanonicalProfile()
    {
        var accountId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        var broker = new RecordingBroker();
        var profiles = new RecordingProfilePolicy(accountId);
        var context = new TransferOperationAutomationContext(
            operationId, batchId, accountId, 3, Share());
        var adapter = new WebViewBaiduNetdiskAdapter(
            new ReadOnlyAdapter(), broker, profiles, new TransferAutomationFlow(), "build-a",
            new AttemptStore(Attempt(context, WebAutomationAttemptState.Preparing, null)));

        var result = await adapter.PrepareWholeShareForOperationAsync(
            context,
            Session(accountId),
            Share(),
            Destination(),
            DuplicatePolicy.Skip,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(new WebAutomationRequestIdentity(
            operationId, batchId, accountId, 3, WebAutomationOwnerKind.Transfer), broker.Identity);
        Assert.Equal(profiles.Path, broker.ProfilePath);
        Assert.Equal(new Uri(Share().OriginalUrl), broker.Navigation!.OfficialUri);
        Assert.Equal(BaiduProtocolValidationStatus.NotValidated,
            broker.Navigation.FieldEvidenceValidationStatus);
        Assert.False(broker.Navigation.Acceptance.IsAccepted);
    }

    [Fact]
    public async Task PreparedRetryReusesPersistedBaselineWithoutOpeningAnotherWindow()
    {
        var accountId = Guid.NewGuid();
        var context = new TransferOperationAutomationContext(
            Guid.NewGuid(), Guid.NewGuid(), accountId, 2, Share());
        var attempts = new AttemptStore(Attempt(
            context,
            WebAutomationAttemptState.BaselinePersisted,
            WebAutomationBaseline.Create(["fictional-old"])));
        var broker = new RecordingBroker();
        var adapter = new WebViewBaiduNetdiskAdapter(
            new ReadOnlyAdapter(), broker, new RecordingProfilePolicy(accountId),
            new TransferAutomationFlow(), "build-a", attempts);

        var result = await adapter.PrepareWholeShareForOperationAsync(
            context, Session(accountId), Share(), Destination(), DuplicatePolicy.Skip, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Null(broker.Identity);
    }

    [Fact]
    public async Task LegacyWriteWithoutPersistentOperationIdentityFailsClosed()
    {
        var accountId = Guid.NewGuid();
        var broker = new RecordingBroker();
        var adapter = new WebViewBaiduNetdiskAdapter(
            new ReadOnlyAdapter(), broker, new RecordingProfilePolicy(accountId),
            new TransferAutomationFlow(), "build-a");

        var result = await adapter.SaveWholeShareAsync(
            Session(accountId), Share(), Destination(), DuplicatePolicy.Skip, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(BaiduErrorKind.NotValidated, result.ErrorKind);
        Assert.Equal(SubmissionAcceptance.DefinitelyNotAccepted, result.SubmissionAcceptance);
        Assert.Null(broker.Identity);
    }

    [Fact]
    public async Task ProductionAuthorizationDenialIsDefinitelyNotAcceptedInsteadOfUnknownSubmission()
    {
        var accountId = Guid.NewGuid();
        var broker = new RecordingBroker { ThrowAuthorization = true };
        var adapter = new WebViewBaiduNetdiskAdapter(
            new ReadOnlyAdapter(), broker, new RecordingProfilePolicy(accountId),
            new TransferAutomationFlow(), "build-a");
        var context = new TransferOperationAutomationContext(
            Guid.NewGuid(), Guid.NewGuid(), accountId, 1, Share());

        var result = await adapter.PrepareWholeShareForOperationAsync(
            context,
            Session(accountId),
            Share(),
            Destination(),
            DuplicatePolicy.Skip,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(BaiduErrorKind.NotValidated, result.ErrorKind);
        Assert.Equal(SubmissionAcceptance.DefinitelyNotAccepted, result.SubmissionAcceptance);
    }

    [Fact]
    public async Task AccountMismatchIsRejectedBeforeQueueing()
    {
        var accountId = Guid.NewGuid();
        var broker = new RecordingBroker();
        var adapter = new WebViewBaiduNetdiskAdapter(
            new ReadOnlyAdapter(), broker, new RecordingProfilePolicy(accountId),
            new TransferAutomationFlow(), "build-a");
        var context = new TransferOperationAutomationContext(
            Guid.NewGuid(), Guid.NewGuid(), accountId, 1, Share());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.SaveWholeShareForOperationAsync(
                context,
                Session(Guid.NewGuid()),
                Share(),
                Destination(),
                DuplicatePolicy.Skip,
                CancellationToken.None));
        Assert.Null(broker.Identity);
    }

    [Fact]
    public async Task ExactRecoveryQueuesOnlyWithPersistedResultPendingAcceptanceAndFullBaseline()
    {
        var accountId = Guid.NewGuid();
        var context = new TransferOperationAutomationContext(
            Guid.NewGuid(), Guid.NewGuid(), accountId, 2, Share());
        var baseline = WebAutomationBaseline.Create(["fictional-old"]);
        var attempts = new AttemptStore(Attempt(
            context,
            WebAutomationAttemptState.ResultPending,
            baseline,
            AcceptedItems()));
        var broker = new RecordingBroker();
        var adapter = new WebViewBaiduNetdiskAdapter(
            new ReadOnlyAdapter(), broker, new RecordingProfilePolicy(accountId),
            new TransferAutomationFlow(), "build-a", attempts);

        var result = await adapter.ConfirmExactTransferForOperationAsync(
            context,
            Session(accountId),
            Query(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(TransferConfirmationState.ExactPresent, result.Value!.State);
        Assert.Equal(context.OperationId, broker.Identity!.OperationId);
        Assert.Equal(context.Generation, broker.Identity.Generation);
    }

    [Fact]
    public async Task MissingPersistentBaselineNeverOpensRecoveryWindow()
    {
        var accountId = Guid.NewGuid();
        var context = new TransferOperationAutomationContext(
            Guid.NewGuid(), Guid.NewGuid(), accountId, 2, Share());
        var attempts = new AttemptStore(Attempt(context, WebAutomationAttemptState.Preparing, null));
        var broker = new RecordingBroker();
        var adapter = new WebViewBaiduNetdiskAdapter(
            new ReadOnlyAdapter(), broker, new RecordingProfilePolicy(accountId),
            new TransferAutomationFlow(), "build-a", attempts);

        var result = await adapter.ConfirmExactTransferForOperationAsync(
            context,
            Session(accountId),
            Query(),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(SubmissionAcceptance.Unknown, result.SubmissionAcceptance);
        Assert.Null(broker.Identity);
    }

    [Fact]
    public async Task ActiveExactTransferAdvancesPersistentWebAttemptToSucceeded()
    {
        var accountId = Guid.NewGuid();
        var context = new TransferOperationAutomationContext(
            Guid.NewGuid(), Guid.NewGuid(), accountId, 2, Share());
        var attempts = new AttemptStore(Attempt(
            context,
            WebAutomationAttemptState.BaselinePersisted,
            WebAutomationBaseline.Create(["fictional-old"])));
        var broker = new CallbackBroker(attempts, new AutomationSession());
        var adapter = new WebViewBaiduNetdiskAdapter(
            new ReadOnlyAdapter(), broker, new RecordingProfilePolicy(accountId),
            new TransferAutomationFlow(), "build-a", attempts);

        var result = await adapter.SaveWholeShareForOperationAsync(
            context, Session(accountId), Share(), Destination(), DuplicatePolicy.Skip, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(WebAutomationAttemptState.Succeeded, attempts.Current.State);
        Assert.Equal(["fictional-new"],
            attempts.Current.AcceptedRemoteItems!.Items.Select(item => item.ExactId));
    }

    [Fact]
    public async Task RecoveryExactTransferAdvancesPersistentWebAttemptToSucceeded()
    {
        var accountId = Guid.NewGuid();
        var context = new TransferOperationAutomationContext(
            Guid.NewGuid(), Guid.NewGuid(), accountId, 2, Share());
        var attempts = new AttemptStore(Attempt(
            context,
            WebAutomationAttemptState.ResultPending,
            WebAutomationBaseline.Create(["fictional-old"]),
            AcceptedItems()));
        var broker = new CallbackBroker(attempts, new AutomationSession());
        var adapter = new WebViewBaiduNetdiskAdapter(
            new ReadOnlyAdapter(), broker, new RecordingProfilePolicy(accountId),
            new TransferAutomationFlow(), "build-a", attempts);

        var result = await adapter.ConfirmExactTransferForOperationAsync(
            context, Session(accountId), Query(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(TransferConfirmationState.ExactPresent, result.Value!.State);
        Assert.Equal(WebAutomationAttemptState.Succeeded, attempts.Current.State);
    }

    [Fact]
    public async Task RecoveryWithoutAcceptedTargetInListingStaysResultPending()
    {
        var accountId = Guid.NewGuid();
        var context = new TransferOperationAutomationContext(
            Guid.NewGuid(), Guid.NewGuid(), accountId, 2, Share());
        var attempts = new AttemptStore(Attempt(
            context,
            WebAutomationAttemptState.ResultPending,
            WebAutomationBaseline.Create(["fictional-old"]),
            AcceptedItems()));
        var broker = new CallbackBroker(
            attempts,
            new AutomationSession { CurrentItems = [new("fictional-old", "旧资源")] });
        var adapter = new WebViewBaiduNetdiskAdapter(
            new ReadOnlyAdapter(), broker, new RecordingProfilePolicy(accountId),
            new TransferAutomationFlow(), "build-a", attempts);

        var result = await adapter.ConfirmExactTransferForOperationAsync(
            context, Session(accountId), Query(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(TransferConfirmationState.Ambiguous, result.Value!.State);
        Assert.Equal(WebAutomationAttemptState.ResultPending, attempts.Current.State);
    }

    [Fact]
    public async Task RecoveryCancellationDoesNotMutateResultPendingAttempt()
    {
        var accountId = Guid.NewGuid();
        var context = new TransferOperationAutomationContext(
            Guid.NewGuid(), Guid.NewGuid(), accountId, 2, Share());
        var attempts = new AttemptStore(Attempt(
            context,
            WebAutomationAttemptState.ResultPending,
            WebAutomationBaseline.Create(["fictional-old"]),
            AcceptedItems()));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var broker = new CallbackBroker(
            attempts,
            new AutomationSession { CancelListingRead = true });
        var adapter = new WebViewBaiduNetdiskAdapter(
            new ReadOnlyAdapter(), broker, new RecordingProfilePolicy(accountId),
            new TransferAutomationFlow(), "build-a", attempts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            adapter.ConfirmExactTransferForOperationAsync(
                context, Session(accountId), Query(), cancelled.Token));

        Assert.Equal(WebAutomationAttemptState.ResultPending, attempts.Current.State);
    }

    [Fact]
    public async Task SucceededAttemptReturnsPersistedExactEvidenceWithoutOpeningRecoveryWindow()
    {
        var accountId = Guid.NewGuid();
        var context = new TransferOperationAutomationContext(
            Guid.NewGuid(), Guid.NewGuid(), accountId, 2, Share());
        var attempts = new AttemptStore(Attempt(
            context,
            WebAutomationAttemptState.Succeeded,
            WebAutomationBaseline.Create(["fictional-old"]),
            AcceptedItems()));
        var broker = new RecordingBroker();
        var adapter = new WebViewBaiduNetdiskAdapter(
            new ReadOnlyAdapter(), broker, new RecordingProfilePolicy(accountId),
            new TransferAutomationFlow(), "build-a", attempts);

        var result = await adapter.ConfirmExactTransferForOperationAsync(
            context, Session(accountId), Query(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(AcceptedItems(), result.Value!.RemoteItems);
        Assert.Null(broker.Identity);
    }

    [Fact]
    public async Task PreparingUserHandoffCanBeRequeuedAfterRestart()
    {
        var accountId = Guid.NewGuid();
        var context = new TransferOperationAutomationContext(
            Guid.NewGuid(), Guid.NewGuid(), accountId, 2, Share());
        var attempts = new AttemptStore(Attempt(
            context,
            WebAutomationAttemptState.UserHandoff,
            null,
            resumeStage: WebAutomationAttemptState.Preparing));
        var broker = new RecordingBroker();
        var adapter = new WebViewBaiduNetdiskAdapter(
            new ReadOnlyAdapter(), broker, new RecordingProfilePolicy(accountId),
            new TransferAutomationFlow(), "build-a", attempts);

        var result = await adapter.PrepareWholeShareForOperationAsync(
            context, Session(accountId), Share(), Destination(), DuplicatePolicy.Skip, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(context.OperationId, broker.Identity!.OperationId);
    }

    [Fact]
    public async Task BaselinePersistedUserHandoffResumesSubmitWithoutLosingBaseline()
    {
        var accountId = Guid.NewGuid();
        var context = new TransferOperationAutomationContext(
            Guid.NewGuid(), Guid.NewGuid(), accountId, 2, Share());
        var baseline = WebAutomationBaseline.Create(["fictional-old"]);
        var attempts = new AttemptStore(Attempt(
            context,
            WebAutomationAttemptState.UserHandoff,
            baseline,
            resumeStage: WebAutomationAttemptState.BaselinePersisted));
        var adapter = new WebViewBaiduNetdiskAdapter(
            new ReadOnlyAdapter(), new CallbackBroker(attempts, new AutomationSession()),
            new RecordingProfilePolicy(accountId), new TransferAutomationFlow(), "build-a", attempts);

        var result = await adapter.SaveWholeShareForOperationAsync(
            context, Session(accountId), Share(), Destination(), DuplicatePolicy.Skip, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(WebAutomationAttemptState.Succeeded, attempts.Current.State);
        Assert.Equal(baseline, attempts.Current.Baseline);
        Assert.Equal(AcceptedItems(), attempts.Current.AcceptedRemoteItems);
    }

    [Fact]
    public async Task ResultPendingUserHandoffResumesExactRecoveryAndSucceeds()
    {
        var accountId = Guid.NewGuid();
        var context = new TransferOperationAutomationContext(
            Guid.NewGuid(), Guid.NewGuid(), accountId, 2, Share());
        var baseline = WebAutomationBaseline.Create(["fictional-old"]);
        var accepted = AcceptedItems();
        var attempts = new AttemptStore(Attempt(
            context,
            WebAutomationAttemptState.UserHandoff,
            baseline,
            accepted,
            WebAutomationAttemptState.ResultPending));
        var adapter = new WebViewBaiduNetdiskAdapter(
            new ReadOnlyAdapter(), new CallbackBroker(attempts, new AutomationSession()),
            new RecordingProfilePolicy(accountId), new TransferAutomationFlow(), "build-a", attempts);

        var result = await adapter.ConfirmExactTransferForOperationAsync(
            context, Session(accountId), Query(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(TransferConfirmationState.ExactPresent, result.Value!.State);
        Assert.Equal(WebAutomationAttemptState.Succeeded, attempts.Current.State);
        Assert.Equal(baseline, attempts.Current.Baseline);
        Assert.Equal(accepted, attempts.Current.AcceptedRemoteItems);
    }

    [Theory]
    [InlineData(WebAutomationAttemptState.BaselinePersisted, true)]
    [InlineData(WebAutomationAttemptState.Submitting, false)]
    public async Task UserHandoffResumeStageOrEvidenceMismatchFailsClosed(
        WebAutomationAttemptState resumeStage,
        bool includeAcceptedEvidence)
    {
        var accountId = Guid.NewGuid();
        var context = new TransferOperationAutomationContext(
            Guid.NewGuid(), Guid.NewGuid(), accountId, 2, Share());
        var attempts = new AttemptStore(Attempt(
            context,
            WebAutomationAttemptState.UserHandoff,
            WebAutomationBaseline.Create(["fictional-old"]),
            includeAcceptedEvidence ? AcceptedItems() : null,
            resumeStage));
        var broker = new RecordingBroker();
        var adapter = new WebViewBaiduNetdiskAdapter(
            new ReadOnlyAdapter(), broker, new RecordingProfilePolicy(accountId),
            new TransferAutomationFlow(), "build-a", attempts);

        var result = await adapter.SaveWholeShareForOperationAsync(
            context, Session(accountId), Share(), Destination(), DuplicatePolicy.Skip, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(SubmissionAcceptance.Unknown, result.SubmissionAcceptance);
        Assert.Null(broker.Identity);
    }

    [Fact]
    public async Task BaselinePersistedUserHandoffPrepareResumesBeforeReadyAndReusesBaselineForSubmit()
    {
        var accountId = Guid.NewGuid();
        var context = new TransferOperationAutomationContext(
            Guid.NewGuid(), Guid.NewGuid(), accountId, 2, Share());
        var baseline = WebAutomationBaseline.Create(["fictional-old"]);
        var attempts = new AttemptStore(Attempt(
            context,
            WebAutomationAttemptState.UserHandoff,
            baseline,
            resumeStage: WebAutomationAttemptState.BaselinePersisted));
        var session = new AutomationSession();
        var broker = new CallbackBroker(attempts, session);
        var adapter = new WebViewBaiduNetdiskAdapter(
            new ReadOnlyAdapter(), broker, new RecordingProfilePolicy(accountId),
            new TransferAutomationFlow(), "build-a", attempts);

        var prepared = await adapter.PrepareWholeShareForOperationAsync(
            context, Session(accountId), Share(), Destination(), DuplicatePolicy.Skip, CancellationToken.None);

        Assert.True(prepared.IsSuccess);
        Assert.Equal(WebAutomationAttemptState.BaselinePersisted, attempts.Current.State);
        Assert.Equal(1, broker.EnqueueCount);
        Assert.Empty(session.Events);

        var saved = await adapter.SaveWholeShareForOperationAsync(
            context, Session(accountId), Share(), Destination(), DuplicatePolicy.Skip, CancellationToken.None);

        Assert.True(saved.IsSuccess);
        Assert.Equal(2, broker.EnqueueCount);
        Assert.Equal(WebAutomationAttemptState.Succeeded, attempts.Current.State);
        Assert.Equal(baseline, attempts.Current.Baseline);
        Assert.DoesNotContain("open-destination", session.Events);
        Assert.Contains("mark-submitting", session.Events);
        Assert.Contains("submit", session.Events);
    }

    [Fact]
    public async Task BaselinePersistedAttemptProvesNoSubmissionDuringReconciliation()
    {
        var accountId = Guid.NewGuid();
        var context = new TransferOperationAutomationContext(
            Guid.NewGuid(), Guid.NewGuid(), accountId, 2, Share());
        var attempts = new AttemptStore(Attempt(
            context,
            WebAutomationAttemptState.BaselinePersisted,
            WebAutomationBaseline.Create(["fictional-old"])));
        var broker = new RecordingBroker();
        var adapter = new WebViewBaiduNetdiskAdapter(
            new ReadOnlyAdapter(), broker, new RecordingProfilePolicy(accountId),
            new TransferAutomationFlow(), "build-a", attempts);

        var result = await adapter.ConfirmExactTransferForOperationAsync(
            context, Session(accountId), Query(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(TransferConfirmationState.Absent, result.Value!.State);
        Assert.Equal(SubmissionAcceptance.DefinitelyNotAccepted, result.SubmissionAcceptance);
        Assert.Null(broker.Identity);
    }

    [Fact]
    public async Task BaselinePersistedUserHandoffMustResumeBeforeProvingNoSubmission()
    {
        var accountId = Guid.NewGuid();
        var context = new TransferOperationAutomationContext(
            Guid.NewGuid(), Guid.NewGuid(), accountId, 2, Share());
        var attempts = new AttemptStore(Attempt(
            context,
            WebAutomationAttemptState.UserHandoff,
            WebAutomationBaseline.Create(["fictional-old"]),
            resumeStage: WebAutomationAttemptState.BaselinePersisted));
        var session = new AutomationSession();
        var broker = new CallbackBroker(attempts, session);
        var adapter = new WebViewBaiduNetdiskAdapter(
            new ReadOnlyAdapter(), broker, new RecordingProfilePolicy(accountId),
            new TransferAutomationFlow(), "build-a", attempts);

        var result = await adapter.ConfirmExactTransferForOperationAsync(
            context, Session(accountId), Query(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(TransferConfirmationState.Absent, result.Value!.State);
        Assert.Equal(SubmissionAcceptance.DefinitelyNotAccepted, result.SubmissionAcceptance);
        Assert.Equal(1, broker.EnqueueCount);
        Assert.Equal(WebAutomationAttemptState.BaselinePersisted, attempts.Current.State);
        Assert.Empty(session.Events);
    }

    [Theory]
    [InlineData(WebAutomationAttemptState.Submitting, false, null)]
    [InlineData(WebAutomationAttemptState.UserHandoff, false, WebAutomationAttemptState.Submitting)]
    [InlineData(WebAutomationAttemptState.BaselinePersisted, true, null)]
    public async Task UncertainOrInconsistentAttemptCannotBeDeclaredNotSubmitted(
        WebAutomationAttemptState state,
        bool includeAcceptedEvidence,
        WebAutomationAttemptState? resumeStage)
    {
        var accountId = Guid.NewGuid();
        var context = new TransferOperationAutomationContext(
            Guid.NewGuid(), Guid.NewGuid(), accountId, 2, Share());
        var attempts = new AttemptStore(Attempt(
            context,
            state,
            WebAutomationBaseline.Create(["fictional-old"]),
            includeAcceptedEvidence ? AcceptedItems() : null,
            resumeStage));
        var broker = new RecordingBroker();
        var adapter = new WebViewBaiduNetdiskAdapter(
            new ReadOnlyAdapter(), broker, new RecordingProfilePolicy(accountId),
            new TransferAutomationFlow(), "build-a", attempts);

        var result = await adapter.ConfirmExactTransferForOperationAsync(
            context, Session(accountId), Query(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(SubmissionAcceptance.Unknown, result.SubmissionAcceptance);
        Assert.Null(broker.Identity);
    }

    private static ShareReference Share() =>
        new("fictional-share", "https://pan.baidu.com/s/fictional", "1111");

    private static RemoteDirectory Destination() =>
        new("fictional-directory", new RemotePath("/fictional"));

    private static TransferConfirmationQuery Query() => new(
        "fictional-share",
        new RemotePath("/fictional"),
        "fictional-name-fingerprint",
        "fictional-operation-fingerprint",
        Now.AddMinutes(-1),
        Now.AddMinutes(1));

    private static WebAutomationAttemptRecord Attempt(
        TransferOperationAutomationContext context,
        WebAutomationAttemptState state,
        WebAutomationBaseline? baseline,
        RemoteItemIdentitySet? acceptedRemoteItems = null,
        WebAutomationAttemptState? resumeStage = null) => new(
            context.OperationId,
            context.AccountId,
            context.Generation,
            WebAutomationOwnerKind.Transfer,
            state,
            resumeStage,
            baseline,
            new string('a', 64),
            state == WebAutomationAttemptState.UserHandoff,
            2,
            Now.AddMinutes(-2),
            Now.AddMinutes(-1),
            acceptedRemoteItems);

    private static RemoteItemIdentitySet AcceptedItems() =>
        new([new("fictional-new", "新资源")]);

    private static AccountSession Session(Guid accountId) => new(
        accountId,
        "fictional-user",
        [new SessionCookie("BDUSS", "fictional", ".baidu.com", "/", null, true, true)],
        Now);

    private sealed class RecordingBroker : IVisibleWebAutomationBroker
    {
        public bool ThrowAuthorization { get; init; }
        public WebAutomationRequestIdentity? Identity { get; private set; }
        public string? ProfilePath { get; private set; }
        public BaiduNavigationAuthorizationRequest? Navigation { get; private set; }

        public Task<VisibleWebAutomationResult<T>> EnqueueAsync<T>(
            VisibleWebAutomationRequest<T> request,
            CancellationToken cancellationToken = default)
        {
            Identity = request.Identity;
            ProfilePath = request.ProfilePath;
            Navigation = request.Navigation;
            if (ThrowAuthorization)
                throw new WebAutomationAuthorizationException(
                    BaiduAutomationDenialReason.CatalogNotValidated,
                    "synthetic authorization denial");
            object value = typeof(T) == typeof(BaiduAdapterResult<TransferPreparationReceipt>)
                ? BaiduAdapterResult<TransferPreparationReceipt>.Success(TransferPreparationReceipt.Ready)
                : typeof(T) == typeof(BaiduAdapterResult<TransferReceipt>)
                    ? BaiduAdapterResult<TransferReceipt>.Success(
                    TransferReceipt.WithExactEvidence(
                        Now,
                        "fictional-request",
                        new RemoteItemIdentitySet([new("fictional-new", "新资源")])))
                    : BaiduAdapterResult<TransferConfirmationResult>.Success(
                    TransferConfirmationResult.ExactPresent(
                        new RemoteItemIdentitySet([new("fictional-new", "新资源")]))) ;
            return Task.FromResult(new VisibleWebAutomationResult<T>(
                VisibleWebAutomationDisposition.ActionCompleted,
                (T)value,
                WebAutomationAttemptState.Succeeded));
        }
    }

    private sealed class AttemptStore(WebAutomationAttemptRecord attempt) : IWebAutomationAttemptStore
    {
        public WebAutomationAttemptRecord Current { get; private set; } = attempt;

        public Task<WebAutomationAttemptRecord?> GetAsync(Guid operationId, Guid accountId, int generation, CancellationToken cancellationToken = default) =>
            Task.FromResult<WebAutomationAttemptRecord?>(
                operationId == Current.OperationId && accountId == Current.AccountId && generation == Current.Generation
                    ? Current
                    : null);
        public Task<bool> BeginAsync(WebAutomationAttemptRecord value, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Page<WebAutomationAttemptRecord>> GetRecoveryPageAsync(PageQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> TryPersistBaselineAsync(Guid operationId, Guid accountId, int generation, long expectedRevision, WebAutomationBaseline baseline, string observationFingerprint, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> TryAdvanceAsync(Guid operationId, Guid accountId, int generation, WebAutomationAttemptState expectedState, long expectedRevision, WebAutomationAttemptState nextState, string? observationFingerprint, bool isWaitingForUser, DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
        {
            if (operationId != Current.OperationId || accountId != Current.AccountId ||
                generation != Current.Generation || expectedState != Current.State ||
                expectedRevision != Current.Revision)
                return Task.FromResult(false);
            Current = new(
                Current.OperationId,
                Current.AccountId,
                Current.Generation,
                Current.OwnerKind,
                nextState,
                null,
                Current.Baseline,
                observationFingerprint ?? Current.ObservationFingerprint,
                isWaitingForUser,
                checked(Current.Revision + 1),
                Current.CreatedAt,
                updatedAt,
                Current.AcceptedRemoteItems);
            return Task.FromResult(true);
        }
        public Task<bool> TryPersistOfficialAcceptanceAsync(Guid operationId, Guid accountId, int generation, long expectedRevision, RemoteItemIdentitySet acceptedRemoteItems, string observationFingerprint, DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
        {
            if (operationId != Current.OperationId || accountId != Current.AccountId ||
                generation != Current.Generation || Current.State != WebAutomationAttemptState.Submitting ||
                expectedRevision != Current.Revision)
                return Task.FromResult(false);
            Current = new(
                Current.OperationId,
                Current.AccountId,
                Current.Generation,
                Current.OwnerKind,
                WebAutomationAttemptState.ResultPending,
                null,
                Current.Baseline,
                observationFingerprint,
                false,
                checked(Current.Revision + 1),
                Current.CreatedAt,
                updatedAt,
                acceptedRemoteItems);
            return Task.FromResult(true);
        }
        public Task<bool> TryResumeFromUserHandoffAsync(Guid operationId, Guid accountId, int generation, long expectedRevision, WebAutomationAttemptState resumeStage, string? observationFingerprint, DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
        {
            if (operationId != Current.OperationId || accountId != Current.AccountId ||
                generation != Current.Generation || Current.State != WebAutomationAttemptState.UserHandoff ||
                Current.ResumeStage != resumeStage || expectedRevision != Current.Revision)
                return Task.FromResult(false);
            Current = new(
                Current.OperationId,
                Current.AccountId,
                Current.Generation,
                Current.OwnerKind,
                resumeStage,
                null,
                Current.Baseline,
                observationFingerprint ?? Current.ObservationFingerprint,
                false,
                checked(Current.Revision + 1),
                Current.CreatedAt,
                updatedAt,
                Current.AcceptedRemoteItems);
            return Task.FromResult(true);
        }
        public Task<bool> TryResolveUserHandoffAsync(Guid operationId, Guid accountId, int generation, long expectedRevision, WebAutomationAttemptState resumeStage, WebAutomationAttemptState terminalState, string? observationFingerprint, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class CallbackBroker(AttemptStore attempts, AutomationSession session) : IVisibleWebAutomationBroker
    {
        public int EnqueueCount { get; private set; }

        public async Task<VisibleWebAutomationResult<T>> EnqueueAsync<T>(
            VisibleWebAutomationRequest<T> request,
            CancellationToken cancellationToken = default)
        {
            EnqueueCount++;
            var initialStage = attempts.Current.State;
            if (initialStage == WebAutomationAttemptState.UserHandoff)
            {
                initialStage = attempts.Current.ResumeStage!.Value;
                var resumed = await attempts.TryResumeFromUserHandoffAsync(
                    request.Identity.OperationId,
                    request.Identity.AccountId,
                    request.Identity.Generation,
                    attempts.Current.Revision,
                    initialStage,
                    attempts.Current.ObservationFingerprint,
                    Now,
                    cancellationToken);
                if (!resumed) throw new InvalidOperationException("Synthetic handoff resume failed.");
            }
            var dispatcher = new InlineDispatcher();
            var coordinator = new UserHandoffCoordinator(attempts, new NoBatchCancellation(), new FixedTimeProvider(Now));
            var context = (VisibleWebAutomationContext)Activator.CreateInstance(
                typeof(VisibleWebAutomationContext),
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                [
                    request.Identity,
                    session,
                    dispatcher,
                    coordinator,
                    initialStage,
                    attempts.Current.ObservationFingerprint,
                ],
                culture: null)!;
            var value = await request.Action(session, context, cancellationToken);
            return new(VisibleWebAutomationDisposition.ActionCompleted, value, context.CurrentStage);
        }
    }

    private sealed class AutomationSession : IVisibleWebAutomationSession, ITransferAutomationSession
    {
        public List<string> Events { get; } = [];
        public IReadOnlyList<RemoteItemIdentity> CurrentItems { get; init; } =
            [new("fictional-old", "旧资源"), new("fictional-new", "新资源")];
        public bool CancelListingRead { get; init; }
        public CancellationToken UserClosed => CancellationToken.None;
        public Task ShowAsync(Uri initialUri, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ShowHandoffAsync(WebAutomationHandoffPrompt prompt, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<WebAutomationUserChoice> WaitForHandoffChoiceAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<TransferSourceListing> OpenExternalShareAsync(ShareReference share, CancellationToken cancellationToken) =>
            Record("open-share", new TransferSourceListing(1, new string('b', 64)));
        public Task EnterExtractionCodeAsync(string extractionCode, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<TransferDirectoryListing> OpenDestinationAndReadStableListingAsync(RemoteDirectory destination, CancellationToken cancellationToken) =>
            Record("open-destination", new TransferDirectoryListing(true, true, [new("fictional-old", "旧资源")], new string('c', 64)));
        public Task PersistBaselineAsync(WebAutomationBaseline baseline, string observationFingerprint, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ReturnToExternalShareAsync(ShareReference share, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task MarkSubmittingAsync(CancellationToken cancellationToken)
        {
            Events.Add("mark-submitting");
            return Task.CompletedTask;
        }
        public Task<TransferOfficialAcceptance> SaveWholeShareAsync(RemoteDirectory destination, DuplicatePolicy duplicatePolicy, CancellationToken cancellationToken) =>
            Record("submit", Acceptance());
        public Task PersistOfficialAcceptanceAsync(TransferOfficialAcceptance acceptance, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<TransferDirectoryListing> ReopenDestinationAndReadStableListingAsync(RemoteDirectory destination, CancellationToken cancellationToken) =>
            CancelListingRead
                ? Task.FromException<TransferDirectoryListing>(new OperationCanceledException(cancellationToken))
                : Task.FromResult(new TransferDirectoryListing(true, true, CurrentItems, new string('d', 64)));
        private static TransferOfficialAcceptance Acceptance() => new(
            TransferOfficialAcceptanceStatus.Accepted,
            "fictional-request",
            new RemoteItemIdentitySet([new("fictional-new", "新资源")]),
            new string('e', 64));

        private Task<T> Record<T>(string name, T value)
        {
            Events.Add(name);
            return Task.FromResult(value);
        }
    }

    private sealed class InlineDispatcher : IWebAutomationUiDispatcher
    {
        public Task<T> InvokeAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken = default) => action();
        public Task InvokeAsync(Func<Task> action, CancellationToken cancellationToken = default) => action();
    }

    private sealed class NoBatchCancellation : IWebAutomationBatchCancellation
    {
        public Task<WebAutomationBatchCancellationResult> CancelAsync(WebAutomationRequestIdentity identity, long expectedAttemptRevision, WebAutomationAttemptState expectedResumeStage, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> IsCancelledAsync(Guid batchId, WebAutomationOwnerKind ownerKind, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public IDisposable Register(Guid batchId, WebAutomationOwnerKind ownerKind, Action cancellationRequested) => new NoopDisposable();
        private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class RecordingProfilePolicy(Guid accountId) : IAccountProfilePathPolicy
    {
        public string Path { get; } = System.IO.Path.GetFullPath(System.IO.Path.Combine("profiles", accountId.ToString("D")));
        public string CanonicalRoot => System.IO.Path.GetDirectoryName(Path)!;
        public string GetAccountProfilePath(Guid requested) => requested == accountId
            ? Path
            : throw new InvalidOperationException("wrong account");
        public void EnsureSafeAccountPath(Guid requested, string exactProfilePath)
        {
            if (requested != accountId || !string.Equals(exactProfilePath, Path, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("unsafe profile");
        }
    }

    private sealed class ReadOnlyAdapter : IBaiduNetdiskAdapter
    {
        public Task<BaiduAdapterResult<BaiduAccountIdentity>> ValidateSessionAsync(AccountSession session, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<AccountCapacity>> GetCapacityAsync(AccountSession session, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<ImmutableArray<RemoteDirectory>>> ListDirectoriesAsync(AccountSession session, RemotePath path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<RemoteDirectory>> EnsureDirectoryAsync(AccountSession session, RemotePath path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<ShareMetadata>> InspectShareAsync(AccountSession session, ShareReference share, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<TransferReceipt>> SaveWholeShareAsync(AccountSession session, ShareReference share, RemoteDirectory destination, DuplicatePolicy duplicatePolicy, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<TransferConfirmation>> ConfirmTransferAsync(AccountSession session, TransferConfirmationQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
