namespace BaiduBatchTransfer.App.Services;

/// <summary>
/// 启动恢复时产生、要在转存页日志里让用户看到的提示（例如上次没暂停就关软件、批次已按停止处理）。
/// 恢复通常在主窗口建好之前完成，页面建好后把攒下的取走；激活后才恢复时，页面已经在了，直接转给它。
/// 运行日志文件由发出方自己写，这里只管界面。
/// </summary>
public sealed class StartupNotices
{
    private readonly object sync = new();
    private readonly List<string> pending = [];
    private Action<string>? listener;

    public void Post(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        Action<string>? target;
        lock (sync)
        {
            target = listener;
            if (target is null) pending.Add(message);
        }
        target?.Invoke(message);
    }

    /// <summary>登记接收方，并返回登记之前攒下的提示。之后的提示可能来自后台线程。</summary>
    public IReadOnlyList<string> Attach(Action<string> onPosted)
    {
        ArgumentNullException.ThrowIfNull(onPosted);
        lock (sync)
        {
            listener = onPosted;
            var earlier = pending.ToArray();
            pending.Clear();
            return earlier;
        }
    }
}
