using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using BaiduBatchTransfer.App.Services;
using Xunit.Abstractions;

namespace BaiduBatchTransfer.App.Tests.Services;

// Opt-in only: this replaces the clipboard with the exact text authorized for this diagnosis.
public sealed class NativeClipboardIntegrationTests(ITestOutputHelper output)
{
    internal const string AuthorizedText = "https://pan.baidu.com/s/1pWE-hpk4rRb69Y82gNbYEQ?pwd=s16c";

    [AuthorizedClipboardFact]
    public void PublicServiceCopiesAuthorizedTextWithoutOleFailure()
    {
        OnStaThread(async () =>
        {
            var dispatcher = new TestDispatcher(Dispatcher.CurrentDispatcher);
            await new WpfTextClipboard(dispatcher).SetTextAsync(AuthorizedText);
            // Only read after our successful public write. If another copy occurred since the write,
            // stop without examining its data. Never save/restore or display earlier clipboard contents.
            VerifyOwnCopiedText(dispatcher.LastCompletedSequence);
        });
        output.WriteLine($"Public service write and exact CF_UNICODETEXT readback passed after owner destruction in Windows session {System.Diagnostics.Process.GetCurrentProcess().SessionId}.");
    }

    private static void VerifyOwnCopiedText(uint writtenSequence)
    {
        var api = new Win32ClipboardApi();
        var owner = api.CreateOwnerWindow();
        try
        {
            api.Open(owner);
            try
            {
                Assert.True(GetClipboardSequenceNumber() == writtenSequence,
                    "Clipboard changed after the authorized write; unrelated data was not read.");
                var memory = GetClipboardData(13);
                Assert.NotEqual((nint)0, memory);
                var expected = Encoding.Unicode.GetBytes(AuthorizedText + '\0');
                Assert.True(GlobalSize(memory) >= (nuint)expected.Length, "CF_UNICODETEXT allocation was too short.");
                var address = api.Lock(memory);
                try
                {
                    var actual = new byte[expected.Length];
                    Marshal.Copy(address, actual, 0, actual.Length);
                    Assert.True(actual.SequenceEqual(expected), "CF_UNICODETEXT did not exactly match the authorized URL.");
                }
                finally { api.Unlock(memory); }
            }
            finally { api.Close(); }
        }
        finally { api.DestroyOwnerWindow(owner); }
    }

    private static void OnStaThread(Func<Task> action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                var task = action();
                var frame = new DispatcherFrame();
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
                timer.Tick += (_, _) => { if (task.IsCompleted) frame.Continue = false; };
                timer.Start();
                Dispatcher.PushFrame(frame);
                timer.Stop();
                task.GetAwaiter().GetResult();
            }
            catch (Exception error) { failure = error; }
            finally { dispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Clipboard test did not finish.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class TestDispatcher(Dispatcher dispatcher) : IUiDispatcher
    {
        public uint LastCompletedSequence { get; private set; }
        public Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return dispatcher.InvokeAsync(() =>
            {
                var result = action();
                LastCompletedSequence = GetClipboardSequenceNumber();
                return result;
            }, DispatcherPriority.Normal, cancellationToken).Task;
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetClipboardData(uint format);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint GlobalSize(nint memory);
}

public sealed class AuthorizedClipboardFactAttribute : FactAttribute
{
    public AuthorizedClipboardFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("BAIDU_CLIPBOARD_NATIVE_TEST") != "1")
            Skip = "Explicit opt-in required: replaces the clipboard with the authorized diagnostic URL.";
    }
}
