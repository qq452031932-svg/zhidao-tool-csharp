using System.Globalization;
using System.Xml.Linq;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class ShareAnalyticsViewStructureTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void DataGridDeclaresTheTwelveVisibleColumnsInProductOrder()
    {
        var (_, grid, columns) = LoadView();

        Assert.Equal(12, columns.Count);
        Assert.Equal(
            ["选择", "序号", "历史累计", "新增", "今天", "昨天", "动态日期2", "动态日期3", "动态日期4", "链接", "标题", "状态"],
            columns.Select(ColumnSemantic));
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
    public void SelectionToolbarBindsCountsRangeAndEveryBulkCommand()
    {
        var (document, _, _) = LoadView();
        var toolbar = document.Descendants(Presentation + "StackPanel")
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
