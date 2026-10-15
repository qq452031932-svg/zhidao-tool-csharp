using System.IO;
using System.Text.Json;
using BaiduBatchTransfer.App.Features.Collect;
using BaiduBatchTransfer.App.Services;

namespace BaiduBatchTransfer.App.Tests.Features;

/// <summary>多 Cookie：导入解析、本机加密保存、账号列表与采集请求。样本全部是编造的，不含真实凭据。</summary>
public sealed class ZhidaoAccountsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "zhidao-accounts-tests", Guid.NewGuid().ToString("N"));

    private static string Cookie(int index) =>
        $"bjhStoken=tok{index}; BAIDUID=ABCDEF{index}:FG=1; BDUSS=fakebduss{index:D4}xyz; STOKEN=st{index}; UBI=fi_PncwhpxZ%7ETaJc{index};";

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void SupplierFormatKeepsRemarkAndCookieAndDropsTheMiddleField()
    {
        var text = $"测试账号一----12345678----{Cookie(1)}\r\n测试账号二----12345678----{Cookie(2)}\n";
        var preview = ZhidaoAccountParser.Parse(text);
        Assert.Empty(preview.Duplicates);
        Assert.Empty(preview.Unrecognized);
        Assert.Equal(["测试账号一", "测试账号二"], preview.Accounts.Select(account => account.Remark));
        Assert.All(preview.Accounts, account => Assert.DoesNotContain("12345678", account.Cookie));
        Assert.Equal("fakebduss0001xyz", ZhidaoCookie.ReadBduss(preview.Accounts[0].Cookie));
        Assert.False(preview.Accounts[0].Cookie.EndsWith(';'));
        // BAIDUID 的值里本来就有冒号，不能被当成分隔符。
        Assert.Contains("BAIDUID=ABCDEF1:FG=1", preview.Accounts[0].Cookie);
        Assert.DoesNotContain("fakebduss", preview.Describe());
        Assert.Contains("密码", preview.Describe());
    }

    [Theory]
    [InlineData("备注甲\t{0}", "备注甲")]
    [InlineData("备注甲|{0}", "备注甲")]
    [InlineData("备注甲：{0}", "备注甲")]
    [InlineData("备注甲:{0}", "备注甲")]
    [InlineData("{0}", "")]
    [InlineData("Cookie: {0}", "")]
    [InlineData("{0}----备注甲", "备注甲")]
    [InlineData("99999----{0}", "")]
    public void OtherCommonLineFormatsAreRecognized(string template, string remark)
    {
        var preview = ZhidaoAccountParser.Parse(string.Format(template, Cookie(7)));
        var account = Assert.Single(preview.Accounts);
        Assert.Equal(remark, account.Remark);
        Assert.Equal("fakebduss0007xyz", ZhidaoCookie.ReadBduss(account.Cookie));
        Assert.DoesNotContain("Cookie:", account.Cookie);
    }

    [Fact]
    public void BrowserCookieJsonBecomesOneAccountAndSkipsOtherDomains()
    {
        var json = JsonSerializer.Serialize(new object[]
        {
            new { name = "BDUSS", value = "jsonbduss", domain = ".baidu.com" },
            new { name = "STOKEN", value = "s", domain = "zhidao.baidu.com" },
            new { name = "other", value = "x", domain = ".example.com" },
        });
        var account = Assert.Single(ZhidaoAccountParser.Parse(json).Accounts);
        Assert.Equal("BDUSS=jsonbduss; STOKEN=s", account.Cookie);
    }

    [Fact]
    public void ProblemsAndDuplicatesAreListedWithLineNumbersNotSilentlyDropped()
    {
        var text = string.Join("\n",
            $"甲----1----{Cookie(1)}",
            "",
            "# 注释行",
            $"乙----1----{Cookie(1)}",
            "丙----1----STOKEN=a; PTOKEN=b",
            "只是一行说明文字",
            $"丁----{Cookie(2)}----{Cookie(3)}",
            $"戊----1----{Cookie(4)}");
        var preview = ZhidaoAccountParser.Parse(text, existingBduss: ["fakebduss0004xyz"]);
        Assert.Equal(["甲"], preview.Accounts.Select(account => account.Remark));
        Assert.Equal([4, 8], preview.Duplicates.Select(issue => issue.LineNumber));
        Assert.Contains("第 1 行", preview.Duplicates[0].Reason);
        Assert.Contains("已有", preview.Duplicates[1].Reason);
        Assert.Equal([5, 6, 7], preview.Unrecognized.Select(issue => issue.LineNumber));
        Assert.Contains("BDUSS", preview.Unrecognized[0].Reason);
        Assert.Contains("多段", preview.Unrecognized[2].Reason);
        var description = preview.Describe();
        Assert.Contains("第 5 行", description);
        Assert.DoesNotContain("fakebduss", description);
    }

    [Theory]
    [InlineData("BDUSS")]
    [InlineData("=abc; BDUSS=x")]
    [InlineData("BDUSS=a\u0001b")]
    public void MalformedCookiesAreRejected(string cookie) =>
        Assert.Empty(ZhidaoAccountParser.Parse($"甲----{cookie}").Accounts);

    [Fact]
    public void MaskShowsOnlyTheEndsOfBduss()
    {
        var mask = ZhidaoCookie.Mask(Cookie(1));
        Assert.Equal("fake…1xyz", mask);
        Assert.Contains("共 5 项", ZhidaoCookie.Describe(Cookie(1)));
        Assert.DoesNotContain("tok1", ZhidaoCookie.Describe(Cookie(1)));
    }

    [Fact]
    public void StoreEncryptsRoundTripsAndMovesAsideAnUnreadableFile()
    {
        var path = Path.Combine(root, "collect", "zhidao-accounts.dat");
        var store = new DpapiZhidaoAccountStore(path);
        Assert.Empty(store.Load().Accounts);
        var account = new StoredZhidaoAccount(Guid.NewGuid(), "测试账号一", Cookie(1), true,
            ZhidaoAccountState.Valid, "ok", DateTimeOffset.Now);
        store.Save([account]);
        var bytes = File.ReadAllBytes(path);
        Assert.DoesNotContain("fakebduss", System.Text.Encoding.UTF8.GetString(bytes));
        Assert.DoesNotContain("测试账号一", System.Text.Encoding.UTF8.GetString(bytes));
        var loaded = Assert.Single(store.Load().Accounts);
        Assert.Equal(account.Cookie, loaded.Cookie);
        Assert.Equal(ZhidaoAccountState.Valid, loaded.State);

        File.WriteAllBytes(path, [1, 2, 3]);
        var broken = store.Load();
        Assert.Empty(broken.Accounts);
        Assert.Contains("无法读取", broken.Problem);
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.unreadable-*"));
    }

    [Fact]
    public async Task ImportConfirmsPreviewSavesAndSkipsDuplicatesOnReimport()
    {
        var store = new MemoryStore();
        var dialogs = new ScriptedDialogs(true);
        var list = new ZhidaoAccountListViewModel(store, new ScriptedValidator(), dialogs, readClipboard: () =>
            $"测试账号一----1----{Cookie(1)}\n测试账号二----1----{Cookie(2)}\n----1----{Cookie(3)}");
        await list.PasteCommand.ExecuteAsync(null);
        Assert.Equal(["测试账号一", "测试账号二", "账号3"], list.Accounts.Select(row => row.Remark));
        Assert.All(list.Accounts, row => Assert.True(row.IsEnabled));
        Assert.Equal(3, store.Saved.Count);
        Assert.Contains("识别到 3 个", dialogs.Messages.Single());
        Assert.Contains("已从剪贴板导入 3 个账号", list.Message);
        Assert.Contains("勾选 3 / 共 3", list.Summary);

        await list.PasteCommand.ExecuteAsync(null);
        Assert.Equal(3, list.Accounts.Count);
        Assert.Contains("没有可导入的账号", list.Message);
        Assert.Contains("重复 3 条", list.Message);
    }

    [Fact]
    public async Task CancelledPreviewImportsNothing()
    {
        var store = new MemoryStore();
        var list = new ZhidaoAccountListViewModel(store, new ScriptedValidator(), new ScriptedDialogs(false),
            readClipboard: () => Cookie(1));
        await list.PasteCommand.ExecuteAsync(null);
        Assert.Empty(list.Accounts);
        Assert.Empty(store.Saved);
        Assert.Contains("已取消", list.Message);
    }

    [Fact]
    public async Task ValidationSeparatesValidExpiredAndUnconfirmedAndOnlyChecksSelection()
    {
        var store = new MemoryStore(Stored(1), Stored(2), Stored(3), Stored(4));
        var validator = new ScriptedValidator();
        validator.Results["fakebduss0001xyz"] = new(true, "Cookie 验证通过");
        validator.Results["fakebduss0002xyz"] = new(false, "Cookie 已失效或未登录", DefinitelyExpired: true);
        validator.Results["fakebduss0003xyz"] = new(false, "Cookie 验证超时，未能确认登录状态");
        validator.Throws.Add("fakebduss0004xyz");
        var log = new RecordingRunLog();
        var list = new ZhidaoAccountListViewModel(store, validator, runLog: log, validationDelay: _ => Task.CompletedTask);

        // 勾选框即选中：只勾一部分时只验证勾选的；表格里鼠标选中哪行不影响。
        foreach (var row in list.Accounts.Skip(1)) row.IsEnabled = false;
        list.SetSelected([list.Accounts[2]]);
        Assert.Equal("验证 1 个", list.ValidateButtonText);
        await list.ValidateCommand.ExecuteAsync(null);
        Assert.Equal(["fakebduss0001xyz"], validator.Checked);
        Assert.Equal(ZhidaoAccountState.Unverified, list.Accounts[1].State);

        list.SetAllChecked(true);
        Assert.Equal("验证全部", list.ValidateButtonText);
        await list.ValidateCommand.ExecuteAsync(null);
        Assert.Equal(["可用", "已失效", "暂时无法验证", "暂时无法验证"], list.Accounts.Select(row => row.StateText));
        Assert.Contains("验证时间", list.Accounts[1].StateDetail);
        Assert.Contains("已失效 1", list.Message);
        Assert.Equal(ZhidaoAccountState.Expired, store.Saved.Single(account => account.Remark == "账号2").State);
        Assert.DoesNotContain(log.Lines, line => line.Contains("fakebduss"));

        var forCollecting = list.AccountsForCollecting(out var skipped);
        Assert.Equal(1, skipped);
        Assert.Equal(["账号1", "账号3", "账号4"], forCollecting.Select(row => row.Remark));
    }

    [Fact]
    public async Task ToggleDeleteAndUpdatePersistImmediately()
    {
        var store = new MemoryStore(Stored(1), Stored(2));
        var clipboard = $"新的----{Cookie(9)}";
        var list = new ZhidaoAccountListViewModel(store, new ScriptedValidator(), new ScriptedDialogs(true),
            readClipboard: () => clipboard);
        list.Accounts[0].IsEnabled = false;
        Assert.False(store.Saved.Single(account => account.Remark == "账号1").Enabled);

        list.SetSelected([list.Accounts[1]]);
        await list.UpdateCookieCommand.ExecuteAsync(null);
        Assert.Equal("账号2", list.Accounts[1].Remark);
        Assert.Equal("fakebduss0009xyz", ZhidaoCookie.ReadBduss(store.Saved[1].Cookie));
        Assert.Equal(ZhidaoAccountState.Unverified, store.Saved[1].State);

        clipboard = Cookie(1);  // 属于另一个已有账号，不能拿来更新这一个
        await list.UpdateCookieCommand.ExecuteAsync(null);
        Assert.Equal("fakebduss0009xyz", ZhidaoCookie.ReadBduss(store.Saved[1].Cookie));
        Assert.Contains("其他账号", list.Message);

        list.SetSelected(list.Accounts);
        Assert.False(list.UpdateCookieCommand.CanExecute(null));

        // 删除按勾选，不按鼠标选中：选中第 2 行、勾选第 1 行，删掉的是第 1 行。
        list.SetAllChecked(false);
        Assert.False(list.DeleteCommand.CanExecute(null));
        list.Accounts[0].IsEnabled = true;
        list.SetSelected([list.Accounts[1]]);
        Assert.Equal("删除 1 个", list.DeleteButtonText);
        await list.DeleteCommand.ExecuteAsync(null);
        Assert.Equal(["账号2"], list.Accounts.Select(row => row.Remark));
        Assert.Equal(["账号2"], store.Saved.Select(account => account.Remark));

        list.SetAllChecked(true);
        await list.DeleteCommand.ExecuteAsync(null);
        Assert.Empty(list.Accounts);
        Assert.Empty(store.Saved);
    }

    [Fact]
    public void LockedListRefusesEdits()
    {
        var list = new ZhidaoAccountListViewModel(new MemoryStore(Stored(1)), new ScriptedValidator(),
            pickFile: () => null, readClipboard: () => null);
        Assert.True(list.CanEdit);
        list.SetLocked(true);
        Assert.False(list.CanEdit);
        Assert.False(list.PasteCommand.CanExecute(null));
        Assert.False(list.ImportFileCommand.CanExecute(null));
        Assert.False(list.ValidateCommand.CanExecute(null));
    }

    [Fact]
    public async Task CollectingSendsEveryEnabledUsableAccountWithUniqueNames()
    {
        var expired = Stored(3) with { State = ZhidaoAccountState.Expired };
        var disabled = Stored(4) with { Enabled = false };
        var store = new MemoryStore(Stored(1) with { Remark = "同名" }, Stored(2) with { Remark = "同名" }, expired, disabled,
            Stored(5));
        var list = new ZhidaoAccountListViewModel(store, new ScriptedValidator());
        var process = new FakeCollectHelperProcess();
        var client = CollectHelperClient.Create(process);
        process.Emit(new { ev = "ready" });
        await client.WaitUntilReadyAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        var log = new RecordingRunLog();
        var model = new CollectViewModel(new FakeCollectHelperSession(client), action => action(), runLog: log,
            accountList: list);
        model.KeywordText = "云边有个小卖部";

        await model.StartCommand.ExecuteAsync(null);

        var command = JsonDocument.Parse(process.WrittenCommands().Last()).RootElement;
        var accounts = command.GetProperty("accounts").EnumerateArray().ToArray();
        Assert.Equal(["同名", "同名#2", "账号5"], accounts.Select(account => account.GetProperty("name").GetString()));
        Assert.Equal(["fakebduss0001xyz", "fakebduss0002xyz", "fakebduss0005xyz"],
            accounts.Select(account => ZhidaoCookie.ReadBduss(account.GetProperty("cookie").GetString()!)));
        Assert.True(model.IsCollecting);
        Assert.False(list.CanEdit);
        Assert.Contains(model.Logs, line => line.Contains("本次使用 3 个知道账号") && line.Contains("跳过已失效的 1 个"));
        Assert.DoesNotContain(log.Lines, line => line.Contains("fakebduss"));
        await model.DisposeAsync();
    }

    [Fact]
    public async Task NoUsableAccountStopsBeforeStartingTheHelper()
    {
        var store = new MemoryStore(Stored(1) with { State = ZhidaoAccountState.Expired });
        var session = new FakeCollectHelperSession(CollectHelperClient.Create(new FakeCollectHelperProcess()));
        var model = new CollectViewModel(session, action => action(),
            accountList: new ZhidaoAccountListViewModel(store, new ScriptedValidator()));
        model.KeywordText = "云边有个小卖部";
        await model.StartCommand.ExecuteAsync(null);
        Assert.Contains("都已失效", model.StatusMessage);
        Assert.Equal(0, session.ConnectCount);

        var empty = new CollectViewModel(session, action => action(),
            accountList: new ZhidaoAccountListViewModel(new MemoryStore(), new ScriptedValidator()));
        empty.KeywordText = "云边有个小卖部";
        await empty.StartCommand.ExecuteAsync(null);
        Assert.Contains("请先导入知道账号", empty.StatusMessage);
        Assert.Equal(0, session.ConnectCount);
    }

    [Fact]
    public void TenAccountsFitWellInsideTheHelperFrameLimit()
    {
        // 10 个真实长度（约 1.5 KB）的 Cookie 加 1 万个关键词，序列化后仍在 1 MiB 帧限制内。
        var accounts = Enumerable.Range(1, 10).Select(index => new Dictionary<string, string>
        {
            ["name"] = $"账号{index}",
            ["cookie"] = Cookie(index) + new string('x', 1400),
        }).ToArray();
        var keywords = Enumerable.Range(1, 10_000).Select(index => $"关键词{index:D5}").ToArray();
        var line = JsonSerializer.Serialize(new { cmd = "start", mode = "fuzzy", keywords, accounts, count = 100 });
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(line) < 1024 * 1024 / 2);
    }

    private static StoredZhidaoAccount Stored(int index) =>
        new(Guid.NewGuid(), $"账号{index}", Cookie(index), true, ZhidaoAccountState.Unverified, string.Empty, null);

    private sealed class MemoryStore(params StoredZhidaoAccount[] initial) : IZhidaoAccountStore
    {
        public List<StoredZhidaoAccount> Saved { get; private set; } = [.. initial];
        public ZhidaoAccountLoadResult Load() => new(Saved.ToArray(), null);
        public void Save(IReadOnlyList<StoredZhidaoAccount> accounts) => Saved = [.. accounts];
    }

    private sealed class ScriptedValidator : IZhidaoCookieValidator
    {
        public Dictionary<string, ZhidaoCookieValidationResult> Results { get; } = [];
        public HashSet<string> Throws { get; } = [];
        public List<string> Checked { get; } = [];

        public Task<ZhidaoCookieValidationResult> ValidateAsync(string cookie, CancellationToken cancellationToken)
        {
            var bduss = ZhidaoCookie.ReadBduss(cookie)!;
            Checked.Add(bduss);
            if (Throws.Contains(bduss)) throw new InvalidOperationException("boom");
            return Task.FromResult(Results.GetValueOrDefault(bduss, new(true, "ok")));
        }
    }

    private sealed class ScriptedDialogs(bool answer) : IDialogService
    {
        public List<string> Messages { get; } = [];

        public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default)
        {
            Messages.Add(message);
            return Task.FromResult(answer);
        }

        public Task ShowMessageAsync(string title, string message, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<string?> PickExcelFileAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
    }

    private sealed class RecordingRunLog : IRunLog
    {
        public List<string> Lines { get; } = [];
        public void Write(string source, string? message) => Lines.Add($"[{source}] {message}");
    }
}
