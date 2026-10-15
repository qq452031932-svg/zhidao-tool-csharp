using System.Windows;
using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.App.Features.Common;

public enum UnfinishedBatchChoice
{
    /// <summary>先结束未结束的批次，再一起清空。</summary>
    EndThenClear,
    /// <summary>只清空已经结束的批次，未结束的保留。</summary>
    ClearEndedOnly,
    Cancel,
}

public interface ILocalHistoryCleanupInteraction
{
    Task<bool> ConfirmAsync(LocalHistoryCleanupPreview preview, CancellationToken cancellationToken = default);

    /// <summary>还有未结束的批次时，问用户要不要先把它们结束再一起清空。</summary>
    Task<UnfinishedBatchChoice> ChooseUnfinishedAsync(
        int unfinishedBatchCount, int endedBatchCount, CancellationToken cancellationToken = default) =>
        Task.FromResult(UnfinishedBatchChoice.ClearEndedOnly);
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

    public static string BuildUnfinishedMessage(int unfinishedBatchCount, int endedBatchCount) =>
        $"还有 {unfinishedBatchCount} 个转存批次没有结束（排队中、进行中或已暂停），清空历史不会删除它们。" +
        "\n\n【是】先停止这些批次，再一起清空。没开始的记录会标为已取消，已转存成功的不受影响；" +
        "正在提交的几条不能中途打断，那一批要等它完成后再清空一次。" +
        (endedBatchCount > 0
            ? $"\n【否】只清空已经结束的 {endedBatchCount} 个批次，未结束的保留。"
            : "\n【否】不停止它们，也没有已结束的批次可清空。") +
        "\n【取消】什么都不做。";

    public Task<UnfinishedBatchChoice> ChooseUnfinishedAsync(
        int unfinishedBatchCount, int endedBatchCount, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var answer = MessageBox.Show(BuildUnfinishedMessage(unfinishedBatchCount, endedBatchCount), "清空本地历史",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        return Task.FromResult(cancellationToken.IsCancellationRequested ? UnfinishedBatchChoice.Cancel : answer switch
        {
            MessageBoxResult.Yes => UnfinishedBatchChoice.EndThenClear,
            MessageBoxResult.No => UnfinishedBatchChoice.ClearEndedOnly,
            _ => UnfinishedBatchChoice.Cancel,
        });
    }

    public Task<bool> ConfirmAsync(LocalHistoryCleanupPreview preview, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var confirmed = MessageBox.Show(BuildMessage(preview), "清空本地历史",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        return Task.FromResult(!cancellationToken.IsCancellationRequested && confirmed);
    }
}
