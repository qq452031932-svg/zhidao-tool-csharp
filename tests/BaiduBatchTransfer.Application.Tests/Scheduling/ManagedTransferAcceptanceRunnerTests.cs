using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Application.Scheduling;

namespace BaiduBatchTransfer.Application.Tests.Scheduling;

public sealed class ManagedTransferAcceptanceRunnerTests
{
    [Fact]
    public async Task ExecutesOnlyTheExactTransferThenLinkedShareThenExport()
    {
        var store = new Store();
        var transfer = new TransferExecutor();
        var share = new ShareExecutor();
        var export = new ExportCoordinator();
        var runner = new ManagedTransferAcceptanceRunner(
            store, transfer, share, export, new Clock(), TimeSpan.FromMinutes(5));

        var outcome = await runner.ExecuteAsync(store.BatchId);

        Assert.Equal(ManagedTransferAcceptanceRunDisposition.Completed, outcome.Disposition);
        Assert.Equal(store.TransferOperationId, transfer.OperationId);
        Assert.Equal(new ShareExecutionLease(store.ShareOperationId, 1, 1), share.Lease);
        Assert.Equal(store.BatchId, export.BatchId);
        Assert.Equal(1, store.FinalizeCount);
    }

    [Fact]
    public async Task MissingLinkedShareStopsWithoutExportOrFinalization()
    {
        var store = new Store { ReturnShareLease = false };
        var export = new ExportCoordinator();
        var runner = new ManagedTransferAcceptanceRunner(
            store, new TransferExecutor(), new ShareExecutor(), export, new Clock());

        var outcome = await runner.ExecuteAsync(store.BatchId);

        Assert.Equal(ManagedTransferAcceptanceRunDisposition.AwaitingTransferResolution, outcome.Disposition);
        Assert.Null(export.BatchId);
        Assert.Equal(0, store.FinalizeCount);
    }

    private sealed class Store : IManagedTransferAcceptanceStore
    {
        public Guid BatchId { get; } = Guid.NewGuid();
        public Guid TransferOperationId { get; } = Guid.NewGuid();
        public Guid ShareOperationId { get; } = Guid.NewGuid();
        public Guid AccountId { get; } = Guid.NewGuid();
        public bool ReturnShareLease { get; init; } = true;
        public int FinalizeCount { get; private set; }

        public Task<ManagedTransferAcceptanceReservation?> TryReserveTransferAsync(
            Guid batchId, DateTimeOffset reservedAt, DateTimeOffset leaseExpiresAt,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ManagedTransferAcceptanceReservation?>(batchId == BatchId
                ? new(BatchId, TransferOperationId, ShareOperationId, AccountId, 1)
                : null);

        public Task<ShareExecutionLease?> TryReserveLinkedShareAsync(
            ManagedTransferAcceptanceReservation reservation,
            DateTimeOffset reservedAt, DateTimeOffset leaseExpiresAt,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ShareExecutionLease?>(ReturnShareLease
                ? new(ShareOperationId, 1, 1)
                : null);

        public Task<bool> TryFinalizeAsync(
            ManagedTransferAcceptanceReservation reservation, DateTimeOffset updatedAt,
            CancellationToken cancellationToken = default)
        {
            FinalizeCount++;
            return Task.FromResult(true);
        }
    }

    private sealed class TransferExecutor : ITransferOperationExecutor
    {
        public Guid? OperationId { get; private set; }
        public Task ExecuteAsync(Guid operationId, CancellationToken cancellationToken = default)
        {
            OperationId = operationId;
            return Task.CompletedTask;
        }
    }

    private sealed class ShareExecutor : IShareOperationExecutor
    {
        public ShareExecutionLease? Lease { get; private set; }
        public Task<ShareExecutionResult> ExecuteAsync(
            ShareExecutionLease lease, CancellationToken cancellationToken = default)
        {
            Lease = lease;
            return Task.FromResult(new ShareExecutionResult(ShareExecutionDisposition.Succeeded, "ok"));
        }
    }

    private sealed class ExportCoordinator : ILocalResultExportCoordinator
    {
        public Guid? BatchId { get; private set; }
        public Task<LocalResultExportOutcome> ExportTransferAsync(
            Guid batchId, CancellationToken cancellationToken = default)
        {
            BatchId = batchId;
            return Task.FromResult(new LocalResultExportOutcome(LocalResultExportDisposition.Exported, "result.xlsx"));
        }

        public Task<LocalResultExportOutcome> ExportStandaloneAsync(
            Guid batchId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> RecoverAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);
    }
}
