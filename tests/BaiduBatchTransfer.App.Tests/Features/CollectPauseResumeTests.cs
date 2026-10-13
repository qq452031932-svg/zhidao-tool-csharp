using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BaiduBatchTransfer.App.Features.Collect;
using Xunit;

namespace BaiduBatchTransfer.App.Tests.Features;

/// <summary>
/// 用户主动暂停与续采。与风控询问是两条路：这里验证按钮状态、发给助手的命令，
/// 以及暂停记录能撑过软件重启。
/// </summary>
public sealed class CollectPauseResumeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private sealed class MemoryPauseStore : ICollectPauseStore
    {
        public CollectPausedTask? Task { get; set; }
        public bool FailOnSave { get; set; }
        public CollectPausedTask? Load() => Task;
        public void Save(CollectPausedTask task)
        {
            if (FailOnSave) throw new IOException("磁盘不可写");
            Task = task;
        }
        public void Clear() => Task = null;
    }

    private static async Task<(CollectViewModel Model, FakeCollectHelperProcess Process, MemoryPauseStore Store)>
        CreateAsync(MemoryPauseStore? store = null)
    {
        var process = new FakeCollectHelperProcess();
        var client = CollectHelperClient.Create(process);
        process.Emit(new { ev = "ready" });
        await client.WaitUntilReadyAsync(Timeout, CancellationToken.None);
        store ??= new MemoryPauseStore();
        var model = new CollectViewModel(new FakeCollectHelperSession(client), action => action(), pauseStore: store);
        return (model, process, store);
    }

    private static async Task<(CollectViewModel Model, FakeCollectHelperProcess Process, MemoryPauseStore Store)>
        StartFuzzyAsync()
    {
        var (model, process, store) = await CreateAsync();
        model.KeywordText = "云边有个小卖部\n长安的荔枝";
        model.CookieText = "BDUSS=secret";
        model.IsFuzzyMode = true;
        model.TargetCount = 10;
        await model.StartCommand.ExecuteAsync(null);
        return (model, process, store);
    }

    private static void EmitItem(FakeCollectHelperProcess process, string name, bool restored = false) =>
        process.Emit(new { ev = "item", name, link = $"https://pan.baidu.com/s/{name}", pwd = "8k2m", source = "q", restored });

    private static async Task PauseAsync(CollectViewModel model, FakeCollectHelperProcess process, int found)
    {
        await model.PauseResumeCommand.ExecuteAsync(null);
        process.Emit(new { ev = "done", found, stopped = true, paused = true, failed = false, stats = new { found } });
        await WaitUntil(() => model.IsPaused);
    }

    private static JsonElement LastCommand(FakeCollectHelperProcess process) =>
        JsonDocument.Parse(process.WrittenCommands().Last()).RootElement;

    [Fact]
    public async Task PauseIsOnlyOfferedWhileCollectingAndSendsThePauseCommandOnce()
    {
        var (model, process, _) = await CreateAsync();
        Assert.Equal("暂停", model.PauseButtonText);
        Assert.False(model.PauseResumeCommand.CanExecute(null), "没在采集时不该能暂停");

        model.KeywordText = "甲";
        model.CookieText = "BDUSS=secret";
        await model.StartCommand.ExecuteAsync(null);
        Assert.True(model.PauseResumeCommand.CanExecute(null));

        await model.PauseResumeCommand.ExecuteAsync(null);

        Assert.Equal("pause", LastCommand(process).GetProperty("cmd").GetString());
        Assert.Contains("正在暂停", model.StatusMessage);
        Assert.False(model.PauseResumeCommand.CanExecute(null), "等待收尾期间不重复发暂停");
        Assert.True(model.StopCommand.CanExecute(null), "等待收尾期间仍可改为停止");
        Assert.True(model.IsCollecting);
    }

    [Fact]
    public async Task PausedRunKeepsItsRowsLocksTheTaskAndIsRemembered()
    {
        var (model, process, store) = await StartFuzzyAsync();
        EmitItem(process, "a1");
        EmitItem(process, "a2");
        await WaitUntil(() => model.Resources.Count == 2);

        await PauseAsync(model, process, found: 2);

        Assert.False(model.IsCollecting);
        Assert.Equal("继续", model.PauseButtonText);
        Assert.Contains("已暂停", model.StatusMessage);
        Assert.Equal(2, model.Resources.Count);
        Assert.True(model.PauseResumeCommand.CanExecute(null));
        Assert.True(model.StopCommand.CanExecute(null));
        Assert.False(model.StartCommand.CanExecute(null), "暂停中须先继续或停止，不能另起一轮覆盖断点");
        Assert.False(model.CanEditTask);
        Assert.False(model.CanEditTargetCount);

        var saved = Assert.IsType<CollectPausedTask>(store.Task);
        Assert.Equal("fuzzy", saved.Mode);
        Assert.Equal(new[] { "云边有个小卖部", "长安的荔枝" }, saved.Keywords);
        Assert.Equal(10, saved.Count);
        Assert.Equal(2, saved.Found);
        Assert.Equal(10, saved.Target);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(saved));
    }

    [Fact]
    public async Task ContinueResumesTheSameTaskAndRebuildsRowsFromTheCheckpoint()
    {
        var (model, process, store) = await StartFuzzyAsync();
        EmitItem(process, "a1");
        EmitItem(process, "a2");
        EmitItem(process, "未落盘的一条");   // 界面比断点多出的行，续采后以断点为准
        await WaitUntil(() => model.Resources.Count == 3);
        await PauseAsync(model, process, found: 2);
        model.CookieText = "BDUSS=replaced";

        await model.PauseResumeCommand.ExecuteAsync(null);

        var start = LastCommand(process);
        Assert.Equal("start", start.GetProperty("cmd").GetString());
        Assert.True(start.GetProperty("resume").GetBoolean());
        Assert.Equal("fuzzy", start.GetProperty("mode").GetString());
        Assert.Equal(10, start.GetProperty("count").GetInt32());
        Assert.Equal(new[] { "云边有个小卖部", "长安的荔枝" },
            start.GetProperty("keywords").EnumerateArray().Select(word => word.GetString()).ToArray());
        Assert.Equal("BDUSS=replaced", start.GetProperty("accounts")[0].GetProperty("cookie").GetString());
        Assert.True(model.IsCollecting);
        Assert.False(model.IsPaused);
        Assert.Equal("暂停", model.PauseButtonText);
        Assert.Empty(model.Resources);

        var logsBefore = model.Logs.Count(entry => entry.Contains("已采到第"));
        EmitItem(process, "a1", restored: true);
        EmitItem(process, "a2", restored: true);
        EmitItem(process, "a3");
        await WaitUntil(() => model.Resources.Count == 3);

        Assert.Equal(new[] { "a1", "a2", "a3" }, model.Resources.Select(row => row.Name).ToArray());
        Assert.Equal(new[] { 1, 2, 3 }, model.Resources.Select(row => row.Ordinal).ToArray());
        Assert.Equal(logsBefore + 1, model.Logs.Count(entry => entry.Contains("已采到第")));
        Assert.NotNull(store.Task);

        process.Emit(new { ev = "done", found = 3, stopped = false, paused = false, failed = false, stats = new { found = 3 } });
        await WaitUntil(() => !model.IsCollecting);

        Assert.False(model.IsPaused);
        Assert.Null(store.Task);
        Assert.Contains("采集完成", model.StatusMessage);
        Assert.True(model.StartCommand.CanExecute(null));
        Assert.True(model.CanEditTask);
    }

    [Fact]
    public async Task ContinueNeedsACookieAndStaysPausedWithoutOne()
    {
        var (model, process, _) = await StartFuzzyAsync();
        await PauseAsync(model, process, found: 0);
        var commands = process.WrittenCommands().Count;
        model.CookieText = "  ";

        await model.PauseResumeCommand.ExecuteAsync(null);

        Assert.Contains("Cookie", model.StatusMessage);
        Assert.True(model.IsPaused);
        Assert.Equal(commands, process.WrittenCommands().Count);
    }

    [Fact]
    public async Task StoppingAPausedTaskDiscardsItWithoutTouchingTheRows()
    {
        var (model, process, store) = await StartFuzzyAsync();
        EmitItem(process, "a1");
        await WaitUntil(() => model.Resources.Count == 1);
        await PauseAsync(model, process, found: 1);
        var commands = process.WrittenCommands().Count;

        await model.StopCommand.ExecuteAsync(null);

        Assert.False(model.IsPaused);
        Assert.Null(store.Task);
        Assert.Single(model.Resources);
        Assert.Equal(commands, process.WrittenCommands().Count);
        Assert.Equal("暂停", model.PauseButtonText);
        Assert.True(model.StartCommand.CanExecute(null));
        Assert.True(model.CanEditTask);
        Assert.False(model.StopCommand.CanExecute(null));
    }

    [Fact]
    public async Task StopRequestedWhileAPauseIsPendingEndsTheTask()
    {
        var (model, process, store) = await StartFuzzyAsync();
        await model.PauseResumeCommand.ExecuteAsync(null);
        await model.StopCommand.ExecuteAsync(null);
        Assert.Equal("stop", LastCommand(process).GetProperty("cmd").GetString());

        process.Emit(new { ev = "done", found = 0, stopped = true, paused = false, failed = false, stats = new { } });
        await WaitUntil(() => !model.IsCollecting);

        Assert.False(model.IsPaused);
        Assert.Null(store.Task);
        Assert.Contains("已停止", model.StatusMessage);
    }

    [Fact]
    public async Task RunThatFinishesBeforeThePauseLandsIsSimplyComplete()
    {
        var (model, process, store) = await StartFuzzyAsync();
        await model.PauseResumeCommand.ExecuteAsync(null);

        process.Emit(new { ev = "done", found = 10, stopped = false, paused = false, failed = false, stats = new { } });
        await WaitUntil(() => !model.IsCollecting);

        Assert.False(model.IsPaused);
        Assert.Null(store.Task);
        Assert.Contains("采集完成", model.StatusMessage);
        Assert.False(model.PauseResumeCommand.CanExecute(null));
    }

    [Fact]
    public async Task PausedTaskSurvivesARestartAndContinuesWithTheStoredParameters()
    {
        var store = new MemoryPauseStore
        {
            Task = new CollectPausedTask("fuzzy", ["甲,带逗号", "乙"], 20, 7, 20, true, DateTimeOffset.Now),
        };

        var (model, process, _) = await CreateAsync(store);

        Assert.True(model.IsPaused);
        Assert.Equal("继续", model.PauseButtonText);
        Assert.True(model.IsFuzzyMode);
        Assert.Equal(20, model.TargetCount);
        Assert.Equal(7, model.FoundCount);
        Assert.Equal(20, model.TargetTotal);
        Assert.Equal(new[] { "甲,带逗号", "乙" }, model.ParseKeywords());
        Assert.Contains("上次采集已暂停", model.StatusMessage);
        Assert.False(model.StartCommand.CanExecute(null));
        Assert.Empty(process.WrittenCommands());

        model.CookieText = "BDUSS=after-restart";
        await model.PauseResumeCommand.ExecuteAsync(null);

        var start = LastCommand(process);
        Assert.True(start.GetProperty("resume").GetBoolean());
        Assert.Equal(20, start.GetProperty("count").GetInt32());
        Assert.Equal(new[] { "甲,带逗号", "乙" },
            start.GetProperty("keywords").EnumerateArray().Select(word => word.GetString()).ToArray());
    }

    [Fact]
    public async Task FailureDuringAContinuedRunKeepsTheTaskResumable()
    {
        var (model, process, store) = await StartFuzzyAsync();
        await PauseAsync(model, process, found: 2);
        await model.PauseResumeCommand.ExecuteAsync(null);

        process.Emit(new { ev = "error", message = "PoolError: 账号全部失效" });
        process.Emit(new { ev = "done", found = 2, stopped = true, paused = false, failed = true, stats = new { } });
        // 助手线程先清「采集中」再置「已暂停」，两个都等到再断言。
        await WaitUntil(() => !model.IsCollecting && model.IsPaused);

        Assert.True(model.IsPaused, "续采途中出错不应丢掉可继续的任务");
        Assert.NotNull(store.Task);
        Assert.Contains("进度已保存", model.StatusMessage);
    }

    [Fact]
    public async Task FailureOfAFirstRunEndsItAsBefore()
    {
        var (model, process, store) = await StartFuzzyAsync();

        process.Emit(new { ev = "done", found = 0, stopped = true, paused = false, failed = true, stats = new { } });
        await WaitUntil(() => !model.IsCollecting);

        Assert.False(model.IsPaused);
        Assert.Null(store.Task);
        Assert.True(model.StartCommand.CanExecute(null));
    }

    [Fact]
    public async Task HelperDyingDuringAContinuedRunReturnsToPaused()
    {
        var (model, process, store) = await StartFuzzyAsync();
        await PauseAsync(model, process, found: 1);
        await model.PauseResumeCommand.ExecuteAsync(null);

        process.CompleteOutput();
        await WaitUntil(() => !model.IsCollecting);

        Assert.True(model.IsPaused);
        Assert.NotNull(store.Task);
    }

    [Fact]
    public async Task RiskPromptIsNotAPauseAndHidesThePauseButton()
    {
        var (model, process, store) = await StartFuzzyAsync();

        process.Emit(new { ev = "ask", reason = "采集账号受限" });
        await WaitUntil(() => model.IsRiskPromptVisible);

        Assert.False(model.IsPaused);
        Assert.False(model.PauseResumeCommand.CanExecute(null), "风控待答复时走答复按钮");
        Assert.DoesNotContain("已暂停", model.StatusMessage);
        Assert.Null(store.Task);
    }

    [Fact]
    public async Task PauseStillWorksInSessionWhenTheRecordCannotBeWritten()
    {
        var (model, process, store) = await StartFuzzyAsync();
        store.FailOnSave = true;

        await PauseAsync(model, process, found: 1);

        Assert.True(model.IsPaused);
        Assert.Contains(model.Logs, entry => entry.Contains("暂停记录未能保存"));
        Assert.True(model.PauseResumeCommand.CanExecute(null));
    }

    [Fact]
    public async Task ClientTranslatesRestoredItemsAndPauseOutcome()
    {
        var process = new FakeCollectHelperProcess();
        await using var client = CollectHelperClient.Create(process);
        process.Emit(new { ev = "ready" });
        await client.WaitUntilReadyAsync(Timeout, CancellationToken.None);
        var resources = new System.Collections.Concurrent.ConcurrentQueue<CollectedResource>();
        var outcome = new TaskCompletionSource<CollectOutcome>();
        client.ResourceCollected += resources.Enqueue;
        client.Completed += outcome.SetResult;

        await client.PauseCollectingAsync(CancellationToken.None);
        Assert.Equal("{\"cmd\":\"pause\"}", process.WrittenCommands().Last());

        EmitItem(process, "旧", restored: true);
        process.Emit(new { ev = "item", name = "新", link = "l", pwd = "p", source = "s" });
        process.Emit(new { ev = "done", found = 2, stopped = true, paused = true, stats = new { } });

        var done = await outcome.Task.WaitAsync(Timeout);
        Assert.True(done.Paused);
        Assert.False(done.Failed);
        Assert.Equal(new[] { true, false }, resources.Select(resource => resource.Restored).ToArray());
    }

    [Fact]
    public void FileStoreRoundTripsToleratesDamageAndClears()
    {
        var directory = Path.Combine(Path.GetTempPath(), "collect-pause-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "collect", "paused-task.json");
        try
        {
            var store = new FileCollectPauseStore(path);
            Assert.Null(store.Load());
            store.Clear();   // 没有记录时清除不报错

            var task = new CollectPausedTask("precise", ["云边有个小卖部", "甲,乙"], 0, 1, 2, true,
                new DateTimeOffset(2026, 10, 12, 9, 30, 0, TimeSpan.FromHours(8)));
            store.Save(task);
            store.Save(task with { Found = 2 });

            var loaded = Assert.IsType<CollectPausedTask>(new FileCollectPauseStore(path).Load());
            Assert.Equal(task.Keywords, loaded.Keywords);
            Assert.Equal((task.Mode, 2, task.Target, task.KeywordLineMode, task.PausedAt),
                (loaded.Mode, loaded.Found, loaded.Target, loaded.KeywordLineMode, loaded.PausedAt));
            Assert.Equal(new[] { "paused-task.json" },
                Directory.GetFiles(Path.GetDirectoryName(path)!).Select(Path.GetFileName).ToArray());

            foreach (var damaged in new[] { "{不是 JSON", "null", "{\"Mode\":\"turbo\",\"Keywords\":[\"甲\"]}",
                         "{\"Mode\":\"fuzzy\",\"Keywords\":[\" \"]}", "{\"Mode\":\"fuzzy\"}" })
            {
                File.WriteAllText(path, damaged);
                Assert.Null(store.Load());
            }

            store.Clear();
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
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
