namespace BaiduBatchTransfer.Application.Jobs;

public interface ICancelOperationStore
{
    Task CancelBeforeSubmissionAsync(
        Guid operationId,
        DateTimeOffset cancelledAt,
        CancellationToken cancellationToken = default);
}

public sealed class CancelOperationService
{
    private readonly ICancelOperationStore store;
    private readonly TimeProvider timeProvider;

    public CancelOperationService(ICancelOperationStore store, TimeProvider? timeProvider = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task CancelAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An operation ID is required.", nameof(operationId));
        return store.CancelBeforeSubmissionAsync(operationId, timeProvider.GetUtcNow(), cancellationToken);
    }
}
