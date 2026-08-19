using BaiduBatchTransfer.Application.Scheduling;

namespace BaiduBatchTransfer.Application.Tests.Scheduling;

public sealed class AccountDailyLimitServiceTests
{
    private static readonly DateTimeOffset LocalNow = new(2026, 8, 20, 0, 5, 0, TimeSpan.FromHours(8));

    [Fact]
    public async Task FirstPersistedSubmitCountsOnceButRetryRestartAndConfirmationDoNot()
    {
        var store = new InMemoryBoundaryStore(dailyLimit: 3);
        var service = new AccountDailyLimitService(store, new FixedTimeProvider(LocalNow));
        var operationId = Guid.NewGuid();
        var accountId = Guid.NewGuid();

        var first = await service.PersistFirstSubmissionAsync(Command(operationId, accountId));
        var retry = await service.PersistFirstSubmissionAsync(Command(operationId, accountId));
        var restart = await new AccountDailyLimitService(store, new FixedTimeProvider(LocalNow))
            .PersistFirstSubmissionAsync(Command(operationId, accountId));

        Assert.True(first.Accepted);
        Assert.True(first.WasNewlyCounted);
        Assert.True(retry.Accepted);
        Assert.False(retry.WasNewlyCounted);
        Assert.False(restart.WasNewlyCounted);
        Assert.Equal(1, store.DailyExecutedCount);
    }

    [Fact]
    public async Task LocalNaturalDateResetsPersistedCount()
    {
        var accountId = Guid.NewGuid();
        var store = new InMemoryBoundaryStore(dailyLimit: 1);
        var firstDay = new AccountDailyLimitService(store, new FixedTimeProvider(LocalNow));
        var nextDay = new AccountDailyLimitService(store, new FixedTimeProvider(LocalNow.AddDays(1)));

        Assert.True((await firstDay.PersistFirstSubmissionAsync(Command(Guid.NewGuid(), accountId))).Accepted);
        Assert.False((await firstDay.PersistFirstSubmissionAsync(Command(Guid.NewGuid(), accountId))).Accepted);
        Assert.True((await nextDay.PersistFirstSubmissionAsync(Command(Guid.NewGuid(), accountId))).Accepted);
        Assert.Equal("2026-08-21", store.DateKey);
        Assert.Equal(1, store.DailyExecutedCount);
    }

    private static SubmissionBoundaryCommand Command(Guid operationId, Guid accountId) =>
        new(operationId, accountId, "{\"safe\":true}", LocalNow.AddMinutes(5));

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.CreateCustomTimeZone("test", TimeSpan.FromHours(8), "test", "test");
    }

    private sealed class InMemoryBoundaryStore(int dailyLimit) : ISubmissionBoundaryStore
    {
        private readonly HashSet<Guid> counted = [];
        public int DailyExecutedCount { get; private set; }
        public string DateKey { get; private set; } = string.Empty;

        public Task<SubmissionBoundaryResult> PersistAsync(
            SubmissionBoundaryPersistence persistence,
            CancellationToken cancellationToken = default)
        {
            if (!string.Equals(DateKey, persistence.LocalDateKey, StringComparison.Ordinal))
            {
                DateKey = persistence.LocalDateKey;
                DailyExecutedCount = 0;
            }
            if (counted.Contains(persistence.Command.OperationId))
                return Task.FromResult(new SubmissionBoundaryResult(true, false, DailyExecutedCount));
            if (dailyLimit > 0 && DailyExecutedCount >= dailyLimit)
                return Task.FromResult(new SubmissionBoundaryResult(false, false, DailyExecutedCount));
            counted.Add(persistence.Command.OperationId);
            DailyExecutedCount++;
            return Task.FromResult(new SubmissionBoundaryResult(true, true, DailyExecutedCount));
        }
    }
}
