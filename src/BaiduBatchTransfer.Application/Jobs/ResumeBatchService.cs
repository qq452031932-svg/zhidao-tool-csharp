namespace BaiduBatchTransfer.Application.Jobs;

public sealed class ResumeBatchService(IBatchRunControlStore store)
{
    private readonly IBatchRunControlStore store = store ?? throw new ArgumentNullException(nameof(store));

    public Task<ResumeBatchDisposition> ResumeAsync(
        Guid batchJobId,
        CancellationToken cancellationToken = default)
    {
        if (batchJobId == Guid.Empty) throw new ArgumentException("A batch ID is required.", nameof(batchJobId));
        return store.TryResumeBatchAsync(batchJobId, cancellationToken);
    }
}
