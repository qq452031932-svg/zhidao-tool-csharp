using BaiduBatchTransfer.App.Features.BrowserAutomation;
using BaiduBatchTransfer.BaiduAdapter.WebAutomation;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class WebViewRemoteResourcePickerTests
{
    private static readonly Guid AccountA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AccountB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void ProductionWindowHasApplicationConfirmAndCancelButNoExecutableDomScript()
    {
        var root = FindRepositoryRoot();
        var directory = Path.Combine(root, "src", "BaiduBatchTransfer.App", "Features", "BrowserAutomation");
        var xaml = File.ReadAllText(Path.Combine(directory, "WebAutomationWindow.xaml"));
        var session = File.ReadAllText(Path.Combine(directory, "WebView2AutomationSession.cs"));

        Assert.Contains("确认选择", xaml, StringComparison.Ordinal);
        Assert.Contains("取消选择", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"确认选择\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IRemoteResourcePickerSession", session, StringComparison.Ordinal);
        Assert.Contains("CatalogNotValidated", session, StringComparison.Ordinal);
        Assert.DoesNotContain("ExecuteScriptAsync", session, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmedSelectionUsesBoundIdentityCanonicalProfileAndNotValidatedNavigation()
    {
        var session = Session(AccountA);
        var profiles = new RecordingProfilePolicy([AccountA]);
        var broker = new RecordingBroker([
            new PickerSession(
                RemoteResourcePickerDecision.ConfirmSelection,
                [Page(AccountA, "opaque-apk", "应用.apk")]),
        ]);
        var sessions = new SessionStore(session);
        var picker = Picker(broker, profiles, sessions);

        var result = await picker.PickAsync(AccountA);

        var selection = Assert.Single(result);
        Assert.Equal(AccountA, selection.AccountId);
        Assert.Equal("opaque-apk", selection.RemoteItems.Items.Single().ExactId);
        var request = Assert.Single(broker.Requests);
        Assert.Equal(Identity(session), request.Identity);
        Assert.Equal(profiles.GetAccountProfilePath(AccountA), request.ProfilePath);
        Assert.Equal(new Uri("https://pan.baidu.com/disk/main"), request.Navigation.OfficialUri);
        Assert.Equal(BaiduAutomationAction.OpenOfficialUri, request.Navigation.Action);
        Assert.False(request.Navigation.Acceptance.IsAccepted);
        Assert.Equal(BaiduProtocolValidationStatus.NotValidated,
            request.Navigation.FieldEvidenceValidationStatus);
        Assert.Equal(RemoteSelectionSessionState.Confirmed, Assert.Single(sessions.Completions));
    }

    [Fact]
    public async Task SwitchingAccountUsesEachAccountsBoundIdentityProfileAndOwnedResult()
    {
        var sessionA = Session(AccountA);
        var sessionB = Session(AccountB);
        var profiles = new RecordingProfilePolicy([AccountA, AccountB]);
        var broker = new RecordingBroker([
            new PickerSession(
                RemoteResourcePickerDecision.ConfirmSelection,
                [Page(AccountA, "opaque-a", "账号A资源")]),
            new PickerSession(
                RemoteResourcePickerDecision.ConfirmSelection,
                [Page(AccountB, "opaque-b", "账号B资源")]),
        ]);
        var picker = Picker(broker, profiles, new SessionStore(sessionA, sessionB));

        var first = await picker.PickAsync(AccountA);
        var second = await picker.PickAsync(AccountB);

        Assert.Equal(AccountA, Assert.Single(first).AccountId);
        Assert.Equal(AccountB, Assert.Single(second).AccountId);
        Assert.Equal([AccountA, AccountB], broker.Requests.Select(request => request.Identity.AccountId));
        Assert.Equal(
            [profiles.GetAccountProfilePath(AccountA), profiles.GetAccountProfilePath(AccountB)],
            broker.Requests.Select(request => request.ProfilePath));
    }

    [Fact]
    public async Task BoundIdentityForAnotherOwnerIsRejectedBeforeQueueing()
    {
        var broker = new RecordingBroker([]);
        var picker = Picker(
            broker,
            new RecordingProfilePolicy([AccountA, AccountB]),
            new SessionStore(Session(AccountB)));

        await Assert.ThrowsAsync<InvalidOperationException>(() => picker.PickAsync(AccountA));

        Assert.Empty(broker.Requests);
    }

    [Theory]
    [InlineData(RemoteResourcePickerDecision.Cancel)]
    [InlineData(RemoteResourcePickerDecision.WindowClosed)]
    public async Task CancelOrWindowCloseReturnsNoSelection(
        RemoteResourcePickerDecision decision)
    {
        var broker = new RecordingBroker([
            new PickerSession(decision, [Page(AccountA, "opaque-hidden", "不应返回")]),
        ]);
        var sessions = new SessionStore(Session(AccountA));
        var picker = Picker(
            broker,
            new RecordingProfilePolicy([AccountA]),
            sessions);

        var result = await picker.PickAsync(AccountA);

        Assert.Empty(result);
        Assert.Equal(0, ((PickerSession)broker.LastSession!).ReadCount);
        Assert.Equal(RemoteSelectionSessionState.Cancelled, Assert.Single(sessions.Completions));
    }

    [Fact]
    public async Task BrokerSkipOrUntypedSessionReturnsNoSelection()
    {
        var skipped = new RecordingBroker([]) { Disposition = VisibleWebAutomationDisposition.Skipped };
        var untyped = new RecordingBroker([new UntypedSession()]);
        var profiles = new RecordingProfilePolicy([AccountA]);
        var skippedSessions = new SessionStore(Session(AccountA));
        var untypedSessions = new SessionStore(Session(AccountA));

        Assert.Empty(await Picker(skipped, profiles, skippedSessions).PickAsync(AccountA));
        Assert.Empty(await Picker(untyped, profiles, untypedSessions).PickAsync(AccountA));
        Assert.Equal(RemoteSelectionSessionState.Cancelled, Assert.Single(skippedSessions.Completions));
        Assert.Equal(RemoteSelectionSessionState.NeedsDecision, Assert.Single(untypedSessions.Completions));
    }

    [Fact]
    public async Task BatchCancellationAcceptsTheExactDurableAlreadyCancelledTerminal()
    {
        var session = Session(AccountA);
        var sessions = new SessionStore(session)
        {
            CompletionSucceeds = false,
            ReloadState = RemoteSelectionSessionState.Cancelled,
        };
        var broker = new RecordingBroker([])
        {
            Disposition = VisibleWebAutomationDisposition.BatchCancelled,
        };

        var result = await Picker(
            broker, new RecordingProfilePolicy([AccountA]), sessions).PickAsync(AccountA);

        Assert.Empty(result);
        Assert.Equal(RemoteSelectionSessionState.Cancelled, Assert.Single(sessions.Completions));
    }

    [Fact]
    public async Task ConflictingTerminalCasNeverReturnsAConfirmedSelection()
    {
        var session = Session(AccountA);
        var sessions = new SessionStore(session)
        {
            CompletionSucceeds = false,
            ReloadState = RemoteSelectionSessionState.NeedsDecision,
        };
        var broker = new RecordingBroker([
            new PickerSession(RemoteResourcePickerDecision.ConfirmSelection,
                [Page(AccountA, "opaque", "资源")]),
        ]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Picker(
            broker, new RecordingProfilePolicy([AccountA]), sessions).PickAsync(AccountA));
    }

    [Fact]
    public async Task AccountInvalidatedAfterBrowserReturnsCannotLeakSelectionsAndEndsNeedsDecision()
    {
        var session = Session(AccountA);
        var sessions = new SessionStore(session)
        {
            CompletionResults = [false, true],
            ReloadState = RemoteSelectionSessionState.Active,
        };
        var broker = new RecordingBroker([
            new PickerSession(RemoteResourcePickerDecision.ConfirmSelection,
                [Page(AccountA, "opaque", "资源")]),
        ]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Picker(
            broker, new RecordingProfilePolicy([AccountA]), sessions).PickAsync(AccountA));

        Assert.Equal(
            [RemoteSelectionSessionState.Confirmed, RemoteSelectionSessionState.NeedsDecision],
            sessions.Completions);
    }

    [Fact]
    public async Task ProductionAuthorizationDenialIsTypedAndDurablyCancelledInsteadOfLookingEmpty()
    {
        var sessions = new SessionStore(Session(AccountA));
        var broker = new RecordingBroker([]) { ThrowAuthorization = true };

        var error = await Assert.ThrowsAsync<WebAutomationAuthorizationException>(() => Picker(
            broker, new RecordingProfilePolicy([AccountA]), sessions).PickAsync(AccountA));

        Assert.Equal(BaiduAutomationDenialReason.CatalogNotValidated, error.DenialReason);
        Assert.Equal(RemoteSelectionSessionState.Cancelled, Assert.Single(sessions.Completions));
    }

    [Fact]
    public async Task ProfileResolutionFailureCannotLeaveTheCreatedSessionActive()
    {
        var sessions = new SessionStore(Session(AccountA));
        var broker = new RecordingBroker([]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Picker(
            broker, new RecordingProfilePolicy([]), sessions).PickAsync(AccountA));

        Assert.Empty(broker.Requests);
        Assert.Equal(RemoteSelectionSessionState.Cancelled, Assert.Single(sessions.Completions));
    }

    private static WebViewRemoteResourcePicker Picker(
        IVisibleWebAutomationBroker broker,
        IAccountProfilePathPolicy profiles,
        IRemoteSelectionSessionStore sessions) =>
        new(broker, profiles, sessions, new RemoteResourcePickerFlow(),
            TimeProvider.System, "test-build");

    private static RemoteSelectionSession Session(Guid accountId) => new(
        Guid.NewGuid(), Guid.NewGuid(), accountId, 1,
        RemoteSelectionSessionState.Active, 0,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static WebAutomationRequestIdentity Identity(RemoteSelectionSession session) => new(
        session.SessionId, session.WorkspaceDraftId, session.AccountId,
        session.Generation, WebAutomationOwnerKind.RemoteSelection);

    private static RemoteResourceSelectionPage Page(
        Guid accountId,
        string stableId,
        string displayName) => new(
        accountId,
        true,
        true,
        null,
        [new(stableId, displayName, RemoteResourceKind.File, new RemotePath("/资料"), true)],
        new string('a', 64));

    private sealed class SessionStore(params RemoteSelectionSession[] sessions)
        : IRemoteSelectionSessionStore
    {
        private readonly Dictionary<Guid, Queue<RemoteSelectionSession>> sessions = sessions
            .GroupBy(session => session.AccountId)
            .ToDictionary(group => group.Key, group => new Queue<RemoteSelectionSession>(group));
        public List<RemoteSelectionSessionState> Completions { get; } = [];
        public bool CompletionSucceeds { get; init; } = true;
        public IReadOnlyList<bool>? CompletionResults { get; init; }
        public RemoteSelectionSessionState? ReloadState { get; init; }
        private RemoteSelectionSession? created;
        private int completionIndex;

        public Task<RemoteSelectionSession> CreateAsync(Guid accountId, DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            created = sessions.TryGetValue(accountId, out var queue)
                ? queue.Dequeue()
                : sessions.Values.SelectMany(queue => queue).First();
            return Task.FromResult(created);
        }

        public Task<RemoteSelectionSession?> GetAsync(Guid sessionId, Guid workspaceDraftId,
            Guid accountId, int generation, CancellationToken cancellationToken = default) =>
            Task.FromResult(created is null || ReloadState is null
                ? null
                : new RemoteSelectionSession(
                    created.SessionId, created.WorkspaceDraftId, created.AccountId,
                    created.Generation, ReloadState.Value, created.Revision + 1,
                    created.CreatedAt, created.UpdatedAt.AddSeconds(1)));

        public Task<bool> TryCompleteAsync(Guid sessionId, Guid workspaceDraftId,
            Guid accountId, int generation, long expectedRevision,
            RemoteSelectionSessionState terminalState, DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            Completions.Add(terminalState);
            var result = CompletionResults is not null && completionIndex < CompletionResults.Count
                ? CompletionResults[completionIndex++]
                : CompletionSucceeds;
            return Task.FromResult(result);
        }

        public Task<int> RecoverIncompleteAsync(DateTimeOffset now,
            CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    private sealed record RequestCapture(
        WebAutomationRequestIdentity Identity,
        string ProfilePath,
        BaiduNavigationAuthorizationRequest Navigation);

    private sealed class RecordingBroker(IEnumerable<IVisibleWebAutomationSession> sessions)
        : IVisibleWebAutomationBroker
    {
        private readonly Queue<IVisibleWebAutomationSession> sessions = new(sessions);

        public VisibleWebAutomationDisposition Disposition { get; init; } =
            VisibleWebAutomationDisposition.ActionCompleted;
        public bool ThrowAuthorization { get; init; }
        public List<RequestCapture> Requests { get; } = [];
        public IVisibleWebAutomationSession? LastSession { get; private set; }

        public async Task<VisibleWebAutomationResult<T>> EnqueueAsync<T>(
            VisibleWebAutomationRequest<T> request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(new(request.Identity, request.ProfilePath, request.Navigation));
            if (ThrowAuthorization)
                throw new WebAutomationAuthorizationException(
                    BaiduAutomationDenialReason.CatalogNotValidated,
                    "not validated");
            if (Disposition != VisibleWebAutomationDisposition.ActionCompleted)
                return new(Disposition, default, WebAutomationAttemptState.Preparing);
            LastSession = sessions.Dequeue();
            var value = await request.Action(LastSession, null!, cancellationToken);
            return new(Disposition, value, WebAutomationAttemptState.Preparing);
        }
    }

    private sealed class PickerSession(
        RemoteResourcePickerDecision decision,
        IEnumerable<RemoteResourceSelectionPage> pages)
        : IVisibleWebAutomationSession, IRemoteResourcePickerSession
    {
        private readonly Queue<RemoteResourceSelectionPage> pages = new(pages);
        public int ReadCount { get; private set; }
        public CancellationToken UserClosed => CancellationToken.None;

        public Task<RemoteResourcePickerDecision> WaitForApplicationDecisionAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(decision);

        public Task<RemoteResourceSelectionPage> ReadSelectionPageAsync(
            string? pageToken,
            CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return Task.FromResult(pages.Dequeue());
        }

        public Task ShowAsync(Uri initialUri, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ShowHandoffAsync(WebAutomationHandoffPrompt prompt, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<WebAutomationUserChoice> WaitForHandoffChoiceAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class UntypedSession : IVisibleWebAutomationSession
    {
        public CancellationToken UserClosed => CancellationToken.None;
        public Task ShowAsync(Uri initialUri, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ShowHandoffAsync(WebAutomationHandoffPrompt prompt, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<WebAutomationUserChoice> WaitForHandoffChoiceAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingProfilePolicy(IEnumerable<Guid> accounts)
        : IAccountProfilePathPolicy
    {
        private readonly HashSet<Guid> accounts = accounts.ToHashSet();
        public string CanonicalRoot { get; } = Path.GetFullPath("profiles");

        public string GetAccountProfilePath(Guid accountId)
        {
            if (!accounts.Contains(accountId)) throw new InvalidOperationException("Unknown account.");
            return Path.GetFullPath(Path.Combine(CanonicalRoot, accountId.ToString("D")));
        }

        public void EnsureSafeAccountPath(Guid accountId, string profilePath)
        {
            if (!string.Equals(
                    GetAccountProfilePath(accountId),
                    profilePath,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unsafe account profile.");
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
