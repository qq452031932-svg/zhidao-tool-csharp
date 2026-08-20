using System.IO;

namespace BaiduBatchTransfer.App;

public static class StartupDataRootSelector
{
    public const string AcceptanceMarkerFileName = ".acceptance-marker";
    public const string AcceptanceMarkerHeader =
        "BaiduBatchTransfer account-management acceptance v1";

    public static string Resolve(IReadOnlyList<string> args, string normalRoot)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentException.ThrowIfNullOrWhiteSpace(normalRoot);
        var fullNormalRoot = Path.GetFullPath(normalRoot);

#if DEBUG
        var indexes = args
            .Select((value, index) => (value, index))
            .Where(item => string.Equals(item.value, "--data-root", StringComparison.Ordinal))
            .Select(item => item.index)
            .ToArray();
        if (indexes.Length == 0) return fullNormalRoot;
        if (indexes.Length != 1 || indexes[0] + 1 >= args.Count)
            throw new InvalidOperationException("验收数据目录参数无效。");

        var requested = args[indexes[0] + 1];
        if (string.IsNullOrWhiteSpace(requested) || !Path.IsPathFullyQualified(requested))
            throw new InvalidOperationException("验收数据目录必须是绝对路径。");

        var fullRequested = Path.GetFullPath(requested);
        var rootInfo = new DirectoryInfo(fullRequested);
        if (!rootInfo.Exists || rootInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("验收数据目录不存在或不安全。");

        var markerPath = Path.Combine(fullRequested, AcceptanceMarkerFileName);
        var markerInfo = new FileInfo(markerPath);
        if (!markerInfo.Exists || markerInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("验收数据目录缺少安全标记。");

        var lines = File.ReadAllLines(markerPath);
        if (lines.Length != 2 ||
            !string.Equals(lines[0], AcceptanceMarkerHeader, StringComparison.Ordinal) ||
            !lines[1].StartsWith("scenario=", StringComparison.Ordinal) ||
            !IsSupportedScenario(lines[1]["scenario=".Length..]))
        {
            throw new InvalidOperationException("验收数据目录标记无效。");
        }

        return fullRequested;
#else
        return fullNormalRoot;
#endif
    }

#if DEBUG
    private static bool IsSupportedScenario(string value) =>
        value is "keep-history" or "cleanup-history" or "active-work";
#endif
}
