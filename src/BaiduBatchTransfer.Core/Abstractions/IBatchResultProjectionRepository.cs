using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Core.Abstractions;

public sealed record BatchResultOriginalColumn(int ColumnOrdinal, string Header)
{
    public override string ToString() =>
        $"BatchResultOriginalColumn {{ ColumnOrdinal = {ColumnOrdinal}, Header = [REDACTED] }}";
}

public sealed record BatchResultOriginalValue(int ColumnOrdinal, string? Text)
{
    public override string ToString() =>
        $"BatchResultOriginalValue {{ ColumnOrdinal = {ColumnOrdinal}, Text = [REDACTED] }}";
}

public sealed record BatchResultProjectionAccount(
    Guid AccountId,
    int OutputOrdinal,
    string DisplayName,
    bool ShareEnabled)
{
    public override string ToString() =>
        $"BatchResultProjectionAccount {{ Identity = [REDACTED], OutputOrdinal = {OutputOrdinal}, ShareEnabled = {ShareEnabled}, DisplayName = [REDACTED] }}";
}

public sealed record BatchResultProjectionDefinition(
    string SourceFileName,
    IReadOnlyList<BatchResultOriginalColumn> OriginalColumns,
    IReadOnlyList<BatchResultProjectionAccount> Accounts)
{
    public override string ToString() =>
        $"BatchResultProjectionDefinition {{ OriginalColumns = {OriginalColumns.Count}, Accounts = {Accounts.Count}, SourceFileName = [REDACTED] }}";
}

public sealed record BatchResultResourceProjection(
    Guid ResourceItemId,
    long CandidateSequence,
    IReadOnlyList<BatchResultOriginalValue> OriginalValues,
    string ResolvedTitle,
    string OriginalShareUrl)
{
    public override string ToString() =>
        $"BatchResultResourceProjection {{ Identity = [REDACTED], CandidateSequence = {CandidateSequence}, OriginalValues = {OriginalValues.Count}, UserContent = [REDACTED] }}";
}

public sealed record BatchResultAccountProjection(
    Guid ResourceItemId,
    Guid AccountId,
    int OutputOrdinal,
    string TransferStatus,
    ShareOperationStatus? ShareStatus,
    string? ShareUrl,
    string? ExtractionCode,
    string? StableErrorCode,
    DateTimeOffset? SharedAt = null)
{
    public override string ToString() =>
        $"BatchResultAccountProjection {{ Identity = [REDACTED], OutputOrdinal = {OutputOrdinal}, TransferStatus = {TransferStatus}, ShareStatus = {ShareStatus}, SharedAt = {SharedAt:O}, UserContent = [REDACTED] }}";
}

public sealed record BatchResultProjectionPage(
    IReadOnlyList<BatchResultResourceProjection> Resources,
    IReadOnlyList<BatchResultAccountProjection> AccountResults,
    long? NextCandidateSequence)
{
    public override string ToString() =>
        $"BatchResultProjectionPage {{ Resources = {Resources.Count}, AccountResults = {AccountResults.Count}, HasNext = {NextCandidateSequence is not null}, UserContent = [REDACTED] }}";
}

public interface IBatchResultProjectionRepository
{
    Task<BatchResultProjectionDefinition?> GetDefinitionAsync(
        Guid batchJobId,
        CancellationToken cancellationToken = default);

    Task<BatchResultProjectionPage> GetProjectionPageAsync(
        Guid batchJobId,
        long afterCandidateSequence,
        int pageSize,
        CancellationToken cancellationToken = default);
}
