using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.BaiduAdapter.Http;

public sealed class LiveBaiduShareAdapter : IBaiduShareAdapter
{
    public Task<BaiduAdapterResult<ShareCreationReceipt>> CreatePermanentShareAsync(
        AccountSession session,
        PermanentShareRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return NotValidated<ShareCreationReceipt>(session, cancellationToken);
    }

    public Task<BaiduAdapterResult<BaiduSharePage>> ListSharesAsync(
        AccountSession session,
        string? cursor,
        CancellationToken cancellationToken)
    {
        _ = cursor;
        return NotValidated<BaiduSharePage>(session, cancellationToken);
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
