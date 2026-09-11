using System.Runtime.CompilerServices;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml.Linq;
using BaiduBatchTransfer.App;
using BaiduBatchTransfer.App.Features.Acceptance;
using BaiduBatchTransfer.App.Features.Common;
using BaiduBatchTransfer.App.Features.Imports;
using BaiduBatchTransfer.App.Features.Jobs;
using BaiduBatchTransfer.App.Features.Transfers;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Application.Scheduling;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Application.Imports;
using BaiduBatchTransfer.Infrastructure.Database;
using BaiduBatchTransfer.Infrastructure.Database.Repositories;
using BaiduBatchTransfer.Infrastructure.Excel;
using SuperPan.Licensing.Client;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class TransferWorkspaceViewModelTests
{
    [Fact]
    public async Task LicenseFailureStopsBeforeTransferJobCreationOrRemotePrecheck()
    {
        var gate = new RecordingLicenseGate(isAuthorized: false);
        using var fixture = Fixture.Valid(licenseGate: gate);

        await fixture.ViewModel.StartAsync();

        Assert.Equal([LicenseOperationKind.Transfer], gate.Operations);
        Assert.Equal(0, fixture.BatchService.ConfirmCalls);
        Assert.DoesNotContain("precheck", fixture.Events);
        Assert.Null(fixture.ViewModel.CreatedBatchJobId);
        Assert.Equal("软件尚未激活，联网功能暂不可用。", fixture.ViewModel.StatusMessage);
    }

    [Fact]
    public void GlobalComboBoxTemplateReservesSeparateColumnsForSelectionAndArrow()
    {
        var path = Path.Combine(FindRepositoryRoot(), "src", "BaiduBatchTransfer.App", "Styles", "SuperPanTheme.xaml");
        var document = XDocument.Load(path);
        var xNamespace = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");
        var comboBoxStyle = document.Descendants().Single(element =>
            element.Name.LocalName == "Style" &&
            (string?)element.Attribute("TargetType") == "ComboBox" &&
            element.Attribute(xNamespace + "Key") is null);
        var template = comboBoxStyle.Descendants().Single(element =>
            element.Name.LocalName == "ControlTemplate" &&
            (string?)element.Attribute("TargetType") == "ComboBox");
        var toggleButton = template.Descendants().Single(element => element.Name.LocalName == "ToggleButton");
        var layoutGrid = Assert.Single(toggleButton.Elements(), element => element.Name.LocalName == "Grid");
        var columns = layoutGrid.Elements()
            .Single(element => element.Name.LocalName == "Grid.ColumnDefinitions")
            .Elements()
            .Select(element => (string?)element.Attribute("Width"))
            .ToArray();

        Assert.Equal(new string?[] { "*", "24" }, columns);
        var selection = layoutGrid.Elements().Single(element => element.Name.LocalName == "ContentPresenter");
        var arrow = layoutGrid.Elements().Single(element => element.Name.LocalName == "Path");
        Assert.Equal("0", (string?)selection.Attribute("Grid.Column"));
        Assert.Equal("1", (string?)arrow.Attribute("Grid.Column"));
    }

    [Fact]
    public void BulkPasteInputUsesAdaptiveTwoToEightLineContractAndKeepsOneImportAction()
    {
        var path = Path.Combine(FindRepositoryRoot(), "src", "BaiduBatchTransfer.App", "Features", "Transfers", "TransferWorkspaceView.xaml");
        var document = XDocument.Load(path);
        var input = document.Descendants().Single(element => element.Name.LocalName == "TextBox" &&
            ((string?)element.Attribute("Text"))?.Contains("Import.TextContent", StringComparison.Ordinal) == true);
        Assert.Equal("2", (string?)input.Attribute("MinLines"));
        Assert.Equal("8", (string?)input.Attribute("MaxLines"));
        Assert.Equal("True", (string?)input.Attribute("AcceptsReturn"));
        Assert.Equal("Wrap", (string?)input.Attribute("TextWrapping"));
        Assert.Equal("Auto", (string?)input.Attribute("VerticalScrollBarVisibility"));
        Assert.Equal("Top", (string?)input.Attribute("VerticalContentAlignment"));
        Assert.Null((string?)input.Attribute("Height"));
        Assert.Contains("可一次粘贴多条分享链接", (string?)input.Attribute("ToolTip"));
        Assert.DoesNotContain(document.Descendants(), element => element.Name.LocalName == "TextBlock" &&
            ((string?)element.Attribute("Text"))?.StartsWith("可一次粘贴多条分享链接", StringComparison.Ordinal) == true);
        Assert.Single(document.Descendants(), element =>
            (string?)element.Attribute("Command") == "{Binding Import.ImportTextCommand}");
        var codeBehind = File.ReadAllText(Path.ChangeExtension(path, ".xaml.cs"));
        Assert.Contains("new TransferPasteInputAutoHeight(this, BulkPasteInput)", codeBehind, StringComparison.Ordinal);
        var logs = document.Descendants().Single(element => element.Name.LocalName == "ListBox" &&
            (string?)element.Attribute("ItemsSource") == "{Binding Logs}");
        Assert.Equal("TransferLogsList", (string?)logs.Attribute(XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml") + "Name"));
    }

    [Fact]
    public void TargetDirectoryIsDisplayedByReadOnlyPickerButton()
    {
        var path = Path.Combine(FindRepositoryRoot(), "src", "BaiduBatchTransfer.App", "Features", "Transfers", "TransferWorkspaceView.xaml");
        var document = XDocument.Load(path);
        var button = Assert.Single(document.Descendants(), element =>
            element.Name.LocalName == "Button" &&
            (string?)element.Attribute("Command") == "{Binding PickTargetDirectoryCommand}");

        Assert.Equal("{Binding Wizard.DefaultTargetPath}", (string?)button.Attribute("Content"));
        Assert.Equal("{Binding Wizard.DefaultTargetPath}", (string?)button.Attribute("ToolTip"));
        Assert.DoesNotContain(document.Descendants(), element =>
            element.Name.LocalName == "TextBox" &&
            ((string?)element.Attribute("Text"))?.Contains("Wizard.DefaultTargetPath", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task PickingTargetWithoutSelectedAccountDoesNotOpenPicker()
    {
        var picker = new RecordingTargetDirectoryPicker("/chosen");
        using var fixture = Fixture.Create(accountCount: 2, targetDirectoryPicker: picker);
        fixture.ApplyReadyDraft();
        foreach (var account in fixture.Wizard.Accounts) account.IsSelected = false;

        await fixture.ViewModel.PickTargetDirectoryAsync();

        Assert.Equal(0, picker.Calls);
        Assert.Equal("请先选择参与账号。", fixture.ViewModel.StatusMessage);
    }

    [Fact]
    public async Task ConfirmedTargetUpdatesWizardAndSelectedSingleAccount()
    {
        var picker = new RecordingTargetDirectoryPicker("/新目录");
        using var fixture = Fixture.Create(targetDirectoryPicker: picker);
        fixture.ApplyReadyDraft();
        fixture.Wizard.DefaultTargetPath = "/旧目录";
        fixture.Wizard.Accounts[0].DefaultTargetPath = "/账号旧目录";

        await fixture.ViewModel.PickTargetDirectoryAsync();

        Assert.Equal(fixture.Wizard.Accounts[0].AccountId, picker.AccountId);
        Assert.Equal("/旧目录", picker.InitialPath);
        Assert.Equal("/新目录", fixture.Wizard.DefaultTargetPath);
        Assert.Equal("/新目录", fixture.Wizard.Accounts[0].DefaultTargetPath);
    }

    [Fact]
    public async Task ConfirmedTargetPathIsRedactedFromStatusAndLogs()
    {
        const string privatePath = "/客户甲/绝密目录";
        var picker = new RecordingTargetDirectoryPicker(privatePath);
        using var fixture = Fixture.Create(targetDirectoryPicker: picker);
        fixture.ApplyReadyDraft();

        await fixture.ViewModel.PickTargetDirectoryAsync();

        Assert.Equal(privatePath, fixture.Wizard.DefaultTargetPath);
        Assert.Equal("目标目录已更新。", fixture.ViewModel.StatusMessage);
        Assert.DoesNotContain(fixture.ViewModel.Logs, entry =>
            entry.Message.Contains(privatePath, StringComparison.Ordinal) ||
            entry.Message.Contains("客户甲", StringComparison.Ordinal) ||
            entry.Message.Contains("绝密目录", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConfirmedTargetUpdatesOnlySelectedAccountsInMultiAccountMode()
    {
        var picker = new RecordingTargetDirectoryPicker("/统一目录");
        using var fixture = Fixture.Create(accountCount: 3, targetDirectoryPicker: picker);
        fixture.ApplyReadyDraft();
        fixture.Wizard.DefaultTargetPath = "/默认旧目录";
        fixture.Wizard.Accounts[0].IsSelected = true;
        fixture.Wizard.Accounts[1].IsSelected = true;
        fixture.Wizard.Accounts[2].IsSelected = false;
        fixture.Wizard.Accounts[0].DefaultTargetPath = "/账号一";
        fixture.Wizard.Accounts[1].DefaultTargetPath = "/账号二";
        fixture.Wizard.Accounts[2].DefaultTargetPath = "/未选账号保留";

        await fixture.ViewModel.PickTargetDirectoryAsync();

        Assert.Equal(fixture.Wizard.Accounts[0].AccountId, picker.AccountId);
        Assert.Equal("/统一目录", fixture.Wizard.DefaultTargetPath);
        Assert.Equal("/统一目录", fixture.Wizard.Accounts[0].DefaultTargetPath);
        Assert.Equal("/统一目录", fixture.Wizard.Accounts[1].DefaultTargetPath);
        Assert.Equal("/未选账号保留", fixture.Wizard.Accounts[2].DefaultTargetPath);
    }

    [Fact]
    public async Task ModalConfirmationUsesParticipantSnapshotEvenAfterSelectionChanges()
    {
        var picker = new ControlledTargetDirectoryPicker();
        using var fixture = Fixture.Create(accountCount: 3, targetDirectoryPicker: picker);
        fixture.ApplyReadyDraft();
        fixture.Wizard.DefaultTargetPath = "/默认旧目录";
        fixture.Wizard.Accounts[0].IsSelected = true;
        fixture.Wizard.Accounts[1].IsSelected = true;
        fixture.Wizard.Accounts[2].IsSelected = false;
        fixture.Wizard.Accounts[0].DefaultTargetPath = "/账号一";
        fixture.Wizard.Accounts[1].DefaultTargetPath = "/账号二";
        fixture.Wizard.Accounts[2].DefaultTargetPath = "/账号三保留";
        var browsingAccountId = fixture.Wizard.Accounts[0].AccountId;

        var picking = fixture.ViewModel.PickTargetDirectoryAsync();
        await picker.Started.Task;
        fixture.Wizard.Accounts[1].IsSelected = false;
        fixture.Wizard.Accounts[2].IsSelected = true;
        picker.Complete("/快照目录");
        await picking;

        Assert.Equal(browsingAccountId, picker.AccountId);
        Assert.Equal("/默认旧目录", picker.InitialPath);
        Assert.Equal("/快照目录", fixture.Wizard.Accounts[0].DefaultTargetPath);
        Assert.Equal("/快照目录", fixture.Wizard.Accounts[1].DefaultTargetPath);
        Assert.Equal("/账号三保留", fixture.Wizard.Accounts[2].DefaultTargetPath);
    }

    [Fact]
    public async Task ModalConfirmationIgnoresRemovedCapturedAccountAndNewAccount()
    {
        var picker = new ControlledTargetDirectoryPicker();
        using var fixture = Fixture.Create(accountCount: 2, targetDirectoryPicker: picker);
        fixture.ApplyReadyDraft();
        fixture.Wizard.DefaultTargetPath = "/默认旧目录";
        foreach (var account in fixture.Wizard.Accounts) account.IsSelected = true;
        fixture.Wizard.Accounts[0].DefaultTargetPath = "/账号一";
        fixture.Wizard.Accounts[1].DefaultTargetPath = "/移除账号保留";
        var remainingId = fixture.Wizard.Accounts[0].AccountId;
        var removed = fixture.Wizard.Accounts[1];
        var added = AccountOption("新增账号");

        var picking = fixture.ViewModel.PickTargetDirectoryAsync();
        await picker.Started.Task;
        fixture.Wizard.ReloadAccounts(
        [
            AccountOption("账号一") with { AccountId = remainingId },
            added,
        ]);
        var newAccount = fixture.Wizard.Accounts.Single(account => account.AccountId == added.AccountId);
        newAccount.DefaultTargetPath = "/新增账号保留";
        picker.Complete("/快照目录");
        await picking;

        Assert.Equal("/快照目录", fixture.Wizard.Accounts.Single(
            account => account.AccountId == remainingId).DefaultTargetPath);
        Assert.Equal("/移除账号保留", removed.DefaultTargetPath);
        Assert.Equal("/新增账号保留", newAccount.DefaultTargetPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelOrPickerFailurePreservesAllTargetPaths(bool fail)
    {
        var picker = new RecordingTargetDirectoryPicker(null)
        {
            Exception = fail ? new InvalidOperationException("adapter detail") : null,
        };
        using var fixture = Fixture.Create(accountCount: 2, targetDirectoryPicker: picker);
        fixture.ApplyReadyDraft();
        fixture.Wizard.DefaultTargetPath = "/默认旧目录";
        fixture.Wizard.Accounts[0].IsSelected = true;
        fixture.Wizard.Accounts[1].IsSelected = false;
        fixture.Wizard.Accounts[0].DefaultTargetPath = "/已选旧目录";
        fixture.Wizard.Accounts[1].DefaultTargetPath = "/未选旧目录";

        await fixture.ViewModel.PickTargetDirectoryAsync();

        Assert.Equal("/默认旧目录", fixture.Wizard.DefaultTargetPath);
        Assert.Equal("/已选旧目录", fixture.Wizard.Accounts[0].DefaultTargetPath);
        Assert.Equal("/未选旧目录", fixture.Wizard.Accounts[1].DefaultTargetPath);
        if (fail)
        {
            Assert.Equal("目录选择失败，请稍后重试。", fixture.ViewModel.StatusMessage);
            Assert.DoesNotContain("adapter", fixture.ViewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void LogAutoScrollSubscribesOnceAndScrollsLatestItemAsynchronouslyOnSta()
    {
        RunLogScrollOnSta((owner, list, source, pump) =>
        {
            var targets = new List<object>();
            list.AddHandler(FrameworkElement.RequestBringIntoViewEvent, new RequestBringIntoViewEventHandler((_, args) =>
            {
                if (args.TargetObject is not ListBoxItem item) return;
                Assert.True(list.Dispatcher.CheckAccess());
                targets.Add(item.Content);
                args.Handled = true;
            }), true);
            owner.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            owner.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Assert.Equal(1, source.SubscriberCount);
            source.Add("第一条日志");
            source.Add("最新日志");
            Assert.Empty(targets);
            list.UpdateLayout();
            pump();
            Assert.NotEmpty(targets);
            Assert.All(targets, item => Assert.Equal("最新日志", item));
            owner.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            Assert.Equal(0, source.SubscriberCount);
            targets.Clear();
            source.Add("卸载后的日志");
            pump();
            Assert.Empty(targets);
            owner.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            list.Dispatcher.InvokeShutdown();
            source.Add("调度器关闭后的日志");
            Assert.Empty(targets);
            owner.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            Assert.Equal(0, source.SubscriberCount);
        });
    }

    [Fact]
    public void LogAutoScrollIgnoresResetEmptyUnloadAndScrollErrorsOnSta()
    {
        RunLogScrollOnSta((owner, list, source, pump) =>
        {
            var calls = 0;
            list.AddHandler(FrameworkElement.RequestBringIntoViewEvent, new RequestBringIntoViewEventHandler((_, args) =>
            {
                if (args.TargetObject is not ListBoxItem) return;
                calls++;
                throw new InvalidOperationException("滚动失败");
            }), true);
            owner.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            source.Reset();
            pump();
            Assert.Equal(0, calls);
            source.Add("清空前日志");
            source.Clear();
            list.UpdateLayout();
            pump();
            Assert.Equal(0, calls);
            source.Add("卸载前日志");
            owner.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            owner.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            list.UpdateLayout();
            pump();
            Assert.Equal(0, calls);
            source.Add("滚动失败也不影响日志");
            list.UpdateLayout();
            pump();
            Assert.Equal(1, calls);
            Assert.Equal(2, source.Count);
            owner.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            Assert.Equal(0, source.SubscriberCount);
        });
    }

    [Fact]
    public void LogAutoScrollMovesToNewestEntryWhenLogViewBecomesVisible()
    {
        RunLogScrollOnSta((owner, list, source, pump) =>
        {
            var targets = new List<object>();
            list.AddHandler(FrameworkElement.RequestBringIntoViewEvent, new RequestBringIntoViewEventHandler((_, args) =>
            {
                if (args.TargetObject is not ListBoxItem item) return;
                targets.Add(item.Content);
                args.Handled = true;
            }), true);
            list.Visibility = Visibility.Collapsed;
            owner.Content = list;
            var window = new Window
            {
                Content = owner,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Width = 240,
                Height = 120
            };
            try
            {
                window.Show();
                pump();
                Assert.True(owner.IsLoaded);
                Assert.False(list.IsVisible);

                source.Add("隐藏期间第一条日志");
                source.Add("隐藏期间最新日志");
                pump();
                Assert.Empty(targets);

                list.Visibility = Visibility.Visible;
                pump();

                Assert.NotEmpty(targets);
                Assert.All(targets, item => Assert.Equal("隐藏期间最新日志", item));
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static void RunLogScrollOnSta(Action<UserControl, ListBox, ScrollLogSource, Action> test)
    {
        var behaviorType = typeof(TransferWorkspaceView).Assembly.GetType(
            "BaiduBatchTransfer.App.Features.Transfers.WorkspaceLogAutoScroll");
        Assert.NotNull(behaviorType);
        var code = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "BaiduBatchTransfer.App", "Features", "Transfers", "TransferWorkspaceView.xaml.cs"));
        Assert.Contains("new WorkspaceLogAutoScroll(this, TransferLogsList, viewModel.Logs)", code);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var owner = new UserControl();
                var source = new ScrollLogSource();
                var list = new ListBox { Width = 200, Height = 80, ItemsSource = source };
                list.Template = (ControlTemplate)XamlReader.Parse("""
                    <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="ListBox">
                        <ScrollViewer CanContentScroll="True"><ItemsPresenter /></ScrollViewer>
                    </ControlTemplate>
                    """);
                VirtualizingPanel.SetIsVirtualizing(list, false);
                list.Measure(new Size(200, 80));
                list.Arrange(new Rect(0, 0, 200, 80));
                list.UpdateLayout();
                _ = Activator.CreateInstance(behaviorType!, owner, list, source);
                // The ItemsSource observer is unrelated to the behavior's direct lifecycle subscription.
                source.IgnoreExistingSubscribers();
                test(owner, list, source, () =>
                {
                    list.Measure(new Size(200, 80));
                    list.Arrange(new Rect(0, 0, 200, 80));
                    list.UpdateLayout();
                    var frame = new DispatcherFrame();
                    Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                        new Action(() => frame.Continue = false));
                    Dispatcher.PushFrame(frame);
                });
            }
            catch (Exception exception) { failure = exception; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "日志滚动测试超时。");
        if (failure is not null) throw new TargetInvocationException(failure);
    }

    private sealed class ScrollLogSource : System.Collections.ObjectModel.Collection<string>, System.Collections.Specialized.INotifyCollectionChanged
    {
        private System.Collections.Specialized.NotifyCollectionChangedEventHandler? handlers;
        private int baseline;
        public int SubscriberCount => (handlers?.GetInvocationList().Length ?? 0) - baseline;
        public void IgnoreExistingSubscribers() => baseline = handlers?.GetInvocationList().Length ?? 0;
        public event System.Collections.Specialized.NotifyCollectionChangedEventHandler? CollectionChanged
        {
            add => handlers += value;
            remove => handlers -= value;
        }
        protected override void InsertItem(int index, string item)
        {
            base.InsertItem(index, item);
            handlers?.Invoke(this, new(System.Collections.Specialized.NotifyCollectionChangedAction.Add, item, index));
        }
        protected override void ClearItems() { base.ClearItems(); Reset(); }
        public void Reset() => handlers?.Invoke(this, new(System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingRowsSkipReadyPrecheckFailuresAtCreationAndAtTerminal(bool checkTerminal)
    {
        var history = new TerminalHistorySource(new(Guid.NewGuid(), "旧任务", "Active"));
        var delay = new TerminalDelay();
        using var fixture = Fixture.Create(historySource: history, refreshDelay: delay);
        fixture.ApplyReadyDraft();
        fixture.Wizard.QuotaBasis = QuotaBasis.TaskCount;
        fixture.Wizard.Accounts[0].TargetItemCount = 4;
        var failed = new ImportPreviewRow(Guid.NewGuid(), 1, "url", null, null, "就绪", "有效", "Ready", "Failed");
        var included = new ImportPreviewRow(Guid.NewGuid(), 2, "url", null, null, "就绪", null, "Ready");
        fixture.Import.Rows.Add(failed);
        fixture.Import.Rows.Add(included);

        await fixture.ViewModel.StartAsync();
        var batch = Assert.IsType<Guid>(fixture.ViewModel.CreatedBatchJobId);
        Assert.Equal("已进入任务", included.RuntimeStatusText);
        if (checkTerminal)
        {
            await fixture.RunPanel.Results.InitializeAsync(batch);
            var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.RunPanel.BatchTerminal += (_, _) => terminal.TrySetResult();
            history.Current = new(batch, "新任务", "Completed");
            delay.Release();
            await terminal.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal("任务已结束，请查看结果", included.RuntimeStatusText);
        }
        Assert.Equal("就绪", failed.DisplayDispositionText);
        Assert.Null(failed.RuntimeStatusText);
    }

    [Fact]
    public async Task PendingRowsStartOnlyForIncludedRawReadyRowsAndExplicitlySelectResults()
    {
        using var fixture = Fixture.Valid();
        var included = new ImportPreviewRow(Guid.NewGuid(), 1, "url", null, null, "任意文字", null, "Ready");
        var excluded = new ImportPreviewRow(Guid.NewGuid(), 2, "url", null, null, "就绪", null, "Excluded");
        fixture.Import.Rows.Add(included);
        fixture.Import.Rows.Add(excluded);
        await fixture.ViewModel.StartAsync();
        Assert.NotNull(fixture.ViewModel.CreatedBatchJobId);
        Assert.Equal("已进入任务", included.DisplayDispositionText);
        Assert.Null(excluded.RuntimeStatusText);
        Assert.Equal(1, fixture.ViewModel.SelectedDataTabIndex);
        var later = new ImportPreviewRow(Guid.NewGuid(), 3, "url", null, null, "就绪", null, "Ready");
        fixture.Import.Rows.Add(later);
        Assert.Null(later.RuntimeStatusText);
    }

    [Theory]
    [InlineData(TransferAccountMode.MultiAccountReplication, "处理中")]
    [InlineData(TransferAccountMode.MultiAccountDistribution, "已完成")]
    public async Task PendingRowsUseCreationAccountCountAndRefreshSnapshots(TransferAccountMode mode, string afterOne)
    {
        var workflow = new EmptyResultsWorkflow();
        using var fixture = Fixture.Create(accountCount: 2, resultsWorkflow: workflow);
        fixture.ApplyReadyDraft();
        await fixture.ViewModel.SelectAccountModeAsync(mode);
        foreach (var account in fixture.Wizard.Accounts)
        {
            account.IsSelected = true;
            account.TargetItemCount = 2;
        }
        fixture.Wizard.QuotaBasis = QuotaBasis.TaskCount;
        var row = new ImportPreviewRow(Guid.NewGuid(), 1, "url", null, null, "就绪", null, "Ready");
        fixture.Import.Rows.Add(row);
        await fixture.ViewModel.StartAsync();
        var batch = Assert.IsType<Guid>(fixture.ViewModel.CreatedBatchJobId);
        fixture.Wizard.Accounts[1].IsSelected = false;
        var first = PendingResult(row.RowId, "Succeeded", "Succeeded");
        var second = PendingResult(row.RowId, "Succeeded", "ReusedExisting");
        workflow.PageItems = [first];
        await fixture.RunPanel.Results.InitializeAsync(batch);
        Assert.Equal(afterOne, row.RuntimeStatusText);
        workflow.PageItems = mode == TransferAccountMode.MultiAccountReplication ? [first, second] : [first];
        await fixture.RunPanel.Results.RefreshAsync();
        Assert.Equal("已完成", row.RuntimeStatusText);
        workflow.PageItems = [first with { ShareStatus = "ResultPending" }];
        await fixture.RunPanel.Results.RefreshAsync();
        Assert.Equal("有问题，请查看结果", row.RuntimeStatusText);
        await fixture.RunPanel.Results.InitializeAsync(Guid.NewGuid());
        fixture.RunPanel.Results.Items.Add(second);
        Assert.Equal("有问题，请查看结果", row.RuntimeStatusText);
        fixture.ViewModel.Close();
        fixture.RunPanel.Results.Items.Clear();
        Assert.Equal("有问题，请查看结果", row.RuntimeStatusText);
    }

    [Fact]
    public async Task PendingRowsWithoutExactDetailsShowTerminalFallbackAfterBatchEnds()
    {
        var history = new TerminalHistorySource(new(Guid.NewGuid(), "旧任务", "Active"));
        var delay = new TerminalDelay();
        using var fixture = Fixture.Create(historySource: history, refreshDelay: delay);
        fixture.ApplyReadyDraft();
        fixture.Wizard.QuotaBasis = QuotaBasis.TaskCount;
        fixture.Wizard.Accounts[0].TargetItemCount = 4;
        var row = new ImportPreviewRow(Guid.NewGuid(), 1, "url", null, null, "就绪", null, "Ready");
        fixture.Import.Rows.Add(row);
        await fixture.ViewModel.StartAsync();
        var batch = Assert.IsType<Guid>(fixture.ViewModel.CreatedBatchJobId);
        await fixture.RunPanel.Results.InitializeAsync(batch);
        var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.RunPanel.BatchTerminal += (_, _) => terminal.TrySetResult();
        history.Current = new(batch, "新任务", "Completed");
        delay.Release();
        await terminal.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("任务已结束，请查看结果", row.RuntimeStatusText);
    }

    [Fact]
    public void PendingRowsGridBindsRuntimeDisplayStatus()
    {
        var view = XDocument.Load(System.IO.Path.Combine(FindRepositoryRoot(), "src", "BaiduBatchTransfer.App",
            "Features", "Transfers", "TransferWorkspaceView.xaml"));
        var column = Assert.Single(view.Descendants(), element => (string?)element.Attribute("Header") == "处理状态");
        Assert.Equal("{Binding DisplayDispositionText}", (string?)column.Attribute("Binding"));
    }

    [Theory]
    [InlineData("", null, 1, false, "已进入任务")]
    [InlineData("Waiting", null, 1, false, "处理中")]
    [InlineData("Waiting", null, 1, true, "任务已结束，请查看结果")]
    [InlineData("Preparing", null, 1, false, "处理中")]
    [InlineData("Preparing", null, 1, true, "任务已结束，请查看结果")]
    [InlineData("Succeeded", "WaitingShare", 1, false, "处理中")]
    [InlineData("Succeeded", "WaitingShare", 1, true, "任务已结束，请查看结果")]
    [InlineData("Succeeded", "Succeeded", 1, false, "已完成")]
    [InlineData("Succeeded", "ReusedExisting", 1, true, "已完成")]
    [InlineData("Succeeded", "Succeeded", 2, false, "处理中")]
    [InlineData("Succeeded", "Succeeded", 2, true, "任务已结束，请查看结果")]
    [InlineData("Succeeded", null, 1, true, "任务已结束，请查看结果")]
    [InlineData("", null, 1, true, "任务已结束，请查看结果")]
    [InlineData("Failed", null, 2, false, "有问题，请查看结果")]
    [InlineData("Failed", null, 1, true, "有问题，请查看结果")]
    [InlineData("Cancelled", null, 2, true, "有问题，请查看结果")]
    [InlineData("Paused", null, 2, false, "有问题，请查看结果")]
    [InlineData("Paused", null, 1, true, "有问题，请查看结果")]
    [InlineData("ResultPending", null, 2, false, "有问题，请查看结果")]
    [InlineData("ResultPending", null, 1, true, "有问题，请查看结果")]
    [InlineData("Succeeded", "Failed", 2, false, "有问题，请查看结果")]
    [InlineData("Succeeded", "Failed", 1, true, "有问题，请查看结果")]
    [InlineData("Succeeded", "Cancelled", 2, false, "有问题，请查看结果")]
    [InlineData("Succeeded", "Cancelled", 1, true, "有问题，请查看结果")]
    [InlineData("Succeeded", "Paused", 2, false, "有问题，请查看结果")]
    [InlineData("Succeeded", "Paused", 1, true, "有问题，请查看结果")]
    [InlineData("Succeeded", "ResultPending", 2, false, "有问题，请查看结果")]
    [InlineData("Succeeded", "ResultPending", 1, true, "有问题，请查看结果")]
    [InlineData("Unknown", null, 1, false, "有问题，请查看结果")]
    public void PendingRowRuntimeAggregatesRawStates(string transfer, string? share, int expected, bool terminal, string text)
    {
        var rowId = Guid.NewGuid();
        JobResultRow[] results = transfer == "" ? [] : [PendingResult(rowId, transfer, share)];
        Assert.Equal(text, AggregatePending(rowId, results, expected, terminal));
    }

    [Fact]
    public void PendingRowRuntimeRequiresEveryAccountAndEveryCandidateWithoutCountingDuplicateOperations()
    {
        var rowId = Guid.NewGuid();
        var first = PendingResult(rowId, "Succeeded", "Succeeded");
        var second = PendingResult(rowId, "Succeeded", "ReusedExisting");
        Assert.Equal("已完成", AggregatePending(rowId, [first, second], 2, false));
        Assert.Equal("处理中", AggregatePending(rowId, [first, first], 2, false));
        Assert.Equal("任务已结束，请查看结果", AggregatePending(rowId, [first, second with { CandidateSequence = 2 }], 2, true));
        Assert.Equal("有问题，请查看结果", AggregatePending(rowId, [first, second with { ShareStatus = "Paused" }], 2, false));
        Assert.Equal("有问题，请查看结果", AggregatePending(rowId, [first with { TransferStatus = "Waiting" }, second with { ShareStatus = "Failed" }], 2, false));
        Assert.Equal("已完成", AggregatePending(rowId, [first with { ShareEnabled = false, ShareStatus = null }], 1, true));
        Assert.Equal("有问题，请查看结果", AggregatePending(rowId, [first with { CanReconcileTransfer = true }], 1, false));
        Assert.Equal("已进入任务", AggregatePending(Guid.NewGuid(), [first, second], 2, false));
    }

    [Fact]
    public void PendingRowRuntimeTextNotifiesDisplayBindingAndPreservesOriginalDisposition()
    {
        var row = new ImportPreviewRow(Guid.NewGuid(), 1, "url", null, null, "就绪", null, "Ready");
        var observable = Assert.IsAssignableFrom<System.ComponentModel.INotifyPropertyChanged>(row);
        var notifications = new List<string?>();
        observable.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        row.RuntimeStatusText = "已进入任务";
        Assert.Equal("已进入任务", row.DisplayDispositionText);
        Assert.Equal(new[] { "RuntimeStatusText", "DisplayDispositionText" }, notifications);
        Assert.Equal("就绪", row.DispositionText);
        Assert.Equal("Ready", row.Disposition);
        row.RuntimeStatusText = null;
        Assert.Equal("就绪", row.DisplayDispositionText);
    }

    private static JobResultRow PendingResult(Guid rowId, string transfer, string? share) =>
        new(Guid.NewGuid(), null, 1, "相同显示名", "标题", "任意显示文案", "任意分享文案", null, null,
            DateTimeOffset.UtcNow, null, null, false, false, false,
            TransferStatus: transfer, StagedImportRowId: rowId, ShareStatus: share);

    private static string AggregatePending(Guid rowId, IReadOnlyList<JobResultRow> results, int expected, bool terminal)
        => PendingRowRuntimeStatus.Evaluate(rowId, results, expected, terminal);

    [Fact]
    public async Task PendingRowUiProjectionPreservesRawIdentityStatusAndDisposition()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pending-projection-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(root);
        try
        {
            var factory = new SqliteConnectionFactory(System.IO.Path.Combine(root, "test.db"));
            await new DatabaseMigrator(factory, new DatabaseBackupService(factory, System.IO.Path.Combine(root, "backups")), "test").MigrateAsync();
            var units = new SqliteUnitOfWorkFactory(factory);
            var now = DateTimeOffset.UtcNow;
            var batch = Guid.NewGuid();
            var account = AccountRecord.Create(Guid.NewGuid(), "pending", "账号");
            var session = ImportSessionRecord.Create(Guid.NewGuid(), "Text", "pending");
            var row = StagedImportRowRecord.Create(Guid.NewGuid(), session.Id, 1, "url", "Ready")
                with { PrecheckStatus = "Failed" };
            var resource = new ResourceItemRecord(Guid.NewGuid(), batch, row.Id, 1, "url", "share", null, null,
                null, null, null, "None", "Valid", null, "Expanded", null, null, now, now);
            var transfer = new TransferOperationRecord(Guid.NewGuid(), resource.Id, batch, 1, 1, "/", "/", "",
                account.Id, "Rule", "Succeeded", "NewTransfer", 1, "pending", null, null, null, "NotPrepared",
                "Unoccupied", null, null, null, null, null, null, now, now);
            var fingerprint = ShareSettingsFingerprint.Compute(true, true, ShareExtractionCodeBehavior.Random, null);
            var shareId = Guid.NewGuid();
            await using (var unit = await units.BeginAsync())
            {
                await unit.Accounts.UpsertAsync(account);
                await unit.Imports.AddSessionAsync(session);
                await unit.Imports.AddRowsAsync([row]);
                await unit.Jobs.AddAsync(new(batch, "pending", "Text", "Distribution", "TaskCount",
                    "ProportionalReduction", 1, 1, "/", "None", "Skip", "Immediate", "Active", now, null, null, null, 1));
                await unit.Jobs.AddResourcesAsync([resource]);
                await unit.Jobs.AddOperationsAsync([transfer]);
                await unit.Jobs.AddAllocationsAsync([new(Guid.NewGuid(), batch, account.Id, 1, "/", "/",
                    1, 0, 0, 1, 1, 1, 0, 1, 0, 0, 0, 0, 0, 1)]);
                await unit.Jobs.SetShareSettingsAsync(new(batch, true, true, "Random", null, "Ask", now, fingerprint));
                await unit.Jobs.AddOutputAccountsAsync([new(batch, account.Id, 1, "账号", 1, 1, true)]);
                await unit.Jobs.AddShareOperationsAsync([new(shareId, transfer.Id, resource.Id, batch, account.Id, 1,
                    "WaitingShare", new RemoteItemIdentitySet([new("remote", "remote")]).Fingerprint,
                    fingerprint, 1, null, null, "Unknown", 0, null, null, null, null, null, now, now)],
                    [new(shareId, 0, "remote", "remote")]);
                await unit.CommitAsync();
            }
            var workflow = new LocalJobResultsWorkflow(new JobPageQuery(units), new BatchStatisticsQuery(units),
                new ReplacementWorkbookExportService(new SqliteBatchResultProjectionRepository(factory), new ReplacementWorkbookFileNameBuilder()),
                new OpenXmlReplacementWorkbookFilePublisher(new OpenXmlReplacementResultWriter()),
                DispatchProxy.Create<IJobResultActions, RejectUnexpectedCall>());
            var result = Assert.Single((await workflow.LoadPageAsync(batch, null, 10)).Items);
            var preview = Assert.Single((await new ImportPreviewPageSource(new ImportPageQuery(units))
                .LoadAsync(new(session.Id, 10), default)).Items);
            Assert.Equal(row.Id, result.StagedImportRowId);
            Assert.Equal("WaitingShare", result.ShareStatus);
            Assert.True(result.ShareEnabled);
            Assert.Equal("Ready", preview.Disposition);
            Assert.Equal("Failed", preview.PrecheckStatus);
            Assert.Equal("就绪", preview.DispositionText);
        }
        finally { System.IO.Directory.Delete(root, true); }
    }

    public class RejectUnexpectedCall : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(false, false, 2, WorkspaceLogLevel.Error)]
    [InlineData(true, false, 2, WorkspaceLogLevel.Warning)]
    [InlineData(false, true, 2, WorkspaceLogLevel.Error)]
    [InlineData(false, false, 0, WorkspaceLogLevel.Success)]
    public async Task TerminalBatchLogsBeforeOneChineseDialogEvenWhenStatisticsOrDialogFail(
        bool failStatistics, bool failDialog, long problemCount, WorkspaceLogLevel expectedLevel)
    {
        var batch = Guid.NewGuid();
        var history = new TerminalHistorySource(new(batch, "批次", "Active"));
        var delay = new TerminalDelay();
        var workflow = new EmptyResultsWorkflow { TerminalProblemCount = problemCount };
        using var fixture = Fixture.Create(historySource: history, refreshDelay: delay, resultsWorkflow: workflow);
        var vm = fixture.ViewModel;
        var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.RunPanel.BatchTerminal += (_, _) => terminal.TrySetResult();
        fixture.Dialog.FailMessage = failDialog;
        fixture.Dialog.BeforeMessage = message => Assert.Contains(vm.Logs, entry => entry.Message == message);
        await fixture.RunPanel.InitializeAsync();
        workflow.FailSummary = failStatistics;
        history.Current = history.Current with { BatchStatus = "Completed" };
        delay.Release();
        await terminal.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var dialog = Assert.Single(fixture.Dialog.Messages);
        Assert.Equal("转存任务已完成", dialog.Title);
        Assert.Contains("转存任务已完成", dialog.Message);
        if (failStatistics) Assert.Contains("统计暂未读取", dialog.Message);
        else
        {
            Assert.Contains("完成 3", dialog.Message);
            Assert.Contains($"失败 {problemCount}", dialog.Message);
        }
        Assert.Equal("Completed", fixture.RunPanel.SelectedBatch?.BatchStatus);
        var log = Assert.Single(vm.Logs, entry => entry.Message == dialog.Message);
        Assert.Equal(expectedLevel, log.Level);
        workflow.FailSummary = false;
        await fixture.RunPanel.Results.RefreshAsync();
        await fixture.RunPanel.InitializeAsync();
        Assert.Single(fixture.Dialog.Messages);
    }

    [Fact]
    public async Task ClosedWorkspaceDoesNotShowTerminalDialog()
    {
        var history = new TerminalHistorySource(new(Guid.NewGuid(), "批次", "Active"));
        var delay = new TerminalDelay();
        using var fixture = Fixture.Create(historySource: history, refreshDelay: delay);
        await fixture.RunPanel.InitializeAsync();
        fixture.ViewModel.Close();
        history.Current = history.Current with { BatchStatus = "Completed" };
        delay.Release();
        await fixture.RunPanel.InitializeAsync();
        Assert.Empty(fixture.Dialog.Messages);
    }

    private sealed class TerminalHistorySource(WorkspaceBatchItem current) : IWorkspaceBatchHistorySource
    {
        public WorkspaceBatchItem Current { get; set; } = current;
        public Task<IReadOnlyList<WorkspaceBatchItem>> LoadRecentAsync(int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WorkspaceBatchItem>>([Current]);
    }

    private sealed class TerminalDelay : IAsyncDelay
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => release.Task.WaitAsync(cancellationToken);
        public void Release() => release.TrySetResult();
    }

    [Fact]
    public async Task StatusChangesProduceSafeLogsAndConsecutiveDuplicatesCoalesce()
    {
        using var fixture = Fixture.Valid();
        var initialCount = fixture.ViewModel.Logs.Count;

        await fixture.ViewModel.SelectAccountModeAsync(TransferAccountMode.SingleAccount);
        await fixture.ViewModel.SelectAccountModeAsync(TransferAccountMode.SingleAccount);

        Assert.Equal(initialCount + 1, fixture.ViewModel.Logs.Count);
        Assert.Equal("已选择单账号转存。", fixture.ViewModel.Logs[^1].Message);
        Assert.Equal(WorkspaceLogLevel.Success, fixture.ViewModel.Logs[^1].Level);
    }

    [Fact]
    public async Task UserFacingStatusMessageNotifiesWheneverInternalStatusChanges()
    {
        using var fixture = Fixture.Valid();
        var changedProperties = new List<string?>();
        fixture.ViewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        await fixture.ViewModel.SelectAccountModeAsync(TransferAccountMode.SingleAccount);

        Assert.Contains(nameof(TransferWorkspaceViewModel.UserFacingStatusMessage), changedProperties);
        Assert.Equal("已选择单账号转存。", fixture.ViewModel.UserFacingStatusMessage);
    }

    [Fact]
    public async Task DisposePreventsLateStatusFromAppendingToLogs()
    {
        var precheck = new BlockingPrecheckWorkflow();
        using var fixture = Fixture.Valid(precheckWorkflow: precheck);
        var start = fixture.ViewModel.StartAsync();
        await precheck.Started.Task;
        fixture.ViewModel.Dispose();
        var countAtDispose = fixture.ViewModel.Logs.Count;

        precheck.Release();
        await start;

        Assert.Equal(countAtDispose, fixture.ViewModel.Logs.Count);
    }

    [Fact]
    public async Task ResultStatusChangesAreLoggedAndUnsubscribedAtDispose()
    {
        using var fixture = Fixture.Create();
        var results = fixture.RunPanel.Results;

        await results.InitializeAsync(Guid.NewGuid());

        Assert.Contains(fixture.ViewModel.Logs,
            entry => entry.Message == "正在读取任务结果……");
        Assert.Equal("已读取 0 / 0 条操作结果。", fixture.ViewModel.Logs[^1].Message);

        fixture.ViewModel.Dispose();
        var countAtDispose = fixture.ViewModel.Logs.Count;
        await results.InitializeAsync(Guid.NewGuid());

        Assert.Equal(countAtDispose, fixture.ViewModel.Logs.Count);
    }

    [Fact]
    public async Task LoadingABatchAutomaticallySelectsResultsAndNewDraftReturnsToPendingLinks()
    {
        using var fixture = Fixture.Create();

        await fixture.RunPanel.ActivateBatchAsync(Guid.NewGuid());

        Assert.Equal(1, fixture.ViewModel.SelectedDataTabIndex);

        fixture.ApplyReadyDraft();

        Assert.Equal(0, fixture.ViewModel.SelectedDataTabIndex);
    }

    [Fact]
    public void SelectedDataTabIndexAcceptsThreeWorkspaceViewsAndRejectsOutOfRangeValues()
    {
        using var fixture = Fixture.Create();

        fixture.ViewModel.SelectedDataTabIndex = 2;
        Assert.Equal(2, fixture.ViewModel.SelectedDataTabIndex);

        fixture.ViewModel.SelectedDataTabIndex = 3;
        Assert.Equal(2, fixture.ViewModel.SelectedDataTabIndex);

        fixture.ViewModel.SelectedDataTabIndex = -1;
        Assert.Equal(2, fixture.ViewModel.SelectedDataTabIndex);
    }

    [Fact]
    public void AcceptanceOnlyForcesExactAutomaticShareConfiguration()
    {
        using var fixture = Fixture.Valid(
            ready: 1,
            remoteWriteMode: RemoteWriteExecutionMode.AcceptanceOnly,
            managedAccount: true);

        fixture.ViewModel.AutoShare = false;
        fixture.ViewModel.ShareResultDirectory = "C:\\results";

        Assert.True(fixture.ViewModel.AutoShare);
        Assert.True(fixture.Wizard.AcceptanceOnly);
        Assert.Equal("C:\\results", fixture.Wizard.AcceptanceResultDirectory);
        Assert.True(fixture.ViewModel.CanStart);
    }

    [Fact]
    public void AcceptanceOnlyTakesPrecedenceOverSimplifiedDirectConfiguration()
    {
        using var fixture = Fixture.Valid(
            ready: 1,
            options: new TransferWorkspaceOptions(
                DefaultTargetDirectory: "/",
                DefaultResultDirectory: "C:\\results",
                SimplifiedDirectMode: true,
                DefaultMaxConcurrency: 2),
            remoteWriteMode: RemoteWriteExecutionMode.AcceptanceOnly,
            managedAccount: true);

        Assert.True(fixture.Wizard.AcceptanceOnly);
        Assert.False(fixture.Wizard.DirectBackground);
        Assert.False(fixture.ViewModel.IsSimplifiedDirectMode);
        Assert.True(fixture.ViewModel.CanStart);
    }

    [Fact]
    public void SimplifiedDirectWorkspaceFixesSingleAccountRenameAndAutomaticShareDefaults()
    {
        using var fixture = Fixture.Create(
            ready: 4,
            options: new TransferWorkspaceOptions(
                DefaultTargetDirectory: "/",
                DefaultResultDirectory: "C:\\results",
                SimplifiedDirectMode: true,
                DefaultMaxConcurrency: 2));

        fixture.ApplyReadyDraft();
        fixture.ViewModel.AutoShare = false;

        Assert.True(fixture.ViewModel.IsSimplifiedDirectMode);
        Assert.True(fixture.ViewModel.AutoShare);
        Assert.Equal(TransferAccountMode.SingleAccount, fixture.ViewModel.AccountMode);
        Assert.True(fixture.Wizard.DirectBackground);
        Assert.Equal(2, fixture.Wizard.MaxConcurrency);
        Assert.Equal(DuplicatePolicy.Rename, fixture.Wizard.DuplicatePolicy);
        Assert.Equal("Regenerate", fixture.Wizard.ExistingSharePolicy);
        Assert.Equal(ShareExtractionCodeBehavior.Random, fixture.Wizard.ExtractionCodeBehavior);
        Assert.Equal("C:\\results", fixture.Wizard.ResultDirectory);
        Assert.True(fixture.Wizard.Accounts[0].ShareEnabled);
    }

    [Fact]
    public void SimplifiedDirectWorkspaceUsesTheLatestSelectedAccountAndCanStart()
    {
        using var fixture = Fixture.Create(
            accountCount: 2,
            ready: 25,
            options: new TransferWorkspaceOptions(
                DefaultTargetDirectory: "/",
                DefaultResultDirectory: "C:\\results",
                SimplifiedDirectMode: true,
                DefaultMaxConcurrency: 2));
        fixture.ApplyReadyDraft();

        fixture.Wizard.Accounts[0].IsSelected = true;

        Assert.Equal(TransferAccountMode.SingleAccount, fixture.ViewModel.AccountMode);
        Assert.Equal(TransferMode.Distribution, fixture.Wizard.TransferMode);
        Assert.Equal(25, fixture.Wizard.Accounts[0].TargetItemCount);
        Assert.True(fixture.ViewModel.CanStart);

        fixture.Wizard.Accounts[1].IsSelected = true;

        Assert.False(fixture.Wizard.Accounts[0].IsSelected);
        Assert.Equal(0, fixture.Wizard.Accounts[0].TargetItemCount);
        Assert.True(fixture.Wizard.Accounts[1].IsSelected);
        Assert.Equal(25, fixture.Wizard.Accounts[1].TargetItemCount);
        Assert.True(fixture.ViewModel.CanStart);
    }

    [Fact]
    public async Task SimplifiedDirectWorkspaceExposesSelectedAccountModeState()
    {
        using var fixture = Fixture.Create(
            accountCount: 2,
            options: new TransferWorkspaceOptions(
                DefaultTargetDirectory: "/",
                DefaultResultDirectory: "C:\\results",
                SimplifiedDirectMode: true,
                DefaultMaxConcurrency: 2));
        fixture.ApplyReadyDraft();

        await fixture.ViewModel.SelectAccountModeAsync(TransferAccountMode.MultiAccountReplication);

        Assert.False(fixture.ViewModel.IsSingleAccountMode);
        Assert.True(fixture.ViewModel.IsReplicationMode);
        Assert.False(fixture.ViewModel.IsDistributionMode);

        await fixture.ViewModel.SelectAccountModeAsync(TransferAccountMode.MultiAccountDistribution);

        Assert.False(fixture.ViewModel.IsSingleAccountMode);
        Assert.False(fixture.ViewModel.IsReplicationMode);
        Assert.True(fixture.ViewModel.IsDistributionMode);
    }

    [Fact]
    public async Task SimplifiedReplicationRetainsEverySelectedAccountAndEnablesSharing()
    {
        using var fixture = Fixture.Create(
            accountCount: 3,
            ready: 5,
            options: new TransferWorkspaceOptions(
                DefaultTargetDirectory: "/",
                DefaultResultDirectory: "C:\\results",
                SimplifiedDirectMode: true,
                DefaultMaxConcurrency: 2));
        fixture.ApplyReadyDraft();
        await fixture.ViewModel.SelectAccountModeAsync(TransferAccountMode.MultiAccountReplication);

        fixture.Wizard.Accounts[0].IsSelected = true;
        fixture.Wizard.Accounts[2].IsSelected = true;

        Assert.True(fixture.Wizard.Accounts[0].IsSelected);
        Assert.False(fixture.Wizard.Accounts[1].IsSelected);
        Assert.True(fixture.Wizard.Accounts[2].IsSelected);
        Assert.True(fixture.Wizard.Accounts[0].ShareEnabled);
        Assert.False(fixture.Wizard.Accounts[1].ShareEnabled);
        Assert.True(fixture.Wizard.Accounts[2].ShareEnabled);
        Assert.Equal(TransferMode.Replication, fixture.Wizard.TransferMode);
        Assert.Equal(10, fixture.Wizard.OperationCount);
        Assert.True(fixture.ViewModel.CanStart);
    }

    [Fact]
    public async Task SimplifiedDistributionAllocatesEvenlyAndRecalculatesForSelectionAndReadyCount()
    {
        using var fixture = Fixture.Create(
            accountCount: 3,
            ready: 8,
            options: new TransferWorkspaceOptions(
                DefaultTargetDirectory: "/",
                DefaultResultDirectory: "C:\\results",
                SimplifiedDirectMode: true,
                DefaultMaxConcurrency: 2));
        fixture.ApplyReadyDraft();
        await fixture.ViewModel.SelectAccountModeAsync(TransferAccountMode.MultiAccountDistribution);
        foreach (var account in fixture.Wizard.Accounts) account.IsSelected = true;

        Assert.Equal([3, 3, 2], fixture.Wizard.Accounts.Select(account => account.TargetItemCount));
        Assert.All(fixture.Wizard.Accounts, account => Assert.True(account.ShareEnabled));
        Assert.True(fixture.ViewModel.CanStart);

        fixture.Wizard.Accounts[1].IsSelected = false;

        Assert.Equal([4, 0, 4], fixture.Wizard.Accounts.Select(account => account.TargetItemCount));

        fixture.Wizard.RefreshImportSnapshot(5, 0, 0, false, 0);

        Assert.Equal([3, 0, 2], fixture.Wizard.Accounts.Select(account => account.TargetItemCount));
        Assert.True(fixture.ViewModel.CanStart);
    }

    [Fact]
    public async Task SwitchingFromMultiAccountToSingleKeepsMostRecentlySelectedAccount()
    {
        using var fixture = Fixture.Create(
            accountCount: 3,
            ready: 5,
            options: new TransferWorkspaceOptions(
                DefaultTargetDirectory: "/",
                DefaultResultDirectory: "C:\\results",
                SimplifiedDirectMode: true,
                DefaultMaxConcurrency: 2));
        fixture.ApplyReadyDraft();
        await fixture.ViewModel.SelectAccountModeAsync(TransferAccountMode.MultiAccountDistribution);
        fixture.Wizard.Accounts[0].IsSelected = true;
        fixture.Wizard.Accounts[1].IsSelected = true;
        fixture.Wizard.Accounts[2].IsSelected = true;

        await fixture.ViewModel.SelectAccountModeAsync(TransferAccountMode.SingleAccount);

        Assert.False(fixture.Wizard.Accounts[0].IsSelected);
        Assert.False(fixture.Wizard.Accounts[1].IsSelected);
        Assert.True(fixture.Wizard.Accounts[2].IsSelected);
        Assert.Equal([0, 0, 5], fixture.Wizard.Accounts.Select(account => account.TargetItemCount));
        Assert.False(fixture.Wizard.Accounts[0].ShareEnabled);
        Assert.False(fixture.Wizard.Accounts[1].ShareEnabled);
        Assert.True(fixture.Wizard.Accounts[2].ShareEnabled);
        Assert.True(fixture.ViewModel.IsSingleAccountMode);
        Assert.True(fixture.ViewModel.CanStart);
    }

    [Fact]
    public async Task LatestAccountModeWaitsForCancellationIgnoringEarlierWorkflowAndRunsLast()
    {
        using var fixture = Fixture.Create(
            accountCount: 2,
            options: new TransferWorkspaceOptions(
                DefaultTargetDirectory: "/",
                DefaultResultDirectory: "C:\\results",
                SimplifiedDirectMode: true,
                DefaultMaxConcurrency: 2));
        fixture.ApplyReadyDraft();
        var firstGate = fixture.DraftWorkflow.QueueModeSwitch(
            TransferMode.Replication,
            ignoreCancellation: true);
        var secondGate = fixture.DraftWorkflow.QueueModeSwitch(TransferMode.Distribution);

        var first = fixture.ViewModel.SelectAccountModeAsync(TransferAccountMode.MultiAccountReplication);
        await firstGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var second = fixture.ViewModel.SelectAccountModeAsync(TransferAccountMode.MultiAccountDistribution);
        var secondEnteredBeforeFirstCompleted = await Task.WhenAny(
            secondGate.Entered.Task,
            Task.Delay(TimeSpan.FromMilliseconds(100))) == secondGate.Entered.Task;
        firstGate.Release();
        await first;
        await secondGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        secondGate.Release();
        await second;

        Assert.False(secondEnteredBeforeFirstCompleted);
        Assert.Equal(TransferAccountMode.MultiAccountDistribution, fixture.ViewModel.AccountMode);
        Assert.Equal(TransferMode.Distribution, fixture.Wizard.TransferMode);
        Assert.False(fixture.ViewModel.IsReplicationMode);
        Assert.True(fixture.ViewModel.IsDistributionMode);
    }

    [Fact]
    public async Task SameWizardModeTransitionUpdatesAccountModeWithoutCallingDraftWorkflow()
    {
        using var fixture = Fixture.Create(
            accountCount: 2,
            options: new TransferWorkspaceOptions(
                DefaultTargetDirectory: "/",
                DefaultResultDirectory: "C:\\results",
                SimplifiedDirectMode: true,
                DefaultMaxConcurrency: 2));
        fixture.ApplyReadyDraft();
        fixture.Wizard.Accounts[0].IsSelected = true;
        Assert.Equal(TransferAccountMode.SingleAccount, fixture.ViewModel.AccountMode);
        Assert.Equal(TransferMode.Distribution, fixture.Wizard.TransferMode);
        fixture.DraftWorkflow.ModeSwitchException = new InvalidOperationException("must not be called");

        await fixture.ViewModel.SelectAccountModeAsync(TransferAccountMode.MultiAccountDistribution);

        Assert.Equal(0, fixture.DraftWorkflow.ModeSwitchCallCount);
        Assert.Equal(TransferAccountMode.MultiAccountDistribution, fixture.ViewModel.AccountMode);
        Assert.True(fixture.ViewModel.IsDistributionMode);
    }

    [Fact]
    public async Task FailedAccountModeChangeRenotifiesThePreviousVisualState()
    {
        using var fixture = Fixture.Create(
            options: new TransferWorkspaceOptions(
                DefaultTargetDirectory: "/",
                DefaultResultDirectory: "C:\\results",
                SimplifiedDirectMode: true,
                DefaultMaxConcurrency: 2));
        fixture.ApplyReadyDraft();
        fixture.DraftWorkflow.ModeSwitchException = new InvalidOperationException("mode switch failed");
        var changes = new List<string?>();
        fixture.ViewModel.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

        await fixture.ViewModel.SelectAccountModeAsync(TransferAccountMode.MultiAccountReplication);

        Assert.Equal(TransferAccountMode.SingleAccount, fixture.ViewModel.AccountMode);
        Assert.True(fixture.ViewModel.IsSingleAccountMode);
        Assert.False(fixture.ViewModel.IsReplicationMode);
        Assert.Contains(nameof(TransferWorkspaceViewModel.AccountModeText), changes);
        Assert.Contains(nameof(TransferWorkspaceViewModel.IsSingleAccountMode), changes);
        Assert.Contains(nameof(TransferWorkspaceViewModel.IsReplicationMode), changes);
        Assert.Contains(nameof(TransferWorkspaceViewModel.IsDistributionMode), changes);
    }

    [Fact]
    public async Task DistributionCanStartWhenASelectedAccountReceivesZeroWork()
    {
        using var fixture = Fixture.Create(
            accountCount: 3,
            ready: 2,
            options: new TransferWorkspaceOptions(
                DefaultTargetDirectory: "/",
                DefaultResultDirectory: "C:\\results",
                SimplifiedDirectMode: true,
                DefaultMaxConcurrency: 2));
        fixture.ApplyReadyDraft();
        await fixture.ViewModel.SelectAccountModeAsync(TransferAccountMode.MultiAccountDistribution);
        foreach (var account in fixture.Wizard.Accounts) account.IsSelected = true;

        Assert.Equal([1, 1, 0], fixture.Wizard.Accounts.Select(account => account.TargetItemCount));
        Assert.True(fixture.ViewModel.CanStart);
        Assert.Equal(string.Empty, fixture.ViewModel.ConfigurationMessage);
    }

    [Fact]
    public void AcceptanceOnlyReadyDraftDefaultsToOneTaskQuota()
    {
        using var fixture = Fixture.Create(
            ready: 1,
            options: new TransferWorkspaceOptions(DefaultResultDirectory: "C:\\results"),
            remoteWriteMode: RemoteWriteExecutionMode.AcceptanceOnly,
            managedAccount: true);

        fixture.ApplyReadyDraft();

        Assert.Equal(QuotaBasis.TaskCount, fixture.Wizard.QuotaBasis);
        Assert.Equal(1, fixture.Wizard.Accounts[0].TargetItemCount);
        Assert.True(fixture.Wizard.Accounts[0].ShareEnabled);
        Assert.True(fixture.ViewModel.CanStart);
    }

    [Fact]
    public void AcceptanceOnlyUsesConfiguredDefaultResultDirectory()
    {
        using var fixture = Fixture.Valid(
            ready: 1,
            options: new TransferWorkspaceOptions(DefaultResultDirectory: "C:\\results"),
            remoteWriteMode: RemoteWriteExecutionMode.AcceptanceOnly,
            managedAccount: true);

        Assert.Equal("C:\\results", fixture.ViewModel.ShareResultDirectory);
        Assert.Equal("C:\\results", fixture.Wizard.AcceptanceResultDirectory);
        Assert.True(fixture.ViewModel.CanStart);
    }

    [Fact]
    public async Task AcceptanceOnlyRunsExactPipelineBeforeResultsHandoff()
    {
        var acceptanceCalls = 0;
        using var fixture = Fixture.Valid(
            ready: 1,
            remoteWriteMode: RemoteWriteExecutionMode.AcceptanceOnly,
            managedAccount: true,
            acceptanceRunAsync: (batchId, _) =>
            {
                acceptanceCalls++;
                return Task.FromResult(new ManagedTransferAcceptanceRunOutcome(
                    ManagedTransferAcceptanceRunDisposition.Completed,
                    batchId,
                    Guid.NewGuid()));
            });
        fixture.ViewModel.ShareResultDirectory = "C:\\results";
        fixture.Events.Clear();

        await fixture.ViewModel.StartAsync();

        Assert.Equal(1, acceptanceCalls);
        Assert.Equal(["precheck", "preview", "confirm", "sink", "handoff"], fixture.Events);
        Assert.Contains("转存、分享和结果保存均已完成", fixture.ViewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AcceptanceConfigurationDetailsAreMappedBeforeEnteringVisibleLogs()
    {
        using var fixture = Fixture.Valid(
            ready: 2,
            remoteWriteMode: RemoteWriteExecutionMode.AcceptanceOnly,
            managedAccount: true);

        await fixture.ViewModel.StartAsync();

        Assert.Contains("现场验收", fixture.ViewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("ManagedChromiumV1", fixture.ViewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Equal("当前账号暂不可执行，请检查账号状态。", fixture.ViewModel.UserFacingStatusMessage);
        Assert.Contains(fixture.ViewModel.Logs,
            entry => entry.Message == "当前账号暂不可执行，请检查账号状态。");
        Assert.All(fixture.ViewModel.Logs, AssertLogContainsNoTechnicalTerms);
    }

    [Fact]
    public async Task MissingAcceptanceRunnerIsMappedBeforeEnteringVisibleLogs()
    {
        using var fixture = Fixture.Valid(
            ready: 1,
            remoteWriteMode: RemoteWriteExecutionMode.AcceptanceOnly,
            managedAccount: true);
        fixture.ViewModel.ShareResultDirectory = "C:\\results";

        await fixture.ViewModel.StartAsync();

        Assert.Contains("现场执行器", fixture.ViewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Equal("转存服务暂不可用，请稍后重试。", fixture.ViewModel.UserFacingStatusMessage);
        Assert.Contains(fixture.ViewModel.Logs,
            entry => entry.Message == "转存服务暂不可用，请稍后重试。");
        Assert.All(fixture.ViewModel.Logs, AssertLogContainsNoTechnicalTerms);
    }

    [Theory]
    [InlineData(false, null, true)]
    [InlineData(true, null, false)]
    [InlineData(true, "C:\\results", true)]
    public void ResultDirectoryIsRequiredOnlyForAutomaticSharing(
        bool autoShare,
        string? resultDirectory,
        bool expected)
    {
        using var fixture = Fixture.Valid();

        fixture.ViewModel.AutoShare = autoShare;
        fixture.ViewModel.ShareResultDirectory = resultDirectory ?? string.Empty;

        Assert.Equal(expected, fixture.ViewModel.CanStart);
    }

    [Fact]
    public async Task TransferOnlyCreatesNoShareWorkAndNeedsNoLocalDirectory()
    {
        using var fixture = Fixture.Valid();
        fixture.ViewModel.AutoShare = false;

        await fixture.ViewModel.StartAsync();

        var draft = Assert.IsType<WizardBatchDraft>(fixture.BatchService.ConfirmedDraft);
        Assert.All(draft.Accounts.Where(account => account.IsSelected), account => Assert.False(account.ShareEnabled));
        Assert.All(fixture.Wizard.Accounts.Where(account => account.IsSelected), account =>
        {
            Assert.Equal(0, account.PlannedShareCount);
            Assert.False(account.ShareEnabled);
        });
        Assert.Equal(0, fixture.BatchService.ShareOperationCount);
        Assert.Empty(fixture.DestinationSink.Values);
    }

    [Fact]
    public async Task AutomaticSharingPassesCreatedBatchAndExactDirectoryToInterimSink()
    {
        var directoryPreparer = new RecordingResultDirectoryPreparer();
        using var fixture = Fixture.Valid(resultDirectoryPreparer: directoryPreparer);
        fixture.ViewModel.AutoShare = true;
        fixture.ViewModel.ShareResultDirectory = "C:\\private\\results";

        await fixture.ViewModel.StartAsync();

        Assert.Equal(["C:\\private\\results"], directoryPreparer.Paths);
        var stored = Assert.Single(fixture.DestinationSink.Values);
        Assert.Equal(fixture.BatchService.ConfirmedDraft!.BatchJobId, stored.BatchJobId);
        Assert.Equal("C:\\private\\results", stored.ResultDirectory);
        Assert.Equal([stored.BatchJobId], fixture.HandedOffBatches);
    }

    [Fact]
    public async Task ResultDirectoryPreparationFailureDoesNotCreateBatch()
    {
        var directoryPreparer = new RecordingResultDirectoryPreparer
        {
            Error = new UnauthorizedAccessException("private path"),
        };
        using var fixture = Fixture.Valid(resultDirectoryPreparer: directoryPreparer);
        fixture.ViewModel.AutoShare = true;
        fixture.ViewModel.ShareResultDirectory = "C:\\private\\results";

        await fixture.ViewModel.StartAsync();

        Assert.Equal(["C:\\private\\results"], directoryPreparer.Paths);
        Assert.Equal(0, fixture.BatchService.ConfirmCalls);
        Assert.Empty(fixture.DestinationSink.Values);
        Assert.Contains("结果目录", fixture.ViewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void AutomaticSharingMapsToEveryParticipatingAccount()
    {
        using var fixture = Fixture.Valid(accountCount: 2, ready: 2);
        fixture.ViewModel.AutoShare = true;

        Assert.All(
            fixture.Wizard.Accounts.Where(account => account.IsSelected),
            account => Assert.True(account.ShareEnabled));
    }

    [Fact]
    public async Task AccountModesMapToExistingWizardAllocationSemantics()
    {
        using var fixture = Fixture.Valid(accountCount: 2, ready: 4);
        fixture.Wizard.Accounts[1].IsSelected = false;

        await fixture.ViewModel.SelectAccountModeAsync(TransferAccountMode.SingleAccount);
        fixture.Wizard.QuotaBasis = QuotaBasis.TaskCount;
        fixture.Wizard.Accounts[0].TargetItemCount = 4;
        Assert.True(fixture.ViewModel.CanStart);
        Assert.Equal(TransferMode.Distribution, fixture.Wizard.TransferMode);

        fixture.Wizard.Accounts[1].IsSelected = true;
        await fixture.ViewModel.SelectAccountModeAsync(TransferAccountMode.MultiAccountReplication);
        Assert.True(fixture.ViewModel.CanStart);
        Assert.Equal(TransferMode.Replication, fixture.Wizard.TransferMode);
        Assert.Equal(8, fixture.Wizard.OperationCount);

        await fixture.ViewModel.SelectAccountModeAsync(TransferAccountMode.MultiAccountDistribution);
        fixture.Wizard.QuotaBasis = QuotaBasis.TaskCount;
        fixture.Wizard.Accounts[0].TargetItemCount = 2;
        fixture.Wizard.Accounts[1].TargetItemCount = 2;
        Assert.True(fixture.ViewModel.CanStart);
        Assert.Equal(TransferMode.Distribution, fixture.Wizard.TransferMode);
        Assert.Equal(4, fixture.Wizard.AssignedCount);
    }

    [Fact]
    public async Task StartSavesCurrentEditThenPrechecksBeforeConfirmation()
    {
        using var fixture = Fixture.Valid();
        fixture.Import.SelectedRow = new(
            Guid.NewGuid(), 1, "https://pan.baidu.com/s/original", "a1b2", null, "就绪", null);
        fixture.Import.SelectedShareUrl = "https://pan.baidu.com/s/edited";
        fixture.Import.SelectedExtractionCode = "c3d4";
        fixture.ViewModel.AutoShare = true;
        fixture.ViewModel.ShareResultDirectory = "C:\\results";
        fixture.Events.Clear();

        await fixture.ViewModel.StartAsync();

        Assert.Equal(["save", "precheck", "preview", "confirm", "sink", "handoff"], fixture.Events);
        Assert.Equal("https://pan.baidu.com/s/edited", fixture.ImportWorkflow.SavedUrl);
        Assert.Equal("c3d4", fixture.ImportWorkflow.SavedCode);
        Assert.DoesNotContain("创建任务", fixture.Dialog.LastConfirmationMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnsavedCurrentEditReportsThatTransferDidNotStart()
    {
        using var fixture = Fixture.Valid();
        fixture.Import.SelectedRow = new(
            Guid.NewGuid(), 1, "https://pan.baidu.com/s/original", null, null, "就绪", null);
        fixture.Import.SelectedShareUrl = "https://pan.baidu.com/s/edited";
        fixture.ImportWorkflow.UpdateLinkException = new InvalidOperationException("save failed");

        await fixture.ViewModel.StartAsync();

        Assert.Equal("当前修改未保存，转存未开始。", fixture.ViewModel.StatusMessage);
        Assert.Equal("当前修改未保存，转存未开始。", fixture.ViewModel.UserFacingStatusMessage);
        Assert.Contains(fixture.ViewModel.Logs,
            entry => entry.Message == "当前修改未保存，转存未开始。");
        Assert.Equal(0, fixture.BatchService.PreviewCalls);
    }

    [Fact]
    public async Task ImportStillBusyAfterRowSaveReportsThatTransferDidNotStart()
    {
        using var fixture = Fixture.Valid();
        fixture.Import.SelectedRow = new(
            Guid.NewGuid(), 1, "https://pan.baidu.com/s/original", null, null, "就绪", null);
        fixture.Import.SelectedShareUrl = "https://pan.baidu.com/s/edited";
        var busyField = typeof(ImportViewModel).GetField(
            "isBusy", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var forcedBusyRace = false;
        fixture.Import.PropertyChanged += (_, args) =>
        {
            if (forcedBusyRace || args.PropertyName != nameof(ImportViewModel.IsBusy) || fixture.Import.IsBusy)
                return;
            forcedBusyRace = true;
            busyField.SetValue(fixture.Import, true);
        };

        await fixture.ViewModel.StartAsync();

        Assert.True(forcedBusyRace);
        Assert.Equal("导入数据仍在处理中，转存未开始。", fixture.ViewModel.StatusMessage);
        Assert.Equal("导入数据仍在处理中，转存未开始。", fixture.ViewModel.UserFacingStatusMessage);
        Assert.Contains(fixture.ViewModel.Logs,
            entry => entry.Message == "导入数据仍在处理中，转存未开始。");
        Assert.Equal(0, fixture.BatchService.PreviewCalls);
    }

    [Fact]
    public async Task CallerCancellationReportsCancellationInsteadOfPreparingStatus()
    {
        using var fixture = Fixture.Valid();
        using var cancellation = new CancellationTokenSource();
        fixture.BatchService.BlockPreview = true;
        var start = fixture.ViewModel.StartAsync(cancellation.Token);
        await fixture.BatchService.PreviewStarted.Task;

        cancellation.Cancel();
        await start;

        Assert.Equal("已取消转存。", fixture.ViewModel.StatusMessage);
        Assert.Equal("已取消转存。", fixture.ViewModel.UserFacingStatusMessage);
        Assert.Contains(fixture.ViewModel.Logs, entry => entry.Message == "已取消转存。");
    }

    [Fact]
    public async Task StartImmediatelyShowsPreparationStatusWhilePreviewIsPending()
    {
        using var fixture = Fixture.Valid();
        fixture.BatchService.BlockPreview = true;

        var start = fixture.ViewModel.StartAsync();
        await fixture.BatchService.PreviewStarted.Task;

        Assert.True(fixture.ViewModel.IsStarting);
        Assert.False(fixture.ViewModel.CanEditConfiguration);
        Assert.False(fixture.ViewModel.CanStart);
        Assert.Equal("正在准备转存任务……", fixture.ViewModel.StatusMessage);
        Assert.Equal("正在准备转存任务……", fixture.ViewModel.UserFacingStatusMessage);
        Assert.Contains(fixture.ViewModel.Logs,
            entry => entry.Message == "正在准备转存任务……");

        fixture.BatchService.ReleasePreview();
        await start;
    }

    [Theory]
    [InlineData("The import draft does not exist.", "导入数据已失效或不存在，请重新导入后再试。")]
    [InlineData("A participating account is unavailable.", "参与账号已失效或不可用，请重新选择账号。")]
    [InlineData("The task quota total must equal the included resource count.", "账号任务配额与有效链接数量不匹配，请重新分配。")]
    [InlineData("Direct-background batches require task-count distribution.", "批量转存配置不完整，请检查模式、同名文件处理和自动分享设置。")]
    [InlineData("AcceptanceOnly requires a valid local result directory.", "本机结果目录无效，请重新选择。")]
    [InlineData("The import contains unresolved rows that require user action.", "导入数据仍有冲突或未解决项，请先处理后再试。")]
    [InlineData("A share-enabled included row is missing its resolved output title.", "部分分享结果缺少标题，请检查导入数据后再试。")]
    [InlineData("请至少选择一个参与账号。", "请至少选择一个参与账号。")]
    public async Task StartMapsConcretePreviewValidationToSafeChinese(
        string validationMessage,
        string expectedStatus)
    {
        using var fixture = Fixture.Valid();
        fixture.BatchService.PreviewException = new BatchDraftValidationException(validationMessage);

        await fixture.ViewModel.StartAsync();

        Assert.Equal(expectedStatus, fixture.ViewModel.StatusMessage);
        Assert.Equal(expectedStatus, fixture.ViewModel.UserFacingStatusMessage);
        Assert.Contains(fixture.ViewModel.Logs, entry => entry.Message == expectedStatus);
        Assert.All(fixture.ViewModel.Logs, AssertLogContainsNoTechnicalTerms);
    }

    [Fact]
    public async Task NullConfirmationShowsValidationOrGenericInsteadOfPreparingStatus()
    {
        using var failed = Fixture.Valid();
        failed.BatchService.ConfirmException = new BatchConfirmationException("stale");

        await failed.ViewModel.StartAsync();

        Assert.Equal(failed.Wizard.ValidationMessage, failed.ViewModel.StatusMessage);
        Assert.Equal(failed.Wizard.ValidationMessage, failed.ViewModel.UserFacingStatusMessage);
        Assert.Contains(failed.ViewModel.Logs,
            entry => entry.Message == failed.Wizard.ValidationMessage);

        using var declined = Fixture.Valid();
        declined.Dialog.ConfirmationResult = false;

        await declined.ViewModel.StartAsync();

        Assert.True(declined.Wizard.LastConfirmationWasDeclined);
        Assert.Equal("已取消转存。", declined.ViewModel.StatusMessage);
        Assert.Equal("已取消转存。", declined.ViewModel.UserFacingStatusMessage);
        Assert.Contains(declined.ViewModel.Logs, entry => entry.Message == "已取消转存。");
    }

    [Fact]
    public async Task PendingImportMutationDisablesStartAndCannotCreateAStaleBatch()
    {
        using var fixture = Fixture.Valid();
        var row = new ImportPreviewRow(
            Guid.NewGuid(), 1, "https://pan.baidu.com/s/unchanged", null, null, "就绪", null);
        fixture.Import.SelectedRow = row;
        fixture.ImportWorkflow.BlockExclusions = true;
        var observedStartStates = new List<bool>();
        fixture.ViewModel.StartCommand.CanExecuteChanged +=
            (_, _) => observedStartStates.Add(fixture.ViewModel.StartCommand.CanExecute(null));

        var mutation = fixture.Import.SetExcludedAsync(row.RowId, true);
        await fixture.ImportWorkflow.ExclusionStarted.Task;
        var canStartWhilePending = fixture.ViewModel.CanStart;

        await fixture.ViewModel.StartAsync();
        var batchCreatedWhilePending = fixture.ViewModel.CreatedBatchJobId;

        fixture.ImportWorkflow.ReleaseExclusion();
        await mutation;

        Assert.Null(batchCreatedWhilePending);
        Assert.False(canStartWhilePending);
        Assert.Equal(0, fixture.BatchService.PreviewCalls);
        Assert.Equal(0, fixture.BatchService.ConfirmCalls);
        Assert.Contains(false, observedStartStates);
        Assert.True(fixture.ViewModel.CanStart);
        Assert.Contains(true, observedStartStates);

        await fixture.ViewModel.StartAsync();

        Assert.NotNull(fixture.ViewModel.CreatedBatchJobId);
    }

    [Fact]
    public async Task TemporaryAcceptanceSkipsLegacyPrecheckAndCreatesImmediateValidationBatch()
    {
        var availability = new TemporaryAcceptanceAdapterAvailability(
            new AcceptanceWriteLeaseOptions(
                isDebugBuild: true,
                [AcceptanceWriteLeaseOptions.Switch]));
        using var fixture = Fixture.Valid(
            precheck: new WizardPrecheckResult(
                WizardPrecheckStatus.Completed,
                0,
                OtherFailureCount: 1),
            adapterAvailability: availability);
        fixture.Events.Clear();

        await fixture.ViewModel.StartAsync();

        Assert.Equal(["preview", "confirm", "handoff"], fixture.Events);
        Assert.Equal("Immediate", fixture.BatchService.ConfirmedDraft?.ValidationMode);
        Assert.NotNull(fixture.ViewModel.CreatedBatchJobId);
    }

    [Fact]
    public async Task TemporaryAcceptanceStillRejectsInvalidConfigurationBeforeBatchWork()
    {
        var availability = new TemporaryAcceptanceAdapterAvailability(
            new AcceptanceWriteLeaseOptions(
                isDebugBuild: true,
                [AcceptanceWriteLeaseOptions.Switch]));
        using var fixture = Fixture.Create(
            adapterAvailability: availability);
        fixture.ApplyReadyDraft();
        fixture.Wizard.Accounts[0].DefaultTargetPath = string.Empty;
        fixture.Events.Clear();

        await fixture.ViewModel.StartAsync();

        Assert.Empty(fixture.Events);
        Assert.Equal(0, fixture.BatchService.PreviewCalls);
        Assert.Equal(0, fixture.BatchService.ConfirmCalls);
        Assert.Null(fixture.ViewModel.CreatedBatchJobId);
    }

    [Fact]
    public async Task ActionablePrecheckFailureStopsBeforeBatchPreview()
    {
        using var fixture = Fixture.Valid(precheck: new WizardPrecheckResult(
            WizardPrecheckStatus.Completed,
            4,
            InvalidLinkCount: 1,
            MissingExtractionCodeCount: 2));

        await fixture.ViewModel.StartAsync();

        Assert.Equal(0, fixture.BatchService.PreviewCalls);
        Assert.Contains("无效链接", fixture.ViewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("缺少提取码", fixture.ViewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownPrecheckFailureStopsTheWholeBatchBeforePreviewOrConfirmation()
    {
        using var fixture = Fixture.Valid(precheck: new WizardPrecheckResult(
            WizardPrecheckStatus.Completed,
            2,
            OtherFailureCount: 1));

        await fixture.ViewModel.StartAsync();

        Assert.Equal(0, fixture.BatchService.PreviewCalls);
        Assert.Equal(0, fixture.BatchService.ConfirmCalls);
        Assert.Null(fixture.ViewModel.CreatedBatchJobId);
        Assert.Contains("暂时无法确认", fixture.ViewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DirectUnavailablePrecheckStopsBeforeDurableCreationAndAllowsRetry()
    {
        using var fixture = Fixture.Valid(precheck: new WizardPrecheckResult(
            WizardPrecheckStatus.Unavailable,
            0));
        fixture.ViewModel.AutoShare = true;
        fixture.ViewModel.ShareResultDirectory = "C:\\results";

        await fixture.ViewModel.StartAsync();

        Assert.Equal("链接检查暂不可用，请稍后重试。", fixture.ViewModel.StatusMessage);
        Assert.Equal(0, fixture.BatchService.PreviewCalls);
        Assert.Equal(0, fixture.BatchService.ConfirmCalls);
        Assert.Empty(fixture.DestinationSink.Values);
        Assert.Empty(fixture.HandedOffBatches);
        Assert.Null(fixture.ViewModel.CreatedBatchJobId);
        Assert.True(fixture.ViewModel.CanStart);
    }

    [Fact]
    public async Task PrecheckExceptionMapsToUnavailableAndStopsEveryPostCheckAction()
    {
        using var fixture = Fixture.Valid(
            precheckWorkflow: new ThrowingPrecheckWorkflow());
        fixture.ViewModel.AutoShare = true;
        fixture.ViewModel.ShareResultDirectory = "C:\\results";

        await fixture.ViewModel.StartAsync();

        Assert.Equal("链接检查暂不可用，请稍后重试。", fixture.ViewModel.StatusMessage);
        Assert.Equal(0, fixture.BatchService.PreviewCalls);
        Assert.Equal(0, fixture.BatchService.ConfirmCalls);
        Assert.Empty(fixture.DestinationSink.Values);
        Assert.Empty(fixture.HandedOffBatches);
        Assert.Null(fixture.ViewModel.CreatedBatchJobId);
        Assert.True(fixture.ViewModel.CanStart);
    }

    [Fact]
    public async Task CompletedRetryAfterUnavailableCreatesExactlyOneBatch()
    {
        var precheck = new SequencePrecheckWorkflow(
            new(WizardPrecheckStatus.Unavailable, 0),
            new(WizardPrecheckStatus.Completed, 4));
        using var fixture = Fixture.Valid(precheckWorkflow: precheck);

        await fixture.ViewModel.StartAsync();
        Assert.Equal(0, fixture.BatchService.PreviewCalls);
        Assert.True(fixture.ViewModel.CanStart);

        await fixture.ViewModel.StartAsync();

        Assert.Equal(2, precheck.Calls);
        Assert.Equal(1, fixture.BatchService.PreviewCalls);
        Assert.Equal(1, fixture.BatchService.ConfirmCalls);
        Assert.Single(fixture.HandedOffBatches);
        Assert.NotNull(fixture.ViewModel.CreatedBatchJobId);
    }

    [Fact]
    public async Task InvalidSessionAccountTargetModeAndFixedCodeCannotStart()
    {
        using var fixture = Fixture.Create(accountCount: 2, ready: 4);
        Assert.False(fixture.ViewModel.CanStart);

        fixture.ApplyReadyDraft();
        Assert.False(fixture.ViewModel.CanStart);

        fixture.Wizard.Accounts[0].IsSelected = true;
        await fixture.ViewModel.SelectAccountModeAsync(TransferAccountMode.SingleAccount);
        fixture.Wizard.QuotaBasis = QuotaBasis.TaskCount;
        fixture.Wizard.Accounts[0].TargetItemCount = 4;
        Assert.True(fixture.ViewModel.CanStart);

        fixture.Wizard.Accounts[0].DefaultTargetPath = string.Empty;
        Assert.False(fixture.ViewModel.CanStart);
        fixture.Wizard.Accounts[0].DefaultTargetPath = "/目标";

        fixture.ViewModel.AutoShare = true;
        fixture.ViewModel.ShareResultDirectory = "C:\\results";
        fixture.ViewModel.ExtractionCodeBehavior = ShareExtractionCodeBehavior.Fixed;
        fixture.ViewModel.FixedExtractionCode = "bad!";
        Assert.False(fixture.ViewModel.CanStart);
        fixture.ViewModel.FixedExtractionCode = "a1B2";
        Assert.True(fixture.ViewModel.CanStart);

        fixture.Wizard.Accounts[0].IsSelected = false;
        Assert.False(fixture.ViewModel.CanStart);
    }

    [Fact]
    public void AccountRefreshKeepsNewAccountsOutOfSharingWhenAutomaticSharingIsOff()
    {
        using var fixture = Fixture.Valid();
        fixture.ViewModel.AutoShare = false;

        fixture.Wizard.ReloadAccounts([
            AccountOption("原账号"),
            AccountOption("新账号") with { ShareEnabled = true },
        ]);

        Assert.All(fixture.Wizard.Accounts, account => Assert.False(account.ShareEnabled));
    }

    [Fact]
    public async Task ExcludingEveryReadyRowRefreshesSnapshotAndDisablesStart()
    {
        using var fixture = Fixture.Valid(ready: 1);
        var session = Assert.IsType<Guid>(fixture.Import.SessionId);
        fixture.ImportWorkflow.NextMutation = new(
            session,
            new ImportDraftStatistics(1, 0, 0, 0, 1));

        await fixture.Import.SetExcludedAsync(Guid.NewGuid(), excluded: true);

        Assert.Equal(0, fixture.Wizard.ReadyResourceCount);
        Assert.False(fixture.ViewModel.CanStart);
        Assert.Contains("就绪 0", fixture.Import.StatisticsText, StringComparison.Ordinal);
        Assert.True(fixture.PageSource.LoadCalls > 0);
    }

    [Fact]
    public async Task RepairingAConflictRefreshesSameSessionAndCanEnableStart()
    {
        using var fixture = Fixture.Create(accountCount: 1, ready: 1);
        fixture.ApplyDraft(new ImportDraftStatistics(2, 1, 0, 0, 0, Conflict: 1));
        fixture.Wizard.QuotaBasis = QuotaBasis.TaskCount;
        fixture.Wizard.Accounts[0].TargetItemCount = 1;
        Assert.False(fixture.ViewModel.CanStart);
        var session = Assert.IsType<Guid>(fixture.Import.SessionId);
        fixture.ImportWorkflow.NextMutation = new(
            session,
            new ImportDraftStatistics(2, 1, 0, 0, 1));

        await fixture.Import.UpdateLinkAsync(Guid.NewGuid(), "https://pan.baidu.com/s/repaired", "a1b2");

        Assert.Equal(session, fixture.Wizard.CurrentImportSessionId);
        Assert.True(fixture.ViewModel.CanStart);
    }

    [Fact]
    public async Task ExcelAccountColumnCanBeResolvedForReplicationAndDistributionOnPage()
    {
        using var fixture = Fixture.Create(accountCount: 2, ready: 2);
        fixture.ApplyDraft(new ImportDraftStatistics(
            2, 2, 0, 0, 0,
            HasExcelAccountColumn: true,
            UnresolvedAccountMappings: 2));
        fixture.Wizard.Accounts[0].IsSelected = true;
        fixture.Wizard.Accounts[1].IsSelected = true;

        await fixture.ViewModel.SelectAccountModeAsync(TransferAccountMode.MultiAccountReplication);
        Assert.False(fixture.ViewModel.CanStart);
        await fixture.Wizard.IgnoreExcelAccountsAsync();
        Assert.True(fixture.ViewModel.CanStart);

        await fixture.ViewModel.SelectAccountModeAsync(TransferAccountMode.MultiAccountDistribution);
        fixture.Wizard.QuotaBasis = QuotaBasis.TaskCount;
        fixture.Wizard.Accounts[0].TargetItemCount = 1;
        fixture.Wizard.Accounts[1].TargetItemCount = 1;
        fixture.Wizard.RefreshImportSnapshot(2, 0, 0, true, 2);
        Assert.False(fixture.ViewModel.CanStart);
        await fixture.Wizard.AutoMapUniqueExcelAccountsAsync();
        Assert.True(fixture.ViewModel.CanStart);
    }

    [Fact]
    public async Task ExcelOverrideUsesExplicitlySelectedParticipatingAccount()
    {
        using var fixture = Fixture.Create(accountCount: 2, ready: 2);
        fixture.ApplyDraft(new ImportDraftStatistics(
            2, 2, 0, 0, 0,
            HasExcelAccountColumn: true,
            UnresolvedAccountMappings: 1));
        fixture.Wizard.Accounts[0].IsSelected = true;
        fixture.Wizard.Accounts[1].IsSelected = true;
        await fixture.ViewModel.SelectAccountModeAsync(TransferAccountMode.MultiAccountDistribution);
        fixture.Wizard.ExcelOverrideAccountId = fixture.Wizard.Accounts[1].AccountId;

        await fixture.Wizard.OverrideAllExcelAccountsAsync();

        Assert.Equal(fixture.Wizard.Accounts[1].AccountId, fixture.DraftWorkflow.LastOverrideAccountId);
        Assert.Equal(ExcelAccountHandling.OverrideAll, fixture.Wizard.ExcelAccountHandling);
    }

    [Fact]
    public async Task ExcelMappingsRequireCurrentEligibleSelectedAccountsAndReevaluateImmediately()
    {
        using var fixture = Fixture.Create(accountCount: 3, ready: 3);
        var mapped = fixture.Wizard.Accounts[2].AccountId;
        fixture.ApplyDraft(new ImportDraftStatistics(
            3, 3, 0, 0, 0,
            HasExcelAccountColumn: true,
            MappedAccountIds: [mapped]));
        fixture.Wizard.Accounts[0].IsSelected = true;
        fixture.Wizard.Accounts[1].IsSelected = true;
        await fixture.ViewModel.SelectAccountModeAsync(TransferAccountMode.MultiAccountDistribution);
        fixture.Wizard.RefreshImportSnapshot(3, 0, 0, true, 0, [mapped]);
        fixture.Wizard.ExcelAccountHandling = ExcelAccountHandling.KeepMappings;
        fixture.Wizard.QuotaBasis = QuotaBasis.TaskCount;
        fixture.Wizard.Accounts[0].TargetItemCount = 1;
        fixture.Wizard.Accounts[1].TargetItemCount = 1;
        fixture.Wizard.Accounts[2].TargetItemCount = 1;

        Assert.False(fixture.ViewModel.CanStart);
        Assert.Contains("已映射", fixture.ViewModel.ConfigurationMessage, StringComparison.Ordinal);

        fixture.Wizard.Accounts[2].IsSelected = true;

        Assert.True(fixture.ViewModel.CanStart);
    }

    [Fact]
    public async Task ExcelMappingToUnavailableAccountRequiresOverride()
    {
        using var fixture = Fixture.Create(accountCount: 2, ready: 2);
        var unavailable = Guid.NewGuid();
        fixture.ApplyDraft(new ImportDraftStatistics(
            2, 2, 0, 0, 0,
            HasExcelAccountColumn: true,
            MappedAccountIds: [unavailable]));
        fixture.Wizard.Accounts[0].IsSelected = true;
        fixture.Wizard.Accounts[1].IsSelected = true;
        await fixture.ViewModel.SelectAccountModeAsync(TransferAccountMode.MultiAccountDistribution);
        fixture.Wizard.RefreshImportSnapshot(2, 0, 0, true, 0, [unavailable]);
        fixture.Wizard.ExcelAccountHandling = ExcelAccountHandling.KeepMappings;
        fixture.Wizard.QuotaBasis = QuotaBasis.TaskCount;
        fixture.Wizard.Accounts[0].TargetItemCount = 1;
        fixture.Wizard.Accounts[1].TargetItemCount = 1;

        Assert.False(fixture.ViewModel.CanStart);
        Assert.Contains("账号已不可用", fixture.ViewModel.ConfigurationMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CloseCancelsStartAndPreventsLateSinkHandoffOrStatusMutation()
    {
        var precheck = new BlockingPrecheckWorkflow();
        using var fixture = Fixture.Valid(precheckWorkflow: precheck);
        var start = fixture.ViewModel.StartAsync();
        await precheck.Started.Task;
        var statusAtClose = fixture.ViewModel.StatusMessage;
        fixture.ViewModel.Close();
        precheck.Release();
        await start;

        Assert.Equal(statusAtClose, fixture.ViewModel.StatusMessage);
        Assert.Empty(fixture.DestinationSink.Values);
        Assert.Empty(fixture.HandedOffBatches);
        Assert.True(fixture.Import.IsClosed);
    }

    [Fact]
    public async Task SinkFailureAfterDurableCreationStillHandsOffAndCannotCreateDuplicate()
    {
        using var fixture = Fixture.Valid();
        fixture.ViewModel.AutoShare = true;
        fixture.ViewModel.ShareResultDirectory = "C:\\results";
        fixture.DestinationSink.Error = new IOException("sink unavailable");

        await fixture.ViewModel.StartAsync();
        var created = Assert.IsType<Guid>(fixture.ViewModel.CreatedBatchJobId);
        await fixture.ViewModel.StartAsync();

        Assert.Equal(1, fixture.BatchService.ConfirmCalls);
        Assert.Equal([created], fixture.HandedOffBatches);
        Assert.False(fixture.ViewModel.CanStart);
        Assert.Equal("批次已创建，结果目录暂未保存。", fixture.ViewModel.StatusMessage);
    }

    [Fact]
    public async Task HandoffFailureAfterDurableCreationReportsRecoverableBatch()
    {
        using var fixture = Fixture.Valid();
        fixture.Handoff.Error = new InvalidOperationException("navigation unavailable");

        await fixture.ViewModel.StartAsync();

        Assert.NotNull(fixture.ViewModel.CreatedBatchJobId);
        Assert.Equal(1, fixture.BatchService.ConfirmCalls);
        Assert.Contains(fixture.RunPanel.Batches,
            item => item.BatchJobId == fixture.ViewModel.CreatedBatchJobId);
        Assert.Contains("批次已创建", fixture.ViewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("稍后", fixture.ViewModel.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("转存未能开始", fixture.ViewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallerCancellationAfterDurableConfirmationStillCompletesPostCommitHandoff()
    {
        using var fixture = Fixture.Valid();
        using var cancellation = new CancellationTokenSource();
        fixture.BatchService.AfterConfirm = cancellation.Cancel;

        await fixture.ViewModel.StartAsync(cancellation.Token);

        var created = Assert.IsType<Guid>(fixture.ViewModel.CreatedBatchJobId);
        Assert.Equal([created], fixture.HandedOffBatches);
        Assert.Equal(1, fixture.BatchService.ConfirmCalls);
    }

    [Fact]
    public async Task CloseDuringPostCommitHandoffCancelsTokenWithoutLateUiMutation()
    {
        using var fixture = Fixture.Valid();
        fixture.Handoff.Block = true;
        var changes = new List<string?>();
        fixture.ViewModel.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

        var start = fixture.ViewModel.StartAsync();
        await fixture.Handoff.Started.Task;
        fixture.ViewModel.Close();
        var changesAtClose = changes.Count;
        fixture.Handoff.Release();
        await start;

        Assert.True(SpinWait.SpinUntil(() => fixture.Handoff.CancellationObserved, TimeSpan.FromSeconds(1)));
        Assert.Equal(changesAtClose, changes.Count);
        Assert.NotNull(fixture.ViewModel.CreatedBatchJobId);
    }

    [Fact]
    public async Task NonCooperativeHandoffTimesOutWithRecoverableBatchAndCannotDuplicateCreate()
    {
        using var fixture = Fixture.Valid(options: new TransferWorkspaceOptions(PostCommitTimeoutSeconds: 1));
        fixture.Handoff.Block = true;

        var start = fixture.ViewModel.StartAsync();
        await fixture.Handoff.Started.Task;
        await start.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.NotNull(fixture.ViewModel.CreatedBatchJobId);
        Assert.Contains("批次已创建", fixture.ViewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("恢复查看", fixture.ViewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(1, fixture.BatchService.ConfirmCalls);

        await fixture.ViewModel.StartAsync();
        Assert.Equal(1, fixture.BatchService.ConfirmCalls);
        fixture.Handoff.Release();
    }

    [Fact]
    public async Task RealHandoffCoordinatorTimeoutReportsLateFailureWithoutChangingCreatedBatchOutcome()
    {
        var operations = new NonCooperativeHandoffOperations();
        var reported = new List<Exception>();
        using var coordinator = new ResultWorkspaceHandoffCoordinator(operations, reported.Add);
        using var fixture = Fixture.Valid(
            options: new TransferWorkspaceOptions(PostCommitTimeoutSeconds: 1),
            explicitHandoff: coordinator.ActivateJobAsync);

        var start = fixture.ViewModel.StartAsync();
        await operations.Started.Task;
        await start.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.NotNull(fixture.ViewModel.CreatedBatchJobId);
        Assert.Contains("恢复查看", fixture.ViewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(1, fixture.BatchService.ConfirmCalls);
        operations.ReleaseWithFailure();
        Assert.True(SpinWait.SpinUntil(() => reported.Count == 1, TimeSpan.FromSeconds(1)));
        Assert.Equal(1, fixture.BatchService.ConfirmCalls);
    }

    [Fact]
    public void SinglePageSourceHasRequiredSectionsAndNoRetiredWorkflowLabels()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            root, "src", "BaiduBatchTransfer.App", "Features", "Transfers", "TransferWorkspaceView.xaml"));

        Assert.Contains("导入粘贴内容", xaml, StringComparison.Ordinal);
        Assert.Contains("默认目标目录", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("转存后自动生成分享链接", xaml, StringComparison.Ordinal);
        Assert.Contains("开始转存", xaml, StringComparison.Ordinal);
        Assert.Contains("运行日志", xaml, StringComparison.Ordinal);
        Assert.Contains("ScrollViewer", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("草稿", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("创建任务", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("批量预检", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("确认任务", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("NextStepCommand", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("PreviousStepCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding SelectSingleAccountCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding SelectReplicationCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding SelectDistributionCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{Binding IsSingleAccountMode, Mode=OneWay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{Binding IsReplicationMode, Mode=OneWay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{Binding IsDistributionMode, Mode=OneWay}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("IsChecked=\"{Binding AutoShare}\"", xaml, StringComparison.Ordinal);

        var appRoot = Path.Combine(root, "src", "BaiduBatchTransfer.App");
        foreach (var xamlPath in Directory.EnumerateFiles(appRoot, "*.xaml", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(xamlPath);
            Assert.DoesNotContain("草稿", source, StringComparison.Ordinal);
            Assert.DoesNotContain("创建任务", source, StringComparison.Ordinal);
            Assert.DoesNotContain("批量预检", source, StringComparison.Ordinal);
            Assert.DoesNotContain("确认任务", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void WorkbenchXamlUsesSingleScreenLayoutAndKeepsCoreControlsVisible()
    {
        var xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "BaiduBatchTransfer.App", "Features", "Transfers", "TransferWorkspaceView.xaml"));
        var document = XDocument.Parse(xaml);
        var root = document.Root;
        Assert.NotNull(root);
        var content = Assert.Single(root!.Elements());

        Assert.Equal("UserControl", root.Name.LocalName);
        Assert.Equal("Grid", content.Name.LocalName);
        Assert.DoesNotContain(content.Elements(), element => element.Name.LocalName == "ScrollViewer");

        var xNamespace = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");
        var namedSections = content.Descendants()
            .SelectMany(element => element.Attributes(xNamespace + "Name"))
            .Select(attribute => attribute.Value)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("TransferToolbar", namedSections);
        Assert.Contains("TransferDataRegion", namedSections);
        Assert.DoesNotContain("TransferLogRegion", namedSections);
        Assert.DoesNotContain("ToolbarActionWrap", namedSections);

        var rows = content.Elements()
            .Single(element => element.Name.LocalName == "Grid.RowDefinitions")
            .Elements()
            .Select(element => (string?)element.Attribute("Height"))
            .ToArray();
        Assert.Equal(new string?[] { "Auto", "*" }, rows);

        var toolbar = content.Descendants()
            .Single(element => (string?)element.Attribute(xNamespace + "Name") == "TransferToolbar");
        Assert.Null((string?)toolbar.Attribute("MaxHeight"));
        Assert.Equal("{Binding CanEditConfiguration}", (string?)toolbar.Attribute("IsEnabled"));

        Assert.Contains("Command=\"{Binding Import.ImportTextCommand}\"", xaml, StringComparison.Ordinal);
        var pasteInput = toolbar.Descendants()
            .Single(element => element.Name.LocalName == "TextBox" &&
                ((string?)element.Attribute("Text"))?.Contains("Import.TextContent", StringComparison.Ordinal) == true);
        Assert.Equal("True", (string?)pasteInput.Attribute("AcceptsReturn"));
        Assert.Contains("Command=\"{Binding Import.SelectAndImportExcelCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding StartCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding SelectSingleAccountCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding SelectReplicationCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding SelectDistributionCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("DefaultTargetPath", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("IsChecked=\"{Binding AutoShare}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("转存后自动生成分享链接", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"结果目录\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip=\"结果保存目录\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"结果保存目录\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip=\"默认目标目录\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"默认目标目录\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ShareResultDirectory", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding PickShareResultDirectoryCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectedItem=\"{Binding MaxConcurrency}\"", xaml, StringComparison.Ordinal);
        var concurrency = toolbar.Descendants().Single(element =>
            element.Name.LocalName == "ComboBox" &&
            (string?)element.Attribute("ItemsSource") == "{Binding ConcurrencyOptions}");
        Assert.Equal("58", (string?)concurrency.Attribute("Width"));
        Assert.Contains("ItemsSource=\"{Binding Import.Rows}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<Popup", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AdvancedSettingsToggle", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain(toolbar.Descendants(), element =>
            (string?)element.Attribute(xNamespace + "Name") == "AdvancedSettingsExpander");
        Assert.DoesNotContain(toolbar.Descendants(), element =>
            (string?)element.Attribute(xNamespace + "Name") == "ToolbarActionWrap");
        Assert.DoesNotContain("Wizard.ApplyEqualAllocationCommand", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Wizard.OverrideExcelMappingsCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ResultsHost\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Height=\"245\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("MinHeight=\"220\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource DenseDataGridStyle}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("RowHeight=\"34\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectedIndex=\"{Binding SelectedDataTabIndex}\"", xaml, StringComparison.Ordinal);
        var dataRegion = content.Descendants()
            .Single(element => (string?)element.Attribute(xNamespace + "Name") == "TransferDataRegion");
        var dataGrid = Assert.Single(dataRegion.Elements());
        Assert.Equal("Grid", dataGrid.Name.LocalName);
        var dataTabs = Assert.Single(dataGrid.Elements());
        Assert.Equal("TabControl", dataTabs.Name.LocalName);
        var tabTemplate = Assert.Single(dataTabs.Descendants(), element =>
            element.Name.LocalName == "ControlTemplate" && (string?)element.Attribute("TargetType") == "TabControl");
        var dataToolbar = tabTemplate.Descendants().Single(element =>
            (string?)element.Attribute(xNamespace + "Name") == "DataViewToolbar");
        Assert.Contains(dataToolbar.Descendants(), element =>
            (string?)element.Attribute("Text") == "{Binding Import.StatisticsText}");
        Assert.Contains(dataToolbar.Descendants(), element => element.Name.LocalName == "TabPanel" &&
            (string?)element.Attribute("IsItemsHost") == "True");
        Assert.Contains(dataToolbar.Descendants(), element =>
            (string?)element.Attribute(xNamespace + "Name") == "AccountModeSelectors");
        Assert.DoesNotContain(dataRegion.Descendants(), element => element.Name.LocalName == "TextBlock" &&
            (string?)element.Attribute("Text") is "资源数据" or "运行日志");
        var tabStyle = Assert.Single(dataTabs.Descendants(), element =>
            element.Name.LocalName == "Style" && (string?)element.Attribute("TargetType") == "TabItem");
        Assert.Contains(tabStyle.Descendants(), element => element.Name.LocalName == "ControlTemplate" &&
            (string?)element.Attribute("TargetType") == "TabItem");
        var selectedTabTrigger = tabStyle.Descendants().Single(element => element.Name.LocalName == "Trigger" &&
            (string?)element.Attribute("Property") == "IsSelected" && (string?)element.Attribute("Value") == "True");
        Assert.Contains(selectedTabTrigger.Elements(), element =>
            (string?)element.Attribute("Property") == "Foreground" && (string?)element.Attribute("Value") == "{StaticResource BrandBrush}");
        Assert.Contains(selectedTabTrigger.Elements(), element =>
            (string?)element.Attribute("Property") == "Background" && (string?)element.Attribute("Value") == "{StaticResource BrandSoftBrush}");
        var tabHeaders = dataRegion.Descendants()
            .Where(element => element.Name.LocalName == "TabItem")
            .Select(element => (string?)element.Attribute("Header"))
            .ToArray();
        Assert.Equal(new string?[] { "待处理链接", "处理结果", "运行日志" }, tabHeaders);
        Assert.Contains(dataRegion.Descendants(),
            element => (string?)element.Attribute(xNamespace + "Name") == "ResultsHost");

        Assert.Contains(dataRegion.Descendants(), element =>
            element.Name.LocalName == "ListBox" &&
            (string?)element.Attribute("ItemsSource") == "{Binding Logs}" &&
            (string?)element.Attribute("VirtualizingPanel.VirtualizationMode") == "Recycling");
        Assert.Contains(dataRegion.Descendants(), element =>
            (string?)element.Attribute(xNamespace + "Name") == "TransferStatusLiveRegion");
        Assert.Contains("Text=\"{Binding LocalTimestamp, StringFormat={}{0:HH:mm:ss}}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding LevelText}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<DataTrigger Binding=\"{Binding Level}\" Value=\"Success\">", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding Message}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TransferStatusLiveRegion\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding UserFacingStatusMessage}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"{Binding UserFacingStatusMessage}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"{Binding StatusMessage}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Opacity=\"0\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Width=\"1\" Height=\"1\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsHitTestVisible=\"False\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Visibility=\"Collapsed\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", xaml, StringComparison.Ordinal);

        Assert.DoesNotContain("NotValidated", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Acceptance", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("adapter", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ManagedChromium", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("browser", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("helper", xaml, StringComparison.OrdinalIgnoreCase);

        var mainWindowXaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "BaiduBatchTransfer.App", "MainWindow.xaml"));
        Assert.Contains("MinWidth=\"1180\"", mainWindowXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ShareWorkbenchXamlUsesLocalTimeAndChineseLogLevelText()
    {
        var xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "BaiduBatchTransfer.App", "Features", "Shares", "ShareWorkspaceView.xaml"));

        Assert.Contains("Text=\"{Binding LocalTimestamp, StringFormat={}{0:HH:mm:ss}}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding LevelText}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<DataTrigger Binding=\"{Binding Level}\" Value=\"Success\">", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void TopConfigurationToolbarStaysOnOneLineAtTypicalWorkspaceWidthOnSta()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var xaml = File.ReadAllText(Path.Combine(
                    FindRepositoryRoot(), "src", "BaiduBatchTransfer.App", "Features", "Transfers", "TransferWorkspaceView.xaml"));
                xaml = xaml.Replace(
                    " x:Class=\"BaiduBatchTransfer.App.Features.Transfers.TransferWorkspaceView\"",
                    string.Empty,
                    StringComparison.Ordinal);
                var openingTagEnd = xaml.IndexOf('>');
                const string resources = """
                    <UserControl.Resources>
                        <ResourceDictionary>
                            <ResourceDictionary.MergedDictionaries>
                                <ResourceDictionary Source="/BaiduBatchTransfer.App;component/Styles/SuperPanTheme.xaml" />
                            </ResourceDictionary.MergedDictionaries>
                            <BooleanToVisibilityConverter x:Key="BooleanToVisibilityConverter" />
                        </ResourceDictionary>
                    </UserControl.Resources>
                    """;
                xaml = xaml.Insert(openingTagEnd + 1, resources);

                using var fixture = Fixture.Valid();
                var view = Assert.IsType<UserControl>(XamlReader.Parse(xaml));
                view.DataContext = fixture.ViewModel;
                var toolbar = Assert.IsType<WrapPanel>(view.FindName("TopConfigurationToolbar"));
                view.FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI");
                view.FontSize = 14;
                var available = new Size(836, 600);
                view.Measure(available);
                view.Arrange(new Rect(available));
                view.UpdateLayout();

                var children = toolbar.Children.OfType<FrameworkElement>().ToArray();
                Assert.NotEmpty(children);
                var tops = children.Select(child => child.TransformToAncestor(toolbar)
                    .Transform(new Point()).Y).ToArray();
                Assert.True(tops.Max() - tops.Min() <= 1,
                    $"836px 工作区的顶部配置没有保持单行，各项顶部为 {string.Join(", ", tops)}px。");
                foreach (var child in children)
                {
                    var right = child.TransformToAncestor(toolbar)
                        .Transform(new Point(child.ActualWidth, 0)).X;
                    Assert.InRange(right, 1, toolbar.ActualWidth + 1);
                }
                var pasteInput = Assert.IsType<TextBox>(view.FindName("BulkPasteInput"));
                Assert.InRange(Math.Abs(pasteInput.ActualWidth - toolbar.ActualWidth), 0, 1);
                Console.WriteLine($"836px workspace: toolbar {toolbar.ActualWidth:N1}x{toolbar.ActualHeight:N1}px, " +
                    $"children desired widths {string.Join(", ", children.Select(child => child.DesiredSize.Width.ToString("N1")))}px, " +
                    $"tops {string.Join(", ", tops)}px, " +
                    $"paste input {pasteInput.ActualWidth:N1}px.");
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Typical toolbar width STA measure test timed out.");
        if (failure is not null) throw new TargetInvocationException(failure);
    }

    [Fact]
    public void CompactConcurrencySelectorRendersSelectedValueInsideReservedContentColumnOnSta()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var xaml = File.ReadAllText(Path.Combine(
                    FindRepositoryRoot(), "src", "BaiduBatchTransfer.App", "Features", "Transfers", "TransferWorkspaceView.xaml"));
                xaml = xaml.Replace(
                    " x:Class=\"BaiduBatchTransfer.App.Features.Transfers.TransferWorkspaceView\"",
                    string.Empty,
                    StringComparison.Ordinal);
                var openingTagEnd = xaml.IndexOf('>');
                const string resources = """
                    <UserControl.Resources>
                        <ResourceDictionary>
                            <ResourceDictionary.MergedDictionaries>
                                <ResourceDictionary Source="/BaiduBatchTransfer.App;component/Styles/SuperPanTheme.xaml" />
                            </ResourceDictionary.MergedDictionaries>
                            <BooleanToVisibilityConverter x:Key="BooleanToVisibilityConverter" />
                        </ResourceDictionary>
                    </UserControl.Resources>
                    """;
                xaml = xaml.Insert(openingTagEnd + 1, resources);

                using var fixture = Fixture.Valid();
                fixture.ViewModel.MaxConcurrency = 2;
                var view = Assert.IsType<UserControl>(XamlReader.Parse(xaml));
                view.DataContext = fixture.ViewModel;
                view.FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI");
                view.FontSize = 14;
                var available = new Size(836, 600);
                view.Measure(available);
                view.Arrange(new Rect(available));
                view.UpdateLayout();

                var selector = Assert.Single(VisualDescendants(view).OfType<ComboBox>(), candidate =>
                    ReferenceEquals(candidate.ItemsSource, fixture.ViewModel.ConcurrencyOptions));
                selector.ApplyTemplate();
                view.UpdateLayout();
                Assert.Equal("2", selector.SelectionBoxItem?.ToString());
                Assert.Equal(58, selector.ActualWidth);

                var toggle = Assert.Single(VisualDescendants(selector)
                    .OfType<System.Windows.Controls.Primitives.ToggleButton>());
                var layoutGrid = Assert.IsType<Grid>(toggle.Content);
                Assert.Equal(2, layoutGrid.ColumnDefinitions.Count);
                Assert.True(layoutGrid.ColumnDefinitions[0].ActualWidth > 0);
                Assert.True(layoutGrid.ColumnDefinitions[1].ActualWidth > 0);

                var selection = Assert.Single(layoutGrid.Children.OfType<ContentPresenter>());
                var arrow = Assert.Single(layoutGrid.Children.OfType<System.Windows.Shapes.Path>());
                Assert.Equal("2", selection.Content?.ToString());
                var selectionBounds = selection.TransformToAncestor(layoutGrid)
                    .TransformBounds(new Rect(selection.RenderSize));
                var arrowBounds = arrow.TransformToAncestor(layoutGrid)
                    .TransformBounds(new Rect(arrow.RenderSize));
                var arrowColumnStart = layoutGrid.ColumnDefinitions[0].ActualWidth;

                Assert.True(selectionBounds.Width > 0 && selectionBounds.Height > 0,
                    $"并发选中值没有可见尺寸：{selectionBounds}。");
                Assert.True(arrowBounds.Width > 0 && arrowBounds.Height > 0,
                    $"下拉箭头没有可见尺寸：{arrowBounds}。");
                Assert.True(selectionBounds.Left >= 0 && selectionBounds.Right <= arrowColumnStart + 0.5,
                    $"并发选中值 {selectionBounds} 越过内容列右边界 {arrowColumnStart:N1}px。");
                Assert.True(arrowBounds.Left >= arrowColumnStart - 0.5,
                    $"下拉箭头 {arrowBounds} 越过箭头列左边界 {arrowColumnStart:N1}px。");
                Assert.True(selectionBounds.Right <= arrowBounds.Left + 0.5,
                    $"并发选中值 {selectionBounds} 与下拉箭头 {arrowBounds} 重叠。");
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Compact concurrency selector STA layout test timed out.");
        if (failure is not null) throw new TargetInvocationException(failure);

        static IEnumerable<DependencyObject> VisualDescendants(DependencyObject parent)
        {
            for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
                yield return child;
                foreach (var descendant in VisualDescendants(child)) yield return descendant;
            }
        }
    }

    [Fact]
    public void WorkbenchMeasuresWithoutHorizontalOverflowAtSupportedMinimumWidthOnSta()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var xaml = File.ReadAllText(Path.Combine(
                    FindRepositoryRoot(), "src", "BaiduBatchTransfer.App", "Features", "Transfers", "TransferWorkspaceView.xaml"));
                xaml = xaml.Replace(
                    " x:Class=\"BaiduBatchTransfer.App.Features.Transfers.TransferWorkspaceView\"",
                    string.Empty,
                    StringComparison.Ordinal);
                var openingTagEnd = xaml.IndexOf('>');
                const string resources = """
                    <UserControl.Resources>
                        <ResourceDictionary>
                            <ResourceDictionary.MergedDictionaries>
                                <ResourceDictionary Source="/BaiduBatchTransfer.App;component/Styles/SuperPanTheme.xaml" />
                            </ResourceDictionary.MergedDictionaries>
                            <BooleanToVisibilityConverter x:Key="BooleanToVisibilityConverter" />
                        </ResourceDictionary>
                    </UserControl.Resources>
                    """;
                xaml = xaml.Insert(openingTagEnd + 1, resources);

                using var fixture = Fixture.Valid();
                var view = Assert.IsType<UserControl>(XamlReader.Parse(xaml));
                view.DataContext = fixture.ViewModel;
                var available = new Size(716, 500);
                var toolbar = Assert.IsType<Border>(view.FindName("TransferToolbar"));
                var startButton = Assert.IsType<Button>(view.FindName("StartTransferButton"));
                var resultDirectoryInput = Assert.IsType<TextBox>(view.FindName("ShareResultDirectoryInput"));
                var pasteInput = Assert.IsType<TextBox>(view.FindName("BulkPasteInput"));
                var dataRegion = Assert.IsType<Border>(view.FindName("TransferDataRegion"));

                view.Measure(available);
                view.Arrange(new Rect(available));
                view.UpdateLayout();

                Assert.InRange(view.DesiredSize.Width, 1, available.Width);
                Assert.InRange(toolbar.ActualWidth, 1, available.Width);
                var toolbarTop = toolbar.TransformToAncestor(view).Transform(new Point()).Y;
                var toolbarBottom = toolbar.TransformToAncestor(view)
                    .Transform(new Point(0, toolbar.ActualHeight)).Y;
                Assert.InRange(toolbarTop, 0, available.Height);
                Assert.InRange(toolbarBottom, toolbarTop + 1, available.Height);
                Assert.True(startButton.ActualWidth > 0);
                Assert.True(dataRegion.ActualHeight >= 80);
                Assert.True(RightEdgeWithin(startButton, toolbar));
                Assert.True(resultDirectoryInput.ActualWidth > 0);
                Assert.True(pasteInput.ActualHeight >= pasteInput.FontSize * 2,
                    $"批量粘贴输入框只有 {pasteInput.ActualHeight:N1}px，无法提供两行可用高度。");
                Assert.True(pasteInput.ActualWidth >= 300);
                var pendingGrid = FindPendingLinksGrid(dataRegion);
                Assert.True(pendingGrid.ActualHeight >= 68,
                    $"待处理表格只有 {pendingGrid.ActualHeight:N1}px，无法同时显示表头和一条数据。");
                Assert.True(dataRegion.ActualHeight >= 80);
                Assert.True(RightEdgeWithin(startButton, toolbar));
                Assert.True(RightEdgeWithin(resultDirectoryInput, toolbar));
                var configurationToolbar = Assert.IsType<WrapPanel>(view.FindName("TopConfigurationToolbar"));
                var configurationChildren = configurationToolbar.Children.OfType<FrameworkElement>().ToArray();
                var configurationRows = configurationChildren.Select(child => Math.Round(
                    child.TransformToAncestor(configurationToolbar).Transform(new Point()).Y)).Distinct().Count();
                Assert.InRange(configurationRows, 1, 2);
                Assert.All(configurationChildren, child => Assert.True(RightEdgeWithin(child, configurationToolbar)));
                Assert.InRange(Math.Abs(pasteInput.ActualWidth - configurationToolbar.ActualWidth), 0, 1);
                Console.WriteLine($"716px workspace: toolbar {configurationToolbar.ActualWidth:N1}x{configurationToolbar.ActualHeight:N1}px, " +
                    $"{configurationRows} rows, tops {string.Join(", ", configurationChildren.Select(child => child.TransformToAncestor(configurationToolbar).Transform(new Point()).Y))}px, " +
                    $"paste input {pasteInput.ActualWidth:N1}px.");
                var dataTabs = Assert.IsType<TabControl>(Assert.IsType<Grid>(dataRegion.Child).Children[0]);
                var dataToolbar = Assert.IsType<Grid>(dataTabs.Template.FindName("DataViewToolbar", dataTabs));
                var accountModes = Assert.IsType<StackPanel>(dataTabs.Template.FindName("AccountModeSelectors", dataTabs));
                Assert.InRange(dataToolbar.ActualHeight, 1, 36);
                Assert.True(RightEdgeWithin(dataToolbar, dataRegion));
                Assert.True(RightEdgeWithin(accountModes, dataToolbar));
                foreach (TabItem tab in dataTabs.Items)
                {
                    Assert.True(tab.ActualWidth > 0);
                    Assert.True(RightEdgeWithin(tab, dataToolbar));
                }
                var statistics = Assert.Single(dataToolbar.Children.OfType<TextBlock>());
                Assert.True(statistics.ActualWidth >= 60);
                Assert.Equal(fixture.Import.StatisticsText, statistics.ToolTip);
                dataTabs.SelectedIndex = 2;
                view.Measure(available);
                view.Arrange(new Rect(available));
                view.UpdateLayout();
                var logs = Assert.IsType<ListBox>(view.FindName("TransferLogsList"));
                var selectedContent = Assert.IsType<ContentPresenter>(dataTabs.Template.FindName("PART_SelectedContentHost", dataTabs));
                Assert.Same(Assert.IsType<TabItem>(dataTabs.Items[2]).Content, selectedContent.Content);
                Assert.True(logs.ActualHeight >= pendingGrid.ActualHeight);
                Assert.Equal(2, fixture.ViewModel.SelectedDataTabIndex);

                static bool RightEdgeWithin(FrameworkElement element, FrameworkElement ancestor)
                {
                    var right = element.TransformToAncestor(ancestor)
                        .Transform(new Point(element.ActualWidth, 0)).X;
                    return right <= ancestor.ActualWidth + 0.5;
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "STA measure test timed out.");
        if (failure is not null) throw new TargetInvocationException(failure);
    }

    [Fact]
    public void TallerViewportGivesAdditionalHeightToMainDataRegionOnSta()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var xaml = File.ReadAllText(Path.Combine(
                    FindRepositoryRoot(), "src", "BaiduBatchTransfer.App", "Features", "Transfers", "TransferWorkspaceView.xaml"));
                xaml = xaml.Replace(
                    " x:Class=\"BaiduBatchTransfer.App.Features.Transfers.TransferWorkspaceView\"",
                    string.Empty,
                    StringComparison.Ordinal);
                var openingTagEnd = xaml.IndexOf('>');
                const string resources = """
                    <UserControl.Resources>
                        <ResourceDictionary>
                            <ResourceDictionary.MergedDictionaries>
                                <ResourceDictionary Source="/BaiduBatchTransfer.App;component/Styles/SuperPanTheme.xaml" />
                            </ResourceDictionary.MergedDictionaries>
                            <BooleanToVisibilityConverter x:Key="BooleanToVisibilityConverter" />
                        </ResourceDictionary>
                    </UserControl.Resources>
                    """;
                xaml = xaml.Insert(openingTagEnd + 1, resources);

                var view = Assert.IsType<UserControl>(XamlReader.Parse(xaml));
                var dataRegion = Assert.IsType<Border>(view.FindName("TransferDataRegion"));
                var pasteInput = Assert.IsType<TextBox>(view.FindName("BulkPasteInput"));

                var compact = new Size(716, 500);
                view.Measure(compact);
                view.Arrange(new Rect(compact));
                view.UpdateLayout();

                var compactDataHeight = dataRegion.ActualHeight;
                var pendingGrid = FindPendingLinksGrid(dataRegion);
                Assert.True(pendingGrid.ActualHeight >= 68,
                    $"待处理表格只有 {pendingGrid.ActualHeight:N1}px，无法同时显示表头和一条数据。");
                Assert.True(pasteInput.ActualHeight >= pasteInput.FontSize * 2);

                var tall = new Size(716, 740);
                view.Measure(tall);
                view.Arrange(new Rect(tall));
                view.UpdateLayout();

                Assert.True(dataRegion.ActualHeight >= compactDataHeight + 200,
                    $"视口增加 240px 后，主区仅从 {compactDataHeight:N1}px 增至 {dataRegion.ActualHeight:N1}px。");
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Tall viewport STA measure test timed out.");
        if (failure is not null) throw new TargetInvocationException(failure);
    }

    [Fact]
    public void PasteInputAutoHeightClampsExplicitLinesBetweenTwoAndEightOnSta()
    {
        RunPasteInputAutoHeightOnSta((_, input, pump) =>
        {
            double HeightFor(string text)
            {
                input.Text = text;
                pump();
                return input.ActualHeight;
            }

            var emptyHeight = HeightFor(string.Empty);
            var oneLineHeight = HeightFor("一行链接");
            var fourLineHeight = HeightFor(string.Join('\n', Enumerable.Repeat("链接", 4)));
            var eightLineHeight = HeightFor(string.Join('\n', Enumerable.Repeat("链接", 8)));
            var overEightLineHeight = HeightFor(string.Join('\n', Enumerable.Repeat("链接", 12)));

            Assert.InRange(Math.Abs(emptyHeight - oneLineHeight), 0, 0.5);
            Assert.True(fourLineHeight > oneLineHeight + input.FontSize,
                $"四行高度 {fourLineHeight:N1}px 没有超过两行高度 {oneLineHeight:N1}px。");
            Assert.True(eightLineHeight > fourLineHeight + input.FontSize,
                $"八行高度 {eightLineHeight:N1}px 没有超过四行高度 {fourLineHeight:N1}px。");
            Assert.InRange(Math.Abs(overEightLineHeight - eightLineHeight), 0, 0.5);
        });
    }

    [Fact]
    public void PasteInputAutoHeightReflowsWrappedTextPausesWhileUnloadedAndResumesWhenReloadedOnSta()
    {
        RunPasteInputAutoHeightOnSta((owner, input, pump) =>
        {
            input.Text = string.Concat(Enumerable.Repeat("百度分享链接标题和提取码 ", 8));
            input.Width = 560;
            pump();
            var wideHeight = input.ActualHeight;

            input.Width = 170;
            pump();
            var narrowHeight = input.ActualHeight;

            input.Text = string.Join('\n', Enumerable.Repeat("链接", 8));
            pump();
            var eightLineCap = input.ActualHeight;

            Assert.True(narrowHeight > wideHeight + input.FontSize,
                $"宽度从 560px 缩至 170px 后，高度没有增加：{wideHeight:N1}px -> {narrowHeight:N1}px。");
            Assert.True(narrowHeight <= eightLineCap + 0.5,
                $"窄宽换行高度 {narrowHeight:N1}px 超过八行上限 {eightLineCap:N1}px。");

            input.Text = string.Concat(Enumerable.Repeat("百度分享链接标题和提取码 ", 8));
            pump();
            var heightAtUnload = input.ActualHeight;
            var localHeightAtUnload = Assert.IsType<double>(input.ReadLocalValue(FrameworkElement.HeightProperty));
            owner.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));

            input.Text = "卸载后的一行";
            input.Width = 560;
            pump();

            Assert.Equal(localHeightAtUnload,
                Assert.IsType<double>(input.ReadLocalValue(FrameworkElement.HeightProperty)));
            Assert.InRange(Math.Abs(input.ActualHeight - heightAtUnload), 0, 0.5);

            owner.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            input.Text = "重新加载后的一行";
            input.Width = 560;
            pump();
            var reloadedOneLineHeight = input.ActualHeight;

            input.Text = string.Join('\n', Enumerable.Repeat("重新加载后的链接", 4));
            pump();
            var reloadedFourLineHeight = input.ActualHeight;

            Assert.True(reloadedOneLineHeight < heightAtUnload - input.FontSize,
                $"重新加载后，一行高度仍停留在卸载时的 {heightAtUnload:N1}px。当前为 {reloadedOneLineHeight:N1}px。");
            Assert.True(reloadedFourLineHeight > reloadedOneLineHeight + input.FontSize,
                $"重新加载后高度未恢复自适应：一行为 {reloadedOneLineHeight:N1}px，四行为 {reloadedFourLineHeight:N1}px。");
        });
    }

    private static void RunPasteInputAutoHeightOnSta(
        Action<UserControl, TextBox, Action> test)
    {
        var helperType = typeof(TransferWorkspaceView).Assembly.GetType(
            "BaiduBatchTransfer.App.Features.Transfers.TransferPasteInputAutoHeight");
        Assert.True(helperType is not null,
            "缺少视图辅助器 BaiduBatchTransfer.App.Features.Transfers.TransferPasteInputAutoHeight。");
        var constructor = helperType!.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            [typeof(FrameworkElement), typeof(TextBox)],
            modifiers: null);
        Assert.True(constructor is not null,
            "TransferPasteInputAutoHeight 必须提供 (FrameworkElement owner, TextBox input) 构造函数。");

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var input = new TextBox
                {
                    Width = 560,
                    MinLines = 2,
                    MaxLines = 8,
                    AcceptsReturn = true,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalAlignment = VerticalAlignment.Top,
                    VerticalContentAlignment = VerticalAlignment.Top
                };
                var owner = new UserControl
                {
                    Width = 620,
                    Height = 360,
                    Content = input
                };
                _ = constructor!.Invoke([owner, input]);
                owner.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));

                test(owner, input, () =>
                {
                    var available = new Size(owner.Width, owner.Height);
                    owner.Measure(available);
                    owner.Arrange(new Rect(available));
                    owner.UpdateLayout();
                    var frame = new DispatcherFrame();
                    Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                        new Action(() => frame.Continue = false));
                    Dispatcher.PushFrame(frame);
                    owner.Measure(available);
                    owner.Arrange(new Rect(available));
                    owner.UpdateLayout();
                });
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Paste input auto-height STA test timed out.");
        if (failure is not null) throw new TargetInvocationException(failure);
    }

    private static DataGrid FindPendingLinksGrid(Border dataRegion)
    {
        var regionGrid = Assert.IsType<Grid>(dataRegion.Child);
        var tabs = Assert.Single(regionGrid.Children.OfType<TabControl>());
        var pendingTab = tabs.Items.OfType<TabItem>().First();
        return Assert.Single(Assert.IsType<Grid>(pendingTab.Content).Children.OfType<DataGrid>());
    }

    [Fact]
    public void RuntimeWorkspaceCopyDoesNotExposeForbiddenWorkflowTerms()
    {
        using var fixture = Fixture.Valid();
        var copy = fixture.Wizard.Steps.Select(step => step.Title)
            .Append(fixture.Wizard.ValidationModeText)
            .Append(fixture.Import.StatusMessage)
            .Append(fixture.Import.AdapterStatusMessage)
            .Append(fixture.ViewModel.StatusMessage)
            .Append(fixture.Wizard.ValidationMessage)
            .ToArray();

        Assert.All(copy, value =>
        {
            Assert.DoesNotContain("草稿", value, StringComparison.Ordinal);
            Assert.DoesNotContain("创建任务", value, StringComparison.Ordinal);
            Assert.DoesNotContain("批量预检", value, StringComparison.Ordinal);
            Assert.DoesNotContain("确认任务", value, StringComparison.Ordinal);
        });
    }

    private static string FindRepositoryRoot([CallerFilePath] string sourceFilePath = "")
    {
        var current = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "BaiduBatchTransfer.sln"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException();
    }

    private static void AssertLogContainsNoTechnicalTerms(WorkspaceLogEntry entry)
    {
        Assert.DoesNotContain("现场验收", entry.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("现场执行器", entry.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("账号引擎", entry.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ManagedChromium", entry.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NotValidated", entry.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("acceptance", entry.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("adapter", entry.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("helper", entry.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static WizardAccountOption AccountOption(string name) => new(
        Guid.NewGuid(), name, "Active", true, false, 1_000, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0,
        BatchAccountSelectionDecision.NotSelected,
        ShareEnabled: false);

    private sealed class Fixture : IDisposable
    {
        private Fixture(
            int accountCount,
            int ready,
            WizardPrecheckResult? precheck,
            IWizardPrecheckWorkflow? precheckWorkflow,
            TransferWorkspaceOptions? options,
            Func<Guid, CancellationToken, Task<ResultWorkspaceHandoffOutcome>>? explicitHandoff,
            IAdapterAvailability? adapterAvailability,
            RemoteWriteExecutionMode remoteWriteMode,
            bool managedAccount,
            Func<Guid, CancellationToken, Task<ManagedTransferAcceptanceRunOutcome>>? acceptanceRunAsync,
            ILocalResultDirectoryPreparer? resultDirectoryPreparer,
            IWorkspaceBatchHistorySource? historySource,
            IAsyncDelay? refreshDelay,
            IJobResultsWorkflow? resultsWorkflow,
            ITransferTargetDirectoryPicker? targetDirectoryPicker,
            ILicenseAuthorizationGate? licenseGate)
        {
            Ready = ready;
            Events = [];
            ImportWorkflow = new FakeImportWorkflow(Events);
            PageSource = new FakePageSource();
            Dialog = new FakeDialog();
            var availability = adapterAvailability ?? new AvailableAdapter();
            Import = new ImportViewModel(
                ImportWorkflow, PageSource, Dialog, availability);
            BatchService = new FakeBatchService(Events);
            DraftWorkflow = new FakeDraftWorkflow(ready);
            Wizard = new CreateJobWizardViewModel(
                Enumerable.Range(1, accountCount).Select(index => Account($"账号 {index}", managedAccount)).ToArray(),
                PageSource,
                BatchService,
                Dialog,
                availability,
                DraftWorkflow,
                precheckWorkflow ?? new FakePrecheckWorkflow(Events, precheck ?? new(WizardPrecheckStatus.Completed, ready)),
                new AvailableCapabilities());
            var results = new JobResultsViewModel(
                resultsWorkflow ?? new EmptyResultsWorkflow(), new FakeResultSaveDialog(), new AvailableCapabilities());
            RunPanel = new WorkspaceRunPanelViewModel(
                results,
                new PauseBatchService(new FakeRunControlStore()),
                new ResumeBatchService(new FakeRunControlStore()),
                new RetryFailedTransferService(new FakeRunControlStore()),
                historySource: historySource, automaticRefreshDelay: refreshDelay);
            DestinationSink = new RecordingDestinationSink(Events);
            Handoff = new RecordingHandoff(Events, HandedOffBatches);
            Func<Guid, CancellationToken, Task<ResultWorkspaceHandoffOutcome>> handoff = explicitHandoff ??
                (async (batch, token) =>
                {
                    await Handoff.RunAsync(batch, token);
                    return new(ResultWorkspaceHandoffStatus.Succeeded);
                });
            ViewModel = new TransferWorkspaceViewModel(
                Import, Wizard, RunPanel, new FakeResultSaveDialog(), DestinationSink, handoff,
                availability, options, remoteWriteMode, acceptanceRunAsync,
                resultDirectoryPreparer ?? new RecordingResultDirectoryPreparer(),
                targetDirectoryPicker, Dialog, licenseGate);
        }

        public int Ready { get; }
        public List<string> Events { get; }
        public FakeImportWorkflow ImportWorkflow { get; }
        public FakePageSource PageSource { get; }
        public FakeDialog Dialog { get; }
        public ImportViewModel Import { get; }
        public FakeBatchService BatchService { get; }
        public CreateJobWizardViewModel Wizard { get; }
        public FakeDraftWorkflow DraftWorkflow { get; }
        public WorkspaceRunPanelViewModel RunPanel { get; }
        public RecordingDestinationSink DestinationSink { get; }
        public RecordingHandoff Handoff { get; }
        public List<Guid> HandedOffBatches { get; } = [];
        public TransferWorkspaceViewModel ViewModel { get; }

        public static Fixture Create(
            int accountCount = 1,
            int ready = 4,
            WizardPrecheckResult? precheck = null,
            IWizardPrecheckWorkflow? precheckWorkflow = null,
            TransferWorkspaceOptions? options = null,
            Func<Guid, CancellationToken, Task<ResultWorkspaceHandoffOutcome>>? explicitHandoff = null,
            IAdapterAvailability? adapterAvailability = null,
            RemoteWriteExecutionMode remoteWriteMode = RemoteWriteExecutionMode.Full,
            bool managedAccount = false,
            Func<Guid, CancellationToken, Task<ManagedTransferAcceptanceRunOutcome>>? acceptanceRunAsync = null,
            ILocalResultDirectoryPreparer? resultDirectoryPreparer = null,
            IWorkspaceBatchHistorySource? historySource = null,
            IAsyncDelay? refreshDelay = null,
            IJobResultsWorkflow? resultsWorkflow = null,
            ITransferTargetDirectoryPicker? targetDirectoryPicker = null,
            ILicenseAuthorizationGate? licenseGate = null) =>
            new(accountCount, ready, precheck, precheckWorkflow, options, explicitHandoff, adapterAvailability,
                remoteWriteMode, managedAccount, acceptanceRunAsync, resultDirectoryPreparer, historySource,
                refreshDelay, resultsWorkflow, targetDirectoryPicker, licenseGate);

        public static Fixture Valid(
            int accountCount = 1,
            int ready = 4,
            WizardPrecheckResult? precheck = null,
            IWizardPrecheckWorkflow? precheckWorkflow = null,
            TransferWorkspaceOptions? options = null,
            Func<Guid, CancellationToken, Task<ResultWorkspaceHandoffOutcome>>? explicitHandoff = null,
            IAdapterAvailability? adapterAvailability = null,
            RemoteWriteExecutionMode remoteWriteMode = RemoteWriteExecutionMode.Full,
            bool managedAccount = false,
            Func<Guid, CancellationToken, Task<ManagedTransferAcceptanceRunOutcome>>? acceptanceRunAsync = null,
            ILocalResultDirectoryPreparer? resultDirectoryPreparer = null,
            ITransferTargetDirectoryPicker? targetDirectoryPicker = null,
            ILicenseAuthorizationGate? licenseGate = null)
        {
            var fixture = Create(
                accountCount,
                ready,
                precheck,
                precheckWorkflow,
                options,
                explicitHandoff,
                adapterAvailability,
                remoteWriteMode,
                managedAccount,
                acceptanceRunAsync,
                resultDirectoryPreparer,
                targetDirectoryPicker: targetDirectoryPicker,
                licenseGate: licenseGate);
            fixture.ApplyReadyDraft();
            if (accountCount == 1)
            {
                fixture.Wizard.QuotaBasis = QuotaBasis.TaskCount;
                fixture.Wizard.Accounts[0].TargetItemCount = ready;
            }
            else
            {
                fixture.Wizard.Accounts[0].IsSelected = true;
                fixture.ViewModel.SelectAccountModeAsync(TransferAccountMode.SingleAccount).GetAwaiter().GetResult();
                fixture.Wizard.QuotaBasis = QuotaBasis.TaskCount;
                fixture.Wizard.Accounts[0].TargetItemCount = ready;
            }
            fixture.ViewModel.AutoShare = false;
            return fixture;
        }

        public void ApplyReadyDraft() => ApplyDraft(new(Ready, Ready, 0, 0, 0));

        public void ApplyDraft(ImportDraftStatistics statistics)
        {
            var draft = new ImportDraftReady(Guid.NewGuid(), statistics);
            ImportWorkflow.NextMutation = new(draft.SessionId, statistics);
            Import.AttachSession(draft.SessionId);
            ViewModel.ApplyImportedDraft(draft);
        }

        public void Dispose()
        {
            ViewModel.Dispose();
        }

        private static WizardAccountOption Account(string name, bool managed)
        {
            var accountId = Guid.NewGuid();
            return new(
            accountId, name, "Active", true, false, 1_000, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0,
            BatchAccountSelectionDecision.NotSelected,
            ShareEnabled: false,
            ExecutionProfile: managed
                ? ExecutionProfileSnapshot.Managed(BrowserFamily.Chrome, "profile", Guid.NewGuid(), Guid.NewGuid())
                : ExecutionProfileSnapshot.LegacyForAccount(accountId));
        }
    }

    private sealed class RecordingLicenseGate(bool isAuthorized) : ILicenseAuthorizationGate
    {
        public List<LicenseOperationKind> Operations { get; } = [];

        public Task<LicenseGateResult> AuthorizeAsync(
            LicenseOperationKind operation,
            CancellationToken cancellationToken)
        {
            Operations.Add(operation);
            return Task.FromResult(new LicenseGateResult(
                operation,
                isAuthorized,
                UsedCachedLease: false,
                isAuthorized ? LicenseCodes.Ok : "activation_required"));
        }
    }

    private sealed class FakeImportWorkflow(List<string> events) : IImportWorkflow
    {
        private readonly TaskCompletionSource exclusionRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string? SavedUrl { get; private set; }
        public string? SavedCode { get; private set; }
        public ImportWorkflowResult? NextMutation { get; set; }
        public Exception? UpdateLinkException { get; set; }
        public bool BlockExclusions { get; set; }
        public TaskCompletionSource ExclusionStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ImportWorkflowResult> ImportTextAsync(Guid sessionId, string text, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ImportWorkflowResult> ImportExcelAsync(Guid sessionId, string filePath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ImportWorkflowResult> ResumeAsync(Guid sessionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public async Task<ImportWorkflowResult> SetExcludedAsync(Guid rowId, bool excluded, CancellationToken cancellationToken)
        {
            if (BlockExclusions)
            {
                ExclusionStarted.TrySetResult();
                await exclusionRelease.Task.WaitAsync(cancellationToken);
            }
            return NextMutation ?? throw new InvalidOperationException("A mutation result is required.");
        }

        public void ReleaseExclusion() => exclusionRelease.TrySetResult();

        public Task<ImportWorkflowResult> UpdateLinkAsync(Guid rowId, string shareUrl, string? extractionCode, CancellationToken cancellationToken)
        {
            events.Add("save");
            if (UpdateLinkException is not null)
                return Task.FromException<ImportWorkflowResult>(UpdateLinkException);
            SavedUrl = shareUrl;
            SavedCode = extractionCode;
            return Task.FromResult(NextMutation ?? new ImportWorkflowResult(
                Guid.NewGuid(), new ImportDraftStatistics(4, 4, 0, 0, 0)));
        }
    }

    private sealed class FakePageSource : IImportPreviewPageSource
    {
        public int LoadCalls { get; private set; }
        public Task<ImportPreviewPage> LoadAsync(ImportPreviewPageRequest request, CancellationToken cancellationToken)
        {
            LoadCalls++;
            return Task.FromResult(new ImportPreviewPage([], 0, null));
        }
    }

    private sealed class FakeBatchService(List<string> events) : IWizardBatchService
    {
        private readonly TaskCompletionSource previewRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int PreviewCalls { get; private set; }
        public int ShareOperationCount { get; private set; }
        public WizardBatchDraft? ConfirmedDraft { get; private set; }
        public int ConfirmCalls { get; private set; }
        public Action? AfterConfirm { get; set; }
        public Exception? ConfirmException { get; set; }
        public bool BlockPreview { get; set; }
        public Exception? PreviewException { get; set; }
        public TaskCompletionSource PreviewStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<WizardConfirmationSummary> PreviewAsync(WizardBatchDraft draft, CancellationToken cancellationToken)
        {
            events.Add("preview");
            PreviewCalls++;
            PreviewStarted.TrySetResult();
            if (BlockPreview) await previewRelease.Task.WaitAsync(cancellationToken);
            if (PreviewException is not null) throw PreviewException;
            var selected = draft.Accounts.Where(account => account.IsSelected).ToArray();
            var transferCount = draft.TransferMode == TransferMode.Replication
                ? (long)draft.ReadyResourceCount * selected.Length
                : draft.ReadyResourceCount;
            var confirmations = selected.Select(account =>
            {
                var plannedTransfer = draft.TransferMode == TransferMode.Replication
                    ? draft.ReadyResourceCount
                    : account.TargetItemCount;
                var plannedShare = account.ShareEnabled ? plannedTransfer : 0;
                ShareOperationCount += plannedShare;
                return new WizardAccountConfirmation(
                    account.AccountId, account.TargetSuccessCount, account.TargetSuccessCount,
                    account.TargetSuccessCount, 0, 0, 0, 0, 0, 0,
                    plannedTransfer, plannedShare, plannedTransfer, plannedShare, account.ShareEnabled);
            }).ToArray();
            var shareCount = confirmations.Sum(account => account.PlannedShareCount ?? 0);
            return new WizardConfirmationSummary(
                draft.ReadyResourceCount, transferCount, "token", true, confirmations,
                TransferCount: transferCount, ShareCount: shareCount,
                TotalActionCount: transferCount + shareCount, ResultColumnCount: selected.Length * 3);
        }

        public void ReleasePreview() => previewRelease.TrySetResult();

        public Task<Guid> ConfirmAsync(WizardBatchDraft draft, string confirmationToken, CancellationToken cancellationToken)
        {
            events.Add("confirm");
            ConfirmCalls++;
            if (ConfirmException is not null) throw ConfirmException;
            ConfirmedDraft = draft;
            AfterConfirm?.Invoke();
            return Task.FromResult(draft.BatchJobId);
        }
    }

    private sealed class FakePrecheckWorkflow(List<string> events, WizardPrecheckResult result) : IWizardPrecheckWorkflow
    {
        public Task<WizardPrecheckResult> RunAsync(Guid sessionId, Guid accountId, CancellationToken cancellationToken)
        {
            events.Add("precheck");
            return Task.FromResult(result);
        }
    }

    private sealed class ThrowingPrecheckWorkflow : IWizardPrecheckWorkflow
    {
        public Task<WizardPrecheckResult> RunAsync(
            Guid sessionId,
            Guid accountId,
            CancellationToken cancellationToken) =>
            Task.FromException<WizardPrecheckResult>(new InvalidOperationException("adapter detail"));
    }

    private sealed class SequencePrecheckWorkflow(params WizardPrecheckResult[] results) : IWizardPrecheckWorkflow
    {
        private readonly Queue<WizardPrecheckResult> remaining = new(results);
        public int Calls { get; private set; }

        public Task<WizardPrecheckResult> RunAsync(
            Guid sessionId,
            Guid accountId,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(remaining.Dequeue());
        }
    }

    private sealed class BlockingPrecheckWorkflow : IWizardPrecheckWorkflow
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<WizardPrecheckResult> RunAsync(Guid sessionId, Guid accountId, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await release.Task;
            cancellationToken.ThrowIfCancellationRequested();
            return new(WizardPrecheckStatus.Completed, 4);
        }
        public void Release() => release.TrySetResult();
    }

    private sealed class FakeDraftWorkflow(int ready) : IWizardDraftWorkflow
    {
        private readonly Dictionary<TransferMode, ModeSwitchGate> modeSwitchGates = [];
        public Guid? LastOverrideAccountId { get; private set; }
        public Exception? ModeSwitchException { get; set; }
        public int ModeSwitchCallCount { get; private set; }
        private WizardDraftMutation Mutation => new(ready, ready, 0, 0, 0, 0, 0);
        public ModeSwitchGate QueueModeSwitch(TransferMode mode, bool ignoreCancellation = false)
        {
            var gate = new ModeSwitchGate(ignoreCancellation);
            modeSwitchGates.Add(mode, gate);
            return gate;
        }

        public async Task<WizardDraftMutation> SwitchModeAsync(Guid sessionId, TransferMode mode, CancellationToken cancellationToken)
        {
            ModeSwitchCallCount++;
            if (ModeSwitchException is { } error) throw error;
            if (modeSwitchGates.Remove(mode, out var gate)) await gate.WaitAsync(cancellationToken);
            return Mutation;
        }
        public Task<WizardDraftMutation> AutoMapUniqueAccountNamesAsync(Guid sessionId, TransferMode mode, CancellationToken cancellationToken) => Task.FromResult(Mutation);
        public Task<WizardDraftMutation> IgnoreAllAccountNamesAsync(Guid sessionId, TransferMode mode, CancellationToken cancellationToken) => Task.FromResult(Mutation);
        public Task<WizardDraftMutation> OverrideAllAccountsAsync(Guid sessionId, Guid accountId, TransferMode mode, CancellationToken cancellationToken)
        {
            LastOverrideAccountId = accountId;
            return Task.FromResult(Mutation);
        }
    }

    private sealed class ModeSwitchGate(bool ignoreCancellation)
    {
        private readonly TaskCompletionSource release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task WaitAsync(CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            if (ignoreCancellation)
            {
                await release.Task;
                return;
            }
            try
            {
                await release.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved.TrySetResult();
                throw;
            }
        }

        public void Release() => release.TrySetResult();
    }

    private sealed class RecordingDestinationSink(List<string> events) : ITransferResultDestinationSink
    {
        public List<TransferResultDestination> Values { get; } = [];
        public Exception? Error { get; set; }
        public Task SetAsync(TransferResultDestination destination, CancellationToken cancellationToken = default)
        {
            events.Add("sink");
            if (Error is not null) throw Error;
            Values.Add(destination);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingResultDirectoryPreparer : ILocalResultDirectoryPreparer
    {
        public List<string> Paths { get; } = [];
        public Exception? Error { get; set; }

        public Task EnsureExistsAsync(string directoryPath, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Paths.Add(directoryPath);
            return Error is null ? Task.CompletedTask : Task.FromException(Error);
        }
    }

    private sealed class RecordingTargetDirectoryPicker(string? result) : ITransferTargetDirectoryPicker
    {
        public int Calls { get; private set; }
        public Guid? AccountId { get; private set; }
        public string? InitialPath { get; private set; }
        public Exception? Exception { get; init; }

        public Task<string?> PickAsync(
            Guid accountId,
            string initialPath,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            AccountId = accountId;
            InitialPath = initialPath;
            return Exception is null
                ? Task.FromResult(result)
                : Task.FromException<string?>(Exception);
        }
    }

    private sealed class ControlledTargetDirectoryPicker : ITransferTargetDirectoryPicker
    {
        private readonly TaskCompletionSource<string?> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Guid? AccountId { get; private set; }
        public string? InitialPath { get; private set; }

        public Task<string?> PickAsync(
            Guid accountId,
            string initialPath,
            CancellationToken cancellationToken = default)
        {
            AccountId = accountId;
            InitialPath = initialPath;
            Started.TrySetResult();
            return completion.Task.WaitAsync(cancellationToken);
        }

        public void Complete(string? path) => completion.TrySetResult(path);
    }

    private sealed class RecordingHandoff(List<string> events, List<Guid> batches)
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? Error { get; set; }
        public bool Block { get; set; }
        public bool CancellationObserved { get; private set; }

        public async Task RunAsync(Guid batch, CancellationToken cancellationToken)
        {
            events.Add("handoff");
            if (Error is not null) throw Error;
            if (Block)
            {
                Started.TrySetResult();
                await release.Task;
                CancellationObserved = cancellationToken.IsCancellationRequested;
                cancellationToken.ThrowIfCancellationRequested();
            }
            batches.Add(batch);
        }

        public void Release() => release.TrySetResult();
    }

    private sealed class NonCooperativeHandoffOperations : IResultWorkspaceHandoffOperations
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<Guid?> FindPendingBatchAsync(CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);
        public async Task InitializeAsync(Guid batchJobId, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await release.Task;
            throw new InvalidOperationException("late private detail");
        }
        public Task ResolveExistingSharesAsync(Guid batchJobId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task NavigateToResultsAsync(Guid batchJobId, CancellationToken cancellationToken) => Task.CompletedTask;
        public void ReleaseWithFailure() => release.TrySetResult();
    }

    private sealed class FakeDialog : IDialogService
    {
        public List<(string Title, string Message)> Messages { get; } = [];
        public bool FailMessage { get; set; }
        public Action<string>? BeforeMessage { get; set; }
        public string LastConfirmationMessage { get; private set; } = string.Empty;
        public bool ConfirmationResult { get; set; } = true;
        public Task<string?> PickExcelFileAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default)
        {
            LastConfirmationMessage = message;
            return Task.FromResult(ConfirmationResult);
        }
        public Task ShowMessageAsync(string title, string message, CancellationToken cancellationToken = default)
        {
            BeforeMessage?.Invoke(message);
            Messages.Add((title, message));
            return FailMessage ? Task.FromException(new IOException("private dialog details")) : Task.CompletedTask;
        }
    }

    private sealed class FakeResultSaveDialog : IResultSaveDialog
    {
        public Task<string?> PickDestinationDirectoryAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }

    private sealed class AvailableAdapter : IAdapterAvailability
    {
        public bool CanStartTransfers => true;
        public string StatusMessage => string.Empty;
    }

    private sealed class AvailableCapabilities : IJobResultCapabilityAvailability
    {
        public bool CanUseTransferActions => true;
        public bool CanUseShareActions => true;
        public string TransferStatusMessage => string.Empty;
        public string ShareStatusMessage => string.Empty;
    }

    private sealed class EmptyResultsWorkflow : IJobResultsWorkflow
    {
        public IReadOnlyList<JobResultRow> PageItems { get; set; } = [];
        public bool FailSummary { get; set; }
        public long TerminalProblemCount { get; set; } = 2;
        public Task<JobResultsSummary> LoadSummaryAsync(Guid batchJobId, CancellationToken cancellationToken = default) =>
            FailSummary ? Task.FromException<JobResultsSummary>(new IOException("private statistics details")) :
                Task.FromResult(new JobResultsSummary(5, 5, 1, 0, 3, TerminalProblemCount));
        public Task<JobResultsPage> LoadPageAsync(Guid batchJobId, long? afterCursor, int pageSize, CancellationToken cancellationToken = default) => Task.FromResult(new JobResultsPage(PageItems, PageItems.Count, null));
        public Task<JobResultActionOutcome> RetryShareOnlyAsync(JobResultRow row, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JobResultActionOutcome> ReconcileTransferAsync(Guid transferOperationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JobResultActionOutcome> ReconcileShareAsync(Guid shareOperationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JobResultActionOutcome> ResumeTransferAsync(JobResultRow row, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JobResultActionOutcome> ResumeShareAsync(JobResultRow row, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<long> ExportAsync(Guid batchJobId, string destinationDirectory, IProgress<long> progress, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeRunControlStore : IBatchRunControlStore
    {
        public Task<PauseBatchDisposition> TryPauseBatchAsync(Guid batchJobId, CancellationToken cancellationToken = default) => Task.FromResult(PauseBatchDisposition.Paused);
        public Task<ResumeBatchDisposition> TryResumeBatchAsync(Guid batchJobId, CancellationToken cancellationToken = default) => Task.FromResult(ResumeBatchDisposition.Queued);
        public Task<RetryFailedTransferDisposition> TryRetryFailedTransferAsync(Guid operationId, DateTimeOffset retriedAt, CancellationToken cancellationToken = default) => Task.FromResult(RetryFailedTransferDisposition.Requeued);
    }
}
