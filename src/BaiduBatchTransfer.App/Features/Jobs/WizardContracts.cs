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
    int ReservedSlotCount);

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
    int TargetSuccessCount);

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
    int ReadyResourceCount);

public sealed record WizardConfirmationSummary(
    long ResourceCount,
    long OperationCount,
    string ConfirmationToken,
    bool HasSufficientDiskSpace);

public interface IWizardBatchService
{
    Task<WizardConfirmationSummary> PreviewAsync(WizardBatchDraft draft, CancellationToken cancellationToken);
    Task ConfirmAsync(WizardBatchDraft draft, string confirmationToken, CancellationToken cancellationToken);
}

public sealed class WizardBatchService(CreateBatchService service) : IWizardBatchService
{
    public async Task<WizardConfirmationSummary> PreviewAsync(WizardBatchDraft draft, CancellationToken cancellationToken)
    {
        var summary = await service.PreviewAsync(ToApplicationDraft(draft), cancellationToken).ConfigureAwait(false);
        return new(summary.ResourceCount, summary.OperationCount, summary.ConfirmationToken,
            summary.StorageEstimate.HasSufficientSpace);
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
            account.TargetSuccessCount)).ToArray(),
        draft.DefaultTargetPath,
        draft.FolderCreationMode,
        draft.DuplicatePolicy,
        draft.ValidationMode,
        0,
        draft.IgnoreMappedAccountsForReplication);
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
