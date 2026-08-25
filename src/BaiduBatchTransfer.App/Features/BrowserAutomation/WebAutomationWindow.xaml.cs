using System.ComponentModel;
using System.Windows;
using BaiduBatchTransfer.BaiduAdapter.WebAutomation;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.App.Features.BrowserAutomation;

public partial class WebAutomationWindow : Window
{
    private readonly CancellationTokenSource userClosed = new();
    private TaskCompletionSource<WebAutomationUserChoice>? handoffChoice;
    private TaskCompletionSource<RemoteResourcePickerDecision>? pickerChoice;
    private TaskCompletionSource<bool>? directoryPreparationChoice;
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
        PickerActions.Visibility = Visibility.Collapsed;
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

    internal void ShowResourcePickerActions()
    {
        if (pickerChoice is { Task.IsCompleted: false }) return;
        HandoffActions.Visibility = Visibility.Collapsed;
        PickerActions.Visibility = Visibility.Visible;
        StatusText.Text = "请在页面中浏览并勾选资源，确认无误后点击“确认选择”。";
        pickerChoice = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Activate();
    }

    internal Task<RemoteResourcePickerDecision> WaitForResourcePickerDecisionAsync(
        CancellationToken cancellationToken)
    {
        var current = pickerChoice ??
            throw new InvalidOperationException("窗口尚未进入资源选择状态。");
        return current.Task.WaitAsync(cancellationToken);
    }

    internal void ShowDirectoryPreparationActions(RemotePath requestedPath)
    {
        ArgumentNullException.ThrowIfNull(requestedPath);
        if (directoryPreparationChoice is { Task.IsCompleted: false })
            throw new InvalidOperationException("窗口已经在等待目录确认。");
        HandoffActions.Visibility = Visibility.Collapsed;
        PickerActions.Visibility = Visibility.Collapsed;
        DirectoryPreparationActions.Visibility = Visibility.Visible;
        StatusText.Text = $"请在官方页面中创建或观察精确目标目录“{requestedPath.Value}”，确认确实存在后再点击确认。";
        directoryPreparationChoice = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Activate();
    }

    internal Task<bool> WaitForDirectoryPreparationDecisionAsync(CancellationToken cancellationToken)
    {
        var current = directoryPreparationChoice ??
            throw new InvalidOperationException("窗口尚未进入目录确认状态。");
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

    private void OnConfirmSelection(object sender, RoutedEventArgs e) =>
        CompletePickerChoice(RemoteResourcePickerDecision.ConfirmSelection);

    private void OnCancelSelection(object sender, RoutedEventArgs e) =>
        CompletePickerChoice(RemoteResourcePickerDecision.Cancel);

    private void OnConfirmDirectoryPrepared(object sender, RoutedEventArgs e) =>
        CompleteDirectoryPreparation(true);

    private void OnCancelDirectoryPreparation(object sender, RoutedEventArgs e) =>
        CompleteDirectoryPreparation(false);

    private void CompleteChoice(WebAutomationUserChoice choice)
    {
        HandoffActions.Visibility = Visibility.Collapsed;
        StatusText.Text = choice == WebAutomationUserChoice.Resume
            ? "已继续处理当前项目。"
            : "正在安全结束当前项目……";
        handoffChoice?.TrySetResult(choice);
    }

    private void CompletePickerChoice(RemoteResourcePickerDecision choice)
    {
        PickerActions.Visibility = Visibility.Collapsed;
        StatusText.Text = choice == RemoteResourcePickerDecision.ConfirmSelection
            ? "正在核对所选资源的精确标识……"
            : "正在取消资源选择……";
        pickerChoice?.TrySetResult(choice);
    }

    private void CompleteDirectoryPreparation(bool confirmed)
    {
        DirectoryPreparationActions.Visibility = Visibility.Collapsed;
        StatusText.Text = confirmed ? "已记录当前操作的人工目录确认。" : "已取消目录准备。";
        directoryPreparationChoice?.TrySetResult(confirmed);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (programmaticClose) return;
        handoffChoice?.TrySetResult(WebAutomationUserChoice.WindowClosed);
        pickerChoice?.TrySetResult(RemoteResourcePickerDecision.WindowClosed);
        directoryPreparationChoice?.TrySetResult(false);
        userClosed.Cancel();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        isClosed = true;
        handoffChoice?.TrySetResult(WebAutomationUserChoice.WindowClosed);
        pickerChoice?.TrySetResult(RemoteResourcePickerDecision.WindowClosed);
        directoryPreparationChoice?.TrySetResult(false);
        userClosed.Cancel();
    }
}
