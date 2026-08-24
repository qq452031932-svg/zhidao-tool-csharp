using BaiduBatchTransfer.App;

namespace BaiduBatchTransfer.App.Tests;

public sealed class ResultWorkspaceHandoffCoordinatorTests
{
    [Fact]
    public async Task NewJobSupersedesAnInFlightRestoreBeforeItCanResolveOrNavigate()
    {
        var pending = Guid.NewGuid();
        var job = Guid.NewGuid();
        var operations = new ControlledOperations { PendingBatch = pending, BlockPendingLookup = true };
        using var coordinator = new ResultWorkspaceHandoffCoordinator(operations);

        var restore = coordinator.RestorePendingAsync();
        await operations.PendingLookupStarted.Task;

        var activate = coordinator.ActivateJobAsync(job);
        operations.ReleasePendingLookup();
        await Task.WhenAll(restore, activate);

        Assert.DoesNotContain(pending, operations.InitializedBatches);
        Assert.DoesNotContain(pending, operations.ResolvedBatches);
        Assert.Equal([job], operations.InitializedBatches);
        Assert.Equal([job], operations.NavigatedBatches);
    }

    [Fact]
    public async Task ConcurrentRestoreRequestsCoalesceToOnePendingLookup()
    {
        var operations = new ControlledOperations { BlockPendingLookup = true };
        using var coordinator = new ResultWorkspaceHandoffCoordinator(operations);

        var first = coordinator.RestorePendingAsync();
        await operations.PendingLookupStarted.Task;
        var second = coordinator.RestorePendingAsync();

        Assert.Same(first, second);
        operations.ReleasePendingLookup();
        await Task.WhenAll(first, second);

        Assert.Equal(1, operations.PendingLookupCalls);
    }

    [Fact]
    public async Task LatestJobActivationOwnsTheResultsWorkspace()
    {
        var firstJob = Guid.NewGuid();
        var latestJob = Guid.NewGuid();
        var operations = new ControlledOperations { BlockFirstActivation = true };
        using var coordinator = new ResultWorkspaceHandoffCoordinator(operations);

        var first = coordinator.ActivateJobAsync(firstJob);
        await operations.FirstActivationStarted.Task;
        var latest = coordinator.ActivateJobAsync(latestJob);
        operations.ReleaseFirstActivation();
        await Task.WhenAll(first, latest);

        Assert.Equal(latestJob, operations.VisibleBatch);
        Assert.Equal([latestJob], operations.NavigatedBatches);
    }

    [Fact]
    public async Task RestoreTickCannotSupersedeAnInFlightJobActivation()
    {
        var job = Guid.NewGuid();
        var operations = new ControlledOperations { BlockFirstActivation = true };
        using var coordinator = new ResultWorkspaceHandoffCoordinator(operations);

        var activate = coordinator.ActivateJobAsync(job);
        await operations.FirstActivationStarted.Task;
        var restore = coordinator.RestorePendingAsync();
        operations.ReleaseFirstActivation();
        await Task.WhenAll(activate, restore);

        Assert.Equal(job, operations.VisibleBatch);
        Assert.Equal([job], operations.NavigatedBatches);
        Assert.Equal(0, operations.PendingLookupCalls);
    }

    [Fact]
    public async Task CloseCancelsRestoreAndPreventsPostCloseNavigation()
    {
        var operations = new ControlledOperations { PendingBatch = Guid.NewGuid(), BlockPendingLookup = true };
        using var coordinator = new ResultWorkspaceHandoffCoordinator(operations);

        var restore = coordinator.RestorePendingAsync();
        await operations.PendingLookupStarted.Task;
        coordinator.Close();
        operations.ReleasePendingLookup();
        await restore;

        Assert.True(coordinator.IsClosed);
        Assert.Empty(operations.NavigatedBatches);
        Assert.Empty(operations.ResolvedBatches);
    }

    [Fact]
    public async Task ActivationHonorsCallerCancellationToken()
    {
        var operations = new ControlledOperations { BlockFirstActivation = true };
        using var coordinator = new ResultWorkspaceHandoffCoordinator(operations);
        using var cancellation = new CancellationTokenSource();

        var activate = coordinator.ActivateJobAsync(Guid.NewGuid(), cancellation.Token);
        await operations.FirstActivationStarted.Task;
        cancellation.Cancel();
        operations.ReleaseFirstActivation();
        await activate;

        Assert.Empty(operations.NavigatedBatches);
    }

