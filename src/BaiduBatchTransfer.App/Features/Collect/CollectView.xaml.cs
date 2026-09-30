using System.Windows.Controls;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Threading;

namespace BaiduBatchTransfer.App.Features.Collect;

/// <summary>「知道采集」页面：采集逻辑在 ViewModel，视图负责日志滚动。</summary>
public partial class CollectView : UserControl
{
    private INotifyCollectionChanged? observedLogs;

    public CollectView()
    {
        InitializeComponent();
        CollectResultSelectionMenu.Attach(ResultsGrid);
        Loaded += (_, _) => ObserveLogs();
        Unloaded += (_, _) => DetachLogs();
        DataContextChanged += (_, _) => { if (IsLoaded) ObserveLogs(); };
    }

    private void ObserveLogs()
    {
        DetachLogs();
        if (DataContext is not CollectViewModel model) return;
        observedLogs = model.Logs;
        CollectionChangedEventManager.AddHandler(observedLogs, OnLogsChanged);
    }

    private void DetachLogs()
    {
        if (observedLogs is not null) CollectionChangedEventManager.RemoveHandler(observedLogs, OnLogsChanged);
        observedLogs = null;
    }

    private void OnLogsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (args.Action != NotifyCollectionChangedAction.Add) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (IsLoaded && RunLogList.Items.Count > 0)
                RunLogList.ScrollIntoView(RunLogList.Items[^1]);
        }));
    }
}
