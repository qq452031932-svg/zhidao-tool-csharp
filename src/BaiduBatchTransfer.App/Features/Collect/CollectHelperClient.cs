using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BaiduBatchTransfer.App.Features.Collect;

/// <summary>采到的一条资源。</summary>
public sealed record CollectedResource(string Name, string Link, string Pwd, string Source);

/// <summary>已采数量与目标数量；Target 为 0 表示不限量。</summary>
public sealed record CollectProgress(int Found, int Target);

/// <summary>撞上风控，等待界面答复；在答复前采集侧是阻塞的。</summary>
public sealed record CollectRiskPrompt(string Reason);

/// <summary>从关键词文件读出的内容。Encoding 说明助手是按哪种编码解的。</summary>
public sealed record CollectedKeywords(
    IReadOnlyList<string> Items, int Lines, string Encoding, bool Truncated);

/// <summary>一次采集的结束。Stopped 表示是被要求停止的，而非自然跑完。</summary>
public sealed record CollectOutcome(int Found, bool Stopped, IReadOnlyDictionary<string, int> Stats);

/// <summary>一次采集的参数。</summary>
public sealed record CollectRequest(
    string Mode,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<CollectAccount> Accounts,
    int Count = 0,
    string TimeRange = "auto",
    bool DetailMode = true,
    bool DedupeNames = true,
    bool Resume = false);

/// <summary>一个采集账号。Cookie 只在本机进程间传递，不落盘、不记日志。</summary>
public sealed record CollectAccount(string Name, string Cookie, string? ExitKind = null);

/// <summary>助手进程的抽象，便于在没有 Python 的情况下测试客户端。</summary>
internal interface ICollectHelperProcess : IDisposable
{
    TextWriter StandardInput { get; }
    TextReader StandardOutput { get; }
    Task ExitTask { get; }
    void Kill();
}

