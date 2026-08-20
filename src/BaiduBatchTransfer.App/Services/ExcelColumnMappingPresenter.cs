using System.Windows;
using BaiduBatchTransfer.App.Features.Imports;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Infrastructure.Excel;

namespace BaiduBatchTransfer.App.Services;

public sealed class ExcelColumnMappingPresenter(ExcelColumnSuggestionEngine suggestionEngine)
    : IExcelColumnMappingPresenter
{
    private readonly ExcelColumnSuggestionEngine suggestionEngine =
        suggestionEngine ?? throw new ArgumentNullException(nameof(suggestionEngine));

    public async Task<ConfirmedImportColumnMapping?> ConfirmAsync(
        TabularWorkbookInspection inspection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        cancellationToken.ThrowIfCancellationRequested();
        var application = System.Windows.Application.Current
            ?? throw new InvalidOperationException("列对应窗口只能在桌面应用中打开。");

        ConfirmedImportColumnMapping? Show()
        {
            cancellationToken.ThrowIfCancellationRequested();
            var viewModel = new ExcelColumnMappingViewModel(inspection, suggestionEngine.Suggest(inspection));
            var window = new ExcelColumnMappingWindow(viewModel)
            {
                Owner = application.MainWindow,
            };
            return window.ShowDialog() == true ? viewModel.ConfirmedMapping : null;
        }

        if (application.Dispatcher.CheckAccess()) return Show();
        return await application.Dispatcher.InvokeAsync(Show, System.Windows.Threading.DispatcherPriority.Normal, cancellationToken);
    }
}
