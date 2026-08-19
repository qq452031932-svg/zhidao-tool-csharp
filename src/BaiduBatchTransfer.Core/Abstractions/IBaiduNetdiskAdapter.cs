using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Core.Abstractions;

public interface IBaiduNetdiskAdapter
{
    Task<BaiduAdapterResult<BaiduAccountIdentity>> ValidateSessionAsync(
        AccountSession session,
        CancellationToken cancellationToken);

    Task<BaiduAdapterResult<AccountCapacity>> GetCapacityAsync(
        AccountSession session,
        CancellationToken cancellationToken);

    Task<BaiduAdapterResult<IReadOnlyList<RemoteDirectory>>> ListDirectoriesAsync(
        AccountSession session,
        RemotePath path,
        CancellationToken cancellationToken);

    Task<BaiduAdapterResult<RemoteDirectory>> EnsureDirectoryAsync(
        AccountSession session,
        RemotePath path,
        CancellationToken cancellationToken);

    Task<BaiduAdapterResult<ShareMetadata>> InspectShareAsync(
        AccountSession session,
        ShareReference share,
        CancellationToken cancellationToken);

    Task<BaiduAdapterResult<TransferReceipt>> SaveWholeShareAsync(
        AccountSession session,
        ShareReference share,
        RemoteDirectory destination,
        DuplicatePolicy duplicatePolicy,
        CancellationToken cancellationToken);

    Task<BaiduAdapterResult<TransferConfirmation>> ConfirmTransferAsync(
        AccountSession session,
        TransferConfirmationQuery query,
        CancellationToken cancellationToken);
}
