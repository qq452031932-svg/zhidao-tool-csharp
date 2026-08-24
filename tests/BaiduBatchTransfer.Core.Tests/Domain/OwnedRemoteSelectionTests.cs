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

    private static OwnedRemoteSelection Selection(Guid accountId, string exactId) => new(
        accountId,
        ExactItems(exactId, "资源"),
        "资源",
        RemoteResourceKind.File,
        new RemotePath("/资料"));

    private static RemoteItemIdentitySet ExactItems(string exactId, string displayName) =>
        new([new RemoteItemIdentity(exactId, displayName)]);
}
