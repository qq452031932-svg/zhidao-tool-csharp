using System.Collections.Immutable;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.BaiduAdapter.Http;

public sealed class LiveBaiduNetdiskAdapter :
    IBaiduNetdiskAdapter,
    ISourceAwareBaiduTransferConfirmationAdapter
{
    public static AdapterCapability Capability { get; } = new(
        transferValidated: false,
        shareValidated: false,
        "百度转存和分享能力尚未通过现场验收。");

    private readonly BaiduSessionIdentityClient identityClient;
    private readonly BaiduWebApiClient? webApiClient;

    public LiveBaiduNetdiskAdapter(
        BaiduSessionIdentityClient identityClient,
        BaiduWebApiClient? webApiClient = null)
    {
        this.identityClient = identityClient ?? throw new ArgumentNullException(nameof(identityClient));
        this.webApiClient = webApiClient;
    }

    public Task<BaiduAdapterResult<BaiduAccountIdentity>> ValidateSessionAsync(
        AccountSession session,
        CancellationToken cancellationToken) =>
        identityClient.ValidateAsync(session, cancellationToken);

    public Task<BaiduAdapterResult<AccountCapacity>> GetCapacityAsync(
        AccountSession session,
        CancellationToken cancellationToken) => identityClient.GetCapacityAsync(session, cancellationToken);

    public Task<BaiduAdapterResult<ImmutableArray<RemoteDirectory>>> ListDirectoriesAsync(
        AccountSession session,
        RemotePath path,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        return webApiClient is null
            ? NotValidated<ImmutableArray<RemoteDirectory>>(session, cancellationToken)
            : webApiClient.ListDirectoriesAsync(session, path, cancellationToken);
    }

    public Task<BaiduAdapterResult<RemoteDirectory>> EnsureDirectoryAsync(
        AccountSession session,
        RemotePath path,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        return webApiClient is null
            ? NotValidated<RemoteDirectory>(session, cancellationToken)
            : webApiClient.EnsureDirectoryAsync(session, path, cancellationToken);
    }

    public Task<BaiduAdapterResult<ShareMetadata>> InspectShareAsync(
        AccountSession session,
        ShareReference share,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(share);
        return webApiClient is null
            ? NotValidated<ShareMetadata>(session, cancellationToken)
            : webApiClient.InspectShareAsync(session, share, cancellationToken);
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
        return webApiClient is null
            ? NotValidated<TransferReceipt>(session, cancellationToken)
            : webApiClient.SaveWholeShareAsync(
                session, share, destination, duplicatePolicy, cancellationToken);
    }

    public Task<BaiduAdapterResult<TransferConfirmation>> ConfirmTransferAsync(
        AccountSession session,
        TransferConfirmationQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return NotValidated<TransferConfirmation>(session, cancellationToken);
    }

    public Task<BaiduAdapterResult<TransferConfirmationResult>> ConfirmExactTransferAsync(
        AccountSession session,
        TransferConfirmationQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return NotValidated<TransferConfirmationResult>(session, cancellationToken);
    }

    public Task<BaiduAdapterResult<TransferConfirmationResult>> ConfirmExactTransferAsync(
        AccountSession session,
        ShareReference sourceShare,
        TransferConfirmationQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceShare);
        ArgumentNullException.ThrowIfNull(query);
        return webApiClient is null
            ? NotValidated<TransferConfirmationResult>(session, cancellationToken)
            : webApiClient.ConfirmExactTransferAsync(session, sourceShare, query, cancellationToken);
    }

    private static Task<BaiduAdapterResult<T>> NotValidated<T>(
        AccountSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(BaiduAdapterResult<T>.Failure(
            BaiduErrorKind.NotValidated,
            "The live operation has not passed the user-assisted gate.",
            submissionAcceptance: SubmissionAcceptance.DefinitelyNotAccepted));
    }
}
