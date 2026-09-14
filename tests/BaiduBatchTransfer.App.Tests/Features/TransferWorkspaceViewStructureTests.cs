using System.Xml.Linq;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class TransferWorkspaceViewStructureTests
{
    [Fact]
    public void CurrentImportCleanupButtonIsAdjacentToExcelImportAndExplainsLocalOnlyScope()
    {
        var document = XDocument.Load(FindTransferWorkspaceView());
        var buttons = document.Descendants().Where(element => element.Name.LocalName == "Button").ToList();
        var selectExcelIndex = buttons.FindIndex(button => (string?)button.Attribute("Content") == "选择 Excel");

        Assert.True(selectExcelIndex >= 0);
        var clearButton = Assert.Single(buttons, button =>
            (string?)button.Attribute("AutomationProperties.Name") == "清空当前导入");
        Assert.Same(buttons[selectExcelIndex + 1], clearButton);
        Assert.Same(buttons[selectExcelIndex].Parent, clearButton.Parent);
        Assert.Equal("清空", (string?)clearButton.Attribute("Content"));
        Assert.Equal(buttons[selectExcelIndex].Attribute("Padding")?.Value,
            clearButton.Attribute("Padding")?.Value);
        Assert.Equal(buttons[selectExcelIndex].Attribute("Margin")?.Value,
            clearButton.Attribute("Margin")?.Value);
        Assert.Equal("{Binding ClearCurrentImportCommand}", (string?)clearButton.Attribute("Command"));
        Assert.Equal("清空当前导入", (string?)clearButton.Attribute("AutomationProperties.Name"));

        const string expectedScope = "只清除本地待处理导入，不影响百度网盘文件和分享链接";
        var explanation = (string?)clearButton.Attribute("AutomationProperties.HelpText") ??
                          (string?)clearButton.Attribute("ToolTip");
        Assert.Contains(expectedScope, explanation);
        Assert.Contains("DangerHistoryButton", (string?)clearButton.Attribute("Style"));
    }

    private static string FindTransferWorkspaceView()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "src"))) root = root.Parent;
        Assert.NotNull(root);
        return Path.Combine(root.FullName, "src", "BaiduBatchTransfer.App", "Features", "Transfers",
            "TransferWorkspaceView.xaml");
    }
}
