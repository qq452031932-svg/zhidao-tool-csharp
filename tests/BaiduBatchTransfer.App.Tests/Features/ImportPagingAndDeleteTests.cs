using BaiduBatchTransfer.App.Features.Imports;
using BaiduBatchTransfer.App.Features.Jobs;
using BaiduBatchTransfer.App.Services;

namespace BaiduBatchTransfer.App.Tests.Features;

/// <summary>
/// 待处理列表的「上一页」与单行删除：往回翻要回到原来那一页，删除只动选中的那一行，
/// 删完之后前后页不重复、不漏行。
/// </summary>
public sealed class ImportPagingAndDeleteTests
{
    [Fact]
    public async Task PreviousPageReturnsToExactlyThePagesAlreadySeen()
    {
        var (vm, draft, _) = await CreateAsync(rows: 250);
        Assert.False(vm.LoadPreviousPageCommand.CanExecute(null), "第一页不该能往回翻");
        Assert.Equal("第 1/3 页", vm.PageText);
        var first = Snapshot(vm);

        await vm.LoadNextPageCommand.ExecuteAsync(null);
        var second = Snapshot(vm);
        await vm.LoadNextPageCommand.ExecuteAsync(null);
        Assert.Equal("第 3/3 页", vm.PageText);
        Assert.Equal(50, vm.Rows.Count);
        Assert.Equal(201, vm.Rows[0].DisplayOrdinal);
        Assert.False(vm.LoadNextPageCommand.CanExecute(null));

        await vm.LoadPreviousPageCommand.ExecuteAsync(null);
        Assert.Equal(second, Snapshot(vm));
        Assert.Equal(2, vm.PageNumber);
        Assert.True(vm.LoadNextPageCommand.CanExecute(null));

        await vm.LoadPreviousPageCommand.ExecuteAsync(null);
        Assert.Equal(first, Snapshot(vm));
        Assert.Equal(1, vm.Rows[0].DisplayOrdinal);
        Assert.False(vm.LoadPreviousPageCommand.CanExecute(null));

        // 往回翻之后再往后翻，仍是同样的内容。
        await vm.LoadNextPageCommand.ExecuteAsync(null);
        Assert.Equal(second, Snapshot(vm));
        Assert.Equal(250, draft.Rows.Count);
    }

    [Fact]
    public async Task RefreshAndOtherEditsGoBackToTheFirstPageAndForgetThePageTrail()
    {
        var (vm, _, _) = await CreateAsync(rows: 250);
        await vm.LoadNextPageCommand.ExecuteAsync(null);
        await vm.LoadNextPageCommand.ExecuteAsync(null);

        await vm.LoadFirstPageCommand.ExecuteAsync(null);
        Assert.Equal(1, vm.PageNumber);
        Assert.False(vm.LoadPreviousPageCommand.CanExecute(null));

        await vm.LoadNextPageCommand.ExecuteAsync(null);
        vm.SelectedRow = vm.Rows[3];
        await vm.ToggleSelectedExclusionCommand.ExecuteAsync(null);
        Assert.Equal(1, vm.PageNumber);
        Assert.Equal(1, vm.Rows[0].DisplayOrdinal);
        Assert.False(vm.LoadPreviousPageCommand.CanExecute(null));
    }

    [Fact]
    public async Task FailedPageLoadLeavesThePositionWhereItWas()
    {
        var (vm, draft, _) = await CreateAsync(rows: 250);
        await vm.LoadNextPageCommand.ExecuteAsync(null);
        var second = Snapshot(vm);

        draft.FailNextLoad = true;
        await vm.LoadNextPageCommand.ExecuteAsync(null);
        Assert.Equal("无法加载导入记录，请稍后重试。", vm.StatusMessage);
        Assert.Equal(second, Snapshot(vm));
        Assert.Equal(2, vm.PageNumber);

        draft.FailNextLoad = true;
        await vm.LoadPreviousPageCommand.ExecuteAsync(null);
        Assert.Equal(second, Snapshot(vm));
        Assert.Equal(2, vm.PageNumber);

        await vm.LoadPreviousPageCommand.ExecuteAsync(null);
        Assert.Equal(1, vm.PageNumber);
        Assert.Equal(1, vm.Rows[0].DisplayOrdinal);
    }

