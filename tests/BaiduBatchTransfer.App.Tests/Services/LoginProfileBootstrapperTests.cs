using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Infrastructure.Security;

namespace BaiduBatchTransfer.App.Tests.Services;

public sealed class LoginProfileBootstrapperTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "BaiduBatchTransfer-LoginProfiles", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ExactAccountPathIsCreatedAndOwnershipRegisteredBeforeUse()
    {
        var accountId = Guid.NewGuid();
        var token = Guid.NewGuid();
        var policy = new FileSystemAccountProfilePathPolicy(root);
        var store = new RecordingStore();
        var bootstrapper = new LoginProfileBootstrapper(policy, store);

        var path = await bootstrapper.PrepareAsync(accountId, token, CancellationToken.None);

        Assert.Equal(policy.GetAccountProfilePath(accountId), path);
        Assert.True(Directory.Exists(path));
        Assert.NotNull(store.Registration);
        Assert.Equal(accountId, store.Registration.Value.Item1);
        Assert.Equal(path, store.Registration.Value.Item2);
        Assert.Equal(token, store.Registration.Value.Item3);
    }

    [Fact]
    public async Task RegisteredBrowserProfileIsClosedAtMostOnce()
    {
        var closer = new WebView2ProfileCloser();
        var accountId = Guid.NewGuid();
        var closeCount = 0;
        using var registration = closer.Register(accountId, _ =>
        {
            closeCount++;
            return Task.CompletedTask;
        });

        await closer.CloseAsync(accountId, CancellationToken.None);
        await closer.CloseAsync(accountId, CancellationToken.None);

        Assert.Equal(1, closeCount);
    }

    [Fact]
    public async Task ClosingProfileKeepsLeaseUntilCloseCallbackCompletes()
    {
        var closer = new WebView2ProfileCloser();
        var accountId = Guid.NewGuid();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = closer.Register(accountId, async _ =>
        {
            entered.TrySetResult();
            await release.Task;
        });

        var closing = closer.CloseAsync(accountId, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Throws<InvalidOperationException>(() => closer.Register(accountId, _ => Task.CompletedTask));
        release.TrySetResult();
        await closing;
        using var reopened = closer.Register(accountId, _ => Task.CompletedTask);
    }

    [Fact]
    public async Task DuplicateCloseAwaitsTheSameCallbackExactlyOnce()
    {
        var closer = new WebView2ProfileCloser();
        var accountId = Guid.NewGuid();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closeCount = 0;
        using var registration = closer.Register(accountId, async _ =>
        {
            Interlocked.Increment(ref closeCount);
            entered.TrySetResult();
            await release.Task;
        });

        var first = closer.CloseAsync(accountId, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var duplicate = closer.CloseAsync(accountId, CancellationToken.None);
        Assert.False(duplicate.IsCompleted);

        release.TrySetResult();
        await Task.WhenAll(first, duplicate);
        Assert.Equal(1, closeCount);
    }

    [Fact]
    public async Task FailedCloseKeepsLeaseIsolatedUntilARetryConfirmsClosure()
    {
        var closer = new WebView2ProfileCloser();
        var accountId = Guid.NewGuid();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fail = true;
        var closeCalls = 0;
        using var registration = closer.Register(accountId, async _ =>
        {
            closeCalls++;
            entered.TrySetResult();
            await release.Task;
            if (fail) throw new InvalidOperationException("synthetic close failure");
        });

        var closing = closer.CloseAsync(accountId, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Throws<InvalidOperationException>(() => closer.Register(accountId, _ => Task.CompletedTask));

        release.TrySetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => closing);
        registration.Dispose();
        Assert.Throws<InvalidOperationException>(() => closer.Register(accountId, _ => Task.CompletedTask));

        fail = false;
        await closer.CloseAsync(accountId, CancellationToken.None);
        Assert.Equal(2, closeCalls);
        using var reopened = closer.Register(accountId, _ => Task.CompletedTask);
    }

    [Fact]
    public async Task CancelledCloseKeepsLeaseAndCanBeRetried()
    {
        var closer = new WebView2ProfileCloser();
        var accountId = Guid.NewGuid();
        var calls = 0;
        using var registration = closer.Register(accountId, cancellationToken =>
        {
            calls++;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            closer.CloseAsync(accountId, cancelled.Token));
        Assert.Throws<InvalidOperationException>(() => closer.Register(accountId, _ => Task.CompletedTask));

        await closer.CloseAsync(accountId, CancellationToken.None);
        Assert.Equal(2, calls);
        using var reopened = closer.Register(accountId, _ => Task.CompletedTask);
    }

    [Fact]
    public async Task CloseWaitsForBlockedProfilePreparationBeforeReleasingLease()
    {
        var closer = new WebView2ProfileCloser();
        var accountId = Guid.NewGuid();
        var preparationStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = closer.Register(accountId, async _ => await preparationStopped.Task);

        var closing = closer.CloseAsync(accountId, CancellationToken.None);

        Assert.False(closing.IsCompleted);
        Assert.Throws<InvalidOperationException>(() => closer.Register(accountId, _ => Task.CompletedTask));
        preparationStopped.TrySetResult();
        await closing;
        using var reopened = closer.Register(accountId, _ => Task.CompletedTask);
    }

    [Fact]
    public async Task UserCloseWaitsForUnresponsivePreparationAndKeepsLoginAndDeletionFenced()
    {
        var closer = new WebView2ProfileCloser();
        var accountId = Guid.NewGuid();
        var preparationStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = closer.Register(accountId, async _ => await preparationStopped.Task);
        var closeGate = new LoginWindowCloseGate(
            preparationStopped.Task,
            cancellationToken => closer.CloseAsync(accountId, cancellationToken));

        var userClose = closeGate.WaitForSafeCloseAsync(CancellationToken.None);
        var accountDeletion = closer.CloseAsync(accountId, CancellationToken.None);

        Assert.False(userClose.IsCompleted);
        Assert.False(accountDeletion.IsCompleted);
        Assert.Throws<InvalidOperationException>(() =>
            closer.Register(accountId, _ => Task.CompletedTask));

        preparationStopped.TrySetResult();
        await Task.WhenAll(userClose, accountDeletion);
        using var reopened = closer.Register(accountId, _ => Task.CompletedTask);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed class RecordingStore : IWebViewProfileStore
    {
        public (Guid, string, Guid)? Registration { get; private set; }
        public Task RegisterOwnershipAsync(Guid accountId, string exactProfilePath, Guid ownershipToken, CancellationToken cancellationToken)
        {
            Registration = (accountId, exactProfilePath, ownershipToken);
            return Task.CompletedTask;
        }
        public Task CloseAsync(Guid accountId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteAsync(Guid accountId, string exactProfilePath, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> CommitOwnedAsync(Guid accountId, string exactProfilePath, Guid ownershipToken, Func<CancellationToken, Task> commitAction, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<OwnedProfileDeletionStatus> DeleteOwnedAsync(Guid accountId, string exactProfilePath, Guid ownershipToken, Func<CancellationToken, Task<bool>> confirmNoActiveAccount, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
