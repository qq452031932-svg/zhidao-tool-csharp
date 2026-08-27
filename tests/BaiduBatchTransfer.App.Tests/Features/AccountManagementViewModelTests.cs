using System.Text.RegularExpressions;
using BaiduBatchTransfer.App.Features.Accounts;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Application.Accounts;
using BaiduBatchTransfer.Core.Abstractions;
using System.Windows;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class AccountManagementViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task FirstPageIsBoundedAndRowsExposeOnlySafeChineseDisplayFields()
    {
        var activeId = Guid.NewGuid();
        var pendingId = Guid.NewGuid();
        var operations = new FakeOperations
        {
            Pages =
            {
                [1] = new AccountPage(
                [
                    new(activeId, "账号甲", "Active", true),
                    new(pendingId, "账号乙", "Disabled", false, true),
                    new(Guid.NewGuid(), "账号丙", "Invalid", true),
                ], 1, 100, 203),
            },
        };
        var vm = Create(operations);

        await vm.LoadFirstPageAsync();

        var request = Assert.Single(operations.PageRequests);
        Assert.Equal((1, 100), request);
        Assert.InRange(request.PageSize, 1, 1_000);
        Assert.Equal(3, vm.Accounts.Count);
        Assert.Equal("账号甲", vm.Accounts[0].DisplayName);
        Assert.Equal("已登录", vm.Accounts[0].LoginStatusText);
        Assert.Equal("已启用", vm.Accounts[0].EnabledStateText);
        Assert.Equal("退出账号", vm.Accounts[0].ActionText);
        Assert.Equal("继续清理", vm.Accounts[1].ActionText);
        Assert.Equal("登录已失效", vm.Accounts[2].LoginStatusText);
        Assert.Equal("第 1 页，共 3 页", vm.PageSummary);

        var visible = vm.Accounts.SelectMany(row => new[]
        {
            row.DisplayName,
            row.LoginStatusText,
            row.EnabledStateText,
            row.ActionText,
            row.ToString(),
        }).Append(vm.PageSummary).ToArray();
        Assert.All(visible, AssertSafeVisibleText);
        Assert.DoesNotContain(visible, value => value.Contains(activeId.ToString(), StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(visible, value => value.Contains(pendingId.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PagedRowsSanitizeAdversarialDisplayNamesEverywhereTheyArePresented()
    {
        var operations = new FakeOperations
        {
            Pages =
            {
                [1] = new AccountPage(
                [
                    new(Guid.NewGuid(), "\u202E账号甲", "Active", true),
                    new(Guid.NewGuid(), "C:\\private\\profile", "Active", true),
                    new(Guid.NewGuid(), "https://evil.invalid/account", "Active", true),
                    new(Guid.NewGuid(), "cookie-token-exception", "Active", true),
                    new(Guid.NewGuid(), "账号 11111111-1111-1111-1111-111111111111", "Active", true),
                    new(Guid.NewGuid(), new string('长', 200), "Active", true),
                ], 1, 100, 6),
            },
        };
        var vm = Create(operations);

        await vm.LoadFirstPageAsync();

        Assert.Equal("账号甲", vm.Accounts[0].DisplayName);
        Assert.All(vm.Accounts.Skip(1).Take(4), row => Assert.Equal("已选择的账号", row.DisplayName));
        Assert.Equal(80, vm.Accounts[5].DisplayName.Length);
        foreach (var row in vm.Accounts)
        {
            AssertSafeVisibleText(row.DisplayName);
            AssertSafeVisibleText(row.ToString());
            AssertSafeVisibleText(row.ActionAutomationName);
            Assert.DoesNotContain("\u202E", row.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("http", row.ActionAutomationName, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task PreviewUsesSelectedAccountAndModeAndBuildsAccountScopedChineseConfirmation()
    {
        var accountId = Guid.NewGuid();
        var operations = SingleAccount(accountId, "账号乙");
        operations.Preview = new(
            AccountExitStatus.Ready,
            "账号乙",
            12,
            3,
            2,
            "preview-token-secret",
            "exception profile C:\\private cookie");
        var vm = Create(operations);
        await vm.LoadFirstPageAsync();
        vm.RemoveTerminalHistorySelected = true;

        await vm.BeginAccountActionAsync(vm.Accounts[0]);

        Assert.Equal((accountId, AccountExitHistoryMode.RemoveTerminalHistory), Assert.Single(operations.PreviewRequests));
        Assert.True(vm.IsConfirmationVisible);
        Assert.Equal(12, vm.TerminalOperationCount);
        Assert.Equal(3, vm.TerminalBatchCount);
        Assert.Equal(2, vm.SharedBatchCount);
        Assert.Contains("账号乙", vm.ConfirmationMessage);
        Assert.Contains("其他账号不受影响", vm.ConfirmationMessage);
        Assert.Contains("终态任务 12", vm.ConfirmationMessage);
        Assert.Contains("终态批次 3", vm.ConfirmationMessage);
        Assert.Contains("共享批次 2", vm.ConfirmationMessage);
        Assert.Contains("公共上下文将保留", vm.ConfirmationMessage);
        Assert.DoesNotContain("preview-token-secret", vm.ConfirmationMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("private", vm.ConfirmationMessage, StringComparison.OrdinalIgnoreCase);
        AssertSafeVisibleText(vm.ConfirmationMessage);
    }

    [Fact]
    public async Task PreviewRaceToCleanupPendingUsesStableMessageAndRequestsRefresh()
    {
        var operations = SingleAccount(Guid.NewGuid(), "账号乙");
        operations.Preview = new(
            AccountExitStatus.CleanupPending,
            "账号乙",
            0,
            0,
            0,
            "must-not-be-visible",
            "raw exception profile C:\\private cookie");
        var vm = Create(operations);
        AccountManagementRefreshEventArgs? refresh = null;
        vm.RefreshRequested += (_, args) => refresh = args;
        await vm.LoadFirstPageAsync();

        await vm.BeginAccountActionAsync(vm.Accounts[0]);

        Assert.Equal(AccountManagementUiText.CleanupPending, vm.StatusMessage);
        Assert.Equal(AccountExitStatus.CleanupPending, Assert.IsType<AccountManagementRefreshEventArgs>(refresh).Status);
        Assert.False(vm.IsConfirmationVisible);
        Assert.False(vm.ConfirmRemovalCommand.CanExecute(null));
        AssertSafeVisibleText(vm.StatusMessage);
    }

    [Fact]
    public async Task ModeSwitchInvalidatesPreviewAndOriginalTokenCannotBeConfirmed()
    {
        var operations = SingleAccount(Guid.NewGuid(), "账号甲");
        operations.Preview = Ready("账号甲", "token-a");
        var dialogs = new FakeConfirmation { ConfirmResult = true };
        var vm = Create(operations, dialogs);
        await vm.LoadFirstPageAsync();
        await vm.BeginAccountActionAsync(vm.Accounts[0]);

        vm.RemoveTerminalHistorySelected = true;
        await vm.ConfirmRemovalAsync();

        Assert.True(vm.IsConfirmationVisible);
        Assert.True(vm.NeedsPreviewRefresh);
        Assert.True(vm.ReloadPreviewCommand.CanExecute(null));
        Assert.False(vm.ConfirmRemovalCommand.CanExecute(null));
        Assert.Empty(operations.ApplyRequests);
        Assert.Empty(dialogs.ConfirmMessages);

        operations.Preview = Ready("账号甲", "token-b");
        await vm.ReloadPreviewAsync();
        await vm.ConfirmRemovalAsync();

        var applied = Assert.Single(operations.ApplyRequests);
        Assert.Equal(AccountExitHistoryMode.RemoveTerminalHistory, applied.HistoryMode);
        Assert.Equal("token-b", applied.ConfirmationToken);
    }

    [Fact]
    public async Task PreviewTokenFromAccountACannotBeReusedForAccountB()
    {
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();
        var operations = new FakeOperations
        {
            Pages =
            {
                [1] = new AccountPage(
                    [new(accountA, "账号甲", "Active", true), new(accountB, "账号乙", "Active", true)],
                    1, 100, 2),
            },
        };
        operations.PreviewHandler = (id, _) =>
            Task.FromResult(id == accountA ? Ready("账号甲", "token-a") : Ready("账号乙", "token-b"));
        var vm = Create(operations, new FakeConfirmation { ConfirmResult = true });
        await vm.LoadFirstPageAsync();

        await vm.BeginAccountActionAsync(vm.Accounts[0]);
        await vm.BeginAccountActionAsync(vm.Accounts[1]);
        await vm.ConfirmRemovalAsync();

        var request = Assert.Single(operations.ApplyRequests);
        Assert.Equal(accountB, request.AccountId);
        Assert.Equal(AccountExitHistoryMode.KeepHistory, request.HistoryMode);
        Assert.Equal("token-b", request.ConfirmationToken);
        Assert.DoesNotContain(operations.ApplyRequests, item => item.ConfirmationToken == "token-a");
    }

    [Fact]
    public async Task VisibleRowSelectionChangeInvalidatesThePreviousAccountsPreview()
    {
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();
        var operations = new FakeOperations
        {
            Pages =
            {
                [1] = new AccountPage(
                    [new(accountA, "账号甲", "Active", true), new(accountB, "账号乙", "Active", true)],
                    1, 100, 2),
            },
            Preview = Ready("账号甲", "token-a"),
        };
        var vm = Create(operations, new FakeConfirmation { ConfirmResult = true });
        await vm.LoadFirstPageAsync();
        await vm.BeginAccountActionAsync(vm.Accounts[0]);
        Assert.True(vm.CanConfirm);

        vm.SelectedAccount = vm.Accounts[1];
        await vm.ConfirmRemovalAsync();

        Assert.False(vm.IsConfirmationVisible);
        Assert.False(vm.CanConfirm);
        Assert.Empty(operations.ApplyRequests);
    }

    [Fact]
    public async Task UserCancelNeverAppliesAndBusyStateDisablesConfirmation()
    {
        var operations = SingleAccount(Guid.NewGuid(), "账号甲");
        var previewGate = new TaskCompletionSource<AccountExitPreview>(TaskCreationOptions.RunContinuationsAsynchronously);
        operations.PreviewHandler = (_, _) => previewGate.Task;
        var dialogs = new FakeConfirmation { ConfirmResult = false };
        var vm = Create(operations, dialogs);
        await vm.LoadFirstPageAsync();

        var loading = vm.BeginAccountActionAsync(vm.Accounts[0]);
        Assert.True(vm.IsBusy);
        Assert.False(vm.ConfirmRemovalCommand.CanExecute(null));
        previewGate.SetResult(Ready("账号甲", "token-a"));
        await loading;
        Assert.True(vm.ConfirmRemovalCommand.CanExecute(null));

        await vm.ConfirmRemovalAsync();

        Assert.Empty(operations.ApplyRequests);
        Assert.Single(dialogs.ConfirmMessages);
    }

    [Theory]
    [InlineData(AccountExitStatus.ActiveWorkBlocked, "账号仍有未完成工作，暂时不能退出。")]
    [InlineData(AccountExitStatus.StaleConfirmation, "确认信息已过期，请重新预览后再试。")]
    [InlineData(AccountExitStatus.Failed, "账号操作失败，请稍后重试。")]
    [InlineData(AccountExitStatus.NotFound, "账号不存在或已被移除。")]
    [InlineData(AccountExitStatus.AlreadyRemoved, "账号已经退出。")]
    public async Task PreviewOutcomesMapToStableChineseWithoutRawMessages(
        AccountExitStatus status,
        string expected)
    {
        var operations = SingleAccount(Guid.NewGuid(), "账号甲");
        operations.Preview = new(status, "账号甲", 0, 0, 0, "secret-token", "exception cookie profile C:\\secret");
        var vm = Create(operations);
        await vm.LoadFirstPageAsync();

        await vm.BeginAccountActionAsync(vm.Accounts[0]);

        Assert.Equal(expected, vm.StatusMessage);
        Assert.False(vm.IsConfirmationVisible);
        AssertSafeVisibleText(vm.StatusMessage);
    }

    [Fact]
    public async Task CleanupPendingApplyRaisesRefreshAndUsesOriginalPreviewRequest()
    {
        var accountId = Guid.NewGuid();
        var operations = SingleAccount(accountId, "账号乙");
        operations.Preview = Ready("账号乙", "original-token");
        operations.ApplyResult = new(
            AccountExitStatus.CleanupPending, "", 0, 0, 0, "leaked-token", "exception text");
        var vm = Create(operations, new FakeConfirmation { ConfirmResult = true });
        AccountManagementRefreshEventArgs? refresh = null;
        vm.RefreshRequested += (_, args) => refresh = args;
        await vm.LoadFirstPageAsync();
        await vm.BeginAccountActionAsync(vm.Accounts[0]);

        await vm.ConfirmRemovalAsync();

        var request = Assert.Single(operations.ApplyRequests);
        Assert.Equal(accountId, request.AccountId);
        Assert.Equal(AccountExitHistoryMode.KeepHistory, request.HistoryMode);
        Assert.Equal("original-token", request.ConfirmationToken);
        Assert.Equal(Now, request.RemovedAt);
        Assert.Equal(AccountExitStatus.CleanupPending, Assert.IsType<AccountManagementRefreshEventArgs>(refresh).Status);
        Assert.Equal("账号已退出，仍需重试清理本机登录数据。", vm.StatusMessage);
        AssertSafeVisibleText(vm.StatusMessage);
    }

    [Fact]
    public async Task DisabledRowRetriesCleanupWithoutPreviewOrConfirmationToken()
    {
        var accountId = Guid.NewGuid();
        var operations = SingleAccount(accountId, "账号乙", "Disabled", false, true);
        operations.ResumeResult = new(AccountExitStatus.Removed, "", 0, 0, 0, "secret", "exception");
        var vm = Create(operations);
        AccountManagementRefreshEventArgs? refresh = null;
        vm.RefreshRequested += (_, args) => refresh = args;
        await vm.LoadFirstPageAsync();

        await vm.BeginAccountActionAsync(vm.Accounts[0]);

        Assert.Empty(operations.PreviewRequests);
        Assert.Empty(operations.ApplyRequests);
        Assert.Equal((accountId, Now), Assert.Single(operations.ResumeRequests));
        Assert.Equal("账号已退出并完成清理。", vm.StatusMessage);
        Assert.Equal(AccountExitStatus.Removed, Assert.IsType<AccountManagementRefreshEventArgs>(refresh).Status);
    }

    [Fact]
    public async Task ManagedProfileCleanupRequiresExactOwnedConfirmationBeforeDeletion()
    {
        var accountId = Guid.NewGuid();
        var manifestId = Guid.NewGuid();
        var token = new ManagedBrowserProfileDeletionConfirmation(manifestId, 2, "exact-token");
        var operations = SingleAccount(accountId, "账号乙", "Disabled", false, true);
        operations.ManagedPreparation = new(
            ManagedBrowserProfileCleanupStatus.Quarantined,
            token);
        operations.ManagedConfirmResult = new(
            AccountExitStatus.Removed, "", 0, 0, 0, "", "");
        var dialogs = new FakeConfirmation { ConfirmResult = false };
        var vm = Create(operations, dialogs);
        await vm.LoadFirstPageAsync();

        await vm.BeginAccountActionAsync(vm.Accounts[0]);

        Assert.Empty(operations.ManagedConfirmRequests);
        Assert.Empty(operations.ResumeRequests);
        Assert.Contains("账号乙", Assert.Single(dialogs.ConfirmMessages));
        Assert.Contains("不可恢复", dialogs.ConfirmMessages[0]);

        dialogs.ConfirmResult = true;
        await vm.BeginAccountActionAsync(vm.Accounts[0]);

        var request = Assert.Single(operations.ManagedConfirmRequests);
        Assert.Equal(accountId, request.AccountId);
        Assert.Same(token, request.Confirmation);
        Assert.Equal(AccountManagementUiText.Removed, vm.StatusMessage);
    }

    [Fact]
    public async Task FailedCleanupRetryStaysListedAndShowsSafeChineseStatus()
    {
        var operations = SingleAccount(Guid.NewGuid(), "账号乙", "Disabled", false, true);
        operations.ResumeResult = new(AccountExitStatus.Failed, "", 0, 0, 0, "token", "InvalidOperationException C:\\profile");
        var vm = Create(operations);
        var refreshCount = 0;
        vm.RefreshRequested += (_, _) => refreshCount++;
        await vm.LoadFirstPageAsync();

        await vm.BeginAccountActionAsync(vm.Accounts[0]);

        Assert.Equal("清理未完成，请稍后重试。", vm.StatusMessage);
        Assert.Equal(0, refreshCount);
        Assert.Single(vm.Accounts);
        AssertSafeVisibleText(vm.StatusMessage);
    }

    [Fact]
    public async Task GenericDisabledRowDoesNotExposeCleanupRetry()
    {
        var operations = SingleAccount(Guid.NewGuid(), "普通停用账号", "Disabled", false, false);
        var vm = Create(operations);

        await vm.LoadFirstPageAsync();

        var row = Assert.Single(vm.Accounts);
        Assert.Equal("不可操作", row.ActionText);
        Assert.False(row.ActionCommand.CanExecute(null));
        Assert.Empty(operations.ResumeRequests);
    }

    [Fact]
    public async Task AttachedLifetimeCancelsDelayedPreviewAndDisablesCommands()
    {
        var operations = SingleAccount(Guid.NewGuid(), "账号甲");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = false;
        operations.PreviewHandlerWithToken = async (_, _, token) =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                cancellationObserved = true;
                throw;
            }
            return Ready("账号甲", "unused");
        };
        using var lifetime = new CancellationTokenSource();
        var vm = Create(operations);
        vm.AttachLifetime(lifetime.Token);
        await vm.LoadFirstPageAsync();

        var preview = vm.BeginAccountActionAsync(vm.Accounts[0]);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        lifetime.Cancel();
        await preview;

        Assert.True(cancellationObserved);
        Assert.True(vm.IsLifetimeEnded);
        Assert.False(vm.IsConfirmationVisible);
        Assert.False(vm.LoadFirstPageCommand.CanExecute(null));
        Assert.False(vm.ConfirmRemovalCommand.CanExecute(null));
        Assert.False(vm.Accounts[0].ActionCommand.CanExecute(null));
    }

    [Fact]
    public async Task LifetimeCancellationAfterCommittedPrepareStillRaisesCleanupPendingRefresh()
    {
        var operations = SingleAccount(Guid.NewGuid(), "账号甲");
        using var lifetime = new CancellationTokenSource();
        operations.ApplyHandlerWithToken = (_, _) =>
        {
            lifetime.Cancel();
            return Task.FromResult(new AccountExitPreview(
                AccountExitStatus.CleanupPending, "账号甲", 0, 0, 0, "", "raw exception"));
        };
        var confirmation = new FakeConfirmation { ConfirmResult = true };
        var vm = Create(operations, confirmation);
        vm.AttachLifetime(lifetime.Token);
        AccountManagementRefreshEventArgs? refresh = null;
        vm.RefreshRequested += (_, args) => refresh = args;
        await vm.LoadFirstPageAsync();
        await vm.BeginAccountActionAsync(vm.Accounts[0]);

        await vm.ConfirmRemovalAsync();

        Assert.True(vm.IsLifetimeEnded);
        Assert.Equal(AccountExitStatus.CleanupPending, Assert.IsType<AccountManagementRefreshEventArgs>(refresh).Status);
        Assert.Equal(AccountManagementUiText.CleanupPending, vm.StatusMessage);
        Assert.False(vm.IsConfirmationVisible);
    }

    [Fact]
    public void OwnedConfirmationAlwaysSuppliesAttachedWindowAndHonorsPreCancellation()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var prompt = new FakeOwnedPrompt { Result = true };
                var confirmation = new OwnedAccountManagementConfirmation(prompt);
                var owner = new Window();
                confirmation.AttachOwner(owner);

                Assert.True(confirmation.ConfirmAsync("确认退出账号", "确认内容", default).GetAwaiter().GetResult());
                Assert.Same(owner, prompt.Owner);
                Assert.Equal("确认退出账号", prompt.Title);

                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                Assert.Throws<OperationCanceledException>(() =>
                    confirmation.ConfirmAsync("确认退出账号", "不应显示", cancelled.Token).GetAwaiter().GetResult());
                Assert.Equal(1, prompt.Calls);
                owner.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "STA owner test timed out.");
        Assert.Null(failure);
    }

    [Fact]
    public async Task ConfirmationUsesFreshPreviewDisplayNameAfterConcurrentRename()
    {
        var operations = SingleAccount(Guid.NewGuid(), "旧名称");
        operations.Preview = Ready("新名称", "fresh-token");
        var vm = Create(operations);
        await vm.LoadFirstPageAsync();

        await vm.BeginAccountActionAsync(vm.Accounts[0]);

        Assert.Contains("新名称", vm.ConfirmationMessage);
        Assert.DoesNotContain("旧名称", vm.ConfirmationMessage);
    }

    [Fact]
    public async Task ConfirmationSanitizesControlBidiUrlPathAndOversizedPreviewName()
    {
        var operations = SingleAccount(Guid.NewGuid(), "旧名称");
        var adversarial = "新\u202E名称\r\nC:\\private\\profile https://evil.invalid/" + new string('长', 200);
        operations.Preview = Ready(adversarial, "fresh-token");
        var vm = Create(operations);
        await vm.LoadFirstPageAsync();

        await vm.BeginAccountActionAsync(vm.Accounts[0]);

        Assert.DoesNotContain("\u202E", vm.ConfirmationMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", vm.ConfirmationMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", vm.ConfirmationMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\", vm.ConfirmationMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", vm.ConfirmationMessage, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(vm.ConfirmationMessage.Length, 1, 300);
        AssertSafeVisibleText(vm.ConfirmationMessage);
    }

    [Theory]
    [InlineData("cookie-secret")]
    [InlineData("token-secret")]
    [InlineData("profile-secret")]
    [InlineData("exception-secret")]
    [InlineData("账号 11111111-1111-1111-1111-111111111111")]
    public async Task ConfirmationReplacesSensitiveMarkerLikePreviewNames(string previewName)
    {
        var operations = SingleAccount(Guid.NewGuid(), "旧名称");
        operations.Preview = Ready(previewName, "fresh-token");
        var vm = Create(operations);
        await vm.LoadFirstPageAsync();

        await vm.BeginAccountActionAsync(vm.Accounts[0]);

        Assert.Contains("已选择的账号", vm.ConfirmationMessage);
        AssertSafeVisibleText(vm.ConfirmationMessage);
    }

    [Fact]
    public async Task PaginationKeepsOnlyCurrentPageAndInvalidatesConfirmation()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var operations = new FakeOperations
        {
            Pages =
            {
                [1] = new AccountPage([new(firstId, "账号甲", "Active", true)], 1, 100, 101),
                [2] = new AccountPage([new(secondId, "账号乙", "Active", true)], 2, 100, 101),
            },
            Preview = Ready("账号甲", "page-one-token"),
        };
        var vm = Create(operations);
        await vm.LoadFirstPageAsync();
        await vm.BeginAccountActionAsync(vm.Accounts[0]);
        Assert.True(vm.IsConfirmationVisible);

        await vm.LoadNextPageAsync();

        Assert.False(vm.IsConfirmationVisible);
        Assert.Single(vm.Accounts);
        Assert.Equal("账号乙", vm.Accounts[0].DisplayName);
        Assert.Equal([(1, 100), (2, 100)], operations.PageRequests);
        Assert.True(vm.LoadPreviousPageCommand.CanExecute(null));
        Assert.False(vm.LoadNextPageCommand.CanExecute(null));
    }

    [Fact]
    public async Task LauncherReturnsPresenterOutcomeAndRaisesRefreshOnlyWhenCommitted()
    {
        var presenter = new FakePresenter
        {
            Result = new AccountManagementRefreshResult(true, AccountExitStatus.CleanupPending),
        };
        var launcher = new AccountManagementLauncher(presenter);
        AccountManagementRefreshEventArgs? raised = null;
        launcher.RefreshRequested += (_, args) => raised = args;

        var result = await launcher.OpenAsync();

        Assert.Equal(presenter.Result, result);
        Assert.Equal(AccountExitStatus.CleanupPending, Assert.IsType<AccountManagementRefreshEventArgs>(raised).Status);
        Assert.Equal(1, presenter.ShowCalls);
    }

    [Fact]
    public void PublicUiTextOffersOnlyTheTwoApprovedExitChoicesAndContainsNoSensitiveMarkers()
    {
        var choices = new[]
        {
            AccountManagementUiText.KeepHistoryChoice,
            AccountManagementUiText.RemoveTerminalHistoryChoice,
        };
        Assert.Equal("仅退出账号，保留历史记录", choices[0]);
        Assert.Equal("退出账号，并清理该账号的终态任务历史", choices[1]);

        var visible = typeof(AccountManagementUiText).GetFields()
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToArray();
        Assert.NotEmpty(visible);
        Assert.All(visible, AssertSafeVisibleText);
        Assert.DoesNotContain(visible, text => text.Contains("云端", StringComparison.Ordinal));
        Assert.DoesNotContain(visible, text => text.Contains("日期", StringComparison.Ordinal));
        Assert.DoesNotContain(visible, text => text.Contains("全部账号", StringComparison.Ordinal));
    }

    [Fact]
    public void WindowXamlIsModalReadyVirtualizedChineseAndAccountScoped()
    {
        var repository = FindRepositoryRoot();
        var path = Path.Combine(
            repository,
            "src",
            "BaiduBatchTransfer.App",
            "Features",
            "Accounts",
            "AccountManagementWindow.xaml");

        var xaml = File.ReadAllText(path);

        Assert.Contains("Title=\"账号管理\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"账号名称\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"登录状态\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"启用状态\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"操作\"", xaml, StringComparison.Ordinal);
        Assert.Contains("EnableRowVirtualization=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("EnableColumnVirtualization=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("VirtualizationMode=\"Recycling\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectedItem=\"{Binding SelectedAccount", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"仅退出账号，保留历史记录\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"退出账号，并清理该账号的终态任务历史\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"取消\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"确认退出\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"重新加载确认信息\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"{Binding ActionAutomationName}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ConfirmRemovalButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "Text=\"退出账号后，专用浏览器登录数据会先进入可恢复区；需要彻底删除时，请点击“继续清理”并再次确认。\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("删除专用浏览器登录数据前会再次要求确认", xaml, StringComparison.Ordinal);
        Assert.Contains("TabIndex=", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("云端", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("日期筛选", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("全部账号", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("cookie", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", xaml, StringComparison.OrdinalIgnoreCase);

        var codeBehind = File.ReadAllText(Path.ChangeExtension(path, ".xaml.cs"));
        Assert.Contains("viewModel.AttachLifetime(lifetime.Token)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("e.Cancel = true", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ConfirmRemovalButton.Focus()", codeBehind, StringComparison.Ordinal);
    }

    private static AccountManagementViewModel Create(
        FakeOperations operations,
        FakeConfirmation? confirmation = null) =>
        new(operations, confirmation ?? new FakeConfirmation(), new FixedTimeProvider(Now));

    private static FakeOperations SingleAccount(
        Guid accountId,
        string displayName,
        string status = "Active",
        bool isEnabled = true,
        bool isCleanupPending = false) =>
        new()
        {
            Pages =
            {
                [1] = new AccountPage([new(accountId, displayName, status, isEnabled, isCleanupPending)], 1, 100, 1),
            },
            Preview = Ready(displayName, "preview-token"),
        };

    private static AccountExitPreview Ready(string displayName, string token) =>
        new(AccountExitStatus.Ready, displayName, 1, 1, 0, token, "raw safe message");

    private static void AssertSafeVisibleText(string value)
    {
        Assert.Matches("[\\u4e00-\\u9fff]", value);
        Assert.DoesNotContain("cookie", value, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", value, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("profile", value, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("exception", value, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(new Regex("[A-Fa-f0-9]{8}-[A-Fa-f0-9-]{27,}", RegexOptions.CultureInvariant), value);
        Assert.DoesNotMatch(new Regex("[A-Za-z]:\\\\", RegexOptions.CultureInvariant), value);
    }

    private static string FindRepositoryRoot(
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFilePath = "")
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

    private sealed class FakeOperations : IAccountRemovalOperations
    {
        public Dictionary<int, AccountPage> Pages { get; } = [];
        public List<(int PageNumber, int PageSize)> PageRequests { get; } = [];
        public List<(Guid AccountId, AccountExitHistoryMode Mode)> PreviewRequests { get; } = [];
        public List<AccountExitRequest> ApplyRequests { get; } = [];
        public List<(Guid AccountId, DateTimeOffset RemovedAt)> ResumeRequests { get; } = [];
        public AccountExitPreview Preview { get; set; } = Ready("账号", "preview-token");
        public AccountExitPreview ApplyResult { get; set; } =
            new(AccountExitStatus.Removed, "", 0, 0, 0, "", "");
        public AccountExitPreview ResumeResult { get; set; } =
            new(AccountExitStatus.Removed, "", 0, 0, 0, "", "");
        public ManagedBrowserProfileDeletionPreparation ManagedPreparation { get; set; } =
            new(ManagedBrowserProfileCleanupStatus.NotFound, null);
        public AccountExitPreview ManagedConfirmResult { get; set; } =
            new(AccountExitStatus.Removed, "", 0, 0, 0, "", "");
        public List<(Guid AccountId, ManagedBrowserProfileDeletionConfirmation Confirmation)> ManagedConfirmRequests { get; } = [];
        public Func<Guid, AccountExitHistoryMode, Task<AccountExitPreview>>? PreviewHandler { get; set; }
        public Func<Guid, AccountExitHistoryMode, CancellationToken, Task<AccountExitPreview>>? PreviewHandlerWithToken { get; set; }
        public Func<AccountExitRequest, CancellationToken, Task<AccountExitPreview>>? ApplyHandlerWithToken { get; set; }

        public Task<AccountPage> GetAccountsPageAsync(
            int pageNumber,
            int pageSize,
            CancellationToken cancellationToken)
        {
            PageRequests.Add((pageNumber, pageSize));
            return Task.FromResult(Pages.TryGetValue(pageNumber, out var page)
                ? page
                : new AccountPage([], pageNumber, pageSize, 0));
        }

        public Task<AccountExitPreview> PreviewAsync(
            Guid accountId,
            AccountExitHistoryMode mode,
            CancellationToken cancellationToken)
        {
            PreviewRequests.Add((accountId, mode));
            return PreviewHandlerWithToken?.Invoke(accountId, mode, cancellationToken)
                ?? PreviewHandler?.Invoke(accountId, mode)
                ?? Task.FromResult(Preview);
        }

        public Task<AccountExitPreview> ApplyAsync(
            AccountExitRequest request,
            CancellationToken cancellationToken)
        {
            ApplyRequests.Add(request);
            return ApplyHandlerWithToken?.Invoke(request, cancellationToken) ?? Task.FromResult(ApplyResult);
        }

        public Task<AccountExitPreview> ResumeCleanupAsync(
            Guid accountId,
            DateTimeOffset removedAt,
            CancellationToken cancellationToken)
        {
            ResumeRequests.Add((accountId, removedAt));
            return Task.FromResult(ResumeResult);
        }

        public Task<ManagedBrowserProfileDeletionPreparation> PrepareManagedProfileDeletionAsync(
            Guid accountId,
            CancellationToken cancellationToken) => Task.FromResult(ManagedPreparation);

        public Task<AccountExitPreview> ConfirmManagedProfileDeletionAsync(
            Guid accountId,
            DateTimeOffset removedAt,
            ManagedBrowserProfileDeletionConfirmation confirmation,
            CancellationToken cancellationToken)
        {
            ManagedConfirmRequests.Add((accountId, confirmation));
            return Task.FromResult(ManagedConfirmResult);
        }
    }

    private sealed class FakeConfirmation : IAccountManagementConfirmation
    {
        public bool ConfirmResult { get; set; }
        public List<string> ConfirmMessages { get; } = [];

        public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default)
        {
            ConfirmMessages.Add(message);
            return Task.FromResult(ConfirmResult);
        }

    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class FakeOwnedPrompt : IAccountManagementOwnedPrompt
    {
        public bool Result { get; set; }
        public Window? Owner { get; private set; }
        public string? Title { get; private set; }
        public int Calls { get; private set; }

        public bool Show(Window owner, string title, string message)
        {
            Owner = owner;
            Title = title;
            Calls++;
            return Result;
        }
    }

    private sealed class FakePresenter : IAccountManagementWindowPresenter
    {
        public AccountManagementRefreshResult Result { get; set; } = AccountManagementRefreshResult.None;
        public int ShowCalls { get; private set; }

        public Task<AccountManagementRefreshResult> ShowAsync(CancellationToken cancellationToken)
        {
            ShowCalls++;
            return Task.FromResult(Result);
        }
    }
}
