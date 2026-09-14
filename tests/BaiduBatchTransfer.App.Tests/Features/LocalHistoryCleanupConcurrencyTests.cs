using BaiduBatchTransfer.App.Features.Common;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Application.Scheduling;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class LocalHistoryCleanupConcurrencyTests
{
    [Fact]
    public async Task EventArrivingDuringFinalCleanupScanGetsFreshSnapshotAfterDeletion()
    {
        var finalScan = new TaskCompletionSource<LocalHistoryCleanupPreview>();
        var store = new CleanupStore();
        store.OnDelete = () => store.PreviewOverride = () => finalScan.Task;
        using var vm = new LocalHistoryCleanupViewModel(new(store), new CleanupInteraction(), LocalHistoryKind.Transfer,
            () => true, _ => Task.CompletedTask, _ => { }, new InlineBackgroundOperationRunner());
        var clearing = vm.ClearAsync();
        Assert.True(vm.IsBusy);
        Assert.Single(store.Deleted);
        store.PreviewOverride = null;
        store.Preview = new(LocalHistoryKind.Transfer, 1, false, "new-terminal");
        await vm.RefreshPreviewAsync();
        finalScan.SetResult(new(LocalHistoryKind.Transfer, 0, false, null));
        await clearing;
        Assert.True(vm.CanClear);
        Assert.Same(store.Preview, vm.Preview);
        Assert.Single(store.Deleted);
    }

    [Fact]
    public async Task EventArrivingDuringPreviewGetsOneFreshTrailingSnapshot()
    {
        var release = new TaskCompletionSource<LocalHistoryCleanupPreview>();
        var store = new CleanupStore { PreviewOverride = () => release.Task };
        using var vm = new LocalHistoryCleanupViewModel(new(store), new CleanupInteraction(), LocalHistoryKind.Transfer,
            () => true, _ => Task.CompletedTask, _ => { }, new InlineBackgroundOperationRunner());
        var first = vm.RefreshPreviewAsync();
        store.PreviewOverride = null;
        store.Preview = new(LocalHistoryKind.Transfer, 1, false, "new-terminal");
        var terminalChange = vm.RefreshPreviewAsync();
        release.SetResult(new(LocalHistoryKind.Transfer, 0, false, null));
        await Task.WhenAll(first, terminalChange);
        Assert.True(vm.CanClear);
        Assert.Same(store.Preview, vm.Preview);
    }
    [Fact]
    public async Task TerminalExportConflictAutomaticallyRecoversAfterBoundedDelay()
    {
        var store = new CleanupStore { Preview = new(LocalHistoryKind.Transfer, 1, true, "export-running") };
        var delay = new CleanupRetryDelay();
        using var vm = new LocalHistoryCleanupViewModel(new(store), new CleanupInteraction(), LocalHistoryKind.Transfer,
            () => true, _ => Task.CompletedTask, _ => { }, conflictRetryDelay: delay);
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.PropertyChanged += (_, _) => { if (vm.CanClear) recovered.TrySetResult(); };
        await vm.RefreshPreviewAsync();
        await delay.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(vm.CanClear);
        Assert.Equal(TimeSpan.FromSeconds(5), Assert.Single(delay.Delays));
        store.Preview = new(LocalHistoryKind.Transfer, 1, false, "export-finished");
        delay.Release.SetResult();
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(vm.CanClear);
        Assert.Single(delay.Delays);
    }

    [Fact]
    public async Task CloseCancelsScheduledConflictRetry()
    {
        var store = new CleanupStore { Preview = new(LocalHistoryKind.Transfer, 1, true, "lease") };
        var delay = new CleanupRetryDelay();
        using var vm = new LocalHistoryCleanupViewModel(new(store), new CleanupInteraction(), LocalHistoryKind.Transfer,
            () => true, _ => Task.CompletedTask, _ => { }, conflictRetryDelay: delay);
        await vm.RefreshPreviewAsync();
        await delay.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        vm.Close();
        await delay.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(vm.CanClear);
        Assert.Null(vm.Preview);
        Assert.Single(delay.Delays);
    }

    private sealed class CleanupRetryDelay : IAsyncDelay
    {
        public List<TimeSpan> Delays { get; } = [];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Delays.Add(delay);
            Started.TrySetResult();
            try { await Release.Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Cancelled.TrySetResult();
                throw;
            }
        }
    }
    [Fact]
    public async Task BlockingSqliteStylePreviewYieldsImmediatelyAndRepeatedRequestsShareOneFlight()
    {
        using var store = new BlockingCleanupStore { BlockPreview = true };
        using var vm = new LocalHistoryCleanupViewModel(new(store), new CleanupInteraction(), LocalHistoryKind.Transfer,
            () => true, _ => Task.CompletedTask, _ => { });
        var first = vm.RefreshPreviewAsync();
        try
        {
            Assert.False(first.IsCompleted);
            await store.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var repeats = Enumerable.Range(0, 100).Select(_ => vm.RefreshPreviewAsync()).ToArray();
            Assert.Equal(1, store.PreviewCalls);
            store.Release.Set();
            await Task.WhenAll(repeats.Append(first));
            Assert.InRange(store.PreviewCalls, 1, 2);
            Assert.Equal(1, store.MaximumConcurrentPreviews);
            Assert.True(vm.CanClear);
        }
        finally { store.Release.Set(); }
    }

    [Fact]
    public async Task BlockingSqliteStyleDeleteRunsOffCallerAndSuppressesRefreshUntilDeletionFinishes()
    {
        using var store = new BlockingCleanupStore { BlockDelete = true };
        using var vm = new LocalHistoryCleanupViewModel(new(store), new CleanupInteraction(), LocalHistoryKind.Transfer,
            () => true, _ => Task.CompletedTask, _ => { });
        var pending = vm.ClearAsync();
        try
        {
            Assert.False(pending.IsCompleted);
            await store.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var before = store.PreviewCalls;
            await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => vm.RefreshPreviewAsync()));
            Assert.Equal(before, store.PreviewCalls);
            store.Release.Set();
            await pending;
            Assert.Equal(1, store.DeleteCalls);
        }
        finally { store.Release.Set(); }
    }

    private sealed class BlockingCleanupStore : ILocalTerminalHistoryStore, IDisposable
    {
        public bool BlockPreview { get; init; }
        public bool BlockDelete { get; init; }
        public int PreviewCalls;
        public int DeleteCalls;
        private int activePreviews;
        public int MaximumConcurrentPreviews;
        public ManualResetEventSlim Release { get; } = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<LocalHistoryCleanupPreview> PreviewAsync(LocalHistoryKind kind, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref PreviewCalls);
            var active = Interlocked.Increment(ref activePreviews);
            MaximumConcurrentPreviews = Math.Max(MaximumConcurrentPreviews, active);
            try
            {
                if (BlockPreview) Block();
                return Task.FromResult(new LocalHistoryCleanupPreview(kind, 1, false, "snapshot"));
            }
            finally { Interlocked.Decrement(ref activePreviews); }
        }
        public Task<LocalHistoryCleanupResult> DeleteAsync(LocalHistoryCleanupPreview confirmedPreview, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref DeleteCalls);
            if (BlockDelete) Block();
            return Task.FromResult(new LocalHistoryCleanupResult(1, LocalHistoryCleanupDisposition.Deleted));
        }
        private void Block()
        {
            Started.TrySetResult();
            Release.Wait(TimeSpan.FromSeconds(3));
        }
        public void Dispose() => Release.Dispose();
    }
}
