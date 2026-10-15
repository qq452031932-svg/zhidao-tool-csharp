using System.Windows;
using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.App.Services;

namespace BaiduBatchTransfer.App.Features.Transfers;

/// <summary>
/// 关软件前的提醒：没暂停就关掉的转存批次，下次启动按停止处理、不再接着跑（用户 10-14 定的规则）。
/// 用户 10-14 要求关的时候先提醒一句，免得以为关掉还能接着转。
/// </summary>
public static class TransferCloseReminder
{
    public const string Title = "正在转存";

    public static string BuildMessage(int runningBatchCount) =>
        $"还有 {runningBatchCount} 个转存批次正在进行，没有暂停。" +
        "\n\n现在关闭，这些批次会按停止处理：没开始的记录取消，下次打开不会接着跑；已经转存成功的不受影响。" +
        "\n想下次接着转，请先点「否」，在处理结果里点「暂停」后再关闭。" +
        "\n\n【是】关闭并停止这些批次\n【否】不关闭";

    /// <summary>可以关闭时返回 true。查不到状态时照常关闭：提醒不能把用户关在软件里。</summary>
    public static async Task<bool> ConfirmAsync(
        EndBatchService? endBatchService,
        Func<string, bool> askToClose,
        IRunLog? runLog = null)
    {
        ArgumentNullException.ThrowIfNull(askToClose);
        if (endBatchService is null) return true;
        int running;
        try
        {
            running = await endBatchService.CountLeftRunningAsync()
                .WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(true);
        }
        catch (Exception)
        {
            return true;
        }
        if (running <= 0) return true;
        var close = askToClose(BuildMessage(running));
        runLog?.Write("转存", close
            ? $"关闭软件时还有 {running} 个转存批次没有暂停，用户确认关闭，下次启动按停止处理。"
            : $"关闭软件时还有 {running} 个转存批次没有暂停，用户选择不关闭。");
        return close;
    }

    public static bool AskWithMessageBox(Window? owner, string message)
    {
        var answer = owner is null
            ? MessageBox.Show(message, Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            : MessageBox.Show(owner, message, Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        return answer == MessageBoxResult.Yes;
    }
}
