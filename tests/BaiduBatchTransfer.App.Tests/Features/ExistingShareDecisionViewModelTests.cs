using BaiduBatchTransfer.App.Features.Jobs;
using BaiduBatchTransfer.Application.Scheduling;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class ExistingShareDecisionViewModelTests
{
    private static readonly Guid BatchId = Guid.Parse("20000000-0000-0000-0000-000000000011");
    private static readonly Guid FirstAccount = Guid.Parse("10000000-0000-0000-0000-000000000011");
    private static readonly Guid SecondAccount = Guid.Parse("10000000-0000-0000-0000-000000000012");

    [Fact]
    public void DefaultsToReuseAndExposesOnlyChineseDecisionTextWithAggregatedCounts()
    {
        var viewModel = CreateViewModel();

        Assert.Equal(ExistingShareDecision.Reuse, viewModel.BatchDefault);
        Assert.Equal("复用已有分享", viewModel.SelectedBatchDefaultOption.DisplayName);
        Assert.Equal(["复用已有分享", "重新生成", "取消分享"],
            viewModel.BatchDecisionOptions.Select(value => value.DisplayName));
        Assert.Equal(["跟随批次默认", "复用已有分享", "重新生成", "取消分享"],
            viewModel.AccountOverrideOptions.Select(value => value.DisplayName));
        Assert.Equal((5, 3, 2),
            (viewModel.TotalDiscoveredCount, viewModel.TotalCompatibleCount, viewModel.TotalIncompatibleCount));
        Assert.Equal(["账号 1", "账号 2"], viewModel.Accounts.Select(value => value.AccountLabel));
        Assert.DoesNotContain("Reuse", viewModel.DecisionSummaryText, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfirmationPreservesOriginalTokenAndAppliesOnlyExplicitAccountOverrides()
    {
        var viewModel = new ExistingShareDecisionViewModel(new(
            BatchId,
            "original-confirmation-token",
            [
                new(FirstAccount, DiscoveredCount: 3, CompatibleCount: 3, IncompatibleCount: 0),
                new(SecondAccount, DiscoveredCount: 2, CompatibleCount: 1, IncompatibleCount: 1),
            ]));
        viewModel.Accounts[0].SelectedOverrideOption = viewModel.AccountOverrideOptions.Single(
            value => value.Decision == ExistingShareDecision.Regenerate);

        Assert.True(viewModel.TryConfirm());

        var command = Assert.IsType<ConfirmExistingShareDecisionCommand>(viewModel.ConfirmedCommand);
        Assert.Equal(BatchId, command.BatchJobId);
        Assert.Equal("original-confirmation-token", command.ConfirmationToken);
        Assert.Equal(ExistingShareDecision.Reuse, command.BatchDefault);
        Assert.Equal(new Dictionary<Guid, ExistingShareDecision>
        {
            [FirstAccount] = ExistingShareDecision.Regenerate,
        }, command.PerAccountOverrides);
    }

    [Fact]
    public void ExplicitCancelCreatesCancelCommandInsteadOfWindowDismissal()
    {
        var viewModel = CreateViewModel();
        viewModel.SelectedBatchDefaultOption = viewModel.BatchDecisionOptions.Single(
            value => value.Decision == ExistingShareDecision.Cancel);

        Assert.True(viewModel.TryConfirm());

        Assert.Equal(ExistingShareDecision.Cancel, viewModel.ConfirmedCommand!.BatchDefault);
    }

    [Fact]
    public void IncompatibleOnlyAccountCannotReuseUntilItOverridesToRegenerateOrCancel()
    {
        var viewModel = CreateViewModel();

        Assert.False(viewModel.CanConfirm);
        Assert.Contains("账号 2", viewModel.ValidationMessage, StringComparison.Ordinal);
        Assert.False(viewModel.TryConfirm());
        Assert.Null(viewModel.ConfirmedCommand);

        viewModel.Accounts[1].SelectedOverrideOption = viewModel.AccountOverrideOptions.Single(
            value => value.Decision == ExistingShareDecision.Regenerate);

        Assert.True(viewModel.CanConfirm);
        Assert.Equal(string.Empty, viewModel.ValidationMessage);
        Assert.True(viewModel.TryConfirm());
    }

    private static ExistingShareDecisionViewModel CreateViewModel() => new(new(
        BatchId,
        "original-confirmation-token",
        [
            new(FirstAccount, DiscoveredCount: 3, CompatibleCount: 3, IncompatibleCount: 0),
            new(SecondAccount, DiscoveredCount: 2, CompatibleCount: 0, IncompatibleCount: 2),
        ]));
}
