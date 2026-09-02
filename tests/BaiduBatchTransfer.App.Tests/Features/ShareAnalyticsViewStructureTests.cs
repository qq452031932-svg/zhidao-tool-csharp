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
    public void ColumnHeadersShareOneCenteredThirtySixPixelBaseline()
    {
        var (document, grid, _) = LoadView();
        var headerStyle = document.Descendants(Presentation + "Style")
            .Single(style => Attribute(style, Xaml + "Key") == "ShareAnalyticsColumnHeaderStyle");

        Assert.Equal("36", SetterValue(headerStyle, "Height"));
        Assert.Equal("Center", SetterValue(headerStyle, "HorizontalContentAlignment"));
        Assert.Equal("Center", SetterValue(headerStyle, "VerticalContentAlignment"));
        Assert.Equal("{StaticResource ShareAnalyticsColumnHeaderStyle}", Attribute(grid, "ColumnHeaderStyle"));

        var sortButtonStyle = document.Descendants(Presentation + "Style")
            .Single(style => Attribute(style, Xaml + "Key") == "ShareAnalyticsSortButtonStyle");
        Assert.Equal("Center", SetterValue(sortButtonStyle, "HorizontalContentAlignment"));
        Assert.Equal("Center", SetterValue(sortButtonStyle, "VerticalContentAlignment"));
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
    public void HeaderAndRowsExposeTriStateAndImmediateSelectionBindings()
    {
        var (_, _, columns) = LoadView();
        var selectionColumn = columns.Single(column => ColumnSemantic(column) == "选择");
        var checkBoxes = selectionColumn.Descendants(Presentation + "CheckBox").ToArray();
        Assert.Equal(2, checkBoxes.Length);

        var header = checkBoxes.Single(checkBox => Attribute(checkBox, "IsThreeState") == "True");
        Assert.Contains("HeaderSelectionState", Attribute(header, "IsChecked"), StringComparison.Ordinal);
        Assert.Contains("Mode=OneWay", Attribute(header, "IsChecked"), StringComparison.Ordinal);
        Assert.Contains("ToggleHeaderSelectionCommand", Attribute(header, "Command"), StringComparison.Ordinal);

        var row = checkBoxes.Single(checkBox => !ReferenceEquals(checkBox, header));
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
        var header = headerElement.Descendants().FirstOrDefault();
        if (header is null) return string.Empty;
        var automationName = Attribute(header, "AutomationProperties.Name");
        if (automationName is "选择" or "序号" or "历史累计" or "新增" or "今天" or "昨天" or "链接" or "标题" or "状态")
            return automationName;
        var content = Attribute(header, "Content");
        if (!string.IsNullOrEmpty(content) && !content.StartsWith("{Binding", StringComparison.Ordinal)) return content;
        var text = Attribute(header, "Text");
        if (!string.IsNullOrEmpty(text) && !text.StartsWith("{Binding", StringComparison.Ordinal)) return text;
        var parameter = Attribute(header, "CommandParameter");
        if (parameter.Contains("PriorDay2", StringComparison.Ordinal)) return "动态日期2";
        if (parameter.Contains("PriorDay3", StringComparison.Ordinal)) return "动态日期3";
        if (parameter.Contains("PriorDay4", StringComparison.Ordinal)) return "动态日期4";
        return string.Empty;
    }

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
