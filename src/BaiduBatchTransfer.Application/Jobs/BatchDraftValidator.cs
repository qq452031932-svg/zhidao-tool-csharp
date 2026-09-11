using System.Collections.Immutable;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Core.Services;

namespace BaiduBatchTransfer.Application.Jobs;

public interface IAcceptanceExecutionProfileStore
{
    Task<bool> IsCurrentManagedProfileAsync(
        Guid accountId,
        ExecutionProfileSnapshot profile,
        CancellationToken cancellationToken = default);

    Task<bool> HasExactAcceptanceGraphAsync(
        Guid batchJobId,
        CancellationToken cancellationToken = default);
}

public sealed class BatchDraftValidator
{
    private const int ReadPageSize = 1_000;
    private readonly BaiduPathNormalizer pathNormalizer = new();
    private readonly BatchDraftValidationComplexityObserver? complexityObserver;

    public BatchDraftValidator()
    {
    }

    internal BatchDraftValidator(BatchDraftValidationComplexityObserver complexityObserver)
    {
        this.complexityObserver = complexityObserver ?? throw new ArgumentNullException(nameof(complexityObserver));
    }

    internal async Task<ValidatedBatchDraft> ValidateAsync(
        BatchDraft draft,
        IUnitOfWork unit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(unit);
        ValidateSelectionSnapshot(draft);
        var session = await unit.Imports.GetSessionAsync(draft.ImportSessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new BatchDraftValidationException("The import draft does not exist.");
        var confirmableStatus = session.Status == "ReadyToConfirm" ||
            (session.Status == "Draft" && draft.ValidationMode != "BatchPrecheck");
        if (!confirmableStatus || session.CreatedBatchJobId is not null || session.AppendToBatchJobId is not null)
            throw new BatchDraftValidationException("The import draft is not ready for final confirmation.");
        await ValidateSessionCountersAsync(session, unit.Imports, cancellationToken).ConfigureAwait(false);

        var accountDrafts = SelectAccounts(draft);
        var acceptanceJobs = draft.AcceptanceOnly ? unit.Jobs : null;
        var accounts = await ValidateAccountsAsync(
            accountDrafts, unit.Accounts, acceptanceJobs, draft.AcceptanceOnly, cancellationToken).ConfigureAwait(false);
        var accountIds = accounts.Select(account => account.Draft.AccountId).ToHashSet();
        var rules = await ReadDirectoryRulesAsync(accounts, unit.Accounts, cancellationToken).ConfigureAwait(false);
        var rows = await ReadEligibleRowsAsync(session, draft, accounts, accountIds, rules, cancellationToken, unit.Imports).ConfigureAwait(false);
        var columnDefinitions = await unit.Imports.GetColumnDefinitionsAsync(session.Id, cancellationToken).ConfigureAwait(false);
        var selectedRecords = accounts.ToDictionary(account => account.Draft.AccountId);
        var workloadInputs = draft.Accounts.Select(account => new BatchWorkloadAccountInput(
            account.AccountId,
            account.PriorityOrder,
            selectedRecords.TryGetValue(account.AccountId, out var selectedAccount)
                ? selectedAccount.Record.DisplayName
                : string.Empty,
            account.SelectionDecision,
            account.ShareEnabled,
            draft.TransferMode == TransferMode.Distribution && draft.QuotaBasis == QuotaBasis.TaskCount
                ? account.TargetItemCount
                : account.TargetSuccessCount)).ToArray();
        var mappedCounts = new Dictionary<Guid, long>();
        foreach (var row in rows)
        {
            complexityObserver?.RecordConstraintRowVisit();
            if (row.ConstraintAccountId is not { } accountId) continue;
            mappedCounts[accountId] = checked(mappedCounts.GetValueOrDefault(accountId) + 1L);
        }
        var workload = new BatchWorkloadPlanner().Plan(rows.Length, draft.TransferMode, workloadInputs, mappedCounts);
        ValidateResolvedTitles(draft, rows, accounts, workload);
        var outputLayout = new ResultColumnLayoutBuilder().Build(
            columnDefinitions.OrderBy(column => column.ColumnOrdinal)
                .Select(column => column.SourceHeader).ToArray(),
            workloadInputs);

        if (draft.TransferMode == TransferMode.Distribution && draft.QuotaBasis == QuotaBasis.TaskCount)
        {
            var targetTotal = accounts.Sum(account => (long)account.Draft.TargetItemCount);
            if (targetTotal != rows.Length)
                throw new BatchDraftValidationException("The task quota total must equal the included resource count.");
            foreach (var account in accounts)
            {
                var locked = ConstraintCount(mappedCounts, account.Draft.AccountId);
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

        var confirmationWorkloads = workload.Accounts.ToDictionary(account => account.AccountId);
        var confirmations = accounts.Select(account =>
        {
            var constraintCount = ConstraintCount(mappedCounts, account.Draft.AccountId);
            var replicationTarget = draft.TransferMode == TransferMode.Replication ? rows.Length : (int?)null;
            var planned = confirmationWorkloads[account.Draft.AccountId];
            return new BatchAccountConfirmation(
                account.Draft.AccountId,
                account.Draft.PriorityOrder,
                account.Draft.DefaultTargetPath,
                account.NormalizedDefaultTargetPath,
                replicationTarget ?? account.Draft.TargetItemCount,
                replicationTarget ?? account.Draft.TargetSuccessCount,
                draft.QuotaBasis == QuotaBasis.TaskCount ? constraintCount : 0,
                draft.QuotaBasis == QuotaBasis.SuccessTarget ? constraintCount : 0,
                effectiveTargetSuccessCount: replicationTarget ??
                    effectiveTargets?[account.Draft.AccountId] ?? account.Draft.TargetSuccessCount,
                reachableTargetSuccessCount: replicationTarget ??
                    reachableTargets?[account.Draft.AccountId] ?? account.Draft.TargetSuccessCount,
                plannedTransferCount: planned.PlannedTransferCount,
                plannedShareCount: planned.PlannedShareCount,
                shareEnabled: planned.ShareEnabled,
                outputOrdinal: planned.OutputOrdinal,
                outputLabel: planned.OutputLabel,
                executionProfile: account.Draft.ExecutionProfile);
        }).ToImmutableArray();

        var operationCount = workload.TransferCount;
        var immediateOperationCount = draft.TransferMode == TransferMode.Replication || draft.QuotaBasis == QuotaBasis.TaskCount
            ? operationCount
            : 0;
        ValidateAcceptanceShape(
            draft, rows.Length, confirmations, operationCount, immediateOperationCount, workload.ShareCount);
        ValidateDirectBackgroundShape(
            draft, rows.Length, confirmations, operationCount, immediateOperationCount, workload.ShareCount);
        return new ValidatedBatchDraft(
            session,
            rows,
            confirmations,
            operationCount,
            immediateOperationCount,
            workload.ShareCount,
            outputLayout,
            columnDefinitions.OrderBy(column => column.ColumnOrdinal).ToImmutableArray(),
            BatchConfirmationTokens.ForCreate(
                draft, session, rows, operationCount, confirmations, rules, columnDefinitions, outputLayout),
            draft.AcceptanceOnly,
            draft.DirectBackground,
            draft.MaxConcurrency);
    }

    private int ConstraintCount(IReadOnlyDictionary<Guid, long> mappedCounts, Guid accountId)
    {
        complexityObserver?.RecordConstraintAccountLookup();
        return checked((int)mappedCounts.GetValueOrDefault(accountId));
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
        return draft.Accounts.Where(account => account.SelectionDecision == BatchAccountSelectionDecision.Selected)
            .OrderBy(account => account.PriorityOrder)
            .ToImmutableArray();
    }

    internal static void ValidateSelectionSnapshot(BatchDraft draft)
    {
        if (draft.Accounts.IsDefaultOrEmpty ||
            draft.Accounts.Any(account => account.SelectionDecision == BatchAccountSelectionDecision.Unspecified))
            throw new BatchDraftValidationException("每个候选账号都必须明确选择或不选择。");
        if (draft.Accounts.Any(account => !Enum.IsDefined(account.SelectionDecision)))
            throw new BatchDraftValidationException("账号选择决定无效。");
        if (draft.Accounts.Select(account => account.AccountId).Distinct().Count() != draft.Accounts.Length ||
            draft.Accounts.Select(account => account.PriorityOrder).Distinct().Count() != draft.Accounts.Length)
            throw new BatchDraftValidationException("候选账号和顺序必须唯一。");
        if (!draft.Accounts.Any(account => account.SelectionDecision == BatchAccountSelectionDecision.Selected))
            throw new BatchDraftValidationException("请至少选择一个参与账号。");
    }

    private async Task<ImmutableArray<ValidatedBatchAccount>> ValidateAccountsAsync(
        ImmutableArray<BatchAccountDraft> drafts,
        IAccountRepository repository,
        IJobRepository? jobs,
        bool acceptanceOnly,
        CancellationToken cancellationToken)
    {
        var result = ImmutableArray.CreateBuilder<ValidatedBatchAccount>(drafts.Length);
        foreach (var draft in drafts)
        {
            var account = await repository.GetByIdAsync(draft.AccountId, cancellationToken).ConfigureAwait(false);
            if (account is null || account.IsDeleted || !account.IsEnabled || account.Status != "Active")
                throw new BatchDraftValidationException("A participating account is unavailable.");
            if (acceptanceOnly)
            {
                if (draft.ExecutionProfile.Engine != BaiduExecutionEngine.ManagedChromiumV1 ||
                    jobs is not IAcceptanceExecutionProfileStore profiles ||
                    !await profiles.IsCurrentManagedProfileAsync(
                        draft.AccountId, draft.ExecutionProfile, cancellationToken).ConfigureAwait(false))
                    throw new BatchDraftValidationException("AcceptanceOnly requires the current active Managed Chromium session.");
            }
            var normalized = NormalizePath(draft.DefaultTargetPath);
            result.Add(new ValidatedBatchAccount(draft, normalized, account));
        }
        return result.MoveToImmutable();
    }

    private static void ValidateAcceptanceShape(
        BatchDraft draft,
        int resourceCount,
        ImmutableArray<BatchAccountConfirmation> accounts,
        long operationCount,
        long immediateOperationCount,
        long shareOperationCount)
    {
        if (!draft.AcceptanceOnly) return;
        if (draft.Accounts.Length != 1 || accounts.Length != 1 ||
            draft.TransferMode != TransferMode.Distribution || draft.QuotaBasis != QuotaBasis.TaskCount ||
            resourceCount != 1 || operationCount != 1 || immediateOperationCount != 1 ||
            shareOperationCount != 1 || !accounts[0].ShareEnabled)
            throw new BatchDraftValidationException(
                "AcceptanceOnly requires exactly one resource, one Managed account, one transfer, and one linked share.");
        try
        {
            if (string.IsNullOrWhiteSpace(draft.ResultDirectory) ||
                !Path.IsPathFullyQualified(draft.ResultDirectory))
                throw new BatchDraftValidationException("AcceptanceOnly requires a valid local result directory.");
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            throw new BatchDraftValidationException("AcceptanceOnly requires a valid local result directory.");
        }
    }

    private static void ValidateDirectBackgroundShape(
        BatchDraft draft,
        int resourceCount,
        ImmutableArray<BatchAccountConfirmation> accounts,
        long operationCount,
        long immediateOperationCount,
        long shareOperationCount)
    {
        if (!draft.DirectBackground) return;
        var expectedOperationCount = draft.TransferMode switch
        {
            TransferMode.Distribution => resourceCount,
            TransferMode.Replication => checked((long)resourceCount * accounts.Length),
            _ => -1,
        };
        if (accounts.Length < 1 || draft.QuotaBasis != QuotaBasis.TaskCount ||
            resourceCount < 1 || operationCount != expectedOperationCount ||
            immediateOperationCount != expectedOperationCount ||
            shareOperationCount != expectedOperationCount || accounts.Any(account => !account.ShareEnabled) ||
            draft.DuplicatePolicy != DuplicatePolicy.Rename ||
            draft.ExistingSharePolicy != "Regenerate" ||
            draft.ExtractionCodeBehavior != ShareExtractionCodeBehavior.Random)
            throw new BatchDraftValidationException(
                "Direct-background batches require task-count replication or distribution, preserve-both transfer, and one automatic share per planned transfer.");
        try
        {
            if (string.IsNullOrWhiteSpace(draft.ResultDirectory) ||
                !Path.IsPathFullyQualified(draft.ResultDirectory))
                throw new BatchDraftValidationException(
                    "Direct-background batches require a valid local result directory.");
            _ = LocalResultDirectoryReference.Normalize(draft.ResultDirectory, nameof(draft.ResultDirectory));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            throw new BatchDraftValidationException(
                "Direct-background batches require a valid local result directory.");
        }
    }

    private static async Task<ImmutableArray<AccountRule>> ReadDirectoryRulesAsync(
        ImmutableArray<ValidatedBatchAccount> accounts,
        IAccountRepository repository,
        CancellationToken cancellationToken)
    {
        var builder = ImmutableArray.CreateBuilder<AccountRule>();
        foreach (var account in accounts)
        {
            for (var pageNumber = 1; ; pageNumber++)
            {
                var page = await repository.GetRulesPageAsync(
                    account.Draft.AccountId, new PageQuery(pageNumber, ReadPageSize), cancellationToken).ConfigureAwait(false);
                foreach (var rule in page.Items)
                {
                    if (!string.Equals(rule.RuleType, "DirectoryPrefix", StringComparison.Ordinal)) continue;
                    try
                    {
                        builder.Add(new AccountRule(rule.Id, rule.RuleValue, rule.AccountId, rule.Priority, rule.IsEnabled));
                    }
                    catch (ArgumentException)
                    {
                        throw new BatchDraftValidationException("An account directory rule is invalid.");
                    }
                }
                if ((long)pageNumber * ReadPageSize >= page.TotalCount) break;
            }
        }
        return builder.ToImmutable();
    }

    private async Task<ImmutableArray<ValidatedStagedRow>> ReadEligibleRowsAsync(
        ImportSessionRecord session,
        BatchDraft draft,
        ImmutableArray<ValidatedBatchAccount> accounts,
        HashSet<Guid> accountIds,
        ImmutableArray<AccountRule> rules,
        CancellationToken cancellationToken,
        IImportRepository repository)
    {
        var builder = ImmutableArray.CreateBuilder<ValidatedStagedRow>();
        var normalizedIds = new HashSet<string>(StringComparer.Ordinal);
        var resolverAccounts = accounts.Select(account => new AccountRuleAccount(
            account.Draft.AccountId,
            account.Draft.PriorityOrder,
            account.Record.IsEnabled,
            account.Record.IsDeleted,
            account.Record.Status == "Active")).ToArray();
        PreparedAccountRuleResolver? ruleResolver = null;
        if (draft.TransferMode == TransferMode.Distribution)
        {
            complexityObserver?.RecordRuleResolverPreparation(resolverAccounts.Length);
            ruleResolver = new AccountRuleResolver().Prepare(rules, resolverAccounts);
        }
        var after = 0;
        var readyRowsSeen = 0;
        while (true)
        {
            var page = await repository.GetRowsAfterSourceRowAsync(
                session.Id, "Ready", after, ReadPageSize, cancellationToken).ConfigureAwait(false);
            if (page.Count == 0) break;
            after = page[^1].SourceRowNumber;
            readyRowsSeen = checked(readyRowsSeen + page.Count);
            var payloads = (await repository.GetRowPayloadsAsync(
                    page.Select(row => row.Id), cancellationToken).ConfigureAwait(false))
                .ToDictionary(payload => payload.StagedImportRowId);
            foreach (var row in page)
            {
                if (row.PrecheckStatus == "Failed") continue;
                if (draft.ValidationMode == "BatchPrecheck" && row.PrecheckStatus != "Succeeded")
                    throw new BatchDraftValidationException("A row has not completed the required batch precheck.");
                if (string.IsNullOrWhiteSpace(row.NormalizedShareId))
                    throw new BatchDraftValidationException("A ready row is missing its normalized identity.");
                if (!normalizedIds.Add(row.NormalizedShareId))
                    throw new BatchDraftValidationException("The import contains a duplicate normalized resource.");
                if (row.RequestedAccountName is not null && row.MappedAccountId is null)
                    throw new BatchDraftValidationException("A requested account name remains unresolved.");
                if (row.MappedAccountId is { } mapped && !accountIds.Contains(mapped))
                    throw new BatchDraftValidationException("A row is mapped to an account outside the confirmed batch.");
                if (draft.TransferMode == TransferMode.Replication && row.MappedAccountId is not null && !draft.IgnoreMappedAccountsForReplication)
                    throw new BatchDraftValidationException("Replication requires explicit confirmation to ignore row-level account mappings.");
                var normalizedTarget = row.TargetPath is null ? null : NormalizePath(row.TargetPath);
                AccountRuleResolution resolution;
                if (ruleResolver is not null)
                {
                    complexityObserver?.RecordRuleResolution();
                    resolution = ruleResolver.Resolve(row.MappedAccountId, row.TargetPath ?? draft.DefaultTargetPath);
                }
                else
                {
                    resolution = new AccountRuleResolution(null, AccountRuleResolutionReason.Public, null, null);
                }
                builder.Add(new ValidatedStagedRow(
                    row,
                    normalizedTarget,
                    resolution.CandidateConstraintAccountId,
                    resolution.Reason,
                    resolution.MatchedRuleId,
                    payloads.TryGetValue(row.Id, out var payload) ? payload.ResolvedOutputTitle : null));
            }
        }
        if (readyRowsSeen != session.ReadyRows)
            throw new BatchDraftValidationException("The import counters no longer match the staged rows.");
        if (builder.Count == 0)
            throw new BatchDraftValidationException("The import has no included ready resources.");
        return builder.ToImmutable();
    }

    private static void ValidateResolvedTitles(
        BatchDraft draft,
        ImmutableArray<ValidatedStagedRow> rows,
        ImmutableArray<ValidatedBatchAccount> accounts,
        BatchWorkloadPlan workload)
    {
        if (workload.ShareCount == 0 || draft.DirectBackground) return;
        var shareEnabled = workload.Accounts.Where(account => account.ShareEnabled)
            .Select(account => account.AccountId).ToHashSet();
        if (draft.TransferMode == TransferMode.Replication || draft.QuotaBasis == QuotaBasis.SuccessTarget)
        {
            if (rows.Any(row => string.IsNullOrWhiteSpace(row.ResolvedOutputTitle)))
                throw new BatchDraftValidationException("A share-enabled included row is missing its resolved output title.");
            return;
        }

        var candidates = rows.Select((row, index) => new TaskQuotaCandidate(
            row.Row.Id, index + 1, row.ConstraintAccountId)).ToArray();
        var quotas = accounts.Select(account => new TaskQuotaAccount(
            account.Draft.AccountId, account.Draft.TargetItemCount, account.Draft.PriorityOrder)).ToArray();
        var assignments = new TaskQuotaAllocator().Allocate(quotas, candidates)
            .ToDictionary(assignment => assignment.ResourceId, assignment => assignment.AccountId);
        if (rows.Any(row => shareEnabled.Contains(assignments[row.Row.Id]) &&
                            string.IsNullOrWhiteSpace(row.ResolvedOutputTitle)))
            throw new BatchDraftValidationException("A share-enabled included row is missing its resolved output title.");
    }

    private string NormalizePath(string value)
    {
        try { return pathNormalizer.Normalize(value).ComparisonValue; }
        catch (ArgumentException) { throw new BatchDraftValidationException("A confirmed target path is invalid."); }
    }
}

internal sealed class BatchDraftValidationComplexityObserver
{
    public long ConstraintRowVisits { get; private set; }
    public long ConstraintAccountLookups { get; private set; }
    public long RuleResolverPreparationCount { get; private set; }
    public long RuleResolverAccountPreparations { get; private set; }
    public long RuleResolutionCalls { get; private set; }

    internal void RecordConstraintRowVisit() => ConstraintRowVisits++;
    internal void RecordConstraintAccountLookup() => ConstraintAccountLookups++;
    internal void RecordRuleResolverPreparation(int accountCount)
    {
        RuleResolverPreparationCount++;
        RuleResolverAccountPreparations = checked(RuleResolverAccountPreparations + accountCount);
    }
    internal void RecordRuleResolution() => RuleResolutionCalls++;
}

internal sealed record ValidatedBatchAccount(
    BatchAccountDraft Draft,
    string NormalizedDefaultTargetPath,
    AccountRecord Record);

internal sealed record ValidatedStagedRow(
    StagedImportRowRecord Row,
    string? NormalizedTargetPath,
    Guid? ConstraintAccountId,
    AccountRuleResolutionReason ConstraintReason = AccountRuleResolutionReason.Public,
    Guid? MatchedRuleId = null,
    string? ResolvedOutputTitle = null);

internal sealed record ValidatedBatchDraft(
    ImportSessionRecord Session,
    ImmutableArray<ValidatedStagedRow> Rows,
    ImmutableArray<BatchAccountConfirmation> Accounts,
    long OperationCount,
    long ImmediateOperationCount,
    long ShareOperationCount,
    ResultColumnLayout ResultColumnLayout,
    ImmutableArray<ImportColumnDefinitionRecord> ColumnDefinitions,
    string ConfirmationToken,
    bool AcceptanceOnly = false,
    bool DirectBackground = false,
    int MaxConcurrency = 1);

internal static class BatchConfirmationTokens
{
    public static string ForCommand(BatchDraft draft)
    {
        using var writer = new CanonicalHashWriter("batch-create-command/v1");
        writer.WriteGuid("batch-job-id", draft.BatchJobId);
        writer.WriteGuid("import-session-id", draft.ImportSessionId);
        writer.WriteString("batch-name", draft.Name);
        writer.WriteInt32("transfer-mode", (int)draft.TransferMode);
        writer.WriteInt32("quota-basis", (int)draft.QuotaBasis);
        writer.WriteInt32("shortage-policy", (int)draft.ShortagePolicy);
        writer.WriteString("default-target-path", draft.DefaultTargetPath);
        writer.WriteString("folder-creation-mode", draft.FolderCreationMode);
        writer.WriteInt32("duplicate-policy", (int)draft.DuplicatePolicy);
        writer.WriteString("validation-mode", draft.ValidationMode);
        writer.WriteInt64("queue-order", draft.QueueOrder);
        writer.WriteBoolean("ignore-mapped-accounts-for-replication", draft.IgnoreMappedAccountsForReplication);
        writer.WriteBoolean("share-permanent", draft.IsSharePermanent);
        writer.WriteBoolean("share-access-unlimited", draft.IsShareAccessUnlimited);
        writer.WriteInt32("extraction-code-behavior", (int)draft.ExtractionCodeBehavior);
        writer.WriteString("fixed-extraction-code", draft.FixedExtractionCode);
        writer.WriteString("existing-share-policy", draft.ExistingSharePolicy);
        writer.WriteBoolean("acceptance-only", draft.AcceptanceOnly);
        writer.WriteString("result-directory", draft.ResultDirectory);
        writer.WriteBoolean("direct-background", draft.DirectBackground);
        writer.WriteInt32("max-concurrency", draft.MaxConcurrency);
        WriteAccountSelections(writer, draft.Accounts);
        return writer.GetHashHex();
    }

    public static string ForCreate(
        BatchDraft draft,
        ImportSessionRecord session,
        IReadOnlyList<ValidatedStagedRow> rows,
        long operationCount,
        IEnumerable<BatchAccountConfirmation> accounts,
        IEnumerable<AccountRule> rules,
        IEnumerable<ImportColumnDefinitionRecord> columnDefinitions,
        ResultColumnLayout outputLayout)
    {
        using var writer = new CanonicalHashWriter("batch-confirmation/create-vNext");
        writer.WriteGuid("batch-job-id", draft.BatchJobId);
        writer.WriteGuid("session-id", session.Id);
        writer.WriteString("session-source-type", session.SourceType);
        writer.WriteString("session-source-name", session.SourceName);
        writer.WriteString("session-status", session.Status);
        writer.WriteInt32("session-total-rows", session.TotalRows);
        writer.WriteInt32("session-ready-rows", session.ReadyRows);
        writer.WriteInt32("session-invalid-rows", session.InvalidRows);
        writer.WriteInt32("session-duplicate-rows", session.DuplicateRows);
        writer.WriteInt32("session-excluded-rows", session.ExcludedRows);
        writer.WriteNullableGuid("session-precheck-account-id", session.PrecheckAccountId);
        writer.WriteNullableGuid("session-created-batch-job-id", session.CreatedBatchJobId);
        writer.WriteNullableGuid("session-append-to-batch-job-id", session.AppendToBatchJobId);
        writer.WriteDateTimeOffset("session-created-at", session.CreatedAt);
        writer.WriteDateTimeOffset("session-updated-at", session.UpdatedAt);
        writer.WriteString("session-source-content-fingerprint", session.SourceContentFingerprint);
        writer.WriteString("session-header-row-number", session.HeaderRowNumber?.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        writer.WriteInt32("transfer-mode", (int)draft.TransferMode);
        writer.WriteInt32("quota-basis", (int)draft.QuotaBasis);
        writer.WriteInt32("shortage-policy", (int)draft.ShortagePolicy);
        writer.WriteString("batch-name", draft.Name);
        writer.WriteString("default-target-path", draft.DefaultTargetPath);
        writer.WriteString("folder-creation-mode", draft.FolderCreationMode);
        writer.WriteInt32("duplicate-policy", (int)draft.DuplicatePolicy);
        writer.WriteString("validation-mode", draft.ValidationMode);
        writer.WriteInt64("queue-order", draft.QueueOrder);
        writer.WriteBoolean("ignore-mapped-accounts-for-replication", draft.IgnoreMappedAccountsForReplication);
        writer.WriteBoolean("share-permanent", draft.IsSharePermanent);
        writer.WriteBoolean("share-access-unlimited", draft.IsShareAccessUnlimited);
        writer.WriteInt32("extraction-code-behavior", (int)draft.ExtractionCodeBehavior);
        writer.WriteString("fixed-extraction-code", draft.FixedExtractionCode);
        writer.WriteString("existing-share-policy", draft.ExistingSharePolicy);
        writer.WriteString("share-settings-fingerprint", draft.ShareSettingsFingerprint);
        writer.WriteBoolean("acceptance-only", draft.AcceptanceOnly);
        writer.WriteString("result-directory", draft.ResultDirectory);
        writer.WriteBoolean("direct-background", draft.DirectBackground);
        writer.WriteInt32("max-concurrency", draft.MaxConcurrency);
        writer.WriteString("column-mapping-fingerprint", session.ColumnMappingFingerprint);
        WriteAccountSelections(writer, draft.Accounts);
        writer.WriteInt32("row-count", rows.Count);
        writer.WriteInt64("operation-count", operationCount);
        WriteRows(writer, rows);
        WriteCreateConfirmations(writer, accounts);
        WriteRules(writer, rules);
        WriteColumnDefinitions(writer, columnDefinitions);
        WriteOutputLayout(writer, outputLayout);
        return writer.GetHashHex();
    }

    public static string ForAppend(
        AppendCandidatesDraft draft,
        ImportSessionRecord session,
        BatchJobRecord batch,
        IReadOnlyList<ValidatedStagedRow> rows,
        IReadOnlyList<BatchAccountConfirmation> accounts,
        long operationCount)
    {
        using var writer = new CanonicalHashWriter("batch-confirmation/append-v1");
        writer.WriteGuid("batch-job-id", draft.BatchJobId);
        writer.WriteGuid("import-session-id", draft.ImportSessionId);
        writer.WriteDateTimeOffset("session-updated-at", session.UpdatedAt);
        writer.WriteString("batch-name", batch.Name);
        writer.WriteString("source-type", batch.SourceType);
        writer.WriteString("transfer-mode", batch.TransferMode);
        writer.WriteString("quota-basis", batch.QuotaBasis);
        writer.WriteString("shortage-policy", batch.ShortagePolicy);
        writer.WriteInt32("resource-count", batch.ResourceCount);
        writer.WriteInt32("stored-operation-count", batch.OperationCount);
        writer.WriteString("default-target-path", batch.DefaultTargetPath);
        writer.WriteString("folder-creation-mode", batch.FolderCreationMode);
        writer.WriteString("duplicate-policy", batch.DuplicatePolicy);
        writer.WriteString("validation-mode", batch.ValidationMode);
        writer.WriteString("status", batch.Status);
        writer.WriteInt64("queue-order", batch.QueueOrder);
        writer.WriteInt32("row-count", rows.Count);
        writer.WriteInt64("operation-count", operationCount);
        WriteRows(writer, rows);
        WriteAppendConfirmations(writer, accounts);
        return writer.GetHashHex();
    }

    private static void WriteRows(CanonicalHashWriter writer, IReadOnlyList<ValidatedStagedRow> rows)
    {
        writer.BeginCollection("rows", rows.Count);
        foreach (var item in rows)
        {
            writer.BeginObject("row", 15);
            writer.WriteGuid("id", item.Row.Id);
            writer.WriteInt64("source-row-number", item.Row.SourceRowNumber);
            writer.WriteString("share-url", item.Row.ShareUrl);
            writer.WriteString("normalized-share-id", item.Row.NormalizedShareId);
            writer.WriteString("extraction-code", item.Row.ExtractionCode);
            writer.WriteString("target-path", item.Row.TargetPath);
            writer.WriteString("create-folder-name", item.Row.CreateFolderName);
            writer.WriteNullableGuid("mapped-account-id", item.Row.MappedAccountId);
            writer.WriteNullableGuid("constraint-account-id", item.ConstraintAccountId);
            writer.WriteInt32("constraint-reason", (int)item.ConstraintReason);
            writer.WriteNullableGuid("matched-rule-id", item.MatchedRuleId);
            writer.WriteString("note", item.Row.Note);
            writer.WriteString("precheck-status", item.Row.PrecheckStatus);
            writer.WriteString("precheck-error-code", item.Row.PrecheckErrorCode);
            writer.WriteString("resolved-output-title", item.ResolvedOutputTitle);
        }
    }

    private static void WriteRules(CanonicalHashWriter writer, IEnumerable<AccountRule> rules)
    {
        var ordered = rules.OrderBy(rule => rule.AccountId).ThenBy(rule => rule.Id).ToArray();
        writer.BeginCollection("rules", ordered.Length);
        foreach (var rule in ordered)
        {
            writer.BeginObject("rule", 5);
            writer.WriteGuid("id", rule.Id);
            writer.WriteGuid("account-id", rule.AccountId);
            writer.WriteString("directory-prefix", rule.DirectoryPrefix);
            writer.WriteInt32("priority", rule.Priority);
            writer.WriteBoolean("enabled", rule.IsEnabled);
        }
    }

    private static void WriteColumnDefinitions(
        CanonicalHashWriter writer,
        IEnumerable<ImportColumnDefinitionRecord> definitions)
    {
        var ordered = definitions.OrderBy(value => value.ColumnOrdinal).ToArray();
        writer.BeginCollection("column-definitions", ordered.Length);
        foreach (var definition in ordered)
        {
            writer.BeginObject("column-definition", 4);
            writer.WriteInt32("ordinal", definition.ColumnOrdinal);
            writer.WriteString("source-header", definition.SourceHeader);
            writer.WriteString("mapped-role", definition.MappedRole);
            writer.WriteBoolean("required", definition.IsRequired);
        }
    }

    private static void WriteAccountSelections(CanonicalHashWriter writer, IEnumerable<BatchAccountDraft> accounts)
    {
        var ordered = accounts.OrderBy(value => value.PriorityOrder).ToArray();
        writer.BeginCollection("account-selections", ordered.Length);
        foreach (var account in ordered)
        {
            writer.BeginObject("account-selection", 12);
            writer.WriteGuid("account-id", account.AccountId);
            writer.WriteInt32("priority-order", account.PriorityOrder);
            writer.WriteInt32("selection-decision", (int)account.SelectionDecision);
            writer.WriteInt32("target-item-count", account.TargetItemCount);
            writer.WriteInt32("target-success-count", account.TargetSuccessCount);
            writer.WriteBoolean("share-enabled", account.ShareEnabled);
            writer.WriteString("default-target-path", account.DefaultTargetPath);
            WriteExecutionProfile(writer, account.ExecutionProfile);
        }
    }

    private static void WriteCreateConfirmations(
        CanonicalHashWriter writer,
        IEnumerable<BatchAccountConfirmation> accounts)
    {
        var ordered = accounts.ToArray();
        writer.BeginCollection("account-confirmations", ordered.Length);
        foreach (var account in ordered)
        {
            writer.BeginObject("account-confirmation", 15);
            writer.WriteGuid("account-id", account.AccountId);
            writer.WriteInt32("priority-order", account.PriorityOrder);
            writer.WriteInt32("target-item-count", account.TargetItemCount);
            writer.WriteInt32("target-success-count", account.TargetSuccessCount);
            writer.WriteBoolean("share-enabled", account.ShareEnabled);
            writer.WriteInt64("planned-transfer-count", account.PlannedTransferCount);
            writer.WriteInt64("planned-share-count", account.PlannedShareCount);
            writer.WriteInt32("output-ordinal", account.OutputOrdinal);
            writer.WriteString("output-label", account.OutputLabel);
            writer.WriteString("normalized-default-target-path", account.NormalizedDefaultTargetPath);
            WriteExecutionProfile(writer, account.ExecutionProfile);
        }
    }

    private static void WriteExecutionProfile(CanonicalHashWriter writer, ExecutionProfileSnapshot profile)
    {
        writer.WriteInt32("execution-engine", (int)profile.Engine);
        writer.WriteInt32("browser-family", (int)profile.BrowserFamily);
        writer.WriteString("profile-id", profile.ProfileId);
        writer.WriteNullableGuid("installation-id", profile.InstallationId);
        writer.WriteNullableGuid("managed-session-id", profile.SessionId);
    }

    private static void WriteAppendConfirmations(
        CanonicalHashWriter writer,
        IReadOnlyList<BatchAccountConfirmation> accounts)
    {
        writer.BeginCollection("account-confirmations", accounts.Count);
        foreach (var account in accounts)
        {
            writer.BeginObject("account-confirmation", 13);
            writer.WriteGuid("account-id", account.AccountId);
            writer.WriteInt32("priority-order", account.PriorityOrder);
            writer.WriteInt32("target-item-count", account.TargetItemCount);
            writer.WriteInt32("target-success-count", account.TargetSuccessCount);
            writer.WriteInt32("effective-target-success-count", account.EffectiveTargetSuccessCount);
            writer.WriteInt32("fulfilled-count", account.FulfilledCount);
            writer.WriteInt32("reserved-slot-count", account.ReservedSlotCount);
            writer.WriteInt32("locked-item-count", account.LockedItemCount);
            writer.WriteInt32("exclusive-candidate-count", account.ExclusiveCandidateCount);
            writer.WriteInt32("unused-candidate-count", account.UnusedCandidateCount);
            writer.WriteInt32("new-exclusive-candidate-count", account.NewExclusiveCandidateCount);
            writer.WriteInt32("reachable-target-success-count", account.ReachableTargetSuccessCount);
            writer.WriteString("normalized-default-target-path", account.NormalizedDefaultTargetPath);
        }
    }

    private static void WriteOutputLayout(CanonicalHashWriter writer, ResultColumnLayout layout)
    {
        writer.WriteString("output-layout-fingerprint", layout.Fingerprint);
        writer.BeginCollection("output-source-headers", layout.SourceHeaders.Length);
        foreach (var header in layout.SourceHeaders) writer.WriteString("source-header", header);
        writer.WriteString("resolved-title-header", layout.ResolvedTitleHeader);
        writer.WriteString("original-share-url-header", layout.OriginalShareUrlHeader);
        writer.BeginCollection("output-accounts", layout.Accounts.Length);
        foreach (var account in layout.Accounts)
        {
            writer.BeginObject("output-account", 8);
            writer.WriteGuid("account-id", account.AccountId);
            writer.WriteInt32("output-ordinal", account.OutputOrdinal);
            writer.WriteString("output-label", account.OutputLabel);
            writer.WriteString("actual-transfer-account-header", account.ActualTransferAccountHeader);
            writer.WriteString("transfer-status-header", account.TransferStatusHeader);
            writer.WriteString("share-url-header", account.ShareUrlHeader);
            writer.WriteString("extraction-code-header", account.ExtractionCodeHeader);
            writer.WriteString("share-status-header", account.ShareStatusHeader);
        }
    }
}
