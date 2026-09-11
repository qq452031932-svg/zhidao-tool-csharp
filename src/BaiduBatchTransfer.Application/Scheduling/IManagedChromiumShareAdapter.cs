using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Scheduling;

/// <summary>
/// Executes operation-bound sharing with a managed-browser reference. This port intentionally does
/// not inherit the cookie-session share adapter and cannot service standalone share operations.
/// </summary>
public interface IManagedChromiumShareAdapter
{
    Task<BaiduAdapterResult<ShareDiscoverySnapshot>> DiscoverPermanentSharesForOperationAsync(
        ShareOperationAutomationContext context,
        ManagedBrowserSessionReference session,
        PermanentShareRequest request,
        CancellationToken cancellationToken);

    Task<BaiduAdapterResult<SharePreparationReceipt>> PreparePermanentShareForOperationAsync(
        ShareOperationAutomationContext context,
        ManagedBrowserSessionReference session,
        PermanentShareRequest request,
        CancellationToken cancellationToken);

    Task<BaiduAdapterResult<ShareCreationReceipt>> SubmitPermanentShareForOperationAsync(
        ShareOperationAutomationContext context,
        ManagedBrowserSessionReference session,
        PermanentShareRequest request,
        CancellationToken cancellationToken);

    Task<BaiduAdapterResult<ShareCreationReceipt>> ConfirmExactShareForOperationAsync(
        ShareOperationAutomationContext context,
        ManagedBrowserSessionReference session,
        PermanentShareRequest request,
        CancellationToken cancellationToken);
}
