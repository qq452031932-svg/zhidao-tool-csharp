using BaiduBatchTransfer.App.Features.Common;
using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class LocalHistoryCleanupViewModelTests
{
    [Fact]
    public async Task SuccessfulDeleteWithReloadFailureReportsDeletedCountAndRefreshFailure()
    {
        var store = new CleanupStore();
        string? status = null;
        using var vm = new LocalHistoryCleanupViewModel(new(store), new CleanupInteraction(), LocalHistoryKind.Transfer,
            () => true, _ => throw new IOException("private path"), text => status = text);
        await vm.ClearAsync();
        Assert.Single(store.Deleted);
        Assert.Contains("已清空 7", status);
        Assert.Contains("刷新", status);
        Assert.DoesNotContain("private", status);
    }
    [Fact]
    public async Task LatePreviewCannotReplaceNewerConflictAndClosedPreviewStaysInvalid()
    {
        var release = new TaskCompletionSource<LocalHistoryCleanupPreview>();
        var store = new CleanupStore { PreviewOverride = () => release.Task };
        using var vm = new LocalHistoryCleanupViewModel(new(store), new CleanupInteraction(), LocalHistoryKind.Transfer,
            () => true, _ => Task.CompletedTask, _ => { });
        var pending = vm.RefreshPreviewAsync();
        store.PreviewOverride = null;
        store.Preview = new(LocalHistoryKind.Transfer, 3, true, "new");
        await vm.RefreshPreviewAsync();
        release.SetResult(new(LocalHistoryKind.Transfer, 7, false, "old"));
        await pending;
        Assert.True(vm.Preview!.HasConflicts);
        Assert.False(vm.CanClear);
        vm.Close();
        await vm.RefreshPreviewAsync();
        Assert.Null(vm.Preview);
    }
    [Theory]
    [InlineData("Transfers", "TransferWorkspaceView.xaml", "处理结果", "清空转存历史", "ClearTransferHistoryCommand")]
    [InlineData("Shares", "ShareWorkspaceView.xaml", "分享结果", "清空分享历史", "ClearShareHistoryCommand")]
    public void CleanupButtonsAreVisibleAccessibleInResultsTab(string folder, string file, string tab, string label, string command)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "src"))) root = root.Parent;
        Assert.NotNull(root);
        var document = System.Xml.Linq.XDocument.Load(Path.Combine(root.FullName, "src", "BaiduBatchTransfer.App", "Features", folder, file));
        var button = Assert.Single(document.Descendants(), node => node.Name.LocalName == "Button" &&
            (string?)node.Attribute("Content") == label);
        Assert.Equal(label, (string?)button.Attribute("AutomationProperties.Name"));
        Assert.Equal("{Binding " + command + "}", (string?)button.Attribute("Command"));
        Assert.Null(button.Attribute("Visibility"));
        Assert.Contains(button.Ancestors(), node => node.Name.LocalName == "TabItem" && (string?)node.Attribute("Header") == tab);
        Assert.NotNull(button.Attribute("ToolTip"));
        Assert.Contains("Danger", (string?)button.Attribute("Style"));
        var interaction = File.ReadAllText(Path.Combine(root.FullName, "src", "BaiduBatchTransfer.App", "Features", "Common", "LocalHistoryCleanupInteraction.cs"));
        Assert.Contains("MessageBoxButton.YesNo", interaction);
        Assert.Contains("MessageBoxResult.No", interaction);
    }
    [Theory]
    [InlineData(0, false)]
    [InlineData(3, true)]
    public async Task EmptyOrConflictedPreviewDisablesCommandAndNeverConfirms(int count, bool conflict)
    {
        var store = new CleanupStore { Preview = new(LocalHistoryKind.Transfer, count, conflict, count > 0 ? "one" : null) };
        var interaction = new CleanupInteraction();
        string? status = null;
        using var vm = new LocalHistoryCleanupViewModel(new(store), interaction, LocalHistoryKind.Transfer,
            () => true, _ => Task.CompletedTask, text => status = text);
        await vm.RefreshPreviewAsync();
        Assert.False(vm.CanClear);
        await vm.ClearAsync();
        Assert.Empty(interaction.Confirmed);
        Assert.Empty(store.Deleted);
        Assert.Contains(conflict ? "仍有任务在处理" : "没有可清空", status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmationUsesLatestExactPreviewAndCancellationNeverDeletes(bool accept)
    {
        var store = new CleanupStore();
        var interaction = new CleanupInteraction { Accept = accept };
        var reloads = 0;
        string? status = null;
        using var vm = new LocalHistoryCleanupViewModel(new(store), interaction, LocalHistoryKind.Transfer,
            () => true, _ => { reloads++; return Task.CompletedTask; }, text => status = text);
        await vm.RefreshPreviewAsync();
        store.Preview = new(LocalHistoryKind.Transfer, 7, false, "latest");
        await vm.ClearAsync();
        Assert.Same(store.Preview, Assert.Single(interaction.Confirmed));
        Assert.Equal(accept ? 1 : 0, store.Deleted.Count);
        if (accept) Assert.Same(interaction.Confirmed.Single(), store.Deleted.Single());
        Assert.Equal(accept ? 1 : 0, reloads);
        Assert.Contains(accept ? "7" : "已取消", status);
    }

    [Fact]
    public async Task TokenDriftConflictDoesNotReloadOrClaimSuccess()
    {
        var store = new CleanupStore { Result = new(0, LocalHistoryCleanupDisposition.Conflict) };
        string? status = null;
        var reloads = 0;
        using var vm = new LocalHistoryCleanupViewModel(new(store), new CleanupInteraction(), LocalHistoryKind.Transfer,
            () => true, _ => { reloads++; return Task.CompletedTask; }, text => status = text);
        await vm.ClearAsync();
        Assert.Single(store.Deleted);
        Assert.Equal(0, reloads);
        Assert.Contains("稍后重试", status);
        Assert.DoesNotContain("已清空", status);
    }

    [Fact]
    public async Task BusyAndClosedOwnerCannotClearAndLateConfirmationCannotDelete()
    {
        var available = true;
        var release = new TaskCompletionSource<bool>();
        var interaction = new CleanupInteraction { Confirm = (_, _) => release.Task };
        var store = new CleanupStore();
        using var vm = new LocalHistoryCleanupViewModel(new(store), interaction, LocalHistoryKind.Transfer,
            () => available, _ => Task.CompletedTask, _ => { });
        await vm.RefreshPreviewAsync();
        Assert.True(vm.CanClear);
        available = false;
        Assert.False(vm.ClearCommand.CanExecute(null));
        await vm.ClearAsync();
        Assert.Empty(interaction.Confirmed);
        available = true;
        var pending = vm.ClearAsync();
        Assert.True(vm.IsBusy);
        await vm.ClearAsync();
        Assert.Single(interaction.Confirmed);
        vm.Close();
        release.SetResult(true);
        await pending;
        Assert.False(vm.CanClear);
        Assert.Empty(store.Deleted);
    }

    [Fact]
    public async Task FailedPreviewInvalidatesPreviousEligibilityAndDeletionFailurePreservesDisplay()
    {
        var store = new CleanupStore();
        string? status = null;
        var reloads = 0;
        using var vm = new LocalHistoryCleanupViewModel(new(store), new CleanupInteraction(), LocalHistoryKind.Transfer,
            () => true, _ => { reloads++; return Task.CompletedTask; }, text => status = text);
        await vm.RefreshPreviewAsync();
        store.ThrowPreview = true;
        await vm.RefreshPreviewAsync();
        Assert.False(vm.CanClear);
        Assert.Null(vm.Preview);
        store.ThrowPreview = false;
        store.ThrowDelete = true;
        await vm.ClearAsync();
        Assert.Equal(0, reloads);
        Assert.Contains("稍后重试", status);
    }

    [Theory]
    [InlineData(LocalHistoryKind.Transfer, "转存")]
    [InlineData(LocalHistoryKind.StandaloneShare, "分享")]
    public void ConfirmationContainsExactBatchCountAndScope(LocalHistoryKind kind, string label)
    {
        var text = WpfLocalHistoryCleanupInteraction.BuildMessage(new(kind, 1234, false, "token"));
        Assert.Contains("1234", text);
        Assert.Contains(label, text);
        Assert.Contains("仅删除本地记录，不影响百度网盘文件和分享链接", text);
    }
}

internal sealed class CleanupStore : ILocalTerminalHistoryStore
{
    public Func<Task<LocalHistoryCleanupPreview>>? PreviewOverride { get; set; }
    public LocalHistoryCleanupPreview Preview { get; set; } = new(LocalHistoryKind.Transfer, 7, false, "first");
    public List<LocalHistoryCleanupPreview> Deleted { get; } = [];
    public LocalHistoryCleanupResult? Result { get; set; }
    public bool ThrowPreview { get; set; }
    public bool ThrowDelete { get; set; }
    public Action? OnDelete { get; set; }
    public Task<LocalHistoryCleanupPreview> PreviewAsync(LocalHistoryKind kind, CancellationToken cancellationToken = default)
    {
        if (ThrowPreview) throw new InvalidOperationException("private failure");
        return PreviewOverride?.Invoke() ?? Task.FromResult(Preview);
    }
    public Task<LocalHistoryCleanupResult> DeleteAsync(LocalHistoryCleanupPreview confirmedPreview, CancellationToken cancellationToken = default)
    {
        Deleted.Add(confirmedPreview);
        if (ThrowDelete) throw new InvalidOperationException("private failure");
        OnDelete?.Invoke();
        return Task.FromResult(Result ?? new(confirmedPreview.TerminalBatchCount, LocalHistoryCleanupDisposition.Deleted));
    }
}

internal sealed class CleanupInteraction : ILocalHistoryCleanupInteraction
{
    public bool Accept { get; set; } = true;
    public List<LocalHistoryCleanupPreview> Confirmed { get; } = [];
    public Func<LocalHistoryCleanupPreview, CancellationToken, Task<bool>>? Confirm { get; set; }
    public Task<bool> ConfirmAsync(LocalHistoryCleanupPreview preview, CancellationToken cancellationToken = default)
    {
        Confirmed.Add(preview);
        return Confirm?.Invoke(preview, cancellationToken) ?? Task.FromResult(Accept);
    }
}
