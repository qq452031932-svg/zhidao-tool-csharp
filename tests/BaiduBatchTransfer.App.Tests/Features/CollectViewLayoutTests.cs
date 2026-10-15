using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using BaiduBatchTransfer.App.Features.Collect;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class CollectViewLayoutTests
{
    [Fact]
    public void CookieValidationIsVisibleBelowCookieInputAndWorksWithoutKeywords()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var directory = new DirectoryInfo(AppContext.BaseDirectory);
                while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BaiduBatchTransfer.sln")))
                    directory = directory.Parent;
                Assert.NotNull(directory);
                XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
                XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
                var document = XDocument.Load(Path.Combine(directory.FullName,
                    "src/BaiduBatchTransfer.App/Features/Collect/CollectView.xaml"));
                document.Root!.Attribute(xaml + "Class")!.Remove();
                var resources = document.Root.Element(presentation + "UserControl.Resources")!;
                var existing = resources.Elements().ToArray();
                resources.ReplaceNodes(new XElement(presentation + "ResourceDictionary",
                    new XElement(presentation + "ResourceDictionary.MergedDictionaries",
                        new XElement(presentation + "ResourceDictionary", new XAttribute("Source",
                            "/知道综合工具;component/Styles/SuperPanTheme.xaml"))), existing));
                var view = Assert.IsType<UserControl>(XamlReader.Parse(document.ToString()));
                var model = new CollectViewModel(new FakeCollectHelperSession(
                    CollectHelperClient.Create(new FakeCollectHelperProcess())), action => action());
                view.DataContext = model;
                model.Resources.Add(new(1, "植物大战僵尸 中文版", "https://pan.baidu.com/s/example1", "9ha1", "https://zhidao.baidu.com/question/123.html"));
                model.Resources.Add(new(2, "测试资源二", "https://pan.baidu.com/s/example2", "abcd", "https://zhidao.baidu.com/question/456.html"));
                model.Resources.Add(new(3, "测试资源三", "https://pan.baidu.com/s/example3", "", "https://zhidao.baidu.com/question/789.html"));
                model.Logs.Add("[15:23:27] 第 1 页处理完成：新增 3 条。");
                model.Logs.Add("[15:23:27] 采集完成，共采到 3 条。");

                foreach (var width in new[] { 640d, 714d, 1000d })
                {
                    view.Measure(new Size(width, 650));
                    view.Arrange(new Rect(0, 0, width, 650));
                    view.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                    view.UpdateLayout();
                    var button = Assert.Single(Descendants(view).OfType<Button>(),
                        item => Equals(item.Content, "验证 Cookie"));
                    var cookie = Assert.Single(Descendants(view).OfType<TextBox>(), item =>
                        BindingOperations.GetBinding(item, TextBox.TextProperty)?.Path.Path == "CookieText");
                    Assert.Equal(Visibility.Visible, button.Visibility);
                    Assert.True(button.ActualHeight > 0 && button.ActualWidth > 0);
                    Assert.Same(model.ValidateCookieCommand, button.Command);
                    var position = button.TransformToAncestor(view).Transform(new Point());
                    var cookieBottom = cookie.TransformToAncestor(view).Transform(new Point(0, cookie.ActualHeight));
                    Assert.InRange(position.X, cookieBottom.X - 1, cookieBottom.X + 1);
                    Assert.True(position.Y >= cookieBottom.Y);
                    Assert.True(position.X + button.ActualWidth <= width);
                    // 开始、暂停、停止并排在同一行，窄窗口下也不能被挤出去。
                    var pause = Assert.Single(Descendants(view).OfType<Button>(),
                        item => ReferenceEquals(item.Command, model.PauseResumeCommand));
                    var stop = Assert.Single(Descendants(view).OfType<Button>(), item => Equals(item.Content, "停止"));
                    Assert.Equal("暂停", pause.Content);
                    var pauseOrigin = pause.TransformToAncestor(view).Transform(new Point());
                    var stopOrigin = stop.TransformToAncestor(view).Transform(new Point());
                    Assert.True(pause.ActualWidth > 0 && pauseOrigin.X >= 0);
                    Assert.InRange(stopOrigin.Y, pauseOrigin.Y - 1, pauseOrigin.Y + 1);
                    Assert.True(stopOrigin.X >= pauseOrigin.X + pause.ActualWidth);
                    Assert.True(stopOrigin.X + stop.ActualWidth <= width);
                    foreach (var caption in new[] { "复制链接", "导出 Excel", "送入批量转存" })
                    {
                        var action = Assert.Single(Descendants(view).OfType<Button>(), item => Equals(item.Content, caption));
                        var origin = action.TransformToAncestor(view).Transform(new Point());
                        Assert.Equal(Visibility.Visible, action.Visibility);
                        Assert.True(action.ActualHeight > 0 && action.ActualWidth > 0);
                        Assert.True(origin.X >= 0 && origin.X + action.ActualWidth <= width);
                        Assert.True(origin.Y + action.ActualHeight <= 650);
                    }
                    var results = Assert.Single(Descendants(view).OfType<DataGrid>(), grid => grid.Name == "ResultsGrid");
                    Assert.Equal(DataGridSelectionMode.Extended, results.SelectionMode);
                    Assert.True(results.Columns[1].ActualWidth >= 140);
                    Assert.True(results.ActualHeight >= 100, $"Results height: {results.ActualHeight}");
                    if (width == 714)
                    {
                        var bitmap = new RenderTargetBitmap(714, 650, 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(view);
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        var artifact = Path.Combine(directory.FullName, ".artifacts", "collect-result-layout.png");
                        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
                        using var output = File.Create(artifact);
                        encoder.Save(output);
                    }
                    button.Command.Execute(null);
                    Assert.Contains("请先填入", model.StatusMessage);
                }

                // 有账号列表时：单个 Cookie 框隐藏，账号表格和四个按钮在窄窗口下也完整可见，结果区仍有足够高度。
                var accountList = new ZhidaoAccountListViewModel(new LayoutAccountStore(), new LayoutValidator(),
                    pickFile: () => null, readClipboard: () => null);
                var withAccounts = new CollectViewModel(new FakeCollectHelperSession(
                    CollectHelperClient.Create(new FakeCollectHelperProcess())), action => action(), accountList: accountList);
                foreach (var row in model.Resources) withAccounts.Resources.Add(row);
                view.DataContext = withAccounts;
                foreach (var width in new[] { 640d, 714d, 1000d })
                {
                    view.Measure(new Size(width, 650));
                    view.Arrange(new Rect(0, 0, width, 650));
                    view.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                    view.UpdateLayout();
                    var cookie = Assert.Single(Descendants(view).OfType<TextBox>(), item =>
                        BindingOperations.GetBinding(item, TextBox.TextProperty)?.Path.Path == "CookieText");
                    Assert.False(Shown(cookie, view));
                    var accounts = Assert.Single(Descendants(view).OfType<DataGrid>(), grid => grid.Name == "AccountsGrid");
                    Assert.True(Shown(accounts, view) && accounts.ActualHeight >= 100);
                    Assert.Equal(2, accounts.Items.Count);
                    foreach (var caption in new[] { "导入TXT", "粘贴导入", "验证 1 个", "删除 1 个" })
                    {
                        var action = Assert.Single(Descendants(view).OfType<Button>(), item => Equals(item.Content, caption) && Shown(item, view));
                        var origin = action.TransformToAncestor(view).Transform(new Point());
                        Assert.True(action.ActualWidth > 0 && origin.X >= 0 && origin.X + action.ActualWidth <= width, $"{caption} at {width}");
                    }
                    var results = Assert.Single(Descendants(view).OfType<DataGrid>(), grid => grid.Name == "ResultsGrid");
                    Assert.True(results.ActualHeight >= 100, $"Results height with accounts: {results.ActualHeight}");
                    if (width == 714)
                    {
                        var bitmap = new RenderTargetBitmap(714, 650, 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(view);
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        var artifact = Path.Combine(directory.FullName, ".artifacts", "collect-accounts-layout.png");
                        using var output = File.Create(artifact);
                        encoder.Save(output);
                    }
                }
            }
            catch (Exception error) { failure = error; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Collect page layout test timed out.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class LayoutAccountStore : IZhidaoAccountStore
    {
        public ZhidaoAccountLoadResult Load() => new([
            new(Guid.NewGuid(), "测试账号一", "BDUSS=fakebduss0001xyz; STOKEN=a", true, ZhidaoAccountState.Valid, "ok", DateTimeOffset.Now),
            new(Guid.NewGuid(), "测试账号二", "BDUSS=fakebduss0002xyz; STOKEN=b", false, ZhidaoAccountState.Expired, "未登录", DateTimeOffset.Now),
        ], null);

        public void Save(IReadOnlyList<StoredZhidaoAccount> accounts) { }
    }

    private sealed class LayoutValidator : IZhidaoCookieValidator
    {
        public Task<ZhidaoCookieValidationResult> ValidateAsync(string cookie, CancellationToken cancellationToken) =>
            Task.FromResult(new ZhidaoCookieValidationResult(true, "ok"));
    }

    /// <summary>离线渲染没有窗口，IsVisible 恒为假；改看自身和所有上级的 Visibility。</summary>
    private static bool Shown(UIElement element, DependencyObject root)
    {
        for (DependencyObject? current = element; current is not null && current != root; current = VisualTreeHelper.GetParent(current))
            if (current is UIElement { Visibility: not Visibility.Visible }) return false;
        return true;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
