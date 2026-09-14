using System.Windows;
using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.App.Features.Common;

public interface ILocalHistoryCleanupInteraction
{
    Task<bool> ConfirmAsync(LocalHistoryCleanupPreview preview, CancellationToken cancellationToken = default);
}

public sealed class WpfLocalHistoryCleanupInteraction : ILocalHistoryCleanupInteraction
{
    public static string BuildMessage(LocalHistoryCleanupPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        var label = preview.Kind == LocalHistoryKind.Transfer ? "转存" : "分享";
        return $"确认清空 {preview.TerminalBatchCount} 个已结束的{label}批次历史？\n\n仅删除本地记录，不影响百度网盘文件和分享链接。此操作无法撤销。";
    }

    public Task<bool> ConfirmAsync(LocalHistoryCleanupPreview preview, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var confirmed = MessageBox.Show(BuildMessage(preview), "清空本地历史",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        return Task.FromResult(!cancellationToken.IsCancellationRequested && confirmed);
    }
}
