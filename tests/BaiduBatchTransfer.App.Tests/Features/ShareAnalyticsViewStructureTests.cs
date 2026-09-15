using System.Globalization;
using System.Xml.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Threading;
using BaiduBatchTransfer.App.Features.ShareAnalytics;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Application.ShareAnalytics;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class ShareAnalyticsViewStructureTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void HeaderUsesOneCompactTwoRowToolbarWithDelayedTitleSearch()
    {
        var (document, _, _) = LoadView();
        var page = document.Root!.Element(Presentation + "Grid")!;
        var toolbar = Assert.Single(page.Elements(Presentation + "Border"), element =>
            Attribute(element, Xaml + "Name") == "ShareAnalyticsToolbar");
        var content = Assert.Single(toolbar.Elements(Presentation + "Grid"));
        Assert.Equal(2, content.Element(Presentation + "Grid.RowDefinitions")!.Elements().Count());
        Assert.Equal(new[] { "Auto", "*" }, page.Element(Presentation + "Grid.RowDefinitions")!
            .Elements().Select(element => Attribute(element, "Height")));
        Assert.DoesNotContain(document.Descendants(Presentation + "TextBlock"), element =>
            Attribute(element, "Text") == "分享数据" || Attribute(element, "Text").Contains("点击表头切换", StringComparison.Ordinal));
        var firstRow = content.Elements().Single(element => Attribute(element, Xaml + "Name") == "ShareSummaryToolbar");
        foreach (var binding in new[] { "CurrentAccountName", "CacheStatusText", "SelectMetricCommand", "TotalCount", "ValidCount", "InvalidCount", "RefreshCommand", "StatusMessage" })
            Assert.Contains(binding, firstRow.ToString(), StringComparison.Ordinal);
        var secondRow = content.Elements().Single(element => Attribute(element, Xaml + "Name") == "SelectionToolbar");
        foreach (var binding in new[] { "SearchText", "SelectedStatusOption", "SelectedCount", "RangeStartText", "RangeEndText", "ExportCommand" })
            Assert.Contains(binding, secondRow.ToString(), StringComparison.Ordinal);
        var search = secondRow.Descendants(Presentation + "TextBox").Single(element =>
            Attribute(element, Xaml + "Name") == "TitleSearchInput");
        Assert.Contains("Delay=300", Attribute(search, "Text"), StringComparison.Ordinal);
        Assert.Contains("UpdateSourceTrigger=PropertyChanged", Attribute(search, "Text"), StringComparison.Ordinal);
        var codeBehind = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "BaiduBatchTransfer.App",
            "Features", "ShareAnalytics", "ShareAnalyticsView.xaml.cs"));
        Assert.Contains("TitleSearchInput.KeyDown += OnTitleSearchKeyDown;", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void TitleSearchDelaysLocalFilteringFlushesOnEnterAndClearsToAllOnSta()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var service = new CachedSearchService();
                var delay = new RecordingCacheDelay();
                using var viewModel = new ShareAnalyticsViewModel(service, new AccountShareAnalyticsProjector(),
                    new UnusedLauncher(), TimeProvider.System, delay, new UnusedExporter(), new UnusedSaveDialog());
                viewModel.SelectAccount(service.AccountId, "测试账号");
                viewModel.EnterAsync().GetAwaiter().GetResult();
                Assert.Equal(2, viewModel.VisibleItems.Count);
                var cacheStatus = viewModel.CacheStatusText;
                var delayCalls = delay.Calls;

                var (document, _, _) = LoadView();
                var searchMarkup = document.Descendants(Presentation + "TextBox").Single(element =>
                    Attribute(element, "Text").Contains("Binding SearchText", StringComparison.Ordinal));
                var input = Assert.IsType<TextBox>(XamlReader.Parse(searchMarkup.ToString()));
                input.DataContext = viewModel;
                PumpFor(20);
                input.Text = "alpha";
                PumpFor(80);
                Assert.Equal(string.Empty, viewModel.SearchText);
                Assert.Equal(2, viewModel.VisibleItems.Count);
                PumpFor(350);
                Assert.Equal("alpha", viewModel.SearchText);
                Assert.Equal("alpha", Assert.Single(viewModel.VisibleItems).Title);

                var handler = typeof(ShareAnalyticsView).GetMethod("OnTitleSearchKeyDown", BindingFlags.Static | BindingFlags.NonPublic);
                Assert.NotNull(handler);
                input.KeyDown += handler!.CreateDelegate<KeyEventHandler>();
                using var source = new HwndSource(new HwndSourceParameters("Title search STA test") { Width = 1, Height = 1, WindowStyle = 0 });
                input.Text = "beta";
                var enter = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent };
                input.RaiseEvent(enter);
                Assert.True(enter.Handled);
                Assert.Equal("beta", viewModel.SearchText);
                Assert.Equal("beta", Assert.Single(viewModel.VisibleItems).Title);

                input.Clear();
                PumpFor(350);
                Assert.Equal(string.Empty, viewModel.SearchText);
                Assert.Equal(2, viewModel.VisibleItems.Count);
                Assert.Equal(0, service.SyncCalls);
                Assert.Equal(1, service.LoadCalls);
                Assert.Equal(delayCalls, delay.Calls);
                Assert.Equal(cacheStatus, viewModel.CacheStatusText);
            }
            catch (Exception exception) { failure = exception; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Title search STA test timed out.");
        if (failure is not null) throw new TargetInvocationException(failure);
    }

    private static void PumpFor(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    [Fact]
    public void CompactToolbarKeepsBothRowsWithinTypicalAndMinimumWorkspaceWidthsOnSta()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var service = new CachedSearchService();
                using var viewModel = new ShareAnalyticsViewModel(service, new AccountShareAnalyticsProjector(),
                    new UnusedLauncher(), TimeProvider.System, new RecordingCacheDelay(), new UnusedExporter(), new UnusedSaveDialog());
                viewModel.SelectAccount(service.AccountId, "一个需要截断展示的较长账号名称");
                viewModel.EnterAsync().GetAwaiter().GetResult();
                var (document, _, _) = LoadView();
                document.Root!.Attribute(Xaml + "Class")!.Remove();
                var resources = document.Root.Element(Presentation + "UserControl.Resources")!;
                var existingResources = resources.Elements().ToArray();
                resources.ReplaceNodes(new XElement(Presentation + "ResourceDictionary",
                    new XElement(Presentation + "ResourceDictionary.MergedDictionaries",
                        new XElement(Presentation + "ResourceDictionary", new XAttribute("Source",
                            "/知道综合工具;component/Styles/SuperPanTheme.xaml"))), existingResources));
                var view = Assert.IsType<UserControl>(XamlReader.Parse(document.ToString()));
                view.FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI");
                view.FontSize = 14;
                view.DataContext = viewModel;
                foreach (var width in new[] { 836, 716 })
                {
                    var size = new Size(width, 600);
                    view.Measure(size);
                    view.Arrange(new Rect(size));
                    view.UpdateLayout();
                    var toolbar = Assert.IsType<Border>(view.FindName("ShareAnalyticsToolbar"));
                    Assert.InRange(toolbar.ActualHeight, 1, 90);
                    foreach (var name in new[] { "ShareSummaryToolbar", "SelectionToolbar" })
                    {
                        var row = Assert.IsType<Grid>(view.FindName(name));
                        Assert.InRange(row.ActualHeight, 1, 34);
                        foreach (var child in row.Children.OfType<FrameworkElement>())
                        {
                            var right = child.TransformToAncestor(row).Transform(new Point(child.ActualWidth, 0)).X;
                            Assert.InRange(right, 1, row.ActualWidth + 1);
                        }
                    }
                    var search = Assert.IsType<TextBox>(view.FindName("TitleSearchInput"));
                    Assert.True(search.ActualWidth >= 90);
                    Console.WriteLine($"Share analytics {width}px: toolbar {toolbar.ActualWidth:N1}x{toolbar.ActualHeight:N1}px; title search {search.ActualWidth:N1}px.");
                }
            }
            catch (Exception exception) { failure = exception; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Compact share toolbar STA test timed out.");
        if (failure is not null) throw new TargetInvocationException(failure);
    }

    private sealed class CachedSearchService : IShareAnalyticsWorkspaceService
    {
        public Guid AccountId { get; } = Guid.NewGuid();
        public int LoadCalls { get; private set; }
        public int SyncCalls { get; private set; }
        public Task<AccountShareCachedSnapshot> LoadCachedAsync(Guid accountId, CancellationToken cancellationToken = default)
        {
            LoadCalls++;
            return Task.FromResult(new AccountShareCachedSnapshot(AccountId, DateTimeOffset.UtcNow,
                new[] { "alpha", "beta" }.Select(title => new AccountShareCachedRecord(title, title,
                    new Uri($"https://pan.baidu.com/s/{title}"), DateTimeOffset.UtcNow, AccountShareRemoteStatus.Valid,
                    Enum.GetValues<AccountShareMetric>().Select(metric => new AccountShareMetricHistory(metric, 1)).ToArray())).ToArray()));
        }
        public Task<AccountShareSyncResult> SyncAsync(Guid accountId, CancellationToken cancellationToken = default)
        {
            SyncCalls++;
            throw new InvalidOperationException("Local search must not refresh remote data.");
        }
    }

    private sealed class RecordingCacheDelay : IShareAnalyticsDelay
    {
        public int Calls { get; private set; }
        public Task WaitAsync(TimeSpan delay, CancellationToken token)
        {
            Calls++;
            return Task.Delay(Timeout.Infinite, token);
        }
    }

    private sealed class UnusedLauncher : IShareLinkLauncher
    {
        public bool TryOpen(Uri uri, out string safeMessage) => throw new NotSupportedException();
    }

    private sealed class UnusedExporter : IShareAnalyticsWorkbookExportService
    {
        public Task<ShareAnalyticsWorkbookExportResult> ExportAsync(ShareAnalyticsWorkbookExportRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class UnusedSaveDialog : IResultSaveDialog
    {
        public Task<string?> PickDestinationDirectoryAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [Fact]
    public void DataGridDeclaresTheTwelveVisibleColumnsInProductOrder()
    {
        var (_, grid, columns) = LoadView();

        Assert.Equal(12, columns.Count);
        Assert.Equal(
            ["选择", "序号", "历史累计", "新增", "今天", "昨天", "动态日期2", "动态日期3", "动态日期4", "链接", "标题", "状态"],
            columns.Select(ColumnSemantic));
        var sequenceColumn = columns.Single(column => ColumnSemantic(column) == "序号");
        Assert.Equal("44", Attribute(sequenceColumn, "Width"));
        Assert.Equal("Visible", Attribute(grid, "ScrollViewer.HorizontalScrollBarVisibility"));
        Assert.Equal("True", Attribute(grid, "EnableRowVirtualization"));
        Assert.Equal("True", Attribute(grid, "EnableColumnVirtualization"));
        Assert.Equal("True", Attribute(grid, "VirtualizingPanel.IsVirtualizing"));
        Assert.Equal("Recycling", Attribute(grid, "VirtualizingPanel.VirtualizationMode"));
    }

    [Fact]
    public void ShareTimeIsNotAVisibleColumnOrSortHeader()
    {
        var (document, _, columns) = LoadView();
        var columnMarkup = string.Concat(columns.Select(column => column.ToString(SaveOptions.DisableFormatting)));

        Assert.DoesNotContain("分享时间", columnMarkup, StringComparison.Ordinal);
        Assert.DoesNotContain("SharedAtText", columnMarkup, StringComparison.Ordinal);
        Assert.DoesNotContain("SharedAtSortGlyph", columnMarkup, StringComparison.Ordinal);
        Assert.DoesNotContain("AccountShareSortColumn.SharedAt", columnMarkup, StringComparison.Ordinal);
        Assert.DoesNotContain("SharedAtSortGlyph", document.ToString(SaveOptions.DisableFormatting), StringComparison.Ordinal);
    }

    [Fact]
    public void ColumnHeadersStretchTheirHitSurfaceWhileKeepingOrdinaryContentCentered()
    {
        var (document, grid, columns) = LoadView();
        var headerStyle = document.Descendants(Presentation + "Style")
            .Single(style => Attribute(style, Xaml + "Key") == "ShareAnalyticsColumnHeaderStyle");

        Assert.Equal("36", SetterValue(headerStyle, "Height"));
        Assert.Equal("Stretch", SetterValue(headerStyle, "HorizontalContentAlignment"));
        Assert.Equal("Stretch", SetterValue(headerStyle, "VerticalContentAlignment"));
        Assert.Equal("{StaticResource ShareAnalyticsColumnHeaderStyle}", Attribute(grid, "ColumnHeaderStyle"));

        var sortButtonStyle = document.Descendants(Presentation + "Style")
            .Single(style => Attribute(style, Xaml + "Key") == "ShareAnalyticsSortButtonStyle");
        Assert.Equal("Stretch", SetterValue(sortButtonStyle, "HorizontalAlignment"));
        Assert.Equal("Stretch", SetterValue(sortButtonStyle, "VerticalAlignment"));
        Assert.Equal("Center", SetterValue(sortButtonStyle, "HorizontalContentAlignment"));
        Assert.Equal("Center", SetterValue(sortButtonStyle, "VerticalContentAlignment"));

        var bySemantic = columns.ToDictionary(ColumnSemantic);
        foreach (var semantic in new[] { "序号", "链接", "标题", "状态" })
        {
            var header = HeaderElement(bySemantic[semantic]);
            Assert.NotNull(header.Element(Presentation + "Grid"));
            var text = header.Descendants(Presentation + "TextBlock").Single();
            Assert.Equal("Center", Attribute(text, "HorizontalAlignment"));
            Assert.Equal("Center", Attribute(text, "VerticalAlignment"));
        }
    }

    [Fact]
    public void NumericTitleLinkAndStatusCellsUseTheirRequiredAlignmentAndPresentation()
    {
        var (_, _, columns) = LoadView();
        var bySemantic = columns.ToDictionary(ColumnSemantic);

        foreach (var semantic in new[] { "序号", "历史累计", "新增", "今天", "昨天", "动态日期2", "动态日期3", "动态日期4" })
        {
            var cellText = FindBoundTextBlock(bySemantic[semantic]);
            Assert.Equal("Right", Attribute(cellText, "TextAlignment"));
        }

        var titleColumn = bySemantic["标题"];
        Assert.True(double.Parse(Attribute(titleColumn, "Width"), CultureInfo.InvariantCulture) >= 240);
        var titleText = FindBoundTextBlock(titleColumn);
        Assert.Equal("{Binding Title}", Attribute(titleText, "Text"));
        Assert.Equal("Left", Attribute(titleText, "TextAlignment"));
        Assert.Equal("CharacterEllipsis", Attribute(titleText, "TextTrimming"));
        Assert.Equal("{Binding Title}", Attribute(titleText, "ToolTip"));

        Assert.Contains(bySemantic["链接"].Descendants(Presentation + "Button"), button =>
            Attribute(button, "Content") == "打开" && Attribute(button, "Command").Contains("OpenCommand", StringComparison.Ordinal));
        Assert.Equal("Center", Attribute(FindBoundTextBlock(bySemantic["状态"]), "TextAlignment"));
    }

    [Fact]
    public void HeaderSelectionUsesAFullSurfaceCommandButtonAndANonInteractiveTriStateDisplay()
    {
        var (_, _, columns) = LoadView();
        var selectionColumn = columns.Single(column => ColumnSemantic(column) == "选择");
        var checkBoxes = selectionColumn.Descendants(Presentation + "CheckBox").ToArray();
        Assert.Equal(2, checkBoxes.Length);

        var display = checkBoxes.Single(checkBox => Attribute(checkBox, "IsThreeState") == "True");
        Assert.Contains("HeaderSelectionState", Attribute(display, "IsChecked"), StringComparison.Ordinal);
        Assert.Contains("Mode=OneWay", Attribute(display, "IsChecked"), StringComparison.Ordinal);
        Assert.Equal("False", Attribute(display, "IsHitTestVisible"));
        Assert.Equal("False", Attribute(display, "Focusable"));
        Assert.Empty(Attribute(display, "Command"));

        var commandButton = display.Ancestors(Presentation + "Button").Single();
        Assert.Contains("ToggleHeaderSelectionCommand", Attribute(commandButton, "Command"), StringComparison.Ordinal);
        Assert.Equal("Stretch", Attribute(commandButton, "HorizontalAlignment"));
        Assert.Equal("Stretch", Attribute(commandButton, "VerticalAlignment"));
        Assert.Equal("选择当前结果", Attribute(commandButton, "AutomationProperties.Name"));
    }

    [Fact]
    public void RowSelectionStillPublishesImmediateTwoWayChanges()
    {
        var (_, _, columns) = LoadView();
        var selectionColumn = columns.Single(column => ColumnSemantic(column) == "选择");
        var row = selectionColumn.Descendants(Presentation + "CheckBox")
            .Single(checkBox => Attribute(checkBox, "IsThreeState") != "True");

        Assert.Contains("IsSelected", Attribute(row, "IsChecked"), StringComparison.Ordinal);
        Assert.Contains("Mode=TwoWay", Attribute(row, "IsChecked"), StringComparison.Ordinal);
        Assert.Contains("UpdateSourceTrigger=PropertyChanged", Attribute(row, "IsChecked"), StringComparison.Ordinal);
    }

    [Fact]
    public void SelectionStylesKeepTextLegibleWithoutMaskingSortedColumns()
    {
        var (document, grid, columns) = LoadView();
        var rowStyle = document.Descendants(Presentation + "Style")
            .Single(style => Attribute(style, Xaml + "Key") == "ShareAnalyticsRowStyle");
        var cellStyle = document.Descendants(Presentation + "Style")
            .Single(style => Attribute(style, Xaml + "Key") == "ShareAnalyticsCellStyle");
        var sortedCellStyle = document.Descendants(Presentation + "Style")
            .Single(style => Attribute(style, Xaml + "Key") == "ShareAnalyticsSortCellStyle");

        Assert.Equal("DataGridRow", Attribute(rowStyle, "TargetType"));
        Assert.Equal("{StaticResource ShareAnalyticsRowStyle}", Attribute(grid, "RowStyle"));
        Assert.Equal("Transparent", SetterValue(rowStyle, "Background"));
        Assert.Equal("{StaticResource TextBrush}", SetterValue(rowStyle, "Foreground"));
        Assert.Equal("DataGridCell", Attribute(cellStyle, "TargetType"));
        Assert.Equal("{StaticResource ShareAnalyticsCellStyle}", Attribute(sortedCellStyle, "BasedOn"));

        foreach (var selectionActive in new[] { "True", "False" })
        {
            var rowSelectionTrigger = SelectionStateTrigger(rowStyle, selectionActive);
            Assert.Equal("{StaticResource TextBrush}", TriggerSetterValue(rowSelectionTrigger, "Foreground"));
            Assert.Equal("{StaticResource NavSelectedBrush}", TriggerSetterValue(rowSelectionTrigger, "Background"));

            var cellSelectionTrigger = SelectionStateTrigger(cellStyle, selectionActive);
            Assert.Equal("{StaticResource TextBrush}", TriggerSetterValue(cellSelectionTrigger, "Foreground"));
            Assert.Equal("{StaticResource NavSelectedBrush}", TriggerSetterValue(cellSelectionTrigger, "Background"));
        }

        var sortedColumns = columns.Where(column => column.Descendants(Presentation + "Style")
            .Any(style => Attribute(style, "BasedOn") == "{StaticResource ShareAnalyticsSortCellStyle}"));
        Assert.NotEmpty(sortedColumns);
        Assert.All(sortedColumns, column => Assert.Contains(column.Descendants(Presentation + "Setter"), setter =>
            Attribute(setter, "Property") == "Background" &&
            Attribute(setter, "Value") == "{StaticResource ShareAnalyticsSortedCellBrush}"));
    }

    [Fact]
    public void SelectionToolbarBindsCountsRangeAndEveryBulkCommand()
    {
        var (document, _, _) = LoadView();
        var toolbar = document.Descendants()
            .Single(element => Attribute(element, Xaml + "Name") == "SelectionToolbar");
        var markup = toolbar.ToString(SaveOptions.DisableFormatting);

        foreach (var binding in new[] { "SelectedCount", "CurrentResultCount", "RangeStartText", "RangeEndText" })
        {
            Assert.Contains(binding, markup, StringComparison.Ordinal);
        }
        foreach (var command in new[] { "SelectRangeCommand", "SelectAllCommand", "InvertSelectionCommand", "ClearSelectionCommand", "ExportCommand" })
        {
            Assert.Contains($"Command=\"{{Binding {command}}}\"", markup, StringComparison.Ordinal);
        }
        foreach (var caption in new[] { "选取", "全选", "反选", "清空选择", "导出表格" })
        {
            Assert.Contains(toolbar.Descendants(Presentation + "Button"), button => Attribute(button, "Content") == caption);
        }

        var rangeInputs = toolbar.Descendants(Presentation + "TextBox").ToArray();
        var startInput = rangeInputs.Single(textBox => Attribute(textBox, "Text").Contains("RangeStartText", StringComparison.Ordinal));
        var endInput = rangeInputs.Single(textBox => Attribute(textBox, "Text").Contains("RangeEndText", StringComparison.Ordinal));
        Assert.Equal("选取起始序号", Attribute(startInput, "AutomationProperties.Name"));
        Assert.Equal("选取结束序号", Attribute(endInput, "AutomationProperties.Name"));
    }

    private static (XDocument Document, XElement Grid, IReadOnlyList<XElement> Columns) LoadView()
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(root, "src", "BaiduBatchTransfer.App", "Features", "ShareAnalytics", "ShareAnalyticsView.xaml");
        var document = XDocument.Load(path, LoadOptions.PreserveWhitespace);
        var grid = document.Descendants(Presentation + "DataGrid").Single();
        var collection = grid.Element(Presentation + "DataGrid.Columns")
            ?? throw new Xunit.Sdk.XunitException("DataGrid.Columns element is required.");
        var columns = collection.Elements()
            .Where(element => element.Name == Presentation + "DataGridTemplateColumn" ||
                              element.Name == Presentation + "DataGridTextColumn")
            .ToArray();
        return (document, grid, columns);
    }

    private static string ColumnSemantic(XElement column)
    {
        var directHeader = Attribute(column, "Header");
        if (!string.IsNullOrEmpty(directHeader)) return directHeader;

        var headerElement = column.Elements().SingleOrDefault(element => element.Name.LocalName.EndsWith(".Header", StringComparison.Ordinal));
        if (headerElement is null) return string.Empty;
        var headerNodes = headerElement.Descendants().ToArray();
        var automationName = headerNodes.Select(node => Attribute(node, "AutomationProperties.Name"))
            .FirstOrDefault(name => name is "选择" or "序号" or "历史累计" or "新增" or "今天" or "昨天" or "链接" or "标题" or "状态");
        if (!string.IsNullOrEmpty(automationName)) return automationName;
        var content = headerNodes.Select(node => Attribute(node, "Content"))
            .FirstOrDefault(value => !string.IsNullOrEmpty(value) && !value.StartsWith("{Binding", StringComparison.Ordinal));
        if (!string.IsNullOrEmpty(content)) return content;
        var text = headerNodes.Select(node => Attribute(node, "Text"))
            .FirstOrDefault(value => !string.IsNullOrEmpty(value) && !value.StartsWith("{Binding", StringComparison.Ordinal));
        if (!string.IsNullOrEmpty(text)) return text;
        var parameter = headerNodes.Select(node => Attribute(node, "CommandParameter"))
            .FirstOrDefault(value => !string.IsNullOrEmpty(value)) ?? string.Empty;
        if (parameter.Contains("PriorDay2", StringComparison.Ordinal)) return "动态日期2";
        if (parameter.Contains("PriorDay3", StringComparison.Ordinal)) return "动态日期3";
        if (parameter.Contains("PriorDay4", StringComparison.Ordinal)) return "动态日期4";
        return string.Empty;
    }

    private static XElement HeaderElement(XElement column) =>
        column.Elements().Single(element => element.Name.LocalName.EndsWith(".Header", StringComparison.Ordinal));

    private static XElement FindBoundTextBlock(XElement column) =>
        column.Descendants(Presentation + "TextBlock")
            .Single(text => Attribute(text, "Text").StartsWith("{Binding", StringComparison.Ordinal));

    private static string SetterValue(XElement style, string property) =>
        Attribute(style.Elements(Presentation + "Setter").Single(setter => Attribute(setter, "Property") == property), "Value");

    private static XElement SelectionStateTrigger(XElement style, string selectionActive) =>
        style.Descendants(Presentation + "MultiTrigger").Single(trigger =>
        {
            var conditions = trigger.Element(Presentation + "MultiTrigger.Conditions")?.Elements(Presentation + "Condition") ?? [];
            return conditions.Any(condition => Attribute(condition, "Property") == "IsSelected" && Attribute(condition, "Value") == "True") &&
                   conditions.Any(condition => Attribute(condition, "Property") == "Selector.IsSelectionActive" && Attribute(condition, "Value") == selectionActive);
        });

    private static string TriggerSetterValue(XElement trigger, string property) =>
        Attribute(trigger.Elements(Presentation + "Setter").Single(setter => Attribute(setter, "Property") == property), "Value");

    private static string Attribute(XElement element, string name) =>
        element.Attribute(name)?.Value ?? string.Empty;

    private static string Attribute(XElement element, XName name) =>
        element.Attribute(name)?.Value ?? string.Empty;

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "BaiduBatchTransfer.sln"))) return current.FullName;
            current = current.Parent;
        }
        throw new InvalidOperationException("Repository root not found.");
    }
}
