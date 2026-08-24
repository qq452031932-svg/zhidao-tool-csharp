using System.ComponentModel;
using System.Windows;

namespace BaiduBatchTransfer.App.Features.BrowserAutomation;

public partial class WebAutomationWindow : Window
{
    private readonly CancellationTokenSource userClosed = new();
    private TaskCompletionSource<WebAutomationUserChoice>? handoffChoice;
    private bool programmaticClose;
    private bool isClosed;

    public WebAutomationWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
        Closed += OnClosed;
    }

    internal Microsoft.Web.WebView2.Wpf.WebView2 WebView => Browser;
    internal CancellationToken UserClosed => userClosed.Token;
    internal bool IsClosed => isClosed;

    internal void ShowHandoff(WebAutomationHandoffPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        if (handoffChoice is { Task.IsCompleted: false })
            throw new InvalidOperationException("窗口已经在等待人工处理。");
        StatusText.Text = prompt.SafeMessage;
        HandoffActions.Visibility = Visibility.Visible;
        handoffChoice = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal Task<WebAutomationUserChoice> WaitForHandoffChoiceAsync(
        CancellationToken cancellationToken)
    {
        var current = handoffChoice ??
            throw new InvalidOperationException("窗口尚未进入人工处理状态。");
        return current.Task.WaitAsync(cancellationToken);
    }

    internal void CloseProgrammatically()
    {
        programmaticClose = true;
        Close();
    }

    private void OnResume(object sender, RoutedEventArgs e) =>
        CompleteChoice(WebAutomationUserChoice.Resume);

    private void OnSkip(object sender, RoutedEventArgs e) =>
        CompleteChoice(WebAutomationUserChoice.Skip);

    private void OnCancelBatch(object sender, RoutedEventArgs e) =>
        CompleteChoice(WebAutomationUserChoice.CancelBatch);

    private void CompleteChoice(WebAutomationUserChoice choice)
    {
        HandoffActions.Visibility = Visibility.Collapsed;
        StatusText.Text = choice == WebAutomationUserChoice.Resume
            ? "已继续处理当前项目。"
            : "正在安全结束当前项目……";
        handoffChoice?.TrySetResult(choice);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (programmaticClose) return;
        handoffChoice?.TrySetResult(WebAutomationUserChoice.WindowClosed);
        userClosed.Cancel();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        isClosed = true;
        handoffChoice?.TrySetResult(WebAutomationUserChoice.WindowClosed);
        userClosed.Cancel();
    }
}
