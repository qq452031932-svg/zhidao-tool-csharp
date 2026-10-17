using System;
using System.IO;
using BaiduBatchTransfer.App.Services;
using Xunit;

namespace BaiduBatchTransfer.App.Tests.Services;

/// <summary>
/// 用户 10-16：运行日志放到软件目录下，打开软件所在文件夹就能找到；写不进去时提醒用户自己选文件夹。
/// </summary>
public sealed class RunLogLocationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "run-log-location-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void AnInstalledCopyLogsBesideUpdateExeSoUpdatesDoNotWipeTheLogs()
    {
        // 安装版每次更新会整个换掉 current，日志要放在它的上一级。
        var install = Path.Combine(root, "知道综合工具");
        var current = Path.Combine(install, "current");

        Assert.Equal(install, RunLogLocation.ResolveSoftwareDirectory(current + Path.DirectorySeparatorChar,
            path => path == Path.Combine(install, "Update.exe")));
        // 没有 Update.exe 的 current 只是个普通文件夹名。
        Assert.Equal(current, RunLogLocation.ResolveSoftwareDirectory(current, _ => false));
        var portable = Path.Combine(root, "便携版");
        Assert.Equal(portable, RunLogLocation.ResolveSoftwareDirectory(portable, _ => true));
    }

    [Fact]
    public void LogsGoToTheSoftwareFolderWhenItIsWritable()
    {
        var software = Path.Combine(root, "软件");
        var data = Path.Combine(root, "数据");

        var location = RunLogLocation.Resolve(software, data, fileExists: _ => false);

        Assert.Equal(Path.Combine(software, "logs"), location.Directory);
        Assert.False(location.IsFallback);
        Assert.False(location.IsCustom);
        Assert.True(Directory.Exists(location.Directory));
        Assert.Empty(Directory.GetFiles(location.Directory));
    }

    [Fact]
    public void AnUnwritableSoftwareFolderFallsBackToTheOldPlaceAndAsksTheUser()
    {
        var software = Path.Combine(root, "软件");
        var data = Path.Combine(root, "数据");
        var blocked = Path.Combine(software, "logs");

        var location = RunLogLocation.Resolve(software, data, canWrite: path => path != blocked, fileExists: _ => false);

        Assert.True(location.IsFallback);
        Assert.Equal(blocked, location.PreferredDirectory);
        Assert.Equal(Path.Combine(data, "logs"), location.Directory);
    }

    [Fact]
    public void AChosenFolderIsRememberedAndUsedNextTime()
    {
        var software = Path.Combine(root, "软件");
        var data = Path.Combine(root, "数据");
        var chosen = Path.Combine(root, "我选的日志");
        var first = RunLogLocation.Resolve(software, data, canWrite: _ => false, fileExists: _ => false);

        first.UseCustomDirectory(chosen);

        Assert.Equal(chosen, first.Directory);
        Assert.False(first.IsFallback);
        var next = RunLogLocation.Resolve(software, data, fileExists: _ => false);
        Assert.Equal(chosen, next.Directory);
        Assert.True(next.IsCustom);
        Assert.False(next.IsFallback);

        // 选过的文件夹后来写不进去了（例如 U 盘拔了）：同样退回原位置并再次提醒。
        var later = RunLogLocation.Resolve(software, data, canWrite: path => path != chosen, fileExists: _ => false);
        Assert.True(later.IsFallback);
        Assert.True(later.IsCustom);
        Assert.Equal(chosen, later.PreferredDirectory);
    }

    [Fact]
    public void AGarbledPreferenceIsIgnored()
    {
        var data = Path.Combine(root, "数据");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, RunLogLocation.PreferenceFileName), "relative\\path");

        var location = RunLogLocation.Resolve(Path.Combine(root, "软件"), data, fileExists: _ => false);

        Assert.False(location.IsCustom);
        Assert.Equal(Path.Combine(root, "软件", "logs"), location.Directory);
    }

    [Fact]
    public void AFolderBlockedByAFileIsNotWritable()
    {
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "logs");
        File.WriteAllText(file, "x");

        Assert.False(RunLogLocation.CanWrite(file));
        Assert.True(RunLogLocation.CanWrite(Path.Combine(root, "ok")));
    }

    [Fact]
    public void MovingTheLogSendsLaterLinesToTheNewFolder()
    {
        var log = new FileRunLog(Path.Combine(root, "old"));
        log.Write("软件", "第一行");
        var target = Path.Combine(root, "new");

        log.MoveTo(target);
        log.Write("软件", "第一行");

        Assert.Equal(Path.Combine(target, FileRunLog.FileName), log.FilePath);
        Assert.Contains("第一行", File.ReadAllText(Path.Combine(target, FileRunLog.FileName)));
        Assert.Single(File.ReadAllLines(Path.Combine(root, "old", FileRunLog.FileName)));
    }
}
