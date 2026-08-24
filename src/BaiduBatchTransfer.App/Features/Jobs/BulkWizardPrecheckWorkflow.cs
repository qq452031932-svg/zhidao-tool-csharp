using BaiduBatchTransfer.Application.Imports;
using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.App.Features.Jobs;

public sealed record WizardPrecheckActionableSnapshot(
    int InvalidLinkCount,
    int MissingExtractionCodeCount,
    int AccountIssueCount,
    int ConflictCount);

public interface IWizardPrecheckSnapshotQuery
{
    Task<WizardPrecheckActionableSnapshot> LoadAsync(
        Guid sessionId,
        CancellationToken cancellationToken);
}

public sealed class BulkWizardPrecheckWorkflow(
    IBulkPrecheckRunner runner,
    IWizardPrecheckSnapshotQuery snapshotQuery) : IWizardPrecheckWorkflow
{
    private readonly IBulkPrecheckRunner runner =
        runner ?? throw new ArgumentNullException(nameof(runner));
    private readonly IWizardPrecheckSnapshotQuery snapshotQuery =
        snapshotQuery ?? throw new ArgumentNullException(nameof(snapshotQuery));

    public async Task<WizardPrecheckResult> RunAsync(
        Guid sessionId,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(
            new BulkPrecheckRequest(sessionId, accountId),
            cancellationToken).ConfigureAwait(false);
        var snapshot = await snapshotQuery.LoadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return new(
            result.Status switch
            {
                BulkPrecheckRunStatus.Completed => WizardPrecheckStatus.Completed,
                BulkPrecheckRunStatus.PausedForLogin => WizardPrecheckStatus.PausedForLogin,
                BulkPrecheckRunStatus.PausedForVerification => WizardPrecheckStatus.PausedForVerification,
                _ => throw new ArgumentOutOfRangeException(nameof(result)),
            },
            result.ProcessedRows,
            snapshot.InvalidLinkCount,
            snapshot.MissingExtractionCodeCount,
            snapshot.AccountIssueCount,
            snapshot.ConflictCount);
    }
}

public sealed class LocalWizardPrecheckSnapshotQuery(IUnitOfWorkFactory unitOfWorkFactory)
    : IWizardPrecheckSnapshotQuery
{
    private const int PageSize = 1_000;
    private readonly IUnitOfWorkFactory unitOfWorkFactory =
        unitOfWorkFactory ?? throw new ArgumentNullException(nameof(unitOfWorkFactory));

    public async Task<WizardPrecheckActionableSnapshot> LoadAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        await using var unit = await unitOfWorkFactory.BeginAsync(cancellationToken).ConfigureAwait(false);
        _ = await unit.Imports.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The import session does not exist.");
        var counts = await unit.Imports.GetDispositionCountsAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var invalidLinks = checked(counts.InvalidRows + counts.NeedsResolutionRows);
        var missingCodes = 0;
        var accountIssues = 0;
        long cursor = 0;
        while (true)
        {
            var page = await unit.Imports.GetRowsKeysetPageAsync(
                new StagedImportKeysetQuery(sessionId, "Ready", "Failed", PageSize, cursor, false),
                cancellationToken).ConfigureAwait(false);
            foreach (var row in page.Items)
            {
                switch (row.PrecheckErrorCode)
                {
                    case "INVALID_LINK":
                        invalidLinks++;
                        break;
                    case "INVALID_EXTRACTION_CODE":
                        missingCodes++;
                        break;
                    case "ACCOUNT_CAPACITY":
                    case "INVALID_TARGET_PATH":
                        accountIssues++;
                        break;
                }
            }
            if (page.NextCursor is null) break;
            cursor = page.NextCursor.Value;
        }
        await unit.RollbackAsync(cancellationToken).ConfigureAwait(false);
        return new(invalidLinks, missingCodes, accountIssues, counts.ConflictRows);
    }
}
