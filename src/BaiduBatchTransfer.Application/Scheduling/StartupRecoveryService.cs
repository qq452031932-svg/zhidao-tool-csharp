using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.Application.Scheduling;

public interface IStartupDatabaseMaintenance
{
    Task PrepareAsync(CancellationToken cancellationToken = default);
}

public interface IInterruptedOperationRecoveryStore
{
    Task<LeaseRecoveryResult> RecoverInterruptedOperationsAsync(
        DateTimeOffset recoveredAt,
        CancellationToken cancellationToken = default);
}

public sealed record ShareLeaseRecoveryResult(int RetryableOperationCount, int ResultPendingOperationCount);

public interface IInterruptedShareOperationRecoveryStore
{
    Task<ShareLeaseRecoveryResult> RecoverInterruptedShareOperationsAsync(
        DateTimeOffset recoveredAt,
        CancellationToken cancellationToken = default);

    Task<ShareLeaseRecoveryResult> RecoverExpiredShareOperationsAsync(
        DateTimeOffset recoveredAt,
        CancellationToken cancellationToken = default);
}

public sealed record StartupRecoveryResult(
    LeaseRecoveryResult OperationRecovery,
    AllocationCounterRebuildResult CounterRebuild,
    bool CoordinatorMayStart,
    ShareLeaseRecoveryResult? ShareRecovery = null,
    EndAllResult? StoppedLeftRunning = null);

public sealed class SingleInstanceUnavailableException : InvalidOperationException
{
    public SingleInstanceUnavailableException()
        : base("Another application instance is already active.")
    {
    }
}

public sealed class StartupRecoveryService
{
    private readonly ISingleInstanceGuard instanceGuard;
    private readonly IStartupDatabaseMaintenance databaseMaintenance;
    private readonly IInterruptedOperationRecoveryStore operationRecovery;
    private readonly AllocationCounterRebuilder counterRebuilder;
    private readonly IInterruptedShareOperationRecoveryStore? shareRecovery;
    private readonly EndBatchService? leftRunningBatches;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim preparationLock = new(1, 1);
    private bool localPrepared;

    public StartupRecoveryService(
        ISingleInstanceGuard instanceGuard,
        IStartupDatabaseMaintenance databaseMaintenance,
        IInterruptedOperationRecoveryStore operationRecovery,
        AllocationCounterRebuilder counterRebuilder,
        TimeProvider? timeProvider = null,
        IInterruptedShareOperationRecoveryStore? shareRecovery = null,
        EndBatchService? leftRunningBatches = null)
    {
        this.instanceGuard = instanceGuard ?? throw new ArgumentNullException(nameof(instanceGuard));
        this.databaseMaintenance = databaseMaintenance ?? throw new ArgumentNullException(nameof(databaseMaintenance));
        this.operationRecovery = operationRecovery ?? throw new ArgumentNullException(nameof(operationRecovery));
        this.counterRebuilder = counterRebuilder ?? throw new ArgumentNullException(nameof(counterRebuilder));
        this.shareRecovery = shareRecovery;
        this.leftRunningBatches = leftRunningBatches;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool CoordinatorMayStart { get; private set; }

    public async Task PrepareLocalAsync(CancellationToken cancellationToken = default)
    {
        await preparationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (localPrepared) return;

            CoordinatorMayStart = false;
            if (!await instanceGuard.TryAcquireAsync(cancellationToken).ConfigureAwait(false))
                throw new SingleInstanceUnavailableException();

            await databaseMaintenance.PrepareAsync(cancellationToken).ConfigureAwait(false);
            localPrepared = true;
        }
        finally
        {
            preparationLock.Release();
        }
    }

    public async Task<StartupRecoveryResult> RecoverPreparedAsync(CancellationToken cancellationToken = default)
    {
        if (!localPrepared)
            throw new InvalidOperationException("Local startup preparation has not completed.");

        CoordinatorMayStart = false;
        var recoveredAt = timeProvider.GetUtcNow();
        var operations = await operationRecovery.RecoverInterruptedOperationsAsync(
            recoveredAt, cancellationToken).ConfigureAwait(false);
        var shares = shareRecovery is null
            ? null
            : await shareRecovery.RecoverInterruptedShareOperationsAsync(recoveredAt, cancellationToken).ConfigureAwait(false);
        // 上次关闭时没有暂停的批次按停止处理：中断的操作刚被放回队列，在调度器启动之前把它们取消，
        // 只留已经提交、需要核对结果的那几条收尾。
        var stopped = leftRunningBatches is null
            ? null
            : await leftRunningBatches.EndAllLeftRunningAsync(cancellationToken).ConfigureAwait(false);
        var counters = await counterRebuilder.RebuildAsync(cancellationToken).ConfigureAwait(false);
        CoordinatorMayStart = true;
        return new StartupRecoveryResult(operations, counters, true, shares, stopped);
    }

    public async Task<StartupRecoveryResult> RecoverAsync(CancellationToken cancellationToken = default)
    {
        await PrepareLocalAsync(cancellationToken).ConfigureAwait(false);
        return await RecoverPreparedAsync(cancellationToken).ConfigureAwait(false);
    }
}
