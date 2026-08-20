using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Core.Services;

public sealed class AccountRuleResolver
{
    private readonly BaiduPathNormalizer _pathNormalizer = new();

    public AccountRuleResolution Resolve(
        Guid? explicitRowAccountId,
        string candidatePath,
        IEnumerable<AccountRule> rules,
        IEnumerable<AccountRuleAccount> confirmedAccounts)
    {
        return Prepare(rules, confirmedAccounts).Resolve(explicitRowAccountId, candidatePath);
    }

    public PreparedAccountRuleResolver Prepare(
        IEnumerable<AccountRule> rules,
        IEnumerable<AccountRuleAccount> confirmedAccounts)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(confirmedAccounts);
        var accounts = confirmedAccounts.ToDictionary(account => account.AccountId);
        var eligibleAccountIds = accounts.Values
            .Where(account => account.IsEnabled && !account.IsDeleted && account.IsHealthy)
            .Select(account => account.AccountId)
            .ToHashSet();
        var ruleSnapshot = rules.ToArray();
        return new PreparedAccountRuleResolver(eligibleAccountIds, () => ruleSnapshot
            .Where(rule => rule.IsEnabled && IsEligible(rule.AccountId, accounts))
            .Select(rule => new NormalizedRule(rule, _pathNormalizer.Normalize(rule.DirectoryPrefix).ComparisonValue))
            .GroupBy(rule => rule.NormalizedPrefix, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(rule => rule.Rule.Priority)
                    .ThenBy(rule => accounts[rule.Rule.AccountId].ConfirmedOrder)
                    .ThenBy(rule => rule.Rule.Id)
                    .First().Rule,
                StringComparer.Ordinal));
    }

    private static bool IsEligible(
        Guid accountId,
        IReadOnlyDictionary<Guid, AccountRuleAccount> accounts) =>
        accounts.TryGetValue(accountId, out var account) &&
        account.IsEnabled &&
        !account.IsDeleted &&
        account.IsHealthy;

    private sealed record NormalizedRule(AccountRule Rule, string NormalizedPrefix);
}

public sealed class PreparedAccountRuleResolver
{
    private readonly HashSet<Guid> eligibleAccountIds;
    private readonly Lazy<IReadOnlyDictionary<string, AccountRule>> rulesByPrefix;
    private readonly BaiduPathNormalizer pathNormalizer = new();

    internal PreparedAccountRuleResolver(
        HashSet<Guid> eligibleAccountIds,
        Func<IReadOnlyDictionary<string, AccountRule>> rulesFactory)
    {
        this.eligibleAccountIds = eligibleAccountIds;
        rulesByPrefix = new Lazy<IReadOnlyDictionary<string, AccountRule>>(
            rulesFactory ?? throw new ArgumentNullException(nameof(rulesFactory)),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public AccountRuleResolution Resolve(Guid? explicitRowAccountId, string candidatePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidatePath);
        if (explicitRowAccountId is { } explicitId && eligibleAccountIds.Contains(explicitId))
            return new AccountRuleResolution(
                explicitId,
                AccountRuleResolutionReason.ExplicitRowAccount,
                null,
                null);

        var prefix = pathNormalizer.Normalize(candidatePath).ComparisonValue;
        while (true)
        {
            if (rulesByPrefix.Value.TryGetValue(prefix, out var rule))
                return new AccountRuleResolution(
                    rule.AccountId,
                    AccountRuleResolutionReason.DirectoryBinding,
                    rule.Id,
                    prefix);
            if (prefix == "/") break;
            var separator = prefix.LastIndexOf('/');
            prefix = separator <= 0 ? "/" : prefix[..separator];
        }

        return new AccountRuleResolution(null, AccountRuleResolutionReason.Public, null, null);
    }
}

public sealed record AccountRuleAccount
{
    public AccountRuleAccount(
        Guid accountId,
        int confirmedOrder,
        bool isEnabled,
        bool isDeleted,
        bool isHealthy)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(confirmedOrder);
        AccountId = accountId;
        ConfirmedOrder = confirmedOrder;
        IsEnabled = isEnabled;
        IsDeleted = isDeleted;
        IsHealthy = isHealthy;
    }

    public Guid AccountId { get; }

    public int ConfirmedOrder { get; }

    public bool IsEnabled { get; }

    public bool IsDeleted { get; }

    public bool IsHealthy { get; }
}

public enum AccountRuleResolutionReason
{
    Public,
    ExplicitRowAccount,
    DirectoryBinding,
}

public sealed record AccountRuleResolution(
    Guid? CandidateConstraintAccountId,
    AccountRuleResolutionReason Reason,
    Guid? MatchedRuleId,
    string? MatchedNormalizedPrefix);
