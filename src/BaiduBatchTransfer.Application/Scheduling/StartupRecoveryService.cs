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
    ShareLeaseRecoveryResult? ShareRecovery = null);

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
    private readonly TimeProvider timeProvider;

    public StartupRecoveryService(
        ISingleInstanceGuard instanceGuard,
        IStartupDatabaseMaintenance databaseMaintenance,
        IInterruptedOperationRecoveryStore operationRecovery,
        AllocationCounterRebuilder counterRebuilder,
        TimeProvider? timeProvider = null,
        IInterruptedShareOperationRecoveryStore? shareRecovery = null)
    {
        this.instanceGuard = instanceGuard ?? throw new ArgumentNullException(nameof(instanceGuard));
        this.databaseMaintenance = databaseMaintenance ?? throw new ArgumentNullException(nameof(databaseMaintenance));
        this.operationRecovery = operationRecovery ?? throw new ArgumentNullException(nameof(operationRecovery));
        this.counterRebuilder = counterRebuilder ?? throw new ArgumentNullException(nameof(counterRebuilder));
        this.shareRecovery = shareRecovery;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool CoordinatorMayStart { get; private set; }

    public async Task<StartupRecoveryResult> RecoverAsync(CancellationToken cancellationToken = default)
    {
        CoordinatorMayStart = false;
        if (!await instanceGuard.TryAcquireAsync(cancellationToken).ConfigureAwait(false))
            throw new SingleInstanceUnavailableException();

        await databaseMaintenance.PrepareAsync(cancellationToken).ConfigureAwait(false);
        var recoveredAt = timeProvider.GetUtcNow();
        var operations = await operationRecovery.RecoverInterruptedOperationsAsync(
            recoveredAt, cancellationToken).ConfigureAwait(false);
        var shares = shareRecovery is null
            ? null
            : await shareRecovery.RecoverInterruptedShareOperationsAsync(recoveredAt, cancellationToken).ConfigureAwait(false);
        var counters = await counterRebuilder.RebuildAsync(cancellationToken).ConfigureAwait(false);
        CoordinatorMayStart = true;
        return new StartupRecoveryResult(operations, counters, true, shares);
    }
}
