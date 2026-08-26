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
    public void TargetEvidenceRequiresCompleteStableBaselineProof()
    {
        var baseline = WebAutomationBaseline.Create(["old-a", "old-b"]);

        var evidence = new TransferTargetPreparationEvidence(
            "target-1", new RemotePath("/目标/目录"), 2, reachedEnd: true, isStable: true,
            "target-generation-1", "baidu-user-1", baseline, Fingerprint('b'));

        Assert.Equal("target-1", evidence.TargetExactId);
        Assert.Equal(2, evidence.TotalCount);
        Assert.Throws<ArgumentException>(() => new TransferTargetPreparationEvidence(
            "target-1", new RemotePath("/目标/目录"), 1, true, true,
            "target-generation-1", "baidu-user-1", baseline, Fingerprint('b')));
        Assert.Throws<ArgumentException>(() => new TransferTargetPreparationEvidence(
            "target-1", new RemotePath("/目标/目录"), 2, false, true,
            "target-generation-1", "baidu-user-1", baseline, Fingerprint('b')));
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
    public void DurableEvidenceAndPartialOutcomeRedactRemoteValues()
    {
        var source = new TransferSourceEvidence(
            Items("secret-source"), 1, true, true, "secret-generation", "secret-user",
            Capability, Fingerprint('a'));
        var target = new TransferTargetPreparationEvidence(
            "secret-target", new RemotePath("/secret/path"), 0, true, true,
            "secret-target-generation", "secret-user", WebAutomationBaseline.Create([]), Fingerprint('b'));
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

    private static string Fingerprint(char value) => new(value, 64);
}
