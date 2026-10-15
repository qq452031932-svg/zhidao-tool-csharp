using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BaiduBatchTransfer.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BaiduBatchTransfer.App.Features.Collect;

/// <summary>知道 Cookie 文本的规范化、取 BDUSS 与脱敏显示。</summary>
public static class ZhidaoCookie
{
    public const int MaximumLength = 16 * 1024;

    /// <summary>去掉「Cookie:」前缀和首尾空白/分号；不是 key=value; 形式或含控制字符时返回 null。</summary>
    public static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = text.Trim();
        if (value.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase)) value = value[7..].Trim();
        value = value.Trim().TrimEnd(';', ' ').Trim();
        if (value.Length is 0 or > MaximumLength || value.Any(char.IsControl)) return null;
        var pairs = Pairs(value);
        return pairs.Count == 0 ? null : value;
    }

    /// <summary>看起来像 Cookie：至少一个 name=value，且含 BDUSS 或两个以上键值对。</summary>
    public static bool LooksLikeCookie(string field)
    {
        var pairs = Pairs(field.Trim().TrimEnd(';', ' '));
        return pairs.Count >= 2 || pairs.Any(pair => pair.Name == "BDUSS");
    }

    public static string? ReadBduss(string cookie) =>
        Pairs(cookie).FirstOrDefault(pair => pair.Name == "BDUSS").Value is { Length: > 0 } value ? value : null;

    /// <summary>表格里显示的 BDUSS 首尾各 4 位。</summary>
    public static string Mask(string cookie)
    {
        var bduss = ReadBduss(cookie);
        return bduss is null ? "（缺少 BDUSS）" : bduss.Length <= 8 ? "****" : $"{bduss[..4]}…{bduss[^4..]}";
    }

    public static string Describe(string cookie) =>
        $"BDUSS {Mask(cookie)}，共 {Pairs(cookie).Count} 项 Cookie。完整内容只在本机加密保存，不显示、不写日志。";

    private static List<(string Name, string Value)> Pairs(string cookie)
    {
        var result = new List<(string, string)>();
        foreach (var part in cookie.Split(';'))
        {
            var item = part.Trim();
            if (item.Length == 0) continue;
            var index = item.IndexOf('=');
            if (index <= 0) return [];
            var name = item[..index].Trim();
            if (!Regex.IsMatch(name, "^[A-Za-z0-9_\\-.]+$")) return [];
            result.Add((name, item[(index + 1)..].Trim()));
        }
        return result;
    }
}

public sealed record ParsedZhidaoAccount(int LineNumber, string Remark, string Cookie);

public sealed record ZhidaoImportIssue(int LineNumber, string Reason);

public sealed record ZhidaoAccountImportPreview(
    IReadOnlyList<ParsedZhidaoAccount> Accounts,
    IReadOnlyList<ZhidaoImportIssue> Duplicates,
    IReadOnlyList<ZhidaoImportIssue> Unrecognized)
{
    /// <summary>预览说明，只含行号、备注和原因，不含 Cookie。</summary>
    public string Describe()
    {
        var builder = new StringBuilder();
        builder.Append($"识别到 {Accounts.Count} 个可导入的账号");
        if (Accounts.Count > 0)
            builder.Append("：").Append(string.Join("、", Accounts.Take(10).Select(account => account.Remark)))
                .Append(Accounts.Count > 10 ? " 等" : string.Empty);
        builder.AppendLine("。");
        if (Duplicates.Count > 0)
            builder.AppendLine($"重复 {Duplicates.Count} 条（不导入）：" + Summarize(Duplicates));
        if (Unrecognized.Count > 0)
            builder.AppendLine($"无法识别 {Unrecognized.Count} 条（不导入）：" + Summarize(Unrecognized));
        builder.Append("每行中备注和 Cookie 以外的字段（例如密码）不会保存。");
        return builder.ToString();
    }

    private static string Summarize(IReadOnlyList<ZhidaoImportIssue> issues) =>
        string.Join("；", issues.Take(8).Select(issue => issue.LineNumber > 0 ? $"第 {issue.LineNumber} 行{issue.Reason}" : issue.Reason)) +
        (issues.Count > 8 ? $"；另有 {issues.Count - 8} 条" : string.Empty);
}

