using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using BaiduBatchTransfer.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BaiduBatchTransfer.App.Features.Collect;

/// <summary>结果表格里的一行。</summary>
public sealed record CollectedRow(int Ordinal, string Name, string Link, string Pwd, string Source);

/// <summary>选择关键词文件；取消返回 null。</summary>
public interface IKeywordFileDialog
{
    string? Show();
}

/// <summary>按需提供一个已就绪的助手客户端，便于测试替换。</summary>
public interface ICollectHelperSession : IAsyncDisposable
{
    Task<CollectHelperClient> ConnectAsync(CancellationToken cancellationToken);
}

/// <summary>
/// 「知道采集」页面的视图模型。
///
/// 助手事件来自后台线程，这里统一经 <c>dispatch</c> 切回界面线程再改集合与属性；
/// 界面只读这些属性，不直接接触助手。
/// </summary>
public sealed class CollectViewModel : ObservableObject, IAsyncDisposable
{
    private const int MaximumKeywords = 10_000;
    private const string RunLogSource = "采集";
    private bool keywordLineMode;
    private bool keywordImportPending;
    private readonly ICollectHelperSession session;
    private readonly IKeywordFileDialog? keywordFileDialog;
    private readonly IZhidaoCookieValidator cookieValidator;
    private CancellationTokenSource? cookieValidationLifetime;
    private readonly Action<Action> dispatch;
    private CollectHelperClient? client;
    private CancellationTokenSource? lifetime;
    private string keywordText = string.Empty;
    private string cookieText = string.Empty;
    private bool isPreciseMode = true;
    private int targetCount = 50;
    private bool isBusy;
    private bool isCollecting;
    private bool isRiskPromptVisible;
    private string riskReason = string.Empty;
    private string statusMessage = "填入关键词与知道账号 Cookie，然后开始采集。";
    private int foundCount;
    private int targetTotal;
    private bool disposed;
    private readonly ICollectResultActions? resultActions;
    private readonly HashSet<CollectedRow> selectedResults = [];
    private bool resultActionBusy;
    private string resultActionMessage = string.Empty;
    private readonly ICollectPauseStore? pauseStore;
    // 本轮任务的参数；暂停后据此续采，而不是读取可能已被改动的输入框。
    private CollectPausedTask? currentTask;
    // 可以接着采的任务。续采运行期间也保留，直到这一轮自然采完或被停止。
    private CollectPausedTask? pausedTask;
    private bool isPaused;
    private bool pausePending;
    private readonly IRunLog? runLog;

    public CollectViewModel(ICollectHelperSession session, Action<Action>? dispatch = null,
        IKeywordFileDialog? keywordFileDialog = null, IZhidaoCookieValidator? cookieValidator = null,
        ICollectResultActions? resultActions = null, ICollectPauseStore? pauseStore = null,
        IRunLog? runLog = null)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.runLog = runLog;
        this.keywordFileDialog = keywordFileDialog;
        this.resultActions = resultActions;
        this.pauseStore = pauseStore;
        this.cookieValidator = cookieValidator ?? new ZhidaoCookieValidator();
        // 在创建页面的线程保存 Dispatcher；助手回调在线程池，那里没有 UI 上下文。
        var uiDispatcher = Dispatcher.FromThread(Thread.CurrentThread);
        var uiContext = SynchronizationContext.Current;
        this.dispatch = dispatch ?? (action =>
        {
            if (uiDispatcher is not null)
            {
                if (uiDispatcher.HasShutdownStarted) return;
                if (uiDispatcher.CheckAccess()) action();
                else uiDispatcher.BeginInvoke(action);
            }
            else if (uiContext is not null) uiContext.Post(_ => action(), null);
            else action();
        });

