using BaiduBatchTransfer.App.Features.Jobs;
using BaiduBatchTransfer.Application.Scheduling;

namespace BaiduBatchTransfer.App.Services;

public sealed class ExistingShareDecisionPresenter : IExistingShareDecisionPresenter
{
    public async Task<ConfirmExistingShareDecisionCommand?> ConfirmAsync(
        ExistingShareBatchSummary summary,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(summary);
        cancellationToken.ThrowIfCancellationRequested();
        var application = System.Windows.Application.Current
            ?? throw new InvalidOperationException("已有分享决定窗口只能在桌面应用中打开。");

        ConfirmExistingShareDecisionCommand? Show()
        {
            cancellationToken.ThrowIfCancellationRequested();
            var viewModel = new ExistingShareDecisionViewModel(summary);
            var window = new ExistingShareDecisionWindow(viewModel)
            {
                Owner = application.MainWindow,
            };
            return window.ShowDialog() == true ? viewModel.ConfirmedCommand : null;
        }

        if (application.Dispatcher.CheckAccess()) return Show();
        return await application.Dispatcher.InvokeAsync(
            Show,
            System.Windows.Threading.DispatcherPriority.Normal,
            cancellationToken);
    }
}
