namespace BaiduBatchTransfer.Application.Jobs;

public sealed class RetryFailedTransferService
{
    private readonly IBatchRunControlStore store;
    private readonly TimeProvider timeProvider;

    public RetryFailedTransferService(IBatchRunControlStore store, TimeProvider? timeProvider = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<RetryFailedTransferDisposition> RetryAsync(
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An operation ID is required.", nameof(operationId));
        return store.TryRetryFailedTransferAsync(operationId, timeProvider.GetUtcNow(), cancellationToken);
    }
}
