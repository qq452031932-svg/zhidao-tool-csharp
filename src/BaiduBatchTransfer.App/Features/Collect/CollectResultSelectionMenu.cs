using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BaiduBatchTransfer.App.Features.Collect;

internal static class CollectResultSelectionMenu
{
    public static void Attach(DataGrid grid)
    {
        var changingSelection = false;
        void Synchronize()
        {
            if (!changingSelection && grid.DataContext is CollectViewModel model)
                model.SetSelectedResults(grid.SelectedItems.OfType<CollectedRow>());
        }
        void ChangeSelection(Action action)
        {
            changingSelection = true;
            try { action(); }
            finally { changingSelection = false; Synchronize(); }
        }

        grid.SelectionChanged += (_, _) => Synchronize();
        grid.DataContextChanged += (_, _) => Synchronize();
        var open = new MenuItem { Header = "打开链接", ToolTip = "在默认浏览器打开右键指向的这一条，有提取码时一并带上。" };
        var check = new MenuItem { Header = "检测链接有效性", ToolTip = "只读取分享信息，不转存、不下载；要检测全部请先全选。" };
        var stop = new MenuItem { Header = "停止检测" };
        var selectAll = new MenuItem { Header = "全选", InputGestureText = "Ctrl+A" };
        var invert = new MenuItem { Header = "反选" };
        var clear = new MenuItem { Header = "取消选择", ToolTip = "未选中时，复制、导出和转存操作全部结果。" };
        open.Click += (_, _) => { if (grid.DataContext is CollectViewModel model) model.OpenLink(GetTarget(grid)); };
        check.Click += (_, _) =>
        {
            if (grid.DataContext is CollectViewModel model && model.CheckSelectedLinksCommand.CanExecute(null))
                model.CheckSelectedLinksCommand.Execute(null);
        };
        stop.Click += (_, _) => { if (grid.DataContext is CollectViewModel model) model.StopLinkCheckCommand.Execute(null); };
        selectAll.Click += (_, _) => ChangeSelection(grid.SelectAll);
        invert.Click += (_, _) => ChangeSelection(() =>
        {
            var previous = grid.SelectedItems.OfType<CollectedRow>().ToHashSet();
            var inverse = grid.Items.OfType<CollectedRow>().Where(row => !previous.Contains(row)).ToArray();
            grid.UnselectAll();
            foreach (var row in inverse) grid.SelectedItems.Add(row);
        });
        clear.Click += (_, _) => ChangeSelection(grid.UnselectAll);
        var menu = new ContextMenu();
        menu.Items.Add(open);
        menu.Items.Add(check);
        menu.Items.Add(stop);
        menu.Items.Add(new Separator());
        menu.Items.Add(selectAll);
        menu.Items.Add(invert);
        menu.Items.Add(clear);

        grid.ContextMenuOpening += (_, args) => PointAt(grid, RowAt(args.OriginalSource as DependencyObject));
        menu.Opened += (_, _) =>
        {
            var target = GetTarget(grid);
            var model = grid.DataContext as CollectViewModel;
            var selected = grid.SelectedItems.OfType<CollectedRow>().Count();
            open.Visibility = model?.CanOpenLinks == true ? Visibility.Visible : Visibility.Collapsed;
            open.IsEnabled = target is not null && grid.Items.Contains(target);
            open.Header = target is null ? "打开链接" : $"打开链接（第 {target.Ordinal} 条）";
            check.Visibility = stop.Visibility =
                model?.CanCheckLinksAtAll == true ? Visibility.Visible : Visibility.Collapsed;
            check.Header = selected <= 1 ? "检测链接有效性" : $"检测选中的 {selected} 条链接";
            check.IsEnabled = model?.CheckSelectedLinksCommand.CanExecute(null) == true;
            stop.IsEnabled = model?.IsCheckingLinks == true;
            selectAll.IsEnabled = invert.IsEnabled = grid.Items.OfType<CollectedRow>().Any();
            clear.IsEnabled = grid.SelectedItems.Count > 0;
        };
        grid.ContextMenu = menu;
        Synchronize();
    }

    // 右键指向的那一行；「打开链接」只打开它，不受多选影响。
    private static readonly DependencyProperty TargetProperty = DependencyProperty.RegisterAttached(
        "Target", typeof(CollectedRow), typeof(CollectResultSelectionMenu));

    private static CollectedRow? GetTarget(DataGrid grid) =>
        grid.GetValue(TargetProperty) is CollectedRow row && grid.Items.Contains(row) ? row : null;

    /// <summary>
    /// 记下右键指向的行。落在未选中的行：改为只选这一行，避免检测到旧的选择；落在已选中的行：保留多选。
    /// </summary>
    internal static void PointAt(DataGrid grid, CollectedRow? row)
    {
        grid.SetValue(TargetProperty, row);
        if (row is null || grid.SelectedItems.Contains(row)) return;
        grid.UnselectAll();
        grid.SelectedItems.Add(row);
    }

    internal static CollectedRow? RowAt(DependencyObject? source)
    {
        while (source is not null and not DataGridRow)
        {
            if (source is System.Windows.Controls.Primitives.DataGridColumnHeader) return null;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return (source as DataGridRow)?.Item as CollectedRow;
    }
}
