using System.Text.Json;
using BaiduBatchTransfer.BaiduAdapter.WebAutomation;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.BaiduAdapter.Tests.WebAutomation;

public sealed class TransferAutomationFlowTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 2, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("folder-transfer.json", 1)]
    [InlineData("apk-transfer.json", 1)]
    [InlineData("multi-root-transfer.json", 3)]
    public async Task Save_ReturnsExactTopLevelIdentitySet(string fixture, int count)
    {
        var scenario = Load(fixture);
        var session = new FixtureSession(scenario);
        var flow = new TransferAutomationFlow(new FixedTimeProvider(Now));

        var result = await flow.ExecuteAsync(session, Request(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(count, result.Value!.ExactRemoteItems!.Count);
        Assert.Equal(scenario.After.Select(item => item.ExactId).Except(scenario.Baseline),
            result.Value.ExactRemoteItems.Items.Select(item => item.ExactId));
        Assert.Equal(scenario.Baseline.Order(StringComparer.Ordinal), session.PersistedBaseline!.Items.Select(x => x.ExactId));
        Assert.Equal(
            ["open-share", "enter-code", "open-destination", "persist-baseline", "open-share", "enter-code", "mark-submitting", "submit", "persist-acceptance", "reopen-destination"],
            session.Events);
    }

    [Fact]
    public async Task PreparePersistsCompleteBaselineWithoutEnteringSubmission()
    {
        var scenario = Load("folder-transfer.json");
        var session = new FixtureSession(scenario);
        var flow = new TransferAutomationFlow(new FixedTimeProvider(Now));

        var result = await flow.PrepareAsync(session, Request(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(["open-share", "enter-code", "open-destination", "persist-baseline"], session.Events);
        Assert.DoesNotContain("mark-submitting", session.Events);
        Assert.DoesNotContain("submit", session.Events);
    }

    [Fact]
    public async Task SubmitUsesPersistedBaselineAndMarksSubmittingImmediatelyBeforeClick()
    {
        var scenario = Load("folder-transfer.json");
        var session = new FixtureSession(scenario);
        var flow = new TransferAutomationFlow(new FixedTimeProvider(Now));

        var result = await flow.SubmitAsync(
            session,
            Request(),
            WebAutomationBaseline.Create(scenario.Baseline),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            ["open-share", "enter-code", "mark-submitting", "submit", "persist-acceptance", "reopen-destination"],
            session.Events);
    }

    [Fact]
    public async Task AmbiguousPostTransferListing_ReturnsResultPending()
    {
        var session = new FixtureSession(Load("ambiguous-transfer.json"));
        var flow = new TransferAutomationFlow(new FixedTimeProvider(Now));

        var result = await flow.ExecuteAsync(session, Request(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(BaiduErrorKind.UnknownResponse, result.ErrorKind);
        Assert.Equal(SubmissionAcceptance.Unknown, result.SubmissionAcceptance);
    }

    [Fact]
    public async Task UnobservedOfficialAcceptanceAfterClickReturnsResultPending()
    {
        var session = new FixtureSession(Load("folder-transfer.json"))
        {
            AcceptanceOverride = new TransferOfficialAcceptance(
                TransferOfficialAcceptanceStatus.Unobserved,
                null,
                null,
                Fingerprint('d')),
        };

        var result = await new TransferAutomationFlow(new FixedTimeProvider(Now))
            .ExecuteAsync(session, Request(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(SubmissionAcceptance.Unknown, result.SubmissionAcceptance);
        Assert.DoesNotContain("persist-acceptance", session.Events);
    }

    [Fact]
    public async Task ExplicitOfficialRejectionAfterClickIsDefinitelyNotAccepted()
    {
        var session = new FixtureSession(Load("folder-transfer.json"))
        {
            AcceptanceOverride = new TransferOfficialAcceptance(
                TransferOfficialAcceptanceStatus.ExplicitlyRejected,
                null,
                null,
                Fingerprint('d')),
        };

        var result = await new TransferAutomationFlow(new FixedTimeProvider(Now))
            .ExecuteAsync(session, Request(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(SubmissionAcceptance.DefinitelyNotAccepted, result.SubmissionAcceptance);
        Assert.DoesNotContain("persist-acceptance", session.Events);
    }

    [Theory]
    [MemberData(nameof(PostClickUnobservedFailures))]
    public async Task PostClickTimeoutOrWindowCloseReturnsResultPending(Exception failure)
    {
        var session = new FixtureSession(Load("folder-transfer.json")) { SaveFailure = failure };

        var result = await new TransferAutomationFlow(new FixedTimeProvider(Now))
            .ExecuteAsync(session, Request(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(SubmissionAcceptance.Unknown, result.SubmissionAcceptance);
    }

    public static TheoryData<Exception> PostClickUnobservedFailures => new()
    {
        new TimeoutException("No official acceptance was observed."),
        new OperationCanceledException("The visible automation window was closed."),
        new BaiduAutomationNotValidatedException(
            BaiduAutomationDenialReason.FieldEvidenceNotValidated,
            "The post-submit field protocol is not validated."),
    };

    [Fact]
    public async Task NotValidatedWhileMarkingSubmittingDoesNotBecomePendingOrCallSave()
    {
        var session = new FixtureSession(Load("folder-transfer.json"))
        {
            MarkSubmittingFailure = new BaiduAutomationNotValidatedException(
                BaiduAutomationDenialReason.FieldEvidenceNotValidated,
                "The submit boundary is not validated."),
        };

        var result = await new TransferAutomationFlow(new FixedTimeProvider(Now))
            .ExecuteAsync(session, Request(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(BaiduErrorKind.NotValidated, result.ErrorKind);
        Assert.Equal(SubmissionAcceptance.DefinitelyNotAccepted, result.SubmissionAcceptance);
        Assert.DoesNotContain("submit", session.Events);
    }

    [Fact]
    public async Task DisplayNameAndTimeSimilarityCannotConfirmAnIdentity()
    {
        var scenario = Load("folder-transfer.json") with
        {
            ExpectedTopLevelCount = 1,
            After =
            [
                new FixtureItem("fictional-baseline-a", "同名文件夹"),
                new FixtureItem("fictional-candidate-a", "同名文件夹"),
                new FixtureItem("fictional-candidate-b", "同名文件夹"),
            ],
        };
        var flow = new TransferAutomationFlow(new FixedTimeProvider(Now));

        var result = await flow.ExecuteAsync(new FixtureSession(scenario), Request(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(SubmissionAcceptance.Unknown, result.SubmissionAcceptance);
    }

    [Fact]
    public async Task ConcurrentUnrelatedAdditionCannotStandInForSkippedSourceItem()
    {
        var scenario = Load("folder-transfer.json") with
        {
            After =
            [
                new FixtureItem("fictional-baseline-a", "旧资源"),
                new FixtureItem("fictional-unrelated-apk", "同名.apk"),
            ],
            OfficialRemoteIds = [],
        };

        var result = await new TransferAutomationFlow(new FixedTimeProvider(Now))
            .ExecuteAsync(new FixtureSession(scenario), Request(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(SubmissionAcceptance.Unknown, result.SubmissionAcceptance);
    }

    [Fact]
    public async Task ConcurrentUnrelatedAdditionAlongsideAcceptedItemRemainsAmbiguous()
    {
        var scenario = Load("folder-transfer.json") with
        {
            After =
            [
                new FixtureItem("fictional-baseline-a", "旧资源"),
                new FixtureItem("fictional-accepted-folder", "目标文件夹"),
                new FixtureItem("fictional-unrelated-apk", "另一设备新增.apk"),
            ],
            OfficialRemoteIds = ["fictional-accepted-folder"],
        };

        var result = await new TransferAutomationFlow(new FixedTimeProvider(Now))
            .ExecuteAsync(new FixtureSession(scenario), Request(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(SubmissionAcceptance.Unknown, result.SubmissionAcceptance);
    }

    [Fact]
    public async Task DeletedBaselineItemPlusNewItemsCannotConfirmTransfer()
    {
        var scenario = Load("multi-root-transfer.json") with
        {
            Baseline = ["fictional-baseline-a", "fictional-baseline-b"],
            After =
            [
                new FixtureItem("fictional-baseline-a", "旧资源"),
                new FixtureItem("fictional-target-a", "目标A"),
                new FixtureItem("fictional-target-b", "目标B"),
                new FixtureItem("fictional-target-c", "目标C"),
            ],
            OfficialRemoteIds = ["fictional-target-a", "fictional-target-b", "fictional-target-c"],
        };

        var result = await new TransferAutomationFlow(new FixedTimeProvider(Now))
            .ExecuteAsync(new FixtureSession(scenario), Request(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(SubmissionAcceptance.Unknown, result.SubmissionAcceptance);
    }

    [Fact]
    public async Task RecoveryUsesPersistedOfficialAcceptanceAndFullBaselineForExactDifference()
    {
        var scenario = Load("multi-root-transfer.json");
        var session = new FixtureSession(scenario);
        var baseline = WebAutomationBaseline.Create(scenario.Baseline);
        var flow = new TransferAutomationFlow(new FixedTimeProvider(Now));

        var result = await flow.ConfirmExactAsync(
            session,
            Share(),
            Destination(),
            baseline,
            session.OfficialAcceptance,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(TransferConfirmationState.ExactPresent, result.Value!.State);
        Assert.Equal(3, result.Value.RemoteItems!.Count);
        Assert.Equal(["open-share", "enter-code", "reopen-destination"], session.Events);
    }

    [Fact]
    public async Task RecoveryWithoutPersistedOfficialAcceptanceNeverConfirmsDifference()
    {
        var scenario = Load("folder-transfer.json");
        var flow = new TransferAutomationFlow(new FixedTimeProvider(Now));

        var result = await flow.ConfirmExactAsync(
            new FixtureSession(scenario),
            Share(),
            Destination(),
            WebAutomationBaseline.Create(scenario.Baseline),
            new TransferOfficialAcceptance(
                TransferOfficialAcceptanceStatus.Unobserved,
                null,
                null,
                Fingerprint('d')),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(TransferConfirmationState.Ambiguous, result.Value!.State);
    }

    private static TransferAutomationRequest Request() => new(
        Share(),
        Destination(),
        DuplicatePolicy.Skip);

    private static ShareReference Share() =>
        new("fictional-share", "https://example.invalid/share", "1111");

    private static RemoteDirectory Destination() =>
        new("fictional-destination", new RemotePath("/fictional-target"));

    private static FixtureScenario Load(string name) => JsonSerializer.Deserialize<FixtureScenario>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    private sealed class FixtureSession(FixtureScenario scenario) : ITransferAutomationSession
    {
        public List<string> Events { get; } = [];
        public WebAutomationBaseline? PersistedBaseline { get; private set; }
        public Exception? SaveFailure { get; init; }
        public Exception? MarkSubmittingFailure { get; init; }
        public TransferOfficialAcceptance? AcceptanceOverride { get; init; }
        public TransferOfficialAcceptance OfficialAcceptance => new(
            scenario.OfficialAccepted
                ? TransferOfficialAcceptanceStatus.Accepted
                : TransferOfficialAcceptanceStatus.Unobserved,
            "fictional-request",
            scenario.OfficialRemoteIds is null
                ? null
                : new RemoteItemIdentitySet(scenario.OfficialRemoteIds.Select(id => new RemoteItemIdentity(id, id))),
            Fingerprint('d'));

        public Task<TransferSourceListing> OpenExternalShareAsync(ShareReference share, CancellationToken cancellationToken)
        {
            Events.Add("open-share");
            return Task.FromResult(new TransferSourceListing(scenario.ExpectedTopLevelCount, Fingerprint('a')));
        }

        public Task EnterExtractionCodeAsync(string extractionCode, CancellationToken cancellationToken)
        {
            Events.Add("enter-code");
            return Task.CompletedTask;
        }

        public Task<TransferDirectoryListing> OpenDestinationAndReadStableListingAsync(RemoteDirectory destination, CancellationToken cancellationToken)
        {
            Events.Add("open-destination");
            return Task.FromResult(Listing(scenario.Baseline.Select(id => new FixtureItem(id, "旧资源")), 'b'));
        }

        public Task PersistBaselineAsync(WebAutomationBaseline baseline, string observationFingerprint, CancellationToken cancellationToken)
        {
            Events.Add("persist-baseline");
            PersistedBaseline = baseline;
            return Task.CompletedTask;
        }

        public Task ReturnToExternalShareAsync(ShareReference share, CancellationToken cancellationToken)
        {
            Events.Add("return-share");
            return Task.CompletedTask;
        }

        public Task MarkSubmittingAsync(CancellationToken cancellationToken)
        {
            Events.Add("mark-submitting");
            return MarkSubmittingFailure is null
                ? Task.CompletedTask
                : Task.FromException(MarkSubmittingFailure);
        }

        public Task<TransferOfficialAcceptance> SaveWholeShareAsync(RemoteDirectory destination, DuplicatePolicy duplicatePolicy, CancellationToken cancellationToken)
        {
            Events.Add("submit");
            return SaveFailure is null
                ? Task.FromResult(AcceptanceOverride ?? OfficialAcceptance)
                : Task.FromException<TransferOfficialAcceptance>(SaveFailure);
        }

        public Task PersistOfficialAcceptanceAsync(TransferOfficialAcceptance acceptance, CancellationToken cancellationToken)
        {
            Events.Add("persist-acceptance");
            return Task.CompletedTask;
        }

        public Task<TransferDirectoryListing> ReopenDestinationAndReadStableListingAsync(RemoteDirectory destination, CancellationToken cancellationToken)
        {
            Events.Add("reopen-destination");
            return Task.FromResult(Listing(scenario.After, 'c'));
        }

        private static TransferDirectoryListing Listing(IEnumerable<FixtureItem> items, char fingerprint) =>
            new(true, true, items.Select(item => new RemoteItemIdentity(item.ExactId, item.DisplayName)).ToArray(), Fingerprint(fingerprint));
    }

    private sealed record FixtureScenario(
        int ExpectedTopLevelCount,
        string[] Baseline,
        FixtureItem[] After,
        bool OfficialAccepted,
        string[]? OfficialRemoteIds);

    private sealed record FixtureItem(string ExactId, string DisplayName);

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private static string Fingerprint(char value) => new(value, 64);
}
