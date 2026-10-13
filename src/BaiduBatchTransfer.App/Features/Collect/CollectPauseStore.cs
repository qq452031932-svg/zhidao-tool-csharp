using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace BaiduBatchTransfer.App.Features.Collect;

/// <summary>
/// 一次已暂停、可以接着采的任务。只记参数与数量；已采结果在助手的断点文件里，Cookie 不在其中。
/// </summary>
public sealed record CollectPausedTask(
    string Mode,
    IReadOnlyList<string> Keywords,
    int Count,
    int Found,
    int Target,
    bool KeywordLineMode,
    DateTimeOffset PausedAt);

/// <summary>保存暂停的任务，关闭软件后再打开仍能继续。</summary>
public interface ICollectPauseStore
{
    /// <summary>没有记录或记录不可用时返回 null。</summary>
    CollectPausedTask? Load();

    void Save(CollectPausedTask task);

    void Clear();
}

/// <summary>把暂停记录存成本机的一个 JSON 文件。</summary>
public sealed class FileCollectPauseStore(string path) : ICollectPauseStore
{
    private readonly string path = Path.GetFullPath(path);

    public CollectPausedTask? Load()
    {
        try
        {
            if (!File.Exists(path)) return null;
            var task = JsonSerializer.Deserialize<CollectPausedTask>(File.ReadAllText(path));
            if (task is null || task.Mode is not ("precise" or "fuzzy") || task.Keywords is null) return null;
            var keywords = task.Keywords.Where(word => !string.IsNullOrWhiteSpace(word)).ToArray();
            return keywords.Length == 0 ? null : task with { Keywords = keywords };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or NotSupportedException)
        {
            // 记录损坏时按没有暂停任务处理，不拦住用户开始新的采集。
            return null;
        }
    }

    public void Save(CollectPausedTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(task));
        File.Move(temporary, path, overwrite: true);
    }

    public void Clear()
    {
        // 目录尚不存在时 File.Delete 会抛异常，而「没有记录」本就是清除后的状态。
        if (File.Exists(path)) File.Delete(path);
    }
}
