using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Core.Tests;

public sealed class TransferAutomationEvidenceTests
{
    private static readonly AdapterCapabilityVersion Capability = new(
        "BaiduTransfer", "baidu-visible-transfer-v2", "baidu-transfer-pages-v2", "build-1");

    [Fact]
    public void SourceEvidenceRequiresCompleteStableIdentitySetAndBinding()
    {
        var items = Items("source-b", "source-a");

        var evidence = new TransferSourceEvidence(
            items, 2, reachedEnd: true, isStable: true, "generation-1", "baidu-user-1",
            Capability, Fingerprint('a'));

        Assert.Equal(["source-a", "source-b"], evidence.Items.Items.Select(item => item.ExactId));
        Assert.Equal(Capability, evidence.Capability);
        Assert.Throws<ArgumentException>(() => new TransferSourceEvidence(
            items, 1, true, true, "generation-1", "baidu-user-1", Capability, Fingerprint('a')));
        Assert.Throws<ArgumentException>(() => new TransferSourceEvidence(
            items, 2, false, true, "generation-1", "baidu-user-1", Capability, Fingerprint('a')));
        Assert.Throws<ArgumentException>(() => new TransferSourceEvidence(
            items, 2, true, false, "generation-1", "baidu-user-1", Capability, Fingerprint('a')));
        Assert.Throws<ArgumentException>(() => new TransferSourceEvidence(
            items, 2, true, true, " ", "baidu-user-1", Capability, Fingerprint('a')));
        Assert.Throws<ArgumentException>(() => new TransferSourceEvidence(
            items, 2, true, true, "generation-1", " ", Capability, Fingerprint('a')));
    }

    [Fact]
    public void TargetEvidenceRequiresAllItemsScopeAndExactDurableFingerprint()
    {
        var baseline = WebAutomationBaseline.Create(["old-a", "old-b"]);
        const string expectedFingerprint =
            "2fba49a77481c7b52916c07067e3a54eceb1d0e7cd5a4d052d19939ee0fb4069";

        Assert.Equal(expectedFingerprint, TransferTargetPreparationEvidence.ComputeObservationFingerprint(
            "target-1", new RemotePath("/目标/目录"), 2, reachedEnd: true, isStable: true,
            "target-generation-1", "baidu-user-1", TransferTargetInventoryScope.AllItems, baseline));

        var evidence = new TransferTargetPreparationEvidence(
            "target-1", new RemotePath("/目标/目录"), 2, reachedEnd: true, isStable: true,
            "target-generation-1", "baidu-user-1", TransferTargetInventoryScope.AllItems,
            baseline, expectedFingerprint);

        Assert.Equal("target-1", evidence.TargetExactId);
        Assert.Equal(2, evidence.TotalCount);
        Assert.Equal(TransferTargetInventoryScope.AllItems, evidence.Scope);
        Assert.Equal(expectedFingerprint, evidence.ObservationFingerprint);
        Assert.Throws<ArgumentOutOfRangeException>(() => new TransferTargetPreparationEvidence(
            "target-1", new RemotePath("/目标/目录"), 2, true, true,
            "target-generation-1", "baidu-user-1", (TransferTargetInventoryScope)0,
            baseline, expectedFingerprint));
        Assert.Throws<ArgumentException>(() => new TransferTargetPreparationEvidence(
            "target-1", new RemotePath("/目标/目录"), 2, true, true,
            "target-generation-1", "baidu-user-1", TransferTargetInventoryScope.AllItems,
            baseline, Fingerprint('b')));
        Assert.Throws<ArgumentException>(() => new TransferTargetPreparationEvidence(
            "target-1", new RemotePath("/目标/目录"), 1, true, true,
            "target-generation-1", "baidu-user-1", TransferTargetInventoryScope.AllItems,
            baseline, expectedFingerprint));
        Assert.Throws<ArgumentException>(() => new TransferTargetPreparationEvidence(
            "target-1", new RemotePath("/目标/目录"), 2, false, true,
            "target-generation-1", "baidu-user-1", TransferTargetInventoryScope.AllItems,
            baseline, expectedFingerprint));
        Assert.Throws<ArgumentException>(() =>
            TransferTargetPreparationEvidence.ComputeObservationFingerprint(
                "target-1", new RemotePath("/目标/\ud800"), 2, true, true,
                "target-generation-1", "baidu-user-1", TransferTargetInventoryScope.AllItems,
                baseline));
    }

