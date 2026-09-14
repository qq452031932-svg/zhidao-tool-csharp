using System.Xml.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BaiduBatchTransfer.App.Features.Licensing;

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
        Assert.Contains("软件授权", textBindings);
        Assert.Contains("请输入卡密", textBindings);
        Assert.Contains("卡密将绑定当前电脑，请确认后激活", textBindings);
        Assert.Contains("{Binding StatusMessage}", textBindings);
        Assert.Contains("{Binding ProductName}", textBindings);
        Assert.DoesNotContain("知道综合工具", textBindings);

        var markup = document.ToString(SaveOptions.DisableFormatting);
        var input = Assert.Single(elements, element => element.Name.LocalName == "TextBox");
        Assert.Equal("48", (string?)input.Attribute("Height"));
        var activateButton = Assert.Single(buttons, button =>
            (string?)button.Attribute("Content") == "{Binding ActivateButtonText}");
        Assert.Equal(
            "{Binding ActivateButtonText}",
            activateButton.Attributes().Single(attribute =>
                attribute.Name.LocalName == "AutomationProperties.Name").Value);
        var window = Assert.IsType<XElement>(document.Root);
        Assert.NotEqual("NoResize", (string?)window.Attribute("ResizeMode"));
        Assert.True(double.Parse(window.Attribute("MinWidth")!.Value) < 520);
        Assert.True(double.Parse(window.Attribute("MinHeight")!.Value) < 430);
        var scrollViewer = Assert.Single(elements, element => element.Name.LocalName == "ScrollViewer");
        Assert.Equal("Auto", (string?)scrollViewer.Attribute("VerticalScrollBarVisibility"));
        Assert.Contains("Text=\"{Binding Card, Mode=TwoWay", markup, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding ActivateCommand}\"", markup, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding CloseCommand}\"", markup, StringComparison.Ordinal);
        Assert.Contains("Background=\"#0F9D8A\"", markup, StringComparison.Ordinal);
        Assert.Contains("Background=\"White\"", markup, StringComparison.Ordinal);
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
        Assert.Contains("{Binding ActivateButtonText}", automationNames);
        Assert.Contains("暂不激活", automationNames);
    }

    [Fact]
    public async Task ActivationWindowKeepsActionsVisibleOrScrollAccessibleWhenSpaceShrinks()
    {
        await RunStaAsync(() =>
        {
            var viewModel = new LicenseActivationViewModel(
                "知道综合工具",
                (_, _) => Task.FromResult(new LicenseActivationResult(false, "license_not_found")));
            var window = new LicenseActivationWindow(viewModel);
            var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
            var scrollViewer = Assert.IsType<ScrollViewer>(window.FindName("ActivationScrollViewer"));
            var body = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("ActivationBody"));
            var activate = Assert.IsType<Button>(window.FindName("ActivateButton"));
            var cancel = Assert.IsType<Button>(window.FindName("CancelButton"));

            MeasureAndArrange(root, new Size(520, 430));

            Assert.True(activate.ActualHeight > 0);
            Assert.True(cancel.ActualHeight > 0);
            Assert.True(ElementBottom(root, activate) <= root.ActualHeight);
            Assert.True(ElementBottom(root, cancel) <= root.ActualHeight);

            body.LayoutTransform = new ScaleTransform(1, 1.5);
            MeasureAndArrange(root, new Size(360, 250));

            Assert.True(scrollViewer.ScrollableHeight > 0);
            Assert.True(activate.ActualHeight > 0);
            Assert.True(cancel.ActualHeight > 0);
            scrollViewer.ScrollToEnd();
            scrollViewer.UpdateLayout();
            Assert.Equal(scrollViewer.ScrollableHeight, scrollViewer.VerticalOffset, precision: 3);
        });
    }

    private static double ElementBottom(Visual ancestor, FrameworkElement element) =>
        element.TransformToAncestor(ancestor).Transform(new Point(0, element.ActualHeight)).Y;

    private static void MeasureAndArrange(FrameworkElement element, Size size)
    {
        element.Measure(size);
        element.Arrange(new Rect(size));
        element.UpdateLayout();
    }

    private static Task RunStaAsync(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                action();
                completion.TrySetResult();
            }
            catch (Exception error)
            {
                completion.TrySetException(error);
            }
        })
        {
            IsBackground = true,
            Name = "LicenseActivationViewStructureTests STA shell",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
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
