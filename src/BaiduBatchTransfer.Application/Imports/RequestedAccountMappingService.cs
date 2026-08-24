using System.Collections.Immutable;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Imports;

public enum AccountNameMatchStatus
{
    Unique,
    Ambiguous,
    Unmatched,
}

public sealed record RequestedAccountNameGroup(
    string RequestedAccountName,
    long RowCount,
    AccountNameMatchStatus Status,
    IReadOnlyList<Guid> MatchingAccountIds)
{
    public override string ToString() =>
        $"RequestedAccountNameGroup {{ RequestedAccountName = [REDACTED], RowCount = {RowCount}, Status = {Status}, MatchingAccounts = {MatchingAccountIds.Count} }}";
}

public enum AccountMappingAction
{
    PreserveUnique,
    Map,
    Ignore,
}

public sealed record AccountMappingDecision
{
    private AccountMappingDecision(string requestedAccountName, AccountMappingAction action, Guid? accountId)
    {
        RequestedAccountName = string.IsNullOrWhiteSpace(requestedAccountName)
            ? throw new ArgumentException("A requested account name is required.", nameof(requestedAccountName))
            : requestedAccountName.Trim();
        Action = action;
        AccountId = accountId;
    }

    public string RequestedAccountName { get; }
    public AccountMappingAction Action { get; }
    public Guid? AccountId { get; }

    public static AccountMappingDecision PreserveUnique(string requestedAccountName) =>
        new(requestedAccountName, AccountMappingAction.PreserveUnique, null);
    public static AccountMappingDecision Map(string requestedAccountName, Guid accountId) =>
        new(requestedAccountName, AccountMappingAction.Map,
            accountId == Guid.Empty ? throw new ArgumentException("An account is required.", nameof(accountId)) : accountId);
    public static AccountMappingDecision Ignore(string requestedAccountName) =>
        new(requestedAccountName, AccountMappingAction.Ignore, null);

    public override string ToString() =>
        $"AccountMappingDecision {{ RequestedAccountName = [REDACTED], Action = {Action}, AccountId = [REDACTED] }}";
}

public sealed class RequestedAccountMappingService(IUnitOfWorkFactory unitOfWorkFactory)
{
    private const int PageSize = 1_000;
    private readonly IUnitOfWorkFactory unitOfWorkFactory =
        unitOfWorkFactory ?? throw new ArgumentNullException(nameof(unitOfWorkFactory));

    public async Task<RequestedAccountResolutionCounts> GetResolutionCountsAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        await using var unit = await unitOfWorkFactory.BeginAsync(cancellationToken).ConfigureAwait(false);
        _ = await unit.Imports.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The import session does not exist.");
        var result = await unit.Imports.GetRequestedAccountResolutionCountsAsync(sessionId, cancellationToken)
            .ConfigureAwait(false);
        await unit.RollbackAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<Page<RequestedAccountNameGroup>> GetGroupsPageAsync(
        Guid sessionId,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        await using var unit = await unitOfWorkFactory.BeginAsync(cancellationToken).ConfigureAwait(false);
        _ = await DraftReclassificationEngine.RequireMutableSessionAsync(unit.Imports, sessionId, cancellationToken)
            .ConfigureAwait(false);
        var groups = await unit.Imports.GetRequestedAccountNameGroupsPageAsync(
            sessionId, new PageQuery(pageNumber, pageSize), cancellationToken).ConfigureAwait(false);
        var accounts = await ReadActiveAccountsAsync(unit.Accounts, cancellationToken).ConfigureAwait(false);
        var byName = accounts.GroupBy(account => account.DisplayName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(account => account.Id).Order().ToImmutableArray(), StringComparer.Ordinal);
        var items = groups.Items.Select(group =>
        {
            var matches = byName.GetValueOrDefault(group.RequestedAccountName, []);
            var status = matches.Length switch
            {
                0 => AccountNameMatchStatus.Unmatched,
                1 => AccountNameMatchStatus.Unique,
                _ => AccountNameMatchStatus.Ambiguous,
            };
            return new RequestedAccountNameGroup(group.RequestedAccountName, group.RowCount, status, matches);
        }).ToArray();
        return new(items, groups.TotalCount, pageNumber, pageSize);
    }

    public async Task<DraftMutationResult> ApplyAsync(
        Guid sessionId,
        IReadOnlyList<AccountMappingDecision> decisions,
        TransferMode transferMode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        if (decisions.Select(x => x.RequestedAccountName).Distinct(StringComparer.Ordinal).Count() != decisions.Count)
            throw new DraftWorkflowException("DRAFT_ACCOUNT_DECISION_DUPLICATE", "账号映射决定存在重复项。");
        await using var unit = await unitOfWorkFactory.BeginAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = await DraftReclassificationEngine.RequireMutableSessionAsync(unit.Imports, sessionId, cancellationToken)
                .ConfigureAwait(false);
            var accounts = await ReadActiveAccountsAsync(unit.Accounts, cancellationToken).ConfigureAwait(false);
            var accountById = accounts.ToDictionary(x => x.Id);
            var byName = accounts.GroupBy(x => x.DisplayName, StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.Select(a => a.Id).ToArray(), StringComparer.Ordinal);
            var resolved = new Dictionary<string, (Guid? AccountId, bool Ignore)>(StringComparer.Ordinal);
            foreach (var decision in decisions)
            {
                switch (decision.Action)
                {
                    case AccountMappingAction.PreserveUnique:
                        if (!byName.TryGetValue(decision.RequestedAccountName, out var matches) || matches.Length != 1)
                            throw new DraftWorkflowException("DRAFT_ACCOUNT_NAME_NOT_UNIQUE", "指定账号名称未唯一匹配，必须人工处理。");
                        resolved.Add(decision.RequestedAccountName, (matches[0], false));
                        break;
                    case AccountMappingAction.Map:
                        if (decision.AccountId is not { } target || !accountById.ContainsKey(target))
                            throw new DraftWorkflowException("DRAFT_ACCOUNT_TARGET_UNAVAILABLE", "映射目标账号不可用。");
                        resolved.Add(decision.RequestedAccountName, (target, false));
                        break;
                    case AccountMappingAction.Ignore:
                        resolved.Add(decision.RequestedAccountName, (null, true));
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(decisions));
                }
            }

