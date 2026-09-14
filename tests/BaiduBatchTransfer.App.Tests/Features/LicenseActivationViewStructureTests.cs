using System.Xml.Linq;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class LicenseActivationViewStructureTests
{
    [Fact]
    public void ActivationWindowContainsTheApprovedBrandedInteractionSurface()
    {
        var document = XDocument.Load(FindRepositoryFile(
            "src",
            "BaiduBatchTransfer.App",
            "Features",
            "Licensing",
            "LicenseActivationWindow.xaml"));
        var elements = document.Descendants().ToArray();

        Assert.Single(elements, element => element.Name.LocalName == "TextBox");
        var buttons = elements.Where(element => element.Name.LocalName == "Button").ToArray();
        Assert.Equal(2, buttons.Length);
        Assert.Contains(buttons, button => (string?)button.Attribute("Content") == "{Binding ActivateButtonText}");
        Assert.Contains(buttons, button => (string?)button.Attribute("Content") == "暂不激活");
        Assert.Contains(buttons, button => (string?)button.Attribute("IsDefault") == "True");
        Assert.Contains(buttons, button => (string?)button.Attribute("IsCancel") == "True");

        var textBindings = elements
            .Where(element => element.Name.LocalName == "TextBlock")
            .Select(element => (string?)element.Attribute("Text"))
            .ToArray();
        Assert.Contains("知道综合工具", textBindings);
        Assert.Contains("软件授权", textBindings);
        Assert.Contains("请输入卡密", textBindings);
        Assert.Contains("卡密将绑定当前电脑，请确认后激活", textBindings);
        Assert.Contains("{Binding StatusMessage}", textBindings);

        var markup = document.ToString(SaveOptions.DisableFormatting);
        Assert.Contains("Text=\"{Binding Card, Mode=TwoWay", markup, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding ActivateCommand}\"", markup, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding CloseCommand}\"", markup, StringComparison.Ordinal);
        Assert.Contains("Background=\"#0F9D8A\"", markup, StringComparison.Ordinal);
        Assert.Contains("Background=\"White\"", markup, StringComparison.Ordinal);
        Assert.Contains("Height=\"48\"", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("续费", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("支付", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("购买", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("到期提醒", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void AccessibilityMetadataNeverIncludesTheCardOrDynamicStatusText()
    {
        var document = XDocument.Load(FindRepositoryFile(
            "src",
            "BaiduBatchTransfer.App",
            "Features",
            "Licensing",
            "LicenseActivationWindow.xaml"));
        var automationNames = document
            .Descendants()
            .Attributes()
            .Where(attribute => attribute.Name.LocalName == "AutomationProperties.Name")
            .Select(attribute => attribute.Value)
            .ToArray();

        Assert.DoesNotContain(automationNames, value =>
            value.Contains("Card", StringComparison.Ordinal) ||
            value.Contains("StatusMessage", StringComparison.Ordinal));
        Assert.Contains("授权状态", automationNames);
        Assert.Contains("卡密输入", automationNames);
        Assert.Contains("立即激活", automationNames);
        Assert.Contains("暂不激活", automationNames);
    }

    private static string FindRepositoryFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate {Path.Combine(segments)}.");
    }
}
