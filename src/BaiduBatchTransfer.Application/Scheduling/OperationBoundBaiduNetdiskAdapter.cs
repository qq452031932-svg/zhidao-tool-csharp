using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Scheduling;

public sealed record TransferOperationAutomationContext(
    Guid OperationId,
    Guid BatchId,
    Guid AccountId,
    int Generation,
    ShareReference SourceShare,
    bool IsRecovery = false,
    AdapterWriteAuthorizationBinding? AuthorizationBinding = null)
{
    public TransferOperationAutomationContext Validate()
    {
        if (OperationId == Guid.Empty) throw new ArgumentException("An operation is required.", nameof(OperationId));
        if (BatchId == Guid.Empty) throw new ArgumentException("A batch is required.", nameof(BatchId));
        if (AccountId == Guid.Empty) throw new ArgumentException("An account is required.", nameof(AccountId));
        if (Generation < 1) throw new ArgumentOutOfRangeException(nameof(Generation));
        ArgumentNullException.ThrowIfNull(SourceShare);
        return this;
    }

    public override string ToString() =>
        "TransferOperationAutomationContext { Identity = [REDACTED], SourceShare = [REDACTED] }";
}

public sealed record TransferPreparationReceipt
{
    private TransferPreparationReceipt() { }

    public static TransferPreparationReceipt Ready { get; } = new();
}

public interface IOperationBoundBaiduNetdiskAdapter : IBaiduNetdiskAdapter
{
    async Task<BaiduAdapterResult<TransferDirectoryPreparationOutcome>> EnsureDirectoryForOperationAsync(
        TransferOperationAutomationContext context,
        AccountSession session,
        RemotePath path,
        CancellationToken cancellationToken)
    {
        var result = await EnsureDirectoryAsync(session, path, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? BaiduAdapterResult<TransferDirectoryPreparationOutcome>.Success(
                TransferDirectoryPreparationOutcome.Prepared(result.Value!),
                result.SubmissionAcceptance)
            : BaiduAdapterResult<TransferDirectoryPreparationOutcome>.Failure(
                result.ErrorKind!.Value, null, result.RetryAfter, result.SubmissionAcceptance);
    }

    Task<BaiduAdapterResult<TransferPreparationReceipt>> PrepareWholeShareForOperationAsync(
        TransferOperationAutomationContext context,
        AccountSession session,
        ShareReference share,
        RemoteDirectory destination,
        DuplicatePolicy duplicatePolicy,
        CancellationToken cancellationToken);

    Task<BaiduAdapterResult<TransferReceipt>> SaveWholeShareForOperationAsync(
        TransferOperationAutomationContext context,
        AccountSession session,
        ShareReference share,
        RemoteDirectory destination,
        DuplicatePolicy duplicatePolicy,
        CancellationToken cancellationToken);

    Task<BaiduAdapterResult<TransferConfirmationResult>> ConfirmExactTransferForOperationAsync(
        TransferOperationAutomationContext context,
        AccountSession session,
        TransferConfirmationQuery query,
        CancellationToken cancellationToken);
}