    [Fact]
    public async Task HandoffExceptionsAreObservedAndDoNotEscapeQueuedWork()
    {
        var exceptions = new List<Exception>();
        var operations = new ControlledOperations { PendingLookupException = new InvalidOperationException("boom") };
        using var coordinator = new ResultWorkspaceHandoffCoordinator(operations, exceptions.Add);

        await coordinator.RestorePendingAsync();

        Assert.IsType<InvalidOperationException>(Assert.Single(exceptions));
    }

    [Fact]
    public async Task ViewModelOperationsUseTheInjectedDispatcherAfterAnAwaitedLookup()
    {
        var dispatcher = new AffinityDispatcher();
        var operations = new ControlledOperations
        {
            PendingBatch = Guid.NewGuid(),
            BlockPendingLookup = true,
            HasDispatcherAccess = () => dispatcher.HasAccess,
        };
        using var coordinator = new ResultWorkspaceHandoffCoordinator(operations, dispatcher.InvokeAsync);

        var restore = coordinator.RestorePendingAsync();
        await operations.PendingLookupStarted.Task;
        operations.ReleasePendingLookup();
        await restore;

        Assert.Equal(["find", "initialize", "resolve", "navigate"], operations.DispatcherBoundOperations);
        Assert.Equal(4, dispatcher.InvocationCount);
    }

    private sealed class ControlledOperations : IResultWorkspaceHandoffOperations
    {
        private readonly TaskCompletionSource pendingLookupRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource firstActivationRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Guid? PendingBatch { get; init; }
        public bool BlockPendingLookup { get; init; }
        public bool BlockFirstActivation { get; init; }
        public Exception? PendingLookupException { get; init; }
        public Func<bool>? HasDispatcherAccess { get; init; }
        public int PendingLookupCalls { get; private set; }
        public List<Guid> InitializedBatches { get; } = [];
        public List<Guid> ResolvedBatches { get; } = [];
        public List<Guid> NavigatedBatches { get; } = [];
        public List<string> DispatcherBoundOperations { get; } = [];
        public Guid? VisibleBatch { get; private set; }
        public TaskCompletionSource PendingLookupStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstActivationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<Guid?> FindPendingBatchAsync(CancellationToken cancellationToken)
        {
            VerifyDispatcher("find");
            PendingLookupCalls++;
            PendingLookupStarted.TrySetResult();
            if (PendingLookupException is not null) throw PendingLookupException;
            if (BlockPendingLookup) await pendingLookupRelease.Task;
            return PendingBatch;
        }

        public async Task InitializeAsync(Guid batchJobId, CancellationToken cancellationToken)
        {
            VerifyDispatcher("initialize");
            if (BlockFirstActivation && InitializedBatches.Count == 0)
            {
                InitializedBatches.Add(batchJobId);
                FirstActivationStarted.TrySetResult();
                await firstActivationRelease.Task;
                cancellationToken.ThrowIfCancellationRequested();
                InitializedBatches.Remove(batchJobId);
            }

            cancellationToken.ThrowIfCancellationRequested();
            InitializedBatches.Add(batchJobId);
            VisibleBatch = batchJobId;
        }

        public Task ResolveExistingSharesAsync(Guid batchJobId, CancellationToken cancellationToken)
        {
            VerifyDispatcher("resolve");
            cancellationToken.ThrowIfCancellationRequested();
            ResolvedBatches.Add(batchJobId);
            return Task.CompletedTask;
        }

        public Task NavigateToResultsAsync(Guid batchJobId, CancellationToken cancellationToken)
        {
            VerifyDispatcher("navigate");
            cancellationToken.ThrowIfCancellationRequested();
            NavigatedBatches.Add(batchJobId);
            return Task.CompletedTask;
        }

        public void ReleasePendingLookup() => pendingLookupRelease.TrySetResult();
        public void ReleaseFirstActivation() => firstActivationRelease.TrySetResult();

        private void VerifyDispatcher(string operation)
        {
            if (HasDispatcherAccess is null) return;
            Assert.True(HasDispatcherAccess(), $"{operation} must run through the dispatcher.");
            DispatcherBoundOperations.Add(operation);
        }
    }

    private sealed class AffinityDispatcher
    {
        private readonly AsyncLocal<bool> hasAccess = new();
        public int InvocationCount { get; private set; }
        public bool HasAccess => hasAccess.Value;

        public async Task InvokeAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
        {
            InvocationCount++;
            var previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
            hasAccess.Value = true;
            try
            {
                await operation(cancellationToken);
            }
            finally
            {
                hasAccess.Value = false;
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
        }
    }
}
