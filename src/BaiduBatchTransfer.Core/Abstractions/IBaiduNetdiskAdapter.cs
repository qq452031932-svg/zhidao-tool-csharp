using BaiduBatchTransfer.Core.Domain;
using System.Collections.Immutable;

namespace BaiduBatchTransfer.Core.Abstractions;

public interface IBaiduNetdiskAdapter
{
    Task<BaiduAdapterResult<BaiduAccountIdentity>> ValidateSessionAsync(
        AccountSession session,
        CancellationToken cancellationToken);

    Task<BaiduAdapterResult<AccountCapacity>> GetCapacityAsync(
        AccountSession session,
        CancellationToken cancellationToken);

    Task<BaiduAdapterResult<ImmutableArray<RemoteDirectory>>> ListDirectoriesAsync(
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

    async Task<BaiduAdapterResult<TransferConfirmationResult>> ConfirmExactTransferAsync(
        AccountSession session,
        TransferConfirmationQuery query,
        CancellationToken cancellationToken)
    {
        var legacy = await ConfirmTransferAsync(session, query, cancellationToken).ConfigureAwait(false);
        if (!legacy.IsSuccess)
        {
            return BaiduAdapterResult<TransferConfirmationResult>.Failure(
                legacy.ErrorKind!.Value,
                adapterDiagnostic: null,
                legacy.RetryAfter,
                legacy.SubmissionAcceptance);
        }

        return BaiduAdapterResult<TransferConfirmationResult>.Success(
            legacy.Value.ToExactResult(),
            legacy.SubmissionAcceptance);
    }
}