    [Fact]
    public void IdentityMappingRequiresOneToOneCanonicalCoverage()
    {
        var mapping = new TransferIdentityMapping([
            new("source-b", "destination-b"),
            new("source-a", "destination-a"),
        ]);

        Assert.True(mapping.Covers(Items("source-a", "source-b")));
        Assert.False(mapping.Covers(Items("source-a")));
        Assert.Equal(["source-a", "source-b"], mapping.Entries.Select(item => item.SourceExactId));
        Assert.Throws<ArgumentException>(() => new TransferIdentityMapping([
            new("source-a", "destination-a"),
            new("source-a", "destination-b"),
        ]));
        Assert.Throws<ArgumentException>(() => new TransferIdentityMapping([
            new("source-a", "destination-a"),
            new("source-b", "destination-a"),
        ]));
    }

    [Fact]
    public void SubmissionEvidenceLocallyBindsSubmissionAndReadinessOrRemainsLegacyUnbound()
    {
        var source = SourceEvidence("source-1");
        var target = TargetEvidence(["old-1"]);
        var submissionId = Guid.Parse("5a33f505-2a7a-4db3-b885-a4195b3d5e12");
        var readinessFingerprint = Fingerprint('c');
        var now = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

        var bound = TransferSubmissionEvidence.CreateBound(
            submissionId, readinessFingerprint, source.Items.Fingerprint, target.TargetExactId,
            target.Baseline.Fingerprint, source.BaiduUserId, source.Capability, now);
        var legacy = new TransferSubmissionEvidence(
            source.Items.Fingerprint, target.TargetExactId, target.Baseline.Fingerprint,
            source.BaiduUserId, source.Capability, now);

        Assert.Equal(submissionId, bound.SubmissionId);
        Assert.Equal(readinessFingerprint, bound.ReadinessFingerprint);
        Assert.True(bound.IsLocallyBound);
        Assert.Null(legacy.SubmissionId);
        Assert.Null(legacy.ReadinessFingerprint);
        Assert.False(legacy.IsLocallyBound);
        Assert.Null(typeof(TransferSubmissionEvidence).GetConstructor([
            typeof(string), typeof(string), typeof(string), typeof(string),
            typeof(AdapterCapabilityVersion), typeof(DateTimeOffset), typeof(Guid?), typeof(string),
        ]));
        Assert.Throws<ArgumentException>(() => TransferSubmissionEvidence.CreateBound(
            Guid.Empty, readinessFingerprint, source.Items.Fingerprint, target.TargetExactId,
            target.Baseline.Fingerprint, source.BaiduUserId, source.Capability, now));
        Assert.Throws<ArgumentException>(() => TransferSubmissionEvidence.CreateBound(
            submissionId, Fingerprint('C'), source.Items.Fingerprint, target.TargetExactId,
            target.Baseline.Fingerprint, source.BaiduUserId, source.Capability, now));
        Assert.Throws<ArgumentNullException>(() => TransferSubmissionEvidence.CreateBound(
            submissionId, readinessFingerprint, null!, target.TargetExactId,
            target.Baseline.Fingerprint, source.BaiduUserId, source.Capability, now));
        Assert.Throws<ArgumentNullException>(() => TransferSubmissionEvidence.CreateBound(
            submissionId, readinessFingerprint, source.Items.Fingerprint, target.TargetExactId,
            null!, source.BaiduUserId, source.Capability, now));
    }

    [Fact]
    public void TargetConfirmationRequiresBoundOneToOneOfficialMappingAndCompleteTargetDiff()
    {
        var source = SourceEvidence("source-1");
        var target = TargetEvidence(["old-1", "old-2"]);
        var submission = TransferSubmissionEvidence.CreateBound(
            Guid.Parse("5a33f505-2a7a-4db3-b885-a4195b3d5e12"), Fingerprint('c'),
            source.Items.Fingerprint, target.TargetExactId, target.Baseline.Fingerprint,
            source.BaiduUserId, source.Capability,
            new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero));
        var legacy = new TransferSubmissionEvidence(
            source.Items.Fingerprint, target.TargetExactId, target.Baseline.Fingerprint,
            source.BaiduUserId, source.Capability, submission.MarkedSubmittingAtUtc);
        var officialMapping = new TransferIdentityMapping([new("source-1", "new-1")]);
        var inventory = WebAutomationBaseline.Create(["old-2", "new-1", "old-1"]);
        var observedAt = submission.MarkedSubmittingAtUtc.AddSeconds(1);

