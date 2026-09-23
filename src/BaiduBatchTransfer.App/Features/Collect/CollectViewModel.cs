using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BaiduBatchTransfer.App.Features.Collect;

/// <summary>结果表格里的一行。</summary>
public sealed record CollectedRow(int Ordinal, string Name, string Link, string Pwd, string Source);

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
    private readonly ICollectHelperSession session;
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

    public CollectViewModel(ICollectHelperSession session, Action<Action>? dispatch = null)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        var uiContext = SynchronizationContext.Current;
        this.dispatch = dispatch ?? (action =>
        {
            if (uiContext is null) action();
            else uiContext.Post(_ => action(), null);
        });

        StartCommand = new AsyncRelayCommand(StartAsync, () => !IsBusy && !IsCollecting);
        StopCommand = new AsyncRelayCommand(StopAsync, () => IsCollecting && !IsRiskPromptVisible);
        ContinueAfterRiskCommand = new AsyncRelayCommand(() => AnswerRiskAsync(true), () => IsRiskPromptVisible);
        AbortAfterRiskCommand = new AsyncRelayCommand(() => AnswerRiskAsync(false), () => IsRiskPromptVisible);
    }

    /// <summary>每行一个关键词。</summary>
    public string KeywordText
    {
        get => keywordText;
        set => SetProperty(ref keywordText, value);
    }

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
            if (SetProperty(ref isPreciseMode, value)) OnPropertyChanged(nameof(IsFuzzyMode));
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
        private set => SetProperty(ref statusMessage, value);
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

    public AsyncRelayCommand ContinueAfterRiskCommand { get; }

    public AsyncRelayCommand AbortAfterRiskCommand { get; }

    internal IReadOnlyList<string> ParseKeywords() =>
        KeywordText.Split(['\n', '\r', '，', ','], StringSplitOptions.RemoveEmptyEntries)
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
            Logs.Clear();
            FoundCount = 0;
            TargetTotal = IsPreciseMode ? keywords.Count : Math.Max(0, TargetCount);
            StatusMessage = "正在启动采集助手…";

            lifetime = new CancellationTokenSource();
            client = await session.ConnectAsync(lifetime.Token).ConfigureAwait(true);
            Attach(client);

            IsCollecting = true;
            StatusMessage = "采集进行中…";
            await client.StartCollectingAsync(new CollectRequest(
                IsPreciseMode ? "precise" : "fuzzy",
                keywords,
                [new CollectAccount("知道账号", CookieText.Trim())],
                Count: IsPreciseMode ? 0 : Math.Max(1, TargetCount)), lifetime.Token).ConfigureAwait(true);
        }
        catch (Exception error)
        {
            IsCollecting = false;
            StatusMessage = $"启动失败：{error.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task StopAsync()
    {
        if (client is null) return;
        StatusMessage = "正在停止…";
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
        helper.ResourceCollected += resource => dispatch(() =>
        {
            Resources.Add(new CollectedRow(Resources.Count + 1, resource.Name, resource.Link,
                resource.Pwd, resource.Source));
        });
        helper.ProgressChanged += progress => dispatch(() =>
        {
            FoundCount = progress.Found;
            if (progress.Target > 0) TargetTotal = progress.Target;
        });
        helper.StatusChanged += text => dispatch(() => StatusMessage = text);
        helper.LogWritten += text => dispatch(() => AppendLog(text));
        helper.RiskEncountered += prompt => dispatch(() =>
        {
            RiskReason = prompt.Reason;
            IsRiskPromptVisible = true;
            StatusMessage = "采集已暂停，等待你的选择。";
        });
        helper.Failed += message => dispatch(() =>
        {
            AppendLog($"助手报告错误：{message}");
            StatusMessage = message;
        });
        helper.ChannelBroken += message => dispatch(() =>
        {
            IsCollecting = false;
            IsRiskPromptVisible = false;
            StatusMessage = $"采集助手已断开：{message}";
        });
        helper.Completed += outcome => dispatch(() =>
        {
            IsCollecting = false;
            IsRiskPromptVisible = false;
            FoundCount = outcome.Found;
            StatusMessage = outcome.Stopped
                ? $"已停止，共采到 {outcome.Found} 条。"
                : $"采集完成，共采到 {outcome.Found} 条。";
            if (outcome.Stats.TryGetValue("no_pwd", out var missing) && missing > 0)
                AppendLog($"其中 {missing} 条没有提取码。");
        });
    }

    private void AppendLog(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Logs.Add(text);
        while (Logs.Count > 500) Logs.RemoveAt(0);
    }

    private void RefreshCommands()
    {
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        ContinueAfterRiskCommand.NotifyCanExecuteChanged();
        AbortAfterRiskCommand.NotifyCanExecuteChanged();
    }

    public async ValueTask DisposeAsync()
    {
        lifetime?.Cancel();
        lifetime?.Dispose();
        lifetime = null;
        client = null;
        await session.DisposeAsync().ConfigureAwait(false);
    }
}
