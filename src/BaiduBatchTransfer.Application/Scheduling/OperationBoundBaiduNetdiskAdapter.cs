using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Scheduling;

public sealed record TransferOperationAutomationContext(
    Guid OperationId,
    Guid BatchId,
    Guid AccountId,
    int Generation,
    ShareReference SourceShare)
{
    public TransferOperationAutomationContext Validate()
    {
        if (OperationId == Guid.Empty) throw new ArgumentException("An operation is required.", nameof(OperationId));
        if (BatchId == Guid.Empty) throw new ArgumentException("A batch is required.", nameof(BatchId));
        if (AccountId == Guid.Empty) throw new ArgumentException("An account is required.", nameof(AccountId));
        if (Generation < 1) throw new ArgumentOutOfRangeException(nameof(Generation));
        ArgumentNullException.ThrowIfNull(SourceShare);
        return this;
    }

    public override string ToString() =>
        "TransferOperationAutomationContext { Identity = [REDACTED], SourceShare = [REDACTED] }";
}

public interface IOperationBoundBaiduNetdiskAdapter : IBaiduNetdiskAdapter
{
    Task<BaiduAdapterResult<TransferReceipt>> SaveWholeShareForOperationAsync(
        TransferOperationAutomationContext context,
        AccountSession session,
        ShareReference share,
        RemoteDirectory destination,
        DuplicatePolicy duplicatePolicy,
        CancellationToken cancellationToken);

    Task<BaiduAdapterResult<TransferConfirmationResult>> ConfirmExactTransferForOperationAsync(
        TransferOperationAutomationContext context,
        AccountSession session,
        TransferConfirmationQuery query,
        CancellationToken cancellationToken);
}