/// <summary>
/// 驱动采集助手子进程：按行写命令、按行读事件，并把事件翻译成 .NET 事件。
/// 事件在后台线程触发，界面层自行切回 UI 线程。
/// 本类不解释采集语义，只保证信道可靠与生命周期干净。
/// </summary>
public sealed class CollectHelperClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>关闭时等待助手自行退出的上限。</summary>
    internal static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(5);

    private readonly ICollectHelperProcess process;
    private readonly Task readPump;
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly TaskCompletionSource readyGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int disposed;

    private CollectHelperClient(ICollectHelperProcess process)
    {
        this.process = process;
        readPump = Task.Run(PumpAsync);
    }

    /// <summary>采到一条资源。</summary>
    public event Action<CollectedResource>? ResourceCollected;

    /// <summary>进度变化。</summary>
    public event Action<CollectProgress>? ProgressChanged;

    /// <summary>面向用户的状态文字。</summary>
    public event Action<string>? StatusChanged;

    /// <summary>诊断文字。</summary>
    public event Action<string>? LogWritten;

    /// <summary>撞上风控，需要界面答复 <see cref="AnswerRiskAsync"/>。</summary>
    public event Action<CollectRiskPrompt>? RiskEncountered;

    /// <summary>一次采集结束。</summary>
    public event Action<CollectOutcome>? Completed;

    /// <summary>助手报告的错误；助手本身仍然存活。</summary>
    public event Action<string>? Failed;

    /// <summary>助手意外退出，信道已不可用。</summary>
    public event Action<string>? ChannelBroken;

    /// <summary>关键词文件读取完成。</summary>
    public event Action<CollectedKeywords>? KeywordsLoaded;

    internal static CollectHelperClient Create(ICollectHelperProcess process) => new(process);

    /// <summary>启动助手并等待它就绪。</summary>
    public static async Task<CollectHelperClient> StartAsync(
        string pythonExecutablePath, string mainScriptPath, TimeSpan readyTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pythonExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(mainScriptPath);
        if (!File.Exists(pythonExecutablePath))
            throw new FileNotFoundException("未找到采集助手所需的 Python 运行时。", pythonExecutablePath);
        if (!File.Exists(mainScriptPath))
            throw new FileNotFoundException("未找到采集助手主程序。", mainScriptPath);

        var client = new CollectHelperClient(new ChildProcess(CreateStartInfo(pythonExecutablePath, mainScriptPath)));
        try
        {
            await client.WaitUntilReadyAsync(readyTimeout, cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static ProcessStartInfo CreateStartInfo(string executablePath, string mainScriptPath)
    {
        var info = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(mainScriptPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        // -B 不写字节码，-E 忽略环境变量，-s 排除用户站点目录
        foreach (var argument in new[] { "-B", "-E", "-s", mainScriptPath }) info.ArgumentList.Add(argument);
        return info;
    }

    internal async Task WaitUntilReadyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await readyGate.Task.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("采集助手在限定时间内没有就绪。");
        }
    }

    /// <summary>开始一次采集。</summary>
    public Task StartCollectingAsync(CollectRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var command = new Dictionary<string, object?>
        {
            ["cmd"] = "start",
            ["mode"] = request.Mode,
            ["keywords"] = request.Keywords,
            ["count"] = request.Count,
            ["time_range"] = request.TimeRange,
            ["detail_mode"] = request.DetailMode,
            ["dedupe_names"] = request.DedupeNames,
            ["resume"] = request.Resume,
            ["accounts"] = BuildAccounts(request.Accounts),
        };
        return SendAsync(command, cancellationToken);
    }

    /// <summary>请求停止；助手会在下一个可中断点收尾，并仍然给出 Completed。</summary>
    public Task StopCollectingAsync(CancellationToken cancellationToken) =>
        SendAsync(new Dictionary<string, object?> { ["cmd"] = "stop" }, cancellationToken);

    /// <summary>答复一次风控询问。</summary>
    public Task AnswerRiskAsync(bool shouldContinue, CancellationToken cancellationToken) =>
        SendAsync(new Dictionary<string, object?>
        {
            ["cmd"] = "resume",
            ["continue"] = shouldContinue,
        }, cancellationToken);

    /// <summary>请助手读取一个关键词文件；编码判别由助手负责，结果经 KeywordsLoaded 回来。</summary>
    public Task ReadKeywordsAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return SendAsync(new Dictionary<string, object?>
        {
            ["cmd"] = "read_keywords",
            ["path"] = path,
        }, cancellationToken);
    }

    private static List<Dictionary<string, string>> BuildAccounts(IReadOnlyList<CollectAccount> accounts)
    {
        var payload = new List<Dictionary<string, string>>(accounts.Count);
        foreach (var account in accounts)
        {
            var entry = new Dictionary<string, string> { ["name"] = account.Name, ["cookie"] = account.Cookie };
            if (!string.IsNullOrWhiteSpace(account.ExitKind)) entry["exit_kind"] = account.ExitKind!;
            payload.Add(entry);
        }

        return payload;
    }

    private async Task SendAsync(Dictionary<string, object?> command, CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(command, JsonOptions);
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeGate.Release();
        }
    }

    private async Task PumpAsync()
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0) continue;
                Dispatch(line);
            }
        }
        catch (Exception error) when (Volatile.Read(ref disposed) == 0)
        {
            ChannelBroken?.Invoke(error.Message);
        }

        readyGate.TrySetException(new InvalidOperationException("采集助手在就绪前退出。"));
        if (Volatile.Read(ref disposed) == 0) ChannelBroken?.Invoke("采集助手已退出。");
    }

    private void Dispatch(string line)
    {
        JsonElement frame;
        try
        {
            frame = JsonDocument.Parse(line).RootElement;
        }
        catch (JsonException)
        {
            LogWritten?.Invoke($"忽略无法解析的助手输出：{Truncate(line)}");
            return;
        }

        if (frame.ValueKind != JsonValueKind.Object ||
            !frame.TryGetProperty("ev", out var eventName) ||
            eventName.ValueKind != JsonValueKind.String)
        {
            LogWritten?.Invoke($"忽略缺少事件名的助手输出：{Truncate(line)}");
            return;
        }

        switch (eventName.GetString())
        {
            case "ready":
                readyGate.TrySetResult();
                break;
            case "item":
                ResourceCollected?.Invoke(new CollectedResource(
                    Text(frame, "name"), Text(frame, "link"), Text(frame, "pwd"), Text(frame, "source")));
                break;
            case "progress":
                ProgressChanged?.Invoke(new CollectProgress(Number(frame, "found"), Number(frame, "target")));
                break;
            case "status":
                StatusChanged?.Invoke(Text(frame, "text"));
                break;
            case "log":
                LogWritten?.Invoke(Text(frame, "text"));
                break;
            case "ask":
                RiskEncountered?.Invoke(new CollectRiskPrompt(Text(frame, "reason")));
                break;
            case "keywords":
                KeywordsLoaded?.Invoke(new CollectedKeywords(
                    Strings(frame, "items"), Number(frame, "lines"),
                    Text(frame, "encoding"), Flag(frame, "truncated")));
                break;
            case "done":
                Completed?.Invoke(new CollectOutcome(Number(frame, "found"), Flag(frame, "stopped"), Stats(frame)));
                break;
            case "error":
                Failed?.Invoke(Text(frame, "message"));
                break;
            case "started":
            case "pong":
                break;
            default:
                LogWritten?.Invoke($"忽略未知助手事件：{eventName.GetString()}");
                break;
        }
    }

    private static string Text(JsonElement frame, string name) =>
        frame.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int Number(JsonElement frame, string name) =>
        frame.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var number)
            ? number
            : 0;

    private static bool Flag(JsonElement frame, string name) =>
        frame.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static IReadOnlyList<string> Strings(JsonElement frame, string name)
    {
        if (!frame.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            return [];
        var items = new List<string>(value.GetArrayLength());
        foreach (var element in value.EnumerateArray())
            if (element.ValueKind == JsonValueKind.String && element.GetString() is { Length: > 0 } text)
                items.Add(text);
        return items;
    }

    private static IReadOnlyDictionary<string, int> Stats(JsonElement frame)
    {
        var stats = new Dictionary<string, int>();
        if (!frame.TryGetProperty("stats", out var value) || value.ValueKind != JsonValueKind.Object) return stats;
        foreach (var property in value.EnumerateObject())
            if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var number))
                stats[property.Name] = number;
        return stats;
    }

    private static string Truncate(string line) => line.Length <= 200 ? line : line[..200] + "…";

    /// <summary>
    /// 先请求停止，再关闭 stdin 让助手自行收尾；超时则结束进程。
    /// 等待刻意保持短：采集进度已由助手写入断点文件，强杀不会丢进度，
    /// 而关闭软件时让界面卡住更不可接受。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try
        {
            using var stopBudget = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await StopCollectingAsync(stopBudget.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 助手可能已经不在了，继续走关闭流程
        }

        try
        {
            process.StandardInput.Close();
        }
        catch (Exception)
        {
            // 管道可能已经断开，交由下面的等待与 Kill 处理
        }

        var exited = await Task.WhenAny(process.ExitTask, Task.Delay(ShutdownGrace))
            .ConfigureAwait(false) == process.ExitTask;
        if (!exited)
        {
            try
            {
                process.Kill();
            }
            catch (Exception)
            {
                // 进程可能刚刚自行退出
            }
        }

        await Task.WhenAny(readPump, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        writeGate.Dispose();
        process.Dispose();
    }

    private sealed class ChildProcess : ICollectHelperProcess
    {
        private readonly Process process;
        private readonly TaskCompletionSource exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ChildProcess(ProcessStartInfo startInfo)
        {
            process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.Exited += (_, _) => exited.TrySetResult();
            if (!process.Start()) throw new InvalidOperationException("无法启动采集助手进程。");
            process.BeginErrorReadLine();
            process.ErrorDataReceived += (_, _) => { };
        }

        public TextWriter StandardInput => process.StandardInput;
        public TextReader StandardOutput => process.StandardOutput;
        public Task ExitTask => exited.Task;

        public void Kill() => process.Kill(entireProcessTree: true);

        public void Dispose() => process.Dispose();
    }
}
