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
        ArgumentException.ThrowIfNullOrWhiteSpace(candidatePath);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(confirmedAccounts);

        var accounts = confirmedAccounts.ToDictionary(account => account.AccountId);
        if (explicitRowAccountId is { } explicitId && IsEligible(explicitId, accounts))
        {
            return new AccountRuleResolution(
                explicitId,
                AccountRuleResolutionReason.ExplicitRowAccount,
                null,
                null);
        }

        var normalizedCandidatePath = _pathNormalizer.Normalize(candidatePath).ComparisonValue;
        var match = rules
            .Where(rule => rule.IsEnabled && IsEligible(rule.AccountId, accounts))
            .Select(rule => new NormalizedRule(rule, _pathNormalizer.Normalize(rule.DirectoryPrefix).ComparisonValue))
            .Where(rule => IsDirectoryPrefix(rule.NormalizedPrefix, normalizedCandidatePath))
            .OrderByDescending(rule => rule.NormalizedPrefix.Length)
            .ThenBy(rule => rule.Rule.Priority)
            .ThenBy(rule => accounts[rule.Rule.AccountId].ConfirmedOrder)
            .ThenBy(rule => rule.Rule.Id)
            .FirstOrDefault();

        return match is null
            ? new AccountRuleResolution(null, AccountRuleResolutionReason.Public, null, null)
            : new AccountRuleResolution(
                match.Rule.AccountId,
                AccountRuleResolutionReason.DirectoryBinding,
                match.Rule.Id,
                match.NormalizedPrefix);
    }

    private static bool IsEligible(
        Guid accountId,
        IReadOnlyDictionary<Guid, AccountRuleAccount> accounts) =>
        accounts.TryGetValue(accountId, out var account) &&
        account.IsEnabled &&
        !account.IsDeleted &&
        account.IsHealthy;

    private static bool IsDirectoryPrefix(string prefix, string path) =>
        prefix == "/" ||
        path.Equals(prefix, StringComparison.Ordinal) ||
        path.StartsWith($"{prefix}/", StringComparison.Ordinal);

    private sealed record NormalizedRule(AccountRule Rule, string NormalizedPrefix);
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