        StartCommand = new AsyncRelayCommand(StartAsync,
            () => !IsBusy && !IsCollecting && !IsPaused && !resultActionBusy);
        StopCommand = new AsyncRelayCommand(StopAsync, () => (IsCollecting || IsPaused) && !IsRiskPromptVisible);
        PauseResumeCommand = new AsyncRelayCommand(
            () => IsPaused ? ContinueAsync() : PauseAsync(),
            () => IsPaused
                ? !IsBusy && !IsCollecting && !resultActionBusy
                : IsCollecting && !IsRiskPromptVisible && !pausePending);
        ContinueAfterRiskCommand = new AsyncRelayCommand(() => AnswerRiskAsync(true), () => IsRiskPromptVisible);
        AbortAfterRiskCommand = new AsyncRelayCommand(() => AnswerRiskAsync(false), () => IsRiskPromptVisible);
        ImportKeywordsCommand = new AsyncRelayCommand(
            ImportKeywordsAsync, () => keywordFileDialog is not null && !IsBusy && !IsCollecting && !IsPaused);
        ValidateCookieCommand = new AsyncRelayCommand(ValidateCookieAsync, () => !IsBusy && !IsCollecting);
        CopyLinksCommand = new AsyncRelayCommand(() => RunResultActionAsync("复制链接", rows => resultActions!.CopyLinksAsync(rows)), CanUseResults);
        ExportResultsCommand = new AsyncRelayCommand(() => RunResultActionAsync("导出结果", rows => resultActions!.ExportAsync(rows)), CanUseResults);
        SendToTransferCommand = new AsyncRelayCommand(() => RunResultActionAsync("送入转存", rows => resultActions!.SendToTransferAsync(rows)), CanUseResults);
        Resources.CollectionChanged += (_, _) =>
        {
            selectedResults.IntersectWith(Resources);
            OnPropertyChanged(nameof(ResultScopeText));
            RefreshCommands();
        };
        RestorePausedTask();
    }

    /// <summary>软件重新打开后，把上次暂停的任务摆回界面，等用户填好 Cookie 再继续。</summary>
    private void RestorePausedTask()
    {
        if (pauseStore?.Load() is not { } task) return;
        pausedTask = currentTask = task;
        keywordLineMode = task.KeywordLineMode;
        keywordText = string.Join("\n", task.Keywords);
        isPreciseMode = task.Mode == "precise";
        if (!isPreciseMode) targetCount = task.Count;
        foundCount = task.Found;
        targetTotal = task.Target;
        isPaused = true;
        statusMessage = $"上次采集已暂停（暂停时已采 {task.Found}/{task.Target} 条）。" +
            "填入 Cookie 后点「继续」接着采，已采结果会重新列出；点「停止」结束该任务。";
    }

    public AsyncRelayCommand CopyLinksCommand { get; }
    public AsyncRelayCommand ExportResultsCommand { get; }
    public AsyncRelayCommand SendToTransferCommand { get; }
    public string ResultScopeText => selectedResults.Count > 0 ? $"已选 {selectedResults.Count} 条" : $"未选中，操作全部 {Resources.Count} 条";
    public string ResultActionMessage { get => resultActionMessage; private set => SetProperty(ref resultActionMessage, value); }

    public void SetSelectedResults(IEnumerable<CollectedRow> rows)
    {
        selectedResults.Clear();
        selectedResults.UnionWith(rows.Where(Resources.Contains));
        OnPropertyChanged(nameof(ResultScopeText));
    }

    private bool CanUseResults() => !disposed && resultActions is not null && Resources.Count > 0 &&
        !IsBusy && !IsCollecting && !resultActionBusy;

    private async Task RunResultActionAsync(string operation, Func<IReadOnlyList<CollectedRow>, Task<string>> action)
    {
        if (!CanUseResults()) return;
        var rows = Resources.Where(row => selectedResults.Count == 0 || selectedResults.Contains(row)).ToArray();
        resultActionBusy = true;
        ResultActionMessage = $"正在{operation} {rows.Length} 条结果…";
        RefreshCommands();
        try { ResultActionMessage = await action(rows); }
        catch (OperationCanceledException) { ResultActionMessage = $"{operation}已取消，采集结果仍保留。"; }
        catch (Exception error)
        {
            ResultActionMessage = $"{operation}失败，采集结果仍保留。请稍后重试。";
            AppendLog($"{operation}失败：{error.GetType().Name}（0x{error.HResult:X8}）。");
        }
        finally { resultActionBusy = false; RefreshCommands(); }
        AppendLog(ResultActionMessage);
    }

    /// <summary>每行一个关键词。</summary>
    public string KeywordText
    {
        get => keywordText;
        set => SetProperty(ref keywordText, value);
    }

    public string KeywordInputHint => keywordLineMode
        ? "已从文件导入：每行一个关键词，词内逗号会保留；编辑后仍按行采集。"
        : "每行一个关键词，也可以用逗号分隔";

    /// <summary>知道账号 Cookie。仅在本机进程间传递，不落盘、不写日志。</summary>
    public string CookieText
    {
        get => cookieText;
        set => SetProperty(ref cookieText, value);
    }

    /// <summary>精准模式：按词各采一条。</summary>
    public bool IsPreciseMode
    {
        get => isPreciseMode;
        set
        {
            if (!SetProperty(ref isPreciseMode, value)) return;
            OnPropertyChanged(nameof(IsFuzzyMode));
            OnPropertyChanged(nameof(CanEditTargetCount));
        }
    }

    /// <summary>模糊模式：采够目标条数为止。与 <see cref="IsPreciseMode"/> 互斥。</summary>
    public bool IsFuzzyMode
    {
        get => !isPreciseMode;
        set => IsPreciseMode = !value;
    }

    /// <summary>模糊模式的目标条数。</summary>
    public int TargetCount
    {
        get => targetCount;
        set => SetProperty(ref targetCount, value);
    }

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (SetProperty(ref isBusy, value)) RefreshCommands();
        }
    }

    public bool IsCollecting
    {
        get => isCollecting;
        private set
        {
            if (SetProperty(ref isCollecting, value)) RefreshCommands();
        }
    }

    /// <summary>
    /// 用户主动暂停后为真：进度已存进断点，可以接着采。与风控询问（<see cref="IsRiskPromptVisible"/>）无关。
    /// </summary>
    public bool IsPaused
    {
        get => isPaused;
        private set
        {
            if (!SetProperty(ref isPaused, value)) return;
            OnPropertyChanged(nameof(PauseButtonText));
            OnPropertyChanged(nameof(CanEditTask));
            OnPropertyChanged(nameof(CanEditTargetCount));
            RefreshCommands();
        }
    }

    public string PauseButtonText => IsPaused ? "继续" : "暂停";

    /// <summary>暂停期间锁住关键词与模式：续采沿用原任务，要改须先停止。</summary>
    public bool CanEditTask => !IsPaused;

    public bool CanEditTargetCount => IsFuzzyMode && !IsPaused;

    /// <summary>撞上风控时为真：采集侧正阻塞等待答复。</summary>
    public bool IsRiskPromptVisible
    {
        get => isRiskPromptVisible;
        private set
        {
            if (SetProperty(ref isRiskPromptVisible, value)) RefreshCommands();
        }
    }

    public string RiskReason
    {
        get => riskReason;
        private set => SetProperty(ref riskReason, value);
    }

    public string StatusMessage
    {
        get => statusMessage;
        private set
        {
            // 有些状态只显示在状态栏、不进界面日志（例如「暂停请求未送达」），落盘时一并记下。
            if (SetProperty(ref statusMessage, value)) runLog?.Write(RunLogSource, value);
        }
    }

    public int FoundCount
    {
        get => foundCount;
        private set => SetProperty(ref foundCount, value);
    }

    public int TargetTotal
    {
        get => targetTotal;
        private set => SetProperty(ref targetTotal, value);
    }

    public ObservableCollection<CollectedRow> Resources { get; } = [];

    public ObservableCollection<string> Logs { get; } = [];

    public AsyncRelayCommand StartCommand { get; }

    public AsyncRelayCommand StopCommand { get; }

    /// <summary>采集中是「暂停」，暂停后是「继续」。</summary>
    public AsyncRelayCommand PauseResumeCommand { get; }

    public AsyncRelayCommand ContinueAfterRiskCommand { get; }

    public AsyncRelayCommand AbortAfterRiskCommand { get; }

    /// <summary>从表格或文本批量导入关键词；文件格式由助手识别。</summary>
    public AsyncRelayCommand ImportKeywordsCommand { get; }

    /// <summary>验证知道账号 Cookie 是否有效。</summary>
    public AsyncRelayCommand ValidateCookieCommand { get; }

    /// <summary>是否提供了选择文件的能力（未提供时界面隐藏该按钮）。</summary>
    public bool CanImportKeywords => keywordFileDialog is not null;

    internal IReadOnlyList<string> ParseKeywords() =>
        KeywordText.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            // After file import keep line semantics across edits and undo operations.
            .SelectMany(line => keywordLineMode ? new[] { line } : line.Split(['，', ',']))
            .Select(word => word.Trim())
            .Where(word => word.Length > 0)
            .Distinct()
            .ToArray();

    private async Task StartAsync()
    {
        var keywords = ParseKeywords();
        if (keywords.Count == 0)
        {
            StatusMessage = "请先填入至少一个关键词。";
            return;
        }

        if (string.IsNullOrWhiteSpace(CookieText))
        {
            StatusMessage = "请先填入知道账号的 Cookie。";
            return;
        }

        IsBusy = true;
        try
        {
            Resources.Clear();
            ResultActionMessage = string.Empty;
            Logs.Clear();
            FoundCount = 0;
            TargetTotal = IsPreciseMode ? keywords.Count : Math.Max(0, TargetCount);
            StatusMessage = "正在启动采集助手…";
            AppendLog(StatusMessage);
            AppendLog($"开始采集：{(IsPreciseMode ? "精准" : "模糊")}模式，关键词 {keywords.Count} 个，目标 {TargetTotal} 条。");
            currentTask = new CollectPausedTask(IsPreciseMode ? "precise" : "fuzzy", keywords,
                IsPreciseMode ? 0 : Math.Max(1, TargetCount), 0, TargetTotal, keywordLineMode, DateTimeOffset.Now);

            var helper = await EnsureConnectedAsync().ConfigureAwait(true);

            IsCollecting = true;
            StatusMessage = "采集进行中…";
            await helper.StartCollectingAsync(ToRequest(currentTask, resume: false),
                lifetime?.Token ?? CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception error)
        {
            IsCollecting = false;
            StatusMessage = $"启动失败：{error.Message}";
            AppendLog(StatusMessage);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private CollectRequest ToRequest(CollectPausedTask task, bool resume) => new(
        task.Mode, task.Keywords, [new CollectAccount("知道账号", CookieText.Trim())],
        Count: task.Count, Resume: resume);

    private async Task PauseAsync()
    {
        if (client is null || !IsCollecting) return;
        pausePending = true;
        RefreshCommands();
        StatusMessage = "正在暂停，等待当前请求收尾…";
        AppendLog(StatusMessage);
        try
        {
            await client.PauseCollectingAsync(lifetime?.Token ?? CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception error)
        {
            pausePending = false;
            RefreshCommands();
            StatusMessage = $"暂停请求未送达：{error.Message}";
        }
    }

    private async Task ContinueAsync()
    {
        if (pausedTask is not { } task) return;
        if (string.IsNullOrWhiteSpace(CookieText))
        {
            StatusMessage = "请先填入知道账号的 Cookie，再继续采集。";
            return;
        }

        IsBusy = true;
        // 续采时列表以断点为准重建；没能发出去就把原来的行放回，不让已采结果从界面消失。
        var previousRows = Resources.ToArray();
        try
        {
            Resources.Clear();
            ResultActionMessage = string.Empty;
            FoundCount = 0;
            TargetTotal = task.Target;
            StatusMessage = "正在从断点继续采集…";
            AppendLog(StatusMessage);
            currentTask = task;

            var helper = await EnsureConnectedAsync().ConfigureAwait(true);

            IsPaused = false;
            IsCollecting = true;
            await helper.StartCollectingAsync(ToRequest(task, resume: true),
                lifetime?.Token ?? CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception error)
        {
            IsCollecting = false;
            IsPaused = true;
            if (Resources.Count == 0)
                foreach (var row in previousRows) Resources.Add(row);
            FoundCount = Math.Max(task.Found, Resources.Count);
            StatusMessage = $"继续失败，任务仍处于暂停：{error.Message}";
            AppendLog(StatusMessage);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>一轮采集没有正常进行下去时：原本可续采的任务回到暂停状态，否则只是结束。</summary>
    private void ReturnToPausedIfResumable()
    {
        pausePending = false;
        IsCollecting = false;
        IsPaused = pausedTask is not null;
    }

    private void RememberPause(int found)
    {
        pausedTask = (currentTask ?? pausedTask)! with { Found = found, PausedAt = DateTimeOffset.Now };
        try { pauseStore?.Save(pausedTask); }
        catch (Exception error)
        {
            AppendLog($"暂停记录未能保存（{error.GetType().Name}）：本次仍可继续，但关闭软件后无法接着采。");
        }
    }

    private void ForgetPause()
    {
        pausedTask = null;
        try { pauseStore?.Clear(); }
        catch (Exception error)
        {
            AppendLog($"暂停记录未能清除（{error.GetType().Name}）。");
        }
    }

    private async Task<CollectHelperClient> EnsureConnectedAsync()
    {
        // 如果 client 存在且进程还活着，直接返回
        if (client?.IsConnected == true) return client;

        // 清理旧连接（如果有）
        if (client is not null)
        {
            // 尝试发送 stop 命令清理 Python 端的残留状态（如果进程还活着）
            try
            {
                if (!client.HasExited)
                    await client.StopCollectingAsync(CancellationToken.None).ConfigureAwait(true);
            }
            catch { /* 进程可能已死，忽略错误 */ }

            client = null;
            lifetime?.Cancel();
            lifetime?.Dispose();
            lifetime = null;
        }

        lifetime = new CancellationTokenSource();
        var helper = await session.ConnectAsync(lifetime.Token).ConfigureAwait(true);
        client = helper;
        Attach(helper);   // 正常完成保留 client，每个助手只订阅一次。
        return helper;
    }

    private async Task ImportKeywordsAsync()
    {
        if (disposed || IsBusy || IsCollecting) return;
        var path = keywordFileDialog?.Show();
        if (string.IsNullOrWhiteSpace(path)) return;

        IsBusy = true;
        keywordImportPending = true;
        try
        {
            StatusMessage = "正在读取关键词文件…";
            var helper = await EnsureConnectedAsync().ConfigureAwait(true);
            await helper.ReadKeywordsAsync(path, lifetime?.Token ?? CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception error)
        {
            keywordImportPending = false;
            StatusMessage = $"读取失败：{error.Message}";
        }
        finally
        {
            if (!keywordImportPending) IsBusy = false;
        }
    }

    private void FinishKeywordImport()
    {
        if (!keywordImportPending) return;
        keywordImportPending = false;
        IsBusy = false;
    }

    private void ApplyImportedKeywords(CollectedKeywords loaded)
    {
        if (!keywordImportPending) return;
        try
        {
            if (loaded.Items.Count == 0)
            {
                StatusMessage = "文件里没有可用的关键词。";
                return;
            }
            var existing = ParseKeywords().ToList();
            var merged = new HashSet<string>(existing, StringComparer.Ordinal);
            var added = 0;
            var limited = loaded.Truncated;
            foreach (var value in loaded.Items)
            {
                var word = value.Trim();
                if (word.Length == 0 || merged.Contains(word)) continue;
                if (existing.Count >= MaximumKeywords) { limited = true; continue; }
                merged.Add(word);
                existing.Add(word);
                added++;
            }
            var source = string.IsNullOrWhiteSpace(loaded.Source) ? loaded.Encoding : loaded.Source;
            var total = Math.Max(loaded.Total, loaded.Items.Count);
            StatusMessage = (limited ? $"已达 {MaximumKeywords} 个上限，余下请分批采集。" : string.Empty) +
                $"已读取 {total} 个关键词，本次新增 {added} 个，当前共 {existing.Count} 个（{source}）。";
            keywordLineMode = true;
            OnPropertyChanged(nameof(KeywordInputHint));
            KeywordText = string.Join("\n", existing);
            AppendLog(StatusMessage);
        }
        finally { FinishKeywordImport(); }
    }

    private async Task ValidateCookieAsync()
    {
        if (IsBusy || IsCollecting) return;
        if (string.IsNullOrWhiteSpace(CookieText))
        {
            StatusMessage = "请先填入知道账号的 Cookie。";
            return;
        }

        var cookie = CookieText.Trim();
        using var cancellation = new CancellationTokenSource();
        cookieValidationLifetime = cancellation;
        IsBusy = true;
        try
        {
            StatusMessage = "正在验证 Cookie…";
            var result = await cookieValidator.ValidateAsync(cookie, cancellation.Token).ConfigureAwait(true);
            StatusMessage = CookieText.Trim() == cookie
                ? result.Message
                : "Cookie 已更改，请重新验证当前填写的 Cookie。";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Cookie 验证已取消。";
        }
        catch (Exception)
        {
            StatusMessage = "Cookie 验证未完成，无法确认登录状态，请稍后重试。";
        }
        finally
        {
            cookieValidationLifetime = null;
            IsBusy = false;
        }
    }

    private async Task StopAsync()
    {
        if (!IsCollecting && IsPaused)
        {
            // 暂停中没有在跑的任务，停止即放弃续采；列表里的结果照旧可用。
            ForgetPause();
            IsPaused = false;
            StatusMessage = $"已结束暂停的任务，共采到 {FoundCount} 条。";
            AppendLog(StatusMessage);
            return;
        }
        if (client is null) return;
        StatusMessage = "正在停止…";
        AppendLog(StatusMessage);
        try
        {
            await client.StopCollectingAsync(lifetime?.Token ?? CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception error)
        {
            StatusMessage = $"停止请求未送达：{error.Message}";
        }
    }

    private async Task AnswerRiskAsync(bool shouldContinue)
    {
        if (client is null) return;
        IsRiskPromptVisible = false;
        try
        {
            await client.AnswerRiskAsync(shouldContinue, lifetime?.Token ?? CancellationToken.None)
                .ConfigureAwait(true);
            StatusMessage = shouldContinue ? "已选择继续采集。" : "已选择停止采集。";
        }
        catch (Exception error)
        {
            StatusMessage = $"答复未送达：{error.Message}";
        }
    }

    private void Attach(CollectHelperClient helper)
    {
        void OnUi(Action action) => dispatch(() =>
        {
            if (!disposed && ReferenceEquals(client, helper)) action();
        });
        helper.ResourceCollected += resource => OnUi(() =>
        {
            Resources.Add(new CollectedRow(Resources.Count + 1, resource.Name, resource.Link,
                resource.Pwd, resource.Source));
            FoundCount = Resources.Count;
            // 断点交还的旧结果不逐条记日志，助手会汇总报一次。
            if (!resource.Restored) AppendLog($"已采到第 {FoundCount} 条：{resource.Name}");
        });
        helper.ProgressChanged += progress => OnUi(() =>
        {
            FoundCount = progress.Found;
            if (progress.Target > 0) TargetTotal = progress.Target;
        });
        helper.StatusChanged += text => OnUi(() =>
        {
            StatusMessage = text;
            AppendLog(text);
        });
        helper.LogWritten += text => OnUi(() => AppendLog(text));
        helper.RiskEncountered += prompt => OnUi(() =>
        {
            RiskReason = prompt.Reason;
            IsRiskPromptVisible = true;
            StatusMessage = "账号受限，采集在等你选择继续或停止。";
            AppendLog(StatusMessage);
        });
        helper.Failed += message => OnUi(() =>
        {
            AppendLog($"助手报告错误：{message}");
            StatusMessage = message;
            FinishKeywordImport();
        });
        helper.StartRejected += message => OnUi(() =>
        {
            IsRiskPromptVisible = false;
            StatusMessage = $"采集未能开始：{message}";
            ReturnToPausedIfResumable();
        });
        helper.ChannelBroken += message => OnUi(() =>
        {
            IsRiskPromptVisible = false;
            StatusMessage = $"采集助手已断开：{message}";
            AppendLog(StatusMessage);
            client = null;  // 清空死连接，下次重新启动
            lifetime?.Cancel();
            lifetime?.Dispose();
            lifetime = null;
            ReturnToPausedIfResumable();
            FinishKeywordImport();
        });
        helper.KeywordsLoaded += loaded => OnUi(() => ApplyImportedKeywords(loaded));
        helper.Completed += outcome => OnUi(() =>
        {
            IsRiskPromptVisible = false;
            FoundCount = outcome.Found;
            if (outcome.Paused)
            {
                RememberPause(outcome.Found);
                StatusMessage = $"已暂停，已采 {outcome.Found} 条，进度已保存。点「继续」接着采，点「停止」结束本次任务。";
            }
            else if (outcome.Failed && pausedTask is not null)
            {
                // 续采途中出错（例如 Cookie 失效）：断点还在，留着让用户处理后再继续。
                StatusMessage = $"采集中断，已采 {outcome.Found} 条，进度已保存。处理后可点「继续」。";
            }
            else
            {
                ForgetPause();
                StatusMessage = outcome.Stopped
                    ? $"已停止，共采到 {outcome.Found} 条。"
                    : $"采集完成，共采到 {outcome.Found} 条。";
            }
            AppendLog(StatusMessage);
            if (outcome.Stats.TryGetValue("no_pwd", out var missing) && missing > 0)
                AppendLog($"其中 {missing} 条没有提取码。");
            // done 只结束本轮任务；助手和事件订阅继续复用。
            ReturnToPausedIfResumable();
        });
    }

    private void AppendLog(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Logs.Add($"[{DateTime.Now:HH:mm:ss}] {text}");
        runLog?.Write(RunLogSource, text);
        while (Logs.Count > 500) Logs.RemoveAt(0);
    }

    private void RefreshCommands()
    {
        StartCommand.NotifyCanExecuteChanged();
        ImportKeywordsCommand.NotifyCanExecuteChanged();
        ValidateCookieCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        PauseResumeCommand.NotifyCanExecuteChanged();
        ContinueAfterRiskCommand.NotifyCanExecuteChanged();
        AbortAfterRiskCommand.NotifyCanExecuteChanged();
        CopyLinksCommand.NotifyCanExecuteChanged();
        ExportResultsCommand.NotifyCanExecuteChanged();
        SendToTransferCommand.NotifyCanExecuteChanged();
    }

    public async ValueTask DisposeAsync()
    {
        disposed = true;
        cookieValidationLifetime?.Cancel();
        lifetime?.Cancel();
        lifetime?.Dispose();
        lifetime = null;
        client = null;
        await session.DisposeAsync().ConfigureAwait(false);
    }
}
