using System;
using System.IO;
using BaiduBatchTransfer.App.Features.Collect;
using Xunit;

namespace BaiduBatchTransfer.App.Tests.Features;

/// <summary>
/// 采集助手的定位约定：装好的程序按安装布局找，开发树上回退到仓库源码。
/// 这层出错的症状是「界面点了没反应」，所以约定必须钉死。
/// </summary>
public sealed class CollectHelperLocatorTests : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), "collect-locator-" + Guid.NewGuid().ToString("N"));

    public CollectHelperLocatorTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不应影响测试结果
        }
    }

    private string Touch(params string[] segments)
    {
        var path = Path.Combine([root, .. segments]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    [Fact]
    public void InstalledLayoutReusesTheBrowserHelperInterpreter()
    {
        var interpreter = Touch("app", CollectHelperLocator.InstalledInterpreterDirectory, "python.exe");
        var script = Touch("app", CollectHelperLocator.InstalledScriptDirectory, "main.py");

        var layout = CollectHelperLocator.Locate(Path.Combine(root, "app"));

        Assert.True(layout.IsComplete);
        Assert.Equal(interpreter, layout.InterpreterPath);
        Assert.Equal(script, layout.MainScriptPath);
    }

    [Fact]
    public void DevelopmentTreeFallsBackToRepositorySourcesAndBuiltInterpreter()
    {
        Touch("repo", "BaiduBatchTransfer.sln");
        var script = Touch("repo", "tools", "collect_helper", "main.py");
        var interpreter = Touch("repo", ".artifacts", "helper-bundle", "python.exe");
        var output = Path.Combine(root, "repo", "src", "App", "bin", "Debug");
        Directory.CreateDirectory(output);

        var layout = CollectHelperLocator.Locate(output);

        Assert.True(layout.IsComplete);
        Assert.Equal(script, layout.MainScriptPath);
        Assert.Equal(interpreter, layout.InterpreterPath);
    }

    [Fact]
    public void InstalledLayoutWinsOverTheRepositoryWhenBothExist()
    {
        Touch("repo", "BaiduBatchTransfer.sln");
        Touch("repo", "tools", "collect_helper", "main.py");
        Touch("repo", ".artifacts", "helper-bundle", "python.exe");
        var output = Path.Combine(root, "repo", "src", "App", "bin", "Debug");
        var installedInterpreter = Touch("repo", "src", "App", "bin", "Debug",
            CollectHelperLocator.InstalledInterpreterDirectory, "python.exe");
        var installedScript = Touch("repo", "src", "App", "bin", "Debug",
            CollectHelperLocator.InstalledScriptDirectory, "main.py");

        var layout = CollectHelperLocator.Locate(output);

        Assert.Equal(installedInterpreter, layout.InterpreterPath);
        Assert.Equal(installedScript, layout.MainScriptPath);
    }

    [Fact]
    public void NewestBuiltInterpreterWinsWhenSeveralBundlesExist()
    {
        Touch("repo", "BaiduBatchTransfer.sln");
        Touch("repo", "tools", "collect_helper", "main.py");
        var older = Touch("repo", ".artifacts", "bundle-old", "python.exe");
        var newer = Touch("repo", ".artifacts", "bundle-new", "python.exe");
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddDays(-3));
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow);
        var output = Path.Combine(root, "repo", "src", "App", "bin", "Debug");
        Directory.CreateDirectory(output);

        Assert.Equal(newer, CollectHelperLocator.Locate(output).InterpreterPath);
    }

    [Fact]
    public void MissingEverythingReportsTheInstalledLayoutForDiagnostics()
    {
        var output = Path.Combine(root, "nowhere");
        Directory.CreateDirectory(output);

        var layout = CollectHelperLocator.Locate(output);

        Assert.False(layout.IsComplete);
        // 报错信息应指向安装位置，而不是开发树，否则用户看不懂
        Assert.Contains(CollectHelperLocator.InstalledInterpreterDirectory, layout.InterpreterPath);
        Assert.Contains(CollectHelperLocator.InstalledScriptDirectory, layout.MainScriptPath);
    }
}
