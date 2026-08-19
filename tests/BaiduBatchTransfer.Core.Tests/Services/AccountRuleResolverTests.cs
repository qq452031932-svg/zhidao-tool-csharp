using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Core.Services;

namespace BaiduBatchTransfer.Core.Tests.Services;

public sealed class AccountRuleResolverTests
{
    [Fact]
    public void ExplicitPerRowAccount_WinsOverDirectoryBinding()
    {
        var explicitAccount = Id(1);
        var directoryAccount = Id(2);
        var resolver = new AccountRuleResolver();

        var result = resolver.Resolve(
            explicitAccount,
            "/team/project",
            new[] { Rule(1, "/team", directoryAccount, 0) },
            new[] { Account(explicitAccount, 0), Account(directoryAccount, 1) });

        Assert.Equal(explicitAccount, result.CandidateConstraintAccountId);
        Assert.Equal(AccountRuleResolutionReason.ExplicitRowAccount, result.Reason);
    }

    [Fact]
    public void DirectoryRules_UseLongestNormalizedPrefixThenNumericPriorityThenAccountOrder()
    {
        var a = Id(1);
        var b = Id(2);
        var c = Id(3);
        var rules = new[]
        {
            Rule(1, "/team", a, 0),
            Rule(2, "\\team\\project\\", b, 5),
            Rule(3, "/team/project", c, 2),
            Rule(4, "/team/project", a, 2),
        };

        var result = new AccountRuleResolver().Resolve(
            null,
            "/team/project/files",
            rules,
            new[] { Account(a, 0), Account(c, 1), Account(b, 2) });

        Assert.Equal(a, result.CandidateConstraintAccountId);
        Assert.Equal(AccountRuleResolutionReason.DirectoryBinding, result.Reason);
        Assert.Equal("/team/project", result.MatchedNormalizedPrefix);
    }

    [Fact]
    public void DisabledDeletedUnhealthyAccountsAndDisabledRules_CannotWin()
    {
        var disabled = Id(1);
        var deleted = Id(2);
        var unhealthy = Id(3);
        var eligible = Id(4);
        var rules = new[]
        {
            Rule(1, "/", disabled, 0),
            Rule(2, "/", deleted, 0),
            Rule(3, "/", unhealthy, 0),
            Rule(4, "/", eligible, 0, enabled: false),
            Rule(5, "/", eligible, 1),
        };
        var accounts = new[]
        {
            Account(disabled, 0, enabled: false),
            Account(deleted, 1, deleted: true),
            Account(unhealthy, 2, healthy: false),
            Account(eligible, 3),
        };

        var result = new AccountRuleResolver().Resolve(null, "/anything", rules, accounts);

        Assert.Equal(eligible, result.CandidateConstraintAccountId);
        Assert.Equal(Id(5), result.MatchedRuleId);
    }

    [Fact]
    public void UnmatchedPath_RemainsPublicAndResolvedSnapshotDoesNotChange()
    {
        var account = Id(1);
        var resolver = new AccountRuleResolver();
        var rules = new List<AccountRule> { Rule(1, "/team", account, 0) };

        var snapshot = resolver.Resolve(null, "/other", rules, new[] { Account(account, 0) });
        rules.Add(Rule(2, "/", account, 0));

        Assert.Null(snapshot.CandidateConstraintAccountId);
        Assert.Equal(AccountRuleResolutionReason.Public, snapshot.Reason);
        Assert.Null(snapshot.MatchedRuleId);
    }

    [Fact]
    public void PrefixMatching_RequiresDirectoryBoundary()
    {
        var account = Id(1);

        var result = new AccountRuleResolver().Resolve(
            null,
            "/foobar",
            new[] { Rule(1, "/foo", account, 0) },
            new[] { Account(account, 0) });

        Assert.Null(result.CandidateConstraintAccountId);
    }

    private static AccountRule Rule(int id, string prefix, Guid accountId, int priority, bool enabled = true) =>
        new(Id(id), prefix, accountId, priority, enabled);

    private static AccountRuleAccount Account(
        Guid id,
        int confirmedOrder,
        bool enabled = true,
        bool deleted = false,
        bool healthy = true) => new(id, confirmedOrder, enabled, deleted, healthy);

    private static Guid Id(int value) => Guid.Parse($"00000000-0000-0000-0000-{value:D12}");
}