/// <summary>识别一行账号文本。新格式加一个识别器即可，无法识别的行在预览里列出。</summary>
public interface IZhidaoAccountLineRecognizer
{
    /// <returns>认出这一行时返回 true；cookie 未通过校验时给出 failure。</returns>
    bool TryRecognize(string line, out string remark, out string cookie, out string? failure);
}

/// <summary>「备注----其他字段----Cookie」，也接受制表符或竖线分隔；只取备注和 Cookie。</summary>
public sealed class SeparatedAccountLineRecognizer : IZhidaoAccountLineRecognizer
{
    private static readonly string[] Separators = ["----", "\t", "|"];

    public bool TryRecognize(string line, out string remark, out string cookie, out string? failure)
    {
        remark = cookie = string.Empty;
        failure = null;
        var separator = Separators.FirstOrDefault(line.Contains);
        if (separator is null) return false;
        var fields = line.Split(separator).Select(field => field.Trim()).ToArray();
        var cookieIndexes = fields.Select((field, index) => (field, index))
            .Where(item => ZhidaoCookie.LooksLikeCookie(item.field)).Select(item => item.index).ToArray();
        if (cookieIndexes.Length == 0) return false;
        if (cookieIndexes.Length > 1)
        {
            failure = "：一行里有多段像 Cookie 的内容，无法确定用哪一段";
            return true;
        }
        cookie = fields[cookieIndexes[0]];
        // 备注取 Cookie 之外第一个不是纯数字的字段；纯数字多半是密码或编号，不当备注、也不保存。
        var others = fields.Where((field, index) => index != cookieIndexes[0] && field.Length > 0).ToArray();
        remark = others.FirstOrDefault(field => !field.All(char.IsAsciiDigit)) ?? string.Empty;
        return true;
    }
}

/// <summary>「备注：Cookie」或「备注:Cookie」。备注里不能有等号，以免把 Cookie 当备注。</summary>
public sealed class LabeledAccountLineRecognizer : IZhidaoAccountLineRecognizer
{
    private static readonly Regex Pattern = new("^(?<remark>[^=;:：]{1,40})[:：]\\s*(?<cookie>.+)$");

    public bool TryRecognize(string line, out string remark, out string cookie, out string? failure)
    {
        remark = cookie = string.Empty;
        failure = null;
        var match = Pattern.Match(line);
        if (!match.Success || !ZhidaoCookie.LooksLikeCookie(match.Groups["cookie"].Value)) return false;
        remark = match.Groups["remark"].Value.Trim();
        // 「Cookie: …」是从浏览器复制请求头带出来的前缀，不是备注。
        if (remark.Equals("Cookie", StringComparison.OrdinalIgnoreCase)) return false;
        cookie = match.Groups["cookie"].Value;
        return true;
    }
}

/// <summary>整行就是一段 Cookie（从浏览器复制的请求头）。</summary>
public sealed class BareCookieLineRecognizer : IZhidaoAccountLineRecognizer
{
    public bool TryRecognize(string line, out string remark, out string cookie, out string? failure)
    {
        remark = string.Empty;
        cookie = line.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase) ? line[7..].Trim() : line;
        failure = null;
        return ZhidaoCookie.LooksLikeCookie(cookie);
    }
}

public static class ZhidaoAccountParser
{
    public const int MaximumRemarkLength = 40;

    public static IReadOnlyList<IZhidaoAccountLineRecognizer> DefaultRecognizers { get; } =
        [new SeparatedAccountLineRecognizer(), new LabeledAccountLineRecognizer(), new BareCookieLineRecognizer()];

