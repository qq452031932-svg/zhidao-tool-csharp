using BaiduBatchTransfer.Application.Imports;
using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.App.Features.Jobs;

public enum WizardStepKind { Import, Precheck, Destination, Allocation, Confirmation }

public sealed class WizardStepItem(WizardStepKind kind, string title) : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    private bool isSkipped;
    private bool isCurrent;
    public WizardStepKind Kind { get; } = kind;
    public string Title { get; } = title;
    public bool IsSkipped { get => isSkipped; set => SetProperty(ref isSkipped, value); }
    public bool IsCurrent { get => isCurrent; set => SetProperty(ref isCurrent, value); }
}

public enum ExcelAccountHandling
{
    Unresolved,
    KeepMappings,
    MapNames,
    OverrideAll,
    IgnoreColumn,
}

public sealed record WizardAccountOption(
    Guid AccountId,
    string DisplayName,
    string Status,
    bool IsEnabled,
    bool IsDeleted,
    long? CapacityTotal,
    long? CapacityUsed,
    int LockedItemCount,
    int ExclusiveCandidateCount,
    int PublicCandidateCount,
    int UnusedExclusiveCandidateCount,
    int OriginalTargetSuccessCount,
    int EffectiveTargetSuccessCount,
    int ReachableTargetSuccessCount,
    int FulfilledCount,
    int ReservedSlotCount,
    BatchAccountSelectionDecision SelectionDecision,
    bool ShareEnabled = true,
    long PlannedTransferCount = 0,
    long PlannedShareCount = 0)
{
    public bool IsSelected => SelectionDecision == BatchAccountSelectionDecision.Selected;

    public override string ToString() =>
        $"WizardAccountOption {{ Identity = [REDACTED], DisplayName = [REDACTED], Status = {Status}, IsEnabled = {IsEnabled}, IsDeleted = {IsDeleted}, SelectionDecision = {SelectionDecision}, ShareEnabled = {ShareEnabled}, PlannedTransferCount = {PlannedTransferCount}, PlannedShareCount = {PlannedShareCount} }}";
}

public sealed record ImportWizardContext(
    Guid ImportSessionId,
    string JobName,
    int ReadyResourceCount,
    bool HasExcelAccountColumn,
    int UnresolvedAccountMappingCount,
    int ConflictRowCount,
    int NeedsResolutionRowCount,
    string DefaultTargetPath);

public sealed record ImportPreviewPageRequest(
    Guid ImportSessionId,
    int PageSize,
    long? AfterSourceRowNumber = null,
    string? Disposition = null,
    string? PrecheckStatus = null);

public sealed record ImportPreviewRow(
    Guid RowId,
    int SourceRowNumber,
    string ShareUrl,
    string? ExtractionCode,
    string? RequestedAccountName,
    string DispositionText,
    string? PrecheckText);

public sealed record ImportPreviewPage(
    IReadOnlyList<ImportPreviewRow> Items,
    long TotalCount,
    long? NextSourceRowNumber);

public interface IImportPreviewPageSource
{
    Task<ImportPreviewPage> LoadAsync(ImportPreviewPageRequest request, CancellationToken cancellationToken);
}

public sealed class ImportPreviewPageSource(ImportPageQuery query) : IImportPreviewPageSource
{
    public async Task<ImportPreviewPage> LoadAsync(
        ImportPreviewPageRequest request,
        CancellationToken cancellationToken)
    {
        if (request.PageSize is < 1 or > 1_000) throw new ArgumentOutOfRangeException(nameof(request));
        var page = await query.ExecuteAsync(new(
            request.ImportSessionId,
            request.Disposition,
            request.PrecheckStatus,
            request.PageSize,
            request.AfterSourceRowNumber), cancellationToken).ConfigureAwait(false);
        return new(
            page.Items.Select(row => new ImportPreviewRow(
                row.Id,
                row.SourceRowNumber,
                row.ShareUrl,
                row.ExtractionCode,
                row.RequestedAccountName,
                ImportUiText.Disposition(row.Disposition),
                ImportUiText.Precheck(row.PrecheckStatus))).ToArray(),
            page.TotalCount,
            page.NextSourceRowNumber);
    }
}

