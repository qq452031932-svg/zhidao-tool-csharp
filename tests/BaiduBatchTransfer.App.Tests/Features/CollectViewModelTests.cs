using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BaiduBatchTransfer.App.Features.Collect;
using Xunit;

namespace BaiduBatchTransfer.App.Tests.Features;

/// <summary>
/// 「知道采集」页面的视图模型测试。
/// 用真实客户端配上脚本化的假进程，验证的是「助手吐出一行 JSON」到「界面多一行」的完整链路。
/// </summary>
public sealed class CollectViewModelTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static async Task<(CollectViewModel Model, FakeCollectHelperProcess Process,
        FakeCollectHelperSession Session)> CreateAsync()
    {
        var process = new FakeCollectHelperProcess();
        var client = CollectHelperClient.Create(process);
        process.Emit(new { ev = "ready" });
        await client.WaitUntilReadyAsync(Timeout, CancellationToken.None);

        var session = new FakeCollectHelperSession(client);
        // 测试内同步派发，省去界面线程
        var model = new CollectViewModel(session, action => action());
        return (model, process, session);
    }

    private static CollectViewModel Configure(CollectViewModel model)
    {
        model.KeywordText = "云边有个小卖部\n长安的荔枝";
        model.CookieText = "BDUSS=secret";
        return model;
    }

    [Theory]
    [InlineData("甲\n乙\n丙", 3)]
    [InlineData("甲，乙,丙", 3)]
    [InlineData("  甲  \n\n  乙  ", 2)]
    [InlineData("甲\n甲\n乙", 2)]           // 去重
    [InlineData("   \n  \n", 0)]
    public void KeywordsAreSplitTrimmedAndDeduplicated(string text, int expected)
    {
        var model = new CollectViewModel(new FakeCollectHelperSession(
            CollectHelperClient.Create(new FakeCollectHelperProcess())), action => action());
        model.KeywordText = text;
        Assert.Equal(expected, model.ParseKeywords().Count);
    }

    [Fact]
    public async Task MissingKeywordsOrCookieIsRefusedBeforeStartingTheHelper()
    {
        var (model, _, session) = await CreateAsync();

        model.CookieText = "BDUSS=x";
        await model.StartCommand.ExecuteAsync(null);
        Assert.Contains("关键词", model.StatusMessage);
        Assert.False(model.IsCollecting);

        model.KeywordText = "云边有个小卖部";
        model.CookieText = "   ";
        await model.StartCommand.ExecuteAsync(null);
        Assert.Contains("Cookie", model.StatusMessage);
        Assert.False(model.IsCollecting);

        Assert.Equal(0, session.ConnectCount);   // 参数不全时不该去启动助手
    }

    [Fact]
    public async Task CollectedResourcesFillTheTableInOrder()
    {
        var (model, process, _) = await CreateAsync();
        await Configure(model).StartCommand.ExecuteAsync(null);

        Assert.True(model.IsCollecting);
        process.Emit(new
        {
            ev = "item",
            name = "云边有个小卖部",
            link = "https://pan.baidu.com/s/1AbCdE?pwd=8k2m",
            pwd = "8k2m",
            source = "https://zhidao.baidu.com/question/11.html",
        });
        process.Emit(new { ev = "item", name = "长安的荔枝", link = "https://pan.baidu.com/s/1Zz", pwd = "2p9q", source = "q" });
        process.Emit(new { ev = "progress", found = 2, target = 2 });

        await WaitUntil(() => model.Resources.Count == 2);

        Assert.Equal(1, model.Resources[0].Ordinal);
        Assert.Equal("云边有个小卖部", model.Resources[0].Name);
        Assert.Equal("8k2m", model.Resources[0].Pwd);
        Assert.Equal(2, model.Resources[1].Ordinal);
        Assert.Equal("长安的荔枝", model.Resources[1].Name);

        await WaitUntil(() => model.FoundCount == 2);
        Assert.Equal(2, model.TargetTotal);
    }

    [Fact]
    public async Task PreciseModeShowsKeywordCountAsTheDenominatorBeforeAnyProgressArrives()
    {
        var (model, _, _) = await CreateAsync();
        model.IsPreciseMode = true;
        await Configure(model).StartCommand.ExecuteAsync(null);

        Assert.Equal(2, model.TargetTotal);   // 两个关键词
        Assert.Equal(0, model.FoundCount);
    }

    [Fact]
    public async Task RiskPromptBlocksStoppingAndIsClearedByAnswering()
    {
        var (model, process, _) = await CreateAsync();
        await Configure(model).StartCommand.ExecuteAsync(null);

        process.Emit(new { ev = "ask", reason = "采集账号受限" });
        await WaitUntil(() => model.IsRiskPromptVisible);

        Assert.Equal("采集账号受限", model.RiskReason);
        Assert.True(model.ContinueAfterRiskCommand.CanExecute(null));
        Assert.False(model.StopCommand.CanExecute(null), "风控待答复时应走答复按钮，而不是停止");

        await model.ContinueAfterRiskCommand.ExecuteAsync(null);

        Assert.False(model.IsRiskPromptVisible);
        var resume = JsonDocument.Parse(process.WrittenCommands().Last()).RootElement;
        Assert.Equal("resume", resume.GetProperty("cmd").GetString());
        Assert.True(resume.GetProperty("continue").GetBoolean());
    }

    [Fact]
    public async Task AbortingAfterRiskSendsAContinueFalse()
    {
        var (model, process, _) = await CreateAsync();
        await Configure(model).StartCommand.ExecuteAsync(null);

        process.Emit(new { ev = "ask", reason = "采集账号受限" });
        await WaitUntil(() => model.IsRiskPromptVisible);
        await model.AbortAfterRiskCommand.ExecuteAsync(null);

        var resume = JsonDocument.Parse(process.WrittenCommands().Last()).RootElement;
        Assert.False(resume.GetProperty("continue").GetBoolean());
    }

    [Fact]
    public async Task StopRequestIsForwardedAndCompletionReportsIt()
    {
        var (model, process, _) = await CreateAsync();
        await Configure(model).StartCommand.ExecuteAsync(null);

        await model.StopCommand.ExecuteAsync(null);
        Assert.Equal("stop", JsonDocument.Parse(process.WrittenCommands().Last())
            .RootElement.GetProperty("cmd").GetString());

        process.Emit(new { ev = "done", found = 3, stopped = true, stats = new { found = 3, no_pwd = 1 } });
        await WaitUntil(() => !model.IsCollecting);

        Assert.Equal(3, model.FoundCount);
        Assert.Contains("已停止", model.StatusMessage);
        Assert.Contains(model.Logs, entry => entry.Contains("没有提取码"));
    }

    [Fact]
    public async Task NaturalCompletionReportsSuccessAndReEnablesStart()
    {
        var (model, process, _) = await CreateAsync();
        await Configure(model).StartCommand.ExecuteAsync(null);
        Assert.False(model.StartCommand.CanExecute(null), "采集期间不应允许重复开始");

        process.Emit(new { ev = "done", found = 2, stopped = false, stats = new { found = 2 } });
        await WaitUntil(() => !model.IsCollecting);

        Assert.Contains("采集完成", model.StatusMessage);
        Assert.True(model.StartCommand.CanExecute(null));
    }

    [Fact]
    public async Task HelperErrorIsSurfacedWithoutEndingTheRun()
    {
        var (model, process, _) = await CreateAsync();
        await Configure(model).StartCommand.ExecuteAsync(null);

        process.Emit(new { ev = "error", message = "第 3 个关键词失败" });
        await WaitUntil(() => model.StatusMessage.Contains("第 3 个关键词失败"));

        Assert.True(model.IsCollecting, "单个关键词失败不应终止整轮采集");
        Assert.Contains(model.Logs, entry => entry.Contains("第 3 个关键词失败"));
    }

    [Fact]
    public async Task BrokenChannelEndsTheRunAndClearsAnyPendingPrompt()
    {
        var (model, process, _) = await CreateAsync();
        await Configure(model).StartCommand.ExecuteAsync(null);
        process.Emit(new { ev = "ask", reason = "采集账号受限" });
        await WaitUntil(() => model.IsRiskPromptVisible);

        process.CompleteOutput();

        await WaitUntil(() => !model.IsCollecting);
        Assert.False(model.IsRiskPromptVisible);
        Assert.Contains("断开", model.StatusMessage);
        Assert.True(model.StartCommand.CanExecute(null), "断开后应允许重新开始");
    }

    [Fact]
    public async Task StartingAgainClearsThePreviousRun()
    {
        var (model, process, _) = await CreateAsync();
        await Configure(model).StartCommand.ExecuteAsync(null);
        process.Emit(new { ev = "item", name = "旧的一条", link = "l", pwd = "p", source = "s" });
        await WaitUntil(() => model.Resources.Count == 1);

        process.Emit(new { ev = "done", found = 1, stopped = false, stats = new { found = 1 } });
        await WaitUntil(() => !model.IsCollecting);

        await model.StartCommand.ExecuteAsync(null);
        Assert.Empty(model.Resources);
        Assert.Empty(model.Logs);
        Assert.Equal(0, model.FoundCount);
    }

    [Fact]
    public async Task DisposeReleasesTheSession()
    {
        var (model, _, session) = await CreateAsync();
        await model.DisposeAsync();
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task ImportedKeywordsAreAppendedNotOverwritten()
    {
        var process = new FakeCollectHelperProcess();
        var client = CollectHelperClient.Create(process);
        process.Emit(new { ev = "ready" });
        await client.WaitUntilReadyAsync(Timeout, CancellationToken.None);
        var model = new CollectViewModel(new FakeCollectHelperSession(client), action => action(),
            new StubKeywordFileDialog(@"C:\words.txt"));

        model.KeywordText = "手填的一个";
        await model.ImportKeywordsCommand.ExecuteAsync(null);

        var command = JsonDocument.Parse(process.WrittenCommands().Last()).RootElement;
        Assert.Equal("read_keywords", command.GetProperty("cmd").GetString());
        Assert.Equal(@"C:\words.txt", command.GetProperty("path").GetString());

        process.Emit(new
        {
            ev = "keywords",
            items = new[] { "云边有个小卖部", "长安的荔枝" },
            lines = 3,
            encoding = "gb18030",
            truncated = false,
        });

        await WaitUntil(() => model.ParseKeywords().Count == 3);
        Assert.Equal(new[] { "手填的一个", "云边有个小卖部", "长安的荔枝" }, model.ParseKeywords());
        Assert.Contains("gb18030", model.StatusMessage);   // 告诉用户是按什么编码读的
    }

    [Fact]
    public async Task ImportingIntoAnEmptyBoxJustFillsIt()
    {
        var process = new FakeCollectHelperProcess();
        var client = CollectHelperClient.Create(process);
        process.Emit(new { ev = "ready" });
        await client.WaitUntilReadyAsync(Timeout, CancellationToken.None);
        var model = new CollectViewModel(new FakeCollectHelperSession(client), action => action(),
            new StubKeywordFileDialog(@"C:\words.txt"));

        await model.ImportKeywordsCommand.ExecuteAsync(null);
        process.Emit(new { ev = "keywords", items = new[] { "甲", "乙" }, lines = 2, encoding = "utf-8", truncated = false });

        await WaitUntil(() => model.ParseKeywords().Count == 2);
        Assert.Equal(new[] { "甲", "乙" }, model.ParseKeywords());
    }

    [Fact]
    public async Task CancellingTheFileDialogSendsNothing()
    {
        var process = new FakeCollectHelperProcess();
        var client = CollectHelperClient.Create(process);
        process.Emit(new { ev = "ready" });
        await client.WaitUntilReadyAsync(Timeout, CancellationToken.None);
        var model = new CollectViewModel(new FakeCollectHelperSession(client), action => action(),
            new StubKeywordFileDialog(null));

        await model.ImportKeywordsCommand.ExecuteAsync(null);

        Assert.Empty(process.WrittenCommands());
    }

    [Fact]
    public async Task EmptyFileIsReportedInsteadOfSilentlyDoingNothing()
    {
        var process = new FakeCollectHelperProcess();
        var client = CollectHelperClient.Create(process);
        process.Emit(new { ev = "ready" });
        await client.WaitUntilReadyAsync(Timeout, CancellationToken.None);
        var model = new CollectViewModel(new FakeCollectHelperSession(client), action => action(),
            new StubKeywordFileDialog(@"C:\empty.txt"));

        await model.ImportKeywordsCommand.ExecuteAsync(null);
        process.Emit(new { ev = "keywords", items = Array.Empty<string>(), lines = 0, encoding = "utf-8", truncated = false });

        await WaitUntil(() => model.StatusMessage.Contains("没有可用的关键词"));
        Assert.Empty(model.ParseKeywords());
    }

    [Fact]
    public async Task ImportIsUnavailableWithoutAFilePicker()
    {
        var (model, _, _) = await CreateAsync();
        Assert.False(model.CanImportKeywords);
        Assert.False(model.ImportKeywordsCommand.CanExecute(null));
    }

    [Fact]
    public async Task StartingAfterImportReusesTheSameHelperConnection()
    {
        var process = new FakeCollectHelperProcess();
        var client = CollectHelperClient.Create(process);
        process.Emit(new { ev = "ready" });
        await client.WaitUntilReadyAsync(Timeout, CancellationToken.None);
        var session = new FakeCollectHelperSession(client);
        var model = new CollectViewModel(session, action => action(),
            new StubKeywordFileDialog(@"C:\words.txt"));

        await model.ImportKeywordsCommand.ExecuteAsync(null);
        process.Emit(new { ev = "keywords", items = new[] { "甲" }, lines = 1, encoding = "utf-8", truncated = false });
        await WaitUntil(() => model.ParseKeywords().Count == 1);

        model.CookieText = "BDUSS=x";
        await model.StartCommand.ExecuteAsync(null);

        Assert.Equal(1, session.ConnectCount);   // 复用同一个助手，且事件只订阅一次
        process.Emit(new { ev = "item", name = "甲资源", link = "l", pwd = "p", source = "s" });
        await WaitUntil(() => model.Resources.Count == 1);
        Assert.Single(model.Resources);          // 重复订阅会让这里变成 2 行
    }

    private sealed class StubKeywordFileDialog(string? path) : IKeywordFileDialog
    {
        public string? Show() => path;
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
