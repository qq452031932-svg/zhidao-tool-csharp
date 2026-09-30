using System.Windows.Threading;
using BaiduBatchTransfer.App.Features.Collect;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class CollectUiDispatchTests
{
    [Fact]
    public void BackgroundHelperEventsUpdateLogsAndResultsOnTheUiThread()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                var uiThread = Environment.CurrentManagedThreadId;
                using var process = new FakeCollectHelperProcess();
                var client = CollectHelperClient.Create(process);
                process.Emit(new { ev = "ready" });
                client.WaitUntilReadyAsync(TimeSpan.FromSeconds(2), CancellationToken.None).GetAwaiter().GetResult();
                var model = new CollectViewModel(new FakeCollectHelperSession(client));
                model.KeywordText = "测试关键词";
                model.CookieText = "BDUSS=test-cookie";
                model.IsPreciseMode = false;
                model.TargetCount = 5;
                var collectionThreads = new List<int>();
                model.Logs.CollectionChanged += (_, _) => collectionThreads.Add(Environment.CurrentManagedThreadId);
                model.Resources.CollectionChanged += (_, _) => collectionThreads.Add(Environment.CurrentManagedThreadId);
                var starting = model.StartCommand.ExecuteAsync(null);
                PumpUntil(() => starting.IsCompleted);
                starting.GetAwaiter().GetResult();
                collectionThreads.Clear();

                process.Emit(new { ev = "status", text = "搜索第 1 页" });
                process.Emit(new { ev = "log", text = "已找到候选问题" });
                process.Emit(new { ev = "item", name = "测试资源", link = "test", pwd = "abcd", source = "test" });
                var frame = new DispatcherFrame();
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
                timer.Tick += (_, _) =>
                {
                    if (model.Resources.Count == 1 || DateTime.UtcNow >= deadline) frame.Continue = false;
                };
                timer.Start();
                Dispatcher.PushFrame(frame);
                timer.Stop();

                Assert.Equal(1, model.FoundCount);
                Assert.Contains(model.Logs, line => line.Contains("已找到候选问题"));
                Assert.Single(model.Resources);
                Assert.True(model.IsCollecting);
                Assert.Contains(model.Logs, line => line.Contains("已采到第 1 条：测试资源"));
                Assert.NotEmpty(collectionThreads);
                Assert.All(collectionThreads, id => Assert.Equal(uiThread, id));

                var stopping = model.StopCommand.ExecuteAsync(null);
                PumpUntil(() => stopping.IsCompleted);
                stopping.GetAwaiter().GetResult();
                Assert.Contains("正在停止", model.StatusMessage);
                Assert.Contains(process.WrittenCommands(), command => command.Contains("\"cmd\":\"stop\""));
                process.Emit(new { ev = "done", found = 1, stopped = true, stats = new { found = 1 } });
                PumpUntil(() => !model.IsCollecting);
                Assert.Contains("已停止", model.StatusMessage);
                Assert.True(model.StartCommand.CanExecute(null));
                Assert.False(model.StopCommand.CanExecute(null));
                Assert.Single(model.Resources);
                Assert.Contains(model.Logs, line => line.Contains("已停止"));
                Assert.All(collectionThreads, id => Assert.Equal(uiThread, id));
                model.DisposeAsync().AsTask().GetAwaiter().GetResult();
                client.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception error) { failure = error; }
            finally { dispatcher.InvokeShutdown(); }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Collect UI dispatch test timed out.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void PumpUntil(Func<bool> complete)
    {
        var frame = new DispatcherFrame();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) =>
        {
            if (complete() || DateTime.UtcNow >= deadline) frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
        Assert.True(complete(), "UI operation timed out.");
    }
}
