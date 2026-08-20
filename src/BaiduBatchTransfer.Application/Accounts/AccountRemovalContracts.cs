namespace BaiduBatchTransfer.Application.Accounts;

public enum AccountExitHistoryMode
{
    KeepHistory,
    RemoveTerminalHistory
}

public enum AccountExitStatus
{
    Ready,
    Removed,
    AlreadyRemoved,
    NotFound,
    ActiveWorkBlocked,
    StaleConfirmation,
    CleanupPending,
    Failed
}

public enum AccountRemovalPreparationStatus
{
    Prepared,
    AlreadyRemoved,
    NotFound,
    ActiveWorkBlocked,
    StaleConfirmation,
    Failed
}

public sealed record AccountExitPreview(
    AccountExitStatus Status,
    string DisplayName,
    int TerminalOperationCount,
    int TerminalBatchCount,
    int SharedBatchCount,
    string ConfirmationToken,
    string SafeMessage)
{
    public override string ToString() =>
        $"AccountExitPreview {{ Status = {Status}, TerminalOperationCount = {TerminalOperationCount}, " +
        $"TerminalBatchCount = {TerminalBatchCount}, SharedBatchCount = {SharedBatchCount}, " +
        "SensitiveData = [REDACTED] }";
}

public sealed record AccountExitRequest(
    Guid AccountId,
    AccountExitHistoryMode HistoryMode,
    string ConfirmationToken,
    DateTimeOffset RemovedAt)
{
    public override string ToString() =>
        $"AccountExitRequest {{ HistoryMode = {HistoryMode}, RemovedAt = {RemovedAt:O}, " +
        "SensitiveData = [REDACTED] }";
}

public sealed record AccountPage(
    IReadOnlyList<AccountListItem> Items,
    int PageNumber,
    int PageSize,
    long TotalCount)
{
    public override string ToString() =>
        $"AccountPage {{ PageNumber = {PageNumber}, PageSize = {PageSize}, TotalCount = {TotalCount}, " +
        "Items = [REDACTED] }";
}

public sealed record AccountListItem(
    Guid AccountId,
    string DisplayName,
    string Status,
    bool IsEnabled)
{
    public override string ToString() =>
        $"AccountListItem {{ Status = {Status}, IsEnabled = {IsEnabled}, SensitiveData = [REDACTED] }}";
}

public sealed record AccountRemovalPreparation(
    AccountRemovalPreparationStatus Status,
    Guid AccountId,
    string? ProfilePath,
    bool DatabasePrepared,
    string SafeMessage)
{
    public override string ToString() =>
        $"AccountRemovalPreparation {{ Status = {Status}, DatabasePrepared = {DatabasePrepared}, " +
        "SensitiveData = [REDACTED] }";
}

public interface IAccountRemovalStore
{
    Task<AccountPage> GetAccountsPageAsync(int pageNumber, int pageSize, CancellationToken token);

    Task<AccountExitPreview> PreviewAsync(
        Guid accountId,
        AccountExitHistoryMode mode,
        CancellationToken token);

    Task<AccountRemovalPreparation> PrepareRemovalAsync(
        AccountExitRequest request,
        CancellationToken token);

    Task FinalizeTombstoneAsync(
        Guid accountId,
        DateTimeOffset removedAt,
        CancellationToken token);
}
