using System.Windows.Controls;

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
        var selectAll = new MenuItem { Header = "全选", InputGestureText = "Ctrl+A" };
        var invert = new MenuItem { Header = "反选" };
        var clear = new MenuItem { Header = "取消选择", ToolTip = "未选中时，复制、导出和转存操作全部结果。" };
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
        menu.Items.Add(selectAll);
        menu.Items.Add(invert);
        menu.Items.Add(clear);
        menu.Opened += (_, _) =>
        {
            selectAll.IsEnabled = invert.IsEnabled = grid.Items.OfType<CollectedRow>().Any();
            clear.IsEnabled = grid.SelectedItems.Count > 0;
        };
        grid.ContextMenu = menu;
        Synchronize();
    }
}
