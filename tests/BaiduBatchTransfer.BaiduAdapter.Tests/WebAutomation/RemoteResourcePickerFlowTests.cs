using BaiduBatchTransfer.BaiduAdapter.WebAutomation;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.BaiduAdapter.Tests.WebAutomation;

public sealed class RemoteResourcePickerFlowTests
{
    private static readonly Guid AccountId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Theory]
    [InlineData(RemoteResourceKind.Folder, "资料", "opaque-folder-id")]
    [InlineData(RemoteResourceKind.File, "安装包.apk", "opaque-apk-id")]
    [InlineData(RemoteResourceKind.File, "说明.txt", "opaque-file-id")]
    public async Task ConfirmedFolderApkOrOrdinaryFileReturnsExactOwnedSelection(
        RemoteResourceKind kind,
        string displayName,
        string stableId)
    {
        var session = ConfirmedSession(Page(
            AccountId,
            [new(stableId, displayName, kind, new RemotePath("/资料"), true)]));

        var result = await new RemoteResourcePickerFlow().ExecuteAsync(session, AccountId);

        Assert.Equal(RemoteResourcePickerDisposition.Confirmed, result.Disposition);
        var selected = Assert.Single(result.Selections);
        Assert.Equal(AccountId, selected.AccountId);
        Assert.Equal(stableId, selected.RemoteItems.Items.Single().ExactId);
        Assert.Equal(displayName, selected.DisplayName);
        Assert.Equal(kind, selected.Kind);
        Assert.Equal(new RemotePath("/资料"), selected.ParentPath);
    }

    [Fact]
    public async Task DuplicateDisplayNamesRemainDistinctOnlyByStableIdentity()
    {
        var session = ConfirmedSession(Page(
            AccountId,
            [
                new("opaque-a", "同名文件.apk", RemoteResourceKind.File, new RemotePath("/应用"), true),
                new("opaque-b", "同名文件.apk", RemoteResourceKind.File, new RemotePath("/应用"), true),
            ]));

        var result = await new RemoteResourcePickerFlow().ExecuteAsync(session, AccountId);

        Assert.Equal(RemoteResourcePickerDisposition.Confirmed, result.Disposition);
        Assert.Equal(
            ["opaque-a", "opaque-b"],
            result.Selections.Select(selection => selection.RemoteItems.Items.Single().ExactId));
    }

    [Fact]
    public async Task DisplayNameWithoutStableIdentityCannotBeConfirmed()
    {
        var session = ConfirmedSession(Page(
            AccountId,
            [new(null, "只有显示名.apk", RemoteResourceKind.File, new RemotePath("/应用"), true)]));

        var result = await new RemoteResourcePickerFlow().ExecuteAsync(session, AccountId);

        Assert.Equal(RemoteResourcePickerDisposition.UnsafeObservation, result.Disposition);
        Assert.Empty(result.Selections);
    }

    [Fact]
    public async Task ConfirmedEmptyDirectoryReturnsAnEmptyConfirmedSelection()
    {
        var result = await new RemoteResourcePickerFlow().ExecuteAsync(
            ConfirmedSession(Page(AccountId, [])),
            AccountId);

        Assert.Equal(RemoteResourcePickerDisposition.Confirmed, result.Disposition);
        Assert.Empty(result.Selections);
    }

    [Fact]
    public async Task PaginationReturnsOnlyAfterEveryStableFullyLoadedPageWasRead()
    {
        var session = ConfirmedSession(
            Page(
                AccountId,
                [new("opaque-page-1", "第一项", RemoteResourceKind.Folder, new RemotePath("/"), true)],
                nextPageToken: "page-2"),
            Page(
                AccountId,
                [new("opaque-page-2", "第二项.apk", RemoteResourceKind.File, new RemotePath("/"), true)]));

        var result = await new RemoteResourcePickerFlow().ExecuteAsync(session, AccountId);

        Assert.Equal(RemoteResourcePickerDisposition.Confirmed, result.Disposition);
        Assert.Equal(2, result.Selections.Count);
        Assert.Equal(new string?[] { null, "page-2" }, session.RequestedPageTokens);
    }

    [Fact]
    public async Task IncompleteLazyLoadedObservationCannotBeConfirmed()
    {
        var session = ConfirmedSession(Page(
            AccountId,
            [new("opaque-visible", "当前可见项", RemoteResourceKind.File, new RemotePath("/"), true)],
            isFullyLoaded: false));

        var result = await new RemoteResourcePickerFlow().ExecuteAsync(session, AccountId);

        Assert.Equal(RemoteResourcePickerDisposition.UnsafeObservation, result.Disposition);
        Assert.Empty(result.Selections);
    }

    [Fact]
    public async Task PageOwnedByAnotherAccountCannotBeConfirmed()
    {
        var otherAccount = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var session = ConfirmedSession(Page(
            otherAccount,
            [new("opaque-other", "其他账号资源", RemoteResourceKind.File, new RemotePath("/"), true)]));

        var result = await new RemoteResourcePickerFlow().ExecuteAsync(session, AccountId);

        Assert.Equal(RemoteResourcePickerDisposition.UnsafeObservation, result.Disposition);
        Assert.Empty(result.Selections);
    }

    [Theory]
    [InlineData(RemoteResourcePickerDecision.Cancel)]
    [InlineData(RemoteResourcePickerDecision.WindowClosed)]
    public async Task CancelOrWindowCloseReturnsNoSelectionWithoutReadingDom(
        RemoteResourcePickerDecision decision)
    {
        var session = new ScriptedSession(decision, []);

        var result = await new RemoteResourcePickerFlow().ExecuteAsync(session, AccountId);

        Assert.Equal(RemoteResourcePickerDisposition.Cancelled, result.Disposition);
        Assert.Empty(result.Selections);
        Assert.Empty(session.RequestedPageTokens);
    }

    private static ScriptedSession ConfirmedSession(params RemoteResourceSelectionPage[] pages) =>
        new(RemoteResourcePickerDecision.ConfirmSelection, pages);

    private static RemoteResourceSelectionPage Page(
        Guid accountId,
        IReadOnlyList<RemoteResourceObservedItem> items,
        bool isStable = true,
        bool isFullyLoaded = true,
        string? nextPageToken = null) =>
        new(
            accountId,
            isStable,
            isFullyLoaded,
            nextPageToken,
            items,
            new string('a', 64));

    private sealed class ScriptedSession(
        RemoteResourcePickerDecision decision,
        IEnumerable<RemoteResourceSelectionPage> pages) : IRemoteResourcePickerSession
    {
        private readonly Queue<RemoteResourceSelectionPage> pages = new(pages);

        public List<string?> RequestedPageTokens { get; } = [];

        public Task<RemoteResourcePickerDecision> WaitForApplicationDecisionAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(decision);

        public Task<RemoteResourceSelectionPage> ReadSelectionPageAsync(
            string? pageToken,
            CancellationToken cancellationToken = default)
        {
            RequestedPageTokens.Add(pageToken);
            return Task.FromResult(pages.Dequeue());
        }
    }
}
