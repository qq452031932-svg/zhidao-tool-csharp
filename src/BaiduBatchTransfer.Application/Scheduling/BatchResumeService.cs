namespace BaiduBatchTransfer.Application.Scheduling;

public interface IUserConfirmedBatchResumeStore
{
    Task<bool> QueuePausedReadyAsync(Guid batchJobId, CancellationToken cancellationToken = default);
}

public sealed class BatchResumeService
{
    private readonly IUserConfirmedBatchResumeStore store;

    public BatchResumeService(IUserConfirmedBatchResumeStore store) =>
        this.store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task ConfirmReadyForQueueAsync(
        Guid batchJobId,
        bool userConfirmed,
        CancellationToken cancellationToken = default)
    {
        if (batchJobId == Guid.Empty) throw new ArgumentException("A batch ID is required.", nameof(batchJobId));
        if (!userConfirmed) throw new InvalidOperationException("Explicit user confirmation is required to queue this batch.");
        if (!await store.QueuePausedReadyAsync(batchJobId, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("The batch is no longer ready to be queued.");
    }
}
