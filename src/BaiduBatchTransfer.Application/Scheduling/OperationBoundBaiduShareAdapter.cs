using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Scheduling;

public sealed record ShareOperationAutomationContext(
    Guid OperationId,
    Guid BatchId,
    Guid AccountId,
    int Generation,
    WebAutomationOwnerKind OwnerKind,
    RemoteItemIdentitySet ExactRemoteItems)
{
    public ShareOperationAutomationContext Validate()
    {
        if (OperationId == Guid.Empty) throw new ArgumentException("An operation is required.", nameof(OperationId));
        if (BatchId == Guid.Empty) throw new ArgumentException("A batch is required.", nameof(BatchId));
        if (AccountId == Guid.Empty) throw new ArgumentException("An account is required.", nameof(AccountId));
        if (Generation < 1) throw new ArgumentOutOfRangeException(nameof(Generation));
        if (OwnerKind is not (WebAutomationOwnerKind.TransferShare or WebAutomationOwnerKind.StandaloneShare))
            throw new ArgumentOutOfRangeException(nameof(OwnerKind));
        ArgumentNullException.ThrowIfNull(ExactRemoteItems);
        return this;
    }

    public override string ToString() =>
        $"ShareOperationAutomationContext {{ Identity = [REDACTED], Generation = {Generation}, OwnerKind = {OwnerKind}, ExactRemoteItems = [REDACTED] }}";
}

public sealed record SharePreparationReceipt
{
    private SharePreparationReceipt() { }

    public static SharePreparationReceipt Ready { get; } = new();
}

public interface IOperationBoundBaiduShareAdapter : IBaiduShareAdapter
{
    Task<BaiduAdapterResult<SharePreparationReceipt>> PreparePermanentShareForOperationAsync(
        ShareOperationAutomationContext context,
        AccountSession session,
        PermanentShareRequest request,
        CancellationToken cancellationToken);

    Task<BaiduAdapterResult<ShareCreationReceipt>> SubmitPermanentShareForOperationAsync(
        ShareOperationAutomationContext context,
        AccountSession session,
        PermanentShareRequest request,
        CancellationToken cancellationToken);

    Task<BaiduAdapterResult<ShareCreationReceipt>> ConfirmExactShareForOperationAsync(
        ShareOperationAutomationContext context,
        AccountSession session,
        PermanentShareRequest request,
        CancellationToken cancellationToken);
}
