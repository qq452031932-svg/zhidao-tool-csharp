using BaiduBatchTransfer.App;
using BaiduBatchTransfer.App.Features.Jobs;
using BaiduBatchTransfer.App.Features.Licensing;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Application.Accounts;
using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Core.Domain;
using CommunityToolkit.Mvvm.Input;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using SuperPan.Licensing.Client;

namespace BaiduBatchTransfer.App.Tests;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public void ShellUsesAccountFeatureAndContentColumns()
    {
        var document = LoadMainWindowDocument();
        var shellBody = FindNamedElement(document, "ShellBody");
        var columns = shellBody.Element(PresentationNamespace + "Grid.ColumnDefinitions")!
            .Elements(PresentationNamespace + "ColumnDefinition")
            .Select(column => AttributeValue(column, "Width") ?? string.Empty)
            .ToArray();

        Assert.Equal(["240", "10", "170", "10", "*"], columns);

        var accountRail = FindNamedElement(document, "AccountRail");
        var accountCommands = accountRail.Descendants(PresentationNamespace + "Button")
            .Select(button => AttributeValue(button, "Command"))
            .ToArray();
        Assert.Contains("{Binding OpenAccountLoginCommand}", accountCommands);
        Assert.Contains("{Binding OpenAccountManagementCommand}", accountCommands);
        Assert.Contains("{Binding RefreshAccountsCommand}", accountCommands);

        var featureRail = FindNamedElement(document, "FeatureRail");
        var featureButtons = featureRail.Descendants(PresentationNamespace + "Button").ToArray();
        Assert.Equal(
            ["首页", "分享数据", "批量转存", "广告插入", "文件排除", "数据统计", "设置中心"],
            featureButtons.Select(button => AttributeValue(button, "Content")));
        Assert.Equal(
            [
                "{Binding ShowHomeCommand}",
                "{Binding ShowShareDataCommand}",
                "{Binding ShowBatchTransferCommand}",
                "{Binding ShowAdInsertionCommand}",
                "{Binding ShowFileExclusionCommand}",
                "{Binding ShowStatisticsCommand}",
                "{Binding ShowSettingsCommand}",
            ],
            featureButtons.Select(button => AttributeValue(button, "Command")));

        var contentWorkbench = FindNamedElement(document, "ContentWorkbench");
        var contentControl = Assert.Single(contentWorkbench.Descendants(PresentationNamespace + "ContentControl"));
        Assert.Equal("{Binding CurrentContent}", AttributeValue(contentControl, "Content"));
    }

    [Fact]
    public void AccountRailBindsTheExistingWizardAccountCollection()
    {
        var accounts = new ObservableCollection<AccountAllocationRowViewModel>();
        var vm = CreateViewModel(new FakeLoginLauncher(), accounts: accounts);
        var document = LoadMainWindowDocument();

        Assert.Same(accounts, vm.Accounts);
        var accountRail = FindNamedElement(document, "AccountRail");
        var scrollViewer = Assert.Single(accountRail.Descendants(PresentationNamespace + "ScrollViewer"));
        var itemsControl = Assert.Single(scrollViewer.Descendants(PresentationNamespace + "ItemsControl"));
        Assert.Equal("{Binding Accounts}", AttributeValue(itemsControl, "ItemsSource"));

        var accountCard = Assert.Single(
            itemsControl.Descendants(PresentationNamespace + "ToggleButton"),
            element => AttributeValue(element, XamlNamespace + "Name") == "AccountCard");
        var textBindings = accountCard.Descendants(PresentationNamespace + "TextBlock")
            .Select(textBlock => AttributeValue(textBlock, "Text"))
            .Where(value => value is not null)
            .ToArray();
        Assert.Contains("{Binding DisplayName}", textBindings);
        Assert.Contains("{Binding StatusText}", textBindings);
        Assert.Contains("{Binding CapacitySummaryText}", textBindings);
        var avatarBrush = Assert.Single(accountCard.Descendants(PresentationNamespace + "ImageBrush"));
        Assert.Equal("{Binding AvatarUri}", AttributeValue(avatarBrush, "ImageSource"));
        Assert.DoesNotContain("{Binding RemainingCapacityText}", textBindings);
        Assert.DoesNotContain("{Binding CapacityUsedText}", textBindings);
        Assert.DoesNotContain("{Binding RemainingCapacity}", textBindings);
        Assert.DoesNotContain("{Binding CapacityUsed}", textBindings);
        Assert.DoesNotContain("空间扫描", File.ReadAllText(MainWindowXamlPath()), StringComparison.Ordinal);
    }

    [Fact]
    public void FeatureNavigationMatchesTheCenteredTopAlignedReferenceLayout()
    {
        var document = LoadMainWindowDocument();
        var featureRail = FindNamedElement(document, "FeatureRail");
        var menu = featureRail.Descendants(PresentationNamespace + "StackPanel")
            .Single(element => AttributeValue(element, XamlNamespace + "Name") == "FeatureMenu");

        Assert.Equal("Top", AttributeValue(menu, "VerticalAlignment"));
        Assert.Equal("0,24,0,0", AttributeValue(menu, "Margin"));

        var theme = XDocument.Load(SuperPanThemePath());
        var navStyle = theme.Descendants(PresentationNamespace + "Style")
            .Single(style => AttributeValue(style, XamlNamespace + "Key") == "NavItemStyle");
        var setters = navStyle.Elements(PresentationNamespace + "Setter")
            .ToDictionary(setter => AttributeValue(setter, "Property")!, setter => AttributeValue(setter, "Value"));
        Assert.Equal("Center", setters["HorizontalContentAlignment"]);
        Assert.Equal("Stretch", setters["HorizontalAlignment"]);
    }

    [Fact]
    public async Task SelectingAnAccountRefreshesOnceAndCoalescesAnInflightRepeat()
    {
        var row = AccountRow("账号甲");
        var accounts = new ObservableCollection<AccountAllocationRowViewModel> { row };
        var calls = 0;
        var first = new TaskCompletionSource<AccountCapacityRefreshResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = CreateViewModel(
            new FakeLoginLauncher(),
            accounts: accounts,
            refreshAccountCapacity: (accountId, _) =>
            {
                calls++;
                return calls == 1
                    ? first.Task
                    : Task.FromResult(Updated(accountId, 200, 50));
            });

        row.IsSelected = true;
        row.IsSelected = false;
        row.IsSelected = true;
        Assert.Equal(1, calls);

        first.SetResult(Updated(row.AccountId, 100, 25));
        await WaitUntilAsync(() => row.CapacityTotal == 100L * 1024 * 1024 * 1024);
        row.IsSelected = false;
        row.IsSelected = true;
        await WaitUntilAsync(() => calls == 2 && row.CapacityTotal == 200L * 1024 * 1024 * 1024);

        Assert.Equal("已用 50 GB / 200 GB", row.CapacitySummaryText);
        vm.Close();
    }

    [Fact]
    public async Task ValidPreferredShareAnalyticsAccountWinsAndInitialActivationRunsOnce()
    {
        var first = AccountRow("甲");
        var second = AccountRow("乙");
        first.IsSelected = true;
        var accounts = new ObservableCollection<AccountAllocationRowViewModel> { first, second };
        var selections = new List<(Guid? Id, string? Name)>();
        var saves = new List<Guid?>();
        var activations = 0;
        var shareData = new object();
        var vm = new MainWindowViewModel(
            new object(), shareData, new object(), new object(), new object(), new object(), new object(),
            new FakeLoginLauncher(), accounts: accounts,
            selectShareAnalyticsAccount: (id, name) => selections.Add((id, name)),
            initialShareAnalyticsAccountId: second.AccountId,
            saveShareAnalyticsAccount: (id, _) => { saves.Add(id); return Task.CompletedTask; },
            enterShareData: _ => { activations++; return Task.CompletedTask; });

        Assert.Equal((second.AccountId, second.DisplayName), selections[^1]);
        Assert.True(second.IsSelected);
        Assert.Empty(saves);
        await vm.ActivateInitialWorkspaceAsync();
        await vm.ActivateInitialWorkspaceAsync();
        Assert.Equal(1, activations);
        vm.Close();
    }

    [Fact]
    public void MissingPreferenceWithOneAccountSelectsItWithoutSavingDuringRestore()
    {
        var account = AccountRow("唯一账号");
        var selections = new List<Guid?>();
        var saves = new List<Guid?>();

        var vm = new MainWindowViewModel(
            new object(), new object(), new object(), new object(), new object(), new object(), new object(),
            new FakeLoginLauncher(),
            accounts: new ObservableCollection<AccountAllocationRowViewModel> { account },
            selectShareAnalyticsAccount: (id, _) => selections.Add(id),
            saveShareAnalyticsAccount: (id, _) => { saves.Add(id); return Task.CompletedTask; });

        Assert.True(account.IsSelected);
        Assert.Equal(account.AccountId, selections[^1]);
        Assert.Empty(saves);
        vm.Close();
    }

    [Fact]
    public void MissingPreferenceWithMultipleAccountsRemainsUnselected()
    {
        var first = AccountRow("甲");
        var second = AccountRow("乙");
        first.IsSelected = true;
        var selections = new List<Guid?>();

        var vm = new MainWindowViewModel(
            new object(), new object(), new object(), new object(), new object(), new object(), new object(),
            new FakeLoginLauncher(),
            accounts: new ObservableCollection<AccountAllocationRowViewModel> { first, second },
            selectShareAnalyticsAccount: (id, _) => selections.Add(id));

        Assert.Null(selections[^1]);
        vm.Close();
    }

    [Fact]
    public async Task LaterAccountSelectionSavesTheSelectedAccountExactlyOnce()
    {
        var first = AccountRow("甲");
        var second = AccountRow("乙");
        var selections = new List<Guid?>();
        var saves = new List<Guid?>();
        var vm = new MainWindowViewModel(
            new object(), new object(), new object(), new object(), new object(), new object(), new object(),
            new FakeLoginLauncher(),
            accounts: new ObservableCollection<AccountAllocationRowViewModel> { first, second },
            selectShareAnalyticsAccount: (id, _) => selections.Add(id),
            saveShareAnalyticsAccount: (id, _) => { saves.Add(id); return Task.CompletedTask; });

        second.IsSelected = true;
        Assert.True(await vm.FlushShareAnalyticsPreferenceAsync(TimeSpan.FromSeconds(1)));

        Assert.Equal(second.AccountId, selections[^1]);
        Assert.Equal([second.AccountId], saves);
        vm.Close();
    }

    [Fact]
    public async Task RemovingCurrentAccountReappliesFallbackAndPersistsTheReplacement()
    {
        var first = AccountRow("甲");
        var second = AccountRow("乙");
        var accounts = new ObservableCollection<AccountAllocationRowViewModel> { first, second };
        var selections = new List<Guid?>();
        var saves = new List<Guid?>();
        var vm = new MainWindowViewModel(
            new object(), new object(), new object(), new object(), new object(), new object(), new object(),
            new FakeLoginLauncher(),
            accounts: accounts,
            selectShareAnalyticsAccount: (id, _) => selections.Add(id),
            initialShareAnalyticsAccountId: first.AccountId,
            saveShareAnalyticsAccount: (id, _) => { saves.Add(id); return Task.CompletedTask; });

        accounts.Remove(first);
        Assert.True(await vm.FlushShareAnalyticsPreferenceAsync(TimeSpan.FromSeconds(1)));

        Assert.True(second.IsSelected);
        Assert.Equal(second.AccountId, selections[^1]);
        Assert.Equal([second.AccountId], saves);
        vm.Close();
    }

    [Fact]
    public async Task RemovingCurrentAccountWithMultipleRemainingAccountsClearsPreference()
    {
        var first = AccountRow("甲");
        var second = AccountRow("乙");
        var third = AccountRow("丙");
        var accounts = new ObservableCollection<AccountAllocationRowViewModel> { first, second, third };
        var selections = new List<Guid?>();
        var saves = new List<Guid?>();
        var vm = new MainWindowViewModel(
            new object(), new object(), new object(), new object(), new object(), new object(), new object(),
            new FakeLoginLauncher(),
            accounts: accounts,
            selectShareAnalyticsAccount: (id, _) => selections.Add(id),
            initialShareAnalyticsAccountId: first.AccountId,
            saveShareAnalyticsAccount: (id, _) => { saves.Add(id); return Task.CompletedTask; });

        accounts.Remove(first);
        Assert.True(await vm.FlushShareAnalyticsPreferenceAsync(TimeSpan.FromSeconds(1)));

        Assert.Null(selections[^1]);
        Assert.Equal([null], saves);
        vm.Close();
    }

    [Fact]
    public async Task PreferencePersistenceFailureUsesSafeStatusAndDoesNotEscapeAccountSelection()
    {
        var first = AccountRow("甲");
        var second = AccountRow("乙");
        var vm = new MainWindowViewModel(
            new object(), new object(), new object(), new object(), new object(), new object(), new object(),
            new FakeLoginLauncher(),
            accounts: new ObservableCollection<AccountAllocationRowViewModel> { first, second },
            saveShareAnalyticsAccount: (_, _) => throw new InvalidOperationException("secret"));

        var exception = Record.Exception(() => second.IsSelected = true);

        Assert.Null(exception);
        await WaitUntilAsync(() => vm.StatusMessage == "账号查看偏好未能保存。");
        Assert.DoesNotContain("secret", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        vm.Close();
    }

    [Fact]
    public void PreferenceLoadFailureUsesSafeStatusWithoutExceptionDetails()
    {
        var vm = new MainWindowViewModel(
            new object(), new object(), new object(), new object(), new object(), new object(), new object(),
            new FakeLoginLauncher(),
            shareAnalyticsPreferenceLoadFailed: true);

        Assert.Equal("账号查看偏好未能恢复。", vm.StatusMessage);
        vm.Close();
    }

    [Fact]
    public async Task CloseDoesNotCancelInFlightPreferencePersistence()
    {
        var first = AccountRow("甲");
        var second = AccountRow("乙");
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<Guid?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = new MainWindowViewModel(
            new object(), new object(), new object(), new object(), new object(), new object(), new object(),
            new FakeLoginLauncher(),
            accounts: new ObservableCollection<AccountAllocationRowViewModel> { first, second },
            saveShareAnalyticsAccount: async (id, token) =>
            {
                started.TrySetResult(token);
                await release.Task.WaitAsync(token);
                completed.TrySetResult(id);
            });

        second.IsSelected = true;
        var persistenceToken = await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        vm.Close();

        Assert.False(persistenceToken.IsCancellationRequested);
        release.TrySetResult();
        Assert.Equal(second.AccountId, await completed.Task.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task PreferenceFlushWaitsForLatestSelectionBeforeClose()
    {
        var first = AccountRow("甲");
        var second = AccountRow("乙");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = new MainWindowViewModel(
            new object(), new object(), new object(), new object(), new object(), new object(), new object(),
            new FakeLoginLauncher(),
            accounts: new ObservableCollection<AccountAllocationRowViewModel> { first, second },
            saveShareAnalyticsAccount: async (_, token) =>
            {
                started.TrySetResult();
                await release.Task.WaitAsync(token);
            });
        second.IsSelected = true;
        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var flush = vm.FlushShareAnalyticsPreferenceAsync(TimeSpan.FromSeconds(1));
        Assert.False(flush.IsCompleted);
        release.TrySetResult();

        Assert.True(await flush);
        vm.Close();
    }

    [Fact]
    public async Task SuccessfulLoginRefreshesANewlyAddedAccountOnce()
    {
        var accounts = new ObservableCollection<AccountAllocationRowViewModel>();
        var added = AccountRow("新账号");
        var capacityCalls = 0;
        var vm = CreateViewModel(
            new FakeLoginLauncher { IsAvailableValue = true },
            refreshAccounts: (applyCount, _) =>
            {
                accounts.Add(added);
                applyCount(1);
                return Task.CompletedTask;
            },
            accounts: accounts,
            refreshAccountCapacity: (accountId, _) =>
            {
                capacityCalls++;
                return Task.FromResult(Updated(accountId, 100, 25));
            });

        await vm.OpenAccountLoginAsync();

        Assert.Equal(1, capacityCalls);
        Assert.Equal("已用 25 GB / 100 GB", added.CapacitySummaryText);
        vm.Close();
    }

    [Fact]
    public void ShellRailsMatchTheApprovedAccountAndFeatureNavigationStructure()
    {
        var xamlPath = MainWindowXamlPath();
        var xaml = File.ReadAllText(xamlPath);
        var document = XDocument.Load(xamlPath);

        var shellBody = FindNamedElement(document, "ShellBody");
        Assert.Equal("12", AttributeValue(shellBody, "Margin"));

        var accountRail = FindNamedElement(document, "AccountRail");
        Assert.Equal("{StaticResource SurfaceBrush}", AttributeValue(accountRail, "Background"));
        Assert.Equal("{StaticResource PanelBorderBrush}", AttributeValue(accountRail, "BorderBrush"));
        var accountCopy = accountRail.Descendants(PresentationNamespace + "TextBlock")
            .Select(textBlock => AttributeValue(textBlock, "Text"))
            .ToArray();
        Assert.Contains("账号管理", accountCopy);
        Assert.Contains("百度网盘账号", accountCopy);

        var scrollViewer = Assert.Single(accountRail.Descendants(PresentationNamespace + "ScrollViewer"));
        var itemsControl = Assert.Single(scrollViewer.Descendants(PresentationNamespace + "ItemsControl"));
        var accountCard = Assert.Single(
            itemsControl.Descendants(PresentationNamespace + "ToggleButton"),
            element => AttributeValue(element, XamlNamespace + "Name") == "AccountCard");
        Assert.Equal("True", AttributeValue(accountCard, "Focusable"));
        Assert.Equal("{Binding IsSelected, Mode=TwoWay}", AttributeValue(accountCard, "IsChecked"));
        Assert.Equal("{Binding DisplayName, StringFormat=选择账号：{0}}", AttributeValue(accountCard, "AutomationProperties.Name"));
        Assert.Equal("按空格键切换账号选择状态", AttributeValue(accountCard, "AutomationProperties.HelpText"));
        Assert.DoesNotContain(
            accountCard.Descendants(PresentationNamespace + "DataTrigger"),
            trigger => AttributeValue(trigger, "Binding")?.Contains("StatusText", StringComparison.Ordinal) == true);
        Assert.Contains(
            accountCard.Descendants(PresentationNamespace + "Trigger"),
            trigger => AttributeValue(trigger, "Property") == "IsChecked" && AttributeValue(trigger, "Value") == "True");
        var availabilityPill = FindNamedDescendant(accountCard, "AccountStatusPill");
        Assert.Contains(
            availabilityPill.Descendants(PresentationNamespace + "TextBlock"),
            textBlock => AttributeValue(textBlock, "Text") == "{Binding StatusText}");

        var featureRail = FindNamedElement(document, "FeatureRail");
        Assert.Equal("{StaticResource SurfaceBrush}", AttributeValue(featureRail, "Background"));
        Assert.Equal("{StaticResource PanelBorderBrush}", AttributeValue(featureRail, "BorderBrush"));
        var featureCopy = featureRail.Descendants(PresentationNamespace + "TextBlock")
            .Select(textBlock => AttributeValue(textBlock, "Text"))
            .ToArray();
        Assert.Contains("功能导航", featureCopy);
        Assert.Contains("选择一个工作模块", featureCopy);
        var featureButtons = featureRail.Descendants(PresentationNamespace + "Button").ToArray();
        Assert.Equal(
            ["首页", "分享数据", "批量转存", "广告插入", "文件排除", "数据统计", "设置中心"],
            featureButtons.Select(button => AttributeValue(button, "Content")));
        Assert.All(featureButtons, button => Assert.Contains(
            button.Descendants(PresentationNamespace + "DataTrigger"),
            trigger => AttributeValue(trigger, "Binding") == "{Binding CurrentWorkspaceTitle}"));

        var moduleBadge = FindNamedElement(document, "CurrentWorkspaceBadge");
        var moduleBadgeText = Assert.Single(moduleBadge.Descendants(PresentationNamespace + "TextBlock"));
        Assert.Equal(
            "{Binding CurrentWorkspaceTitle, StringFormat=当前模块：{0}}",
            AttributeValue(moduleBadgeText, "Text"));
        Assert.DoesNotContain("#102A43", xaml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AccountCardsTrackBatchTransferConfigurationLockAcrossAllNavigation()
    {
        var document = LoadMainWindowDocument();
        var accountCard = FindNamedElement(document, "AccountCard");
        var style = Assert.Single(accountCard.Elements(PresentationNamespace + "ToggleButton.Style"))
            .Element(PresentationNamespace + "Style");
        Assert.NotNull(style);
        Assert.Contains(style!.Elements(PresentationNamespace + "Setter"), setter =>
            AttributeValue(setter, "Property") == "IsEnabled" &&
            AttributeValue(setter, "Value") == "True");

        Assert.Empty(style.Descendants(PresentationNamespace + "MultiDataTrigger"));
        var trigger = Assert.Single(style.Descendants(PresentationNamespace + "DataTrigger"), trigger =>
            AttributeValue(trigger, "Binding") ==
            "{Binding DataContext.BatchTransferWorkspace.DataContext.CanEditConfiguration, RelativeSource={RelativeSource FindAncestor, AncestorType={x:Type Window}}}" &&
            AttributeValue(trigger, "Value") == "False");
        Assert.Contains(trigger.Elements(PresentationNamespace + "Setter"), setter =>
            AttributeValue(setter, "Property") == "IsEnabled" &&
            AttributeValue(setter, "Value") == "False");
        Assert.DoesNotContain(style.Descendants(PresentationNamespace + "DataTrigger"), trigger =>
            AttributeValue(trigger, "Binding")?.Contains("CurrentWorkspaceTitle", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void ThemeUsesExplicitFlatRoundedTemplatesForCoreInputs()
    {
        var path = Path.Combine(
            FindRepositoryRoot(), "src", "BaiduBatchTransfer.App", "Styles", "SuperPanTheme.xaml");
        var document = XDocument.Load(path);

        foreach (var controlType in new[] { "Button", "ComboBox", "TextBox" })
        {
            var style = Assert.Single(document.Root!.Elements(PresentationNamespace + "Style"), element =>
                AttributeValue(element, "TargetType") == controlType &&
                AttributeValue(element, XamlNamespace + "Key") is null);
            var templateSetter = Assert.Single(style.Elements(PresentationNamespace + "Setter"), element =>
                AttributeValue(element, "Property") == "Template");
            var template = Assert.Single(templateSetter.Descendants(PresentationNamespace + "ControlTemplate"));
            Assert.Equal(controlType, AttributeValue(template, "TargetType"));
            Assert.Contains(template.Descendants(PresentationNamespace + "Border"), border =>
                AttributeValue(border, "CornerRadius") is "4" or "5" or "6");
            Assert.DoesNotContain(template.Descendants(), element =>
                element.Name.LocalName.Contains("Gradient", StringComparison.OrdinalIgnoreCase));
        }

        var buttonStyle = Assert.Single(document.Root!.Elements(PresentationNamespace + "Style"), element =>
            AttributeValue(element, "TargetType") == "Button" &&
            AttributeValue(element, XamlNamespace + "Key") is null);
        Assert.Contains(buttonStyle.Elements(PresentationNamespace + "Setter"), setter =>
            AttributeValue(setter, "Property") == "Height" && AttributeValue(setter, "Value") == "34");
    }

    [Fact]
    public void ShellDoesNotExposeTechnicalAdapterAcceptanceOrBrowserCopy()
    {
        var xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "BaiduBatchTransfer.App",
            "MainWindow.xaml"));

        Assert.DoesNotContain("AdapterStatusMessage", xaml);
        Assert.DoesNotContain("验收", xaml);
        Assert.DoesNotContain("浏览器", xaml);
    }

    [Fact]
    public async Task ManagedReloginRefreshDoesNotPretendTheAccountWasRemoved()
    {
        var management = new FakeManagementLauncher { Result = new AccountManagementRefreshResult(true, null) };
        var refreshes = 0;
        var vm = CreateViewModel(new FakeLoginLauncher(), management,
            (apply, _) => { refreshes++; apply(2); return Task.CompletedTask; });
        await vm.OpenAccountManagementAsync();
        Assert.Equal(1, refreshes);
        Assert.Equal("账号身份已核验，账号列表已刷新。", vm.StatusMessage);
    }
    [Fact]
    public void ShellDefaultsToLinkMonitorAndRoutesAllSevenWorkspaces()
    {
        var login = new FakeLoginLauncher();
        var home = new object();
        var linkMonitor = new object();
        var transfer = new object();
        var adInsertion = new object();
        var fileExclusion = new object();
        var statistics = new object();
        var settings = new object();
        var vm = new MainWindowViewModel(
            home, linkMonitor, transfer, adInsertion, fileExclusion, statistics, settings, login);

        Assert.Equal("分享数据", vm.CurrentWorkspaceTitle);
        Assert.Same(linkMonitor, vm.CurrentContent);
        Assert.Same(transfer, vm.BatchTransferWorkspace);
        Assert.Equal("百度批量转存", vm.ApplicationTitle);
        Assert.False(vm.OpenAccountLoginCommand.CanExecute(null));
        Assert.Contains("尚不可用", vm.AdapterStatusMessage);
        Assert.False(vm.OpenAccountManagementCommand.CanExecute(null));
        Assert.Contains("尚不可用", vm.AccountManagementStatusMessage);

        Assert.Collection(
            new (IRelayCommand Command, string Title, object Workspace)[]
            {
                (vm.ShowHomeCommand, "首页", home),
                (vm.ShowShareDataCommand, "分享数据", linkMonitor),
                (vm.ShowBatchTransferCommand, "批量转存", transfer),
                (vm.ShowAdInsertionCommand, "广告插入", adInsertion),
                (vm.ShowFileExclusionCommand, "文件排除", fileExclusion),
                (vm.ShowStatisticsCommand, "数据统计", statistics),
                (vm.ShowSettingsCommand, "设置中心", settings),
            },
            route => AssertRoute(vm, route),
            route => AssertRoute(vm, route),
            route => AssertRoute(vm, route),
            route => AssertRoute(vm, route),
            route => AssertRoute(vm, route),
            route => AssertRoute(vm, route),
            route => AssertRoute(vm, route));
        Assert.Same(transfer, vm.BatchTransferWorkspace);
    }

    [Fact]
    public void AcceptanceBuildAddsAnExplicitProductTitleMarker()
    {
        Assert.Equal("百度批量转存", MainWindowViewModel.FormatApplicationTitle(false));
        Assert.Equal(
            "百度批量转存 · OTA 本地验收版",
            MainWindowViewModel.FormatApplicationTitle(true));
    }

    [Fact]
    public void LicenseSummaryLoadsBeforeShellCreationRefreshesLiveAndUnsubscribesOnClose()
    {
        var state = new LicenseRuntimeAuthorizationState();
        var initialExpiry = new DateTimeOffset(2026, 10, 2, 8, 30, 0, TimeSpan.FromHours(8));
        state.PublishSummary(new AuthorizedLicenseSummary("基础版", initialExpiry));
        var vm = CreateViewModel(new FakeLoginLauncher(), licenseAuthorizationState: state);

        Assert.True(vm.IsLicenseSummaryVisible);
        Assert.Equal("基础版", vm.LicensePackageDisplay);
        Assert.Equal(initialExpiry.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), vm.LicenseExpiryDisplay);

        var refreshedExpiry = initialExpiry.AddMonths(3);
        state.PublishSummary(new AuthorizedLicenseSummary("团队版", refreshedExpiry));

        Assert.Equal("团队版", vm.LicensePackageDisplay);
        Assert.Equal(refreshedExpiry.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), vm.LicenseExpiryDisplay);

        state.PublishSummary(null);
        Assert.False(vm.IsLicenseSummaryVisible);
        Assert.Equal("未激活", vm.LicensePackageDisplay);
        Assert.Equal("未激活", vm.LicenseExpiryDisplay);

        vm.Close();
        state.PublishSummary(new AuthorizedLicenseSummary("关闭后套餐", refreshedExpiry));
        Assert.Equal("未激活", vm.LicensePackageDisplay);
    }

    [Fact]
    public void ProductHeaderShowsOnlyTheSafeLicensePackageAndExpirySummary()
    {
        var document = LoadMainWindowDocument();
        var panel = FindNamedElement(document, "LicenseSummaryPanel");
        var panelMarkup = panel.ToString();
        var bindings = panel.Descendants(PresentationNamespace + "TextBlock")
            .Select(element => AttributeValue(element, "Text"))
            .ToArray();

        Assert.Contains("{Binding LicensePackageDisplay, StringFormat=授权：{0}}", bindings);
        Assert.Contains("{Binding LicenseExpiryDisplay, StringFormat=到期：{0}}", bindings);
        Assert.DoesNotContain("Card", panelMarkup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MachineHash", panelMarkup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SignedPayload", panelMarkup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AvailableUpdateChangesShellStatusOnlyOnce()
    {
        var viewModel = CreateViewModel(new FakeLoginLauncher());

        viewModel.ReportUpdateAvailable("2.0.0");
        viewModel.ReportUpdateAvailable("3.0.0");

        Assert.Equal("发现新版本 2.0.0，可到设置中心更新。", viewModel.StatusMessage);
    }

    [Fact]
    public void ShellDoesNotExposeLegacyNavigationCommands()
    {
        var navigationProperties = typeof(MainWindowViewModel)
            .GetProperties()
            .Where(property => property.Name.StartsWith("Show", StringComparison.Ordinal))
            .Select(property => property.Name)
            .OrderBy(name => name)
            .ToArray();

        Assert.Equal(
            [
                "ShowAdInsertionCommand",
                "ShowBatchTransferCommand",
                "ShowFileExclusionCommand",
                "ShowHomeCommand",
                "ShowSettingsCommand",
                "ShowShareDataCommand",
                "ShowStatisticsCommand",
            ],
            navigationProperties);
        Assert.Null(typeof(MainWindowViewModel).GetProperty("ShowShareCommand"));
    }

    [Fact]
    public async Task LoginLauncherFailureIsMappedToChineseWithoutLeakingException()
    {
        var login = new FakeLoginLauncher { IsAvailableValue = true, Throw = true };
        var vm = CreateViewModel(login);

        await vm.OpenAccountLoginAsync();

        Assert.Equal("无法打开账号登录，请稍后重试。", vm.StatusMessage);
        Assert.DoesNotContain("InvalidOperationException", vm.StatusMessage);
    }

    [Fact]
    public void UnexpectedHandoffFailureUsesStableSanitizedShellStatus()
    {
        var vm = CreateViewModel(new FakeLoginLauncher());
        var report = typeof(MainWindowViewModel).GetMethod(
            "ReportHandoffFailure",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(report);
        report.Invoke(vm, null);

        Assert.Equal("结果工作区操作失败，请稍后重试。", vm.StatusMessage);
    }

    [Fact]
    public async Task SuccessfulLoginRefreshesVisibleAccountCount()
    {
        var login = new FakeLoginLauncher { IsAvailableValue = true };
        var refreshCalls = 0;
        var vm = CreateViewModel(
            login,
            refreshAccounts: (applyCount, _) =>
            {
                refreshCalls++;
                applyCount(1);
                return Task.CompletedTask;
            },
            initialActiveAccountCount: 0);

        Assert.Equal("尚未登录百度账号", vm.AccountStatusMessage);

        await vm.OpenAccountLoginAsync();

        Assert.Equal("已登录 1 个百度账号", vm.AccountStatusMessage);
        Assert.Equal(1, refreshCalls);
    }

    [Theory]
    [InlineData(AccountExitStatus.Removed)]
    [InlineData(AccountExitStatus.CleanupPending)]
    public async Task CommittedManagementCloseRefreshesCountAndDisablesCommandsWhileBusy(
        AccountExitStatus status)
    {
        var management = new FakeManagementLauncher
        {
            Result = new AccountManagementRefreshResult(true, status),
            Delay = true,
        };
        var refreshCalls = 0;
        var vm = CreateViewModel(
            new FakeLoginLauncher { IsAvailableValue = true },
            management,
            (applyCount, _) =>
            {
                refreshCalls++;
                applyCount(2);
                return Task.CompletedTask;
            },
            initialActiveAccountCount: 3);

        var opening = vm.OpenAccountManagementAsync();
        await management.Started.Task;

        Assert.False(vm.OpenAccountManagementCommand.CanExecute(null));
        Assert.False(vm.OpenAccountLoginCommand.CanExecute(null));
        Assert.Contains("正在", vm.AccountManagementStatusMessage);

        management.Complete();
        await opening;

        Assert.Equal(1, refreshCalls);
        Assert.Equal("已登录 2 个百度账号", vm.AccountStatusMessage);
        Assert.True(vm.OpenAccountManagementCommand.CanExecute(null));
    }

    [Fact]
    public async Task ManagementFailureUsesSafeChineseText()
    {
        var management = new FakeManagementLauncher { Throw = true };
        var vm = CreateViewModel(new FakeLoginLauncher { IsAvailableValue = true }, management);

        await vm.OpenAccountManagementAsync();

        Assert.Equal("无法打开账号管理，请稍后重试。", vm.StatusMessage);
        Assert.DoesNotContain("secret-account-id", vm.StatusMessage);
        Assert.DoesNotContain("InvalidOperationException", vm.StatusMessage);
    }

    [Theory]
    [InlineData(AccountExitStatus.Removed)]
    [InlineData(AccountExitStatus.CleanupPending)]
    public async Task CommittedManagementActionReportsRefreshFailureAndRetriesOnlyTheRefresh(
        AccountExitStatus status)
    {
        var management = new FakeManagementLauncher
        {
            Result = new AccountManagementRefreshResult(true, status),
        };
        var refreshCalls = 0;
        var vm = CreateViewModel(
            new FakeLoginLauncher { IsAvailableValue = true },
            management,
            (applyCount, _) =>
            {
                refreshCalls++;
                if (refreshCalls == 1)
                    throw new InvalidOperationException("secret-account-id C:\\private\\profile token");
                applyCount(4);
                return Task.CompletedTask;
            },
            initialActiveAccountCount: 3);

        await vm.OpenAccountManagementAsync();

        Assert.Equal("账号操作已完成，但列表刷新失败，请重试刷新。", vm.StatusMessage);
        Assert.Equal(1, management.OpenCalls);
        Assert.True(vm.OpenAccountManagementCommand.CanExecute(null));
        Assert.True(vm.RefreshAccountsCommand.CanExecute(null));

        await vm.RefreshAccountsCommand.ExecuteAsync(null);

        Assert.Equal(1, management.OpenCalls);
        Assert.Equal(2, refreshCalls);
        Assert.Equal("已登录 4 个百度账号", vm.AccountStatusMessage);
        Assert.Equal("账号列表已刷新。", vm.StatusMessage);
    }

    [Fact]
    public async Task CompletedLoginReportsRefreshFailureWithoutOpeningLoginAgainOnRetry()
    {
        var login = new FakeLoginLauncher { IsAvailableValue = true };
        var refreshCalls = 0;
        var vm = CreateViewModel(
            login,
            refreshAccounts: (applyCount, _) =>
            {
                refreshCalls++;
                if (refreshCalls == 1)
                    throw new InvalidOperationException("private-profile token");
                applyCount(2);
                return Task.CompletedTask;
            });

        await vm.OpenAccountLoginAsync();

        Assert.Equal("账号登录窗口已关闭，但列表刷新失败，请重试刷新。", vm.StatusMessage);
        Assert.Equal(1, login.OpenCalls);
        Assert.True(vm.RefreshAccountsCommand.CanExecute(null));

        await vm.RefreshAccountsCommand.ExecuteAsync(null);

        Assert.Equal(1, login.OpenCalls);
        Assert.Equal(2, refreshCalls);
        Assert.Equal("已登录 2 个百度账号", vm.AccountStatusMessage);
    }

    [Fact]
    public void HeaderExposesAccountRefreshAndPolitelyAnnouncesAccountState()
    {
        var xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "BaiduBatchTransfer.App",
            "MainWindow.xaml"));

        Assert.Contains("Content=\"刷新账号\" Command=\"{Binding RefreshAccountsCommand}\"", xaml);
        Assert.Contains("Content=\"首页\" Command=\"{Binding ShowHomeCommand}\"", xaml);
        Assert.Contains("Content=\"分享数据\" Command=\"{Binding ShowShareDataCommand}\"", xaml);
        Assert.Contains("Content=\"批量转存\" Command=\"{Binding ShowBatchTransferCommand}\"", xaml);
        Assert.Contains("Content=\"广告插入\" Command=\"{Binding ShowAdInsertionCommand}\"", xaml);
        Assert.Contains("Content=\"文件排除\" Command=\"{Binding ShowFileExclusionCommand}\"", xaml);
        Assert.Contains("Content=\"数据统计\" Command=\"{Binding ShowStatisticsCommand}\"", xaml);
        Assert.Contains("Content=\"设置中心\" Command=\"{Binding ShowSettingsCommand}\"", xaml);
        Assert.DoesNotContain("Command=\"{Binding ShowShareCommand}\"", xaml);
        Assert.DoesNotContain("导入与草稿", xaml);
        Assert.DoesNotContain("创建任务", xaml);
        Assert.DoesNotContain("任务结果", xaml);
        Assert.DoesNotContain("批量预检", xaml);
        Assert.DoesNotContain("ShowImportCommand", xaml);
        Assert.DoesNotContain("ShowCreateJobCommand", xaml);
        Assert.DoesNotContain("ShowResultsCommand", xaml);
        Assert.Contains("x:Name=\"AccountStatusLiveRegion\"", xaml);
        Assert.Contains("AutomationProperties.Name=\"{Binding AccountStatusMessage}\"", xaml);
        Assert.Contains("x:Name=\"OperationStatusLiveRegion\"", xaml);
        Assert.Contains("AutomationProperties.Name=\"{Binding StatusMessage}\"", xaml);
        Assert.Contains("AutomationProperties.Name=\"{Binding AccountManagementStatusMessage}\"", xaml);
        Assert.Equal(2, xaml.Split("AutomationProperties.LiveSetting=\"Polite\"").Length - 1);

        var codeBehind = File.ReadAllText(Path.ChangeExtension(
            Path.Combine(FindRepositoryRoot(), "src", "BaiduBatchTransfer.App", "MainWindow.xaml"),
            ".xaml.cs"));
        Assert.Contains("DataContextChanged += OnDataContextChanged", codeBehind);
        Assert.Contains("Closed += OnClosed", codeBehind);
        Assert.Contains("liveRegionNotifier?.Dispose()", codeBehind);
        Assert.Contains("AutomationEvents.LiveRegionChanged", codeBehind);
    }

    [Fact]
    public void LiveRegionNotifierDispatchesTrackedChangesAndStopsAfterDispose()
    {
        var source = new FakePropertyChanges();
        var dispatched = new List<Action>();
        var announcements = new List<string>();
        var notifier = new PropertyChangeLiveRegionNotifier(
            source,
            new Dictionary<string, Action>
            {
                [nameof(MainWindowViewModel.AccountStatusMessage)] = () => announcements.Add("account"),
                [nameof(MainWindowViewModel.StatusMessage)] = () => announcements.Add("operation"),
            },
            action => dispatched.Add(action));

        source.Raise(nameof(MainWindowViewModel.CurrentWorkspaceTitle));
        Assert.Empty(dispatched);

        source.Raise(nameof(MainWindowViewModel.AccountStatusMessage));
        var accountAnnouncement = Assert.Single(dispatched);
        Assert.Empty(announcements);
        accountAnnouncement();
        Assert.Equal(["account"], announcements);

        source.Raise(nameof(MainWindowViewModel.StatusMessage));
        Assert.Equal(2, dispatched.Count);
        notifier.Dispose();
        dispatched[1]();
        Assert.Equal(["account"], announcements);
        source.Raise(nameof(MainWindowViewModel.StatusMessage));
        Assert.Equal(2, dispatched.Count);
    }

    private static string FindRepositoryRoot([CallerFilePath] string sourceFilePath = "")
    {
        var current = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "BaiduBatchTransfer.sln")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("未找到测试仓库目录。");
    }

    private static readonly XNamespace PresentationNamespace = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string MainWindowXamlPath() => Path.Combine(
        FindRepositoryRoot(),
        "src",
        "BaiduBatchTransfer.App",
        "MainWindow.xaml");

    private static string SuperPanThemePath() => Path.Combine(
        FindRepositoryRoot(),
        "src",
        "BaiduBatchTransfer.App",
        "Styles",
        "SuperPanTheme.xaml");

    private static XDocument LoadMainWindowDocument() => XDocument.Load(MainWindowXamlPath());

    private static XElement FindNamedElement(XDocument document, string elementName) =>
        Assert.Single(document.Descendants(), element =>
            AttributeValue(element, XamlNamespace + "Name") == elementName);

    private static XElement FindNamedDescendant(XElement parent, string elementName) =>
        Assert.Single(parent.Descendants(), element =>
            AttributeValue(element, XamlNamespace + "Name") == elementName);

    private static string? AttributeValue(XElement element, XName attributeName) =>
        element.Attribute(attributeName)?.Value;

    private static string? AttributeValue(XElement element, string attributeName)
    {
        return element.Attributes().SingleOrDefault(attribute =>
            attribute.Name.LocalName == attributeName)?.Value;
    }

    private static MainWindowViewModel CreateViewModel(
        IAccountLoginLauncher loginLauncher,
        IAccountManagementLauncher? accountManagementLauncher = null,
        Func<Action<int>, CancellationToken, Task>? refreshAccounts = null,
        int initialActiveAccountCount = 0,
        ObservableCollection<AccountAllocationRowViewModel>? accounts = null,
        Func<Guid, CancellationToken, Task<AccountCapacityRefreshResult>>? refreshAccountCapacity = null,
        LicenseRuntimeAuthorizationState? licenseAuthorizationState = null)
    {
        var placeholders = Enumerable.Range(0, 7).Select(_ => new object()).ToArray();
        return new MainWindowViewModel(
            placeholders[0], placeholders[1], placeholders[2], placeholders[3],
            placeholders[4], placeholders[5], placeholders[6],
            loginLauncher,
            accountManagementLauncher,
            refreshAccounts,
            initialActiveAccountCount,
            accounts,
            refreshAccountCapacity,
            licenseAuthorizationState: licenseAuthorizationState);
    }

    private static AccountAllocationRowViewModel AccountRow(string name) => new(
        new WizardAccountOption(
            Guid.NewGuid(), name, "Active", true, false, null, null,
            0, 0, 0, 0, 0, 0, 0, 0, 0,
            BatchAccountSelectionDecision.NotSelected),
        "/目标");

    private static AccountCapacityRefreshResult Updated(Guid accountId, long totalGiB, long usedGiB) => new(
        accountId,
        AccountCapacityRefreshDisposition.Updated,
        new AccountCapacity(totalGiB * 1024L * 1024 * 1024, usedGiB * 1024L * 1024 * 1024),
        "账号容量已更新。");

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
            await Task.Delay(5);
        Assert.True(condition());
    }

    private static void AssertRoute(
        MainWindowViewModel viewModel,
        (IRelayCommand Command, string Title, object Workspace) route)
    {
        route.Command.Execute(null);
        Assert.Equal(route.Title, viewModel.CurrentWorkspaceTitle);
        Assert.Same(route.Workspace, viewModel.CurrentContent);
    }

    private sealed class FakeLoginLauncher : IAccountLoginLauncher
    {
        public bool IsAvailableValue { get; init; }
        public bool Throw { get; init; }
        public int OpenCalls { get; private set; }
        public bool IsAvailable => IsAvailableValue;
        public string StatusMessage => IsAvailable ? string.Empty : "百度登录适配器尚不可用。";
        public Task OpenAsync(CancellationToken cancellationToken = default)
        {
            OpenCalls++;
            return Throw ? throw new InvalidOperationException("secret") : Task.CompletedTask;
        }
    }

    private sealed class FakePropertyChanges : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public void Raise(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private sealed class FakeManagementLauncher : IAccountManagementLauncher
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event EventHandler<AccountManagementRefreshEventArgs>? RefreshRequested
        {
            add { }
            remove { }
        }
        public AccountManagementRefreshResult Result { get; init; } = AccountManagementRefreshResult.None;
        public bool Delay { get; init; }
        public bool Throw { get; init; }
        public int OpenCalls { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<AccountManagementRefreshResult> OpenAsync(CancellationToken cancellationToken = default)
        {
            OpenCalls++;
            Started.TrySetResult();
            if (Throw) throw new InvalidOperationException("secret-account-id C:\\private\\profile token");
            if (Delay) await release.Task.WaitAsync(cancellationToken);
            return Result;
        }

        public void Complete() => release.TrySetResult();
    }
}
