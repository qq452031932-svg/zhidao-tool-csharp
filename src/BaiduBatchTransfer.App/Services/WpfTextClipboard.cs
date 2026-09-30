namespace BaiduBatchTransfer.App.Services;

public sealed class WpfTextClipboard : ITextClipboard
{
    private const int MaximumAttempts = 3;
    private readonly IUiDispatcher dispatcher;
    private readonly Action<string> setText;

    public WpfTextClipboard(IUiDispatcher dispatcher)
        : this(dispatcher, new NativeTextClipboard(new Win32ClipboardApi()).SetText) { }

    internal WpfTextClipboard(IUiDispatcher dispatcher, Action<string> setText)
    {
        this.dispatcher = dispatcher;
        this.setText = setText;
    }

    public async Task SetTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // Keep HWND creation, ownership transfer and HWND destruction on one UI thread.
                // There is no OLE Set/Flush/Get path and no readback of unrelated clipboard data.
                await dispatcher.InvokeAsync(() => { setText(text); return true; }, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (ClipboardNativeException error) when (attempt < MaximumAttempts &&
                error.Operation == "OpenClipboard" && error.NativeErrorCode is 5 or 170)
            {
                // OpenClipboard can briefly contend with listeners. No data has been replaced yet.
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