public sealed record WizardAccountDraft(
    Guid AccountId,
    int PriorityOrder,
    string DefaultTargetPath,
    int TargetItemCount,
    int TargetSuccessCount,
    BatchAccountSelectionDecision SelectionDecision,
    bool ShareEnabled = true)
{
    public bool IsSelected => SelectionDecision == BatchAccountSelectionDecision.Selected;

    public override string ToString() =>
        $"WizardAccountDraft {{ Identity = [REDACTED], PriorityOrder = {PriorityOrder}, TargetPath = [REDACTED], TargetItemCount = {TargetItemCount}, TargetSuccessCount = {TargetSuccessCount}, SelectionDecision = {SelectionDecision}, ShareEnabled = {ShareEnabled} }}";
}

public sealed record WizardBatchDraft(
    Guid BatchJobId,
    Guid ImportSessionId,
    string JobName,
    TransferMode TransferMode,
    QuotaBasis QuotaBasis,
    ShortagePolicy ShortagePolicy,
    IReadOnlyList<WizardAccountDraft> Accounts,
    string DefaultTargetPath,
    string FolderCreationMode,
    DuplicatePolicy DuplicatePolicy,
    string ValidationMode,
    bool IgnoreMappedAccountsForReplication,
    int ReadyResourceCount,
    bool IsSharePermanent = true,
    bool IsShareAccessUnlimited = true,
    ShareExtractionCodeBehavior ExtractionCodeBehavior = ShareExtractionCodeBehavior.Random,
    string? FixedExtractionCode = null,
    string ExistingSharePolicy = "Ask")
{
    public override string ToString() =>
        $"WizardBatchDraft {{ Identity = [REDACTED], JobName = [REDACTED], TransferMode = {TransferMode}, QuotaBasis = {QuotaBasis}, Accounts = {Accounts?.Count ?? 0}, PathsAndCodes = [REDACTED], IsSharePermanent = {IsSharePermanent}, IsShareAccessUnlimited = {IsShareAccessUnlimited}, ExtractionCodeBehavior = {ExtractionCodeBehavior}, ExistingSharePolicy = {ExistingSharePolicy} }}";
}

public sealed record WizardConfirmationSummary(
    long ResourceCount,
    long OperationCount,
    string ConfirmationToken,
    bool HasSufficientDiskSpace,
    IReadOnlyList<WizardAccountConfirmation>? Accounts = null,
    long RequiredFreeBytes = 0,
    long AvailableFreeBytes = 0,
    long TransferCount = 0,
    long ShareCount = 0,
    long TotalActionCount = 0,
    int ResultColumnCount = 0)
{
    public override string ToString() =>
        $"WizardConfirmationSummary {{ ResourceCount = {ResourceCount}, OperationCount = {OperationCount}, TransferCount = {TransferCount}, ShareCount = {ShareCount}, TotalActionCount = {TotalActionCount}, ResultColumnCount = {ResultColumnCount}, Accounts = {Accounts?.Count ?? 0}, ConfirmationToken = [REDACTED] }}";
}

public sealed record WizardAccountConfirmation(
    Guid AccountId,
    int OriginalTarget,
    int EffectiveTarget,
    int ReachableTarget,
    int Fulfilled,
    int Reserved,
    int Locked,
    int Exclusive,
    int Public,
    int Unused,
    long? PlannedTransferCount = null,
    long? PlannedShareCount = null,
    long? ConfirmedTransferCount = null,
    long? ConfirmedShareCount = null,
    bool ShareEnabled = true)
{
    public override string ToString() =>
        $"WizardAccountConfirmation {{ Identity = [REDACTED], PlannedTransferCount = {PlannedTransferCount}, PlannedShareCount = {PlannedShareCount}, ConfirmedTransferCount = {ConfirmedTransferCount}, ConfirmedShareCount = {ConfirmedShareCount}, ShareEnabled = {ShareEnabled} }}";
}

public interface IWizardBatchService
{
    Task<WizardConfirmationSummary> PreviewAsync(WizardBatchDraft draft, CancellationToken cancellationToken);
    Task ConfirmAsync(WizardBatchDraft draft, string confirmationToken, CancellationToken cancellationToken);
}

