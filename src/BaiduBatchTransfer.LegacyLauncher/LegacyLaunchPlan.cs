using System.Diagnostics;

namespace BaiduBatchTransfer.LegacyLauncher;

public enum LegacyPathState
{
    Missing,
    RegularFile,
    Directory,
    ReparsePoint,
}

public sealed class LegacyLaunchPlan
{
    private const string VelopackArgumentPrefix = "--veloapp-";
    private readonly string[] _arguments;

    private LegacyLaunchPlan(
        string targetPath,
        string workingDirectory,
        IReadOnlyList<string> arguments)
    {
        TargetPath = targetPath;
        WorkingDirectory = workingDirectory;
        _arguments = arguments.ToArray();
        WaitForExit = _arguments.Any(argument =>
            argument.StartsWith(VelopackArgumentPrefix, StringComparison.OrdinalIgnoreCase));
    }

    public string TargetPath { get; }

    public string WorkingDirectory { get; }

    public IReadOnlyList<string> Arguments => Array.AsReadOnly(_arguments);

    public bool WaitForExit { get; }

    public static bool TryCreate(
        string? launcherProcessPath,
        IReadOnlyList<string> arguments,
        Func<string, LegacyPathState> inspectPath,
        out LegacyLaunchPlan? plan,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(inspectPath);

        plan = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(launcherProcessPath) ||
            !Path.IsPathFullyQualified(launcherProcessPath))
        {
            error = "无法确定旧版启动器的安全位置。";
            return false;
        }

        try
        {
            var launcherPath = Path.GetFullPath(launcherProcessPath);
            var launcherDirectory = Path.GetDirectoryName(launcherPath);
            if (string.IsNullOrEmpty(launcherDirectory))
            {
                error = "无法确定旧版启动器所在目录。";
                return false;
            }

            launcherDirectory = Path.GetFullPath(launcherDirectory);
            var targetPath = Path.GetFullPath(
                Path.Combine(launcherDirectory, ProductIdentity.MainExecutableName));

            if (!string.Equals(
                    launcherDirectory,
                    Path.GetDirectoryName(targetPath),
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    ProductIdentity.MainExecutableName,
                    Path.GetFileName(targetPath),
                    StringComparison.Ordinal))
            {
                error = "新版程序路径不安全。";
                return false;
            }

            if (inspectPath(launcherPath) != LegacyPathState.RegularFile)
            {
                error = "旧版启动器路径不安全。";
                return false;
            }

            if (inspectPath(targetPath) != LegacyPathState.RegularFile)
            {
                error = $"未找到安全的新版程序：{ProductIdentity.MainExecutableName}";
                return false;
            }

            plan = new LegacyLaunchPlan(targetPath, launcherDirectory, arguments);
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = "无法验证新版程序路径。";
            return false;
        }
    }

    public ProcessStartInfo CreateStartInfo()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = TargetPath,
            WorkingDirectory = WorkingDirectory,
            UseShellExecute = false,
        };

        foreach (var argument in _arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    public int GetSuccessfulExitCode(int childExitCode) => WaitForExit ? childExitCode : 0;
}
