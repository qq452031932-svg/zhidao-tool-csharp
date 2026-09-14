using System.Windows;

namespace BaiduBatchTransfer.App.Features.Transfers;

public interface ICurrentImportCleanupInteraction
{
    Task<bool> ConfirmAsync(long currentRowCount, CancellationToken cancellationToken = default);
}

public sealed class WpfCurrentImportCleanupInteraction : ICurrentImportCleanupInteraction
{
    public static string BuildMessage(long currentRowCount)
    {
        if (currentRowCount < 0) throw new ArgumentOutOfRangeException(nameof(currentRowCount));
        return $"确认清空当前导入的 {currentRowCount} 条记录？\n\n只删除本地待处理导入，不影响百度网盘文件和分享链接。此操作无法撤销。";
    }

    public Task<bool> ConfirmAsync(long currentRowCount, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var confirmed = MessageBox.Show(BuildMessage(currentRowCount), "清空当前导入",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(confirmed);
    }
}

internal sealed class RefusingCurrentImportCleanupInteraction : ICurrentImportCleanupInteraction
{
    public static RefusingCurrentImportCleanupInteraction Instance { get; } = new();
    public Task<bool> ConfirmAsync(long currentRowCount, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(false);
    }
}
