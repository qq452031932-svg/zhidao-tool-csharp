using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BaiduBatchTransfer.App.Features.Jobs;

namespace BaiduBatchTransfer.App.Features.Transfers;

public partial class TransferWorkspaceView : UserControl
{
    public TransferWorkspaceView(TransferWorkspaceViewModel viewModel)
    {
        InitializeComponent();
        _ = new TransferPasteInputAutoHeight(this, BulkPasteInput);
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        ResultsHost.Content = new JobResultsView(viewModel.RunPanel.Results);
        _ = new WorkspaceLogAutoScroll(this, TransferLogsList, viewModel.Logs);
    }
}

internal sealed class TransferPasteInputAutoHeight
{
    private readonly TextBox input;
    private bool subscribed;
    private DispatcherOperation? pendingMeasure;

    public TransferPasteInputAutoHeight(FrameworkElement owner, TextBox input)
    {
        this.input = input;
        owner.Loaded += OnLoaded;
        owner.Unloaded += OnUnloaded;
        if (owner.IsLoaded) OnLoaded(owner, new RoutedEventArgs());
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (subscribed || input.Dispatcher.HasShutdownStarted || input.Dispatcher.HasShutdownFinished) return;
        subscribed = true;
        input.TextChanged += OnTextChanged;
        input.SizeChanged += OnSizeChanged;
        input.Dispatcher.ShutdownStarted += OnDispatcherShutdownStarted;
        ScheduleMeasure();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => StopMeasuring();

    private void OnDispatcherShutdownStarted(object? sender, EventArgs e) => StopMeasuring();

    private void StopMeasuring()
    {
        if (!subscribed) return;
        subscribed = false;
        input.TextChanged -= OnTextChanged;
        input.SizeChanged -= OnSizeChanged;
        input.Dispatcher.ShutdownStarted -= OnDispatcherShutdownStarted;
        pendingMeasure?.Abort();
        pendingMeasure = null;
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e) => ScheduleMeasure();

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 高度改变可能使滚动条出现或消失，进而改变可用换行宽度。
        ScheduleMeasure();
    }

    private void ScheduleMeasure()
    {
        if (!subscribed || input.Dispatcher.HasShutdownStarted || input.Dispatcher.HasShutdownFinished ||
            pendingMeasure?.Status == DispatcherOperationStatus.Pending) return;
        try
        {
            pendingMeasure = input.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                pendingMeasure = null;
                if (!subscribed || input.Dispatcher.HasShutdownStarted || input.Dispatcher.HasShutdownFinished) return;

                // TextBox 的布局行数包含实际宽度引起的自动换行。
                input.UpdateLayout();
                var minLines = input.MinLines > 0 ? input.MinLines : 2;
                var maxLines = input.MaxLines >= minLines ? input.MaxLines : Math.Max(minLines, 8);
                var lines = Math.Clamp(input.LineCount, minLines, maxLines);
                var lineHeight = input.FontFamily.LineSpacing * input.FontSize;
                var height = Math.Ceiling(lines * lineHeight + input.Padding.Top + input.Padding.Bottom +
                    input.BorderThickness.Top + input.BorderThickness.Bottom);
                if (double.IsNaN(input.Height) || Math.Abs(input.Height - height) > 0.5)
                    input.SetCurrentValue(FrameworkElement.HeightProperty, height);
            }));
        }
        catch (InvalidOperationException)
        {
            // 关闭窗口时调度器可能已停止接收测量工作。
        }
    }
}

internal sealed class WorkspaceLogAutoScroll
{
    private readonly ListBox list;
    private readonly INotifyCollectionChanged logs;
    private bool subscribed;
    private DispatcherOperation? pendingScroll;

    public WorkspaceLogAutoScroll(UserControl owner, ListBox list, INotifyCollectionChanged logs)
    {
        this.list = list;
        this.logs = logs;
        owner.Loaded += OnLoaded;
        owner.Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (subscribed) return;
        subscribed = true;
        logs.CollectionChanged += OnLogsChanged;
        list.IsVisibleChanged += OnListIsVisibleChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!subscribed) return;
        logs.CollectionChanged -= OnLogsChanged;
        list.IsVisibleChanged -= OnListIsVisibleChanged;
        subscribed = false;
        pendingScroll?.Abort();
        pendingScroll = null;
    }

    private void OnLogsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add) ScheduleScrollToLatest();
    }

    private void OnListIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (list.IsVisible && list.Items.Count > 0) ScheduleScrollToLatest();
    }

    private void ScheduleScrollToLatest()
    {
        if (!subscribed || list.Items.Count == 0 ||
            list.Dispatcher.HasShutdownStarted || list.Dispatcher.HasShutdownFinished ||
            (!list.IsVisible && PresentationSource.FromVisual(list) is not null)) return;
        if (pendingScroll?.Status == DispatcherOperationStatus.Pending) return;
        try
        {
            pendingScroll = list.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                pendingScroll = null;
                if (!subscribed || list.Items.Count == 0 ||
                    list.Dispatcher.HasShutdownStarted || list.Dispatcher.HasShutdownFinished ||
                    (!list.IsVisible && PresentationSource.FromVisual(list) is not null)) return;
                try
                {
                    list.ScrollIntoView(list.Items[list.Items.Count - 1]);
                }
                catch (Exception)
                {
                    // 日志展示不应因布局或滚动失败而中断任务。
                }
            }));
        }
        catch (InvalidOperationException)
        {
            // 窗口关闭期间，调度器可能已停止接收工作。
        }
    }
}
