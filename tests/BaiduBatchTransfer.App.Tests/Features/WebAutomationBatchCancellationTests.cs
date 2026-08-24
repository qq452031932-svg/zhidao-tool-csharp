using BaiduBatchTransfer.App.Features.BrowserAutomation;
using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class WebAutomationBatchCancellationTests
{
    [Fact]
    public async Task DurableSuccessBroadcastsOnceAndDuplicateCancelIsIdempotent()
    {
        var store = new RecordingStore
        {
            Results = new Queue<WebAutomationBatchCancellationDisposition>([
                WebAutomationBatchCancellationDisposition.Cancelled,
                WebAutomationBatchCancellationDisposition.AlreadyCancelled,
            ]),
        };
        using var service = new WebAutomationBatchCancellation(store, TimeProvider.System);
        var identity = Identity();
        var signals = 0;
        using var registration = service.Register(
            identity.BatchId, identity.OwnerKind, () => Interlocked.Increment(ref signals));

        Assert.True(await service.CancelAsync(identity, 1, WebAutomationAttemptState.Preparing));
        Assert.True(await service.CancelAsync(identity, 1, WebAutomationAttemptState.Preparing));

        Assert.Equal(2, store.CancelCalls);
        Assert.Equal(1, signals);
    }

    [Fact]
    public async Task PersistenceFailureOrTerminalBatchNeverBroadcastsCancellation()
    {
        var identity = Identity();
        foreach (var behavior in new[] { StoreBehavior.Throw, StoreBehavior.Terminal })
        {
            var store = new RecordingStore { Behavior = behavior };
            using var service = new WebAutomationBatchCancellation(store, TimeProvider.System);
            var signaled = false;
            using var registration = service.Register(
                identity.BatchId, identity.OwnerKind, () => signaled = true);

            await Assert.ThrowsAnyAsync<Exception>(() => service.CancelAsync(
                identity, 1, WebAutomationAttemptState.Preparing));

            Assert.False(signaled);
        }
    }

    [Fact]
    public async Task RestartedServiceLoadsDurableCancellationBeforeWorkStarts()
    {
        var identity = Identity();
        var store = new RecordingStore { PersistedCancelled = true };
        using var restarted = new WebAutomationBatchCancellation(store, TimeProvider.System);
        var signaled = false;
        using var registration = restarted.Register(
            identity.BatchId, identity.OwnerKind, () => signaled = true);

        Assert.True(await restarted.IsCancelledAsync(identity.BatchId, identity.OwnerKind));
        Assert.True(signaled);
    }

    private static WebAutomationRequestIdentity Identity() => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, WebAutomationOwnerKind.Transfer);

    private enum StoreBehavior
    {
        Success,
        Throw,
        Terminal,
    }

    private sealed class RecordingStore : IWebAutomationBatchCancellationStore
    {
        public Queue<WebAutomationBatchCancellationDisposition>? Results { get; init; }
        public StoreBehavior Behavior { get; init; }
        public bool PersistedCancelled { get; init; }
        public int CancelCalls { get; private set; }

        public Task<WebAutomationBatchCancellationDisposition> CancelAsync(
            Guid operationId,
            Guid batchId,
            Guid accountId,
            int generation,
            WebAutomationOwnerKind ownerKind,
            long expectedAttemptRevision,
            WebAutomationAttemptState expectedResumeStage,
            DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            CancelCalls++;
            if (Behavior == StoreBehavior.Throw) throw new IOException("synthetic database failure");
            if (Behavior == StoreBehavior.Terminal)
                return Task.FromResult(WebAutomationBatchCancellationDisposition.Terminal);
            return Task.FromResult(Results?.Dequeue() ?? WebAutomationBatchCancellationDisposition.Cancelled);
        }

        public Task<bool> IsCancelledAsync(
            Guid batchId,
            WebAutomationOwnerKind ownerKind,
            CancellationToken cancellationToken = default) => Task.FromResult(PersistedCancelled);
    }
}
