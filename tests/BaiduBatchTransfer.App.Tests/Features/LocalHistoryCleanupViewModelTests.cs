using BaiduBatchTransfer.App.Features.Common;
using BaiduBatchTransfer.Application.Jobs;
using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class LocalHistoryCleanupViewModelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulDeleteWithReloadFailureReportsDeletedCountAndRefreshFailure(bool finalPreviewFails)
    {
        var store = new CleanupStore();
        string? status = null;
        using var vm = new LocalHistoryCleanupViewModel(new(store), new CleanupInteraction(), LocalHistoryKind.Transfer,
            () => true, _ =>
            {
                store.ThrowPreview = finalPreviewFails;
                throw new IOException("private path");
            }, text => status = text);
        await vm.ClearAsync();
        Assert.Single(store.Deleted);
        Assert.Contains("已清空 7", status);
        Assert.Contains("刷新", status);
        Assert.DoesNotContain("private", status);
    }
    [Fact]
    public async Task CoalescedPreviewCannotApplyAfterClose()
    {
        var release = new TaskCompletionSource<LocalHistoryCleanupPreview>();
        var store = new CleanupStore { PreviewOverride = () => release.Task };
        using var vm = new LocalHistoryCleanupViewModel(new(store), new CleanupInteraction(), LocalHistoryKind.Transfer,
            () => true, _ => Task.CompletedTask, _ => { }, new InlineBackgroundOperationRunner());
        var pending = vm.RefreshPreviewAsync();
        var repeated = vm.RefreshPreviewAsync();
        vm.Close();
        release.SetResult(new(LocalHistoryKind.Transfer, 7, false, "old"));
        await Task.WhenAll(pending, repeated);
        Assert.False(vm.CanClear);
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
        var x = System.Xml.Linq.XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");
        var style = Assert.Single(document.Descendants(), element =>
            element.Name.LocalName == "Style" && (string?)element.Attribute(x + "Key") == "DangerHistoryButton");
        var highContrast = Assert.Single(style.Descendants(), element => element.Name.LocalName == "DataTrigger" &&
            ((string?)element.Attribute("Binding"))?.Contains("SystemParameters.HighContrast", StringComparison.Ordinal) == true);
        Assert.Contains(highContrast.Elements(), element => (string?)element.Attribute("Property") == "Background" &&
            (string?)element.Attribute("Value") == "{DynamicResource {x:Static SystemColors.HighlightBrushKey}}");
        Assert.Contains(highContrast.Elements(), element => (string?)element.Attribute("Property") == "Foreground" &&
            (string?)element.Attribute("Value") == "{DynamicResource {x:Static SystemColors.HighlightTextBrushKey}}");
        Assert.Contains(style.Descendants(), element => element.Name.LocalName == "Trigger" &&
            (string?)element.Attribute("Property") == "IsKeyboardFocused");
        Assert.Contains(style.Descendants(), element => element.Name.LocalName == "Trigger" &&
            (string?)element.Attribute("Property") == "IsEnabled");
        Assert.Contains(style.Elements(), element => (string?)element.Attribute("Property") == "Height" &&
            (string?)element.Attribute("Value") == "Auto");
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
        await interaction.Started.Task;
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

public sealed class LocalHistoryCleanupUnfinishedBatchTests
{
    [Fact]
    public async Task WhenOnlyUnfinishedBatchesExistTheButtonStillWorksAndEndsThemFirst()
    {
        // 其他用户的情况：导入错了、整批都还在「等待」，原来按钮点了也清不掉。
        var history = new CleanupStore { Preview = new(LocalHistoryKind.Transfer, 0, false, "empty") };
        var batches = new RunControlStore([Guid.NewGuid()]);
        batches.OnEnded = () => history.Preview = new(LocalHistoryKind.Transfer, 1, false, "after-end");
        var interaction = new CleanupInteraction { UnfinishedChoice = UnfinishedBatchChoice.EndThenClear };
        string? status = null;
        var reloads = 0;
        using var vm = Create(history, interaction, batches, text => status = text, () => reloads++);

        await vm.RefreshPreviewAsync();
        Assert.True(vm.CanClear);
        Assert.Equal(1, vm.UnfinishedBatchCount);
        Assert.Contains("1 个批次还没结束", vm.ClearBlockedReason);

        await vm.ClearAsync();

        Assert.Equal([(1, 0)], interaction.UnfinishedQuestions);
        Assert.Single(batches.Ended);
        Assert.Equal("after-end", Assert.Single(history.Deleted).SnapshotToken);
        Assert.True(reloads >= 2);
        Assert.Contains("已清空 1 个已结束的转存批次历史", status);
        Assert.DoesNotContain("予以保留", status);
    }

    [Fact]
    public async Task ChoosingToClearOnlyEndedBatchesLeavesUnfinishedOnesRunning()
    {
        var history = new CleanupStore();
        var batches = new RunControlStore([Guid.NewGuid(), Guid.NewGuid()]);
        var interaction = new CleanupInteraction { UnfinishedChoice = UnfinishedBatchChoice.ClearEndedOnly };
        string? status = null;
        using var vm = Create(history, interaction, batches, text => status = text);

        await vm.ClearAsync();

        Assert.Equal([(2, 7)], interaction.UnfinishedQuestions);
        Assert.Empty(batches.Ended);
        Assert.Single(history.Deleted);
        Assert.Contains("予以保留", status);
    }

    [Fact]
    public async Task CancellingTheUnfinishedQuestionDoesNothing()
    {
        var history = new CleanupStore();
        var batches = new RunControlStore([Guid.NewGuid()]);
        var interaction = new CleanupInteraction { UnfinishedChoice = UnfinishedBatchChoice.Cancel };
        string? status = null;
        using var vm = Create(history, interaction, batches, text => status = text);

        await vm.ClearAsync();

        Assert.Empty(batches.Ended);
        Assert.Empty(history.Deleted);
        Assert.Empty(interaction.Confirmed);
        Assert.Equal("已取消清空历史。", status);
    }

    [Fact]
    public async Task ABatchStillSubmittingIsStoppedAndTheUserIsToldToClearAgainLater()
    {
        var history = new CleanupStore { Preview = new(LocalHistoryKind.Transfer, 0, false, "empty") };
        var batches = new RunControlStore([Guid.NewGuid()]) { InFlight = 2 };
        var interaction = new CleanupInteraction { UnfinishedChoice = UnfinishedBatchChoice.EndThenClear };
        string? status = null;
        using var vm = Create(history, interaction, batches, text => status = text);

        await vm.ClearAsync();

        Assert.Empty(history.Deleted);
        Assert.Empty(interaction.Confirmed);
        Assert.Contains("1 个批次还有正在提交的记录", status);
        Assert.Contains("再点一次「清空转存历史」", status);
    }

    [Fact]
    public async Task WithNothingAtAllTheButtonStaysOff()
    {
        var history = new CleanupStore { Preview = new(LocalHistoryKind.Transfer, 0, false, "empty") };
        using var vm = Create(history, new CleanupInteraction(), new RunControlStore([]), _ => { });

        await vm.RefreshPreviewAsync();

        Assert.False(vm.CanClear);
        Assert.Equal("没有可清空的记录。", vm.ClearBlockedReason);
    }

    [Fact]
    public async Task ShareHistoryIgnoresTransferBatches()
    {
        var history = new CleanupStore { Preview = new(LocalHistoryKind.StandaloneShare, 3, false, "share") };
        var batches = new RunControlStore([Guid.NewGuid()]);
        var interaction = new CleanupInteraction { UnfinishedChoice = UnfinishedBatchChoice.EndThenClear };
        using var vm = new LocalHistoryCleanupViewModel(new(history), interaction, LocalHistoryKind.StandaloneShare,
            () => true, _ => Task.CompletedTask, _ => { }, new InlineBackgroundOperationRunner(),
            unfinishedBatches: new EndBatchService(batches));

        await vm.ClearAsync();

        Assert.Empty(interaction.UnfinishedQuestions);
        Assert.Empty(batches.Ended);
        Assert.Single(history.Deleted);
    }

    private static LocalHistoryCleanupViewModel Create(CleanupStore history, CleanupInteraction interaction,
        RunControlStore batches, Action<string> report, Action? reloaded = null) =>
        new(new(history), interaction, LocalHistoryKind.Transfer, () => true,
            _ => { reloaded?.Invoke(); return Task.CompletedTask; }, report, new InlineBackgroundOperationRunner(),
            unfinishedBatches: new EndBatchService(batches));

    /// <summary>内存里的批次：结束时按 <see cref="InFlight"/> 决定是直接结束还是要等提交收尾。</summary>
    private sealed class RunControlStore(IEnumerable<Guid> unfinished) : IBatchRunControlStore
    {
        private readonly List<Guid> unfinished = unfinished.ToList();
        public List<Guid> Ended { get; } = [];
        public int InFlight { get; set; }
        public Action? OnEnded { get; set; }
        public Task<IReadOnlyList<Guid>> ListUnfinishedBatchIdsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Guid>>(unfinished.ToArray());
        public Task<EndBatchResult> TryEndBatchAsync(Guid batchJobId, DateTimeOffset endedAt, CancellationToken cancellationToken = default)
        {
            if (InFlight > 0) return Task.FromResult(new EndBatchResult(EndBatchDisposition.Stopping, 3, InFlight));
            unfinished.Remove(batchJobId);
            Ended.Add(batchJobId);
            OnEnded?.Invoke();
            return Task.FromResult(new EndBatchResult(EndBatchDisposition.Ended, 3, 0));
        }
        public Task<PauseBatchDisposition> TryPauseBatchAsync(Guid batchJobId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ResumeBatchDisposition> TryResumeBatchAsync(Guid batchJobId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RetryFailedTransferDisposition> TryRetryFailedTransferAsync(Guid operationId, DateTimeOffset retriedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Accept { get; set; } = true;
    public List<LocalHistoryCleanupPreview> Confirmed { get; } = [];
    public Func<LocalHistoryCleanupPreview, CancellationToken, Task<bool>>? Confirm { get; set; }
    public UnfinishedBatchChoice UnfinishedChoice { get; set; } = UnfinishedBatchChoice.ClearEndedOnly;
    public List<(int Unfinished, int Ended)> UnfinishedQuestions { get; } = [];
    public Task<UnfinishedBatchChoice> ChooseUnfinishedAsync(int unfinishedBatchCount, int endedBatchCount, CancellationToken cancellationToken = default)
    {
        UnfinishedQuestions.Add((unfinishedBatchCount, endedBatchCount));
        return Task.FromResult(UnfinishedChoice);
    }
    public Task<bool> ConfirmAsync(LocalHistoryCleanupPreview preview, CancellationToken cancellationToken = default)
    {
        Confirmed.Add(preview);
        Started.TrySetResult();
        return Confirm?.Invoke(preview, cancellationToken) ?? Task.FromResult(Accept);
    }
}

internal sealed class InlineBackgroundOperationRunner : IBackgroundOperationRunner
{
    public Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken) => operation(cancellationToken);
}
