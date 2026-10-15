using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using BaiduBatchTransfer.App.Features.Imports;
using BaiduBatchTransfer.App.Features.Jobs;
using BaiduBatchTransfer.App.Services;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class TransferPendingSelectionTests
{
    [Fact]
    public void PendingTableUsesOneCurrentRowAndEditorFollowsEverySelection() => OnSta(() =>
    {
        using var model = CreateModel();
        var view = LoadPendingTable(model);
        var grid = Descendants(view).OfType<DataGrid>().Single();
        // 允许多选（批量删除），但单击仍只选中一行，编辑区跟着它走。
        Assert.Equal(DataGridSelectionMode.Extended, grid.SelectionMode);
        foreach (var row in new[] { model.Rows[1], model.Rows[0], model.Rows[1] })
        {
            grid.SelectedItem = row;
            view.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            Assert.Same(row, model.SelectedRow);
            Assert.Equal(row.ShareUrl, model.SelectedShareUrl);
            Assert.Equal(row.ExtractionCode, model.SelectedExtractionCode);
            Assert.Single(grid.SelectedItems);
            Assert.Contains(Descendants(view).OfType<TextBox>(), box => box.Text == row.ShareUrl);
        }
    });

    [Fact]
    public void DisplayOrdinalsStartAtOneWithoutChangingOriginalExcelRowNumbers() => OnSta(() =>
    {
        using var model = CreateModel();
        var view = LoadPendingTable(model);
        var grid = Descendants(view).OfType<DataGrid>().Single();
        Assert.Equal("序号", grid.Columns[0].Header);
        Assert.Equal("1", Assert.IsType<TextBlock>(grid.Columns[0].GetCellContent(model.Rows[0])).Text);
        Assert.Equal("2", Assert.IsType<TextBlock>(grid.Columns[0].GetCellContent(model.Rows[1])).Text);
        Assert.Equal(2, model.Rows[0].SourceRowNumber);
        Assert.Equal(6, model.Rows[1].SourceRowNumber);
        model.LoadNextPageAsync().GetAwaiter().GetResult();
        view.UpdateLayout();
        Assert.Equal("3", Assert.IsType<TextBlock>(grid.Columns[0].GetCellContent(model.Rows[0])).Text);
        Assert.Equal(9, model.Rows[0].SourceRowNumber);
        model.LoadFirstPageAsync().GetAwaiter().GetResult();
        view.UpdateLayout();
        Assert.Equal("1", Assert.IsType<TextBlock>(grid.Columns[0].GetCellContent(model.Rows[0])).Text);
    });

    [Fact]
    public void RightClickMenuDeletesTheRowUnderThePointerAndPagingButtonsArePresent() => OnSta(() =>
    {
        using var model = CreateModel();
        var view = LoadPendingTable(model);
        var grid = Descendants(view).OfType<DataGrid>().Single();
        var item = Assert.IsType<MenuItem>(Assert.Single(Assert.IsType<ContextMenu>(grid.ContextMenu).Items));
        // 菜单文字随选中条数变化（「删除这条记录」/「删除所选 N 条记录」）。
        Assert.Equal("PlacementTarget.DataContext.Import.DeleteMenuText",
            System.Windows.Data.BindingOperations.GetBinding(item, MenuItem.HeaderProperty)!.Path.Path);
        Assert.Equal("删除这条记录", model.DeleteMenuText);

        var cell = grid.Columns[1].GetCellContent(model.Rows[1]);
        var row = BaiduBatchTransfer.App.Features.Transfers.PendingRowContextMenu.RowUnder(cell);
        Assert.Same(model.Rows[1], Assert.IsType<DataGridRow>(row).Item);
        Assert.Null(BaiduBatchTransfer.App.Features.Transfers.PendingRowContextMenu.RowUnder(grid));
        Assert.Null(BaiduBatchTransfer.App.Features.Transfers.PendingRowContextMenu.RowUnder(null));

        var buttons = Descendants(view).OfType<Button>().ToArray();
        var previous = Assert.Single(buttons, button => Equals(button.Content, "上一页"));
        var next = Assert.Single(buttons, button => Equals(button.Content, "下一页"));
        Assert.Same(model.LoadPreviousPageCommand, previous.Command);
        Assert.False(previous.IsEnabled);
        Assert.True(next.IsEnabled);
        model.LoadNextPageAsync().GetAwaiter().GetResult();
        view.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        Assert.True(previous.IsEnabled);
        Assert.Contains(Descendants(view).OfType<TextBlock>(), text => text.Text == "第 2/2 页");
        // 编辑与翻页分成两行：按钮都在可见宽度内，链接输入框留有足够的宽度（用户实机时曾被挤到只剩几十像素）。
        Assert.All(buttons, button => Assert.True(
            button.TranslatePoint(new Point(button.ActualWidth, 0), view).X <= 714.5, $"{button.Content} 超出可见宽度"));
        Assert.True(Descendants(view).OfType<TextBox>().First().ActualWidth >= 300, "链接输入框过窄");
        var filter = Assert.Single(Descendants(view).OfType<CheckBox>(), box => Equals(box.Content, "只看冲突"));
        Assert.True(filter.IsEnabled);
        Assert.False(filter.IsChecked);
        Assert.True(filter.TranslatePoint(new Point(0, 0), view).Y > previous.TranslatePoint(new Point(0, 0), view).Y - 20, "筛选与翻页应在同一行");
        Assert.True(grid.ActualHeight >= 250, "表格高度被底部两行挤占过多");
    });

    private static ImportViewModel CreateModel()
    {
        var model = new ImportViewModel(new UnavailableImportWorkflow(), new Pages(), new Dialogs());
        model.AttachSession(Guid.NewGuid());
        model.LoadFirstPageAsync().GetAwaiter().GetResult();
        return model;
    }

    internal static UserControl LoadPendingTable(ImportViewModel model)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BaiduBatchTransfer.sln"))) directory = directory.Parent;
        Assert.NotNull(directory);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var document = XDocument.Load(Path.Combine(directory.FullName, "src/BaiduBatchTransfer.App/Features/Transfers/TransferWorkspaceView.xaml"));
        var pending = document.Descendants(presentation + "TabItem").Single(item => (string?)item.Attribute("Header") == "待处理链接").Elements().Single();
        var root = new XElement(presentation + "UserControl", new XAttribute(XNamespace.Xmlns + "x", xaml),
            new XElement(presentation + "UserControl.Resources",
                new XElement(presentation + "ResourceDictionary",
                    new XElement(presentation + "ResourceDictionary.MergedDictionaries",
                        new XElement(presentation + "ResourceDictionary", new XAttribute("Source", "/知道综合工具;component/Styles/SuperPanTheme.xaml"))))),
            new XElement(pending));
        var view = Assert.IsType<UserControl>(XamlReader.Parse(root.ToString()));
        view.DataContext = new { Import = model };
        view.Measure(new Size(714, 420));
        view.Arrange(new Rect(0, 0, 714, 420));
        view.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        view.UpdateLayout();
        return view;
    }

    internal static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    internal static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); } catch (Exception error) { failure = error; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Pending table test timed out.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class Pages : IImportPreviewPageSource
    {
        public Task<ImportPreviewPage> LoadAsync(ImportPreviewPageRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(request.AfterSourceRowNumber is null
                ? new ImportPreviewPage([Row(2, "first", "abcd"), Row(6, "last", "efgh")], 3, 6)
                : new ImportPreviewPage([Row(9, "next", "ijkl")], 3, null));
        private static ImportPreviewRow Row(int number, string name, string code) =>
            new(Guid.NewGuid(), number, "https://pan.baidu.com/s/" + name, code, null, "就绪", null);
    }

    private sealed class Dialogs : IDialogService
    {
        public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task ShowMessageAsync(string title, string message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string?> PickExcelFileAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }
}
