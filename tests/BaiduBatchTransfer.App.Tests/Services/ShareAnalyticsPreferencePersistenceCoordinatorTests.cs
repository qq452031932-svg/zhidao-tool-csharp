using BaiduBatchTransfer.App.Services;

namespace BaiduBatchTransfer.App.Tests.Services;

public sealed class ShareAnalyticsPreferencePersistenceCoordinatorTests
{
    [Fact]
    public async Task SlowEarlierSaveIsSerializedAndLatestRequestWins()
    {
        var first = Guid.NewGuid();
        var latest = Guid.NewGuid();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<Guid?>();
        var completed = new List<Guid?>();
        var active = 0;
        var maximumActive = 0;
        using var coordinator = new ShareAnalyticsPreferencePersistenceCoordinator(async (accountId, token) =>
        {
            lock (calls) calls.Add(accountId);
            maximumActive = Math.Max(maximumActive, Interlocked.Increment(ref active));
            try
            {
                if (accountId == first)
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task.WaitAsync(token);
                }
                lock (completed) completed.Add(accountId);
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        });

        coordinator.RequestSave(first);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        coordinator.RequestSave(latest);

        await Task.Delay(20);
        Assert.Equal([first], calls);
        releaseFirst.TrySetResult();
        Assert.True(await coordinator.FlushAsync(TimeSpan.FromSeconds(1)));

        Assert.Equal([first, latest], calls);
        Assert.Equal([first, latest], completed);
        Assert.Equal(latest, completed[^1]);
        Assert.Equal(1, maximumActive);
    }

    [Fact]
    public async Task RapidPendingRequestsAreCoalescedToTheLatestValue()
    {
        var first = Guid.NewGuid();
        var superseded = Guid.NewGuid();
        var latest = Guid.NewGuid();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<Guid?>();
        using var coordinator = new ShareAnalyticsPreferencePersistenceCoordinator(async (accountId, token) =>
        {
            calls.Add(accountId);
            if (accountId == first)
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task.WaitAsync(token);
            }
        });

        coordinator.RequestSave(first);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        coordinator.RequestSave(superseded);
        coordinator.RequestSave(latest);
        releaseFirst.TrySetResult();

        Assert.True(await coordinator.FlushAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal([first, latest], calls);
    }

    [Fact]
    public async Task FlushTimesOutWithoutCancellingTheIndependentSaveLifetime()
    {
        var accountId = Guid.NewGuid();
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<Guid?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = new ShareAnalyticsPreferencePersistenceCoordinator(async (id, token) =>
        {
            started.TrySetResult(token);
            await release.Task.WaitAsync(token);
            completed.TrySetResult(id);
        });
        coordinator.RequestSave(accountId);

        var persistenceToken = await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(await coordinator.FlushAsync(TimeSpan.FromMilliseconds(20)));
        Assert.False(persistenceToken.IsCancellationRequested);

        release.TrySetResult();
        Assert.True(await coordinator.FlushAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(accountId, await completed.Task.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task FailedSaveCannotReportSuccessfulSealAndFlush()
    {
        using var coordinator = new ShareAnalyticsPreferencePersistenceCoordinator(
            (_, _) => throw new IOException("secret persistence failure"));
        coordinator.RequestSave(Guid.NewGuid());

        Assert.False(await coordinator.SealAndFlushAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task ResumeAfterFailedShutdownRetriesTheLastFailedValue()
    {
        var accountId = Guid.NewGuid();
        var attempts = 0;
        using var coordinator = new ShareAnalyticsPreferencePersistenceCoordinator((id, _) =>
        {
            Assert.Equal(accountId, id);
            if (Interlocked.Increment(ref attempts) == 1)
                throw new IOException("first attempt fails");
            return Task.CompletedTask;
        });
        coordinator.RequestSave(accountId);
        Assert.False(await coordinator.SealAndFlushAsync(TimeSpan.FromSeconds(1)));

        coordinator.ResumeAfterFailedShutdown();

        Assert.True(await coordinator.SealAndFlushAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task SealAtomicallyRejectsNewRequestsAndFlushesItsFinalVersion()
    {
        var sealedAccount = Guid.NewGuid();
        var rejectedAccount = Guid.NewGuid();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var persisted = new List<Guid?>();
        using var coordinator = new ShareAnalyticsPreferencePersistenceCoordinator(async (id, token) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            persisted.Add(id);
        });
        coordinator.RequestSave(sealedAccount);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var flush = coordinator.SealAndFlushAsync(TimeSpan.FromSeconds(1));
        Assert.False(coordinator.RequestSave(rejectedAccount));
        release.TrySetResult();

        Assert.True(await flush);
        Assert.Equal([sealedAccount], persisted);
    }
}
