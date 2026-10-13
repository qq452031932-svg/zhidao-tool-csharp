using System.Text.Json;
using BaiduBatchTransfer.App.Features.Collect;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class CollectKeywordImportTests
{
    private static async Task<(CollectViewModel Model, FakeCollectHelperProcess Process, CollectHelperClient Client)> CreateAsync()
    {
        var process = new FakeCollectHelperProcess();
        var client = CollectHelperClient.Create(process);
        process.Emit(new { ev = "ready" });
        await client.WaitUntilReadyAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        return (new CollectViewModel(new FakeCollectHelperSession(client), action => action(), new Picker()), process, client);
    }

    [Fact]
    public async Task ReadingTableKeepsButtonsBusyUntilTheReply()
    {
        var (model, process, client) = await CreateAsync();
        await using var owned = client;
        await model.ImportKeywordsCommand.ExecuteAsync(null);
        Assert.True(model.IsBusy);
        Assert.False(model.StartCommand.CanExecute(null));
        Assert.False(model.ImportKeywordsCommand.CanExecute(null));
        process.Emit(new { ev = "keywords", items = new[] { "三体" }, lines = 1, encoding = "xlsx", source = "Excel：词榜 · 热搜索词", total = 1 });
        await WaitUntil(() => model.KeywordText == "三体" && !model.IsBusy);
        Assert.Contains("热搜索词", model.StatusMessage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadFailureOrDisconnectPreservesExistingWordsAndReleasesButtons(bool disconnect)
    {
        var (model, process, client) = await CreateAsync();
        await using var owned = client;
        model.KeywordText = "原有关键词";
        await model.ImportKeywordsCommand.ExecuteAsync(null);
        Assert.True(model.IsBusy);
        if (disconnect) process.CompleteOutput();
        else process.Emit(new { ev = "error", command = "read_keywords", message = "Excel 文件损坏", running = false });
        await WaitUntil(() => !model.IsBusy);
        Assert.Equal("原有关键词", model.KeywordText);
        Assert.True(model.ImportKeywordsCommand.CanExecute(null));
    }

    [Fact]
    public async Task TableKeywordsWithCommasRemainOneKeywordWhenStarting()
    {
        var (model, process, client) = await CreateAsync();
        await using var owned = client;
        model.KeywordText = "手填甲,手填乙";
        model.CookieText = "test-cookie";
        await model.ImportKeywordsCommand.ExecuteAsync(null);
        process.Emit(new { ev = "keywords", items = new[] { "甲,乙", "丙，丁" }, lines = 2, encoding = "xlsx", total = 2 });
        await WaitUntil(() => model.KeywordText.Contains("丙，丁"));
        Assert.Equal(new[] { "手填甲", "手填乙", "甲,乙", "丙，丁" }, model.ParseKeywords());
        await model.StartCommand.ExecuteAsync(null);
        using var command = JsonDocument.Parse(process.WrittenCommands().Last());
        Assert.Equal(new[] { "手填甲", "手填乙", "甲,乙", "丙，丁" },
            command.RootElement.GetProperty("keywords").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public async Task AppendingRespectsTheTotalLimitAndDeduplicatesBeforeCounting()
    {
        var (model, process, client) = await CreateAsync();
        await using var owned = client;
        model.KeywordText = string.Join("\n", Enumerable.Range(0, 9999).Select(index => $"词{index}"));
        await model.ImportKeywordsCommand.ExecuteAsync(null);
        process.Emit(new { ev = "keywords", items = new[] { "词0", "新增甲", "新增乙" }, lines = 3, encoding = "xlsx", total = 3 });
        await WaitUntil(() => model.KeywordText.Contains("新增甲"));
        Assert.Equal(10000, model.ParseKeywords().Count);
        Assert.DoesNotContain("新增乙", model.ParseKeywords());
        Assert.Contains("新增 1", model.StatusMessage);
        Assert.Contains("上限", model.StatusMessage);
    }

    [Fact]
    public async Task EditingAndUndoingImportedCommaKeywordsKeepsEachLineAtomic()
    {
        var (model, process, client) = await CreateAsync();
        await using var owned = client;
        await model.ImportKeywordsCommand.ExecuteAsync(null);
        process.Emit(new { ev = "keywords", items = new[] { "甲,乙", "丙，丁" }, lines = 2, encoding = "xlsx" });
        await WaitUntil(() => model.KeywordText.Contains("丙，丁") && !model.IsBusy);
        model.KeywordText = "甲,乙2\n丙，丁";
        Assert.Equal(new[] { "甲,乙2", "丙，丁" }, model.ParseKeywords());
        model.KeywordText = "";
        model.KeywordText = "甲,乙\n丙，丁";
        Assert.Equal(new[] { "甲,乙", "丙，丁" }, model.ParseKeywords());
    }

    [Fact]
    public async Task ImportingTheSameTableTwiceDoesNotRepeatWordsInTheBox()
    {
        var (model, process, client) = await CreateAsync();
        await using var owned = client;
        for (var round = 0; round < 2; round++)
        {
            await model.ImportKeywordsCommand.ExecuteAsync(null);
            process.Emit(new { ev = "keywords", items = new[] { "三体", "流浪地球" }, lines = 2, encoding = "xlsx", total = 2 });
            await WaitUntil(() => model.StatusMessage.Contains("已") && !model.IsBusy);
        }
        Assert.Equal("三体\n流浪地球", model.KeywordText);
        Assert.Contains("新增 0", model.StatusMessage);
    }

    private sealed class Picker : IKeywordFileDialog
    {
        public string? Show() => @"C:\sample.xlsx";
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < timeout) await Task.Delay(10);
        Assert.True(condition(), "等待关键词导入事件超时");
    }
}
