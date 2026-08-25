using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Jobs;

public sealed record StandaloneShareResultProjection
{
    public StandaloneShareResultProjection(
        Guid operationId,
        int sequenceNumber,
        string accountDisplayName,
        string resourceDisplayName,
        RemoteResourceKind resourceKind,
        StandaloneShareOperationStatus status,
        string? shareUrl,
        string? extractionCode,
        string? stableErrorCode)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An operation is required.", nameof(operationId));
        if (sequenceNumber < 1) throw new ArgumentOutOfRangeException(nameof(sequenceNumber));
        ArgumentException.ThrowIfNullOrWhiteSpace(accountDisplayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceDisplayName);
        if (!Enum.IsDefined(resourceKind)) throw new ArgumentOutOfRangeException(nameof(resourceKind));
        if (!Enum.IsDefined(status)) throw new ArgumentOutOfRangeException(nameof(status));
        OperationId = operationId;
        SequenceNumber = sequenceNumber;
        AccountDisplayName = accountDisplayName.Trim();
        ResourceDisplayName = resourceDisplayName.Trim();
        ResourceKind = resourceKind;
        Status = status;
        ShareUrl = string.IsNullOrWhiteSpace(shareUrl) ? null : shareUrl.Trim();
        ExtractionCode = string.IsNullOrWhiteSpace(extractionCode) ? null : extractionCode.Trim();
        StableErrorCode = string.IsNullOrWhiteSpace(stableErrorCode) ? null : stableErrorCode.Trim();
    }

    public Guid OperationId { get; }
    public int SequenceNumber { get; }
    public string AccountDisplayName { get; }
    public string ResourceDisplayName { get; }
    public RemoteResourceKind ResourceKind { get; }
    public StandaloneShareOperationStatus Status { get; }
    public string? ShareUrl { get; }
    public string? ExtractionCode { get; }
    public string? StableErrorCode { get; }

    public override string ToString() =>
        $"StandaloneShareResultProjection {{ OperationId = {OperationId}, SequenceNumber = {SequenceNumber}, Status = {Status}, UserContent = [REDACTED] }}";
}

public sealed record StandaloneShareResultProjectionPage(
    IReadOnlyList<StandaloneShareResultProjection> Items,
    int? NextSequenceNumber);

public interface IStandaloneShareResultProjectionRepository
{
    Task<StandaloneShareResultProjectionPage> GetPageAsync(
        Guid batchId,
        int afterSequenceNumber,
        int pageSize,
        CancellationToken cancellationToken = default);
}

public sealed record StandaloneShareResultExportRequest(Guid BatchId, int PageSize = 500);

public sealed record StandaloneShareResultExportResult(long ExportedRows, string SuggestedFileName)
{
    public override string ToString() =>
        $"StandaloneShareResultExportResult {{ ExportedRows = {ExportedRows}, SuggestedFileName = [REDACTED] }}";
}
