using System.Runtime.CompilerServices;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Jobs;

public sealed class ReplacementWorkbookExportService(
    IBatchResultProjectionRepository repository,
    ReplacementWorkbookFileNameBuilder fileNameBuilder,
    TimeProvider? timeProvider = null)
{
    private const int MaximumProjectedCellsPerPage = 10_000;
    private readonly IBatchResultProjectionRepository repository =
        repository ?? throw new ArgumentNullException(nameof(repository));
    private readonly ReplacementWorkbookFileNameBuilder fileNameBuilder =
        fileNameBuilder ?? throw new ArgumentNullException(nameof(fileNameBuilder));
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<ReplacementWorkbookExportResult> ExportAsync(
        ReplacementWorkbookExportRequest request,
        Stream output,
        IReplacementWorkbookWriter writer,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(writer);
        if (request.BatchJobId == Guid.Empty) throw new ArgumentException("A batch is required.", nameof(request));
        if (request.PageSize is < 1 or > 1_000) throw new ArgumentOutOfRangeException(nameof(request.PageSize));

        var definition = await repository.GetDefinitionAsync(request.BatchJobId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The batch result projection is unavailable.");
        var originalColumns = definition.OriginalColumns.OrderBy(column => column.ColumnOrdinal).ToArray();
        var accounts = definition.Accounts.OrderBy(account => account.OutputOrdinal).ToArray();
        ValidateDefinition(originalColumns, accounts);

        var layout = BuildLayout(originalColumns, accounts);

        long exportedRows = 0;
        await writer.WriteAsync(
            output,
            layout,
            StreamRowsAsync(request, originalColumns, accounts, count =>
            {
                exportedRows = count;
                progress?.Report(count);
            }, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        return new(
            exportedRows,
            fileNameBuilder.Build(definition.SourceFileName, timeProvider.GetUtcNow()));
    }

    public async Task<ReplacementWorkbookFilePublishReceipt> ExportToDirectoryAsync(
        ReplacementWorkbookExportRequest request,
        string destinationDirectory,
        IReplacementWorkbookFilePublisher publisher,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(destinationDirectory))
            throw new ArgumentException("A destination directory is required.", nameof(destinationDirectory));
        ArgumentNullException.ThrowIfNull(publisher);
        if (request.BatchJobId == Guid.Empty) throw new ArgumentException("A batch is required.", nameof(request));
        if (request.PageSize is < 1 or > 1_000) throw new ArgumentOutOfRangeException(nameof(request.PageSize));

        var definition = await repository.GetDefinitionAsync(request.BatchJobId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The batch result projection is unavailable.");
        return await ExportToDirectoryCoreAsync(
            request, destinationDirectory,
            fileNameBuilder.Build(definition.SourceFileName, timeProvider.GetUtcNow()),
            definition, publisher, progress, cancellationToken, manifest: null).ConfigureAwait(false);
    }

    public async Task<string> GetSuggestedFileNameAsync(
        Guid batchJobId,
        CancellationToken cancellationToken = default)
    {
        if (batchJobId == Guid.Empty) throw new ArgumentException("A batch is required.", nameof(batchJobId));
        var definition = await repository.GetDefinitionAsync(batchJobId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The batch result projection is unavailable.");
        return fileNameBuilder.Build(definition.SourceFileName, timeProvider.GetUtcNow());
    }

    public async Task<ReplacementWorkbookLayout> GetExpectedLayoutAsync(
        Guid batchJobId,
        CancellationToken cancellationToken = default)
    {
        if (batchJobId == Guid.Empty) throw new ArgumentException("A batch is required.", nameof(batchJobId));
        var definition = await repository.GetDefinitionAsync(batchJobId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The batch result projection is unavailable.");
        var originalColumns = definition.OriginalColumns.OrderBy(column => column.ColumnOrdinal).ToArray();
        var accounts = definition.Accounts.OrderBy(account => account.OutputOrdinal).ToArray();
        ValidateDefinition(originalColumns, accounts);
        return BuildLayout(originalColumns, accounts);
    }

    public async Task<ReplacementWorkbookFilePublishReceipt> ExportToDirectoryAsync(
        ReplacementWorkbookExportRequest request,
        string destinationDirectory,
        string exactTargetFileName,
        IReplacementWorkbookFilePublisher publisher,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default,
        LocalResultWorkbookManifest? manifest = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        LocalResultExportValidation.SafeFileName(exactTargetFileName, nameof(exactTargetFileName));
        ArgumentNullException.ThrowIfNull(publisher);
        if (request.BatchJobId == Guid.Empty) throw new ArgumentException("A batch is required.", nameof(request));
        if (request.PageSize is < 1 or > 1_000) throw new ArgumentOutOfRangeException(nameof(request.PageSize));
        var definition = await repository.GetDefinitionAsync(request.BatchJobId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The batch result projection is unavailable.");
        return await ExportToDirectoryCoreAsync(
            request, destinationDirectory, exactTargetFileName, definition,
            publisher, progress, cancellationToken, manifest).ConfigureAwait(false);
    }

    private async Task<ReplacementWorkbookFilePublishReceipt> ExportToDirectoryCoreAsync(
        ReplacementWorkbookExportRequest request,
        string destinationDirectory,
        string targetFileName,
        BatchResultProjectionDefinition definition,
        IReplacementWorkbookFilePublisher publisher,
        IProgress<long>? progress,
        CancellationToken cancellationToken,
        LocalResultWorkbookManifest? manifest)
    {
        var originalColumns = definition.OriginalColumns.OrderBy(column => column.ColumnOrdinal).ToArray();
        var accounts = definition.Accounts.OrderBy(account => account.OutputOrdinal).ToArray();
        ValidateDefinition(originalColumns, accounts);

        var layout = BuildLayout(originalColumns, accounts);

        if (manifest is not null)
            return await publisher.PublishWithManifestAsync(
                destinationDirectory,
                targetFileName,
                layout,
                StreamRowsAsync(request, originalColumns, accounts,
                    count => progress?.Report(count), cancellationToken),
                manifest,
                cancellationToken).ConfigureAwait(false);
        return await publisher.PublishAsync(
            destinationDirectory,
            targetFileName,
            layout,
            StreamRowsAsync(request, originalColumns, accounts,
                count => progress?.Report(count), cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private static ReplacementWorkbookLayout BuildLayout(
        IReadOnlyList<BatchResultOriginalColumn> originalColumns,
        IReadOnlyList<BatchResultProjectionAccount> accounts)
    {
        var resultLayout = new ResultColumnLayoutBuilder().Build(
            originalColumns.Select(column => (string?)column.Header).ToArray(),
            accounts.Select(account => new BatchWorkloadAccountInput(
                account.AccountId,
                account.OutputOrdinal,
                account.DisplayName,
                BatchAccountSelectionDecision.Selected,
                account.ShareEnabled,
                DistributionCount: 1)).ToArray());
        var headers = resultLayout.SourceHeaders
            .Append(resultLayout.ResolvedTitleHeader)
            .Append(resultLayout.OriginalShareUrlHeader)
            .Concat(resultLayout.Accounts.SelectMany(account => new[]
            {
                account.ActualTransferAccountHeader,
                account.TransferStatusHeader,
                account.ShareUrlHeader,
                account.ExtractionCodeHeader,
                account.ShareStatusHeader,
            }));
        return new ReplacementWorkbookLayout(headers);
    }

    private async IAsyncEnumerable<ReplacementWorkbookRow> StreamRowsAsync(
        ReplacementWorkbookExportRequest request,
        IReadOnlyList<BatchResultOriginalColumn> originalColumns,
        IReadOnlyList<BatchResultProjectionAccount> accounts,
        Action<long> report,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var projectedCellsPerResource = checked(originalColumns.Count + 2 + accounts.Count * 5);
        var boundedPageSize = Math.Min(
            request.PageSize,
            Math.Max(1, MaximumProjectedCellsPerPage / projectedCellsPerResource));
        long cursor = 0;
        long exported = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await repository.GetProjectionPageAsync(
                request.BatchJobId, cursor, boundedPageSize, cancellationToken).ConfigureAwait(false);
            if (page.Resources.Count == 0)
            {
                if (page.NextCandidateSequence is not null || page.AccountResults.Count != 0)
                    throw new InvalidOperationException("The resource projection cursor did not advance.");
                yield break;
            }

            var orderedResources = page.Resources.ToArray();
            ValidateResourcePage(orderedResources, cursor, page.NextCandidateSequence);
            var outcomes = ValidateAccountResults(orderedResources, accounts, page.AccountResults);

            foreach (var resource in orderedResources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return BuildRow(resource, originalColumns, accounts, outcomes);
                exported = checked(exported + 1);
                report(exported);
            }

            if (page.NextCandidateSequence is not { } next) yield break;
            cursor = next;
        }
    }

    private static ReplacementWorkbookRow BuildRow(
        BatchResultResourceProjection resource,
        IReadOnlyList<BatchResultOriginalColumn> originalColumns,
        IReadOnlyList<BatchResultProjectionAccount> accounts,
        IReadOnlyDictionary<(Guid ResourceId, Guid AccountId), BatchResultAccountProjection> outcomes)
    {
        if (string.IsNullOrWhiteSpace(resource.ResolvedTitle))
            throw new InvalidOperationException("The resolved result title is unavailable.");
        var definedOrdinals = originalColumns.Select(column => column.ColumnOrdinal).ToHashSet();
        if (resource.OriginalValues.Count != originalColumns.Count ||
            resource.OriginalValues.Any(value => !definedOrdinals.Contains(value.ColumnOrdinal)) ||
            resource.OriginalValues.Select(value => value.ColumnOrdinal).Distinct().Count() != resource.OriginalValues.Count)
            throw new InvalidOperationException("The original result values do not match the column definition.");
        var originalValues = resource.OriginalValues.ToDictionary(value => value.ColumnOrdinal, value => value.Text);
        var cells = new List<string?>(checked(originalColumns.Count + 2 + accounts.Count * 5));
        cells.AddRange(originalColumns.Select(column => originalValues[column.ColumnOrdinal]));
        cells.Add(resource.ResolvedTitle);
        cells.Add(resource.OriginalShareUrl);

        foreach (var account in accounts)
        {
            if (!outcomes.TryGetValue((resource.ResourceItemId, account.AccountId), out var outcome))
            {
                cells.Add(string.Empty);
                cells.Add(string.Empty);
                cells.Add(string.Empty);
                cells.Add(string.Empty);
                cells.Add(string.Empty);
                continue;
            }
            cells.Add(account.DisplayName);
            cells.Add(StableTransferStatus(outcome.TransferStatus));
            if (!account.ShareEnabled)
            {
                cells.Add(string.Empty);
                cells.Add(string.Empty);
                cells.Add(string.Empty);
                continue;
            }

            var result = ProjectOutcome(outcome);
            cells.Add(result.ExposesShareResult ? outcome.ShareUrl ?? string.Empty : string.Empty);
            cells.Add(result.ExposesShareResult ? outcome.ExtractionCode ?? string.Empty : string.Empty);
            cells.Add(outcome.TransferStatus == "Succeeded" ? result.StableStatus : string.Empty);
        }

        return new(resource.CandidateSequence, cells);
    }

    private static (string StableStatus, bool ExposesShareResult) ProjectOutcome(
        BatchResultAccountProjection? outcome)
    {
        if (outcome is null) return (string.Empty, false);
        return outcome.TransferStatus switch
        {
            "Failed" => ("转存失败", false),
            "Paused" => ("转存暂停", false),
            "ResultPending" => ("转存结果待确认", false),
            "Waiting" => ("等待转存", false),
            "Reserved" or "Preparing" or "Submitting" => ("转存处理中", false),
            "Cancelled" => ("转存已取消", false),
            "Succeeded" => outcome.ShareStatus switch
            {
                ShareOperationStatus.Succeeded => ("分享成功", true),
                ShareOperationStatus.ReusedExisting => ("复用已有分享", true),
                { } status => (StableShareStatus(status), false),
                null => (string.Empty, false),
            },
            _ => throw new InvalidOperationException("The transfer result status is invalid."),
        };
    }

    private static string StableTransferStatus(string status) => status switch
    {
        "Failed" => "转存失败",
        "Paused" => "转存暂停",
        "ResultPending" => "转存结果待确认",
        "Waiting" => "等待转存",
        "Reserved" or "Preparing" or "Submitting" => "转存处理中",
        "Cancelled" => "转存已取消",
        "Succeeded" => "转存成功",
        _ => throw new InvalidOperationException("The transfer result status is invalid."),
    };

    private static string StableShareStatus(ShareOperationStatus status) => status switch
    {
        ShareOperationStatus.WaitingTransfer => "等待转存",
        ShareOperationStatus.WaitingShare => "等待分享",
        ShareOperationStatus.Reserved => "已保留",
        ShareOperationStatus.Preparing => "准备中",
        ShareOperationStatus.Submitting => "分享中",
        ShareOperationStatus.ResultPending => "分享结果待确认",
        ShareOperationStatus.Succeeded => "分享成功",
        ShareOperationStatus.Failed => "分享失败",
        ShareOperationStatus.Paused => "暂停",
        ShareOperationStatus.ReusedExisting => "复用已有分享",
        ShareOperationStatus.Cancelled => "已取消",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    private static void ValidateResourcePage(
        IReadOnlyList<BatchResultResourceProjection> resources,
        long afterCandidateSequence,
        long? nextCandidateSequence)
    {
        var previous = afterCandidateSequence;
        var resourceIds = new HashSet<Guid>();
        foreach (var resource in resources)
        {
            if (resource.ResourceItemId == Guid.Empty || resource.CandidateSequence <= previous ||
                !resourceIds.Add(resource.ResourceItemId))
                throw new InvalidOperationException("The resource projection page is not a strict keyset page.");
            previous = resource.CandidateSequence;
        }
        if (nextCandidateSequence is not null && nextCandidateSequence != previous)
            throw new InvalidOperationException("The resource projection cursor does not match the page boundary.");
    }

    private static IReadOnlyDictionary<(Guid ResourceId, Guid AccountId), BatchResultAccountProjection>
        ValidateAccountResults(
            IReadOnlyList<BatchResultResourceProjection> resources,
            IReadOnlyList<BatchResultProjectionAccount> accounts,
            IReadOnlyList<BatchResultAccountProjection> accountResults)
    {
        var resourceIds = resources.Select(resource => resource.ResourceItemId).ToHashSet();
        var accountDefinitions = accounts.ToDictionary(account => account.AccountId);
        var outcomes = new Dictionary<(Guid ResourceId, Guid AccountId), BatchResultAccountProjection>();
        foreach (var outcome in accountResults)
        {
            if (!resourceIds.Contains(outcome.ResourceItemId) ||
                !accountDefinitions.TryGetValue(outcome.AccountId, out var account) ||
                outcome.OutputOrdinal != account.OutputOrdinal ||
                !outcomes.TryAdd((outcome.ResourceItemId, outcome.AccountId), outcome))
                throw new InvalidOperationException("The account result does not match the current projection page.");
        }
        return outcomes;
    }

    private static void ValidateDefinition(
        IReadOnlyList<BatchResultOriginalColumn> originalColumns,
        IReadOnlyList<BatchResultProjectionAccount> accounts)
    {
        if (originalColumns.Any(column => column.ColumnOrdinal < 0) ||
            originalColumns.Select(column => column.ColumnOrdinal).Distinct().Count() != originalColumns.Count)
            throw new InvalidOperationException("The original result column definition is invalid.");
        if (accounts.Count == 0 || accounts.Any(account => account.AccountId == Guid.Empty || account.OutputOrdinal < 1) ||
            accounts.Select(account => account.AccountId).Distinct().Count() != accounts.Count ||
            accounts.Select(account => account.OutputOrdinal).Distinct().Count() != accounts.Count ||
            accounts.Select((account, index) => account.OutputOrdinal != index + 1).Any(invalid => invalid))
            throw new InvalidOperationException("The result account definition is invalid.");
    }
}