    [Fact]
    public async Task DeleteRemovesOnlyTheSelectedRowAndStaysOnTheSamePage()
    {
        var (vm, draft, dialog) = await CreateAsync(rows: 250);
        await vm.LoadNextPageCommand.ExecuteAsync(null);
        var before = vm.Rows.Select(row => row.RowId).ToArray();
        var target = vm.Rows[10];
        vm.SelectedRow = target;

        await vm.DeleteSelectedRowCommand.ExecuteAsync(null);

        Assert.Equal([target.RowId], draft.Deleted);
        Assert.Contains("第 111 条", Assert.Single(dialog.Confirmations));
        Assert.Equal("已删除该记录。", vm.StatusMessage);
        Assert.Equal(2, vm.PageNumber);
        Assert.Equal(249, vm.TotalRows);
        Assert.Null(vm.SelectedRow);
        // 相邻的行一行没少，后面的一行补了上来，序号仍然连续。
        var after = vm.Rows.Select(row => row.RowId).ToArray();
        Assert.Equal(100, after.Length);
        Assert.Equal(before.Where(id => id != target.RowId), after.Take(99));
        Assert.Equal(Enumerable.Range(101, 100).Select(value => (long)value), vm.Rows.Select(row => row.DisplayOrdinal));

        // 删除之后把全部页走一遍：不重复、不漏行。
        await vm.LoadFirstPageCommand.ExecuteAsync(null);
        var seen = vm.Rows.Select(row => row.RowId).ToList();
        while (vm.LoadNextPageCommand.CanExecute(null))
        {
            await vm.LoadNextPageCommand.ExecuteAsync(null);
            seen.AddRange(vm.Rows.Select(row => row.RowId));
        }
        Assert.Equal(draft.Rows.Select(row => row.RowId), seen);
        Assert.DoesNotContain(target.RowId, seen);
    }

    [Fact]
    public async Task NextAndPreviousStayConsistentAfterDeletingOnAMiddlePage()
    {
        var (vm, _, _) = await CreateAsync(rows: 250);
        var first = Snapshot(vm);
        await vm.LoadNextPageCommand.ExecuteAsync(null);
        vm.SelectedRow = vm.Rows[0];
        await vm.DeleteSelectedRowCommand.ExecuteAsync(null);

        await vm.LoadNextPageCommand.ExecuteAsync(null);
        Assert.Equal(201, vm.Rows[0].DisplayOrdinal);
        Assert.Equal(49, vm.Rows.Count);
        await vm.LoadPreviousPageCommand.ExecuteAsync(null);
        Assert.Equal(101, vm.Rows[0].DisplayOrdinal);
        Assert.Equal(100, vm.Rows.Count);
        await vm.LoadPreviousPageCommand.ExecuteAsync(null);
        Assert.Equal(first, Snapshot(vm));
    }

    [Fact]
    public async Task DeletingTheLastRowOfTheLastPageFallsBackToThePreviousPage()
    {
        var (vm, draft, _) = await CreateAsync(rows: 101);
        await vm.LoadNextPageCommand.ExecuteAsync(null);
        Assert.Single(vm.Rows);
        vm.SelectedRow = vm.Rows[0];

        await vm.DeleteSelectedRowCommand.ExecuteAsync(null);

        Assert.Equal(100, draft.Rows.Count);
        Assert.Equal(1, vm.PageNumber);
        Assert.Equal(100, vm.Rows.Count);
        Assert.Equal(1, vm.Rows[0].DisplayOrdinal);
        Assert.False(vm.LoadPreviousPageCommand.CanExecute(null));
        Assert.False(vm.LoadNextPageCommand.CanExecute(null));
        Assert.Equal("第 1/1 页", vm.PageText);
    }

    [Fact]
    public async Task DeletingTheOnlyRowLeavesAnEmptyFirstPage()
    {
        var (vm, draft, _) = await CreateAsync(rows: 1);
        vm.SelectedRow = vm.Rows[0];

        await vm.DeleteSelectedRowCommand.ExecuteAsync(null);

        Assert.Empty(draft.Rows);
        Assert.Empty(vm.Rows);
        Assert.Equal(1, vm.PageNumber);
        Assert.Equal(string.Empty, vm.PageText);
        Assert.False(vm.DeleteSelectedRowCommand.CanExecute(null));
    }

