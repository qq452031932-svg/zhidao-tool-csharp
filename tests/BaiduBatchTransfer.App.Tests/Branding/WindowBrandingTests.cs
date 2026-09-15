using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

namespace BaiduBatchTransfer.App.Tests.Branding;

public sealed class WindowBrandingTests
{
    private const string ProductIconResource = "{DynamicResource ProductWindowIcon}";
    private const string ProductIconPackUri = "/知道综合工具;component/Assets/Branding/知道综合工具.png";
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace PresentationOptions = "http://schemas.microsoft.com/winfx/2006/xaml/presentation/options";

    [Fact]
    public void AppDefinesOneFrozenCanonicalWindowIcon()
    {
        var appProject = AppProjectDirectory();
        var definitions = Directory
            .EnumerateFiles(appProject, "*.xaml", SearchOption.AllDirectories)
            .Select(XDocument.Load)
            .SelectMany(document => document.Descendants())
            .Where(element => (string?)element.Attribute(Xaml + "Key") == "ProductWindowIcon")
            .ToArray();

        var resource = Assert.Single(definitions);
        Assert.Equal(Presentation + "BitmapImage", resource.Name);
        Assert.Equal(ProductIconPackUri, (string?)resource.Attribute("UriSource"));
        Assert.Equal("OnLoad", (string?)resource.Attribute("CacheOption"));
        Assert.Equal("True", (string?)resource.Attribute(PresentationOptions + "Freeze"));
    }

    [Fact]
    public void EveryProductionWindowUsesTheSharedProductIcon()
    {
        var windows = ProductionWindows();

        Assert.True(windows.Count >= 12, $"Expected at least the current 12 production windows, but found {windows.Count}.");
        foreach (var window in windows)
        {
            Assert.Equal(ProductIconResource, (string?)window.Document.Root!.Attribute("Icon"));
            Assert.DoesNotContain("Assets/Branding/", window.Text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Assets\\Branding\\", window.Text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void MainWindowUsesTheExactProductTitleAndNoWindowUsesTheStaleProductTitle()
    {
        var windows = ProductionWindows();
        var mainWindow = Assert.Single(windows, window =>
            Path.GetFileName(window.Path).Equals("MainWindow.xaml", StringComparison.OrdinalIgnoreCase));

        Assert.Equal("知道综合工具", (string?)mainWindow.Document.Root!.Attribute("Title"));
        foreach (var window in windows)
            Assert.DoesNotContain("百度批量转存", window.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CanonicalProductPngLoadsThroughTheWpfResourcePack()
    {
        await RunStaAsync(() =>
        {
            var assemblyName = typeof(App).Assembly.GetName().Name;
            var resourceUri = new Uri(
                $"/{assemblyName};component/Assets/Branding/知道综合工具.png",
                UriKind.Relative);
            var resource = System.Windows.Application.GetResourceStream(resourceUri);
            Assert.NotNull(resource);
            using var stream = resource.Stream;
            var image = BitmapFrame.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            image.Freeze();

            Assert.True(image.IsFrozen);
            Assert.True(image.PixelWidth > 0);
            Assert.True(image.PixelHeight > 0);
        });
    }

    private static IReadOnlyList<WindowXaml> ProductionWindows() => Directory
        .EnumerateFiles(AppProjectDirectory(), "*.xaml", SearchOption.AllDirectories)
        .Select(path => new WindowXaml(path, File.ReadAllText(path), XDocument.Load(path)))
        .Where(file => file.Document.Root?.Name == Presentation + "Window")
        .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
        .ToArray();

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
            Name = "WindowBrandingTests WPF resource loader",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static string AppProjectDirectory() => Path.Combine(
        RepositoryRoot(), "src", "BaiduBatchTransfer.App");

    private static string RepositoryRoot([CallerFilePath] string sourceFilePath = "")
    {
        var current = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "BaiduBatchTransfer.sln"))) return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed record WindowXaml(string Path, string Text, XDocument Document);
}
