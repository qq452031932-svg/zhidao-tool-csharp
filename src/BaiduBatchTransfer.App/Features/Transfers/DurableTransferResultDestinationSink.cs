using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.App.Features.Transfers;

public sealed class DurableTransferResultDestinationSink(
    ILocalResultDestinationStore store,
    TimeProvider? timeProvider = null) : ITransferResultDestinationSink
{
    private readonly ILocalResultDestinationStore store =
        store ?? throw new ArgumentNullException(nameof(store));
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

    public Task SetAsync(
        TransferResultDestination destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var validated = destination.Validate();
        return store.SetAsync(LocalResultDestinationRecord.Create(
            validated.BatchJobId,
            validated.ResultDirectory,
            timeProvider.GetUtcNow()), cancellationToken);
    }
}
