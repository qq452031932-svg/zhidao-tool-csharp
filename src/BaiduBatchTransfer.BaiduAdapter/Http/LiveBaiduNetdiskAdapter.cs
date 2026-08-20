using System.Collections.Immutable;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.BaiduAdapter.Http;

public sealed class LiveBaiduNetdiskAdapter(BaiduSessionIdentityClient identityClient) : IBaiduNetdiskAdapter
{
    private readonly BaiduSessionIdentityClient identityClient =
        identityClient ?? throw new ArgumentNullException(nameof(identityClient));

    public Task<BaiduAdapterResult<BaiduAccountIdentity>> ValidateSessionAsync(
        AccountSession session,
        CancellationToken cancellationToken) =>
        identityClient.ValidateAsync(session, cancellationToken);

    public Task<BaiduAdapterResult<AccountCapacity>> GetCapacityAsync(
        AccountSession session,
        CancellationToken cancellationToken) => NotValidated<AccountCapacity>(session, cancellationToken);

    public Task<BaiduAdapterResult<ImmutableArray<RemoteDirectory>>> ListDirectoriesAsync(
        AccountSession session,
        RemotePath path,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        return NotValidated<ImmutableArray<RemoteDirectory>>(session, cancellationToken);
    }

    public Task<BaiduAdapterResult<RemoteDirectory>> EnsureDirectoryAsync(
        AccountSession session,
        RemotePath path,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        return NotValidated<RemoteDirectory>(session, cancellationToken);
    }

    public Task<BaiduAdapterResult<ShareMetadata>> InspectShareAsync(
        AccountSession session,
        ShareReference share,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(share);
        return NotValidated<ShareMetadata>(session, cancellationToken);
    }

    public Task<BaiduAdapterResult<TransferReceipt>> SaveWholeShareAsync(
        AccountSession session,
        ShareReference share,
        RemoteDirectory destination,
        DuplicatePolicy duplicatePolicy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(share);
        ArgumentNullException.ThrowIfNull(destination);
        return NotValidated<TransferReceipt>(session, cancellationToken);
    }

    public Task<BaiduAdapterResult<TransferConfirmation>> ConfirmTransferAsync(
        AccountSession session,
        TransferConfirmationQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return NotValidated<TransferConfirmation>(session, cancellationToken);
    }

    private static Task<BaiduAdapterResult<T>> NotValidated<T>(
        AccountSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(BaiduAdapterResult<T>.Failure(
            BaiduErrorKind.UnknownResponse,
            "The live transfer operation has not passed the user-assisted gate."));
    }
}
