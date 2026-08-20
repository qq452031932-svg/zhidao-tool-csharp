using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.App.Services;

public interface IExcelColumnMappingPresenter
{
    Task<ConfirmedImportColumnMapping?> ConfirmAsync(
        TabularWorkbookInspection inspection,
        CancellationToken cancellationToken = default);
}
