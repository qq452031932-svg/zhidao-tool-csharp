using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace BaiduBatchTransfer.App.Features.Collect;

/// <summary>采集助手的落地位置：解释器与主脚本。</summary>
public sealed record CollectHelperLayout(string InterpreterPath, string MainScriptPath)
{
    /// <summary>两者都在才算可用。</summary>
    public bool IsComplete => File.Exists(InterpreterPath) && File.Exists(MainScriptPath);
}

/// <summary>
/// 按约定找出采集助手的位置。
///
/// 已安装的程序里，解释器沿用浏览器助手那一份 CPython，采集助手只多出几个 .py，
/// 因此安装包不会为此再背一整个运行时。开发树上则回退到仓库内的源码位置。
/// </summary>
public static class CollectHelperLocator
{
    internal const string InstalledInterpreterDirectory = "managed-browser-helper";
    internal const string InstalledScriptDirectory = "collect-helper";
    internal const string RepositoryScriptDirectory = "tools/collect_helper";

    /// <summary>先按安装布局找，找不到再按开发树找；都找不到则返回安装布局供报错使用。</summary>
    public static CollectHelperLayout Locate(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        var installed = new CollectHelperLayout(
            Path.Combine(baseDirectory, InstalledInterpreterDirectory, "python.exe"),
            Path.Combine(baseDirectory, InstalledScriptDirectory, "main.py"));
        if (installed.IsComplete) return installed;

        var development = LocateInRepository(baseDirectory);
        return development?.IsComplete == true ? development : installed;
    }

    private static CollectHelperLayout? LocateInRepository(string baseDirectory)
    {
        var directory = new DirectoryInfo(baseDirectory);
        while (directory is not null)
        {
            if (directory.GetFiles("BaiduBatchTransfer.sln").Length == 0)
            {
                directory = directory.Parent;
                continue;
            }

            var script = Path.Combine(directory.FullName,
                RepositoryScriptDirectory.Replace('/', Path.DirectorySeparatorChar), "main.py");
            var interpreter = FindBundledInterpreter(directory.FullName);
            return interpreter is null ? null : new CollectHelperLayout(interpreter, script);
        }

        return null;
    }

    /// <summary>开发树上解释器来自构建产物目录，取最新的一份。</summary>
    private static string? FindBundledInterpreter(string repositoryRoot)
    {
        var artifacts = Path.Combine(repositoryRoot, ".artifacts");
        if (!Directory.Exists(artifacts)) return null;

        string? newest = null;
        var newestWriteTime = DateTime.MinValue;
        foreach (var candidate in Directory.EnumerateFiles(artifacts, "python.exe", SearchOption.AllDirectories))
        {
            var writeTime = File.GetLastWriteTimeUtc(candidate);
            if (writeTime <= newestWriteTime) continue;
            newest = candidate;
            newestWriteTime = writeTime;
        }

        return newest;
    }
}

/// <summary>按需启动一个采集助手，并在页面存活期间复用它。</summary>
public sealed class CollectHelperSession : ICollectHelperSession
{
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(30);

    private readonly Func<CollectHelperLayout> resolveLayout;
    private readonly SemaphoreSlim gate = new(1, 1);
    private CollectHelperClient? client;
    private bool disposed;

    public CollectHelperSession(Func<CollectHelperLayout>? resolveLayout = null) =>
        this.resolveLayout = resolveLayout ?? (() => CollectHelperLocator.Locate(AppContext.BaseDirectory));

    public async Task<CollectHelperClient> ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (client is not null) return client;

            var layout = resolveLayout();
            if (!File.Exists(layout.InterpreterPath))
                throw new FileNotFoundException(
                    "未找到采集助手所需的 Python 运行时，请先完成安装或生成助手包。", layout.InterpreterPath);
            if (!File.Exists(layout.MainScriptPath))
                throw new FileNotFoundException(
                    "未找到采集助手主程序。", layout.MainScriptPath);

            client = await CollectHelperClient.StartAsync(
                layout.InterpreterPath, layout.MainScriptPath, ReadyTimeout, cancellationToken)
                .ConfigureAwait(false);
            return client;
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        var owned = client;
        client = null;
        if (owned is not null) await owned.DisposeAsync().ConfigureAwait(false);
        gate.Dispose();
    }
}
