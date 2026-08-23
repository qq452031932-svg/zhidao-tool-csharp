using BaiduBatchTransfer.App.Features.Jobs;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Application.Scheduling;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class ExistingShareDecisionInteractionTests
{
    [Fact]
    public async Task PendingBatchCanBeRecoveredAfterRestartAndOriginalTokenIsSubmitted()
    {
        var batch = Guid.NewGuid();
        var account = Guid.NewGuid();
        var application = new FakeApplication(batch, account);
        var presenter = new FakePresenter();
        var interaction = new ExistingShareDecisionInteraction(application, presenter);

        var pending = await interaction.GetPendingBatchIdsAsync();
        var outcome = await interaction.ResolveAsync(Assert.Single(pending));

        Assert.True(outcome.Completed);
        Assert.Equal("token-1", Assert.Single(application.Commands).ConfirmationToken);
        Assert.Equal(ExistingShareDecision.Reuse, application.Commands[0].BatchDefault);
        Assert.Equal("账号甲", Assert.Single(presenter.Summaries).Accounts[0].AccountDisplayName);
    }

    [Fact]
    public async Task StaleConfirmationIsRediscoveredAndNeverReusesOldToken()
    {
        var batch = Guid.NewGuid();
        var application = new FakeApplication(batch, Guid.NewGuid()) { FirstConfirmationIsStale = true };
        var interaction = new ExistingShareDecisionInteraction(application, new FakePresenter());

        var outcome = await interaction.ResolveAsync(batch);

        Assert.True(outcome.Completed);
        Assert.Equal(["token-1", "token-2"], application.Commands.Select(command => command.ConfirmationToken));
    }

    private sealed class FakeApplication(Guid batch, Guid account) : IExistingShareDecisionApplication
    {
        private int discovery;
        public bool FirstConfirmationIsStale { get; init; }
        public List<ConfirmExistingShareDecisionCommand> Commands { get; } = [];

        public Task<IReadOnlyList<Guid>> GetPendingBatchIdsAsync(int maximumCount, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Guid>>([batch]);

        public Task<ExistingShareBatchSummary> DiscoverAsync(Guid batchJobId, CancellationToken cancellationToken = default)
        {
            discovery++;
            return Task.FromResult(new ExistingShareBatchSummary(
                batchJobId,
                $"token-{discovery}",
                [new ExistingShareAccountSummary(account, 2, 1, 1, "账号甲")]));
        }

        public Task<ExistingShareDecisionConfirmation> ConfirmAsync(
            ConfirmExistingShareDecisionCommand command,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            return Task.FromResult(FirstConfirmationIsStale && Commands.Count == 1
                ? ExistingShareDecisionConfirmation.Stale
                : ExistingShareDecisionConfirmation.Success);
        }
    }

    private sealed class FakePresenter : IExistingShareDecisionPresenter
    {
        public List<ExistingShareBatchSummary> Summaries { get; } = [];

        public Task<ConfirmExistingShareDecisionCommand?> ConfirmAsync(
            ExistingShareBatchSummary summary,
            CancellationToken cancellationToken = default)
        {
            Summaries.Add(summary);
            return Task.FromResult<ConfirmExistingShareDecisionCommand?>(new(
                summary.BatchJobId,
                summary.ConfirmationToken,
                ExistingShareDecision.Reuse,
                new Dictionary<Guid, ExistingShareDecision>()));
        }
    }
}
