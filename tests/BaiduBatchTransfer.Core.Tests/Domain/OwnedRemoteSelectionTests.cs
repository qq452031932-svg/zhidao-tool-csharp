using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Core.Tests.Domain;

public sealed class OwnedRemoteSelectionTests
{
    private static readonly Guid AccountId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void Constructor_RequiresAnOwningAccount()
    {
        var error = Assert.Throws<ArgumentException>(() => new OwnedRemoteSelection(
            Guid.Empty,
            ExactItems("fs-id-1", "安装包.apk"),
            "安装包.apk",
            RemoteResourceKind.File,
            new RemotePath("/应用")));

        Assert.Equal("accountId", error.ParamName);
    }

    [Fact]
    public void Constructor_RequiresAnExactStableIdentitySet()
    {
        var error = Assert.Throws<ArgumentNullException>(() => new OwnedRemoteSelection(
            AccountId,
            null!,
            "安装包.apk",
            RemoteResourceKind.File,
            new RemotePath("/应用")));

        Assert.Equal("remoteItems", error.ParamName);
    }

    [Fact]
    public void Constructor_RequiresExactlyOneRemoteItem()
    {
        Assert.Throws<ArgumentException>(() => new RemoteItemIdentitySet([]));

        var error = Assert.Throws<ArgumentException>(() => new OwnedRemoteSelection(
            AccountId,
            new RemoteItemIdentitySet([
                new RemoteItemIdentity("fs-id-1", "文件一"),
                new RemoteItemIdentity("fs-id-2", "文件二"),
            ]),
            "两个文件",
            RemoteResourceKind.File,
            new RemotePath("/应用")));

        Assert.Equal("remoteItems", error.ParamName);
    }

    [Fact]
    public void DisplayName_IsUiOnlyAndDoesNotReplaceExactIdentity()
    {
        var exact = ExactItems("opaque-fs-id-739", "旧名称.apk");
        var selection = new OwnedRemoteSelection(
            AccountId,
            exact,
            "用户看到的新名称.apk",
            RemoteResourceKind.File,
            new RemotePath("/应用"));

        Assert.Same(exact, selection.RemoteItems);
        Assert.Equal("opaque-fs-id-739", selection.RemoteItems.Items.Single().ExactId);
        Assert.NotEqual(selection.DisplayName, selection.RemoteItems.Items.Single().ExactId);
        Assert.True(selection.HasSameExecutionIdentity(new OwnedRemoteSelection(
            AccountId,
            ExactItems("opaque-fs-id-739", "再次改名.apk"),
            "再次改名.apk",
            RemoteResourceKind.File,
            new RemotePath("/其他目录"))));
    }

    [Fact]
    public void ExecutionIdentity_IncludesAccountOwnership()
    {
        var selection = Selection(AccountId, "same-remote-id");
        var otherAccount = Selection(Guid.Parse("22222222-2222-2222-2222-222222222222"), "same-remote-id");

        Assert.False(selection.HasSameExecutionIdentity(otherAccount));
    }

