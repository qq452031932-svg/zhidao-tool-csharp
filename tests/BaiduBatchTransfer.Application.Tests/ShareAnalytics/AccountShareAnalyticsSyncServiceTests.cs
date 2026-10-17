using BaiduBatchTransfer.Application.ShareAnalytics;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Tests.ShareAnalytics;

public sealed class AccountShareAnalyticsSyncServiceTests
{
    private readonly Guid accountId = Guid.NewGuid();

    [Fact]
    public async Task ReadsEveryPageAndCommitsOnlyTheCompleteUniqueSet()
    {
        var client = new FakeClient(new Dictionary<string, BaiduAdapterResult<AccountSharePage>>
        {
            ["first"] = Success([Item("one")], "2"),
            ["2"] = Success([Item("two")], null),
        });
        var repository = new FakeRepository();
        var result = await Service(client, repository).SyncAsync(accountId, default);

        Assert.Equal(AccountShareSyncDisposition.Updated, result.Disposition);
        Assert.Equal(new string?[] { null, "2" }, client.Cursors);
        Assert.Equal(new[] { "one", "two" }, Assert.Single(repository.Commits).Items.Select(item => item.ShareId));
    }

    [Fact]
    public async Task RemoteFailureDoesNotRetryAtServiceLevelOrCommitPartialPages()
    {
        var client = new FakeClient(new Dictionary<string, BaiduAdapterResult<AccountSharePage>>
        {
            ["first"] = Success([Item("old")], "2"),
            ["2"] = BaiduAdapterResult<AccountSharePage>.Failure(BaiduErrorKind.TransientService, "private"),
        });
        var repository = new FakeRepository();
        var result = await Service(client, repository).SyncAsync(accountId, default);

        Assert.Equal(AccountShareSyncDisposition.TemporarilyUnavailable, result.Disposition);
        Assert.Equal(1, client.Cursors.Count(cursor => cursor == "2"));
        Assert.Empty(repository.Commits);
        Assert.DoesNotContain(accountId.ToString(), result.SafeMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(BaiduErrorKind.ExpiredLogin, AccountShareSyncDisposition.LoginRequired)]
    [InlineData(BaiduErrorKind.VerificationRequired, AccountShareSyncDisposition.VerificationRequired)]
    public async Task AccountFailuresStopImmediately(BaiduErrorKind kind, AccountShareSyncDisposition disposition)
    {
        var client = new FakeClient(new() { ["first"] = BaiduAdapterResult<AccountSharePage>.Failure(kind, "private") });
        var repository = new FakeRepository();
        var result = await Service(client, repository).SyncAsync(accountId, default);
        Assert.Equal(disposition, result.Disposition);
        Assert.Empty(repository.Commits);
    }

    [Fact]
    public async Task RepeatedCursorFailsWithoutCommit()
    {
        var repeated = new FakeClient(new() { ["first"] = Success([Item("one")], "2"), ["2"] = Success([Item("two")], "2") });
        var repeatedRepository = new FakeRepository();
        Assert.Equal(AccountShareSyncDisposition.Unavailable,
            (await Service(repeated, repeatedRepository).SyncAsync(accountId, default)).Disposition);
        Assert.Empty(repeatedRepository.Commits);
    }

    [Fact]
    public async Task ShareRepeatedAcrossPageBoundaryIsCountedOnceAndStillCommits()
    {
        // 同一秒批量创建的分享排在页边界上，前后两页各出现一次（用户 10-17 实测第 66 页）。
        var duplicate = new FakeClient(new()
        {
            ["first"] = Success([Item("a"), Item("same")], "2"),
            ["2"] = Success([Item("same"), Item("b")], null),
        });
        var repository = new FakeRepository();
        var result = await Service(duplicate, repository).SyncAsync(accountId, default);

        Assert.Equal(AccountShareSyncDisposition.Updated, result.Disposition);
        Assert.Equal(new[] { "a", "same", "b" }, Assert.Single(repository.Commits).Items.Select(item => item.ShareId));
        Assert.Contains("1 条分享前后两页重复出现", result.Detail);
    }

    [Fact]
    public async Task MoreThanAPageOfDuplicatesStillFailsWithoutCommit()
    {
        var page = Enumerable.Range(0, 101).Select(index => Item($"s{index}")).ToArray();
        var client = new FakeClient(new() { ["first"] = Success(page, "2"), ["2"] = Success(page, null) });
        var repository = new FakeRepository();
        var result = await Service(client, repository).SyncAsync(accountId, default);

        Assert.Equal(AccountShareSyncDisposition.Unavailable, result.Disposition);
        Assert.Contains("翻页重复超过 100 条", result.Detail);
        Assert.Empty(repository.Commits);
    }

    [Fact]
    public async Task FailureDetailNamesThePageAndHowFarItGotButStaysOutOfSafeMessage()
    {
        var rejected = new FakeClient(new()
        {
            ["first"] = Success([Item("one"), Item("two")], "2"),
            ["2"] = BaiduAdapterResult<AccountSharePage>.Failure(BaiduErrorKind.UnknownResponse, "private"),
        });
        var result = await Service(rejected, new FakeRepository()).SyncAsync(accountId, default);
        Assert.Equal(AccountShareSyncDisposition.Unavailable, result.Disposition);
        Assert.Contains("第 2 页", result.Detail);
        Assert.Contains("UnknownResponse", result.Detail);
        Assert.Contains("已读 2 条", result.Detail);
        Assert.DoesNotContain("第 2 页", result.SafeMessage);
    }

    [Fact]
    public async Task CancellationNeverCommitsAndGenerationIsReservedBeforeRemoteRead()
    {
        var repository = new FakeRepository();
        var client = new FakeClient(new(), async cancellationToken =>
        {
            Assert.Equal(1, repository.ReserveCalls);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Success([], null);
        });
        using var cancellation = new CancellationTokenSource();
        var pending = Service(client, repository).SyncAsync(accountId, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Empty(repository.Commits);
    }

    private AccountShareAnalyticsSyncService Service(FakeClient client, FakeRepository repository) =>
        new(new FakeSessions(accountId), client, repository, TimeProvider.System);

    private static BaiduAdapterResult<AccountSharePage> Success(IReadOnlyList<AccountShareObservation> items, string? next) =>
        BaiduAdapterResult<AccountSharePage>.Success(new(items, next));

    private static AccountShareObservation Item(string id) => new(
        id, $"title-{id}", new Uri($"https://pan.baidu.com/s/{id}"), DateTimeOffset.UtcNow,
        AccountShareRemoteStatus.Valid,
        Enum.GetValues<AccountShareMetric>().Select(metric => new AccountShareMetricObservation(metric, 1)).ToArray());

    private sealed class FakeSessions(Guid accountId) : IBaiduSessionStore
    {
        private readonly AccountSession session = new(accountId, "user",
            [new SessionCookie("BDUSS", "secret", ".baidu.com", "/", null, true, true)], DateTimeOffset.UtcNow);
        public Task<AccountSession?> LoadAsync(string account, CancellationToken cancellationToken) => Task.FromResult<AccountSession?>(session);
        public Task SaveAsync(AccountSession value, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RemoveAsync(string account, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeClient(
        Dictionary<string, BaiduAdapterResult<AccountSharePage>> pages,
        Func<CancellationToken, Task<BaiduAdapterResult<AccountSharePage>>>? callback = null) : IAccountShareAnalyticsClient
    {
        public List<string?> Cursors { get; } = [];
        public async Task<BaiduAdapterResult<AccountSharePage>> ReadPageAsync(AccountSession session, string? cursor, CancellationToken cancellationToken)
        {
            Cursors.Add(cursor);
            if (callback is not null) return await callback(cancellationToken);
            return pages[cursor ?? "first"];
        }
    }

    private sealed class FakeRepository : IAccountShareAnalyticsRepository
    {
        public int ReserveCalls { get; private set; }
        public List<AccountShareSyncCommit> Commits { get; } = [];
        public Task<long> ReserveGenerationAsync(Guid accountId, CancellationToken cancellationToken) => Task.FromResult((long)++ReserveCalls);
        public Task<AccountShareCachedSnapshot> LoadCachedAsync(Guid accountId, CancellationToken cancellationToken) =>
            Task.FromResult(new AccountShareCachedSnapshot(accountId, null, []));
        public Task<bool> CommitCompleteSyncAsync(AccountShareSyncCommit commit, CancellationToken cancellationToken)
        {
            Commits.Add(commit);
            return Task.FromResult(true);
        }
    }
}
