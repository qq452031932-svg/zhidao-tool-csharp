using System.Diagnostics;

namespace BaiduBatchTransfer.LegacyLauncher;

public enum LegacyPathState
{
    Missing,
    RegularFile,
    Directory,
    ReparsePoint,
}

public sealed record LegacyPathInspection(
    LegacyPathState State,
    string? FinalPath,
    uint LinkCount,
    bool HasReparsePoint);

public sealed class LegacyLaunchPlan
{
    private const string VelopackArgumentPrefix = "--veloapp-";
    private readonly string[] _arguments;

    private LegacyLaunchPlan(
        string targetPath,
        IReadOnlyList<string> arguments)
    {
        TargetPath = targetPath;
        _arguments = arguments.ToArray();
        WaitForExit = _arguments.Any(argument =>
            argument.StartsWith(VelopackArgumentPrefix, StringComparison.OrdinalIgnoreCase));
    }

    public string TargetPath { get; }

    public IReadOnlyList<string> Arguments => Array.AsReadOnly(_arguments);

    public bool WaitForExit { get; }

    public static bool TryCreate(
        string? launcherProcessPath,
        IReadOnlyList<string> arguments,
        Func<string, LegacyPathInspection> inspectPath,
        out LegacyLaunchPlan? plan,
        out string error) =>
        TryCreateForTargetLeaf(
            launcherProcessPath,
            ProductIdentity.MainExecutableName,
            arguments,
            inspectPath,
            out plan,
            out error);

    internal static bool TryCreateForTargetLeaf(
        string? launcherProcessPath,
        string? targetExecutableLeaf,
        IReadOnlyList<string> arguments,
        Func<string, LegacyPathInspection> inspectPath,
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

            if (string.IsNullOrWhiteSpace(targetExecutableLeaf) ||
                Path.IsPathRooted(targetExecutableLeaf) ||
                targetExecutableLeaf.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                !string.Equals(
                    targetExecutableLeaf,
                    Path.GetFileName(targetExecutableLeaf),
                    StringComparison.Ordinal))
            {
                error = "新版程序名称不是安全的文件名。";
                return false;
            }

            launcherDirectory = Path.GetFullPath(launcherDirectory);
            var targetPath = Path.GetFullPath(
                Path.Combine(launcherDirectory, targetExecutableLeaf));

            if (!string.Equals(
                    launcherDirectory,
                    Path.GetDirectoryName(targetPath),
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    targetExecutableLeaf,
                    Path.GetFileName(targetPath),
                    StringComparison.Ordinal))
            {
                error = "新版程序路径不安全。";
                return false;
            }

            var launcherInspection = inspectPath(launcherPath);
            if (launcherInspection.State != LegacyPathState.RegularFile ||
                launcherInspection.HasReparsePoint ||
                string.IsNullOrWhiteSpace(launcherInspection.FinalPath) ||
                !Path.IsPathFullyQualified(launcherInspection.FinalPath))
            {
                error = "旧版启动器路径不安全。";
                return false;
            }

            var targetInspection = inspectPath(targetPath);
            if (targetInspection.State != LegacyPathState.RegularFile ||
                targetInspection.HasReparsePoint ||
                targetInspection.LinkCount != 1 ||
                string.IsNullOrWhiteSpace(targetInspection.FinalPath) ||
                !Path.IsPathFullyQualified(targetInspection.FinalPath))
            {
                error = $"未找到安全的新版程序：{targetExecutableLeaf}";
                return false;
            }

            var finalLauncherPath = Path.GetFullPath(launcherInspection.FinalPath);
            var finalTargetPath = Path.GetFullPath(targetInspection.FinalPath);
            var finalLauncherDirectory = Path.GetDirectoryName(finalLauncherPath);
            var finalTargetDirectory = Path.GetDirectoryName(finalTargetPath);
            if (string.IsNullOrEmpty(finalLauncherDirectory) ||
                string.IsNullOrEmpty(finalTargetDirectory) ||
                !string.Equals(
                    finalLauncherDirectory,
                    finalTargetDirectory,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    targetExecutableLeaf,
                    Path.GetFileName(finalTargetPath),
                    StringComparison.Ordinal))
            {
                error = "新版程序解析后的路径不安全。";
                return false;
            }

            plan = new LegacyLaunchPlan(targetPath, arguments);
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
