using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.BaiduAdapter.Http;

public sealed class LiveBaiduShareAdapter(BaiduWebApiClient? webApiClient = null) : IBaiduShareAdapter
{
    private readonly BaiduWebApiClient? webApiClient = webApiClient;

    public Task<BaiduAdapterResult<ShareCreationReceipt>> CreatePermanentShareAsync(
        AccountSession session,
        PermanentShareRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return webApiClient is null
            ? NotValidated<ShareCreationReceipt>(session, cancellationToken)
            : webApiClient.CreatePermanentShareAsync(session, request, cancellationToken);
    }

    public Task<BaiduAdapterResult<BaiduSharePage>> ListSharesAsync(
        AccountSession session,
        string? cursor,
        CancellationToken cancellationToken)
    {
        return webApiClient is null
            ? NotValidated<BaiduSharePage>(session, cancellationToken)
            : webApiClient.ListSharesAsync(session, cursor, cancellationToken);
    }

    private static Task<BaiduAdapterResult<T>> NotValidated<T>(
        AccountSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(BaiduAdapterResult<T>.Failure(
            BaiduErrorKind.NotValidated,
            "The live share operation has not passed the user-assisted gate.",
            submissionAcceptance: SubmissionAcceptance.DefinitelyNotAccepted));
    }
}
