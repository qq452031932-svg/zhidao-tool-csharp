using BaiduBatchTransfer.App.Features.Collect;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class CollectHelperSessionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BrokenOutputOrExitedProcessIsReplacedByAFreshHelper(bool exitProcess)
    {
        var layout = CollectHelperLocator.Locate(AppContext.BaseDirectory);
        Assert.True(File.Exists(layout.InterpreterPath), "This acceptance test needs the bundled Python runtime.");
        var temporary = Path.Combine(Path.GetTempPath(), "collect-reconnect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        var script = Path.Combine(temporary, "probe.py");
        await File.WriteAllTextAsync(script, $$"""
            import json, os, sys
            print('{"ev":"ready"}', flush=True)
            for line in sys.stdin:
                if json.loads(line).get("cmd") == "start":
                    if {{(exitProcess ? "True" : "False")}}:
                        sys.exit(0)
                    os.close(sys.stdout.fileno())
            """);
        try
        {
            await using var session = new CollectHelperSession(() => new(layout.InterpreterPath, script));
            var first = await session.ConnectAsync(CancellationToken.None);
            var broken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            first.ChannelBroken += _ => broken.TrySetResult();
            await first.StartCollectingAsync(new("precise", ["测试"], []), CancellationToken.None);
            await broken.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(first.IsConnected);
            if (!exitProcess) Assert.False(first.HasExited);
            var second = await session.ConnectAsync(CancellationToken.None);
            Assert.NotSame(first, second);
            Assert.True(second.IsConnected);
        }
        finally
        {
            File.Delete(script);
            Directory.Delete(temporary);
        }
    }
}