public sealed record WizardDraftMutation(
    int TotalRows,
    int ReadyRows,
    int InvalidRows,
    int DuplicateRows,
    int ConflictRows,
    int NeedsResolutionRows,
    int ExcludedRows,
    int UnresolvedAccountMappings = 0);

public interface IWizardDraftWorkflow
{
    Task<WizardDraftMutation> SwitchModeAsync(Guid sessionId, TransferMode mode, CancellationToken cancellationToken);
    Task<WizardDraftMutation> AutoMapUniqueAccountNamesAsync(Guid sessionId, TransferMode mode, CancellationToken cancellationToken);
    Task<WizardDraftMutation> IgnoreAllAccountNamesAsync(Guid sessionId, TransferMode mode, CancellationToken cancellationToken);
    Task<WizardDraftMutation> OverrideAllAccountsAsync(Guid sessionId, Guid accountId, TransferMode mode, CancellationToken cancellationToken);
}

public enum WizardPrecheckStatus
{
    Completed,
    PausedForLogin,
    PausedForVerification,
    Unavailable,
}

public sealed record WizardPrecheckResult(
    WizardPrecheckStatus Status,
    int ProcessedRows,
    int InvalidLinkCount = 0,
    int MissingExtractionCodeCount = 0,
    int AccountIssueCount = 0,
    int ConflictCount = 0)
{
    public bool HasActionableFailures =>
        InvalidLinkCount > 0 || MissingExtractionCodeCount > 0 || AccountIssueCount > 0 || ConflictCount > 0;
}

public interface IWizardPrecheckWorkflow
{
    Task<WizardPrecheckResult> RunAsync(
        Guid sessionId,
        Guid accountId,
        CancellationToken cancellationToken);
}

internal sealed class UnavailableWizardPrecheckWorkflow : IWizardPrecheckWorkflow
{
    public Task<WizardPrecheckResult> RunAsync(
        Guid sessionId,
        Guid accountId,
        CancellationToken cancellationToken) =>
        Task.FromResult(new WizardPrecheckResult(WizardPrecheckStatus.Unavailable, 0));
}

public sealed class WizardDraftWorkflow(
    TransferModeReclassificationService reclassification,
    RequestedAccountMappingService mappings) : IWizardDraftWorkflow
{
    public async Task<WizardDraftMutation> SwitchModeAsync(Guid sessionId, TransferMode mode, CancellationToken cancellationToken) =>
        Map(await reclassification.SwitchAsync(sessionId, mode, cancellationToken).ConfigureAwait(false));

    public async Task<WizardDraftMutation> AutoMapUniqueAccountNamesAsync(
        Guid sessionId,
        TransferMode mode,
        CancellationToken cancellationToken)
    {
        var unique = new List<AccountMappingDecision>();
        var unresolved = 0;
        for (var pageNumber = 1; ; pageNumber++)
        {
            var page = await mappings.GetGroupsPageAsync(sessionId, pageNumber, 1_000, cancellationToken).ConfigureAwait(false);
            unique.AddRange(page.Items
                .Where(x => x.Status == AccountNameMatchStatus.Unique)
                .Select(x => AccountMappingDecision.PreserveUnique(x.RequestedAccountName)));
            unresolved += page.Items.Count(x => x.Status != AccountNameMatchStatus.Unique);
            if ((long)pageNumber * page.PageSize >= page.TotalCount) break;
        }

        var result = unique.Count == 0
            ? await reclassification.SwitchAsync(sessionId, mode, cancellationToken).ConfigureAwait(false)
            : await mappings.ApplyAsync(sessionId, unique, mode, cancellationToken).ConfigureAwait(false);
        return Map(result) with { UnresolvedAccountMappings = unresolved };
    }

    public async Task<WizardDraftMutation> IgnoreAllAccountNamesAsync(Guid sessionId, TransferMode mode, CancellationToken cancellationToken) =>
        Map(await mappings.IgnoreAllAsync(sessionId, mode, cancellationToken).ConfigureAwait(false));

    public async Task<WizardDraftMutation> OverrideAllAccountsAsync(Guid sessionId, Guid accountId, TransferMode mode, CancellationToken cancellationToken) =>
        Map(await mappings.OverrideAllAsync(sessionId, accountId, mode, cancellationToken).ConfigureAwait(false));

    private static WizardDraftMutation Map(DraftMutationResult value) => new(
        value.TotalRows, value.ReadyRows, value.InvalidRows, value.DuplicateRows,
        value.ConflictRows, value.NeedsResolutionRows, value.ExcludedRows);
}

