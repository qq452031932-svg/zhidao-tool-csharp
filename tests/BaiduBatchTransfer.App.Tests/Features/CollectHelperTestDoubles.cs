using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using BaiduBatchTransfer.App.Features.Collect;

namespace BaiduBatchTransfer.App.Tests.Features;

/// <summary>脚本化的假助手进程：测试喂事件、收命令，全程不起真进程。</summary>
internal sealed class FakeCollectHelperProcess : ICollectHelperProcess
{
    private readonly ScriptedReader reader = new();
    private readonly RecordingWriter writer;
    private readonly TaskCompletionSource exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly bool exitsWhenInputCloses;

    /// <param name="exitsWhenInputCloses">
    /// 真实助手在 stdin 关闭后会自行退出；传 false 可模拟卡住不退的助手。
    /// </param>
    public FakeCollectHelperProcess(bool exitsWhenInputCloses = true)
    {
        this.exitsWhenInputCloses = exitsWhenInputCloses;
        writer = new RecordingWriter(() =>
        {
            StandardInputClosed = true;
            if (this.exitsWhenInputCloses) CompleteOutput();
        });
    }

    public TextWriter StandardInput => writer;

    public TextReader StandardOutput => reader;

    public TextReader StandardError => TextReader.Null;

    public Task ExitTask => exited.Task;

    public bool StandardInputClosed { get; private set; }

    public bool WasKilled { get; private set; }

    public void Emit(object frame) => reader.Emit(JsonSerializer.Serialize(frame));

    public void EmitRaw(string line) => reader.Emit(line);

    public void CompleteOutput()
    {
        reader.Complete();
        exited.TrySetResult();
    }

    public IReadOnlyList<string> WrittenCommands() => writer.Lines();

    public void Kill()
    {
        WasKilled = true;
        CompleteOutput();
    }

    public void Dispose()
    {
        reader.Complete();
        exited.TrySetResult();
    }

    private sealed class ScriptedReader : TextReader
    {
        private readonly BlockingCollection<string> lines = new();

        public void Emit(string line)
        {
            if (!lines.IsAddingCompleted) lines.Add(line);
        }

        public void Complete()
        {
            if (!lines.IsAddingCompleted) lines.CompleteAdding();
        }

        public override string? ReadLine()
        {
            try
            {
                return lines.Take();
            }
            catch (InvalidOperationException)
            {
                return null;   // 输出已结束
            }
        }
    }

    private sealed class RecordingWriter : TextWriter
    {
        private readonly List<string> lines = [];
        private readonly StringBuilder current = new();
        private readonly Action onClose;
        private readonly object sync = new();

        public RecordingWriter(Action onClose) => this.onClose = onClose;

        public override Encoding Encoding => Encoding.UTF8;

        public IReadOnlyList<string> Lines()
        {
            lock (sync) return lines.ToArray();
        }

        public override void Write(char value)
        {
            lock (sync)
            {
                if (value == '\n')
                {
                    lines.Add(current.ToString().TrimEnd('\r'));
                    current.Clear();
                }
                else
                {
                    current.Append(value);
                }
            }
        }

        public override void Close() => onClose();
    }
}

/// <summary>把一个已就绪的客户端交给视图模型，并记录是否被释放。</summary>
internal sealed class FakeCollectHelperSession(CollectHelperClient client) : ICollectHelperSession
{
    public bool Disposed { get; private set; }

    public int ConnectCount { get; private set; }

    public Task<CollectHelperClient> ConnectAsync(System.Threading.CancellationToken cancellationToken)
    {
        ConnectCount++;
        return Task.FromResult(client);
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