    public static ZhidaoAccountImportPreview Parse(string? text, IEnumerable<string>? existingBduss = null,
        IReadOnlyList<IZhidaoAccountLineRecognizer>? recognizers = null)
    {
        recognizers ??= DefaultRecognizers;
        var accounts = new List<ParsedZhidaoAccount>();
        var duplicates = new List<ZhidaoImportIssue>();
        var unrecognized = new List<ZhidaoImportIssue>();
        var existing = new HashSet<string>(existingBduss ?? [], StringComparer.Ordinal);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var source = text ?? string.Empty;
        var trimmed = source.TrimStart('﻿').Trim();

        // 浏览器扩展导出的 JSON（[{name, value, ...}]）整体算一个账号。
        if (trimmed.StartsWith('[') && TryReadCookieJson(trimmed, out var jsonCookie))
            source = jsonCookie;

        var lines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var lineNumber = index + 1;
            var line = lines[index].Trim().TrimStart('﻿');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            string remark = string.Empty, rawCookie = string.Empty;
            string? failure = null;
            var recognized = false;
            foreach (var recognizer in recognizers)
            {
                if (!recognizer.TryRecognize(line, out remark, out rawCookie, out failure)) continue;
                recognized = true;
                break;
            }
            if (!recognized)
            {
                unrecognized.Add(new(lineNumber, "：认不出备注和 Cookie"));
                continue;
            }
            if (failure is not null)
            {
                unrecognized.Add(new(lineNumber, failure));
                continue;
            }
            var cookie = ZhidaoCookie.Normalize(rawCookie);
            if (cookie is null)
            {
                unrecognized.Add(new(lineNumber, "：Cookie 格式不对（应为 名称=值; 名称=值）"));
                continue;
            }
            var bduss = ZhidaoCookie.ReadBduss(cookie);
            if (bduss is null)
            {
                unrecognized.Add(new(lineNumber, "：缺少 BDUSS（百度知道登录必需）"));
                continue;
            }
            remark = remark.Length > MaximumRemarkLength ? remark[..MaximumRemarkLength] : remark;
            if (existing.Contains(bduss))
            {
                duplicates.Add(new(lineNumber, $"（{Label(remark)}）：账号列表里已有同一个 BDUSS"));
                continue;
            }
            if (seen.TryGetValue(bduss, out var firstLine))
            {
                duplicates.Add(new(lineNumber, $"（{Label(remark)}）：与第 {firstLine} 行是同一个 BDUSS"));
                continue;
            }
            seen[bduss] = lineNumber;
            accounts.Add(new(lineNumber, remark, cookie));
        }
        return new(accounts, duplicates, unrecognized);
    }

    private static string Label(string remark) => remark.Length > 0 ? remark : "无备注";

    private static bool TryReadCookieJson(string text, out string cookie)
    {
        cookie = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return false;
            var pairs = new List<string>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
                    !item.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.String)
                    return false;
                if (item.TryGetProperty("domain", out var domain) && domain.ValueKind == JsonValueKind.String &&
                    !domain.GetString()!.TrimStart('.').EndsWith("baidu.com", StringComparison.OrdinalIgnoreCase))
                    continue;
                pairs.Add($"{name.GetString()}={value.GetString()}");
            }
            if (pairs.Count == 0) return false;
            cookie = string.Join("; ", pairs);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

public enum ZhidaoAccountState
{
    Unverified,
    Checking,
    Valid,
    Expired,
    Unconfirmed,
}

public sealed record StoredZhidaoAccount(
    Guid Id,
    string Remark,
    string Cookie,
    bool Enabled,
    ZhidaoAccountState State,
    string StateDetail,
    DateTimeOffset? CheckedAt);

public sealed record ZhidaoAccountLoadResult(IReadOnlyList<StoredZhidaoAccount> Accounts, string? Problem);

public interface IZhidaoAccountStore
{
    ZhidaoAccountLoadResult Load();

    void Save(IReadOnlyList<StoredZhidaoAccount> accounts);
}

/// <summary>用 Windows 当前用户的 DPAPI 加密保存；换电脑或换 Windows 用户后读不出来。</summary>
public sealed class DpapiZhidaoAccountStore(string path) : IZhidaoAccountStore
{
    private static readonly byte[] Entropy = SHA256.HashData(Encoding.UTF8.GetBytes("BaiduBatchTransfer/ZhidaoAccounts/v1"));
    private readonly string path = Path.GetFullPath(path);

