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
    public async Task FolderCandidatesCannotBecomeThePreSaveBaseline()
    {
        var session = new FixtureSession(Load("folder-transfer.json"))
        {
            BaselineScope = TransferDirectoryListingScope.FolderCandidates,
        };

        var result = await new TransferAutomationFlow(new FixedTimeProvider(Now))
            .PrepareAsync(session, Request(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(BaiduErrorKind.UnknownResponse, result.ErrorKind);
        Assert.DoesNotContain("persist-baseline", session.Events);
    }

    [Fact]
    public async Task FolderCandidatesCannotConfirmPostSaveMappingOrSuccess()
    {
        var session = new FixtureSession(Load("folder-transfer.json"))
        {
            ReopenScope = TransferDirectoryListingScope.FolderCandidates,
        };

        var result = await new TransferAutomationFlow(new FixedTimeProvider(Now)).ConfirmExactAsync(
            session, Share(), Destination(), WebAutomationBaseline.Create(["fictional-baseline-a"]),
            session.OfficialAcceptance, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(TransferConfirmationState.Ambiguous, result.Value!.State);
    }

    [Fact]
    public async Task FolderCandidatesCannotBecomeTheExactTargetBaseline()
    {
        var session = new FixtureSession(Load("folder-transfer.json"))
        {
            ExactTargetScope = TransferDirectoryListingScope.FolderCandidates,
        };

        var result = await new TransferAutomationFlow(new FixedTimeProvider(Now))
            .PrepareFromPersistedSourceAsync(session, Request(), session.SourceEvidence, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(BaiduErrorKind.NotValidated, result.ErrorKind);
        Assert.DoesNotContain("persist-target", session.Events);
    }

    [Fact]
    public async Task SubmitAsyncRejectsFolderCandidatesAtThePostSaveComparisonBoundary()
    {
        var scenario = Load("folder-transfer.json");
        var session = new FixtureSession(scenario)
        {
            ReopenScope = TransferDirectoryListingScope.FolderCandidates,
        };

        var result = await new TransferAutomationFlow(new FixedTimeProvider(Now)).SubmitAsync(
            session, Request(), WebAutomationBaseline.Create(scenario.Baseline), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(SubmissionAcceptance.Unknown, result.SubmissionAcceptance);
        Assert.Contains("reopen-destination", session.Events);
    }

    [Fact]
    public async Task ConfirmExactFromTargetAsyncRejectsFolderCandidatesBeforePersistedMappingCanConfirmSuccess()
    {
        var session = new FixtureSession(Load("folder-transfer.json"));
        var flow = new TransferAutomationFlow(new FixedTimeProvider(Now));
        var prepared = await flow.PrepareFromPersistedSourceAsync(
            session, Request(), session.SourceEvidence, CancellationToken.None);
        session.Events.Clear();
        session.ReopenScope = TransferDirectoryListingScope.FolderCandidates;
        var evidence = prepared.Value!.Evidence!;
        var submission = new TransferSubmissionEvidence(
            evidence.Source.Items.Fingerprint, evidence.Target.TargetExactId,
            evidence.Target.Baseline.Fingerprint, evidence.Source.BaiduUserId,
            evidence.Source.Capability, Now);

        var result = await flow.ConfirmExactFromTargetAsync(
            session, Destination(), evidence, submission,
            session.OfficialAcceptance, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(TransferConfirmationState.Ambiguous, result.Value!.State);
        Assert.Equal(["reopen-destination"], session.Events);
    }

    [Fact]
    public async Task PrepareFromPersistedSourceReadsOnlyTheExactTarget()
    {
        var scenario = Load("folder-transfer.json");
        var session = new FixtureSession(scenario);
        var flow = new TransferAutomationFlow(new FixedTimeProvider(Now));

        var result = await flow.PrepareFromPersistedSourceAsync(
            session,
            Request(),
            session.SourceEvidence,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            ["open-exact-target", "persist-target"],
            session.Events);
        Assert.Equal(session.SourceEvidence, result.Value!.Evidence!.Source);
        Assert.Equal(Destination().RemoteId, result.Value.Evidence.Target.TargetExactId);
        Assert.Equal(Destination().Path, result.Value.Evidence.Target.TargetPath);
        Assert.Equal(Destination(), session.LastExactTargetDestination);
        Assert.Equal(TransferTargetInventoryScope.AllItems,
            session.PersistedTargetPreparation!.Scope);
    }

    [Fact]
    public async Task PrepareFromPersistedSourceComputesDurableAllItemsFingerprintInsteadOfReusingWireFingerprint()
    {
        var session = new FixtureSession(Load("folder-transfer.json"));

        var result = await new TransferAutomationFlow(new FixedTimeProvider(Now))
            .PrepareFromPersistedSourceAsync(
                session, Request(), session.SourceEvidence, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var target = Assert.IsType<TransferTargetPreparationEvidence>(session.PersistedTargetPreparation);
        Assert.Equal(TransferTargetInventoryScope.AllItems, target.Scope);
        Assert.NotEqual(Fingerprint('b'), target.ObservationFingerprint);
        Assert.Equal(TransferTargetPreparationEvidence.ComputeObservationFingerprint(
            target.TargetExactId, target.TargetPath, target.TotalCount, target.ReachedEnd,
            target.IsStable, target.TargetGenerationToken, target.BaiduUserId,
            TransferTargetInventoryScope.AllItems, target.Baseline), target.ObservationFingerprint);
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

    [Theory]
    [InlineData("apk-transfer.json")]
    [InlineData("folder-transfer.json")]
    [InlineData("multi-root-transfer.json")]
    public async Task SubmitRequiresCombinedPreparationAndBindsTheOfficialTarget(string fixture)
    {
        var scenario = Load(fixture);
        var session = new FixtureSession(scenario);
        var flow = new TransferAutomationFlow(new FixedTimeProvider(Now));
        var prepared = await flow.PrepareFromPersistedSourceAsync(
            session, Request(), session.SourceEvidence, CancellationToken.None);
        session.Events.Clear();

        var result = await flow.SubmitAsync(
            session,
            Request(),
            prepared.Value!.Evidence!,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            ["open-share", "enter-code", "prepare-submit", "mark-transfer-submitting", "submit", "persist-transfer-acceptance", "reopen-destination"],
            session.Events);
        Assert.NotNull(session.PersistedSubmission);
        Assert.Equal(session.SourceEvidence.Items.Fingerprint, session.PersistedSubmission!.SourceFingerprint);
        Assert.Equal(Destination().RemoteId, session.PersistedSubmission.TargetExactId);
    }

    [Fact]
    public async Task ReopenedSourceGenerationMismatchStopsBeforeSubmissionBoundary()
    {
        var scenario = Load("folder-transfer.json");
        var session = new FixtureSession(scenario)
        {
            OpenedSourceOverride = new TransferSourceEvidence(
                new RemoteItemIdentitySet(scenario.SourceIds.Select(id => new RemoteItemIdentity(id, id))),
                scenario.ExpectedTopLevelCount, true, true, "different-generation",
                "baidu-user", Capability(), Fingerprint('a')),
        };
        var flow = new TransferAutomationFlow(new FixedTimeProvider(Now));
        var prepared = await flow.PrepareFromPersistedSourceAsync(
            session, Request(), session.SourceEvidence, CancellationToken.None);
        session.Events.Clear();

        var result = await flow.SubmitAsync(
            session, Request(), prepared.Value!.Evidence!, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(BaiduErrorKind.NotValidated, result.ErrorKind);
        Assert.DoesNotContain("mark-transfer-submitting", session.Events);
        Assert.DoesNotContain("submit", session.Events);
    }

    [Fact]
    public async Task OfficialResultForAnotherTargetRemainsPending()
    {
        var scenario = Load("folder-transfer.json");
        var session = new FixtureSession(scenario)
        {
            AcceptanceOverride = new TransferOfficialAcceptance(
                TransferOfficialAcceptanceStatus.Accepted,
                "request-1",
                new RemoteItemIdentitySet([new("fictional-accepted-folder", null)]),
                new TransferIdentityMapping([
                    new TransferIdentityMappingEntry(
                        scenario.SourceIds[0], "fictional-accepted-folder"),
                ]),
                Fingerprint('d'),
                "another-target"),
        };
        var flow = new TransferAutomationFlow(new FixedTimeProvider(Now));
        var prepared = await flow.PrepareFromPersistedSourceAsync(
            session, Request(), session.SourceEvidence, CancellationToken.None);
        session.Events.Clear();

        var result = await flow.SubmitAsync(
            session, Request(), prepared.Value!.Evidence!, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(SubmissionAcceptance.Unknown, result.SubmissionAcceptance);
        Assert.DoesNotContain("persist-transfer-acceptance", session.Events);
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
    public async Task OfficialMappingMustCoverEveryExactSourceIdentity()
    {
        var scenario = Load("multi-root-transfer.json") with
        {
            OfficialRemoteIds = ["fictional-multi-a", "fictional-multi-b"],
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

    [Fact]
    public async Task TargetOnlyRecoveryNeverOpensOrUnlocksTheSource()
    {
        var scenario = Load("multi-root-transfer.json");
        var session = new FixtureSession(scenario);
        var preparation = await new TransferAutomationFlow(new FixedTimeProvider(Now))
            .PrepareFromPersistedSourceAsync(
                session, Request(), session.SourceEvidence, CancellationToken.None);
        session.Events.Clear();
        var evidence = preparation.Value!.Evidence!;
        var submission = new TransferSubmissionEvidence(
            evidence.Source.Items.Fingerprint,
            evidence.Target.TargetExactId,
            evidence.Target.Baseline.Fingerprint,
            evidence.Source.BaiduUserId,
            evidence.Source.Capability,
            Now);

        var result = await new TransferAutomationFlow(new FixedTimeProvider(Now))
            .ConfirmExactFromTargetAsync(
                session, Destination(), evidence, submission,
                session.OfficialAcceptance, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(TransferConfirmationState.ExactPresent, result.Value!.State);
        Assert.Equal(["reopen-destination"], session.Events);
    }

    [Theory]
    [InlineData("folder-transfer.json")]
    [InlineData("apk-transfer.json")]
    public async Task TargetOnlyConfirmationReturnsOnlyTheOfficiallyMappedSingleDestination(
        string fixture)
    {
        var scenario = Load(fixture);
        var session = new FixtureSession(scenario);
        var flow = new TransferAutomationFlow(new FixedTimeProvider(Now));
        var preparation = await flow.PrepareFromPersistedSourceAsync(
            session, Request(), session.SourceEvidence, CancellationToken.None);
        var evidence = preparation.Value!.Evidence!;
        var readinessFingerprint = new string('d', 64);
        var submission = TransferSubmissionEvidence.CreateBound(
            Guid.Parse("11111111-1111-1111-1111-111111111111"), readinessFingerprint,
            evidence.Source.Items.Fingerprint, evidence.Target.TargetExactId,
            evidence.Target.Baseline.Fingerprint, evidence.Source.BaiduUserId,
            evidence.Source.Capability, Now);
        session.Events.Clear();

        var result = await flow.ConfirmExactFromTargetAsync(
            session, Destination(), evidence, submission,
            session.OfficialAcceptance, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(TransferConfirmationState.ExactPresent, result.Value!.State);
        Assert.Single(result.Value.RemoteItems!.Items);
        Assert.Equal(session.OfficialAcceptance.IdentityMapping!.Entries.Single().DestinationExactId,
            result.Value.RemoteItems.Items.Single().ExactId);
        Assert.Equal(["reopen-destination"], session.Events);
    }

    [Fact]
    public async Task TargetOnlyRecoveryWithoutOfficialMappingReadsThenRemainsAmbiguous()
    {
        var scenario = Load("folder-transfer.json");
        var session = new FixtureSession(scenario);
        var preparation = await new TransferAutomationFlow(new FixedTimeProvider(Now))
            .PrepareFromPersistedSourceAsync(
                session, Request(), session.SourceEvidence, CancellationToken.None);
        session.Events.Clear();
        var evidence = preparation.Value!.Evidence!;
        var submission = new TransferSubmissionEvidence(
            evidence.Source.Items.Fingerprint, evidence.Target.TargetExactId,
            evidence.Target.Baseline.Fingerprint, evidence.Source.BaiduUserId,
            evidence.Source.Capability, Now);

        var result = await new TransferAutomationFlow(new FixedTimeProvider(Now))
            .ConfirmExactFromTargetAsync(
                session, Destination(), evidence, submission,
                officialAcceptance: null, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(TransferConfirmationState.Ambiguous, result.Value!.State);
        Assert.Equal(["reopen-destination"], session.Events);
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
        public TransferTargetPreparationEvidence? PersistedTargetPreparation { get; private set; }
        public RemoteDirectory? LastExactTargetDestination { get; private set; }
        public Exception? SaveFailure { get; init; }
        public Exception? MarkSubmittingFailure { get; init; }
        public TransferOfficialAcceptance? AcceptanceOverride { get; init; }
        public TransferSourceEvidence? OpenedSourceOverride { get; init; }
        public TransferDirectoryListingScope BaselineScope { get; init; } = TransferDirectoryListingScope.AllItems;
        public TransferDirectoryListingScope ExactTargetScope { get; init; } = TransferDirectoryListingScope.AllItems;
        public TransferDirectoryListingScope ReopenScope { get; set; } = TransferDirectoryListingScope.AllItems;
        public TransferSubmissionEvidence? PersistedSubmission { get; private set; }
        public TransferSourceEvidence SourceEvidence => new(
            new RemoteItemIdentitySet(scenario.SourceIds.Select(id => new RemoteItemIdentity(id, id))),
            scenario.ExpectedTopLevelCount,
            reachedEnd: true,
            isStable: true,
            "source-generation",
            "baidu-user",
            Capability(),
            Fingerprint('a'));
        public TransferOfficialAcceptance OfficialAcceptance => new(
            scenario.OfficialAccepted
                ? TransferOfficialAcceptanceStatus.Accepted
                : TransferOfficialAcceptanceStatus.Unobserved,
            "fictional-request",
            scenario.OfficialRemoteIds is null
                ? null
                : new RemoteItemIdentitySet(scenario.OfficialRemoteIds.Select(id => new RemoteItemIdentity(id, id))),
            scenario.OfficialRemoteIds is null
                ? null
                : new TransferIdentityMapping(scenario.SourceIds.Zip(
                    scenario.OfficialRemoteIds,
                    (source, destination) => new TransferIdentityMappingEntry(source, destination))),
            Fingerprint('d'),
            Destination().RemoteId);

        public Task<TransferSourceListing> OpenExternalShareAsync(ShareReference share, CancellationToken cancellationToken)
        {
            Events.Add("open-share");
            return Task.FromResult(new TransferSourceListing(OpenedSourceOverride ?? SourceEvidence));
        }

        public async Task<TransferSourceListing> OpenAndReadSourceForSubmissionAsync(
            ShareReference share,
            CancellationToken cancellationToken)
        {
            var source = await OpenExternalShareAsync(share, cancellationToken);
            if (share.ExtractionCode is not null)
                await EnterExtractionCodeAsync(share.ExtractionCode, cancellationToken);
            return source;
        }

        public Task<TransferDirectoryListing> OpenExactTargetAndReadStableListingAsync(
            RemoteDirectory destination,
            CancellationToken cancellationToken)
        {
            Events.Add("open-exact-target");
            LastExactTargetDestination = destination;
            var items = scenario.Baseline.Select(id => new TransferRemoteItem(
                new RemoteItemIdentity(id, id),
                ExactTargetScope == TransferDirectoryListingScope.AllItems
                    ? TransferRemoteItemKind.File
                    : TransferRemoteItemKind.Folder,
                id)).ToArray();
            return Task.FromResult(TransferDirectoryListing.Exact(
                destination,
                ExactTargetScope == TransferDirectoryListingScope.AllItems
                    ? ["root", destination.RemoteId]
                    : [destination.RemoteId],
                items,
                items.Length,
                reachedEnd: true,
                isStable: true,
                "target-generation",
                "baidu-user",
                Fingerprint('b'), ExactTargetScope));
        }

        public Task PersistTargetPreparationAsync(
            TransferTargetPreparationEvidence evidence,
            CancellationToken cancellationToken)
        {
            Events.Add("persist-target");
            PersistedTargetPreparation = evidence;
            PersistedBaseline = evidence.Baseline;
            return Task.CompletedTask;
        }

        public Task EnterExtractionCodeAsync(string extractionCode, CancellationToken cancellationToken)
        {
            Events.Add("enter-code");
            return Task.CompletedTask;
        }

        public Task<TransferDirectoryListing> OpenDestinationAndReadStableListingAsync(RemoteDirectory destination, CancellationToken cancellationToken)
        {
            Events.Add("open-destination");
            return Task.FromResult(Listing(scenario.Baseline.Select(id => new FixtureItem(id, "旧资源")), 'b', BaselineScope));
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

        public Task MarkSubmittingAsync(
            TransferSubmissionEvidence evidence,
            CancellationToken cancellationToken)
        {
            Events.Add("mark-transfer-submitting");
            PersistedSubmission = evidence;
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

        public Task<TransferOfficialAcceptance> SaveWholeShareExactlyOnceAsync(
            RemoteDirectory destination,
            TransferSourceEvidence source,
            DuplicatePolicy duplicatePolicy,
            CancellationToken cancellationToken) =>
            SaveWholeShareAsync(destination, duplicatePolicy, cancellationToken);

        public Task PrepareWholeShareSubmissionAsync(
            RemoteDirectory destination,
            TransferSourceEvidence source,
            DuplicatePolicy duplicatePolicy,
            CancellationToken cancellationToken)
        {
            Events.Add("prepare-submit");
            return Task.CompletedTask;
        }

        public Task PersistOfficialAcceptanceAsync(TransferOfficialAcceptance acceptance, CancellationToken cancellationToken)
        {
            Events.Add("persist-acceptance");
            return Task.CompletedTask;
        }

        public Task PersistTransferAcceptanceAsync(
            TransferOfficialAcceptance acceptance,
            CancellationToken cancellationToken)
        {
            Events.Add("persist-transfer-acceptance");
            return Task.CompletedTask;
        }

        public Task<TransferDirectoryListing> ReopenDestinationAndReadStableListingAsync(RemoteDirectory destination, CancellationToken cancellationToken)
        {
            Events.Add("reopen-destination");
            var items = scenario.After.Select(item => new TransferRemoteItem(
                new RemoteItemIdentity(item.ExactId, item.DisplayName),
                ReopenScope == TransferDirectoryListingScope.AllItems
                    ? TransferRemoteItemKind.File
                    : TransferRemoteItemKind.Folder,
                item.DisplayName)).ToArray();
            return Task.FromResult(TransferDirectoryListing.Exact(
                destination,
                ReopenScope == TransferDirectoryListingScope.AllItems
                    ? ["root", destination.RemoteId]
                    : [destination.RemoteId],
                items,
                items.Length,
                true,
                true,
                "target-after-generation",
                "baidu-user",
                Fingerprint('c'),
                ReopenScope));
        }

        private static TransferDirectoryListing Listing(
            IEnumerable<FixtureItem> items,
            char fingerprint,
            TransferDirectoryListingScope scope) =>
            scope == TransferDirectoryListingScope.AllItems
                ? new(true, true, items.Select(item => new RemoteItemIdentity(item.ExactId, item.DisplayName)).ToArray(), Fingerprint(fingerprint))
                : TransferDirectoryListing.Exact(
                    Destination(), [Destination().RemoteId],
                    items.Select(item => new TransferRemoteItem(
                        new RemoteItemIdentity(item.ExactId, item.DisplayName),
                        TransferRemoteItemKind.Folder, item.DisplayName)).ToArray(),
                    items.Count(), true, true, "candidate-generation", "baidu-user",
                    Fingerprint(fingerprint), scope);
    }

    private sealed record FixtureScenario(
        int ExpectedTopLevelCount,
        string[] SourceIds,
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

    private static AdapterCapabilityVersion Capability() => new(
        "BaiduTransfer", "baidu-visible-transfer-v2", "baidu-transfer-pages-v2", "build-1");
}
