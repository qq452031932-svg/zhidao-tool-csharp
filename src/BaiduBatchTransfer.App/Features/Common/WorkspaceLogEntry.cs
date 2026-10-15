using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace BaiduBatchTransfer.App.Features.Common;

public enum WorkspaceLogLevel
{
    Information,
    Success,
    Warning,
    Error,
}

public sealed record WorkspaceLogEntry
{
    public WorkspaceLogEntry(
        DateTimeOffset timestamp,
        WorkspaceLogLevel? level,
        string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Timestamp = timestamp;
        Message = WorkspaceLogBuffer.SanitizeForEntry(message.Trim());
        Level = level ?? WorkspaceLogBuffer.Classify(Message);
    }

    public DateTimeOffset Timestamp { get; }
    public DateTimeOffset LocalTimestamp => Timestamp.ToLocalTime();
    public WorkspaceLogLevel Level { get; }
    public string LevelText => Level switch
    {
        WorkspaceLogLevel.Information => "信息",
        WorkspaceLogLevel.Success => "成功",
        WorkspaceLogLevel.Warning => "警告",
        WorkspaceLogLevel.Error => "错误",
        _ => throw new ArgumentOutOfRangeException(nameof(Level)),
    };
    public string Message { get; }

    public override string ToString() => $"{Timestamp:O} [{Level}]";
}

public sealed class WorkspaceLogBuffer
{
    private const int Capacity = 100;
    private const string HiddenSensitiveValue = "[敏感信息已隐藏]";
    private const string HiddenLocalPath = "[本机路径已隐藏]";
    private const string HiddenBaiduShareUrl = "[百度网盘链接已隐藏]";

