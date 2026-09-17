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
        Assert.Contains("示例：ABCD2345-EFGH6723-JKLM4567-NPQR2345（4 组，每组 8 位）", textBindings);
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
        Assert.Contains("#0F766E", markup, StringComparison.Ordinal);
        Assert.Contains("Property=\"Background\" Value=\"#FFFFFF\"", markup, StringComparison.Ordinal);
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
        Assert.Contains("卡密格式示例：4 组，每组 8 位，用横线分隔", automationNames);
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
        Assert.Equal("#FFFFFF", headerBackground);
        Assert.Equal("#0F766E", buttonBackground);
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
    public void EveryBodyElementUsesAnExplicitHighContrastAwareStyle()
    {
        var document = XDocument.Load(FindRepositoryFile(
            "src",
            "BaiduBatchTransfer.App",
            "Features",
            "Licensing",
            "LicenseActivationWindow.xaml"));

        AssertElementStyle(document, "CardLabel", "ActivationBodyLabelStyle");
        AssertElementStyle(document, "CardInput", "ActivationTextBoxStyle");
        AssertElementStyle(document, "CardExampleHint", "ActivationBodyHintStyle");
        AssertElementStyle(document, "CardBindingHint", "ActivationBodyHintStyle");
        AssertElementStyle(document, "ActivationStatusRegion", "ActivationStatusRegionStyle");
        AssertElementStyle(document, "ActivationStatusText", "ActivationStatusTextStyle");

        AssertHighContrastSetter(document, "ActivationBodyLabelStyle", "Foreground", "SystemColors.WindowTextBrushKey");
        AssertHighContrastSetter(document, "ActivationBodyHintStyle", "Foreground", "SystemColors.WindowTextBrushKey");
        AssertHighContrastSetter(document, "ActivationTextBoxStyle", "Background", "SystemColors.WindowBrushKey");
        AssertHighContrastSetter(document, "ActivationTextBoxStyle", "Foreground", "SystemColors.WindowTextBrushKey");
        AssertHighContrastSetter(document, "ActivationTextBoxStyle", "BorderBrush", "SystemColors.HighlightBrushKey");
        AssertHighContrastSetter(document, "ActivationStatusRegionStyle", "Background", "SystemColors.WindowBrushKey");
        AssertHighContrastSetter(document, "ActivationStatusRegionStyle", "BorderBrush", "SystemColors.HighlightBrushKey");
        AssertHighContrastSetter(document, "ActivationStatusTextStyle", "Foreground", "SystemColors.WindowTextBrushKey");
    }

    [Fact]
    public void LocalStylesInheritTheApplicationThemeWithoutLosingControlBehavior()
    {
        var activation = XDocument.Load(FindRepositoryFile(
            "src",
            "BaiduBatchTransfer.App",
            "Features",
            "Licensing",
            "LicenseActivationWindow.xaml"));
        var theme = XDocument.Load(FindRepositoryFile(
            "src",
            "BaiduBatchTransfer.App",
            "Styles",
            "SuperPanTheme.xaml"));
        var xamlKey = XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml");

        AssertStyleBasedOn(activation, "ActivationPrimaryButtonStyle", "{StaticResource {x:Type Button}}");
        AssertStyleBasedOn(activation, "ActivationSecondaryButtonStyle", "{StaticResource {x:Type Button}}");
        AssertStyleBasedOn(activation, "ActivationTextBoxStyle", "{StaticResource {x:Type TextBox}}");
        var windowStyle = Assert.Single(activation.Root!.Elements(), element =>
            element.Name.LocalName == "Window.Style").Elements().Single();
        Assert.Equal("{StaticResource {x:Type Window}}", (string?)windowStyle.Attribute("BasedOn"));

        var implicitWindow = Assert.Single(theme.Descendants(), element =>
            element.Name.LocalName == "Style" &&
            element.Attribute(xamlKey) is null &&
            (string?)element.Attribute("TargetType") == "Window");
        Assert.Contains(implicitWindow.Elements(), element =>
            element.Name.LocalName == "Setter" &&
            (string?)element.Attribute("Property") == "FontFamily" &&
            (string?)element.Attribute("Value") == "Microsoft YaHei UI");
        var implicitButton = Assert.Single(theme.Descendants(), element =>
            element.Name.LocalName == "Style" &&
            element.Attribute(xamlKey) is null &&
            (string?)element.Attribute("TargetType") == "Button");
        Assert.Contains(implicitButton.Elements(), element =>
            element.Name.LocalName == "Setter" &&
            (string?)element.Attribute("Property") == "Cursor" &&
            (string?)element.Attribute("Value") == "Hand");
        Assert.Contains(implicitButton.Descendants(), element => element.Name.LocalName == "ControlTemplate");
        Assert.Contains(implicitButton.Descendants(), element =>
            element.Name.LocalName == "Trigger" &&
            (string?)element.Attribute("Property") == "IsEnabled" &&
            (string?)element.Attribute("Value") == "False");
        var implicitTextBox = Assert.Single(theme.Descendants(), element =>
            element.Name.LocalName == "Style" &&
            element.Attribute(xamlKey) is null &&
            (string?)element.Attribute("TargetType") == "TextBox");
        Assert.Contains(implicitTextBox.Descendants(), element => element.Name.LocalName == "ControlTemplate");
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
                Card = "ABCD2345-EFGH6723-JKLM4567-NPQR2345"
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
    public async Task ControlledCancellationCompletesTheRealCommandWithoutAnUnhandledDispatcherException()
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
                Card = "ABCD2345-EFGH6723-JKLM4567-NPQR2345"
            };
            var window = new LicenseActivationWindow(viewModel);
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            var unhandledExceptions = new List<Exception>();
            System.Windows.Threading.DispatcherUnhandledExceptionEventHandler handler = (_, args) =>
            {
                unhandledExceptions.Add(args.Exception);
                args.Handled = true;
            };
            dispatcher.UnhandledException += handler;
            window.Show();
            System.Windows.Input.ICommand command = viewModel.ActivateCommand;
            command.Execute(null);
            await started.Task;

            window.CancelActivationAndClose();

            await cancellationObserved.Task;
            var execution = Assert.IsAssignableFrom<Task>(viewModel.ActivateCommand.ExecutionTask);
            try
            {
                Exception? executionException = null;
                try
                {
                    await execution;
                }
                catch (Exception error)
                {
                    executionException = error;
                }
                await dispatcher.InvokeAsync(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);

                Assert.Null(executionException);
                Assert.Equal(TaskStatus.RanToCompletion, execution.Status);
                Assert.False(viewModel.ActivateCommand.IsRunning);
                Assert.False(viewModel.IsActivating);
                Assert.False(window.IsVisible);
                Assert.Empty(unhandledExceptions);
            }
            finally
            {
                dispatcher.UnhandledException -= handler;
            }
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
                Card = "ABCD2345-EFGH6723-JKLM4567-NPQR2345"
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

    private static void AssertElementStyle(XDocument document, string elementName, string styleKey)
    {
        var xamlName = XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml");
        var element = Assert.Single(document.Descendants(), candidate =>
            AttributeValue(candidate, xamlName) == elementName);
        Assert.Equal($"{{StaticResource {styleKey}}}", (string?)element.Attribute("Style"));
    }

    private static void AssertHighContrastSetter(
        XDocument document,
        string styleKey,
        string property,
        string systemBrushKey)
    {
        var xamlKey = XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml");
        var style = Assert.Single(document.Descendants(), element =>
            element.Name.LocalName == "Style" && AttributeValue(element, xamlKey) == styleKey);
        var trigger = Assert.Single(style.Descendants(), element =>
            element.Name.LocalName == "DataTrigger" &&
            ((string?)element.Attribute("Binding"))?.Contains(
                "SystemParameters.HighContrast", StringComparison.Ordinal) == true);
        var setter = Assert.Single(trigger.Elements(), element =>
            element.Name.LocalName == "Setter" && (string?)element.Attribute("Property") == property);
        Assert.Contains(systemBrushKey, setter.Attribute("Value")!.Value, StringComparison.Ordinal);
    }

    private static void AssertStyleBasedOn(XDocument document, string styleKey, string expectedBasedOn)
    {
        var xamlKey = XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml");
        var style = Assert.Single(document.Descendants(), element =>
            element.Name.LocalName == "Style" && AttributeValue(element, xamlKey) == styleKey);
        Assert.Equal(expectedBasedOn, (string?)style.Attribute("BasedOn"));
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
