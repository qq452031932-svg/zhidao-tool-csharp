using BaiduBatchTransfer.App.Features.Acceptance;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class BaiduAcceptanceProbeBrowserLifecycleTests
{
    [Fact]
    public async Task ObservesProcessExitBeforeDisposingTheBrowser()
    {
        var browser = new RecordingBrowserProcess(exits: true);
        var lifecycle = new BaiduAcceptanceProbeBrowserLifecycle(browser, TimeSpan.FromSeconds(1));

        var exited = await lifecycle.DisposeAfterExitAsync();

        Assert.True(exited);
        Assert.Equal(["observe", "dispose", "wait"], browser.Events);
    }

    [Fact]
    public async Task TimesOutWithoutThrowingWhenTheBrowserProcessDoesNotExit()
    {
        var browser = new RecordingBrowserProcess(exits: false);
        var lifecycle = new BaiduAcceptanceProbeBrowserLifecycle(browser, TimeSpan.Zero);

        var exited = await lifecycle.DisposeAfterExitAsync();

        Assert.False(exited);
        Assert.Equal(["observe", "dispose", "wait"], browser.Events);
    }

    private sealed class RecordingBrowserProcess(bool exits) : IBaiduAcceptanceProbeBrowserProcess
    {
        public List<string> Events { get; } = [];
        public Task ObserveExitAsync(CancellationToken cancellationToken = default)
        {
            Events.Add("observe");
            return exits ? Task.CompletedTask : Task.Delay(Timeout.Infinite, cancellationToken);
        }
        public void DisposeBrowser() => Events.Add("dispose");
        public Task WaitForObservationAsync(Task observation, CancellationToken cancellationToken = default)
        {
            Events.Add("wait");
            return observation.WaitAsync(cancellationToken);
        }
    }
}