    private static readonly Regex BaiduShareUrlPattern = new(
        @"https?://(?:pan|yun)\.baidu\.com(?::443)?/[^\s\""'<>，。；;！？、）》】\]]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex CookieHeaderPattern = new(
        @"\b(?:cookie|set-cookie)\b\s*[:=：]\s*[^\r\n]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex AuthorizationHeaderPattern = new(
        @"\bauthorization\b\s*[:=：]\s*(?:bearer\s+)?[^\s,，;；。！？]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex NamedSecretPattern = new(
        @"(?<label>\b(?:access[_ -]?token|refresh[_ -]?token|confirmation[_ -]?token|token|bduss|stoken|profile(?:[_ -]?(?:id|name|path))?|draft[_ -]?fingerprint|fingerprint|pwd|passcode|extraction[_ -]?code)\b|提取码|访问码)" +
        @"(?<separator>\s*[:=：]\s*|\s+)(?<value>\""[^\""\r\n]*\""|'[^'\r\n]*'|“[^”\r\n]*”|‘[^’\r\n]*’|[^,，;；。！？\r\n]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex LeaseSecretPattern = new(
        @"(?<label>\b(?:lease(?:[_ -]?id)?|write[_ -]?lease(?:[_ -]?id)?)\b)\s*[:=：]\s*" +
        @"(?<value>\""[^\""\r\n]*\""|'[^'\r\n]*'|“[^”\r\n]*”|‘[^’\r\n]*’|[^,，;；。！？\r\n]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex QuotedLocalPathPattern = new(
        @"(?<open>[\""'“‘])(?<path>(?:[A-Za-z]:[\\/]|\\\\|/(?:Users|home|var|tmp|private)/)[^\""'“”‘’\r\n]+)(?<close>[\""'”’])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex LocalPathPattern = new(
        @"(?<![\w])(?:(?:[A-Za-z]:[\\/]|\\\\)[^,，;；。！？、）》】\r\n\""'“”‘’]+|/(?:Users|home|var|tmp|private)/[^\s,，;；。！？、）》】]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex TransferProgressPattern = new(
        @"^进度 [\d,]+/[\d,]+：成功 [\d,]+，失败 [\d,]+(?:，已取消 (?<cancelled>[\d,]+))?，剩余 [\d,]+。$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly ObservableCollection<WorkspaceLogEntry> entries = [];
    private readonly TimeProvider timeProvider;
    private readonly Action<WorkspaceLogEntry>? appended;
    private WorkspaceLogEntry? lastAppendedEntry;

    /// <param name="appended">每追加一条就通知一次，用于把界面日志同时落盘；清空界面日志不影响它。</param>
    public WorkspaceLogBuffer(TimeProvider? timeProvider = null, Action<WorkspaceLogEntry>? appended = null)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.appended = appended;
        Entries = new ReadOnlyObservableCollection<WorkspaceLogEntry>(entries);
    }

    public ReadOnlyObservableCollection<WorkspaceLogEntry> Entries { get; }

    public void Clear()
    {
        entries.Clear();
        lastAppendedEntry = null;
    }

    /// <param name="persist">false 时只显示在界面上，不通知落盘（发出方已经自己写过运行日志）。</param>
    public void AppendSafe(
        string? message,
        WorkspaceLogLevel? level = null,
        DateTimeOffset? timestamp = null,
        bool persist = true)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        var entry = new WorkspaceLogEntry(
            timestamp ?? timeProvider.GetUtcNow(),
            level,
            message.Trim());

        if (lastAppendedEntry is not null &&
            string.Equals(lastAppendedEntry.Message, entry.Message, StringComparison.Ordinal))
        {
            var duplicateIndex = IndexOfReference(lastAppendedEntry);
            if (duplicateIndex >= 0) entries.RemoveAt(duplicateIndex);
        }

        InsertSorted(entry);
        lastAppendedEntry = entry;
        while (entries.Count > Capacity)
        {
            var removed = entries[0];
            entries.RemoveAt(0);
            if (ReferenceEquals(removed, lastAppendedEntry)) lastAppendedEntry = null;
        }
        if (persist) appended?.Invoke(entry);
    }

    internal static string SanitizeForEntry(string message)
    {
        var safe = BaiduShareUrlPattern.Replace(message, HiddenBaiduShareUrl);
        safe = CookieHeaderPattern.Replace(safe, "Cookie：" + HiddenSensitiveValue);
        safe = AuthorizationHeaderPattern.Replace(safe, "Authorization：" + HiddenSensitiveValue);
        safe = LeaseSecretPattern.Replace(safe, match =>
            match.Groups["label"].Value + "：" + HiddenSensitiveValue);
        safe = NamedSecretPattern.Replace(safe, match =>
            match.Groups["label"].Value + "：" + HiddenSensitiveValue);
        safe = QuotedLocalPathPattern.Replace(safe, match =>
            match.Groups["open"].Value + HiddenLocalPath + match.Groups["close"].Value);
        return LocalPathPattern.Replace(safe, HiddenLocalPath);
    }

    internal static WorkspaceLogLevel Classify(string message)
    {
        // 进度行里的失败是累计数：一条失败后后面每行都会带「失败 1」，不能每行都标红（用户 10-15 反馈）。
        // 真正出错的那一刻由「第 N 条转存失败：原因」单独一行标红。
        var progressMatch = TransferProgressPattern.Match(message);
        if (progressMatch.Success)
            return progressMatch.Groups["cancelled"].Success ? WorkspaceLogLevel.Warning : WorkspaceLogLevel.Information;

        if (ContainsAny(message, "失败", "错误", "无法", "未能", "不可用", "异常"))
            return WorkspaceLogLevel.Error;
        if (ContainsAny(message, "请", "冲突", "无效", "缺少", "取消", "尚未", "尚待", "暂", "等待", "待处理", "需要确认", "未取得", "未获得", "not acquired"))
            return WorkspaceLogLevel.Warning;
        if (ContainsAny(message, "完成", "成功", "已加入", "已加载", "已选择", "已保存", "已创建", "已更新", "已显示"))
            return WorkspaceLogLevel.Success;
        return WorkspaceLogLevel.Information;
    }

    private static bool ContainsAny(string message, params string[] values) =>
        values.Any(value => message.Contains(value, StringComparison.OrdinalIgnoreCase));

    private int IndexOfReference(WorkspaceLogEntry target)
    {
        for (var index = 0; index < entries.Count; index++)
            if (ReferenceEquals(entries[index], target)) return index;
        return -1;
    }

    private void InsertSorted(WorkspaceLogEntry entry)
    {
        var index = 0;
        while (index < entries.Count && entries[index].Timestamp <= entry.Timestamp) index++;
        entries.Insert(index, entry);
    }
}