        var confirmation = TransferTargetConfirmationEvidence.Create(
            submission, "official-request-1", officialMapping, target, inventory,
            Fingerprint('d'), observedAt);
        var repeated = TransferTargetConfirmationEvidence.Create(
            submission, "official-request-1", officialMapping, target, inventory,
            Fingerprint('d'), observedAt);

        Assert.Equal(confirmation, repeated);
        Assert.Equal(submission.SubmissionId, confirmation.SubmissionId);
        Assert.Equal("official-request-1", confirmation.OfficialRequestId);
        Assert.Equal("new-1", confirmation.AddedTargetExactId);
        Assert.Equal(target.Baseline.Fingerprint, confirmation.TargetBaselineFingerprint);
        Assert.DoesNotContain("source-1", confirmation.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("new-1", confirmation.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("official-request-1", confirmation.ToString(), StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() => TransferTargetConfirmationEvidence.Create(
            submission, " ", officialMapping, target, inventory, Fingerprint('d'), observedAt));
        Assert.Throws<ArgumentException>(() => TransferTargetConfirmationEvidence.Create(
            submission, "official-request-1", officialMapping, target, inventory,
            Fingerprint('D'), observedAt));
        Assert.Throws<ArgumentNullException>(() => TransferTargetConfirmationEvidence.Create(
            submission, "official-request-1", officialMapping, target, inventory,
            null!, observedAt));
        Assert.Throws<ArgumentException>(() => TransferTargetConfirmationEvidence.Create(
            submission, "official-request-1",
            new TransferIdentityMapping([new("source-1", "different-new")]), target, inventory,
            Fingerprint('d'), observedAt));
        Assert.Throws<ArgumentException>(() => TransferTargetConfirmationEvidence.Create(
            submission, "official-request-1", new TransferIdentityMapping([
                new("source-1", "new-1"),
                new("source-2", "new-2"),
            ]), target, WebAutomationBaseline.Create(["old-1", "old-2", "new-1", "new-2"]),
            Fingerprint('d'), observedAt));
        Assert.Throws<ArgumentException>(() => TransferTargetConfirmationEvidence.Create(
            submission, "official-request-1", officialMapping, target,
            WebAutomationBaseline.Create(["old-1", "old-2"]), Fingerprint('d'), observedAt));
        Assert.Throws<ArgumentException>(() => TransferTargetConfirmationEvidence.Create(
            submission, "official-request-1", officialMapping, target,
            WebAutomationBaseline.Create(["old-1", "old-2", "new-1", "new-2"]),
            Fingerprint('d'), observedAt));
        Assert.Throws<ArgumentException>(() => TransferTargetConfirmationEvidence.Create(
            submission, "official-request-1", officialMapping, target,
            WebAutomationBaseline.Create(["old-1", "new-1"]), Fingerprint('d'), observedAt));
        Assert.Throws<ArgumentException>(() => TransferTargetConfirmationEvidence.Create(
            submission, "official-request-1", new TransferIdentityMapping([new("source-1", "new-2")]),
            target, WebAutomationBaseline.Create(["old-1", "old-2", "new-1"]),
            Fingerprint('d'), observedAt));
        Assert.Throws<ArgumentException>(() => TransferTargetConfirmationEvidence.Create(
            legacy, "official-request-1", officialMapping, target, inventory, Fingerprint('d'), observedAt));
        Assert.Throws<ArgumentException>(() => TransferTargetConfirmationEvidence.Create(
            submission, "official-request-1", new TransferIdentityMapping([new("other-source", "new-1")]),
            target, inventory, Fingerprint('d'), observedAt));
        Assert.Throws<ArgumentException>(() => TransferTargetConfirmationEvidence.Create(
            submission, "official-request-1", officialMapping, TargetEvidence(["old-1", "old-2"],
                targetExactId: "other-target"), inventory, Fingerprint('d'), observedAt));
        Assert.Throws<ArgumentException>(() => TransferTargetConfirmationEvidence.Create(
            submission, "official-request-1", officialMapping, TargetEvidence(["other-baseline"]), inventory,
            Fingerprint('d'), observedAt));
        Assert.Throws<ArgumentException>(() => TransferTargetConfirmationEvidence.Create(
            submission, "official-request-1", officialMapping, TargetEvidence(["old-1", "old-2"],
                baiduUserId: "other-user"), inventory, Fingerprint('d'), observedAt));
        Assert.Throws<ArgumentException>(() => TransferTargetConfirmationEvidence.Create(
            submission, "official-request-1", officialMapping, target, inventory, Fingerprint('d'),
            new DateTimeOffset(2026, 8, 30, 19, 59, 59, TimeSpan.FromHours(8))));
    }

    [Fact]
    public void DurableEvidenceAndPartialOutcomeRedactRemoteValues()
    {
        var source = new TransferSourceEvidence(
            Items("secret-source"), 1, true, true, "secret-generation", "secret-user",
            Capability, Fingerprint('a'));
        var target = new TransferTargetPreparationEvidence(
            "secret-target", new RemotePath("/secret/path"), 0, true, true,
            "secret-target-generation", "secret-user", TransferTargetInventoryScope.AllItems,
            WebAutomationBaseline.Create([]),
            TransferTargetPreparationEvidence.ComputeObservationFingerprint(
                "secret-target", new RemotePath("/secret/path"), 0, true, true,
                "secret-target-generation", "secret-user", TransferTargetInventoryScope.AllItems,
                WebAutomationBaseline.Create([])));
        var preparation = new TransferPreparationEvidence(source, target);
        var submission = new TransferSubmissionEvidence(
            source.Items.Fingerprint, target.TargetExactId, target.Baseline.Fingerprint,
            target.BaiduUserId, Capability, DateTimeOffset.UtcNow);
        var outcome = TransferDirectoryPreparationOutcome.NeedsAttention(
            new RemotePath("/secret/partial"), BaiduErrorKind.UnknownResponse);

        var text = string.Join('|', preparation, submission, outcome);

        Assert.DoesNotContain("secret", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NeedsAttention", text, StringComparison.Ordinal);
    }

    [Fact]
    public void PartialOutcomeRejectsControlCharactersBeforeUserFacingPersistence()
    {
        var path = new RemotePath("/confirmed\nforged-status");

        Assert.Throws<ArgumentException>(() =>
            TransferDirectoryPreparationOutcome.NeedsAttention(path, BaiduErrorKind.UnknownResponse));
    }

    [Fact]
    public void PartialOutcomeAcceptsOnlyDefinedDiagnosticsForNotValidatedFailures()
    {
        var path = new RemotePath("/");

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TransferDirectoryPreparationOutcome.NeedsAttention(
                path,
                BaiduErrorKind.NotValidated,
                (BaiduCandidateDiagnosticCode)int.MaxValue));
        Assert.Throws<ArgumentException>(() =>
            TransferDirectoryPreparationOutcome.NeedsAttention(
                path,
                BaiduErrorKind.UnknownResponse,
                BaiduCandidateDiagnosticCode.TrustedRootMissing));
    }

    [Fact]
    public void DirectoryMutationBindsUidCapabilityAndOwningRevision()
    {
        var mutation = new TransferDirectoryMutationRecord(
            Guid.NewGuid(), Guid.NewGuid(), 2, 1, "parent-1", null,
            new RemotePath("/目标/新目录"), "segment-generation", "baidu-user-1",
            Capability, TransferDirectoryMutationState.OutcomeUnknown, 7);

        Assert.Equal(7, mutation.OwningAttemptRevision);
        Assert.Equal(Capability, mutation.Capability);
        Assert.DoesNotContain("parent-1", mutation.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("baidu-user-1", mutation.ToString(), StringComparison.Ordinal);
    }

    private static RemoteItemIdentitySet Items(params string[] ids) =>
        new(ids.Select(id => new RemoteItemIdentity(id, id)));

    private static TransferSourceEvidence SourceEvidence(params string[] ids) =>
        new(Items(ids), ids.Length, true, true, "source-generation", "baidu-user-1",
            Capability, Fingerprint('a'));

    private static TransferTargetPreparationEvidence TargetEvidence(
        string[] baselineIds,
        string targetExactId = "target-1",
        string baiduUserId = "baidu-user-1")
    {
        var baseline = WebAutomationBaseline.Create(baselineIds);
        return new TransferTargetPreparationEvidence(
            targetExactId, new RemotePath("/target"), baseline.Items.Length, true, true,
            "target-generation", baiduUserId, TransferTargetInventoryScope.AllItems,
            baseline, TransferTargetPreparationEvidence.ComputeObservationFingerprint(
                targetExactId, new RemotePath("/target"), baseline.Items.Length, true, true,
                "target-generation", baiduUserId, TransferTargetInventoryScope.AllItems, baseline));
    }

    private static string Fingerprint(char value) => new(value, 64);
}
