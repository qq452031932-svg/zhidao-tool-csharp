using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BaiduBatchTransfer.App.Features.Collect;
using BaiduBatchTransfer.App.Features.ShareAnalytics;
using BaiduBatchTransfer.App.Services;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class CollectLinkCheckTests
{
    [Fact]
    public async Task ChecksOnlySelectedRowsAndWritesResultsBackToThoseRows()
    {
        var checker = new ScriptedChecker(link => link.EndsWith('2')
            ? new(CollectLinkStatus.Invalid, "分享已失效（百度返回 140）。")
            : new(CollectLinkStatus.Accessible, "可访问，读到 1 项文件。"));
        var log = new RecordingRunLog();
        var model = CreateModel(checker, runLog: log);
        AddRows(model, 3);
        Assert.False(model.CheckSelectedLinksCommand.CanExecute(null));

        model.SetSelectedResults([model.Resources[1], model.Resources[2]]);
        Assert.Equal(2, model.SelectedResultCount);
        await model.CheckSelectedLinksCommand.ExecuteAsync(null);

        Assert.Equal(["https://pan.baidu.com/s/1Abcde2", "https://pan.baidu.com/s/1Abcde3"], checker.Checked);
        Assert.Equal(CollectLinkStatus.Unchecked, model.Resources[0].LinkStatus);
        Assert.Equal("已失效", model.Resources[1].LinkStatusText);
        Assert.Equal("可访问", model.Resources[2].LinkStatusText);
        Assert.Contains("140", model.Resources[1].LinkStatusDetail);
        Assert.Contains("检测时间", model.Resources[1].LinkStatusDetail);
        Assert.Contains("检测完成（2 条）", model.ResultActionMessage);
        Assert.Contains("已失效 1", model.ResultActionMessage);
        Assert.False(model.IsCheckingLinks);
        // 运行日志只记序号和结论，不落链接和提取码。
        Assert.Contains(log.Lines, line => line.Contains("第 2 条：已失效"));
        Assert.DoesNotContain(log.Lines, line => line.Contains("pan.baidu.com") || line.Contains("pw2"));
    }

    [Fact]
    public async Task StoppingKeepsFinishedResultsAndRestoresTheRowInProgress()
    {
        var model = default(CollectViewModel)!;
        var checker = new ScriptedChecker(_ => new(CollectLinkStatus.Accessible, "可访问"));
        checker.BlockOn = "https://pan.baidu.com/s/1Abcde2";
        model = CreateModel(checker);
        AddRows(model, 3);
        model.SetSelectedResults(model.Resources);

        var running = model.CheckSelectedLinksCommand.ExecuteAsync(null);
        await checker.Blocked.Task;
        Assert.True(model.IsCheckingLinks);
        Assert.Equal("检测中", model.Resources[1].LinkStatusText);
        Assert.False(model.StartCommand.CanExecute(null));
        Assert.False(model.CheckSelectedLinksCommand.CanExecute(null));
        model.StopLinkCheckCommand.Execute(null);
        await running;

        Assert.Equal(CollectLinkStatus.Accessible, model.Resources[0].LinkStatus);
        Assert.Equal(CollectLinkStatus.Unchecked, model.Resources[1].LinkStatus);
        Assert.Equal(CollectLinkStatus.Unchecked, model.Resources[2].LinkStatus);
        Assert.Contains("已停止", model.ResultActionMessage);
        Assert.Contains("1/3", model.ResultActionMessage);
        Assert.False(model.IsCheckingLinks);
        Assert.True(model.StartCommand.CanExecute(null));
    }

    [Fact]
    public async Task RecheckingAfterStopReplacesEarlierStatus()
    {
        var verdict = CollectLinkStatus.Undetermined;
        var model = CreateModel(new ScriptedChecker(_ => new(verdict, "x")));
        AddRows(model, 1);
        model.SetSelectedResults(model.Resources);
        await model.CheckSelectedLinksCommand.ExecuteAsync(null);
        Assert.Equal("暂无法判断", model.Resources[0].LinkStatusText);
        verdict = CollectLinkStatus.PasswordProblem;
        await model.CheckSelectedLinksCommand.ExecuteAsync(null);
        Assert.Equal("提取码问题", model.Resources[0].LinkStatusText);
    }

    [Fact]
    public async Task ThreeRateLimitedResultsInARowStopTheBatch()
    {
        var checker = new ScriptedChecker(_ => new(CollectLinkStatus.Undetermined, "百度要求验证", LooksRateLimited: true));
        var model = CreateModel(checker);
        AddRows(model, 5);
        model.SetSelectedResults(model.Resources);
        await model.CheckSelectedLinksCommand.ExecuteAsync(null);
        Assert.Equal(3, checker.Checked.Count);
        Assert.Equal(CollectLinkStatus.Unchecked, model.Resources[3].LinkStatus);
        Assert.Contains("自动停止", model.ResultActionMessage);
    }

    [Fact]
    public async Task CheckerExceptionsBecomeUndeterminedAndTheBatchContinues()
    {
        var checker = new ScriptedChecker(link => link.EndsWith('1')
            ? throw new InvalidOperationException("boom")
            : new(CollectLinkStatus.Accessible, "可访问"));
        var model = CreateModel(checker);
        AddRows(model, 2);
        model.SetSelectedResults(model.Resources);
        await model.CheckSelectedLinksCommand.ExecuteAsync(null);
        Assert.Equal(CollectLinkStatus.Undetermined, model.Resources[0].LinkStatus);
        Assert.Equal(CollectLinkStatus.Accessible, model.Resources[1].LinkStatus);
    }

    [Fact]
    public void OpenLinkOpensOnlyTheGivenRowWithItsPassword()
    {
        var launcher = new RecordingLauncher();
        var log = new RecordingRunLog();
        var model = CreateModel(new ScriptedChecker(_ => throw new InvalidOperationException()), launcher, log);
        AddRows(model, 3);
        model.SetSelectedResults(model.Resources);

        model.OpenLink(model.Resources[1]);

        Assert.Equal("https://pan.baidu.com/s/1Abcde2?pwd=pw2", Assert.Single(launcher.Opened).AbsoluteUri);
        Assert.Contains("第 2 条", model.ResultActionMessage);
        Assert.DoesNotContain(log.Lines, line => line.Contains("pan.baidu.com") || line.Contains("pw2"));

        model.OpenLink(new CollectedRow(9, "不在列表里", "https://pan.baidu.com/s/1Zzzzz", "", ""));
        model.Resources.Add(new CollectedRow(4, "坏链接", "https://example.com/s/1Abcde4", "", ""));
        model.OpenLink(model.Resources[3]);
        Assert.Single(launcher.Opened);
        Assert.Contains("不是可识别的百度网盘分享链接", model.ResultActionMessage);
    }

    [Fact]
    public void RightClickTargetsTheRowAndKeepsOrReplacesSelectionConsistently()
    {
        RunSta(() =>
        {
            var launcher = new RecordingLauncher();
            var model = CreateModel(new ScriptedChecker(_ => new(CollectLinkStatus.Accessible, "ok")), launcher);
            AddRows(model, 3);
            var grid = new DataGrid
            {
                DataContext = model, ItemsSource = model.Resources, AutoGenerateColumns = false,
                SelectionMode = DataGridSelectionMode.Extended, SelectionUnit = DataGridSelectionUnit.FullRow,
            };
            grid.Columns.Add(new DataGridTextColumn { Header = "资源名称", Binding = new System.Windows.Data.Binding("Name") });
            grid.Measure(new Size(714, 300));
            grid.Arrange(new Rect(0, 0, 714, 300));
            grid.UpdateLayout();
            CollectResultSelectionMenu.Attach(grid);
            var menu = Assert.IsType<ContextMenu>(grid.ContextMenu);

            // 已选中第 1、2 条，右键第 2 条：保留多选，「打开链接」只开第 2 条。
            grid.SelectedItems.Add(model.Resources[0]);
            grid.SelectedItems.Add(model.Resources[1]);
            CollectResultSelectionMenu.PointAt(grid, model.Resources[1]);
            Assert.Equal(2, model.SelectedResultCount);
            Click(menu, "打开链接");
            Assert.Equal("https://pan.baidu.com/s/1Abcde2?pwd=pw2", Assert.Single(launcher.Opened).AbsoluteUri);

            // 右键未选中的第 3 条：改为只选第 3 条，检测不会用到旧的选择。
            CollectResultSelectionMenu.PointAt(grid, model.Resources[2]);
            Assert.Equal([model.Resources[2]], grid.SelectedItems.Cast<CollectedRow>());
            Assert.Equal(1, model.SelectedResultCount);
            Click(menu, "检测链接有效性");
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Assert.Equal(CollectLinkStatus.Accessible, model.Resources[2].LinkStatus);
            Assert.Equal(CollectLinkStatus.Unchecked, model.Resources[0].LinkStatus);

            // 右键空白处：不改变选择，也没有可打开的目标。
            CollectResultSelectionMenu.PointAt(grid, null);
            Click(menu, "打开链接");
            Assert.Single(launcher.Opened);
            Assert.Equal(1, model.SelectedResultCount);
            model.DisposeAsync().AsTask().GetAwaiter().GetResult();
        });
    }

    [Fact]
    public void MenuItemsAreHiddenWhenTheServicesAreNotProvided()
    {
        RunSta(() =>
        {
            var model = new CollectViewModel(new FakeCollectHelperSession(
                CollectHelperClient.Create(new FakeCollectHelperProcess())), action => action());
            Assert.False(model.CanOpenLinks);
            Assert.False(model.CanCheckLinksAtAll);
            AddRows(model, 1);
            model.SetSelectedResults(model.Resources);
            Assert.False(model.CheckSelectedLinksCommand.CanExecute(null));
            model.DisposeAsync().AsTask().GetAwaiter().GetResult();
        });
    }

    private static CollectViewModel CreateModel(IShareLinkChecker checker, IShareLinkLauncher? launcher = null,
        IRunLog? runLog = null) =>
        new(new FakeCollectHelperSession(CollectHelperClient.Create(new FakeCollectHelperProcess())), action => action(),
            runLog: runLog, linkChecker: checker, linkLauncher: launcher ?? new RecordingLauncher(),
            linkCheckDelay: _ => Task.CompletedTask);

    private static void AddRows(CollectViewModel model, int count)
    {
        foreach (var index in Enumerable.Range(1, count))
            model.Resources.Add(new CollectedRow(index, $"资源{index}", $"https://pan.baidu.com/s/1Abcde{index}", $"pw{index}", ""));
    }

    private static void Click(ContextMenu menu, string caption) =>
        menu.Items.OfType<MenuItem>().Single(item => ((string)item.Header).StartsWith(caption, StringComparison.Ordinal))
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    private static void RunSta(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception error) { failure = error; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "STA test timed out.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class ScriptedChecker(Func<string, ShareLinkCheckResult> verdict) : IShareLinkChecker
    {
        public List<string> Checked { get; } = [];
        public string? BlockOn { get; set; }
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ShareLinkCheckResult> CheckAsync(string link, string pwd, CancellationToken cancellationToken)
        {
            if (link == BlockOn)
            {
                Blocked.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            Checked.Add(link);
            return verdict(link);
        }
    }

    private sealed class RecordingLauncher : IShareLinkLauncher
    {
        public List<Uri> Opened { get; } = [];

        public bool TryOpen(Uri uri, out string safeMessage)
        {
            Opened.Add(uri);
            safeMessage = "已在默认浏览器中打开链接。";
            return true;
        }
    }

    private sealed class RecordingRunLog : IRunLog
    {
        public List<string> Lines { get; } = [];
        public void Write(string source, string? message) => Lines.Add($"[{source}] {message}");
    }
}
