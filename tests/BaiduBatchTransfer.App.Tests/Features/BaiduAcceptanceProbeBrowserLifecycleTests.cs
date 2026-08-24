using BaiduBatchTransfer.App.Features.Acceptance;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class BaiduAcceptanceProbeBrowserLifecycleTests
{
    [Fact]
    public async Task ObservesProcessExitBeforeDisposingTheBrowser()
    {
        var events = new List<string>();
        var lifecycle = new BaiduAcceptanceProbeBrowserLifecycle(7, () => events.Add("dispose"), TimeSpan.FromSeconds(1));
        lifecycle.NotifyBrowserProcessExited(6);
        lifecycle.NotifyBrowserProcessExited(7);

        var exited = await lifecycle.DisposeAfterExitAsync();

        Assert.True(exited);
        Assert.Equal(["dispose"], events);
    }

    [Fact]
    public async Task TimesOutWithoutThrowingWhenTheBrowserProcessDoesNotExit()
    {
        var events = new List<string>();
        var lifecycle = new BaiduAcceptanceProbeBrowserLifecycle(7, () => events.Add("dispose"), TimeSpan.Zero);

        var exited = await lifecycle.DisposeAfterExitAsync();

        Assert.False(exited);
        Assert.Equal(["dispose"], events);
    }
}
