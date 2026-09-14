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
        Assert.Contains("#00796B", markup, StringComparison.Ordinal);
        Assert.Contains("Property=\"Background\" Value=\"White\"", markup, StringComparison.Ordinal);
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
    public void BrandColorsMeetNormalTextContrastAndHighContrastUsesSystemBrushes()
    {
        var document = XDocument.Load(FindRepositoryFile(
            "src",
            "BaiduBatchTransfer.App",
            "Features",
            "Licensing",
            "LicenseActivationWindow.xaml"));
        var markup = document.ToString(SaveOptions.DisableFormatting);

        Assert.Contains("SystemParameters.HighContrast", markup, StringComparison.Ordinal);
        Assert.Contains("SystemColors.WindowBrushKey", markup, StringComparison.Ordinal);
        Assert.Contains("SystemColors.WindowTextBrushKey", markup, StringComparison.Ordinal);
        Assert.Contains("SystemColors.HighlightBrushKey", markup, StringComparison.Ordinal);
        Assert.Contains("SystemColors.HighlightTextBrushKey", markup, StringComparison.Ordinal);

        var headerBackground = StyleSetterValue(document, "ActivationHeaderStyle", "Background");
        var titleForeground = StyleSetterValue(document, "ActivationHeaderTitleStyle", "Foreground");
        var subtitleForeground = StyleSetterValue(document, "ActivationHeaderSubtitleStyle", "Foreground");
        var buttonBackground = StyleSetterValue(document, "ActivationPrimaryButtonStyle", "Background");
        var buttonForeground = StyleSetterValue(document, "ActivationPrimaryButtonStyle", "Foreground");
        Assert.Equal("#00796B", headerBackground);
        Assert.Equal(headerBackground, buttonBackground);
        Assert.True(ContrastRatio(titleForeground, headerBackground) >= 4.5);
        Assert.True(ContrastRatio(subtitleForeground, headerBackground) >= 4.5);
        Assert.True(ContrastRatio(buttonForeground, buttonBackground) >= 4.5);

        var header = Assert.Single(document.Descendants(), element =>
            AttributeValue(element, XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) ==
            "ActivationHeader");
        var activateButton = Assert.Single(document.Descendants(), element =>
            AttributeValue(element, XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) ==
            "ActivateButton");
        Assert.Equal("{StaticResource ActivationHeaderStyle}", (string?)header.Attribute("Style"));
        Assert.Equal("{StaticResource ActivationPrimaryButtonStyle}", (string?)activateButton.Attribute("Style"));
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
            var buttonTop = activate.TransformToAncestor(scrollViewer)
                .Transform(new Point(0, 0)).Y;
            var buttonBottom = activate.TransformToAncestor(scrollViewer)
                .Transform(new Point(0, activate.ActualHeight)).Y;
            Assert.True(buttonTop >= 0, $"Activate button top was {buttonTop:N1}px above the viewport.");
            Assert.True(buttonBottom <= scrollViewer.ViewportHeight,
                $"Activate button bottom was {buttonBottom:N1}px below the {scrollViewer.ViewportHeight:N1}px viewport.");

            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ActiveRequestBlocksEscapeAndSystemCloseUntilTheAttemptFinishes()
    {
        await RunStaAsync(async () =>
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var completion = new TaskCompletionSource<LicenseActivationResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var viewModel = new LicenseActivationViewModel(
                "知道综合工具",
                (_, _) =>
                {
                    started.TrySetResult();
                    return completion.Task;
                })
            {
                Card = "CARD"
            };
            var window = new LicenseActivationWindow(viewModel);
            var cancel = Assert.IsType<Button>(window.FindName("CancelButton"));
            window.Show();

            var activation = viewModel.ActivateCommand.ExecuteAsync(null);
            await started.Task;
            window.UpdateLayout();

            Assert.False(viewModel.CloseCommand.CanExecute(null));
            Assert.False(cancel.IsEnabled);
            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(window.IsVisible);
            window.Close();
            Assert.True(window.IsVisible);

            completion.SetResult(new LicenseActivationResult(false, "license_not_found"));
            await activation;

            Assert.True(viewModel.CloseCommand.CanExecute(null));
            Assert.True(cancel.IsEnabled);
            window.Close();
            Assert.False(window.IsVisible);
        });
    }

    [Fact]
    public async Task ControlledCancellationCancelsTheAttemptBeforeClosingTheWindow()
    {
        await RunStaAsync(async () =>
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var viewModel = new LicenseActivationViewModel(
                "知道综合工具",
                async (_, cancellationToken) =>
                {
                    started.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }
                    finally
                    {
                        if (cancellationToken.IsCancellationRequested)
                            cancellationObserved.TrySetResult();
                    }
                    return new LicenseActivationResult(false, "license_not_found");
                })
            {
                Card = "CARD"
            };
            var window = new LicenseActivationWindow(viewModel);
            window.Show();
            var activation = viewModel.ActivateCommand.ExecuteAsync(null);
            await started.Task;

            window.CancelActivationAndClose();

            await cancellationObserved.Task;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => activation);
            Assert.False(window.IsVisible);
        });
    }

    [Fact]
    public async Task SuccessfulActivationStillUsesTheControlledInternalClosePath()
    {
        await RunStaAsync(() =>
        {
            var viewModel = new LicenseActivationViewModel(
                "知道综合工具",
                (_, _) => Task.FromResult(new LicenseActivationResult(true, "ok")))
            {
                Card = "CARD"
            };
            var window = new LicenseActivationWindow(viewModel);
            _ = window.Dispatcher.BeginInvoke(new Action(() =>
                viewModel.ActivateCommand.Execute(null)));

            Assert.True(window.ShowDialog());
            return Task.CompletedTask;
        });
    }

    private static double ElementBottom(Visual ancestor, FrameworkElement element) =>
        element.TransformToAncestor(ancestor).Transform(new Point(0, element.ActualHeight)).Y;

    private static string? AttributeValue(XElement element, XName name) =>
        (string?)element.Attribute(name);

    private static string StyleSetterValue(XDocument document, string styleKey, string property)
    {
        var xamlNamespace = XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml");
        var style = Assert.Single(document.Descendants(), element =>
            element.Name.LocalName == "Style" && AttributeValue(element, xamlNamespace) == styleKey);
        var setter = Assert.Single(style.Elements(), element =>
            element.Name.LocalName == "Setter" && (string?)element.Attribute("Property") == property);
        return setter.Attribute("Value")!.Value;
    }

    private static double ContrastRatio(string foreground, string background)
    {
        var lighter = Math.Max(RelativeLuminance(foreground), RelativeLuminance(background));
        var darker = Math.Min(RelativeLuminance(foreground), RelativeLuminance(background));
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(string color)
    {
        var parsed = Assert.IsType<Color>(ColorConverter.ConvertFromString(color));
        Assert.Equal(byte.MaxValue, parsed.A);
        var red = parsed.R / 255d;
        var green = parsed.G / 255d;
        var blue = parsed.B / 255d;
        return 0.2126 * Linearize(red) + 0.7152 * Linearize(green) + 0.0722 * Linearize(blue);
    }

    private static double Linearize(double channel) =>
        channel <= 0.04045
            ? channel / 12.92
            : Math.Pow((channel + 0.055) / 1.055, 2.4);

    private static void MeasureAndArrange(FrameworkElement element, Size size)
    {
        element.Measure(size);
        element.Arrange(new Rect(size));
        element.UpdateLayout();
    }

    private static Task RunStaAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(
                new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(() => _ = ExecuteAsync()));
            System.Windows.Threading.Dispatcher.Run();

            async Task ExecuteAsync()
            {
                try
                {
                    await action();
                    completion.TrySetResult();
                }
                catch (Exception error)
                {
                    completion.TrySetException(error);
                }
                finally
                {
                    if (!dispatcher.HasShutdownStarted)
                        dispatcher.InvokeShutdown();
                }
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