    [Fact]
    public async Task DecliningTheConfirmationDeletesNothing()
    {
        var (vm, draft, dialog) = await CreateAsync(rows: 5);
        dialog.Answer = false;
        vm.SelectedRow = vm.Rows[2];

        await vm.DeleteSelectedRowCommand.ExecuteAsync(null);

        Assert.Empty(draft.Deleted);
        Assert.Equal(5, vm.Rows.Count);
        Assert.Same(vm.Rows[2], vm.SelectedRow);
        Assert.Contains("不能恢复", Assert.Single(dialog.Confirmations));
    }

    [Fact]
    public async Task DeleteNeedsASelectedRow()
    {
        var (vm, draft, dialog) = await CreateAsync(rows: 5);
        Assert.False(vm.DeleteSelectedRowCommand.CanExecute(null));

        await vm.DeleteSelectedRowAsync();

        Assert.Empty(dialog.Confirmations);
        Assert.Empty(draft.Deleted);
        vm.SelectedRow = vm.Rows[0];
        Assert.True(vm.DeleteSelectedRowCommand.CanExecute(null));
    }

    [Fact]
    public async Task RejectedDeleteKeepsTheRowAndSaysWhy()
    {
        var (vm, draft, _) = await CreateAsync(rows: 5);
        draft.RejectDelete = true;
        vm.SelectedRow = vm.Rows[1];

        await vm.DeleteSelectedRowCommand.ExecuteAsync(null);

        Assert.Equal(5, draft.Rows.Count);
        Assert.Equal(5, vm.Rows.Count);
        Assert.Equal("无法删除记录：请刷新后重试。（InvalidOperationException）", vm.StatusMessage);
        Assert.False(vm.IsBusy);
    }

    [Theory]
    [InlineData("DRAFT_NOT_MUTABLE", "无法删除记录：这批导入已经生成转存任务或正在处理，不能再删除。（DRAFT_NOT_MUTABLE）")]
    [InlineData("DRAFT_ROW_NOT_FOUND", "无法删除记录：这条记录已不在当前导入中，请点「刷新」。（DRAFT_ROW_NOT_FOUND）")]
    [InlineData("DRAFT_SOMETHING_ELSE", "无法删除记录：当前不能修改这批导入。（DRAFT_SOMETHING_ELSE）")]
    public async Task FailedDeleteStatesTheRealReasonWithACodeForTheRunLog(string code, string expected)
    {
        var (vm, draft, _) = await CreateAsync(rows: 5);
        draft.DeleteError = new BaiduBatchTransfer.Application.Imports.DraftWorkflowException(code, "含有 https://pan.baidu.com/s/secret 的内部说明");
        vm.SelectedRow = vm.Rows[1];

        await vm.DeleteSelectedRowCommand.ExecuteAsync(null);

        Assert.Equal(expected, vm.StatusMessage);
        Assert.Equal(5, vm.Rows.Count);
    }

    [Fact]
    public async Task DeletingARowThatIsAlreadyGoneAsksForARefresh()
    {
        var (vm, draft, _) = await CreateAsync(rows: 5);
        draft.DeleteError = new ImportWorkflowException(ImportUiError.InvalidData);
        vm.SelectedRow = vm.Rows[1];

        await vm.DeleteSelectedRowCommand.ExecuteAsync(null);

        Assert.Equal("无法删除记录：这条记录已不在当前导入中，请点「刷新」。（ROW_MISSING）", vm.StatusMessage);
    }

    [Fact]
    public async Task UnfinishedImportCannotDeleteRowsBecauseResumingWouldBringThemBack()
    {
        var draft = new Draft(5) { Resumable = true };
        var vm = new ImportViewModel(draft, draft, new Dialog());
        await vm.InitializeAsync();
        Assert.True(vm.CanResumeImport);
        await vm.LoadFirstPageAsync();
        vm.SelectedRow = vm.Rows[0];

        Assert.False(vm.DeleteSelectedRowCommand.CanExecute(null));
        await vm.DeleteSelectedRowAsync();
        Assert.Empty(draft.Deleted);
    }

    [Fact]
    public async Task ClearingTheCurrentImportResetsThePagePosition()
    {
        var (vm, _, _) = await CreateAsync(rows: 250);
        await vm.LoadNextPageCommand.ExecuteAsync(null);

        Assert.True(vm.TryResetAfterSuccessfulCleanup(vm.SessionId!.Value));

        Assert.Equal(1, vm.PageNumber);
        Assert.Equal(string.Empty, vm.PageText);
        Assert.False(vm.LoadPreviousPageCommand.CanExecute(null));
    }

