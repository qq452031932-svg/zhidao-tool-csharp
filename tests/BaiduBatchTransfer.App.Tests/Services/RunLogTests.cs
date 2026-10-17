using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BaiduBatchTransfer.App.Features.Collect;
using BaiduBatchTransfer.App.Features.Common;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.App.Tests.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BaiduBatchTransfer.App.Tests.Services;

/// <summary>
/// 落盘的运行日志：文件自动创建、逐行追加、不泄露凭据，写不进去时不连累功能本身；
/// 采集、转存、分享写到界面上的进展都要同时进文件。
/// </summary>
public sealed class RunLogTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly string directory = Path.Combine(Path.GetTempPath(), "run-log-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string LogPath => Path.Combine(directory, "logs", FileRunLog.FileName);

    private FileRunLog Create(long maximumBytes = 5 * 1024 * 1024, TimeProvider? time = null) =>
        new(Path.Combine(directory, "logs"), time, maximumBytes);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class MemoryRunLog : IRunLog
    {
        public List<string> Lines { get; } = [];
        public void Write(string source, string? message) => Lines.Add($"[{source}] {message}");
    }

    [Fact]
    public void FileIsCreatedOnFirstWriteAndLinesCarryTimeAndSource()
    {
        var log = Create(time: new FixedTime(new DateTimeOffset(2026, 10, 13, 9, 5, 7, TimeSpan.Zero)));
        Assert.False(File.Exists(LogPath), "没写过日志时不该留下空文件");

        log.Write("软件", "软件启动，版本 1.1.12。");
        log.Write("采集", "采集进行中…");

        Assert.Equal("运行日志.txt", Path.GetFileName(log.FilePath));
        Assert.Equal(
            ["2026-10-13 09:05:07 [软件] 软件启动，版本 1.1.12。", "2026-10-13 09:05:07 [采集] 采集进行中…"],
            File.ReadAllLines(LogPath));
        // 带 BOM，旧版记事本才能正确显示中文。
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, File.ReadAllBytes(LogPath).Take(3).ToArray());
    }

    [Fact]
    public void NextLaunchAppendsInsteadOfOverwriting()
    {
        Create().Write("软件", "第一次启动");
        Create().Write("软件", "第二次启动");

        var lines = File.ReadAllLines(LogPath);
        Assert.Equal(2, lines.Length);
        Assert.EndsWith("第一次启动", lines[0]);
        Assert.EndsWith("第二次启动", lines[1]);
        // 文件头只有一个 BOM，续写时不会在中间再插一个。
        Assert.DoesNotContain('﻿', string.Concat(lines));
    }

    [Fact]
    public void CredentialsLinksAndCodesNeverReachTheFile()
    {
        var log = Create();
        log.Write("采集", "请求失败 Cookie: BDUSS=abcdef123456; STOKEN=zzz999");
        log.Write("采集", "账号已失效，BDUSS=abcdef123456");
        log.Write("转存", "已保存 https://pan.baidu.com/s/1AbCdEfGh?pwd=8k2m 提取码：8k2m");

        var text = File.ReadAllText(LogPath);
        Assert.DoesNotContain("abcdef123456", text);
        Assert.DoesNotContain("zzz999", text);
        Assert.DoesNotContain("1AbCdEfGh", text);
        Assert.DoesNotContain("8k2m", text);
        // 脱敏按整段遮住凭据后面的内容，凭据之前的说明要留着。
        Assert.Contains("账号已失效", text);
    }

    [Fact]
    public void BlankMessagesAreIgnoredAndImmediateRepeatsAreWrittenOnce()
    {
        var log = Create();
        log.Write("采集", null);
        log.Write("采集", "   ");
        log.Write("采集", "正在停止…");
        log.Write("采集", "正在停止…");
        log.Write("转存", "正在停止…");
        log.Write("采集", "已停止，共采到 3 条。");
        log.Write("采集", "正在停止…");

        Assert.Equal(
            ["[采集] 正在停止…", "[转存] 正在停止…", "[采集] 已停止，共采到 3 条。", "[采集] 正在停止…"],
            File.ReadAllLines(LogPath).Select(line => line[20..]));
    }

    [Fact]
    public void MultiLineMessagesStayAttachedToTheirEntry()
    {
        Create().Write("软件", "启动失败：System.IO.IOException: 磁盘已满\n   at Foo.Bar()");

        var lines = File.ReadAllLines(LogPath);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("    ", lines[1]);
        Assert.Contains("at Foo.Bar()", lines[1]);
    }

    [Fact]
    public void OversizedFileIsMovedAsideSoTheLogCannotGrowWithoutBound()
    {
        var log = Create(maximumBytes: 200);
        for (var index = 0; index < 20; index++) log.Write("采集", $"已采到第 {index} 条：某个资源名称");

        var previous = Path.Combine(directory, "logs", FileRunLog.PreviousFileName);
        Assert.True(File.Exists(previous));
        Assert.True(new FileInfo(LogPath).Length < 400);
        Assert.EndsWith("已采到第 19 条：某个资源名称", File.ReadAllLines(LogPath).Last());
        Assert.Equal(2, Directory.GetFiles(Path.Combine(directory, "logs")).Length);
    }

    [Fact]
    public void LogStaysWritableWhileOpenInAnotherProgram()
    {
        var log = Create();
        log.Write("软件", "第一行");
        using (new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            log.Write("软件", "第二行");

        Assert.Equal(2, File.ReadAllLines(LogPath).Length);
    }

    [Fact]
    public void FailureToWriteNeverThrowsAndLaterWritesRecover()
    {
        var log = Create();
        log.Write("软件", "第一行");
        using (new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.None))
            log.Write("软件", "写不进去的一行");
        log.Write("软件", "写不进去的一行");

        // 上一次没写成，所以这句不算重复，恢复后要补上。
        Assert.Equal(2, File.ReadAllLines(LogPath).Length);
    }

    [Fact]
    public void LogDirectoryBlockedByAFileDoesNotThrow()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "logs"), "占位");

        Create().Write("软件", "软件启动");
    }

    [Fact]
    public void WorkspaceLogEntriesAreMirroredWithTheirSeverityAndSurviveClearingTheScreen()
    {
        var runLog = new MemoryRunLog();
        var buffer = new WorkspaceLogBuffer(appended: runLog.ForWorkspace("转存"));

        buffer.AppendSafe("正在准备转存任务……");
        buffer.AppendSafe("转存任务已完成：完成 3 条，失败 1 条。", WorkspaceLogLevel.Error);
        buffer.AppendSafe("链接 https://pan.baidu.com/s/1AbCdEfGh 已加入");
        buffer.AppendSafe("请先导入链接，再选择账号和保存目录。");
        buffer.Clear();

        Assert.Empty(buffer.Entries);
        Assert.Equal(4, runLog.Lines.Count);
        // 带「请」字的普通提示在界面上算警告，文件里不加标记。
        Assert.Equal("[转存] 请先导入链接，再选择账号和保存目录。", runLog.Lines[3]);
        Assert.Equal("[转存] 正在准备转存任务……", runLog.Lines[0]);
        Assert.Equal("[转存] 【错误】转存任务已完成：完成 3 条，失败 1 条。", runLog.Lines[1]);
        Assert.DoesNotContain("1AbCdEfGh", runLog.Lines[2]);
        Assert.Null(((IRunLog?)null).ForWorkspace("转存"));
    }

    [Fact]
    public async Task CollectRunIsRecordedFromStartToFinishWithoutTheCookie()
    {
        var process = new FakeCollectHelperProcess();
        var client = CollectHelperClient.Create(process);
        process.Emit(new { ev = "ready" });
        await client.WaitUntilReadyAsync(Timeout, CancellationToken.None);
        var log = Create();
        var model = new CollectViewModel(new FakeCollectHelperSession(client), action => action(), runLog: log);

        model.KeywordText = "云边有个小卖部\n长安的荔枝";
        model.CookieText = "BDUSS=topsecretcookie";
        await model.StartCommand.ExecuteAsync(null);
        process.Emit(new { ev = "item", name = "云边有个小卖部", link = "https://pan.baidu.com/s/1AbCdEfGh", pwd = "8k2m", source = "q", restored = false });
        process.Emit(new { ev = "done", found = 1, stopped = false, paused = false, failed = false, stats = new { found = 1 } });
        await WaitUntil(() => !model.IsCollecting);

        var lines = File.ReadAllLines(LogPath).Select(line => line[20..]).ToArray();
        Assert.Contains("[采集] 开始采集：精准模式，关键词 2 个，目标 2 条。", lines);
        // 「采集进行中…」只显示在状态栏，界面日志里没有，文件里要有。
        Assert.Contains("[采集] 采集进行中…", lines);
        Assert.Contains("[采集] 已采到第 1 条：云边有个小卖部", lines);
        Assert.Contains("[采集] 采集完成，共采到 1 条。", lines);
        Assert.Equal(lines.Length, lines.Distinct().Count());
        var text = string.Join("\n", lines);
        Assert.DoesNotContain("topsecretcookie", text);
        Assert.DoesNotContain("1AbCdEfGh", text);
        Assert.Contains(model.Logs, entry => entry.Contains("开始采集：精准模式", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CollectStatusThatNeverReachesTheOnScreenLogIsStillRecorded()
    {
        var process = new FakeCollectHelperProcess();
        var client = CollectHelperClient.Create(process);
        process.Emit(new { ev = "ready" });
        await client.WaitUntilReadyAsync(Timeout, CancellationToken.None);
        var log = Create();
        var model = new CollectViewModel(new FakeCollectHelperSession(client), action => action(), runLog: log);

        model.KeywordText = "甲";
        await model.StartCommand.ExecuteAsync(null);

        Assert.Empty(model.Logs);
        Assert.Contains("[采集] 请先填入知道账号的 Cookie。", File.ReadAllLines(LogPath).Select(line => line[20..]));
    }

    [Fact]
    public void HostWritesTheRunLogUnderTheSoftwareDirectory()
    {
        // 用户 10-16：日志放在软件目录下（原来在数据目录，路径太长找不到）；写不进去才退回数据目录。
        using var host = AppBootstrap.CreateHost(directory);
        var log = Assert.IsType<FileRunLog>(host.Services.GetRequiredService<IRunLog>());
        var location = host.Services.GetRequiredService<RunLogLocation>();

        var expected = location.IsFallback
            ? Path.Combine(directory, "logs")
            : Path.Combine(RunLogLocation.ResolveSoftwareDirectory(AppContext.BaseDirectory), "logs");
        Assert.Equal(Path.Combine(expected, FileRunLog.FileName), log.FilePath);
        Assert.Same(log, host.Services.GetRequiredService<FileRunLog>());
        if (!location.IsFallback)
            Assert.False(Directory.Exists(Path.Combine(directory, "logs")), "能写软件目录时不该再往数据目录写");
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }

        Assert.Fail("等待条件超时");
    }
}
