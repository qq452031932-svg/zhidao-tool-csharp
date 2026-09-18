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
        // 可重试的失败项不再硬挡住清空，但代价必须写进确认框：点"是"之后这些失败项
        // 就再也重试不了了。只说"无法撤销"太笼统，用户看不出丢掉的究竟是什么。
        var retryWarning = preview.RetryableFailureCount > 0
            ? $"\n\n其中有 {preview.RetryableFailureCount} 条失败项仍可重试，清空后将无法再重试。"
            : string.Empty;
        return $"确认清空 {preview.TerminalBatchCount} 个已结束的{label}批次历史？{retryWarning}" +
            "\n\n仅删除本地记录，不影响百度网盘文件和分享链接。此操作无法撤销。";
    }

    public Task<bool> ConfirmAsync(LocalHistoryCleanupPreview preview, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var confirmed = MessageBox.Show(BuildMessage(preview), "清空本地历史",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        return Task.FromResult(!cancellationToken.IsCancellationRequested && confirmed);
    }
}
