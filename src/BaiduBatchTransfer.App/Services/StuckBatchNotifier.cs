using BaiduBatchTransfer.Application.Jobs;

namespace BaiduBatchTransfer.App.Services;

/// <summary>
/// 后台自动放弃收尾后告诉用户：写进运行日志文件，并显示在转存页日志里（与启动时的提示走同一条路）。
/// </summary>
public sealed class StuckBatchNotifier(IRunLog? runLog, StartupNotices notices) : IStuckBatchNotifier
{
    private readonly StartupNotices notices = notices ?? throw new ArgumentNullException(nameof(notices));

    public void Notify(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        runLog?.Write("转存", message);
        notices.Post(message);
    }
}
