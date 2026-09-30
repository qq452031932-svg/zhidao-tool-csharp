using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BaiduBatchTransfer.App.Features.Collect;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class CollectSelectionMenuTests
{
    [Fact]
    public void MenuSelectsAllInvertsAndClearsActualRowsWithoutChangingResults()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var model = new CollectViewModel(new FakeCollectHelperSession(
                    CollectHelperClient.Create(new FakeCollectHelperProcess())), action => action());
                foreach (var index in Enumerable.Range(1, 3)) model.Resources.Add(new(index, $"资源{index}", $"https://pan.baidu.com/s/{index}", "abcd", ""));
                var grid = new DataGrid { DataContext = model, ItemsSource = model.Resources, AutoGenerateColumns = false, SelectionMode = DataGridSelectionMode.Extended, SelectionUnit = DataGridSelectionUnit.FullRow };
                grid.Columns.Add(new DataGridTextColumn { Header = "资源名称", Binding = new System.Windows.Data.Binding("Name") });
                grid.Measure(new Size(714, 300));
                grid.Arrange(new Rect(0, 0, 714, 300));
                grid.UpdateLayout();
                CollectResultSelectionMenu.Attach(grid);
                grid.SelectedItems.Add(model.Resources[0]);
                Assert.Contains("已选 1", model.ResultScopeText);
                var menu = Assert.IsType<ContextMenu>(grid.ContextMenu);
                Click(menu, "反选");
                Assert.Equal([model.Resources[1], model.Resources[2]], grid.SelectedItems.Cast<CollectedRow>());
                Assert.Contains("已选 2", model.ResultScopeText);
                Click(menu, "全选");
                Assert.Equal(3, grid.SelectedItems.Count);
                Assert.Contains("已选 3", model.ResultScopeText);
                Click(menu, "取消选择");
                Assert.Empty(grid.SelectedItems);
                Assert.Contains("操作全部 3", model.ResultScopeText);
                Assert.Equal(3, model.Resources.Count);
                // 更换来源后，菜单不能继续操作旧模型。
                grid.ItemsSource = null;
                Click(menu, "全选");
                Assert.Empty(grid.SelectedItems);
                model.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception error) { failure = error; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Selection menu test timed out.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void Click(ContextMenu menu, string caption) =>
        menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, caption)).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
}
