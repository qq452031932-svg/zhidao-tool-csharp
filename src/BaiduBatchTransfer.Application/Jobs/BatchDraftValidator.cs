using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Core.Services;

namespace BaiduBatchTransfer.Application.Jobs;

public sealed class BatchDraftValidator
{
    private const int ReadPageSize = 1_000;
    private readonly BaiduPathNormalizer pathNormalizer = new();

    internal async Task<ValidatedBatchDraft> ValidateAsync(
        BatchDraft draft,
        IUnitOfWork unit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(unit);
        var session = await unit.Imports.GetSessionAsync(draft.ImportSessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new BatchDraftValidationException("The import draft does not exist.");
        var confirmableStatus = session.Status == "ReadyToConfirm" ||
            (session.Status == "Draft" && draft.ValidationMode != "BatchPrecheck");
        if (!confirmableStatus || session.CreatedBatchJobId is not null || session.AppendToBatchJobId is not null)
            throw new BatchDraftValidationException("The import draft is not ready for final confirmation.");
        await ValidateSessionCountersAsync(session, unit.Imports, cancellationToken).ConfigureAwait(false);

        var accountDrafts = SelectAccounts(draft);
        var accounts = await ValidateAccountsAsync(accountDrafts, unit.Accounts, cancellationToken).ConfigureAwait(false);
        var accountIds = accounts.Select(account => account.Draft.AccountId).ToHashSet();
        var rows = await ReadEligibleRowsAsync(session, draft, accountIds, cancellationToken, unit.Imports).ConfigureAwait(false);

        if (draft.TransferMode == TransferMode.Distribution && draft.QuotaBasis == QuotaBasis.TaskCount)
        {
            var targetTotal = accounts.Sum(account => (long)account.Draft.TargetItemCount);
            if (targetTotal != rows.Length)
                throw new BatchDraftValidationException("The task quota total must equal the included resource count.");
            foreach (var account in accounts)
            {
                var locked = rows.Count(row => row.ConstraintAccountId == account.Draft.AccountId);
                if (locked > account.Draft.TargetItemCount)
                    throw new BatchDraftValidationException("A locked assignment count exceeds its confirmed account quota.");
            }
        }

        if (draft.TransferMode == TransferMode.Distribution && draft.QuotaBasis == QuotaBasis.SuccessTarget)
        {
            var targetTotal = accounts.Sum(account => (long)account.Draft.TargetSuccessCount);
            if (targetTotal > rows.Length && draft.ShortagePolicy != ShortagePolicy.WaitForCandidates)
                throw new BatchDraftValidationException("The success target total exceeds the available candidates.");
        }

        IReadOnlyDictionary<Guid, int>? effectiveTargets = null;
        IReadOnlyDictionary<Guid, int>? reachableTargets = null;
        if (draft.TransferMode == TransferMode.Distribution && draft.QuotaBasis == QuotaBasis.SuccessTarget)
        {
            var targetAccounts = accounts.Select(account => new SuccessTargetAccount(
                account.Draft.AccountId,
                account.Draft.TargetSuccessCount,
                account.Draft.TargetSuccessCount,
                0,
                0,
                account.Draft.PriorityOrder)).ToArray();
            var candidates = rows.Select((row, index) => new AllocationCandidate(
                row.Row.Id,
                index + 1,
                row.ConstraintAccountId,
                CandidateStatus.Unallocated)).ToArray();
            var simulator = new ShortageSimulator();
            effectiveTargets = simulator.Simulate(draft.ShortagePolicy, targetAccounts, candidates).EffectiveTargets;
            var reachablePolicy = draft.ShortagePolicy == ShortagePolicy.WaitForCandidates
                ? ShortagePolicy.ProportionalReduction
                : draft.ShortagePolicy;
            reachableTargets = simulator.Simulate(reachablePolicy, targetAccounts, candidates).EffectiveTargets;
        }

        var confirmations = accounts.Select(account =>
        {
            var constraintCount = rows.Count(row => row.ConstraintAccountId == account.Draft.AccountId);
            return new BatchAccountConfirmation(
                account.Draft.AccountId,
                account.Draft.PriorityOrder,
                account.Draft.DefaultTargetPath,
                account.NormalizedDefaultTargetPath,
                draft.TransferMode == TransferMode.Replication ? rows.Length : account.Draft.TargetItemCount,
                draft.TransferMode == TransferMode.Replication ? rows.Length : account.Draft.TargetSuccessCount,
                draft.QuotaBasis == QuotaBasis.TaskCount ? constraintCount : 0,
                draft.QuotaBasis == QuotaBasis.SuccessTarget ? constraintCount : 0,
                effectiveTargetSuccessCount: effectiveTargets?[account.Draft.AccountId] ?? account.Draft.TargetSuccessCount,
                reachableTargetSuccessCount: reachableTargets?[account.Draft.AccountId] ?? account.Draft.TargetSuccessCount);
        }).ToImmutableArray();

        var operationCount = draft.TransferMode == TransferMode.Replication
            ? ReplicationExpander.EstimateIntentCount(rows.Length, accounts.Length)
            : rows.Length;
        var immediateOperationCount = draft.TransferMode == TransferMode.Replication || draft.QuotaBasis == QuotaBasis.TaskCount
            ? operationCount
            : 0;
        return new ValidatedBatchDraft(
            session,
            rows,
            confirmations,
            operationCount,
            immediateOperationCount,
            BatchConfirmationTokens.ForCreate(draft, session, rows, operationCount, confirmations));
    }

    internal static async Task ValidateSessionCountersAsync(
        ImportSessionRecord session,
        IImportRepository repository,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        if (await HasDispositionAsync(repository, session.Id, "Conflict", cancellationToken).ConfigureAwait(false))
            throw new BatchDraftValidationException("The import contains conflicting rows that require user action.");
        if (await HasDispositionAsync(repository, session.Id, "NeedsResolution", cancellationToken).ConfigureAwait(false))
            throw new BatchDraftValidationException("The import contains unresolved rows that require user action.");

        var classified = checked((long)session.ReadyRows + session.InvalidRows + session.DuplicateRows + session.ExcludedRows);
        if (classified != session.TotalRows)
            throw new BatchDraftValidationException("The import counters no longer match the staged rows.");
    }

    private static async Task<bool> HasDispositionAsync(
        IImportRepository repository,
        Guid sessionId,
        string disposition,
        CancellationToken cancellationToken)
    {
        var rows = await repository.GetRowsAfterSourceRowAsync(
            sessionId, disposition, 0, 1, cancellationToken).ConfigureAwait(false);
        return rows.Count != 0;
    }

    private static ImmutableArray<BatchAccountDraft> SelectAccounts(BatchDraft draft)
    {
        var selected = draft.TransferMode == TransferMode.Replication
            ? draft.Accounts
            : draft.Accounts.Where(account => draft.QuotaBasis == QuotaBasis.TaskCount
                ? account.TargetItemCount > 0
                : account.TargetSuccessCount > 0).ToImmutableArray();
        if (selected.IsDefaultOrEmpty)
            throw new BatchDraftValidationException("At least one participating account is required.");
        if (selected.Select(account => account.AccountId).Distinct().Count() != selected.Length ||
            selected.Select(account => account.PriorityOrder).Distinct().Count() != selected.Length)
            throw new BatchDraftValidationException("Participating accounts and priorities must be unique.");
        return selected.OrderBy(account => account.PriorityOrder).ToImmutableArray();
    }

    private async Task<ImmutableArray<ValidatedBatchAccount>> ValidateAccountsAsync(
        ImmutableArray<BatchAccountDraft> drafts,
        IAccountRepository repository,
        CancellationToken cancellationToken)
    {
        var result = ImmutableArray.CreateBuilder<ValidatedBatchAccount>(drafts.Length);
        foreach (var draft in drafts)
        {
            var account = await repository.GetByIdAsync(draft.AccountId, cancellationToken).ConfigureAwait(false);
            if (account is null || account.IsDeleted || !account.IsEnabled || account.Status != "Active")
                throw new BatchDraftValidationException("A participating account is unavailable.");
            var normalized = NormalizePath(draft.DefaultTargetPath);
            result.Add(new ValidatedBatchAccount(draft, normalized));
        }
        return result.MoveToImmutable();
    }

    private async Task<ImmutableArray<ValidatedStagedRow>> ReadEligibleRowsAsync(
        ImportSessionRecord session,
        BatchDraft draft,
        HashSet<Guid> accountIds,
        CancellationToken cancellationToken,
        IImportRepository repository)
    {
        var builder = ImmutableArray.CreateBuilder<ValidatedStagedRow>();
        var normalizedIds = new HashSet<string>(StringComparer.Ordinal);
        var after = 0;
        var readyRowsSeen = 0;
        while (true)
        {
            var page = await repository.GetRowsAfterSourceRowAsync(
                session.Id, "Ready", after, ReadPageSize, cancellationToken).ConfigureAwait(false);
            if (page.Count == 0) break;
            after = page[^1].SourceRowNumber;
            readyRowsSeen = checked(readyRowsSeen + page.Count);
            foreach (var row in page)
            {
                if (row.PrecheckStatus == "Failed") continue;
                if (draft.ValidationMode == "BatchPrecheck" && row.PrecheckStatus != "Succeeded")
                    throw new BatchDraftValidationException("A row has not completed the required batch precheck.");
                if (string.IsNullOrWhiteSpace(row.NormalizedShareId))
                    throw new BatchDraftValidationException("A ready row is missing its normalized identity.");
                if (!normalizedIds.Add(row.NormalizedShareId))
                    throw new BatchDraftValidationException("The import contains a duplicate normalized resource.");
                if (row.MappedAccountId is { } mapped && !accountIds.Contains(mapped))
                    throw new BatchDraftValidationException("A row is mapped to an account outside the confirmed batch.");
                if (draft.TransferMode == TransferMode.Replication && row.MappedAccountId is not null && !draft.IgnoreMappedAccountsForReplication)
                    throw new BatchDraftValidationException("Replication requires explicit confirmation to ignore row-level account mappings.");
                var normalizedTarget = row.TargetPath is null ? null : NormalizePath(row.TargetPath);
                builder.Add(new ValidatedStagedRow(
                    row,
                    normalizedTarget,
                    draft.TransferMode == TransferMode.Distribution ? row.MappedAccountId : null));
            }
        }
        if (readyRowsSeen != session.ReadyRows)
            throw new BatchDraftValidationException("The import counters no longer match the staged rows.");
        if (builder.Count == 0)
            throw new BatchDraftValidationException("The import has no included ready resources.");
        return builder.ToImmutable();
    }

    private string NormalizePath(string value)
    {
        try { return pathNormalizer.Normalize(value).ComparisonValue; }
        catch (ArgumentException) { throw new BatchDraftValidationException("A confirmed target path is invalid."); }
    }
}

internal sealed record ValidatedBatchAccount(BatchAccountDraft Draft, string NormalizedDefaultTargetPath);

internal sealed record ValidatedStagedRow(
    StagedImportRowRecord Row,
    string? NormalizedTargetPath,
    Guid? ConstraintAccountId);

internal sealed record ValidatedBatchDraft(
    ImportSessionRecord Session,
    ImmutableArray<ValidatedStagedRow> Rows,
    ImmutableArray<BatchAccountConfirmation> Accounts,
    long OperationCount,
    long ImmediateOperationCount,
    string ConfirmationToken);

internal static class BatchConfirmationTokens
{
    public static string ForCreate(
        BatchDraft draft,
        ImportSessionRecord session,
        IReadOnlyList<ValidatedStagedRow> rows,
        long operationCount,
        IEnumerable<BatchAccountConfirmation> accounts) =>
        Hash(string.Join('|', new[]
        {
            "create-v1", draft.BatchJobId.ToString("N"), session.Id.ToString("N"), session.UpdatedAt.ToString("O"),
            draft.TransferMode.ToString(), draft.QuotaBasis.ToString(), draft.ShortagePolicy.ToString(),
            Hash(draft.Name), Hash(draft.DefaultTargetPath), draft.FolderCreationMode,
            draft.DuplicatePolicy.ToString(), draft.ValidationMode,
            draft.QueueOrder.ToString(System.Globalization.CultureInfo.InvariantCulture),
            draft.IgnoreMappedAccountsForReplication.ToString(System.Globalization.CultureInfo.InvariantCulture),
            rows.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            operationCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            HashRows(rows),
            string.Join(';', accounts.Select(account => $"{account.AccountId:N}:{account.PriorityOrder}:{account.TargetItemCount}:{account.TargetSuccessCount}:{Hash(account.NormalizedDefaultTargetPath)}")),
        }));