internal sealed class PassiveWizardDraftWorkflow : IWizardDraftWorkflow
{
    public Task<WizardDraftMutation> SwitchModeAsync(Guid sessionId, TransferMode mode, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("草稿服务尚未连接。");
    public Task<WizardDraftMutation> AutoMapUniqueAccountNamesAsync(Guid sessionId, TransferMode mode, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("草稿服务尚未连接。");
    public Task<WizardDraftMutation> IgnoreAllAccountNamesAsync(Guid sessionId, TransferMode mode, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("草稿服务尚未连接。");
    public Task<WizardDraftMutation> OverrideAllAccountsAsync(Guid sessionId, Guid accountId, TransferMode mode, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("草稿服务尚未连接。");
}

public sealed class WizardBatchService(CreateBatchService service) : IWizardBatchService
{
    public async Task<WizardConfirmationSummary> PreviewAsync(WizardBatchDraft draft, CancellationToken cancellationToken)
    {
        var summary = await service.PreviewAsync(ToApplicationDraft(draft), cancellationToken).ConfigureAwait(false);
        var publicCandidates = Math.Max(0, checked((int)summary.ResourceCount) - summary.Accounts.Sum(x => x.ExclusiveCandidateCount));
        return new(summary.ResourceCount, summary.OperationCount, summary.ConfirmationToken,
            summary.StorageEstimate.HasSufficientSpace,
            summary.Accounts.Select(account => new WizardAccountConfirmation(
                account.AccountId,
                account.TargetSuccessCount,
                account.EffectiveTargetSuccessCount,
                account.ReachableTargetSuccessCount,
                account.FulfilledCount,
                account.ReservedSlotCount,
                account.LockedItemCount,
                account.ExclusiveCandidateCount,
                publicCandidates,
                account.UnusedCandidateCount,
                account.PlannedTransferCount,
                account.PlannedShareCount,
                account.PlannedTransferCount,
                account.PlannedShareCount,
                account.ShareEnabled)).ToArray(),
            summary.StorageEstimate.RequiredFreeBytes,
            summary.StorageEstimate.AvailableFreeBytes,
            summary.TransferCount,
            summary.ShareCount,
            summary.TotalActionCount,
            summary.ResultColumnCount);
    }

    public async Task ConfirmAsync(WizardBatchDraft draft, string confirmationToken, CancellationToken cancellationToken)
    {
        _ = await service.ConfirmAsync(ToApplicationDraft(draft), confirmationToken, cancellationToken).ConfigureAwait(false);
    }

    private static BatchDraft ToApplicationDraft(WizardBatchDraft draft) => new(
        draft.BatchJobId,
        draft.ImportSessionId,
        draft.JobName,
        draft.TransferMode,
        draft.QuotaBasis,
        draft.ShortagePolicy,
        draft.Accounts.Select(account => new BatchAccountDraft(
            account.AccountId,
            account.PriorityOrder,
            account.DefaultTargetPath,
            account.TargetItemCount,
            account.TargetSuccessCount,
            account.SelectionDecision,
            account.ShareEnabled)).ToArray(),
        draft.DefaultTargetPath,
        draft.FolderCreationMode,
        draft.DuplicatePolicy,
        draft.ValidationMode,
        0,
        draft.IgnoreMappedAccountsForReplication,
        draft.IsSharePermanent,
        draft.IsShareAccessUnlimited,
        draft.ExtractionCodeBehavior,
        draft.FixedExtractionCode,
        draft.ExistingSharePolicy);
}

internal static class ImportUiText
{
    public static string Disposition(string value) => value switch
    {
        "Ready" => "就绪",
        "Invalid" => "格式异常",
        "Duplicate" => "重复",
        "Conflict" => "冲突待处理",
        "NeedsResolution" => "等待重新解析",
        "Excluded" => "已排除",
        _ => "状态未知",
    };

    public static string? Precheck(string? value) => value switch
    {
        null or "Pending" => null,
        "Succeeded" => "有效",
        "Failed" => "未通过",
        _ => "状态未知",
    };
}
