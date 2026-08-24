using BaiduBatchTransfer.App.Features.Jobs;
using BaiduBatchTransfer.Application.Imports;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class BulkWizardPrecheckWorkflowTests
{
    [Fact]
    public async Task CompletedRunIncludesActionableCountsFromUpdatedSession()
    {
        var session = Guid.NewGuid();
        var account = Guid.NewGuid();
        var runner = new FakeRunner(new(BulkPrecheckRunStatus.Completed, 7));
        var query = new FakeSnapshotQuery(new(1, 2, 3, 4));
        var workflow = new BulkWizardPrecheckWorkflow(runner, query);

        var result = await workflow.RunAsync(session, account, CancellationToken.None);

        Assert.Equal(WizardPrecheckStatus.Completed, result.Status);
        Assert.Equal(7, result.ProcessedRows);
        Assert.Equal((1, 2, 3, 4), (
            result.InvalidLinkCount,
            result.MissingExtractionCodeCount,
            result.AccountIssueCount,
            result.ConflictCount));
        Assert.Equal((session, account), runner.LastRequest);
        Assert.Equal(session, query.LastSessionId);
    }

    [Theory]
    [InlineData(BulkPrecheckRunStatus.PausedForLogin, WizardPrecheckStatus.PausedForLogin)]
    [InlineData(BulkPrecheckRunStatus.PausedForVerification, WizardPrecheckStatus.PausedForVerification)]
    public async Task PauseStatusIsMappedWithoutDiscardingUpdatedCounts(
        BulkPrecheckRunStatus bulkStatus,
        WizardPrecheckStatus expected)
    {
        var workflow = new BulkWizardPrecheckWorkflow(
            new FakeRunner(new(bulkStatus, 2)),
            new FakeSnapshotQuery(new(0, 1, 0, 0)));

        var result = await workflow.RunAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(expected, result.Status);
        Assert.Equal(1, result.MissingExtractionCodeCount);
    }

    private sealed class FakeRunner(BulkPrecheckResult result) : IBulkPrecheckRunner
    {
        public (Guid SessionId, Guid AccountId) LastRequest { get; private set; }

        public Task<BulkPrecheckResult> RunAsync(
            BulkPrecheckRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = (request.ImportSessionId, request.AccountId);
            return Task.FromResult(result);
        }
    }

    private sealed class FakeSnapshotQuery(WizardPrecheckActionableSnapshot result)
        : IWizardPrecheckSnapshotQuery
    {
        public Guid LastSessionId { get; private set; }

        public Task<WizardPrecheckActionableSnapshot> LoadAsync(
            Guid sessionId,
            CancellationToken cancellationToken)
        {
            LastSessionId = sessionId;
            return Task.FromResult(result);
        }
    }
}
