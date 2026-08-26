using System.Collections.Immutable;
using System.Reflection;
using BaiduBatchTransfer.App.Features.Acceptance;
using BaiduBatchTransfer.App.Features.BrowserAutomation;
using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.BaiduAdapter.WebAutomation;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Infrastructure.Security;

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
        context = context with { AuthorizationBinding = CandidateBinding(context).Binding };
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
        Assert.Same(context.AuthorizationBinding, broker.CandidateBinding!.Binding);
        Assert.Equal(AdapterWriteAction.EnsureDirectory, broker.CandidateBinding.Authorization.Action);
        Assert.Equal(BaiduSelectorCatalog.TransferProtocolVersion, broker.Navigation.ProtocolVersion);
        Assert.Equal(BaiduSelectorCatalog.TransferSelectorVersion, broker.Navigation.SelectorVersion);
        Assert.Equal("fictional-user", broker.CandidateBinding.ExpectedBaiduUserId);
    }

    [Fact]
    public async Task PersistedTransferEvidenceRejectsAMismatchedStableBaiduUserIdBeforeOpeningBrowser()
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
        var wrongSession = new AccountSession(
            accountId,
            "different-baidu-user",
            [new SessionCookie("BDUSS", "fictional", ".baidu.com", "/", null, true, true)],
            Now);

        var result = await adapter.PrepareWholeShareForOperationAsync(
            context, wrongSession, Share(), Destination(), DuplicatePolicy.Skip,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(BaiduErrorKind.NotValidated, result.ErrorKind);
        Assert.Null(broker.Identity);
    }

    [Fact]
    public async Task PersistedOldTransferCapabilityCannotBeReusedByTransferV2()
    {
        var accountId = Guid.NewGuid();
        var context = new TransferOperationAutomationContext(
            Guid.NewGuid(), Guid.NewGuid(), accountId, 2, Share());
        var oldCapability = new AdapterCapabilityVersion(
            "BaiduTransfer",
            BaiduSelectorCatalog.ProtocolVersion,
            BaiduSelectorCatalog.SelectorVersion,
            "build-a");
        var attempts = new AttemptStore(Attempt(
            context,
            WebAutomationAttemptState.BaselinePersisted,
            WebAutomationBaseline.Create(["fictional-old"]),
            capabilityOverride: oldCapability));
        var broker = new RecordingBroker();
        var adapter = new WebViewBaiduNetdiskAdapter(
            new ReadOnlyAdapter(), broker, new RecordingProfilePolicy(accountId),
            new TransferAutomationFlow(), "build-a", attempts);

        var result = await adapter.PrepareWholeShareForOperationAsync(
            context, Session(accountId), Share(), Destination(), DuplicatePolicy.Skip,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(BaiduErrorKind.NotValidated, result.ErrorKind);
        Assert.Null(broker.Identity);
    }

    [Fact]
    public async Task UnboundTransferPreparationStopsBeforeBrokerSessionCreation()
    {
        var accountId = Guid.NewGuid();
        var context = new TransferOperationAutomationContext(
            Guid.NewGuid(), Guid.NewGuid(), accountId, 1, Share());
        var broker = new RecordingBroker();
        var adapter = new WebViewBaiduNetdiskAdapter(
            new ReadOnlyAdapter(), broker, new RecordingProfilePolicy(accountId),
            new TransferAutomationFlow(), "build-a",
            new AttemptStore(Attempt(context, WebAutomationAttemptState.Preparing, null)));

        var result = await adapter.PrepareWholeShareForOperationAsync(
            context, Session(accountId), Share(), Destination(), DuplicatePolicy.Skip,
            CancellationToken.None);

        Assert.Equal(BaiduErrorKind.NotValidated, result.ErrorKind);
        Assert.Null(broker.Identity);
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
    public async Task OperationBoundDirectoryPreparationCreatesExactTargetWithoutManualConfirmation()
    {
        var accountId = Guid.NewGuid();
        var context = new TransferOperationAutomationContext(
            Guid.NewGuid(), Guid.NewGuid(), accountId, 1, Share());
        context = context with { AuthorizationBinding = CandidateBinding(context).Binding };
        var attempts = new AttemptStore(Attempt(
            context, WebAutomationAttemptState.Preparing, null));
        var session = new ManualDirectorySession();
        var adapter = new WebViewBaiduNetdiskAdapter(
            new ReadOnlyAdapter(), new CallbackBroker(attempts, session),
            new RecordingProfilePolicy(accountId), new TransferAutomationFlow(),
            "build-a", attempts);
        var path = new RemotePath("/acceptance-target");

        var result = await adapter.EnsureDirectoryForOperationAsync(
            context, Session(accountId), path, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(path, result.Value!.Directory!.Path);
        Assert.Equal(1, session.CreateCount);
        Assert.Equal(1, session.CancelCount);
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
    public async Task BoundProductionSessionMapsUnavailableTransferFieldsToNotValidatedWithoutDomMutation()
    {
        var accountId = Guid.NewGuid();
        var context = new TransferOperationAutomationContext(
            Guid.NewGuid(), Guid.NewGuid(), accountId, 1, Share());
        context = context with { AuthorizationBinding = CandidateBinding(context).Binding };
        var attempts = new AttemptStore(Attempt(
            context, WebAutomationAttemptState.Preparing, null));
        var identity = new WebAutomationRequestIdentity(
            context.OperationId, context.BatchId, accountId, context.Generation,
            WebAutomationOwnerKind.Transfer);
        var executor = new DenyingCandidateExecutor();
        await using var session = new WebView2AutomationSession(
            identity, CandidateBinding(context), executor, CancellationToken.None);
        var adapter = new WebViewBaiduNetdiskAdapter(
            new ReadOnlyAdapter(), new CallbackBroker(attempts, session),
            new RecordingProfilePolicy(accountId), new TransferAutomationFlow(),
            "build-a", attempts);

        var result = await adapter.PrepareWholeShareForOperationAsync(
            context, Session(accountId), Share(), Destination(), DuplicatePolicy.Skip,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(BaiduErrorKind.NotValidated, result.ErrorKind);
        Assert.Equal(SubmissionAcceptance.DefinitelyNotAccepted, result.SubmissionAcceptance);
        Assert.Equal(0, executor.MutateCount);
        Assert.Equal(WebAutomationAttemptState.Preparing, attempts.Current.State);
    }

    [Fact]
    public async Task RealQueueCreatesOnlyBoundProductionSessionAndKeepsUnavailableFieldsFailClosed()
    {
        var accountId = Guid.NewGuid();
        var context = new TransferOperationAutomationContext(
            Guid.NewGuid(), Guid.NewGuid(), accountId, 1, Share());
        var candidate = CandidateBinding(context);
        var boundContext = context with { AuthorizationBinding = candidate.Binding };
        var attempts = new AttemptStore(Attempt(
            boundContext, WebAutomationAttemptState.Preparing, null));
        var executor = new DenyingCandidateExecutor();
        var factory = new HeadlessProductionSessionFactory(executor);
        var profileRoot = Path.Combine(Path.GetTempPath(), "bbt-transfer-binding", Guid.NewGuid().ToString("N"));
        var profiles = new FileSystemAccountProfilePathPolicy(profileRoot);
        Directory.CreateDirectory(profiles.GetAccountProfilePath(accountId));
        var cancellation = new NoBatchCancellation();
        await using var queue = new VisibleWebAutomationQueue(
            factory,
            new InlineDispatcher(),
            profiles,
            new WebView2ProfileCloser(),
            new UserHandoffCoordinator(attempts, cancellation, new FixedTimeProvider(Now)),
            cancellation,
            new AlwaysBoundOwnershipStore(),
            new ProductionWebAutomationNavigationAuthorizer(
                new ExactNavigationSource(new(
                    "BaiduTransfer", accountId, context.OperationId,
                    context.Generation, context.BatchId))),
            new VisibleWebAutomationQueueOptions
            {
                ActionStopTimeout = TimeSpan.FromSeconds(1),
                SessionTimeout = TimeSpan.FromSeconds(5),
                WindowCloseTimeout = TimeSpan.FromSeconds(1),
            });
        var adapter = new WebViewBaiduNetdiskAdapter(
            new ReadOnlyAdapter(), queue, profiles, new TransferAutomationFlow(),
            "build-a", attempts);

        var unbound = await adapter.PrepareWholeShareForOperationAsync(
            context, Session(accountId), Share(), Destination(), DuplicatePolicy.Skip,
            CancellationToken.None);
        var bound = await adapter.PrepareWholeShareForOperationAsync(
            boundContext, Session(accountId), Share(), Destination(), DuplicatePolicy.Skip,
            CancellationToken.None);

        Assert.Equal(BaiduErrorKind.NotValidated, unbound.ErrorKind);
        Assert.Equal(BaiduErrorKind.NotValidated, bound.ErrorKind);
        Assert.Equal(1, factory.BoundCreatedCount);
        Assert.Equal(0, factory.UnboundCreatedCount);
        Assert.Same(candidate.Binding, factory.LastBinding!.Binding);
        Assert.Equal(0, executor.MutateCount);
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
            AcceptedItems(),
            includeTransferMapping: true));
        var broker = new RecordingBroker();
        var adapter = new WebViewBaiduNetdiskAdapter(
            new ReadOnlyAdapter(), broker, new RecordingProfilePolicy(accountId),
            new TransferAutomationFlow(), "build-a", attempts,
            recoveryAuthorization: new RecoveryAuthorization(attempts));

        var result = await adapter.ConfirmExactTransferForOperationAsync(
            context,
            Session(accountId),
            Query(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(TransferConfirmationState.ExactPresent, result.Value!.State);
        Assert.Equal(context.OperationId, broker.Identity!.OperationId);
        Assert.Equal(context.Generation, broker.Identity.Generation);
        Assert.Null(broker.CandidateBinding);
        Assert.NotNull(broker.TransferRecoveryBinding);
        Assert.Equal(Destination(), broker.TransferRecoveryBinding!.Target);
        Assert.DoesNotContain(context.SourceShare.OriginalUrl,
            broker.Navigation!.OfficialUri.AbsoluteUri, StringComparison.Ordinal);
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
        context = context with { AuthorizationBinding = CandidateBinding(context).Binding };
        var attempts = new AttemptStore(Attempt(
            context,
            WebAutomationAttemptState.BaselinePersisted,
            WebAutomationBaseline.Create(["fictional-old"])));
        var automation = new AutomationSession();
        var broker = new CallbackBroker(attempts, automation);
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
    public async Task RecoveryWithoutOfficialIdentityMappingStaysResultPending()
    {
        var accountId = Guid.NewGuid();
        var context = new TransferOperationAutomationContext(
            Guid.NewGuid(), Guid.NewGuid(), accountId, 2, Share());
        var attempts = new AttemptStore(Attempt(
            context,
            WebAutomationAttemptState.ResultPending,
            WebAutomationBaseline.Create(["fictional-old"]),
            AcceptedItems()));
        var automation = new AutomationSession();
        var broker = new CallbackBroker(attempts, automation);
        var adapter = new WebViewBaiduNetdiskAdapter(
            new ReadOnlyAdapter(), broker, new RecordingProfilePolicy(accountId),
            new TransferAutomationFlow(), "build-a", attempts,
            recoveryAuthorization: new RecoveryAuthorization(attempts));

        var result = await adapter.ConfirmExactTransferForOperationAsync(
            context, Session(accountId), Query(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(TransferConfirmationState.Ambiguous, result.Value!.State);
        Assert.Equal(WebAutomationAttemptState.ResultPending, attempts.Current.State);
        Assert.Equal(["reopen-destination"], automation.Events);
    }

    [Fact]
    public async Task RecoveryWithoutOfficialIdentityMappingEmitsNoAcceptanceEvidence()
    {
        var accountId = Guid.NewGuid();
        var context = new TransferOperationAutomationContext(
            Guid.NewGuid(), Guid.NewGuid(), accountId, 2, Share(), IsRecovery: true);
        var attempts = new AttemptStore(Attempt(
            context,
            WebAutomationAttemptState.ResultPending,
            WebAutomationBaseline.Create(["fictional-old"]),
            AcceptedItems()));
        var evidence = new RecordingEvidenceSink();
        var adapter = new WebViewBaiduNetdiskAdapter(
            new ReadOnlyAdapter(), new CallbackBroker(attempts, new AutomationSession()),
            new RecordingProfilePolicy(accountId), new TransferAutomationFlow(), "build-a",
            attempts, evidence, new RecoveryAuthorization(attempts));

        var result = await adapter.ConfirmExactTransferForOperationAsync(
            context, Session(accountId), Query(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(TransferConfirmationState.Ambiguous, result.Value!.State);
        Assert.Empty(evidence.Remote);
        Assert.Empty(evidence.Recovery);
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
            new TransferAutomationFlow(), "build-a", attempts,
            recoveryAuthorization: new RecoveryAuthorization(attempts));

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
            new TransferAutomationFlow(), "build-a", attempts,
            recoveryAuthorization: new RecoveryAuthorization(attempts));

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
            Guid.NewGuid(), Guid.NewGuid(), accountId, 2, Share(), IsRecovery: true);
        var attempts = new AttemptStore(Attempt(
            context,
            WebAutomationAttemptState.Succeeded,
            WebAutomationBaseline.Create(["fictional-old"]),
            AcceptedItems()));
        var broker = new RecordingBroker();
        var evidence = new RecordingEvidenceSink();
        var adapter = new WebViewBaiduNetdiskAdapter(
            new ReadOnlyAdapter(), broker, new RecordingProfilePolicy(accountId),
            new TransferAutomationFlow(), "build-a", attempts, evidence);

        var result = await adapter.ConfirmExactTransferForOperationAsync(
            context, Session(accountId), Query(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(AcceptedItems(), result.Value!.RemoteItems);
        Assert.Null(broker.Identity);
        Assert.Single(evidence.Remote);
        Assert.Empty(evidence.Recovery);
    }

    [Fact]
    public async Task AcceptanceTelemetryFailureCannotChangePersistedExactTransferResult()
    {
        var accountId = Guid.NewGuid();
        var context = new TransferOperationAutomationContext(
            Guid.NewGuid(), Guid.NewGuid(), accountId, 2, Share(), IsRecovery: true);
        var attempts = new AttemptStore(Attempt(
            context,
            WebAutomationAttemptState.Succeeded,
            WebAutomationBaseline.Create(["fictional-old"]),
            AcceptedItems()));
        var adapter = new WebViewBaiduNetdiskAdapter(
            new ReadOnlyAdapter(), new RecordingBroker(), new RecordingProfilePolicy(accountId),
            new TransferAutomationFlow(), "build-a", attempts, new ThrowingEvidenceSink());

        var result = await adapter.ConfirmExactTransferForOperationAsync(
            context, Session(accountId), Query(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(AcceptedItems(), result.Value!.RemoteItems);
    }

    [Fact]
    public async Task PreparingUserHandoffCanBeRequeuedAfterRestart()
    {
        var accountId = Guid.NewGuid();
        var context = new TransferOperationAutomationContext(
            Guid.NewGuid(), Guid.NewGuid(), accountId, 2, Share());
        context = context with { AuthorizationBinding = CandidateBinding(context).Binding };
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
        context = context with { AuthorizationBinding = CandidateBinding(context).Binding };
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
    public async Task ResultPendingUserHandoffWithoutOfficialIdentityMappingStaysAmbiguous()
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
            new RecordingProfilePolicy(accountId), new TransferAutomationFlow(), "build-a", attempts,
            recoveryAuthorization: new RecoveryAuthorization(attempts));

        var result = await adapter.ConfirmExactTransferForOperationAsync(
            context, Session(accountId), Query(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(TransferConfirmationState.Ambiguous, result.Value!.State);
        Assert.Equal(WebAutomationAttemptState.ResultPending, attempts.Current.State);
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
        context = context with { AuthorizationBinding = CandidateBinding(context).Binding };
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
        WebAutomationAttemptState? resumeStage = null,
        bool includeTransferMapping = false,
        AdapterCapabilityVersion? capabilityOverride = null)
    {
        var capability = capabilityOverride ?? new AdapterCapabilityVersion(
            "BaiduTransfer", BaiduSelectorCatalog.TransferProtocolVersion,
            BaiduSelectorCatalog.TransferSelectorVersion, "build-a");
        var source = baseline is null ? null : new TransferSourceEvidence(
            new RemoteItemIdentitySet([new("fictional-source", "来源资源")]),
            1, true, true, "source-generation", "fictional-user", capability,
            new string('b', 64));
        var target = baseline is null ? null : new TransferTargetPreparationEvidence(
            Destination().RemoteId, Destination().Path, baseline.Items.Length,
            true, true, "target-generation", "fictional-user", baseline,
            new string('c', 64));
        var effectiveState = state == WebAutomationAttemptState.UserHandoff ? resumeStage : state;
        var submission = effectiveState == WebAutomationAttemptState.ResultPending &&
                         source is not null && target is not null
            ? new TransferSubmissionEvidence(
                source.Items.Fingerprint, target.TargetExactId, target.Baseline.Fingerprint,
                source.BaiduUserId, source.Capability, Now.AddMinutes(-1))
            : null;
        var mapping = includeTransferMapping && source is not null && acceptedRemoteItems is not null
            ? new TransferIdentityMapping([
                new TransferIdentityMappingEntry(
                    source.Items.Items[0].ExactId,
                    acceptedRemoteItems.Items[0].ExactId),
            ])
            : null;
        return new WebAutomationAttemptRecord(
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
            acceptedRemoteItems,
            source,
            target,
            submission,
            mapping is null ? null : "request-1",
            mapping);
    }

    private static RemoteItemIdentitySet AcceptedItems() =>
        new([new("fictional-new", "新资源")]);

    private static BaiduCandidateSessionBinding CandidateBinding(
        TransferOperationAutomationContext context)
    {
        var capability = new AdapterCapabilityVersion(
            "BaiduTransfer", BaiduSelectorCatalog.TransferProtocolVersion,
            BaiduSelectorCatalog.TransferSelectorVersion, "build-a");
        var authorization = new AdapterWriteAuthorizationRequest(
            capability, context.AccountId, context.OperationId, context.Generation,
            context.BatchId, AdapterWriteAction.EnsureDirectory, context.IsRecovery);
        return new(
            authorization,
            new AdapterWriteAuthorizationBinding(
                AdapterWriteAuthorizationSource.ConsumedLease,
                capability,
                context.AccountId,
                context.OperationId,
                context.Generation,
                context.BatchId,
                Now,
                Now.AddMinutes(5),
                context.IsRecovery,
                null),
            "fictional-user");
    }

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
        public BaiduCandidateSessionBinding? CandidateBinding { get; private set; }
        public ReadOnlyTransferRecoveryBinding? TransferRecoveryBinding { get; private set; }

        public Task<VisibleWebAutomationResult<T>> EnqueueAsync<T>(
            VisibleWebAutomationRequest<T> request,
            CancellationToken cancellationToken = default)
        {
            Identity = request.Identity;
            ProfilePath = request.ProfilePath;
            Navigation = request.Navigation;
            CandidateBinding = request.CandidateBinding;
            TransferRecoveryBinding = request.TransferRecoveryBinding;
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

    private sealed class RecordingEvidenceSink : IAcceptanceOperationEvidenceSink
    {
        public List<(AdapterWriteAuthorizationRequest Request, string Fingerprint)> Remote { get; } = [];
        public List<(AdapterWriteAuthorizationRequest Request, string Fingerprint)> Recovery { get; } = [];

        public Task RecordRemoteResultAsync(
            AdapterWriteAuthorizationRequest request,
            string exactEvidenceFingerprint,
            CancellationToken cancellationToken = default)
        {
            Remote.Add((request, exactEvidenceFingerprint));
            return Task.CompletedTask;
        }

        public Task RecordRecoveryAsync(
            AdapterWriteAuthorizationRequest request,
            string exactEvidenceFingerprint,
            CancellationToken cancellationToken = default)
        {
            Recovery.Add((request, exactEvidenceFingerprint));
            return Task.CompletedTask;
        }
    }

    private sealed class DenyingCandidateExecutor : IBaiduCandidateDomExecutor
    {
        public int MutateCount { get; private set; }

        public Task<BaiduCandidateDomResult> ObserveAsync(
            BaiduCandidateDomRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(BaiduCandidateDomResult.Denied(
                BaiduAutomationDenialReason.FieldEvidenceNotValidated));

        public Task<BaiduCandidateDomResult> MutateAsync(
            BaiduCandidateDomRequest request,
            BaiduCandidateDomObservation observation,
            CancellationToken cancellationToken = default)
        {
            MutateCount++;
            return Task.FromResult(BaiduCandidateDomResult.Success(observation));
        }
    }

    private sealed class ThrowingEvidenceSink : IAcceptanceOperationEvidenceSink
    {
        public Task RecordRemoteResultAsync(
            AdapterWriteAuthorizationRequest request,
            string exactEvidenceFingerprint,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("simulated acceptance presenter failure");

        public Task RecordRecoveryAsync(
            AdapterWriteAuthorizationRequest request,
            string exactEvidenceFingerprint,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("simulated acceptance presenter failure");
    }

    private sealed class AttemptStore(WebAutomationAttemptRecord attempt) : IWebAutomationAttemptStore
    {
        public WebAutomationAttemptRecord Current { get; private set; } = attempt;
        public List<TransferDirectoryMutationRecord> Mutations { get; } = [];

        public Task<WebAutomationAttemptRecord?> GetAsync(Guid operationId, Guid accountId, int generation, CancellationToken cancellationToken = default) =>
            Task.FromResult<WebAutomationAttemptRecord?>(
                operationId == Current.OperationId && accountId == Current.AccountId && generation == Current.Generation
                    ? Current
                    : null);
        public Task<bool> BeginAsync(WebAutomationAttemptRecord value, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
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
                Current.AcceptedRemoteItems,
                Current.SourceEvidence,
                Current.TargetPreparationEvidence,
                Current.SubmissionEvidence,
                Current.OfficialRequestId,
                Current.TransferIdentityMapping);
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
                acceptedRemoteItems,
                Current.SourceEvidence,
                Current.TargetPreparationEvidence,
                Current.SubmissionEvidence,
                Current.OfficialRequestId,
                Current.TransferIdentityMapping);
            return Task.FromResult(true);
        }
        public Task<bool> TryPersistTransferSourceEvidenceAsync(
            Guid operationId, Guid accountId, int generation, long expectedRevision,
            TransferSourceEvidence evidence, DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            if (operationId != Current.OperationId || accountId != Current.AccountId ||
                generation != Current.Generation || Current.State != WebAutomationAttemptState.Preparing ||
                expectedRevision != Current.Revision || Current.SourceEvidence is not null)
                return Task.FromResult(false);
            Current = new(
                Current.OperationId, Current.AccountId, Current.Generation, Current.OwnerKind,
                Current.State, Current.ResumeStage, Current.Baseline, evidence.ObservationFingerprint,
                Current.IsWaitingForUser, checked(Current.Revision + 1), Current.CreatedAt, updatedAt,
                Current.AcceptedRemoteItems, evidence,
                Current.TargetPreparationEvidence,
                Current.SubmissionEvidence,
                Current.OfficialRequestId,
                Current.TransferIdentityMapping);
            return Task.FromResult(true);
        }
        public Task<bool> TryPersistTransferTargetPreparationAsync(
            Guid operationId, Guid accountId, int generation, long expectedRevision,
            TransferTargetPreparationEvidence evidence, DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            if (operationId != Current.OperationId || accountId != Current.AccountId ||
                generation != Current.Generation || Current.State != WebAutomationAttemptState.Preparing ||
                expectedRevision != Current.Revision || Current.SourceEvidence is null)
                return Task.FromResult(false);
            Current = new(
                Current.OperationId, Current.AccountId, Current.Generation, Current.OwnerKind,
                WebAutomationAttemptState.BaselinePersisted, null, evidence.Baseline,
                evidence.ObservationFingerprint, false, checked(Current.Revision + 1),
                Current.CreatedAt, updatedAt, Current.AcceptedRemoteItems,
                Current.SourceEvidence, evidence, Current.SubmissionEvidence,
                Current.OfficialRequestId, Current.TransferIdentityMapping);
            return Task.FromResult(true);
        }
        public Task<bool> TryPersistTransferSubmissionAsync(
            Guid operationId, Guid accountId, int generation, long expectedRevision,
            TransferSubmissionEvidence evidence, DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            if (operationId != Current.OperationId || accountId != Current.AccountId ||
                generation != Current.Generation || Current.State != WebAutomationAttemptState.BaselinePersisted ||
                expectedRevision != Current.Revision || Current.SourceEvidence is null ||
                Current.TargetPreparationEvidence is null)
                return Task.FromResult(false);
            Current = new(
                Current.OperationId, Current.AccountId, Current.Generation, Current.OwnerKind,
                WebAutomationAttemptState.Submitting, null, Current.Baseline,
                Current.ObservationFingerprint, false, checked(Current.Revision + 1),
                Current.CreatedAt, updatedAt, Current.AcceptedRemoteItems,
                Current.SourceEvidence, Current.TargetPreparationEvidence, evidence,
                Current.OfficialRequestId, Current.TransferIdentityMapping);
            return Task.FromResult(true);
        }
        public Task<bool> TryPersistTransferAcceptanceAsync(
            Guid operationId, Guid accountId, int generation, long expectedRevision,
            string officialRequestId, TransferIdentityMapping mapping,
            string observationFingerprint, DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            if (operationId != Current.OperationId || accountId != Current.AccountId ||
                generation != Current.Generation || Current.State != WebAutomationAttemptState.Submitting ||
                expectedRevision != Current.Revision || Current.SourceEvidence is null ||
                Current.SubmissionEvidence is null || !mapping.Covers(Current.SourceEvidence.Items))
                return Task.FromResult(false);
            var accepted = new RemoteItemIdentitySet(mapping.Entries.Select(entry =>
                new RemoteItemIdentity(entry.DestinationExactId, null)));
            Current = new(
                Current.OperationId, Current.AccountId, Current.Generation, Current.OwnerKind,
                WebAutomationAttemptState.ResultPending, null, Current.Baseline,
                observationFingerprint, false, checked(Current.Revision + 1),
                Current.CreatedAt, updatedAt, accepted, Current.SourceEvidence,
                Current.TargetPreparationEvidence, Current.SubmissionEvidence,
                officialRequestId, mapping);
            return Task.FromResult(true);
        }
        public Task<IReadOnlyList<TransferDirectoryMutationRecord>> GetDirectoryMutationsAsync(
            Guid operationId, Guid accountId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TransferDirectoryMutationRecord>>(Mutations.ToArray());
        public Task<bool> TryBeginDirectoryMutationAsync(
            TransferDirectoryMutationRecord mutation, DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            Mutations.Add(mutation);
            return Task.FromResult(true);
        }
        public Task<bool> TryResolveDirectoryMutationAsync(
            TransferDirectoryMutationRecord expected, TransferDirectoryMutationState nextState,
            string? childExactId, DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            var index = Mutations.IndexOf(expected);
            if (index < 0) return Task.FromResult(false);
            Mutations[index] = new(
                expected.OperationId, expected.AccountId, expected.Generation, expected.SegmentOrdinal,
                expected.ParentExactId, childExactId, expected.CanonicalChildPath,
                expected.ListingGenerationToken, expected.BaiduUserId, expected.Capability,
                nextState, expected.OwningAttemptRevision);
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
                Current.AcceptedRemoteItems,
                Current.SourceEvidence,
                Current.TargetPreparationEvidence,
                Current.SubmissionEvidence,
                Current.OfficialRequestId,
                Current.TransferIdentityMapping);
            return Task.FromResult(true);
        }
        public Task<bool> TryResolveUserHandoffAsync(Guid operationId, Guid accountId, int generation, long expectedRevision, WebAutomationAttemptState resumeStage, WebAutomationAttemptState terminalState, string? observationFingerprint, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class RecoveryAuthorization(AttemptStore attempts)
        : ITransferRecoveryAuthorizationSource
    {
        public Task<TransferDirectoryRecoveryAuthorization?> TryAuthorizeDirectoryMutationAsync(
            TransferDirectoryRecoveryAuthorizationRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<TransferDirectoryRecoveryAuthorization?>(null);

        public Task<ReadOnlyTransferRecoveryBinding?> TryAuthorizeSubmissionRecoveryAsync(
            TransferSubmissionRecoveryAuthorizationRequest request,
            CancellationToken cancellationToken = default)
        {
            var current = attempts.Current;
            if (current.SourceEvidence is null || current.TargetPreparationEvidence is null ||
                current.SubmissionEvidence is null)
                return Task.FromResult<ReadOnlyTransferRecoveryBinding?>(null);
            return Task.FromResult<ReadOnlyTransferRecoveryBinding?>(new(
                request.OperationId, request.BatchId, request.AccountId, request.Generation,
                request.Target, request.BaiduUserId,
                current.SourceEvidence.Items.Fingerprint,
                current.TargetPreparationEvidence.Baseline.Fingerprint,
                current.SubmissionEvidence.MarkedSubmittingAtUtc,
                current.TransferIdentityMapping?.Fingerprint,
                request.Capability,
                DateTimeOffset.UtcNow.AddMinutes(5)));
        }
    }

    private sealed class CallbackBroker(AttemptStore attempts, IVisibleWebAutomationSession session) : IVisibleWebAutomationBroker
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

    private sealed class ManualDirectorySession :
        IVisibleWebAutomationSession,
        ITransferDirectoryPreparationSession
    {
        public int CreateCount { get; private set; }
        public int CancelCount { get; private set; }
        public CancellationToken UserClosed => CancellationToken.None;

        public Task OpenSourceAsync(ShareReference share, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task EnterExtractionCodeAsync(string extractionCode, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<TransferSourceListing> ReadCompleteSourceAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new TransferSourceListing(new TransferSourceEvidence(
                new RemoteItemIdentitySet([new("source", "来源")]), 1, true, true,
                "source-generation", "fictional-user", new AdapterCapabilityVersion(
                    "BaiduTransfer", BaiduSelectorCatalog.TransferProtocolVersion,
                    BaiduSelectorCatalog.TransferSelectorVersion, "build-a"), new string('a', 64))));
        public Task<TransferDirectoryListing> OpenDestinationPickerAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Listing(rootHasChild: false));
        public Task<TransferDirectoryListing> OpenVerifiedRootAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Listing(rootHasChild: false));
        public Task<TransferDirectoryListing> EnterDirectoryAsync(RemoteDirectory directory, CancellationToken cancellationToken) =>
            Task.FromResult(TransferDirectoryListing.Exact(
                directory, [directory.RemoteId], [], 0, true, true,
                "directory-generation", "fictional-user", new string('b', 64)));
        public Task BeginCreateDirectoryAsync(string segment, CancellationToken cancellationToken)
        {
            CreateCount++;
            return Task.CompletedTask;
        }
        public Task ConfirmDirectoryCreationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<TransferDirectoryListing> ReadCurrentDirectoryAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(Listing(rootHasChild: true));
        }
        public Task CancelDestinationPickerAsync(CancellationToken cancellationToken)
        {
            CancelCount++;
            return Task.CompletedTask;
        }
        public Task<TransferDirectoryListing> ReadExactParentForRecoveryAsync(
            RemoteDirectory parent, IReadOnlyList<string> expectedAncestors,
            TransferDirectoryRecoveryAuthorization authorization,
            CancellationToken cancellationToken) => Task.FromResult(Listing(rootHasChild: true));

        private static TransferDirectoryListing Listing(bool rootHasChild) =>
            TransferDirectoryListing.Exact(
                new RemoteDirectory("root", new RemotePath("/")), [],
                rootHasChild
                    ? [new TransferRemoteItem(new RemoteItemIdentity("target", "acceptance-target"),
                        TransferRemoteItemKind.Folder, "acceptance-target")]
                    : [],
                rootHasChild ? 1 : 0, true, true, "directory-generation",
                "fictional-user", new string('b', 64));

        public Task ShowAsync(Uri initialUri, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ShowHandoffAsync(WebAutomationHandoffPrompt prompt, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<WebAutomationUserChoice> WaitForHandoffChoiceAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
            Record("open-share", new TransferSourceListing(new TransferSourceEvidence(
                new RemoteItemIdentitySet([new("fictional-source", "来源资源")]),
                1,
                reachedEnd: true,
                isStable: true,
                "source-generation",
                "fictional-user",
                new AdapterCapabilityVersion(
                    "BaiduTransfer", BaiduSelectorCatalog.TransferProtocolVersion,
                    BaiduSelectorCatalog.TransferSelectorVersion, "build-a"),
                new string('b', 64))));
        public async Task<TransferSourceListing> OpenAndReadSourceForSubmissionAsync(
            ShareReference share, CancellationToken cancellationToken)
        {
            var source = await OpenExternalShareAsync(share, cancellationToken);
            if (share.ExtractionCode is not null)
                await EnterExtractionCodeAsync(share.ExtractionCode, cancellationToken);
            return source;
        }
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
        public Task PrepareWholeShareSubmissionAsync(
            RemoteDirectory destination, TransferSourceEvidence source,
            DuplicatePolicy duplicatePolicy, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<TransferOfficialAcceptance> SaveWholeShareExactlyOnceAsync(
            RemoteDirectory destination, TransferSourceEvidence source,
            DuplicatePolicy duplicatePolicy, CancellationToken cancellationToken) =>
            SaveWholeShareAsync(destination, duplicatePolicy, cancellationToken);
        public Task PersistOfficialAcceptanceAsync(TransferOfficialAcceptance acceptance, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<TransferDirectoryListing> ReopenDestinationAndReadStableListingAsync(RemoteDirectory destination, CancellationToken cancellationToken)
        {
            if (CancelListingRead)
                return Task.FromException<TransferDirectoryListing>(new OperationCanceledException(cancellationToken));

            return Record("reopen-destination", TransferDirectoryListing.Exact(
                destination,
                [destination.RemoteId],
                CurrentItems.Select(item => new TransferRemoteItem(
                    item, TransferRemoteItemKind.File,
                    item.DisplayedTopLevelName ?? item.ExactId)).ToArray(),
                CurrentItems.Count,
                true,
                true,
                "target-current-generation",
                "fictional-user",
                new string('d', 64)));
        }
        private static TransferOfficialAcceptance Acceptance() => new(
            TransferOfficialAcceptanceStatus.Accepted,
            "fictional-request",
            new RemoteItemIdentitySet([new("fictional-new", "新资源")]),
            new TransferIdentityMapping([
                new TransferIdentityMappingEntry("fictional-source", "fictional-new"),
            ]),
            new string('e', 64),
            Destination().RemoteId);

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

    private sealed class HeadlessProductionSessionFactory(IBaiduCandidateDomExecutor executor)
        : IBoundVisibleWebAutomationSessionFactory
    {
        public int BoundCreatedCount { get; private set; }
        public int UnboundCreatedCount { get; private set; }
        public BaiduCandidateSessionBinding? LastBinding { get; private set; }

        public Task<IVisibleWebAutomationSession> CreateBoundAsync(
            WebAutomationRequestIdentity identity,
            string profilePath,
            BaiduCandidateSessionBinding binding,
            CancellationToken cancellationToken = default)
        {
            binding.ValidateIdentity(identity);
            BoundCreatedCount++;
            LastBinding = binding;
            return Task.FromResult<IVisibleWebAutomationSession>(
                new WebView2AutomationSession(identity, binding, executor, CancellationToken.None));
        }

        public Task<IVisibleWebAutomationSession> CreateAsync(
            WebAutomationRequestIdentity identity,
            string profilePath,
            CancellationToken cancellationToken = default)
        {
            UnboundCreatedCount++;
            throw new InvalidOperationException("An unbound production session must not be created.");
        }
    }

    private sealed class ExactNavigationSource(BaiduAcceptanceOperationScope expected)
        : IAcceptanceNavigationAuthorizationSource
    {
        public bool IsAuthorized(BaiduNavigationAuthorizationRequest request) =>
            request.AcceptanceScope == expected;
    }

    private sealed class AlwaysBoundOwnershipStore : IWebAutomationRequestOwnershipStore
    {
        public Task<bool> IsBoundAsync(
            Guid operationId,
            Guid batchId,
            Guid accountId,
            int generation,
            WebAutomationOwnerKind ownerKind,
            CancellationToken cancellationToken = default) => Task.FromResult(true);
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
