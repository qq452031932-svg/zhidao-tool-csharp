using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Core.Services;

namespace BaiduBatchTransfer.Core.Tests.Services;

public sealed class DuplicateClassifierTests
{
    private readonly DuplicateClassifier _classifier = new();

    [Fact]
    public void Distribution_ExactRepeatedRowsFoldAndRetainFirstIncludedNote()
    {
        var first = Candidate(note: "first note");
        var repeated = Candidate(note: "later note");

        var result = _classifier.Classify(TransferMode.Distribution, first, repeated);

        Assert.Equal(DuplicateKind.Duplicate, result.Kind);
        Assert.Equal("first note", result.NoteToRetain);
        Assert.False(result.ExpandsByBatchAccounts);
    }

    [Fact]
    public void DifferentShareIds_AreDistinct()
    {
        var result = _classifier.Classify(
            TransferMode.Distribution,
            Candidate(shareId: "share-a", note: "first"),
            Candidate(shareId: "share-b", note: "second"));

        Assert.Equal(DuplicateKind.Distinct, result.Kind);
        Assert.Equal("second", result.NoteToRetain);
    }

    [Theory]
    [InlineData("different-code", "/target", "folder", "account", null)]
    [InlineData("code", "/different", "folder", "account", null)]
    [InlineData("code", "/target", "different-folder", "account", null)]
    [InlineData("code", "/target", "folder", "different-account", null)]
    [InlineData("code", "/target", "folder", "account", "20000000-0000-0000-0000-000000000002")]
    public void Distribution_DifferentIdentityFieldsAreConflicts(
        string extractionCode,
        string targetPath,
        string outerFolder,
        string requestedAccount,
        string? mappedAccountId)
    {
        var result = _classifier.Classify(
            TransferMode.Distribution,
            Candidate(),
            Candidate(
                extractionCode: extractionCode,
                targetPath: targetPath,
                outerFolder: outerFolder,
                requestedAccount: requestedAccount,
                mappedAccountId: mappedAccountId is null ? null : Guid.Parse(mappedAccountId)));

        Assert.Equal(DuplicateKind.Conflict, result.Kind);
        Assert.Equal(DuplicateConflictReason.SourceSettingsDiffer, result.ConflictReason);
    }

    [Fact]
    public void NoteDoesNotParticipateInDuplicateIdentity()
    {
        var result = _classifier.Classify(
            TransferMode.Distribution,
            Candidate(note: null),
            Candidate(note: "different"));

        Assert.Equal(DuplicateKind.Duplicate, result.Kind);
        Assert.Null(result.NoteToRetain);
    }

    [Fact]
    public void Replication_SourceResourceFoldsBeforeLaterBatchAccountExpansion()
    {
        var result = _classifier.Classify(
            TransferMode.Replication,
            Candidate(requestedAccount: null, mappedAccountId: null),
            Candidate(requestedAccount: null, mappedAccountId: null));

        Assert.Equal(DuplicateKind.Duplicate, result.Kind);
        Assert.True(result.ExpandsByBatchAccounts);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Replication_PerRowAccountFieldsRequireIgnoringColumnOrSwitchingMode(
        bool useRequestedAccount,
        bool useMappedAccount)
    {
        var accountName = useRequestedAccount ? "private-account-name" : null;
        var accountId = useMappedAccount
            ? Guid.Parse("30000000-0000-0000-0000-000000000003")
            : (Guid?)null;
        var result = _classifier.Classify(
            TransferMode.Replication,
            Candidate(shareId: "share-a", requestedAccount: accountName, mappedAccountId: accountId),
            Candidate(shareId: "share-b", requestedAccount: null, mappedAccountId: null));

        Assert.Equal(DuplicateKind.Conflict, result.Kind);
        Assert.Equal(
            DuplicateConflictReason.ReplicationAccountSubsetUnsupported,
            result.ConflictReason);
        Assert.Contains("ignore", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("switch", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-account-name", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Replication_DifferingSourceSettingsRemainAConflict()
    {
        var result = _classifier.Classify(
            TransferMode.Replication,
            Candidate(requestedAccount: null, mappedAccountId: null),
            Candidate(
                extractionCode: "other-secret",
                requestedAccount: null,
                mappedAccountId: null));

        Assert.Equal(DuplicateKind.Conflict, result.Kind);
        Assert.Equal(DuplicateConflictReason.SourceSettingsDiffer, result.ConflictReason);
        Assert.DoesNotContain("other-secret", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("other-secret", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void CandidateToString_DoesNotExposeShareIdOrExtractionCode()
    {
        var candidate = Candidate(shareId: "private-share", extractionCode: "private-code");

        var display = candidate.ToString();

        Assert.DoesNotContain("private-share", display, StringComparison.Ordinal);
        Assert.DoesNotContain("private-code", display, StringComparison.Ordinal);
    }

    private static DuplicateCandidate Candidate(
        string shareId = "share-id",
        string? extractionCode = "code",
        string? targetPath = "/target",
        string? outerFolder = "folder",
        string? requestedAccount = "account",
        Guid? mappedAccountId = null,
        string? note = "note") => new(
        shareId,
        extractionCode,
        targetPath,
        outerFolder,
        requestedAccount,
        mappedAccountId,
        note);
}
