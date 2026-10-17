using System.IO;
using System.Text;

namespace BaiduBatchTransfer.App.Services;

/// <summary>
/// 运行日志放在哪儿。用户 10-16 要求：放在软件目录下，打开软件所在文件夹就能找到
/// （原来在 %LOCALAPPDATA%\BaiduBatchTransfer\logs，路径太长，用户找不到）。
/// 写不进去时先临时写回原来的位置，并提醒用户自己选一个文件夹；选过的记下来，以后都用它。
/// </summary>
public sealed class RunLogLocation
{
    public const string LogsDirectoryName = "logs";
    public const string PreferenceFileName = "run-log-directory.txt";
    private const string UpdaterFileName = "Update.exe";
    private const string VelopackContentDirectoryName = "current";

    private RunLogLocation(string directory, string preferredDirectory, bool isFallback, bool isCustom, string preferencePath)
    {
        Directory = directory;
        PreferredDirectory = preferredDirectory;
        IsFallback = isFallback;
        IsCustom = isCustom;
        PreferencePath = preferencePath;
    }

    /// <summary>这次实际写日志的文件夹。</summary>
    public string Directory { get; private set; }

    /// <summary>本来想写的文件夹：用户选过的，或软件目录下的 logs。</summary>
    public string PreferredDirectory { get; private set; }

    /// <summary>本来想写的位置写不进去，临时写在原来的数据目录里，需要提醒用户选文件夹。</summary>
    public bool IsFallback { get; private set; }

    /// <summary>本来想写的是用户自己选的文件夹（不是软件目录）。</summary>
    public bool IsCustom { get; private set; }

    public string PreferencePath { get; }

    /// <summary>
    /// 软件目录：安装版（Velopack）的程序文件在「安装目录\current」里，每次更新整个 current 会被换掉，
    /// 日志放进去更新后就没了，所以放在安装目录（current 的上一级，有 Update.exe 的那层）。
    /// 便携版或开发时直接用程序所在目录。
    /// </summary>
    public static string ResolveSoftwareDirectory(string baseDirectory, Func<string, bool>? fileExists = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        fileExists ??= File.Exists;
        var directory = Path.GetFullPath(baseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(directory);
        if (string.Equals(Path.GetFileName(directory), VelopackContentDirectoryName, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrEmpty(parent) && fileExists(Path.Combine(parent, UpdaterFileName)))
            return parent;
        return directory;
    }

    public static RunLogLocation Resolve(
        string baseDirectory,
        string dataRoot,
        Func<string, bool>? canWrite = null,
        Func<string, bool>? fileExists = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        canWrite ??= CanWrite;
        var preferencePath = Path.Combine(Path.GetFullPath(dataRoot), PreferenceFileName);
        var custom = ReadPreference(preferencePath);
        var preferred = custom ??
            Path.Combine(ResolveSoftwareDirectory(baseDirectory, fileExists), LogsDirectoryName);
        if (canWrite(preferred))
            return new(preferred, preferred, isFallback: false, isCustom: custom is not null, preferencePath);
        var fallback = Path.Combine(Path.GetFullPath(dataRoot), LogsDirectoryName);
        return new(fallback, preferred, isFallback: true, isCustom: custom is not null, preferencePath);
    }

    /// <summary>用户选了文件夹：记下来，以后都写这里。</summary>
    public void UseCustomDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var full = Path.GetFullPath(directory);
        var parent = Path.GetDirectoryName(PreferencePath);
        if (!string.IsNullOrEmpty(parent)) System.IO.Directory.CreateDirectory(parent);
        File.WriteAllText(PreferencePath, full, new UTF8Encoding(false));
        Directory = full;
        PreferredDirectory = full;
        IsFallback = false;
        IsCustom = true;
    }

    /// <summary>真的写一个小文件再删掉：只看权限设置不可靠（只读介质、杀毒拦截、网络盘）。</summary>
    public static bool CanWrite(string directory)
    {
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".write-test-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static string? ReadPreference(string preferencePath)
    {
        try
        {
            if (!File.Exists(preferencePath)) return null;
            var text = File.ReadAllText(preferencePath).Trim();
            return text.Length > 0 && Path.IsPathFullyQualified(text) ? text : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException or System.Security.SecurityException)
        {
            return null;
        }
    }
}
