using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Scheduling;

/// <summary>
/// Executes transfer phases using a managed-browser reference, never a cookie session.
/// Concrete adapters must still verify live page identity and profile ownership, the write permit,
/// and the persisted submission boundary before taking the corresponding action.
/// </summary>
public interface IManagedChromiumTransferAdapter
{
    Task<BaiduAdapterResult<TransferDirectoryPreparationOutcome>> EnsureDirectoryForOperationAsync(
        TransferOperationAutomationContext context,
        ManagedBrowserSessionReference session,
        RemotePath path,
        CancellationToken cancellationToken);

    Task<BaiduAdapterResult<TransferPreparationReceipt>> PrepareWholeShareForOperationAsync(
        TransferOperationAutomationContext context,
        ManagedBrowserSessionReference session,
        ShareReference share,
        RemoteDirectory destination,
        DuplicatePolicy duplicatePolicy,
        CancellationToken cancellationToken);

    Task<BaiduAdapterResult<TransferReceipt>> SaveWholeShareForOperationAsync(
        TransferOperationAutomationContext context,
        ManagedBrowserSessionReference session,
        ShareReference share,
        RemoteDirectory destination,
        DuplicatePolicy duplicatePolicy,
        CancellationToken cancellationToken);

    Task<BaiduAdapterResult<TransferConfirmationResult>> ConfirmExactTransferForOperationAsync(
        TransferOperationAutomationContext context,
        ManagedBrowserSessionReference session,
        TransferConfirmationQuery query,
        CancellationToken cancellationToken);
}
