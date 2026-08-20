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
/// External profile cleanup begins only after a committed preparation. Completion implementations hold a database
/// writer lease across profile cleanup and the conditional tombstone commit.
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
    /// Completes profile cleanup and tombstoning while holding the database writer lease.
    /// </summary>
    /// <remarks>
    /// Implementations must read the persisted profile path while holding the lease, invoke
    /// <paramref name="externalCleanup"/> before tombstoning, and roll back if cleanup or finalization fails.
    /// They must refuse the callback when the row has been re-enabled or contains credentials.
    /// </remarks>
    Task CompletePreparedRemovalAsync(
        Guid accountId,
        DateTimeOffset removedAt,
        Func<Guid, string?, CancellationToken, Task> externalCleanup,
        CancellationToken token);
}
