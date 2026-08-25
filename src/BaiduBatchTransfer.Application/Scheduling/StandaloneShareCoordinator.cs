using System.Threading.Channels;
using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.Application.Scheduling;

public sealed class StandaloneShareCoordinator :
    IStandaloneShareBatchNotifications,
    IAsyncDisposable
{
    private readonly IStandaloneShareExecutionStore store;
    private readonly IStandaloneShareOperationRunner runner;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan leaseDuration;
    private readonly TimeSpan rescanInterval;
    private readonly ILocalResultExportCoordinator? localResultExports;
    private readonly Channel<Guid> batches = Channel.CreateUnbounded<Guid>(new()
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false,
    });
    private readonly CancellationTokenSource lifetime = new();
    private readonly HashSet<Guid> scheduled = [];
    private readonly object gate = new();
    private Task? worker;
    private Task? scanner;
    private int started;
    private int disposed;

    public StandaloneShareCoordinator(
        IStandaloneShareExecutionStore store,
        IStandaloneShareOperationRunner runner,
        TimeProvider? timeProvider = null,
        TimeSpan? leaseDuration = null,
        TimeSpan? rescanInterval = null,
        ILocalResultExportCoordinator? localResultExports = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.leaseDuration = leaseDuration ?? TimeSpan.FromMinutes(30);
        this.rescanInterval = rescanInterval ?? TimeSpan.FromMinutes(1);
        this.localResultExports = localResultExports;
        if (this.leaseDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        if (this.rescanInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(rescanInterval));
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (Interlocked.Exchange(ref started, 1) != 0) return;
        worker = PumpAsync(lifetime.Token);
        await ScanAsync(cancellationToken).ConfigureAwait(false);
        scanner = ScanLoopAsync(lifetime.Token);
    }

    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        while (await store.RecoverExpiredLeasesAsync(now, 1_000, cancellationToken).ConfigureAwait(false) == 1_000)
        {
        }
        for (var pageNumber = 1; ; pageNumber++)
        {
            var page = await store.GetBatchesAsync(new PageQuery(pageNumber, 100), cancellationToken)
                .ConfigureAwait(false);
            foreach (var batch in page.Items.Where(item => item.Status is
                         StandaloneShareBatchStatus.Queued or StandaloneShareBatchStatus.Active))
                Notify(batch.Id);
            if (pageNumber * page.PageSize >= page.TotalCount) break;
        }
    }

    private async Task ScanLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(rescanInterval, timeProvider, cancellationToken).ConfigureAwait(false);
                try
                {
                    await ScanAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // A periodic recovery read is advisory. Keep the scanner alive so a
                    // transient store failure cannot strand durable queued work.
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    public void Notify(Guid batchId)
    {
        if (batchId == Guid.Empty) throw new ArgumentException("A batch is required.", nameof(batchId));
        if (Volatile.Read(ref disposed) != 0) return;
        lock (gate)
        {
            if (!scheduled.Add(batchId)) return;
        }
        if (!batches.Writer.TryWrite(batchId))
        {
            lock (gate) scheduled.Remove(batchId);
        }
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var batchId in batches.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                lock (gate) scheduled.Remove(batchId);
                try { await RunBatchAsync(batchId, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch { }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task RunBatchAsync(Guid batchId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var reconciliation = await store.TryClaimReconciliationAsync(
            batchId, Guid.NewGuid(), now, now.Add(leaseDuration), cancellationToken).ConfigureAwait(false);
        if (reconciliation is not null)
            await runner.ReconcileAsync(reconciliation, cancellationToken).ConfigureAwait(false);

        while (!cancellationToken.IsCancellationRequested)
        {
            now = timeProvider.GetUtcNow();
            var lease = await store.TryLeaseNextAsync(
                batchId, Guid.NewGuid(), now, now.Add(leaseDuration), cancellationToken).ConfigureAwait(false);
            if (lease is null)
            {
                if (localResultExports is not null)
                {
                    try
                    {
                        await localResultExports.ExportStandaloneAsync(batchId, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch
                    {
                        // Export is independently durable and retried by startup recovery.
                    }
                }
                return;
            }
            await runner.ExecuteAsync(lease, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        batches.Writer.TryComplete();
        lifetime.Cancel();
        var tasks = new[] { worker, scanner }.Where(task => task is not null).Cast<Task>().ToArray();
        if (tasks.Length == 0)
        {
            lifetime.Dispose();
            return;
        }

        var completion = Task.WhenAll(tasks);
        try
        {
            await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
            lifetime.Dispose();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = CompleteCleanupAsync(completion, lifetime);
        }
        catch (OperationCanceledException)
        {
            lifetime.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await StopAsync(timeout.Token).ConfigureAwait(false);
    }

    private static async Task CompleteCleanupAsync(Task completion, CancellationTokenSource lifetime)
    {
        try { await completion.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch { }
        finally { lifetime.Dispose(); }
    }
}
