using System.Runtime.CompilerServices;

namespace BaiduBatchTransfer.App.Tests;

public sealed class MainWindowChromeTests
{
    [Fact]
    public void ShellUsesCustomChromeAndNamedHeaderRegions()
    {
        var xaml = ReadAppFile("MainWindow.xaml");

        Assert.Contains("WindowStyle=\"None\"", xaml);
        Assert.Contains("<shell:WindowChrome", xaml);
        Assert.Contains("CaptionHeight=\"0\"", xaml);
        Assert.Contains("ResizeBorderThickness=\"6\"", xaml);
        Assert.Contains("x:Name=\"CustomTitleBar\"", xaml);
        Assert.Contains("Height=\"36\"", ExtractNamedElement(xaml, "CustomTitleBar"));
        Assert.Contains("MouseLeftButtonDown=\"OnTitleBarMouseLeftButtonDown\"", xaml);
        Assert.Contains("x:Name=\"ProductHeader\"", xaml);
        Assert.Contains("Height=\"64\"", ExtractNamedElement(xaml, "ProductHeader"));
    }

    [Fact]
    public void ShellKeepsExactThreeColumnBodyAndRemovesTheOldGlobalFooter()
    {
        var xaml = ReadAppFile("MainWindow.xaml");
        var body = ExtractGridColumnDefinitions(xaml, "ShellBody");

        Assert.Contains("<ColumnDefinition Width=\"240\" />", body);
        Assert.Contains("<ColumnDefinition Width=\"170\" />", body);
        Assert.Contains("<ColumnDefinition Width=\"*\" />", body);
        Assert.DoesNotContain("<ColumnDefinition Width=\"180\" />", body);
        Assert.DoesNotContain("<RowDefinition Height=\"36\" />", xaml);
        Assert.DoesNotContain("<Border Grid.Row=\"2\"", xaml);
    }

    [Fact]
    public void CaptionButtonsAreKeyboardAccessibleAndConnectedToHandlers()
    {
        var xaml = ReadAppFile("MainWindow.xaml");
        var codeBehind = ReadAppFile("MainWindow.xaml.cs");

        Assert.Contains("x:Name=\"MinimizeButton\"", xaml);
        Assert.Contains("AutomationProperties.Name=\"最小化\"", xaml);
        Assert.Contains("Click=\"OnMinimizeClick\"", xaml);
        Assert.Contains("x:Name=\"MaximizeRestoreButton\"", xaml);
        Assert.Contains("AutomationProperties.Name=\"最大化或还原\"", xaml);
        Assert.Contains("Click=\"OnMaximizeRestoreClick\"", xaml);
        Assert.Contains("x:Name=\"CloseButton\"", xaml);
        Assert.Contains("AutomationProperties.Name=\"关闭\"", xaml);
        Assert.Contains("Click=\"OnCloseClick\"", xaml);
        Assert.Contains("WindowChrome.IsHitTestVisibleInChrome=\"True\"", xaml);

        Assert.Contains("void OnMinimizeClick", codeBehind);
        Assert.Contains("void OnMaximizeRestoreClick", codeBehind);
        Assert.Contains("void OnCloseClick", codeBehind);
        Assert.Contains("void OnTitleBarMouseLeftButtonDown", codeBehind);
        Assert.Contains("e.ClickCount == 2", codeBehind);
        Assert.Contains("DragMove()", codeBehind);
    }

    [Fact]
    public void AppMergesSuperPanThemeWithRequiredTokensAndCompactStyles()
    {
        var appXaml = ReadAppFile("App.xaml");
        var theme = ReadAppFile("Styles", "SuperPanTheme.xaml");

        Assert.Contains("Source=\"Styles/SuperPanTheme.xaml\"", appXaml);
        Assert.Equal(1, appXaml.Split("x:Key=\"BooleanToVisibilityConverter\"", StringSplitOptions.None).Length - 1);
        Assert.Contains("#08B7B7", theme);
        Assert.Contains("#F7F9FC", theme);
        Assert.Contains("#E3E8EF", theme);
        Assert.Contains("#EAF6FF", theme);
        Assert.Contains("#DFF7E8", theme);
        Assert.Contains("#FFF6D8", theme);
        Assert.Contains("#FDE8E8", theme);
        Assert.Contains("x:Key=\"ToolbarButtonStyle\"", theme);
        Assert.Contains("Height\" Value=\"34\"", theme);
        Assert.Contains("x:Key=\"SecondaryButtonStyle\"", theme);
        Assert.Contains("x:Key=\"BorderedPanelStyle\"", theme);
        Assert.Contains("x:Key=\"NavItemStyle\"", theme);
        Assert.Contains("x:Key=\"DenseDataGridStyle\"", theme);
    }

    private static string ReadAppFile(params string[] relativeSegments)
    {
        var pathSegments = new[]
        {
            FindRepositoryRoot(),
            "src",
            "BaiduBatchTransfer.App",
        }.Concat(relativeSegments).ToArray();
        return File.ReadAllText(Path.Combine(pathSegments));
    }

    private static string ExtractNamedElement(string xaml, string elementName)
    {
        var nameIndex = xaml.IndexOf($"x:Name=\"{elementName}\"", StringComparison.Ordinal);
        Assert.True(nameIndex >= 0, $"未找到 {elementName} 元素。");
        var start = xaml.LastIndexOf('<', nameIndex);
        var end = xaml.IndexOf('>', nameIndex);
        Assert.True(start >= 0 && end >= 0, $"无法读取 {elementName} 元素。");
        return xaml[start..(end + 1)];
    }

    private static string ExtractGridColumnDefinitions(string xaml, string elementName)
    {
        var nameIndex = xaml.IndexOf($"x:Name=\"{elementName}\"", StringComparison.Ordinal);
        Assert.True(nameIndex >= 0, $"未找到 {elementName} 元素。");
        var end = xaml.IndexOf("</Grid.ColumnDefinitions>", nameIndex, StringComparison.Ordinal);
        Assert.True(end >= 0, $"未找到 {elementName} 的列定义。");
        return xaml[nameIndex..end];
    }

    private static string FindRepositoryRoot([CallerFilePath] string sourceFilePath = "")
    {
        var current = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "BaiduBatchTransfer.sln")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("未找到测试仓库目录。");
    }
}