    public static string ForAppend(
        AppendCandidatesDraft draft,
        ImportSessionRecord session,
        BatchJobRecord batch,
        IReadOnlyList<ValidatedStagedRow> rows,
        IReadOnlyList<BatchAccountConfirmation> accounts,
        long operationCount) =>
        Hash(string.Join('|', new[]
        {
            "append-v1", draft.BatchJobId.ToString("N"), draft.ImportSessionId.ToString("N"),
            session.UpdatedAt.ToString("O"), Hash(batch.Name), batch.SourceType, batch.TransferMode,
            batch.QuotaBasis, batch.ShortagePolicy, batch.ResourceCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            batch.OperationCount.ToString(System.Globalization.CultureInfo.InvariantCulture), Hash(batch.DefaultTargetPath),
            batch.FolderCreationMode, batch.DuplicatePolicy, batch.ValidationMode, batch.Status,
            batch.QueueOrder.ToString(System.Globalization.CultureInfo.InvariantCulture),
            rows.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            operationCount.ToString(System.Globalization.CultureInfo.InvariantCulture), HashRows(rows),
            string.Join(';', accounts.Select(account =>
                $"{account.AccountId:N}:{account.PriorityOrder}:{account.TargetItemCount}:{account.TargetSuccessCount}:{account.EffectiveTargetSuccessCount}:{account.FulfilledCount}:{account.ReservedSlotCount}:{account.LockedItemCount}:{account.ExclusiveCandidateCount}:{account.UnusedCandidateCount}:{account.NewExclusiveCandidateCount}:{account.ReachableTargetSuccessCount}:{Hash(account.NormalizedDefaultTargetPath)}")),
        }));

    private static string HashRows(IEnumerable<ValidatedStagedRow> rows)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var item in rows)
        {
            Append(hash, item.Row.Id.ToString("N"));
            Append(hash, item.Row.SourceRowNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Append(hash, item.Row.ShareUrl);
            Append(hash, item.Row.NormalizedShareId);
            Append(hash, item.Row.ExtractionCode);
            Append(hash, item.Row.TargetPath);
            Append(hash, item.Row.CreateFolderName);
            Append(hash, item.Row.MappedAccountId?.ToString("N"));
            Append(hash, item.Row.Note);
            Append(hash, item.Row.PrecheckStatus);
            Append(hash, item.Row.PrecheckErrorCode);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, string? value)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(value ?? string.Empty));
        hash.AppendData([0]);
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
