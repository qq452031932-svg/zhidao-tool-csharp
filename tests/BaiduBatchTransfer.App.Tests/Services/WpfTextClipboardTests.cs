using System.Runtime.InteropServices;
using BaiduBatchTransfer.App.Services;

namespace BaiduBatchTransfer.App.Tests.Services;

public sealed class WpfTextClipboardTests
{
    private const string Text = "https://pan.baidu.com/s/example?pwd=abcd";

    [Fact]
    public async Task ExecutesTheWriterOnlyThroughTheUiDispatcher()
    {
        var dispatcher = new RecordingDispatcher();
        var writes = 0;
        var clipboard = new WpfTextClipboard(dispatcher, text =>
        {
            Assert.True(dispatcher.IsDispatching);
            Assert.Equal(Text, text);
            writes++;
        });
        await clipboard.SetTextAsync(Text);
        Assert.Equal(1, writes);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(170)]
    public async Task RetriesOnlyTemporaryNativeOpenContention(int error)
    {
        var writes = 0;
        var clipboard = new WpfTextClipboard(new RecordingDispatcher(),
            _ => { if (++writes == 1) throw new ClipboardNativeException("OpenClipboard", error); });
        await clipboard.SetTextAsync(Text);
        Assert.Equal(2, writes);
    }

    [Fact]
    public async Task StopsAfterThreeOpenAttemptsAndRetainsTheNativeError()
    {
        var failure = new ClipboardNativeException("OpenClipboard", 5);
        var writes = 0;
        var clipboard = new WpfTextClipboard(new RecordingDispatcher(), _ => { writes++; throw failure; });
        Assert.Same(failure, await Assert.ThrowsAsync<ClipboardNativeException>(() => clipboard.SetTextAsync(Text)));
        Assert.Equal(3, writes);
        Assert.Equal(unchecked((int)0x80070005), failure.HResult);
    }

    [Theory]
    [InlineData("SetClipboardData", 5)]
    [InlineData("CloseClipboard", 5)]
    [InlineData("OpenClipboard", 1400)]
    public async Task OtherNativeErrorsAreNeverHiddenOrRetried(string operation, int error)
    {
        var failure = new ClipboardNativeException(operation, error);
        var writes = 0;
        var clipboard = new WpfTextClipboard(new RecordingDispatcher(), _ => { writes++; throw failure; });
        Assert.Same(failure, await Assert.ThrowsAsync<ClipboardNativeException>(() => clipboard.SetTextAsync(Text)));
        Assert.Equal(1, writes);
    }

    [Fact]
    public async Task OleExceptionsAreNotConvertedIntoSuccessOrRetried()
    {
        var failure = new COMException("old OLE path must not be used", unchecked((int)0x800401D0));
        var writes = 0;
        var clipboard = new WpfTextClipboard(new RecordingDispatcher(), _ => { writes++; throw failure; });
        Assert.Same(failure, await Assert.ThrowsAsync<COMException>(() => clipboard.SetTextAsync(Text)));
        Assert.Equal(1, writes);
    }

    [Fact]
    public async Task CancellationPreventsAnotherWriteAfterContention()
    {
        using var cancellation = new CancellationTokenSource();
        var writes = 0;
        var clipboard = new WpfTextClipboard(new RecordingDispatcher(), _ =>
        {
            writes++;
            cancellation.Cancel();
            throw new ClipboardNativeException("OpenClipboard", 5);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => clipboard.SetTextAsync(Text, cancellation.Token));
        Assert.Equal(1, writes);
    }

    private sealed class RecordingDispatcher : IUiDispatcher
    {
        public bool IsDispatching { get; private set; }
        public Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsDispatching = true;
            try { return Task.FromResult(action()); }
            finally { IsDispatching = false; }
        }
    }
}
