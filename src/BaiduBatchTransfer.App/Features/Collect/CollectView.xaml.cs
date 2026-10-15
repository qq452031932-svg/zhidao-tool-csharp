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
        AttachAccountsGrid();
        Loaded += (_, _) => ObserveLogs();
        Unloaded += (_, _) => DetachLogs();
        DataContextChanged += (_, _) => { if (IsLoaded) ObserveLogs(); };
    }

    /// <summary>账号表格的选择回报给账号列表；右键可用剪贴板里的 Cookie 更新选中的账号。</summary>
    private void AttachAccountsGrid()
    {
        void Synchronize()
        {
            if (DataContext is CollectViewModel { AccountList: { } list })
                list.SetSelected(AccountsGrid.SelectedItems.OfType<ZhidaoAccountRow>());
        }
        AccountsGrid.SelectionChanged += (_, _) => Synchronize();
        DataContextChanged += (_, _) => Synchronize();
        var update = new MenuItem
        {
            Header = "用剪贴板里的 Cookie 更新此账号",
            ToolTip = "只更新右键点中的这一个账号：先复制它的新 Cookie，再点这里；备注和勾选保持不变。",
        };
        var checkAll = new MenuItem { Header = "全部勾选" };
        var uncheckAll = new MenuItem { Header = "全部取消勾选" };
        checkAll.Click += (_, _) => (DataContext as CollectViewModel)?.AccountList?.SetAllChecked(true);
        uncheckAll.Click += (_, _) => (DataContext as CollectViewModel)?.AccountList?.SetAllChecked(false);
        update.Click += (_, _) =>
        {
            if (DataContext is CollectViewModel { AccountList: { } list } && list.UpdateCookieCommand.CanExecute(null))
                list.UpdateCookieCommand.Execute(null);
        };
        var delete = new MenuItem { Header = "删除勾选的账号" };
        delete.Click += (_, _) =>
        {
            if (DataContext is CollectViewModel { AccountList: { } list } && list.DeleteCommand.CanExecute(null))
                list.DeleteCommand.Execute(null);
        };
        var menu = new ContextMenu();
        menu.Items.Add(update);
        menu.Items.Add(new Separator());
        menu.Items.Add(checkAll);
        menu.Items.Add(uncheckAll);
        menu.Items.Add(delete);
        AccountsGrid.ContextMenuOpening += (_, args) =>
        {
            var row = CollectResultSelectionMenu.RowItemAt(args.OriginalSource as DependencyObject) as ZhidaoAccountRow;
            // 表格选中只用来确定「右键更新」的目标，批量操作看勾选；所以右键总是改为只选这一行。
            if (row is not null)
            {
                AccountsGrid.UnselectAll();
                AccountsGrid.SelectedItems.Add(row);
            }
        };
        menu.Opened += (_, _) =>
        {
            var list = (DataContext as CollectViewModel)?.AccountList;
            update.IsEnabled = list?.UpdateCookieCommand.CanExecute(null) == true;
            update.Header = list?.UpdateTarget is { } target ? $"用剪贴板里的 Cookie 更新「{target.Remark}」" : "用剪贴板里的 Cookie 更新此账号";
            delete.IsEnabled = list?.DeleteCommand.CanExecute(null) == true;
            delete.Header = list is { CheckedCount: > 0 } ? $"删除勾选的 {list.CheckedCount} 个账号" : "删除勾选的账号";
            checkAll.IsEnabled = uncheckAll.IsEnabled = list is { CanEdit: true, Accounts.Count: > 0 };
        };
        AccountsGrid.ContextMenu = menu;
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
