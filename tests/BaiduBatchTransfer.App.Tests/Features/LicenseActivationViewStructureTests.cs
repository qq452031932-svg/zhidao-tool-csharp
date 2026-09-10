using System.Xml.Linq;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class LicenseActivationViewStructureTests
{
    [Fact]
    public void ActivationWindowContainsOnlyTheApprovedMinimalInteractionSurface()
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
        Assert.Contains(buttons, button => (string?)button.Attribute("Content") == "激活");
        Assert.Contains(buttons, button => (string?)button.Attribute("Content") == "关闭");

        var textBindings = elements
            .Where(element => element.Name.LocalName == "TextBlock")
            .Select(element => (string?)element.Attribute("Text"))
            .ToArray();
        Assert.Equal(2, textBindings.Length);
        Assert.Contains("{Binding ProductName}", textBindings);
        Assert.Contains("{Binding StatusMessage}", textBindings);

        var markup = document.ToString(SaveOptions.DisableFormatting);
        Assert.Contains("Text=\"{Binding Card, Mode=TwoWay", markup, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding ActivateCommand}\"", markup, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding CloseCommand}\"", markup, StringComparison.Ordinal);
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