    public ZhidaoAccountLoadResult Load()
    {
        if (!File.Exists(path)) return new([], null);
        byte[]? plaintext = null;
        try
        {
            plaintext = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            var payload = JsonSerializer.Deserialize<Payload>(plaintext);
            if (payload?.Version != 1 || payload.Accounts is null) throw new JsonException("unsupported");
            var accounts = payload.Accounts
                .Where(account => ZhidaoCookie.Normalize(account.Cookie) is not null)
                .Select(account => account with
                {
                    State = account.State == ZhidaoAccountState.Checking ? ZhidaoAccountState.Unverified : account.State,
                    Remark = account.Remark ?? string.Empty,
                    StateDetail = account.StateDetail ?? string.Empty,
                }).ToArray();
            return new(accounts, null);
        }
        catch (Exception error) when (error is CryptographicException or JsonException or IOException
            or UnauthorizedAccessException or NotSupportedException)
        {
            // 读不出来时把原文件挪开，不让下一次保存把它覆盖掉。
            var aside = path + $".unreadable-{DateTime.Now:yyyyMMddHHmmss}";
            try { File.Move(path, aside); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return new([], "保存的知道账号无法读取（可能来自其他电脑或 Windows 用户），请重新导入。");
        }
        finally
        {
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public void Save(IReadOnlyList<StoredZhidaoAccount> accounts)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(new Payload(1, accounts.ToArray()));
        try
        {
            var ciphertext = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllBytes(temporary, ciphertext);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private sealed record Payload(int Version, StoredZhidaoAccount[] Accounts);
}

/// <summary>账号表格的一行。Cookie 只在本机进程里，界面只显示脱敏后的样子。</summary>
public sealed class ZhidaoAccountRow : ObservableObject
{
    private readonly Action changed;
    private bool isEnabled;
    private ZhidaoAccountState state;
    private string stateDetail;
    private DateTimeOffset? checkedAt;
    private string cookie;

    internal ZhidaoAccountRow(StoredZhidaoAccount stored, Action changed)
    {
        Id = stored.Id;
        Remark = stored.Remark;
        cookie = stored.Cookie;
        isEnabled = stored.Enabled;
        state = stored.State;
        stateDetail = stored.StateDetail;
        checkedAt = stored.CheckedAt;
        this.changed = changed;
    }

    public Guid Id { get; }
    public string Remark { get; }
    public string Cookie => cookie;
    public string MaskedCookie => ZhidaoCookie.Mask(cookie);
    public string CookieDetail => ZhidaoCookie.Describe(cookie);

    /// <summary>勾选 = 参与采集。</summary>
    public bool IsEnabled
    {
        get => isEnabled;
        set { if (SetProperty(ref isEnabled, value)) changed(); }
    }

    public ZhidaoAccountState State
    {
        get => state;
        private set { if (SetProperty(ref state, value)) OnPropertyChanged(nameof(StateText)); }
    }

    public string StateText => State switch
    {
        ZhidaoAccountState.Checking => "验证中",
        ZhidaoAccountState.Valid => "可用",
        ZhidaoAccountState.Expired => "已失效",
        ZhidaoAccountState.Unconfirmed => "暂时无法验证",
        _ => "未验证",
    };

    public string StateDetail
    {
        get => stateDetail.Length == 0 && checkedAt is null ? "导入后还没验证。" :
            checkedAt is { } time ? $"{stateDetail}\n验证时间：{time.LocalDateTime:yyyy-MM-dd HH:mm:ss}" : stateDetail;
    }

    internal void MarkChecking()
    {
        State = ZhidaoAccountState.Checking;
        OnPropertyChanged(nameof(StateDetail));
    }

    internal void Apply(ZhidaoAccountState newState, string detail, DateTimeOffset? at)
    {
        stateDetail = detail;
        checkedAt = at;
        State = newState;
        OnPropertyChanged(nameof(StateDetail));
    }

    internal void ReplaceCookie(string newCookie)
    {
        cookie = newCookie;
        OnPropertyChanged(nameof(Cookie));
        OnPropertyChanged(nameof(MaskedCookie));
        OnPropertyChanged(nameof(CookieDetail));
        Apply(ZhidaoAccountState.Unverified, "Cookie 已更新，还没验证。", null);
    }

    internal StoredZhidaoAccount ToStored() =>
        new(Id, Remark, cookie, isEnabled, State == ZhidaoAccountState.Checking ? ZhidaoAccountState.Unverified : State,
            stateDetail, checkedAt);
}

/// <summary>采集页上的知道账号列表：导入、勾选、验证、更新、删除，变更即加密保存。</summary>
public sealed class ZhidaoAccountListViewModel : ObservableObject
{
    private const string RunLogSource = "采集";
    private readonly IZhidaoAccountStore store;
    private readonly IZhidaoCookieValidator validator;
    private readonly IDialogService? dialogs;
    private readonly Func<string?>? pickFile;
    private readonly Func<string?>? readClipboard;
    private readonly IRunLog? runLog;
    private readonly Func<CancellationToken, Task> validationDelay;
    private readonly List<ZhidaoAccountRow> selected = [];
    private CancellationTokenSource? validationLifetime;
    private bool isBusy;
    private bool isLocked;
    private bool suppressSave;
    private string message = string.Empty;

    public ZhidaoAccountListViewModel(IZhidaoAccountStore store, IZhidaoCookieValidator validator,
        IDialogService? dialogs = null, Func<string?>? pickFile = null, Func<string?>? readClipboard = null,
        IRunLog? runLog = null, Func<CancellationToken, Task>? validationDelay = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.validator = validator ?? throw new ArgumentNullException(nameof(validator));
        this.dialogs = dialogs;
        this.pickFile = pickFile;
        this.readClipboard = readClipboard;
        this.runLog = runLog;
        // 逐个验证之间留一点间隔，不在同一秒里连发多个账号请求。
        this.validationDelay = validationDelay ?? (token => Task.Delay(TimeSpan.FromSeconds(1), token));
        ImportFileCommand = new AsyncRelayCommand(ImportFileAsync, () => CanEdit && pickFile is not null);
        PasteCommand = new AsyncRelayCommand(PasteAsync, () => CanEdit && readClipboard is not null);
        ValidateCommand = new AsyncRelayCommand(ValidateAsync, () => CanEdit && Accounts.Count > 0);
        // 勾选框即「选中」：删除、验证、参与采集都按勾选；右键更新 Cookie 只针对右键指向的那一行。
        DeleteCommand = new AsyncRelayCommand(DeleteCheckedAsync, () => CanEdit && CheckedCount > 0);
        UpdateCookieCommand = new AsyncRelayCommand(UpdateCookieFromClipboardAsync,
            () => CanEdit && selected.Count == 1 && readClipboard is not null);
        Accounts.CollectionChanged += (_, _) => { NotifySummary(); Refresh(); };

        var loaded = store.Load();
        suppressSave = true;
        foreach (var account in loaded.Accounts) Accounts.Add(new ZhidaoAccountRow(account, OnRowChanged));
        suppressSave = false;
        if (loaded.Problem is not null) Message = loaded.Problem;
    }

    public ObservableCollection<ZhidaoAccountRow> Accounts { get; } = [];
    public AsyncRelayCommand ImportFileCommand { get; }
    public AsyncRelayCommand PasteCommand { get; }
    public AsyncRelayCommand ValidateCommand { get; }
    public AsyncRelayCommand DeleteCommand { get; }
    public AsyncRelayCommand UpdateCookieCommand { get; }

    public string Message { get => message; private set => SetProperty(ref message, value); }

    public string Summary => Accounts.Count == 0
        ? "还没有账号：导入 TXT 或粘贴 Cookie"
        : $"勾选 {Accounts.Count(row => row.IsEnabled)} / 共 {Accounts.Count} 个，" +
          $"可用 {Accounts.Count(row => row.State == ZhidaoAccountState.Valid)}" +
          (Accounts.Any(row => row.State == ZhidaoAccountState.Expired)
              ? $"，已失效 {Accounts.Count(row => row.State == ZhidaoAccountState.Expired)}" : string.Empty);

    public int CheckedCount => Accounts.Count(row => row.IsEnabled);

    /// <summary>没勾或全勾时验证全部，只勾了一部分时验证勾选的。</summary>
    public string ValidateButtonText => CheckedCount == 0 || CheckedCount == Accounts.Count
        ? "验证全部" : $"验证 {CheckedCount} 个";

    public string DeleteButtonText => CheckedCount > 0 ? $"删除 {CheckedCount} 个" : "删除";

    /// <summary>右键「用剪贴板更新」针对的那一行。</summary>
    public ZhidaoAccountRow? UpdateTarget => selected.Count == 1 ? selected[0] : null;

    public void SetAllChecked(bool value)
    {
        if (!CanEdit) return;
        suppressSave = true;
        foreach (var row in Accounts) row.IsEnabled = value;
        suppressSave = false;
        TrySave();
        NotifySummary();
    }

    /// <summary>采集或暂停期间锁住：正在跑的任务用的是开始时的账号。</summary>
    public bool CanEdit => !isBusy && !isLocked;

    public bool IsBusy
    {
        get => isBusy;
        private set { if (SetProperty(ref isBusy, value)) Refresh(); }
    }

    public void SetLocked(bool locked)
    {
        if (isLocked == locked) return;
        isLocked = locked;
        Refresh();
    }

    public void SetSelected(IEnumerable<ZhidaoAccountRow> rows)
    {
        selected.Clear();
        selected.AddRange(rows.Where(Accounts.Contains));
        OnPropertyChanged(nameof(UpdateTarget));
        Refresh();
    }

    /// <summary>本次采集用的账号：勾选且没被明确判为失效的。</summary>
    public IReadOnlyList<ZhidaoAccountRow> AccountsForCollecting(out int skippedExpired)
    {
        var enabled = Accounts.Where(row => row.IsEnabled).ToArray();
        skippedExpired = enabled.Count(row => row.State == ZhidaoAccountState.Expired);
        return enabled.Where(row => row.State != ZhidaoAccountState.Expired).ToArray();
    }

    public void CancelWork() => validationLifetime?.Cancel();

    private async Task ImportFileAsync()
    {
        var path = pickFile?.Invoke();
        if (string.IsNullOrWhiteSpace(path)) return;
        string text;
        try { text = await Task.Run(() => ReadText(path)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            Message = $"读取账号文件失败（{error.GetType().Name}），请确认文件存在且没有被占用。";
            return;
        }
        await ImportTextAsync(text, "文件");
    }

    private Task PasteAsync()
    {
        string? text;
        try { text = readClipboard?.Invoke(); }
        catch (Exception error) when (error is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            Message = "读取剪贴板失败，请重新复制后再试。";
            return Task.CompletedTask;
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            Message = "剪贴板里没有文字。请先复制账号文本或 Cookie，再点「粘贴导入」。";
            return Task.CompletedTask;
        }
        return ImportTextAsync(text, "剪贴板");
    }

    internal async Task ImportTextAsync(string text, string source)
    {
        var existing = Accounts.Select(row => ZhidaoCookie.ReadBduss(row.Cookie)).OfType<string>();
        var preview = ZhidaoAccountParser.Parse(text, existing);
        if (preview.Accounts.Count == 0)
        {
            Message = "没有可导入的账号。" + preview.Describe().Replace("识别到 0 个可导入的账号。", string.Empty).Trim();
            return;
        }
        if (dialogs is not null && !await dialogs.ConfirmAsync("导入知道账号", preview.Describe() + "\n\n确定导入吗？"))
        {
            Message = "已取消导入。";
            return;
        }
        suppressSave = true;
        foreach (var account in preview.Accounts)
        {
            var remark = account.Remark.Length > 0 ? account.Remark : $"账号{Accounts.Count + 1}";
            Accounts.Add(new ZhidaoAccountRow(new StoredZhidaoAccount(Guid.NewGuid(), remark, account.Cookie, true,
                ZhidaoAccountState.Unverified, string.Empty, null), OnRowChanged));
        }
        suppressSave = false;
        if (!TrySave()) return;
        Message = $"已从{source}导入 {preview.Accounts.Count} 个账号" +
            (preview.Duplicates.Count + preview.Unrecognized.Count > 0
                ? $"，跳过重复 {preview.Duplicates.Count} 条、无法识别 {preview.Unrecognized.Count} 条" : string.Empty) +
            "。建议点「验证全部」确认登录状态。";
        runLog?.Write(RunLogSource, Message);
    }

    private async Task ValidateAsync()
    {
        var rows = CheckedCount == 0 ? Accounts.ToArray() : Accounts.Where(row => row.IsEnabled).ToArray();
        using var cancellation = new CancellationTokenSource();
        validationLifetime = cancellation;
        IsBusy = true;
        var done = 0;
        try
        {
            foreach (var row in rows)
            {
                if (cancellation.IsCancellationRequested) break;
                if (done > 0)
                {
                    try { await validationDelay(cancellation.Token); }
                    catch (OperationCanceledException) { break; }
                }
                Message = $"正在验证 {done + 1}/{rows.Length}：{row.Remark}…";
                row.MarkChecking();
                ZhidaoCookieValidationResult result;
                try { result = await validator.ValidateAsync(row.Cookie, cancellation.Token); }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    row.Apply(ZhidaoAccountState.Unverified, "验证被中断。", null);
                    break;
                }
                catch (Exception error)
                {
                    result = new(false, $"验证出错（{error.GetType().Name}），未能确认登录状态。");
                }
                var state = result.IsValid ? ZhidaoAccountState.Valid
                    : result.DefinitelyExpired ? ZhidaoAccountState.Expired : ZhidaoAccountState.Unconfirmed;
                row.Apply(state, result.Message, DateTimeOffset.Now);
                runLog?.Write(RunLogSource, $"知道账号「{row.Remark}」验证：{row.StateText}");
                done++;
            }
        }
        finally
        {
            validationLifetime = null;
            IsBusy = false;
            TrySave();
            NotifySummary();
        }
        Message = $"验证完成 {done}/{rows.Length} 个：" +
            $"可用 {rows.Count(row => row.State == ZhidaoAccountState.Valid)}，" +
            $"已失效 {rows.Count(row => row.State == ZhidaoAccountState.Expired)}，" +
            $"暂时无法验证 {rows.Count(row => row.State == ZhidaoAccountState.Unconfirmed)}。" +
            (rows.Any(row => row.State == ZhidaoAccountState.Expired) ? "已失效的账号开始采集时会自动跳过。" : string.Empty);
    }

    private async Task DeleteCheckedAsync()
    {
        var rows = Accounts.Where(row => row.IsEnabled).ToArray();
        if (rows.Length == 0) return;
        if (dialogs is not null && !await dialogs.ConfirmAsync("删除知道账号",
                $"删除勾选的 {rows.Length} 个账号（{string.Join("、", rows.Take(5).Select(row => row.Remark))}{(rows.Length > 5 ? " 等" : string.Empty)}）？\n删除后本机不再保存它们的 Cookie。"))
            return;
        suppressSave = true;
        foreach (var row in rows) Accounts.Remove(row);
        suppressSave = false;
        selected.RemoveAll(row => !Accounts.Contains(row));
        OnPropertyChanged(nameof(UpdateTarget));
        if (TrySave()) Message = $"已删除 {rows.Length} 个账号。";
    }

    private Task UpdateCookieFromClipboardAsync()
    {
        if (selected.Count != 1) return Task.CompletedTask;
        var row = selected[0];
        string? text;
        try { text = readClipboard?.Invoke(); }
        catch (Exception error) when (error is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            Message = "读取剪贴板失败，请重新复制后再试。";
            return Task.CompletedTask;
        }
        var others = Accounts.Where(item => item != row).Select(item => ZhidaoCookie.ReadBduss(item.Cookie)).OfType<string>();
        var preview = ZhidaoAccountParser.Parse(text, others);
        if (preview.Accounts.Count != 1)
        {
            Message = preview.Accounts.Count == 0
                ? "剪贴板里没有可用的 Cookie（需要含 BDUSS），或它属于列表里的其他账号。"
                : "剪贴板里有多个账号，更新时一次只能粘贴一个账号的 Cookie。";
            return Task.CompletedTask;
        }
        row.ReplaceCookie(preview.Accounts[0].Cookie);
        if (TrySave()) Message = $"已更新「{row.Remark}」的 Cookie，请重新验证。";
        return Task.CompletedTask;
    }

    private void OnRowChanged()
    {
        NotifySummary();
        if (!suppressSave) TrySave();
    }

    private bool TrySave()
    {
        try
        {
            store.Save(Accounts.Select(row => row.ToStored()).ToArray());
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException)
        {
            Message = $"账号未能保存到本机（{error.GetType().Name}）：本次仍可使用，关闭软件后需要重新导入。";
            return false;
        }
    }

    private static string ReadText(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > 4 * 1024 * 1024) throw new IOException("文件过大");
        var bytes = File.ReadAllBytes(path);
        try { return new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿'); }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding("GB18030").GetString(bytes);
        }
    }

    private void NotifySummary()
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(CheckedCount));
        OnPropertyChanged(nameof(ValidateButtonText));
        OnPropertyChanged(nameof(DeleteButtonText));
        DeleteCommand.NotifyCanExecuteChanged();
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(CanEdit));
        ImportFileCommand.NotifyCanExecuteChanged();
        PasteCommand.NotifyCanExecuteChanged();
        ValidateCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        UpdateCookieCommand.NotifyCanExecuteChanged();
    }
}