    [Fact]
    public void Constructor_RequiresUsefulUiMetadata()
    {
        Assert.Throws<ArgumentException>(() => new OwnedRemoteSelection(
            AccountId, ExactItems("fs-id", "资源"), "  ", RemoteResourceKind.File, new RemotePath("/")));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OwnedRemoteSelection(
            AccountId, ExactItems("fs-id", "资源"), "资源", (RemoteResourceKind)99, new RemotePath("/")));
        Assert.Throws<ArgumentNullException>(() => new OwnedRemoteSelection(
            AccountId, ExactItems("fs-id", "资源"), "资源", RemoteResourceKind.File, null!));
    }

    [Fact]
    public void StandaloneRecords_DoNotPretendToBeTransferOperations()
    {
        var batchId = Guid.NewGuid();
        var selection = Selection(AccountId, "fs-id-1");
        var now = DateTimeOffset.UtcNow;
        var operation = StandaloneShareOperationRecord.Create(
            Guid.NewGuid(), batchId, selection, sequenceNumber: 1, now);

        Assert.Equal(AccountId, operation.AccountId);
        Assert.Equal(selection.RemoteItems, operation.ExactRemoteItems);
        Assert.Equal(1, operation.Generation);
        Assert.Equal(StandaloneShareOperationStatus.Queued, operation.Status);
        Assert.Equal(StandaloneShareSubmissionState.NotStarted, operation.SubmissionState);
        Assert.DoesNotContain(
            typeof(StandaloneShareOperationRecord).GetProperties(),
            property => property.Name == "TransferOperationId");
    }

    [Fact]
    public void StandaloneBatch_HoldsTheChosenLocalResultDirectoryReference()
    {
        var batch = StandaloneShareBatchRecord.Create(
            Guid.NewGuid(),
            @"C:\结果目录",
            operationCount: 2,
            DateTimeOffset.UtcNow);

        Assert.Equal(@"C:\结果目录", batch.ResultDirectoryReference);
        Assert.Equal(StandaloneShareBatchStatus.Queued, batch.Status);
        Assert.Equal(2, batch.OperationCount);
        Assert.Equal(0, batch.Revision);
    }

    [Fact]
    public void StandaloneBatch_NormalizesLocalResultDirectoryAndRedactsIt()
    {
        var secret = Path.Combine(Path.GetTempPath(), "secret", "..", "results");

        var batch = StandaloneShareBatchRecord.Create(
            Guid.NewGuid(), secret, operationCount: 1, DateTimeOffset.UtcNow);

        Assert.Equal(Path.GetFullPath(secret), batch.ResultDirectoryReference);
        Assert.DoesNotContain(secret, batch.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[REDACTED]", batch.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("relative\\results")]
    [InlineData("https://example.test/results")]
    [InlineData("http://example.test/results")]
    [InlineData("file:///C:/results")]
    [InlineData("C:\\results\u0001private")]
    [InlineData("C:\\results\0private")]
    public void StandaloneBatch_RejectsNonLocalOrUnsafeResultDirectoryWithoutEchoingIt(string value)
    {
        var error = Assert.Throws<ArgumentException>(() => StandaloneShareBatchRecord.Create(
            Guid.NewGuid(), value, operationCount: 1, DateTimeOffset.UtcNow));

        Assert.DoesNotContain(value, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StandaloneBatch_RejectsRootOnlyResultDirectory()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;

        var error = Assert.Throws<ArgumentException>(() => StandaloneShareBatchRecord.Create(
            Guid.NewGuid(), root, operationCount: 1, DateTimeOffset.UtcNow));

        Assert.DoesNotContain(root, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StandaloneRecords_RejectInvalidDurableState()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Throws<ArgumentException>(() => StandaloneShareBatchRecord.Create(
            Guid.NewGuid(), "  ", 1, now));
        Assert.Throws<ArgumentOutOfRangeException>(() => StandaloneShareBatchRecord.Create(
            Guid.NewGuid(), @"C:\结果", 0, now));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StandaloneShareOperationRecord(
            Guid.NewGuid(), Guid.NewGuid(), AccountId, ExactItems("id", "资源"), "资源",
            RemoteResourceKind.File, new RemotePath("/"), 1, 0,
            StandaloneShareOperationStatus.Queued, StandaloneShareSubmissionState.NotStarted,
            0, now, now));
    }

    [Fact]
    public void StandaloneOperationPersistenceBoundary_RejectsMoreThanOneRemoteItem()
    {
        var exactItems = new RemoteItemIdentitySet([
            new RemoteItemIdentity("fs-id-1", "文件一"),
            new RemoteItemIdentity("fs-id-2", "文件二"),
        ]);

        var error = Assert.Throws<ArgumentException>(() => new StandaloneShareOperationRecord(
            Guid.NewGuid(), Guid.NewGuid(), AccountId, exactItems, "两个文件",
            RemoteResourceKind.File, new RemotePath("/"), 1, 1,
            StandaloneShareOperationStatus.Queued, StandaloneShareSubmissionState.NotStarted,
            0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        Assert.Equal("exactRemoteItems", error.ParamName);
    }

    [Theory]
    [InlineData(StandaloneShareBatchStatus.Active, 0, 0)]
    [InlineData(StandaloneShareBatchStatus.Queued, 1, 0)]
    [InlineData(StandaloneShareBatchStatus.Queued, 0, 1)]
    public void StandaloneCreateGraph_RejectsNonInitialBatchState(
        StandaloneShareBatchStatus status,
        long revision,
        int updatedSeconds)
    {
        var now = DateTimeOffset.UtcNow;
        var batchId = Guid.NewGuid();
        var batch = new StandaloneShareBatchRecord(
            batchId, Path.Combine(Path.GetTempPath(), $"results-{batchId:N}"), status,
            1, revision, now, now.AddSeconds(updatedSeconds));

        Assert.Throws<ArgumentException>(() => StandaloneSharePendingBatchEnvelope.Create(
            batch, [InitialOperation(batchId, now)]));
    }

    [Theory]
    [InlineData("submitting")]
    [InlineData("result-pending")]
    [InlineData("succeeded")]
    [InlineData("generation")]
    [InlineData("revision")]
    [InlineData("submission")]
    [InlineData("acceptance")]
    [InlineData("baseline")]
    [InlineData("lease")]
    [InlineData("error")]
    [InlineData("updated")]
    public void StandaloneCreateGraph_RejectsAnyOperationExecutionEvidence(string invalidFamily)
    {
        var now = DateTimeOffset.UtcNow;
        var batchId = Guid.NewGuid();
        var batch = StandaloneShareBatchRecord.Create(
            batchId, Path.Combine(Path.GetTempPath(), $"results-{batchId:N}"), 1, now);
        var initial = InitialOperation(batchId, now);
        var operation = invalidFamily switch
        {
            "submitting" => OperationWith(initial, now, status: StandaloneShareOperationStatus.Submitting),
            "result-pending" => OperationWith(initial, now, status: StandaloneShareOperationStatus.ResultPending),
            "succeeded" => OperationWith(initial, now, status: StandaloneShareOperationStatus.Succeeded),
            "generation" => OperationWith(initial, now, generation: 2),
            "revision" => OperationWith(initial, now, revision: 1),
            "submission" => OperationWith(initial, now, submission: StandaloneShareSubmissionState.Prepared),
            "acceptance" => OperationWith(initial, now) with
            {
                SubmissionAcceptance = SubmissionAcceptance.DefinitelyNotAccepted,
            },
            "baseline" => OperationWith(initial, now) with
            {
                BaselineFingerprint = new string('a', 64),
                BaselineShareCount = 0,
                BaselineCompletedAt = now,
            },
            "lease" => OperationWith(initial, now) with
            {
                LeaseGeneration = 1,
                LeaseOwnerId = Guid.NewGuid(),
                LeaseExpiresAt = now.AddMinutes(1),
            },
            "error" => OperationWith(initial, now) with { LastErrorCode = "FAILED" },
            "updated" => OperationWith(initial, now, updatedAt: now.AddSeconds(1)),
            _ => throw new InvalidOperationException(),
        };

        Assert.Throws<ArgumentException>(() => StandaloneSharePendingBatchEnvelope.Create(batch, [operation]));
    }

    private static StandaloneShareOperationRecord InitialOperation(Guid batchId, DateTimeOffset now) =>
        StandaloneShareOperationRecord.Create(
            Guid.NewGuid(), batchId, Selection(AccountId, $"remote-{batchId:N}"), 1, now);

    private static StandaloneShareOperationRecord OperationWith(
        StandaloneShareOperationRecord initial,
        DateTimeOffset now,
        int generation = 1,
        StandaloneShareOperationStatus status = StandaloneShareOperationStatus.Queued,
        StandaloneShareSubmissionState submission = StandaloneShareSubmissionState.NotStarted,
        long revision = 0,
        DateTimeOffset? updatedAt = null) =>
        new(
            initial.Id, initial.BatchId, initial.AccountId, initial.ExactRemoteItems,
            initial.DisplayName, initial.Kind, initial.ParentPath, initial.SequenceNumber,
            generation, status, submission, revision, now, updatedAt ?? now);

    private static OwnedRemoteSelection Selection(Guid accountId, string exactId) => new(
        accountId,
        ExactItems(exactId, "资源"),
        "资源",
        RemoteResourceKind.File,
        new RemotePath("/资料"));

    private static RemoteItemIdentitySet ExactItems(string exactId, string displayName) =>
        new([new RemoteItemIdentity(exactId, displayName)]);
}
