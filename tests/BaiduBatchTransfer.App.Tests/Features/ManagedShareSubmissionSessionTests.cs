using BaiduBatchTransfer.App.Features.ManagedBrowser;
using BaiduBatchTransfer.BaiduAdapter.WebAutomation;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class ManagedShareSubmissionSessionTests
{
    [Fact]
    public async Task ExistingShareFlowPersistsBaselineAndSubmittingBeforeTheOnlyClick()
    {
        var fixture = new Fixture();
        var session = fixture.CreateSession(recoveryOnly: false);

        var result = await new ShareAutomationFlow().ExecuteAsync(session, fixture.Request);

        Assert.True(result.IsSuccess);
        Assert.Equal("new-share", result.Value!.ShareId);
        Assert.Equal(new[]
        {
            "ReadShareOfficialListing",
            "PersistBaseline",
            "MarkSubmitting",
            "SubmitShareOnce",
            "ObserveShareSubmissionOutcome",
            "PersistOfficialAcceptance",
            "ReadShareOfficialListing",
        }, fixture.Boundaries.Events);
        Assert.Single(fixture.Boundaries.Requests,
            request => request.Action == ManagedBrowserAction.SubmitShareOnce);
    }

    [Fact]
    public async Task RecoveryUsesOnlyTheOfficialListingAndNeverSubmitsAgain()
    {
        var fixture = new Fixture();
        var session = fixture.CreateSession(recoveryOnly: true);
        var baseline = WebAutomationBaseline.Create(["old-share"]);

        var recovered = await new ShareAutomationFlow().RecoverWithoutAcceptanceAsync(
            session, fixture.Request, baseline);

        Assert.True(recovered.IsSuccess);
        Assert.Equal("new-share", recovered.Value!.Acceptance.ShareId);
        Assert.Equal(new[] { "ReadShareOfficialListing" }, fixture.Boundaries.Events);
        Assert.DoesNotContain(fixture.Boundaries.Requests,
            request => request.Action == ManagedBrowserAction.SubmitShareOnce);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.CreateShareOnceAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DispatchOutcomeUnknownReturnsUnobservedAndTheSessionRefusesASecondClick()
    {
        var fixture = new Fixture
        {
            SubmitResult = new ManagedBrowserSubmitShareOnceResult(
                ManagedBrowserShareSubmitResultKind.BrowserUnavailable,
                ManagedBrowserShareSubmitDispatchState.DispatchOutcomeUnknown),
        };
        var session = fixture.CreateSession(recoveryOnly: false);
        await session.SelectExactRemoteItemsAsSingleGroupAsync(fixture.Request.RemoteItems, CancellationToken.None);
        await session.ConfigurePermanentShareAsync(fixture.Request, CancellationToken.None);
        var listing = await session.ReadCompleteStableOfficialShareListAsync(CancellationToken.None);
        await session.PersistBaselineAsync(
            WebAutomationBaseline.Create(listing.Shares.Select(share => share.ShareId)),
            listing.ObservationFingerprint,
            CancellationToken.None);
        await session.MarkSubmittingAsync(CancellationToken.None);

        var first = await session.CreateShareOnceAsync(CancellationToken.None);

        Assert.Equal(ShareOfficialAcceptanceStatus.Unobserved, first.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.CreateShareOnceAsync(CancellationToken.None));
        Assert.Single(fixture.Boundaries.Requests,
            request => request.Action == ManagedBrowserAction.SubmitShareOnce);
        Assert.DoesNotContain(fixture.Boundaries.Requests,
            request => request.Action == ManagedBrowserAction.ObserveShareSubmissionOutcome);
    }

    [Fact]
    public async Task FreshSessionCanResumeSubmissionFromThePersistedBaselineWithoutReadingOrClickingTwice()
    {
        var fixture = new Fixture();
        var listing = new ManagedBrowserShareOfficialListing(
            fixture.Context,
            true,
            true,
            [new ManagedBrowserShareOfficialEntry(
                "old-share", "https://pan.baidu.com/s/old_share", "item-1", true, true,
                ShareExtractionCodeBehavior.Fixed, "A1b2")]);
        var baseline = WebAutomationBaseline.Create(["old-share"]);
        var session = fixture.CreateSession(
            recoveryOnly: false,
            persistedBaseline: baseline,
            baselineObservationFingerprint: listing.ObservationFingerprint);
        await session.SelectExactRemoteItemsAsSingleGroupAsync(fixture.Items, CancellationToken.None);
        await session.ConfigurePermanentShareAsync(fixture.Request, CancellationToken.None);

        await session.MarkSubmittingAsync(CancellationToken.None);
        var accepted = await session.CreateShareOnceAsync(CancellationToken.None);

        Assert.True(accepted.IsAccepted);
        Assert.Equal(new[] { "MarkSubmitting", "SubmitShareOnce", "ObserveShareSubmissionOutcome" },
            fixture.Boundaries.Events);
        Assert.Single(fixture.Boundaries.Requests,
            request => request.Action == ManagedBrowserAction.SubmitShareOnce);
    }

    [Fact]
    public async Task MultiItemRequestIsSubmittedAndAcceptedAsOneExactGroup()
    {
        var fixture = new Fixture(multiple: true);
        var session = fixture.CreateSession(recoveryOnly: false);

        var result = await new ShareAutomationFlow().ExecuteAsync(session, fixture.Request);

        Assert.True(result.IsSuccess);
        Assert.Equal(fixture.Items, result.Value!.Request.RemoteItems);
        Assert.Single(fixture.Boundaries.Requests,
            request => request.Action == ManagedBrowserAction.SubmitShareOnce);
    }

    private sealed class Fixture
    {
        internal Fixture(bool multiple = false)
        {
            Items = new RemoteItemIdentitySet(multiple
                ? [new RemoteItemIdentity("item-1", "one"), new RemoteItemIdentity("item-2", "two")]
                : [new RemoteItemIdentity("item-1", "app.apk")]);
            Request = new PermanentShareRequest(Items, "A1b2");
            Context = new ManagedBrowserShareSubmissionContext(
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                "target-1",
                new Uri("http://127.0.0.1:43127/"),
                30_000,
                "uid-1",
                "directory-1",
                "/prepared",
                Items,
                ShareExtractionCodeBehavior.Fixed,
                "A1b2");
            Boundaries = new FakeBoundaries(this);
        }

        internal RemoteItemIdentitySet Items { get; }
        internal PermanentShareRequest Request { get; }
        internal ManagedBrowserShareSubmissionContext Context { get; }
        internal FakeBoundaries Boundaries { get; }
        internal ManagedBrowserSubmitShareOnceResult SubmitResult { get; set; } =
            new(ManagedBrowserShareSubmitResultKind.ClickAttempted,
                ManagedBrowserShareSubmitDispatchState.ClickDispatched);

        internal ManagedShareSubmissionSession CreateSession(
            bool recoveryOnly,
            WebAutomationBaseline? persistedBaseline = null,
            string? baselineObservationFingerprint = null)
        {
            Boundaries.RecoveryOnly = recoveryOnly;
            return new(
                Guid.Parse("22222222-2222-2222-2222-222222222222"),
                Guid.Parse("33333333-3333-3333-3333-333333333333"),
                1,
                Context,
                Boundaries,
                recoveryOnly,
                persistedBaseline,
                baselineObservationFingerprint);
        }
    }

    private sealed class FakeBoundaries(Fixture fixture) : IManagedShareSubmissionBoundaries
    {
        internal List<string> Events { get; } = [];
        internal List<ManagedBrowserRequest> Requests { get; } = [];
        internal bool RecoveryOnly { get; set; }
        private int listingReads;

        public Task<ManagedBrowserResponse> SendAsync(
            ManagedBrowserRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            Events.Add(request.Action.ToString());
            ManagedBrowserResult result = request.Action switch
            {
                ManagedBrowserAction.ReadShareOfficialListing => ListingResult(),
                ManagedBrowserAction.SubmitShareOnce => fixture.SubmitResult,
                ManagedBrowserAction.ObserveShareSubmissionOutcome =>
                    ManagedBrowserShareSubmissionOutcomeResult.CreateAcceptedBound(
                        "request-1", "new-share", "https://pan.baidu.com/s/new_share", fixture.Items,
                        ShareExtractionCodeBehavior.Fixed, "A1b2"),
                _ => throw new InvalidOperationException("Unexpected helper action."),
            };
            return Task.FromResult(ManagedBrowserResponse.For(request, result));
        }

        public Task PersistBaselineAsync(
            WebAutomationBaseline baseline,
            string observationFingerprint,
            CancellationToken cancellationToken)
        {
            Events.Add("PersistBaseline");
            return Task.CompletedTask;
        }

        public Task MarkSubmittingAsync(CancellationToken cancellationToken)
        {
            Events.Add("MarkSubmitting");
            return Task.CompletedTask;
        }

        public Task PersistOfficialAcceptanceAsync(
            ShareOfficialAcceptance acceptance,
            CancellationToken cancellationToken)
        {
            Events.Add("PersistOfficialAcceptance");
            return Task.CompletedTask;
        }

        private ManagedBrowserReadShareOfficialListingResult ListingResult()
        {
            var entries = !RecoveryOnly && listingReads++ == 0 && !Requests.Any(request =>
                request.Action == ManagedBrowserAction.SubmitShareOnce)
                ? new[] { Entry("old-share", "https://pan.baidu.com/s/old_share") }
                : new[]
                {
                    Entry("old-share", "https://pan.baidu.com/s/old_share"),
                    Entry("new-share", "https://pan.baidu.com/s/new_share"),
                };
            var listing = new ManagedBrowserShareOfficialListing(fixture.Context, true, true, entries);
            return new(ManagedBrowserShareListingResultKind.Observed, listing);
        }

        private ManagedBrowserShareOfficialEntry Entry(string shareId, string shareUrl) => new(
            shareId, shareUrl, fixture.Items, true, true,
            ShareExtractionCodeBehavior.Fixed, "A1b2");
    }
}
