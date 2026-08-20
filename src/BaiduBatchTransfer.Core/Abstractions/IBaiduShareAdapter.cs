using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Core.Abstractions;

public interface IBaiduShareAdapter
{
    Task<BaiduAdapterResult<ShareCreationReceipt>> CreatePermanentShareAsync(
        AccountSession session,
        PermanentShareRequest request,
        CancellationToken cancellationToken);

    Task<BaiduAdapterResult<BaiduSharePage>> ListSharesAsync(
        AccountSession session,
        string? cursor,
        CancellationToken cancellationToken);
}
