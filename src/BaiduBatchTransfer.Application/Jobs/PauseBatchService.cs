namespace BaiduBatchTransfer.Application.Jobs;

public sealed class PauseBatchService(IBatchRunControlStore store)
{
    private readonly IBatchRunControlStore store = store ?? throw new ArgumentNullException(nameof(store));

    public Task<PauseBatchDisposition> PauseAsync(
        Guid batchJobId,
        CancellationToken cancellationToken = default)
    {
        if (batchJobId == Guid.Empty) throw new ArgumentException("A batch ID is required.", nameof(batchJobId));
        return store.TryPauseBatchAsync(batchJobId, cancellationToken);
    }
}
