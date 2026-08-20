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

/// <summary>
/// Provides account-scoped listing, removal preview, atomic removal preparation, and tombstone finalization.
/// </summary>
/// <remarks>
/// Implementations own the database transaction that disables an account and clears its credentials.
/// External profile cleanup is deliberately outside that transaction and begins only after a committed preparation.
/// </remarks>
public interface IAccountRemovalStore
{
    Task<AccountPage> GetAccountsPageAsync(int pageNumber, int pageSize, CancellationToken token);

    Task<AccountExitPreview> PreviewAsync(
        Guid accountId,
        AccountExitHistoryMode mode,
        CancellationToken token);

    /// <summary>
    /// Atomically validates the confirmation and prepares the requested account for external profile cleanup.
    /// </summary>
    /// <remarks>
    /// If this method throws, or returns any status other than
    /// <see cref="AccountRemovalPreparationStatus.Prepared"/>, it must have committed zero writes.
    /// A <see cref="AccountRemovalPreparationStatus.Prepared"/> result whose
    /// <see cref="AccountRemovalPreparation.DatabasePrepared"/> value is <see langword="true"/> guarantees that
    /// the account-disable and credential-clearing state has committed and must not be rolled back by later cleanup.
    /// </remarks>
    Task<AccountRemovalPreparation> PrepareRemovalAsync(
        AccountExitRequest request,
        CancellationToken token);

    /// <summary>
    /// Finalizes the already-disabled, credential-free account as a tombstone.
    /// </summary>
    /// <remarks>
    /// Implementations must be idempotent. They must refuse to finalize a row that has been re-enabled or still
    /// contains account credentials.
    /// </remarks>
    Task FinalizeTombstoneAsync(
        Guid accountId,
        DateTimeOffset removedAt,
        CancellationToken token);
}
