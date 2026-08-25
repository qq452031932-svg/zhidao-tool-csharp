using BaiduBatchTransfer.BaiduAdapter.WebAutomation;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.BaiduAdapter.Tests.WebAutomation;

public sealed class ShareAutomationFlowTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task MultipleTopLevelItemsAreSelectedAsOneGroupAndSubmittedOnce()
    {
        var request = Request("remote-a", "remote-b", "remote-c");
        var session = SuccessfulSession(request);

        var result = await Flow().ExecuteAsync(session, request, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, session.SelectedGroups.Count);
        Assert.All(session.SelectedGroups, group => Assert.Equal(request.RemoteItems, group));
        Assert.Equal(2, session.ConfiguredRequests.Count);
        Assert.All(session.ConfiguredRequests, configured => Assert.True(configured.IsPermanent));
        Assert.All(session.ConfiguredRequests, configured => Assert.True(configured.IsAccessUnlimited));
        Assert.Equal(1, session.SubmitCount);
        Assert.Equal(
            ["select-group", "configure", "read-list", "persist-baseline", "select-group", "configure", "mark-submitting", "submit", "persist-acceptance", "read-list"],
            session.Events);
    }

    [Fact]
    public async Task PreparePersistsBaselineWithoutCrossingSubmissionBoundary()
    {
        var request = Request("remote-a", "remote-b");
        var session = SuccessfulSession(request);

        var result = await Flow().PrepareAsync(session, request, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(["select-group", "configure", "read-list", "persist-baseline"], session.Events);
        Assert.Equal(["share-existing"], result.Value!.Baseline.Items.Select(item => item.ExactId));
        Assert.Equal(0, session.SubmitCount);
        Assert.DoesNotContain("mark-submitting", session.Events);
        Assert.DoesNotContain("persist-acceptance", session.Events);
    }

    [Fact]
    public async Task PrepareCrashReturnsDefinitelyNotAcceptedWithoutClicking()
    {
        var request = Request("remote-a");
        var session = SuccessfulSession(request) with
        {
            ReadFailure = new InvalidOperationException("The baseline window crashed."),
        };

        var result = await Flow().PrepareAsync(session, request, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(SubmissionAcceptance.DefinitelyNotAccepted, result.SubmissionAcceptance);
        Assert.Equal(0, session.SubmitCount);
        Assert.DoesNotContain("mark-submitting", session.Events);
    }

    [Fact]
    public async Task PrepareCancellationPropagatesWithoutClicking()
    {
        var request = Request("remote-a");
        var session = SuccessfulSession(request);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Flow().PrepareAsync(session, request, cancellation.Token));

        Assert.Equal(0, session.SubmitCount);
        Assert.DoesNotContain("mark-submitting", session.Events);
    }

    [Fact]
    public async Task SubmitUsesPassedBaselineAndMarksSubmittingImmediatelyBeforeSingleClick()
    {
        var request = Request("remote-a");
        var session = SuccessfulSession(request);
        var preparation = await Flow().PrepareAsync(session, request, CancellationToken.None);
        session.Events.Clear();

        var result = await Flow().SubmitAsync(
            session,
            request,
            preparation.Value!.Baseline,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            ["select-group", "configure", "mark-submitting", "submit", "persist-acceptance", "read-list"],
            session.Events);
        Assert.Equal(1, session.SubmitCount);
    }

    [Fact]
    public async Task RecoveryConfirmsUniqueExactAdditionWithoutClicking()
    {
        var request = Request("remote-a", "remote-b");
        var session = SuccessfulSession(request);
        var baseline = WebAutomationBaseline.Create(["share-existing"]);
        session.BaselineListing = session.CurrentListing;

        var result = await Flow().ConfirmExactAsync(
            session,
            request,
            baseline,
            session.Acceptance,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("share-new", result.Value!.ShareId);
        Assert.Equal(["read-list"], session.Events);
        Assert.Equal(0, session.SubmitCount);
        Assert.Null(session.PersistedAcceptance);
    }

    [Fact]
    public async Task RecoveryLeavesMultipleAdditionsAmbiguousWithoutClicking()
    {
        var request = Request("remote-a");
        var session = SuccessfulSession(request);
        var baseline = WebAutomationBaseline.Create(["share-existing"]);
        session.BaselineListing = Listing(
            ExistingEntry(),
            AcceptedEntry(request),
            Entry("share-unrelated", Request("remote-x"), "X9y8"));

        var result = await Flow().ConfirmExactAsync(
            session,
            request,
            baseline,
            session.Acceptance,
            CancellationToken.None);

        AssertPending(result);
        Assert.Equal(["read-list"], session.Events);
        Assert.Equal(0, session.SubmitCount);
    }

    [Fact]
    public async Task RandomExtractionCodeRequiresAndReturnsCompleteRandomSettings()
    {
        var request = Request("remote-a");
        var session = SuccessfulSession(request, extractionCode: "R4nD");

        var result = await Flow().ExecuteAsync(session, request, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(request.UsesRandomExtractionCode);
        Assert.Equal("R4nD", result.Value!.ExtractionCode);
        Assert.Equal(ShareExtractionCodeBehavior.Random, result.Value.EffectiveSettings.ExtractionCodeBehavior);
        Assert.Equal(ShareSettingsMatchStatus.Matched, result.Value.SettingsMatchStatus);
    }

    [Fact]
    public async Task FixedFourCharacterExtractionCodeIsConfiguredAndMatchedExactly()
    {
        var request = RequestWithFixedCode("A1b2", "remote-a");
        var session = SuccessfulSession(request, extractionCode: "A1b2");

        var result = await Flow().ExecuteAsync(session, request, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.All(session.ConfiguredRequests, configured => Assert.Equal("A1b2", configured.FixedExtractionCode));
        Assert.Equal("A1b2", result.Value!.ExtractionCode);
        Assert.Equal(ShareExtractionCodeBehavior.Fixed, result.Value.EffectiveSettings.ExtractionCodeBehavior);
        Assert.Equal(ShareSettingsMatchStatus.Matched, result.Value.SettingsMatchStatus);
    }

    [Theory]
    [MemberData(nameof(PostClickFailures))]
    public async Task WindowCloseOrTimeoutAfterClickReturnsResultPending(Exception failure)
    {
        var request = Request("remote-a");
        var session = SuccessfulSession(request) with { SubmitFailure = failure };

        var result = await Flow().ExecuteAsync(session, request, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(BaiduErrorKind.UnknownResponse, result.ErrorKind);
        Assert.Equal(SubmissionAcceptance.Unknown, result.SubmissionAcceptance);
        Assert.Equal(1, session.SubmitCount);
    }

    public static TheoryData<Exception> PostClickFailures => new()
    {
        new TimeoutException("No acceptance toast appeared."),
        new OperationCanceledException("The visible share window was closed."),
    };

    [Fact]
    public async Task ExplicitOfficialRejectionIsDefinitelyNotAccepted()
    {
        var request = Request("remote-a");
        var session = SuccessfulSession(request) with
        {
            Acceptance = ShareOfficialAcceptance.ExplicitlyRejected(Fingerprint('d')),
        };

        var result = await Flow().ExecuteAsync(session, request, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(SubmissionAcceptance.DefinitelyNotAccepted, result.SubmissionAcceptance);
        Assert.Null(session.PersistedAcceptance);
        Assert.Equal(1, session.SubmitCount);
    }

    [Fact]
    public async Task UnobservedOfficialAcceptanceIsResultPending()
    {
        var request = Request("remote-a");
        var session = SuccessfulSession(request) with
        {
            Acceptance = ShareOfficialAcceptance.Unobserved(Fingerprint('d')),
        };

        var result = await Flow().ExecuteAsync(session, request, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(SubmissionAcceptance.Unknown, result.SubmissionAcceptance);
        Assert.Null(session.PersistedAcceptance);
    }

    [Fact]
    public async Task UniqueExactAdditionMatchingPersistedAcceptanceSucceeds()
    {
        var request = Request("remote-a", "remote-b");
        var session = SuccessfulSession(request);

        var result = await Flow().ExecuteAsync(session, request, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(session.PersistedAcceptance);
        Assert.Equal("share-new", result.Value!.ShareId);
        Assert.Equal("https://pan.baidu.com/s/share-new", result.Value.ShareUrl);
        Assert.Equal(request, result.Value.Request);
        Assert.Equal(Now, result.Value.SubmittedAtUtc);
        Assert.Equal(["share-existing"], session.PersistedBaseline!.Items.Select(item => item.ExactId));
    }

    [Fact]
    public async Task MultipleNewSharesIncludingExactAcceptedShareRemainAmbiguous()
    {
        var request = Request("remote-a");
        var session = SuccessfulSession(request);
        session.CurrentListing = Listing(
            ExistingEntry(),
            AcceptedEntry(request),
            Entry("share-unrelated", Request("remote-x"), "X9y8", "并发新增", Now));

        var result = await Flow().ExecuteAsync(session, request, CancellationToken.None);

        AssertPending(result);
    }

    [Fact]
    public async Task NoNewShareNeverReusesAnExistingBaselineMember()
    {
        var request = Request("remote-a");
        var existing = Entry("share-new", request, "R4nD", "同名分享", Now);
        var session = SuccessfulSession(request);
        session.BaselineListing = Listing(existing);
        session.CurrentListing = Listing(existing);

        var result = await Flow().ExecuteAsync(session, request, CancellationToken.None);

        AssertPending(result);
    }

    [Fact]
    public async Task DisappearingBaselineMemberMakesOtherwiseExactAdditionAmbiguous()
    {
        var request = Request("remote-a");
        var session = SuccessfulSession(request);
        session.BaselineListing = Listing(ExistingEntry(), Entry("share-existing-2", Request("old-b"), "B2c3"));
        session.CurrentListing = Listing(ExistingEntry(), AcceptedEntry(request));

        var result = await Flow().ExecuteAsync(session, request, CancellationToken.None);

        AssertPending(result);
    }

    [Fact]
    public async Task SimilarNameAndTimeCannotReplaceExactAcceptedShareIdentity()
    {
        var request = Request("remote-a");
        var session = SuccessfulSession(request);
        session.CurrentListing = Listing(
            ExistingEntry(),
            Entry("share-lookalike", request, "R4nD", "新分享", Now));

        var result = await Flow().ExecuteAsync(session, request, CancellationToken.None);

        AssertPending(result);
    }

    [Fact]
    public async Task ExactShareIdWithDifferentItemSetOrSettingsCannotSucceed()
    {
        var request = RequestWithFixedCode("A1b2", "remote-a");
        var session = SuccessfulSession(request, extractionCode: "A1b2");
        session.CurrentListing = Listing(
            ExistingEntry(),
            Entry("share-new", RequestWithFixedCode("Z9y8", "remote-other"), "Z9y8"));

        var result = await Flow().ExecuteAsync(session, request, CancellationToken.None);

        AssertPending(result);
    }

    [Fact]
    public async Task BaselineIsPersistedBeforeTheSubmissionBoundary()
    {
        var request = Request("remote-a");
        var session = SuccessfulSession(request);

        _ = await Flow().ExecuteAsync(session, request, CancellationToken.None);

        Assert.True(session.BaselineListing.IsComplete);
        Assert.True(session.BaselineListing.IsStable);
        Assert.True(session.Events.IndexOf("persist-baseline") < session.Events.IndexOf("mark-submitting"));
        Assert.True(session.Events.IndexOf("mark-submitting") < session.Events.IndexOf("submit"));
    }

    private static ShareAutomationFlow Flow() => new(new FixedTimeProvider(Now));

    private static PermanentShareRequest Request(params string[] exactIds) =>
        new(Items(exactIds));

    private static PermanentShareRequest RequestWithFixedCode(string code, params string[] exactIds) =>
        new(Items(exactIds), code);

    private static RemoteItemIdentitySet Items(IEnumerable<string> exactIds) =>
        new(exactIds.Select(id => new RemoteItemIdentity(id, id)));

    private static FixtureSession SuccessfulSession(PermanentShareRequest request, string extractionCode = "R4nD")
    {
        var accepted = ShareOfficialAcceptance.Accepted(
            "request-1",
            "share-new",
            "https://pan.baidu.com/s/share-new",
            request.RemoteItems,
            Settings(request, extractionCode),
            Fingerprint('d'));
        return new FixtureSession
        {
            BaselineListing = Listing(ExistingEntry()),
            CurrentListing = Listing(ExistingEntry(), AcceptedEntry(request, extractionCode)),
            Acceptance = accepted,
        };
    }

    private static ShareOfficialListEntry ExistingEntry() =>
        Entry("share-existing", Request("old-a"), "O1d2", "旧分享", Now.AddHours(-1));

    private static ShareOfficialListEntry AcceptedEntry(PermanentShareRequest request, string extractionCode = "R4nD") =>
        Entry("share-new", request, extractionCode, "新分享", Now);

    private static ShareOfficialListEntry Entry(
        string shareId,
        PermanentShareRequest request,
        string extractionCode,
        string? displayedName = null,
        DateTimeOffset? createdAt = null) =>
        new(
            shareId,
            $"https://pan.baidu.com/s/{shareId}",
            request.RemoteItems,
            Settings(request, extractionCode),
            displayedName,
            createdAt);

    private static EffectiveShareSettings Settings(PermanentShareRequest request, string extractionCode) =>
        new(
            true,
            true,
            request.UsesRandomExtractionCode
                ? ShareExtractionCodeBehavior.Random
                : ShareExtractionCodeBehavior.Fixed,
            extractionCode);

    private static ShareOfficialListing Listing(params ShareOfficialListEntry[] entries) =>
        new(true, true, entries, Fingerprint('b'));

    private static void AssertPending(BaiduAdapterResult<ShareCreationReceipt> result)
    {
        Assert.False(result.IsSuccess);
        Assert.Equal(BaiduErrorKind.UnknownResponse, result.ErrorKind);
        Assert.Equal(SubmissionAcceptance.Unknown, result.SubmissionAcceptance);
    }

    private sealed record FixtureSession : IShareAutomationSession
    {
        private int readCount;

        public List<string> Events { get; } = [];
        public List<RemoteItemIdentitySet> SelectedGroups { get; } = [];
        public List<PermanentShareRequest> ConfiguredRequests { get; } = [];
        public required ShareOfficialListing BaselineListing { get; set; }
        public required ShareOfficialListing CurrentListing { get; set; }
        public required ShareOfficialAcceptance Acceptance { get; init; }
        public Exception? SubmitFailure { get; init; }
        public Exception? ReadFailure { get; init; }
        public int SubmitCount { get; private set; }
        public WebAutomationBaseline? PersistedBaseline { get; private set; }
        public ShareOfficialAcceptance? PersistedAcceptance { get; private set; }

        public Task SelectExactRemoteItemsAsSingleGroupAsync(
            RemoteItemIdentitySet remoteItems,
            CancellationToken cancellationToken)
        {
            Events.Add("select-group");
            SelectedGroups.Add(remoteItems);
            return Task.CompletedTask;
        }

        public Task ConfigurePermanentShareAsync(
            PermanentShareRequest request,
            CancellationToken cancellationToken)
        {
            Events.Add("configure");
            ConfiguredRequests.Add(request);
            return Task.CompletedTask;
        }

        public Task<ShareOfficialListing> ReadCompleteStableOfficialShareListAsync(
            CancellationToken cancellationToken)
        {
            Events.Add("read-list");
            cancellationToken.ThrowIfCancellationRequested();
            if (ReadFailure is not null)
                return Task.FromException<ShareOfficialListing>(ReadFailure);
            return Task.FromResult(readCount++ == 0 ? BaselineListing : CurrentListing);
        }

        public Task PersistBaselineAsync(
            WebAutomationBaseline baseline,
            string observationFingerprint,
            CancellationToken cancellationToken)
        {
            Events.Add("persist-baseline");
            PersistedBaseline = baseline;
            return Task.CompletedTask;
        }

        public Task MarkSubmittingAsync(CancellationToken cancellationToken)
        {
            Events.Add("mark-submitting");
            return Task.CompletedTask;
        }

        public Task<ShareOfficialAcceptance> CreateShareOnceAsync(CancellationToken cancellationToken)
        {
            Events.Add("submit");
            SubmitCount++;
            return SubmitFailure is null
                ? Task.FromResult(Acceptance)
                : Task.FromException<ShareOfficialAcceptance>(SubmitFailure);
        }

        public Task PersistOfficialAcceptanceAsync(
            ShareOfficialAcceptance acceptance,
            CancellationToken cancellationToken)
        {
            Events.Add("persist-acceptance");
            PersistedAcceptance = acceptance;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private static string Fingerprint(char value) => new(value, 64);
}