            long cursor = 0;
            while (true)
            {
                var page = await unit.Imports.GetRowsKeysetPageAsync(
                    new StagedImportKeysetQuery(sessionId, null, null, PageSize, cursor, false), cancellationToken)
                    .ConfigureAwait(false);
                var changed = new List<StagedImportRowRecord>();
                foreach (var row in page.Items)
                {
                    if (row.RequestedAccountName is null || !resolved.TryGetValue(row.RequestedAccountName, out var mapping))
                        continue;
                    var desired = row with
                    {
                        RequestedAccountName = mapping.Ignore ? null : row.RequestedAccountName,
                        MappedAccountId = mapping.AccountId,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    };
                    if (desired.RequestedAccountName != row.RequestedAccountName || desired.MappedAccountId != row.MappedAccountId)
                        changed.Add(desired);
                }
                if (changed.Count != 0) await unit.Imports.UpdateRowsAsync(changed, cancellationToken).ConfigureAwait(false);
                if (page.NextCursor is null) break;
                cursor = page.NextCursor.Value;
            }
            var result = await DraftReclassificationEngine.ReclassifyAsync(unit.Imports, session, transferMode, cancellationToken)
                .ConfigureAwait(false);
            await unit.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch
        {
            try { await unit.RollbackAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            throw;
        }
    }

    public Task<DraftMutationResult> OverrideAllAsync(
        Guid sessionId,
        Guid accountId,
        TransferMode transferMode,
        CancellationToken cancellationToken = default)
    {
        if (accountId == Guid.Empty) throw new ArgumentException("An account is required.", nameof(accountId));
        return ApplyAllAsync(sessionId, accountId, transferMode, cancellationToken);
    }

    public Task<DraftMutationResult> IgnoreAllAsync(
        Guid sessionId,
        TransferMode transferMode,
        CancellationToken cancellationToken = default) =>
        ApplyAllAsync(sessionId, null, transferMode, cancellationToken);

    private async Task<DraftMutationResult> ApplyAllAsync(
        Guid sessionId,
        Guid? accountId,
        TransferMode transferMode,
        CancellationToken cancellationToken)
    {
        await using var unit = await unitOfWorkFactory.BeginAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = await DraftReclassificationEngine.RequireMutableSessionAsync(unit.Imports, sessionId, cancellationToken)
                .ConfigureAwait(false);
            if (accountId is { } selected)
            {
                var account = await unit.Accounts.GetByIdAsync(selected, cancellationToken).ConfigureAwait(false);
                if (account is null || !account.IsEnabled || account.IsDeleted || account.Status != "Active")
                    throw new DraftWorkflowException("DRAFT_ACCOUNT_TARGET_UNAVAILABLE", "覆盖目标账号不可用。");
            }
            long cursor = 0;
            while (true)
            {
                var page = await unit.Imports.GetRowsKeysetPageAsync(
                    new StagedImportKeysetQuery(sessionId, null, null, PageSize, cursor, false), cancellationToken)
                    .ConfigureAwait(false);
                var changed = page.Items.Where(row => row.Disposition != "Excluded").Select(row => row with
                {
                    RequestedAccountName = (string?)null,
                    MappedAccountId = accountId,
                    UpdatedAt = DateTimeOffset.UtcNow,
                }).ToArray();
                if (changed.Length != 0) await unit.Imports.UpdateRowsAsync(changed, cancellationToken).ConfigureAwait(false);
                if (page.NextCursor is null) break;
                cursor = page.NextCursor.Value;
            }
            var result = await DraftReclassificationEngine.ReclassifyAsync(unit.Imports, session, transferMode, cancellationToken)
                .ConfigureAwait(false);
            await unit.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch
        {
            try { await unit.RollbackAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            throw;
        }
    }

    private static async Task<IReadOnlyList<AccountRecord>> ReadActiveAccountsAsync(
        IAccountRepository repository,
        CancellationToken cancellationToken)
    {
        var result = new List<AccountRecord>();
        for (var pageNumber = 1; ; pageNumber++)
        {
            var page = await repository.GetPageAsync(new PageQuery(pageNumber, PageSize), cancellationToken).ConfigureAwait(false);
            result.AddRange(page.Items.Where(account => account.IsEnabled && !account.IsDeleted && account.Status == "Active"));
            if ((long)pageNumber * PageSize >= page.TotalCount) return result;
        }
    }
}
