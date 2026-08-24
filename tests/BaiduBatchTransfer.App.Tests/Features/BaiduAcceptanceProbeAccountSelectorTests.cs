using BaiduBatchTransfer.App.Features.Acceptance;
using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class BaiduAcceptanceProbeAccountSelectorTests
{
    [Fact]
    public void SelectsTheOnlyActiveProfileWithoutExposingIdentity()
    {
        var account = Account("C:\\safe\\profile");

        var result = new BaiduAcceptanceProbeAccountSelector().Select([account]);

        Assert.True(result.CanOpen);
        Assert.Equal(account.Id, result.AccountId);
        Assert.Equal("C:\\safe\\profile", result.ProfilePath);
        Assert.DoesNotContain(account.BaiduUserId, result.SafeMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(account.DisplayName, result.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void RequiresExactlyOneEligibleAccount()
    {
        var inactive = Account("C:\\safe\\one") with { IsEnabled = false };
        var missingProfile = Account(null);
        var selector = new BaiduAcceptanceProbeAccountSelector();
        var none = selector.Select([inactive, missingProfile]);
        var many = selector.Select(
        [
            Account("C:\\safe\\one"),
            Account("C:\\safe\\two"),
        ]);

        Assert.False(none.CanOpen);
        Assert.Contains("一个", none.SafeMessage);
        Assert.False(many.CanOpen);
        Assert.Contains("一个", many.SafeMessage);
        Assert.Null(none.ProfilePath);
        Assert.Null(many.ProfilePath);
    }

    private static AccountRecord Account(string? profilePath) => new(
        Guid.NewGuid(),
        "private-baidu-user-id",
        "私人账号名称",
        [1, 2, 3],
        profilePath,
        "WebView2",
        "Active",
        null,
        null,
        0,
        0,
        0,
        true,
        DateTimeOffset.UtcNow,
        "2026-08-24",
        false,
        null);
}
