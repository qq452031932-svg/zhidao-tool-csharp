using System.IO;
using System.Text;
using BaiduBatchTransfer.App.Features.Common;

namespace BaiduBatchTransfer.App.Services;

/// <summary>
/// 落盘的运行日志：软件启动/关闭以及采集、转存、分享各页写到界面上的进展都记一份，
/// 出问题时不必靠截图回忆。写入失败不影响功能本身。
/// </summary>
public interface IRunLog
{
    /// <param name="source">来源，例如「采集」「转存」。</param>
    void Write(string source, string? message);
}

/// <summary>
/// 把运行日志追加到一个 TXT 文件。每行先经过与界面日志相同的脱敏（Cookie、网盘链接、
/// 提取码、本机路径），所以文件可以直接发给别人排查。
/// </summary>
public sealed class FileRunLog : IRunLog
{
    public const string FileName = "运行日志.txt";
    public const string PreviousFileName = "运行日志-旧.txt";
    private const long DefaultMaximumBytes = 5 * 1024 * 1024;

    private readonly object sync = new();
    private readonly string path;
    private readonly string previousPath;
    private readonly TimeProvider timeProvider;
    private readonly long maximumBytes;
    private string? lastLine;

    public FileRunLog(string directory, TimeProvider? timeProvider = null, long maximumBytes = DefaultMaximumBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (maximumBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        var fullDirectory = Path.GetFullPath(directory);
        path = Path.Combine(fullDirectory, FileName);
        previousPath = Path.Combine(fullDirectory, PreviousFileName);
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.maximumBytes = maximumBytes;
    }

    public string FilePath => path;

    public void Write(string source, string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        var text = WorkspaceLogBuffer.SanitizeForEntry(message.Trim())
            .ReplaceLineEndings(Environment.NewLine + "    ");
        var line = $"[{source}] {text}";
        lock (sync)
        {
            // 同一句话连着出现（状态栏与日志各报一次、定时刷新反复失败）只记第一次。
            if (string.Equals(line, lastLine, StringComparison.Ordinal)) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                RollIfTooLarge();
                // 允许别的程序同时打开：用户常常开着记事本看日志。
                using var stream = new FileStream(
                    path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                // 带 BOM，旧版记事本才不会把中文显示成乱码。
                using var writer = new StreamWriter(stream, new UTF8Encoding(stream.Length == 0));
                writer.WriteLine($"{timeProvider.GetLocalNow():yyyy-MM-dd HH:mm:ss} {line}");
                lastLine = line;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                or NotSupportedException or System.Security.SecurityException)
            {
                // 日志写不进去（磁盘满、文件被独占）不该让采集或转存跟着失败。
            }
        }
    }

    private void RollIfTooLarge()
    {
        var current = new FileInfo(path);
        if (!current.Exists || current.Length < maximumBytes) return;
        File.Move(path, previousPath, overwrite: true);
    }
}

public static class RunLogExtensions
{
    /// <summary>把工作区界面日志的每一条同时写进运行日志；没有运行日志时返回 null。</summary>
    public static Action<WorkspaceLogEntry>? ForWorkspace(this IRunLog? runLog, string source) =>
        runLog is null
            ? null
            // 只标错误：界面上的「警告」多半是按措辞猜的（带「请」字的提示也算），写进文件反而误导排查。
            : entry => runLog.Write(
                source,
                entry.Level is WorkspaceLogLevel.Error ? $"【{entry.LevelText}】{entry.Message}" : entry.Message);
}
