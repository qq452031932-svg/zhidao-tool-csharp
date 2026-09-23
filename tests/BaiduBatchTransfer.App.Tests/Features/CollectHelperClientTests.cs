using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BaiduBatchTransfer.App.Features.Collect;
using Xunit;

namespace BaiduBatchTransfer.App.Tests.Features;

/// <summary>
/// 采集助手客户端的契约测试：用脚本化的假进程驱动，不需要 Python、不联网、不用账号。
/// 这里只验证信道行为，采集语义由助手侧自己的测试覆盖。
/// </summary>
public sealed class CollectHelperClientTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void StartInfoIsolatesTheInterpreterAndPinsUtf8()
    {
        var info = CollectHelperClient.CreateStartInfo(@"C:\bundle\python.exe", @"C:\bundle\collect\main.py");

        Assert.Equal(@"C:\bundle\python.exe", info.FileName);
        Assert.Equal(@"C:\bundle\collect", info.WorkingDirectory);
        Assert.False(info.UseShellExecute);
        Assert.True(info.CreateNoWindow);
        Assert.True(info.RedirectStandardInput);
        Assert.True(info.RedirectStandardOutput);
        Assert.True(info.RedirectStandardError);
        Assert.Equal(new[] { "-B", "-E", "-s", @"C:\bundle\collect\main.py" }, info.ArgumentList);
        // 信道固定 UTF-8，否则中文资源名会被控制台代码页破坏
        Assert.Equal("utf-8", info.StandardOutputEncoding!.WebName);
        Assert.Equal("utf-8", info.StandardInputEncoding!.WebName);
    }

    [Fact]
    public async Task ReadyGatesStartupAndTimesOutWhenTheHelperIsSilent()
    {
        using var process = new FakeCollectHelperProcess();
        await using var client = CollectHelperClient.Create(process);

        var pending = client.WaitUntilReadyAsync(TimeSpan.FromMilliseconds(150), CancellationToken.None);
        await Assert.ThrowsAsync<TimeoutException>(() => pending);

        process.Emit(new { ev = "ready", protocolVersion = 1 });
        await client.WaitUntilReadyAsync(Timeout, CancellationToken.None);
    }

    [Fact]
    public async Task EventsAreTranslatedIntoTypedCallbacks()
    {
        using var process = new FakeCollectHelperProcess();
        await using var client = CollectHelperClient.Create(process);

        var resources = new List<CollectedResource>();
        CollectProgress? progress = null;
        string? status = null;
        string? log = null;
        CollectRiskPrompt? risk = null;
        CollectOutcome? outcome = null;
        string? failure = null;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        client.ResourceCollected += resources.Add;
        client.ProgressChanged += value => progress = value;
        client.StatusChanged += value => status = value;
        client.LogWritten += value => log = value;
        client.RiskEncountered += value => risk = value;
        client.Failed += value => failure = value;
        client.Completed += value => { outcome = value; finished.TrySetResult(); };

        process.Emit(new { ev = "ready", protocolVersion = 1 });
        await client.WaitUntilReadyAsync(Timeout, CancellationToken.None);

        process.Emit(new { ev = "started", mode = "precise", keywords = 2, target = 2 });
        process.Emit(new
        {
            ev = "item",
            name = "云边有个小卖部",
            link = "https://pan.baidu.com/s/1AbCdE?pwd=8k2m",
            pwd = "8k2m",
            source = "https://zhidao.baidu.com/question/11.html",
        });
        process.Emit(new { ev = "progress", found = 1, target = 2 });
        process.Emit(new { ev = "status", text = "时间范围自动放宽到「一年」继续采集…" });
        process.Emit(new { ev = "log", text = "跳过重复链接：x" });
        process.Emit(new { ev = "ask", reason = "采集账号受限" });
        process.Emit(new { ev = "error", message = "第 3 个关键词失败" });
        process.Emit(new { ev = "done", found = 1, stopped = false, stats = new { found = 1, no_pwd = 0 } });

        await finished.Task.WaitAsync(Timeout);

        var resource = Assert.Single(resources);
        Assert.Equal("云边有个小卖部", resource.Name);          // 中文往返完好
        Assert.Equal("https://pan.baidu.com/s/1AbCdE?pwd=8k2m", resource.Link);
        Assert.Equal("8k2m", resource.Pwd);
        Assert.Equal("https://zhidao.baidu.com/question/11.html", resource.Source);

        Assert.Equal(new CollectProgress(1, 2), progress);
        Assert.Equal("时间范围自动放宽到「一年」继续采集…", status);
        Assert.Equal("跳过重复链接：x", log);
        Assert.Equal("采集账号受限", risk!.Reason);
        Assert.Equal("第 3 个关键词失败", failure);

        Assert.Equal(1, outcome!.Found);
        Assert.False(outcome.Stopped);
        Assert.Equal(1, outcome.Stats["found"]);
        Assert.Equal(0, outcome.Stats["no_pwd"]);
    }

    [Fact]
    public async Task MalformedOutputIsReportedButNeverBreaksTheChannel()
    {
        using var process = new FakeCollectHelperProcess();
        await using var client = CollectHelperClient.Create(process);

        var logs = new List<string>();
        var resources = new List<CollectedResource>();
        client.LogWritten += logs.Add;
        client.ResourceCollected += resources.Add;

        process.Emit(new { ev = "ready" });
        await client.WaitUntilReadyAsync(Timeout, CancellationToken.None);

        process.EmitRaw("这一行不是 JSON");
        process.EmitRaw("{\"没有事件名\":1}");
        process.EmitRaw("{\"ev\":\"从未定义的事件\"}");
        process.Emit(new { ev = "item", name = "长安的荔枝", link = "https://pan.baidu.com/s/1Zz", pwd = "2p9q", source = "s" });

        await WaitUntil(() => resources.Count == 1);
        Assert.Equal("长安的荔枝", resources[0].Name);   // 坏帧之后信道仍然可用
        Assert.Equal(3, logs.Count);
    }

    [Fact]
    public async Task CommandsAreSerializedAsOneJsonLineEach()
    {
        using var process = new FakeCollectHelperProcess();
        await using var client = CollectHelperClient.Create(process);
        process.Emit(new { ev = "ready" });
        await client.WaitUntilReadyAsync(Timeout, CancellationToken.None);

        await client.StartCollectingAsync(new CollectRequest(
            "precise",
            new[] { "云边有个小卖部", "长安的荔枝" },
            new[] { new CollectAccount("知道甲", "BDUSS=secret", "direct") },
            Count: 20), CancellationToken.None);
        await client.StopCollectingAsync(CancellationToken.None);
        await client.AnswerRiskAsync(shouldContinue: true, CancellationToken.None);

        var commands = process.WrittenCommands();
        Assert.Equal(3, commands.Count);

        var start = JsonDocument.Parse(commands[0]).RootElement;
        Assert.Equal("start", start.GetProperty("cmd").GetString());
        Assert.Equal("precise", start.GetProperty("mode").GetString());
        Assert.Equal(20, start.GetProperty("count").GetInt32());
        Assert.Equal(
            new[] { "云边有个小卖部", "长安的荔枝" },
            start.GetProperty("keywords").EnumerateArray().Select(value => value.GetString()).ToArray());
        var account = Assert.Single(start.GetProperty("accounts").EnumerateArray().ToArray());
        Assert.Equal("知道甲", account.GetProperty("name").GetString());
        Assert.Equal("BDUSS=secret", account.GetProperty("cookie").GetString());
        Assert.Equal("direct", account.GetProperty("exit_kind").GetString());

        Assert.Equal("stop", JsonDocument.Parse(commands[1]).RootElement.GetProperty("cmd").GetString());
        var resume = JsonDocument.Parse(commands[2]).RootElement;
        Assert.Equal("resume", resume.GetProperty("cmd").GetString());
        Assert.True(resume.GetProperty("continue").GetBoolean());
    }

    [Fact]
    public async Task AccountWithoutExitKindOmitsTheField()
    {
        using var process = new FakeCollectHelperProcess();
        await using var client = CollectHelperClient.Create(process);
        process.Emit(new { ev = "ready" });
        await client.WaitUntilReadyAsync(Timeout, CancellationToken.None);

        await client.StartCollectingAsync(new CollectRequest(
            "fuzzy", new[] { "小说" }, new[] { new CollectAccount("知道甲", "BDUSS=x") }, Count: 5),
            CancellationToken.None);

        var start = JsonDocument.Parse(process.WrittenCommands()[0]).RootElement;
        var account = start.GetProperty("accounts").EnumerateArray().First();
        Assert.False(account.TryGetProperty("exit_kind", out _));
    }

    [Fact]
    public async Task HelperExitIsSurfacedAsABrokenChannel()
    {
        using var process = new FakeCollectHelperProcess();
        await using var client = CollectHelperClient.Create(process);

        string? broken = null;
        client.ChannelBroken += value => broken = value;

        process.Emit(new { ev = "ready" });
        await client.WaitUntilReadyAsync(Timeout, CancellationToken.None);

        process.CompleteOutput();

        await WaitUntil(() => broken is not null);
        Assert.Contains("退出", broken!);
    }

    [Fact]
    public async Task DisposeClosesStandardInputSoTheHelperCanFinishOnItsOwn()
    {
        var process = new FakeCollectHelperProcess();
        var client = CollectHelperClient.Create(process);
        process.Emit(new { ev = "ready" });
        await client.WaitUntilReadyAsync(Timeout, CancellationToken.None);

        await client.DisposeAsync();

        Assert.True(process.StandardInputClosed);
        Assert.False(process.WasKilled);   // 助手正常退出时不应被强杀
        // 关闭前应先请求停止，让正在跑的采集尽快收尾
        Assert.Contains("stop", process.WrittenCommands().Select(
            command => JsonDocument.Parse(command).RootElement.GetProperty("cmd").GetString()));
    }

    [Fact]
    public async Task ShutdownIsBoundedEvenWhenTheHelperRefusesToExit()
    {
        var process = new FakeCollectHelperProcess(exitsWhenInputCloses: false);
        var client = CollectHelperClient.Create(process);
        process.Emit(new { ev = "ready" });
        await client.WaitUntilReadyAsync(Timeout, CancellationToken.None);

        var started = DateTime.UtcNow;
        await client.DisposeAsync();
        var elapsed = DateTime.UtcNow - started;

        Assert.True(process.WasKilled, "卡住不退的助手必须被结束掉");
        // 采集进度已写入断点文件，强杀不丢进度；关闭软件时不能让界面长时间卡住
        Assert.True(elapsed < CollectHelperClient.ShutdownGrace + TimeSpan.FromSeconds(8),
            $"关闭耗时 {elapsed.TotalSeconds:F1}s，超出可接受范围");
    }

    /// <summary>
    /// 真正起一个 Python 子进程跑助手，验证 C# 与助手确实能对话。
    /// 找不到解释器或助手脚本时跳过：这两者都是构建产物，CI 上未必存在。
    /// </summary>
    [Fact]
    public async Task RealHelperProcessCompletesTheHandshakeAndValidatesCommands()
    {
        var repositoryRoot = FindRepositoryRoot();
        if (repositoryRoot is null) return;

        var script = Path.Combine(repositoryRoot, "tools", "collect_helper", "main.py");
        var interpreter = FindInterpreter(repositoryRoot);
        if (interpreter is null || !File.Exists(script)) return;

        await using var client = await CollectHelperClient.StartAsync(
            interpreter, script, TimeSpan.FromSeconds(30), CancellationToken.None);

        var failures = new List<string>();
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Failed += message =>
        {
            failures.Add(message);
            reported.TrySetResult();
        };

        // 故意漏掉账号：助手应当拒绝并保持存活
        await client.StartCollectingAsync(
            new CollectRequest("precise", new[] { "云边有个小卖部" }, Array.Empty<CollectAccount>()),
            CancellationToken.None);

        await reported.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Contains("账号", failures[0]);   // 中文经由真实管道往返完好
    }

    private static string? FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (directory.GetFiles("BaiduBatchTransfer.sln").Length > 0) return directory.FullName;
            directory = directory.Parent;
        }

        return null;
    }

    private static string? FindInterpreter(string repositoryRoot)
    {
        var bundled = Directory.Exists(Path.Combine(repositoryRoot, ".artifacts"))
            ? Directory.EnumerateFiles(Path.Combine(repositoryRoot, ".artifacts"), "python.exe",
                SearchOption.AllDirectories).FirstOrDefault()
            : null;
        if (bundled is not null) return bundled;

        foreach (var candidate in Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator)
                                 ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            string path;
            try
            {
                path = Path.Combine(candidate.Trim(), "python.exe");
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (File.Exists(path)) return path;
        }

        return null;
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
