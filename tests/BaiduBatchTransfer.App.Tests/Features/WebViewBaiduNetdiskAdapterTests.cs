using System.Collections.Immutable;
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
    public async Task OperationBoundSaveQueuesExactAuthoritativeIdentityAndCanonicalProfile()
    {
        var accountId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        var broker = new RecordingBroker();
        var profiles = new RecordingProfilePolicy(accountId);
        var adapter = new WebViewBaiduNetdiskAdapter(
            new ReadOnlyAdapter(), broker, profiles, new TransferAutomationFlow(), "build-a");
        var context = new TransferOperationAutomationContext(
            operationId, batchId, accountId, 3, Share());

        var result = await adapter.SaveWholeShareForOperationAsync(
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

        var result = await adapter.SaveWholeShareForOperationAsync(
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
        var attempts = new AttemptStore(Attempt(context, WebAutomationAttemptState.ResultPending, baseline));
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
        WebAutomationBaseline? baseline) => new(
            context.OperationId,
            context.AccountId,
            context.Generation,
            WebAutomationOwnerKind.Transfer,
            state,
            null,
            baseline,
            new string('a', 64),
            false,
            2,
            Now.AddMinutes(-2),
            Now.AddMinutes(-1));

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
            object value = typeof(T) == typeof(BaiduAdapterResult<TransferReceipt>)
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
        public Task<WebAutomationAttemptRecord?> GetAsync(Guid operationId, Guid accountId, int generation, CancellationToken cancellationToken = default) =>
            Task.FromResult<WebAutomationAttemptRecord?>(
                operationId == attempt.OperationId && accountId == attempt.AccountId && generation == attempt.Generation
                    ? attempt
                    : null);
        public Task<bool> BeginAsync(WebAutomationAttemptRecord value, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Page<WebAutomationAttemptRecord>> GetRecoveryPageAsync(PageQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> TryPersistBaselineAsync(Guid operationId, Guid accountId, int generation, long expectedRevision, WebAutomationBaseline baseline, string observationFingerprint, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> TryAdvanceAsync(Guid operationId, Guid accountId, int generation, WebAutomationAttemptState expectedState, long expectedRevision, WebAutomationAttemptState nextState, string? observationFingerprint, bool isWaitingForUser, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> TryResumeFromUserHandoffAsync(Guid operationId, Guid accountId, int generation, long expectedRevision, WebAutomationAttemptState resumeStage, string? observationFingerprint, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> TryResolveUserHandoffAsync(Guid operationId, Guid accountId, int generation, long expectedRevision, WebAutomationAttemptState resumeStage, WebAutomationAttemptState terminalState, string? observationFingerprint, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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