    [Fact]
    public void InTheRealTableDeletingTwiceInARowActsOnTheRowChosenEachTime() => TransferPendingSelectionTests.OnSta(() =>
    {
        var draft = new Draft(250);
        using var vm = new ImportViewModel(draft, draft, new Dialog());
        vm.AttachSession(draft.SessionId);
        vm.LoadFirstPageAsync().GetAwaiter().GetResult();
        var view = TransferPendingSelectionTests.LoadPendingTable(vm);
        var grid = TransferPendingSelectionTests.Descendants(view).OfType<System.Windows.Controls.DataGrid>().Single();
        void Pump() => view.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);

        var first = vm.Rows[3];
        grid.SelectedItem = first;
        Pump();
        Assert.Same(first, vm.SelectedRow);
        vm.DeleteSelectedRowCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Pump();
        Assert.Equal([first.RowId], draft.Deleted);
        Assert.True(vm.SelectedRow is null || vm.Rows.Contains(vm.SelectedRow), "删除后选中项不能停在已删除的行上");

        // 用户实机遇到的情况：表格控件还记着刚删掉的那一行，再选别的行会被顶回去，
        // 第二次删除落在不存在的记录上（运行日志里的 ROW_MISSING）。
        Assert.Null(grid.SelectedItem);
        var second = vm.Rows[5];
        grid.SelectedItem = second;
        Pump();
        Assert.Same(second, vm.SelectedRow);
        Assert.Same(second, grid.SelectedItem);
        vm.DeleteSelectedRowCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Pump();
        Assert.Equal([first.RowId, second.RowId], draft.Deleted);
        Assert.Equal("已删除该记录。", vm.StatusMessage);
    });

    [Fact]
    public async Task ConflictsOnlyListsJustTheConflictsAndDeletingThemAllReturnsToEverything()
    {
        var (vm, draft, _) = await CreateAsync(rows: 250);
        draft.MarkConflict(40, 130, 131);
        await vm.LoadNextPageCommand.ExecuteAsync(null);
        Assert.True(vm.CanFilterConflicts);

        vm.ShowConflictsOnly = true;

        Assert.Equal(new[] { 80, 260, 262 }, vm.Rows.Select(row => row.SourceRowNumber));
        Assert.Equal(new long[] { 1, 2, 3 }, vm.Rows.Select(row => row.DisplayOrdinal));
        Assert.Equal("第 1/1 页", vm.PageText);
        Assert.Equal(250, vm.TotalRows);
        Assert.False(vm.LoadPreviousPageCommand.CanExecute(null));
        Assert.False(vm.LoadNextPageCommand.CanExecute(null));

        vm.SelectedRow = vm.Rows[1];
        await vm.DeleteSelectedRowCommand.ExecuteAsync(null);
        Assert.True(vm.ShowConflictsOnly);
        Assert.Equal(new[] { 80, 262 }, vm.Rows.Select(row => row.SourceRowNumber));
        Assert.Equal(250, vm.TotalRows + 1);

        // 排除也算处理掉：这一行不再是冲突，从筛选结果里消失。
        vm.SelectedRow = vm.Rows[0];
        await vm.ToggleSelectedExclusionCommand.ExecuteAsync(null);
        Assert.Equal(new[] { 262 }, vm.Rows.Select(row => row.SourceRowNumber));

        vm.SelectedRow = vm.Rows[0];
        await vm.DeleteSelectedRowCommand.ExecuteAsync(null);
        Assert.False(vm.ShowConflictsOnly);
        Assert.Equal("没有冲突记录了，已显示全部记录。", vm.StatusMessage);
        Assert.Equal(100, vm.Rows.Count);
        Assert.Equal(1, vm.Rows[0].DisplayOrdinal);
        Assert.Equal(248, vm.TotalRows);
        Assert.Equal("第 1/3 页", vm.PageText);
    }

    [Fact]
    public async Task ConflictsOnlyWithNothingToShowSwitchesItselfBackOff()
    {
        var (vm, _, _) = await CreateAsync(rows: 5);
        var notified = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ImportViewModel.ShowConflictsOnly)) notified++; };

        vm.ShowConflictsOnly = true;

        Assert.False(vm.ShowConflictsOnly);
        Assert.Equal(2, notified);
        Assert.Equal(5, vm.Rows.Count);
        Assert.Equal("没有冲突记录了，已显示全部记录。", vm.StatusMessage);
    }

    [Fact]
    public async Task TurningTheFilterOffReturnsToTheFullListAndANewImportStartsUnfiltered()
    {
        var (vm, draft, _) = await CreateAsync(rows: 250);
        draft.MarkConflict(7);
        vm.ShowConflictsOnly = true;
        Assert.Single(vm.Rows);

        vm.ShowConflictsOnly = false;
        Assert.Equal(100, vm.Rows.Count);
        Assert.Equal("第 1/3 页", vm.PageText);

        vm.ShowConflictsOnly = true;
        vm.AttachSession(Guid.NewGuid());
        Assert.False(vm.ShowConflictsOnly);
    }

    [Fact]
    public void FilterCannotBeSwitchedBeforeAnythingIsImported()
    {
        var draft = new Draft(3);
        using var vm = new ImportViewModel(draft, draft, new Dialog());
        Assert.False(vm.CanFilterConflicts);

        vm.ShowConflictsOnly = true;

        Assert.False(vm.ShowConflictsOnly);
    }

    [Fact]
    public async Task SeveralSelectedRowsAreDeletedTogetherAfterOneConfirmation()
    {
        var (vm, draft, dialog) = await CreateAsync(rows: 250);
        await vm.LoadNextPageCommand.ExecuteAsync(null);
        var before = vm.Rows.Select(row => row.RowId).ToArray();
        var targets = new[] { vm.Rows[2], vm.Rows[3], vm.Rows[40] };
        vm.SelectedRow = targets[0];
        vm.SetSelectedRows(targets);

        Assert.Equal("删除所选 3 条记录", vm.DeleteMenuText);
        await vm.DeleteSelectedRowCommand.ExecuteAsync(null);

        Assert.Equal([targets.Select(row => row.RowId).ToArray()], draft.DeletedTogether);
        Assert.Empty(draft.Deleted);
        Assert.Contains("选中的 3 条", Assert.Single(dialog.Confirmations));
        Assert.Equal("已删除 3 条记录。", vm.StatusMessage);
        Assert.Equal(2, vm.PageNumber);
        Assert.Equal(247, vm.TotalRows);
        Assert.Empty(vm.SelectedRows);
        Assert.Equal("删除这条记录", vm.DeleteMenuText);
        // 其余行一行没少，后面补上来 3 行，序号仍然连续。
        Assert.Equal(before.Except(targets.Select(row => row.RowId)), vm.Rows.Select(row => row.RowId).Take(97));
        Assert.Equal(Enumerable.Range(101, 100).Select(value => (long)value), vm.Rows.Select(row => row.DisplayOrdinal));
    }

    [Fact]
    public async Task SelectingAllConflictsAndDeletingThemClearsTheFilterInOneStep()
    {
        var (vm, draft, _) = await CreateAsync(rows: 250);
        draft.MarkConflict(3, 77, 140, 141, 249);
        vm.ShowConflictsOnly = true;
        Assert.Equal(5, vm.Rows.Count);

        vm.SetSelectedRows(vm.Rows.ToArray());
        await vm.DeleteSelectedRowCommand.ExecuteAsync(null);

        Assert.Equal(5, Assert.Single(draft.DeletedTogether).Length);
        Assert.False(vm.ShowConflictsOnly);
        Assert.Equal("没有冲突记录了，已显示全部记录。", vm.StatusMessage);
        Assert.Equal(245, vm.TotalRows);
        Assert.DoesNotContain(draft.Rows, row => row.Disposition == "Conflict");
    }

    [Fact]
    public async Task EditingIsOnlyOfferedWhileASingleRowIsSelected()
    {
        var (vm, _, _) = await CreateAsync(rows: 5);
        vm.SelectedRow = vm.Rows[0];
        vm.SetSelectedRows([vm.Rows[0]]);
        Assert.True(vm.CanEditSelectedRow);
        Assert.True(vm.SaveSelectedRowCommand.CanExecute(null));
        Assert.True(vm.ToggleSelectedExclusionCommand.CanExecute(null));

        vm.SetSelectedRows([vm.Rows[0], vm.Rows[1]]);

        Assert.False(vm.CanEditSelectedRow);
        Assert.False(vm.SaveSelectedRowCommand.CanExecute(null));
        Assert.False(vm.ToggleSelectedExclusionCommand.CanExecute(null));
        Assert.True(vm.DeleteSelectedRowCommand.CanExecute(null));
    }

    [Fact]
    public async Task AFailedBatchDeleteRemovesNothingAndSaysWhy()
    {
        var (vm, draft, _) = await CreateAsync(rows: 5);
        draft.DeleteError = new BaiduBatchTransfer.Application.Imports.DraftWorkflowException("DRAFT_ROW_NOT_FOUND", "x");
        vm.SetSelectedRows([vm.Rows[0], vm.Rows[1]]);

        await vm.DeleteSelectedRowCommand.ExecuteAsync(null);

        Assert.Equal(5, draft.Rows.Count);
        Assert.Equal(5, vm.Rows.Count);
        Assert.Equal("无法删除记录：这条记录已不在当前导入中，请点「刷新」。（DRAFT_ROW_NOT_FOUND）", vm.StatusMessage);
    }

    [Fact]
    public async Task SelectionChangedWhileConfirmingDeletesNothing()
    {
        var (vm, draft, dialog) = await CreateAsync(rows: 5);
        vm.SetSelectedRows([vm.Rows[0], vm.Rows[1]]);
        dialog.OnConfirm = () => vm.SetSelectedRows([vm.Rows[3]]);

        await vm.DeleteSelectedRowCommand.ExecuteAsync(null);

        Assert.Empty(draft.DeletedTogether);
        Assert.Empty(draft.Deleted);
        Assert.Equal(5, draft.Rows.Count);
    }

    [Fact]
    public void InTheRealTableSeveralRowsCanBeSelectedAndDeletedTogether() => TransferPendingSelectionTests.OnSta(() =>
    {
        var draft = new Draft(250);
        using var vm = new ImportViewModel(draft, draft, new Dialog());
        vm.AttachSession(draft.SessionId);
        vm.LoadFirstPageAsync().GetAwaiter().GetResult();
        var view = TransferPendingSelectionTests.LoadPendingTable(vm);
        var grid = TransferPendingSelectionTests.Descendants(view).OfType<System.Windows.Controls.DataGrid>().Single();
        void Pump() => view.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        Assert.Equal(System.Windows.Controls.DataGridSelectionMode.Extended, grid.SelectionMode);
        // 这里只加载了 XAML，选中项回报给界面模型的那段代码要手动接上（真实窗口由 TransferWorkspaceView 接）。
        BaiduBatchTransfer.App.Features.Transfers.PendingRowContextMenu.Attach(grid, vm.SetSelectedRows);

        grid.SelectedItems.Add(vm.Rows[1]);
        grid.SelectedItems.Add(vm.Rows[4]);
        grid.SelectedItems.Add(vm.Rows[9]);
        Pump();
        var chosen = new[] { vm.Rows[1].RowId, vm.Rows[4].RowId, vm.Rows[9].RowId };
        Assert.Equal(chosen, vm.SelectedRows.Select(row => row.RowId));
        Assert.Equal("删除所选 3 条记录", vm.DeleteMenuText);

        vm.DeleteSelectedRowCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Pump();
        Assert.Equal([chosen], draft.DeletedTogether);
        Assert.Empty(grid.SelectedItems);
        Assert.Empty(vm.SelectedRows);

        // 删完再单选一行删除，落在所选的那一行上。
        var next = vm.Rows[0];
        grid.SelectedItem = next;
        Pump();
        vm.DeleteSelectedRowCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Pump();
        Assert.Equal([next.RowId], draft.Deleted);
    });

    private static async Task<(ImportViewModel Model, Draft Draft, Dialog Dialog)> CreateAsync(int rows)
    {
        var draft = new Draft(rows);
        var dialog = new Dialog();
        var vm = new ImportViewModel(draft, draft, dialog);
        vm.AttachSession(draft.SessionId);
        await vm.LoadFirstPageAsync();
        return (vm, draft, dialog);
    }

    private static string Snapshot(ImportViewModel vm) =>
        string.Join("|", vm.Rows.Select(row => $"{row.DisplayOrdinal}:{row.RowId:N}"));

    /// <summary>一份内存里的导入草稿，按来源行号分页，删除后总数随之变化。</summary>
    private sealed class Draft : IImportWorkflow, IImportPreviewPageSource
    {
        public Draft(int rows) =>
            // 来源行号故意不连续，贴近真实表格里夹着空行的情况。
            Rows = Enumerable.Range(1, rows)
                .Select(index => new ImportPreviewRow(Guid.NewGuid(), index * 2, $"https://pan.baidu.com/s/{index}", "a1b2", null, "就绪", null))
                .ToList();

        public Guid SessionId { get; } = Guid.NewGuid();
        public List<ImportPreviewRow> Rows { get; }
        public List<Guid> Deleted { get; } = [];
        public List<Guid[]> DeletedTogether { get; } = [];
        public bool FailNextLoad { get; set; }
        public bool RejectDelete { get; set; }
        public Exception? DeleteError { get; set; }
        public bool Resumable { get; init; }

        public Task<ImportPreviewPage> LoadAsync(ImportPreviewPageRequest request, CancellationToken cancellationToken)
        {
            if (FailNextLoad)
            {
                FailNextLoad = false;
                throw new InvalidOperationException("Injected page failure.");
            }
            var listed = Rows.Where(row => request.Disposition is null || row.Disposition == request.Disposition).ToArray();
            var remaining = listed.Where(row => row.SourceRowNumber > (request.AfterSourceRowNumber ?? 0)).ToArray();
            var items = remaining.Take(request.PageSize).ToArray();
            return Task.FromResult(new ImportPreviewPage(
                items, listed.Length, remaining.Length > items.Length ? items[^1].SourceRowNumber : null));
        }

        /// <summary>把第 N 条（从 1 数）标成冲突。</summary>
        public void MarkConflict(params int[] ordinals)
        {
            foreach (var ordinal in ordinals)
                Rows[ordinal - 1] = Restate(Rows[ordinal - 1], "冲突待处理", "Conflict");
        }

        private static ImportPreviewRow Restate(ImportPreviewRow row, string text, string disposition) =>
            new(row.RowId, row.SourceRowNumber, row.ShareUrl, row.ExtractionCode, row.RequestedAccountName,
                text, row.PrecheckText, disposition, row.PrecheckStatus);

        public Task<ImportWorkflowResult> DeleteRowAsync(Guid rowId, CancellationToken cancellationToken)
        {
            if (RejectDelete) throw new InvalidOperationException("Injected delete rejection.");
            if (DeleteError is not null) throw DeleteError;
            Assert.Equal(1, Rows.RemoveAll(row => row.RowId == rowId));
            Deleted.Add(rowId);
            return Result();
        }

        public Task<ImportWorkflowResult> DeleteRowsAsync(IReadOnlyList<Guid> rowIds, CancellationToken cancellationToken)
        {
            if (DeleteError is not null) throw DeleteError;
            foreach (var rowId in rowIds) Assert.Equal(1, Rows.RemoveAll(row => row.RowId == rowId));
            DeletedTogether.Add(rowIds.ToArray());
            return Result();
        }

        public Task<ImportWorkflowResult> SetExcludedAsync(Guid rowId, bool excluded, CancellationToken cancellationToken)
        {
            var index = Rows.FindIndex(row => row.RowId == rowId);
            Rows[index] = Restate(Rows[index], excluded ? "已排除" : "就绪", excluded ? "Excluded" : "Ready");
            return Result();
        }
        public Task<ImportWorkflowResult> UpdateLinkAsync(Guid rowId, string shareUrl, string? extractionCode, CancellationToken cancellationToken) => Result();
        public Task<Guid?> FindLatestResumableExcelSessionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<Guid?>(Resumable ? SessionId : null);
        public Task<ImportWorkflowResult> ImportTextAsync(Guid sessionId, string text, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ImportWorkflowResult> ImportExcelAsync(Guid sessionId, string filePath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ImportWorkflowResult> ResumeAsync(Guid sessionId, CancellationToken cancellationToken) => throw new NotSupportedException();

        private Task<ImportWorkflowResult> Result() =>
            Task.FromResult(new ImportWorkflowResult(SessionId, new(Rows.Count, Rows.Count, 0, 0, 0)));
    }

    private sealed class Dialog : IDialogService
    {
        public bool Answer { get; set; } = true;
        public List<string> Confirmations { get; } = [];
        public Action? OnConfirm { get; set; }
        public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default)
        {
            Confirmations.Add(message);
            OnConfirm?.Invoke();
            return Task.FromResult(Answer);
        }
        public Task ShowMessageAsync(string title, string message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string?> PickExcelFileAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }
}
