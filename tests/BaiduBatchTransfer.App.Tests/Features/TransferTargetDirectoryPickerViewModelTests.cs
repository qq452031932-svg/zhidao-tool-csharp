using System.Collections.Immutable;
using BaiduBatchTransfer.App.Features.Transfers;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class TransferTargetDirectoryPickerViewModelTests
{
    [Fact]
    public void PickerWindowProvidesReadOnlyNavigationAndSelectionControls()
    {
        var path = Path.Combine(
            FindRepositoryRoot(),
            "src", "BaiduBatchTransfer.App", "Features", "Transfers",
            "TransferTargetDirectoryWindow.xaml");
        var document = System.Xml.Linq.XDocument.Load(path);

        Assert.Contains(document.Descendants(), element =>
            element.Name.LocalName == "TextBlock" &&
            (string?)element.Attribute("Text") == "{Binding CurrentPath}");
        Assert.Contains(document.Descendants(), element =>
            element.Name.LocalName == "ListBox" &&
            (string?)element.Attribute("ItemsSource") == "{Binding Directories}");
        Assert.Contains(document.Descendants(), element =>
            (string?)element.Attribute("Command") == "{Binding GoToParentCommand}");
        Assert.Contains(document.Descendants(), element =>
            (string?)element.Attribute("Command") == "{Binding EnterSelectedDirectoryCommand}");
        Assert.Contains(document.Descendants(), element =>
            (string?)element.Attribute("Command") == "{Binding SelectCurrentDirectoryCommand}");
        Assert.Contains(document.Descendants(), element =>
            (string?)element.Attribute("Command") == "{Binding CancelCommand}");
        Assert.Contains(document.Descendants(), element =>
            (string?)element.Attribute("BorderBrush") == "{StaticResource PanelBorderBrush}");
        Assert.DoesNotContain(document.Descendants(), element =>
            (string?)element.Attribute("BorderBrush") == "{StaticResource BorderBrush}");
        Assert.DoesNotContain(document.Descendants(), element =>
            element.Name.LocalName == "TextBox");
        var codeBehind = File.ReadAllText(Path.ChangeExtension(path, ".xaml.cs"));
        Assert.Contains("ItemsControl.ContainerFromElement", codeBehind, StringComparison.Ordinal);
        Assert.Contains("is not ListBoxItem", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void PickerWindowProvidesOwnedChineseCreateFolderDialog()
    {
        var directory = Path.Combine(
            FindRepositoryRoot(),
            "src", "BaiduBatchTransfer.App", "Features", "Transfers");
        var pickerPath = Path.Combine(directory, "TransferTargetDirectoryWindow.xaml");
        var picker = System.Xml.Linq.XDocument.Load(pickerPath);
        Assert.Contains(picker.Descendants(), element =>
            element.Name.LocalName == "Button" &&
            (string?)element.Attribute("Content") == "新建文件夹" &&
            (string?)element.Attribute("Click") == "CreateFolder_Click");

        var dialogPath = Path.Combine(directory, "TransferFolderNameWindow.xaml");
        var dialog = System.Xml.Linq.XDocument.Load(dialogPath);
        Assert.Contains(dialog.Descendants(), element =>
            element.Name.LocalName == "TextBox" &&
            (string?)element.Attribute(System.Xml.Linq.XNamespace.Get(
                "http://schemas.microsoft.com/winfx/2006/xaml") + "Name") == "FolderNameInput");
        Assert.Contains(dialog.Descendants(), element =>
            element.Name.LocalName == "Button" &&
            (string?)element.Attribute("Content") == "取消" &&
            (string?)element.Attribute("IsCancel") == "True");
        Assert.Contains(dialog.Descendants(), element =>
            element.Name.LocalName == "Button" &&
            (string?)element.Attribute("Content") == "创建" &&
            (string?)element.Attribute("IsDefault") == "True");

        var codeBehind = File.ReadAllText(Path.ChangeExtension(pickerPath, ".xaml.cs"));
        Assert.Contains("new TransferFolderNameWindow", codeBehind, StringComparison.Ordinal);
        Assert.Contains("Owner = this", codeBehind, StringComparison.Ordinal);
        Assert.Contains("await viewModel.CreateFolderAsync(dialog.FolderName)", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SourceUsesCanonicalAccountKeyAndSortsDirectoriesNaturally()
    {
        var accountId = Guid.Parse("6cfd071c-a2eb-4e4d-82f2-00367c03982a");
        var session = Session(accountId);
        var store = new RecordingSessionStore(session);
        var adapter = new RecordingAdapter(BaiduAdapterResult<ImmutableArray<RemoteDirectory>>.Success(
        [
            new("10", new RemotePath("/folder10")),
            new("2", new RemotePath("/folder2")),
            new("1", new RemotePath("/folder1")),
        ]));
        var source = new BaiduTransferTargetDirectorySource(store, adapter);

        var result = await source.LoadAsync(accountId, "/", CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(accountId.ToString("D"), store.LoadedAccountId);
        Assert.Same(session, adapter.Session);
        Assert.Equal("/", adapter.Path?.Value);
        Assert.Equal(["folder1", "folder2", "folder10"], result.Directories.Select(item => item.DisplayName));
    }

    [Fact]
    public async Task SourceReturnsShortErrorWhenSessionIsMissingWithoutCallingAdapter()
    {
        var adapter = new RecordingAdapter(
            BaiduAdapterResult<ImmutableArray<RemoteDirectory>>.Success([]));
        var source = new BaiduTransferTargetDirectorySource(new RecordingSessionStore(null), adapter);

        var result = await source.LoadAsync(Guid.NewGuid(), "/", CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("登录会话不可用，请重新登录。", result.ErrorMessage);
        Assert.Equal(0, adapter.ListCalls);
    }

    [Fact]
    public async Task SourceRejectsSessionBoundToDifferentAccountWithoutCallingAdapter()
    {
        var requestedAccountId = Guid.NewGuid();
        var adapter = new RecordingAdapter(
            BaiduAdapterResult<ImmutableArray<RemoteDirectory>>.Success([]));
        var source = new BaiduTransferTargetDirectorySource(
            new RecordingSessionStore(Session(Guid.NewGuid())),
            adapter);

        var result = await source.LoadAsync(requestedAccountId, "/", CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("登录会话与所选账号不匹配，请重新登录。", result.ErrorMessage);
        Assert.Equal(0, adapter.ListCalls);
    }

    [Fact]
    public async Task SourceHidesAdapterDiagnosticOnFailure()
    {
        var accountId = Guid.NewGuid();
        var source = new BaiduTransferTargetDirectorySource(
            new RecordingSessionStore(Session(accountId)),
            new RecordingAdapter(BaiduAdapterResult<ImmutableArray<RemoteDirectory>>.Failure(
                BaiduErrorKind.UnknownResponse,
                "secret-adapter-diagnostic")));

        var result = await source.LoadAsync(accountId, "/", CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("目录读取失败，请稍后重试。", result.ErrorMessage);
        Assert.DoesNotContain("secret", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreatorTrimsNameAndCreatesCanonicalDirectChildWithCurrentBoundSession()
    {
        var accountId = Guid.NewGuid();
        var session = Session(accountId);
        var adapter = new DirectoryCreationAdapter();
        var creator = new BaiduTransferTargetDirectoryCreator(
            new RecordingSessionStore(session), adapter);

        var result = await creator.CreateAsync(
            accountId, "\\父目录\\当前目录\\", "  新目录  ", CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("/父目录/当前目录/新目录", result.CreatedPath);
        Assert.Same(session, adapter.EnsureSession);
        Assert.Equal("/父目录/当前目录/新目录", adapter.EnsurePath?.Value);
        Assert.Equal(1, adapter.EnsureCalls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("子/目录")]
    [InlineData("子\\目录")]
    [InlineData("子\u0001目录")]
    public async Task CreatorRejectsInvalidSingleSegmentNamesBeforeLoadingSessionOrWriting(string name)
    {
        var store = new RecordingSessionStore(null);
        var adapter = new DirectoryCreationAdapter();
        var creator = new BaiduTransferTargetDirectoryCreator(store, adapter);

        var result = await creator.CreateAsync(
            Guid.NewGuid(), "/父目录", name, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("文件夹名称无效，请更换名称。", result.ErrorMessage);
        Assert.Null(store.LoadedAccountId);
        Assert.Equal(0, adapter.EnsureCalls);
    }

    [Fact]
    public async Task CreatorCountsUnicodeScalarsRatherThanUtf16CodeUnits()
    {
        var accountId = Guid.NewGuid();
        var adapter = new DirectoryCreationAdapter();
        var creator = new BaiduTransferTargetDirectoryCreator(
            new RecordingSessionStore(Session(accountId)), adapter);

        var accepted = await creator.CreateAsync(
            accountId, "/", string.Concat(Enumerable.Repeat("😀", 255)), CancellationToken.None);
        var rejected = await creator.CreateAsync(
            accountId, "/", string.Concat(Enumerable.Repeat("😀", 256)), CancellationToken.None);

        Assert.True(accepted.IsSuccess);
        Assert.Equal(1, adapter.EnsureCalls);
        Assert.False(rejected.IsSuccess);
        Assert.Equal("文件夹名称无效，请更换名称。", rejected.ErrorMessage);
    }

    [Theory]
    [InlineData("/父目录/..")]
    [InlineData("/父目录/./子目录")]
    public async Task CreatorRejectsInvalidOrTraversalParentBeforeWriting(string parentPath)
    {
        var adapter = new DirectoryCreationAdapter();
        var creator = new BaiduTransferTargetDirectoryCreator(
            new RecordingSessionStore(null), adapter);

        var result = await creator.CreateAsync(
            Guid.NewGuid(), parentPath, "新目录", CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("文件夹名称无效，请更换名称。", result.ErrorMessage);
        Assert.Equal(0, adapter.EnsureCalls);
    }

    [Fact]
    public async Task CreatorFailsClosedOnMissingOrMismatchedSessionWithoutWriting()
    {
        var accountId = Guid.NewGuid();
        var missingAdapter = new DirectoryCreationAdapter();
        var missing = await new BaiduTransferTargetDirectoryCreator(
                new RecordingSessionStore(null), missingAdapter)
            .CreateAsync(accountId, "/", "新目录", CancellationToken.None);
        var mismatchAdapter = new DirectoryCreationAdapter();
        var mismatch = await new BaiduTransferTargetDirectoryCreator(
                new RecordingSessionStore(Session(Guid.NewGuid())), mismatchAdapter)
            .CreateAsync(accountId, "/", "新目录", CancellationToken.None);

        Assert.Equal("登录会话不可用，请重新登录。", missing.ErrorMessage);
        Assert.Equal("登录会话与所选账号不匹配，请重新登录。", mismatch.ErrorMessage);
        Assert.Equal(0, missingAdapter.EnsureCalls);
        Assert.Equal(0, mismatchAdapter.EnsureCalls);
    }

    [Fact]
    public async Task CreatorTreatsExistingDirectoryAsSuccessfulEnsure()
    {
        var accountId = Guid.NewGuid();
        var adapter = new DirectoryCreationAdapter
        {
            EnsureResult = BaiduAdapterResult<RemoteDirectory>.Success(
                new RemoteDirectory("existing", new RemotePath("/父目录/已有"))),
        };
        var creator = new BaiduTransferTargetDirectoryCreator(
            new RecordingSessionStore(Session(accountId)), adapter);

        var result = await creator.CreateAsync(
            accountId, "/父目录", "已有", CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("/父目录/已有", result.CreatedPath);
    }

    [Theory]
    [InlineData(BaiduErrorKind.InvalidTargetPath, "无法创建文件夹，请更换名称。")]
    [InlineData(BaiduErrorKind.ExpiredLogin, "登录已失效，请重新登录。")]
    [InlineData(BaiduErrorKind.VerificationRequired, "登录已失效，请重新登录。")]
    [InlineData(BaiduErrorKind.TransientService, "文件夹创建失败，请稍后重试。")]
    [InlineData(BaiduErrorKind.UnknownResponse, "文件夹创建失败，请稍后重试。")]
    [InlineData(BaiduErrorKind.NotValidated, "文件夹创建失败，请稍后重试。")]
    public async Task CreatorMapsAdapterFailuresToStableSafeChineseErrors(
        BaiduErrorKind errorKind,
        string expectedMessage)
    {
        var accountId = Guid.NewGuid();
        var adapter = new DirectoryCreationAdapter
        {
            EnsureResult = BaiduAdapterResult<RemoteDirectory>.Failure(
                errorKind, "secret adapter diagnostic"),
        };
        var creator = new BaiduTransferTargetDirectoryCreator(
            new RecordingSessionStore(Session(accountId)), adapter);

        var result = await creator.CreateAsync(
            accountId, "/", "新目录", CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(expectedMessage, result.ErrorMessage);
        Assert.DoesNotContain("secret", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreatorHonorsCancellationBeforeRemoteWrite()
    {
        var accountId = Guid.NewGuid();
        var adapter = new DirectoryCreationAdapter();
        var creator = new BaiduTransferTargetDirectoryCreator(
            new RecordingSessionStore(Session(accountId)), adapter);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => creator.CreateAsync(
            accountId, "/", "新目录", cancellation.Token));

        Assert.Equal(0, adapter.EnsureCalls);
    }

    [Fact]
    public async Task CreatorIgnoresSuccessfulResultWhenCancellationArrivesAfterRemoteCommit()
    {
        var accountId = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();
        var adapter = new DirectoryCreationAdapter { CancelAfterEnsure = cancellation };
        var creator = new BaiduTransferTargetDirectoryCreator(
            new RecordingSessionStore(Session(accountId)), adapter);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => creator.CreateAsync(
            accountId, "/", "新目录", cancellation.Token));

        Assert.Equal(1, adapter.EnsureCalls);
    }

    [Fact]
    public async Task PickerLoadsRootEntersChildAndReturnsToParent()
    {
        var source = new PathDirectorySource(new Dictionary<string, IReadOnlyList<TransferTargetDirectoryEntry>>
        {
            ["/"] = [new("父目录", "/父目录")],
            ["/父目录"] = [new("子目录", "/父目录/子目录")],
        });
        using var viewModel = new TransferTargetDirectoryPickerViewModel(Guid.NewGuid(), "/", source);

        await viewModel.InitializeAsync();
        Assert.Equal("/", viewModel.CurrentPath);
        Assert.False(viewModel.GoToParentCommand.CanExecute(null));
        var child = Assert.Single(viewModel.Directories);

        await viewModel.EnterChildAsync(child);
        Assert.Equal("/父目录", viewModel.CurrentPath);
        Assert.True(viewModel.GoToParentCommand.CanExecute(null));

        await viewModel.GoToParentAsync();
        Assert.Equal("/", viewModel.CurrentPath);
        Assert.False(viewModel.GoToParentCommand.CanExecute(null));
    }

    [Fact]
    public async Task PickerShowsReadErrorWithoutChangingCurrentPath()
    {
        var source = new PathDirectorySource(new Dictionary<string, IReadOnlyList<TransferTargetDirectoryEntry>>
        {
            ["/"] = [new("失败目录", "/失败目录")],
        }) { ErrorPath = "/失败目录" };
        using var viewModel = new TransferTargetDirectoryPickerViewModel(Guid.NewGuid(), "/", source);
        await viewModel.InitializeAsync();

        await viewModel.EnterChildAsync(Assert.Single(viewModel.Directories));

        Assert.Equal("/", viewModel.CurrentPath);
        Assert.Equal("目录读取失败，请稍后重试。", viewModel.ErrorMessage);
    }

    [Fact]
    public async Task PickerConvertsUnexpectedSourceFailureToSafeError()
    {
        using var viewModel = new TransferTargetDirectoryPickerViewModel(
            Guid.NewGuid(), "/", new ThrowingDirectorySource());

        await viewModel.InitializeAsync();

        Assert.Equal("/", viewModel.CurrentPath);
        Assert.Equal("目录读取失败，请稍后重试。", viewModel.ErrorMessage);
        Assert.False(viewModel.IsLoading);
    }

    [Fact]
    public async Task NewerNavigationSuppressesStaleResponseEvenWhenSourceIgnoresCancellation()
    {
        var source = new ControlledDirectorySource();
        using var viewModel = new TransferTargetDirectoryPickerViewModel(Guid.NewGuid(), "/", source);

        var older = viewModel.NavigateToAsync("/较早");
        await source.WaitForCallAsync(0);
        var newer = viewModel.NavigateToAsync("/较新");
        await source.WaitForCallAsync(1);
        source.Complete(1, TransferTargetDirectoryLoadResult.Success([new("新结果", "/较新/新结果")]));
        await newer;
        source.Complete(0, TransferTargetDirectoryLoadResult.Success([new("旧结果", "/较早/旧结果")]));
        await older;

        Assert.Equal("/较新", viewModel.CurrentPath);
        Assert.Equal("新结果", Assert.Single(viewModel.Directories).DisplayName);
        Assert.True(source.Tokens[0].IsCancellationRequested);
    }

    [Fact]
    public async Task NavigationCommandsAreDisabledWhileLoading()
    {
        var source = new ControlledDirectorySource();
        using var viewModel = new TransferTargetDirectoryPickerViewModel(Guid.NewGuid(), "/child", source);

        var loading = viewModel.InitializeAsync();
        await source.WaitForCallAsync(0);

        Assert.True(viewModel.IsLoading);
        Assert.False(viewModel.GoToParentCommand.CanExecute(null));
        Assert.False(viewModel.EnterSelectedDirectoryCommand.CanExecute(null));
        source.Complete(0, TransferTargetDirectoryLoadResult.Success([new("sub", "/child/sub")]));
        await loading;

        viewModel.SelectedDirectory = Assert.Single(viewModel.Directories);
        Assert.True(viewModel.GoToParentCommand.CanExecute(null));
        Assert.True(viewModel.EnterSelectedDirectoryCommand.CanExecute(null));
    }

    [Fact]
    public async Task ClosingPickerCancelsOutstandingReadAndCancelChoosesNothing()
    {
        var source = new ControlledDirectorySource();
        using var viewModel = new TransferTargetDirectoryPickerViewModel(Guid.NewGuid(), "/", source);
        string? chosen = "unchanged";
        viewModel.Completed += (_, path) => chosen = path;
        var loading = viewModel.InitializeAsync();
        await source.WaitForCallAsync(0);

        viewModel.CancelCommand.Execute(null);
        source.Complete(0, TransferTargetDirectoryLoadResult.Success([]));
        await loading;

        Assert.Null(chosen);
        Assert.True(source.Tokens[0].IsCancellationRequested);
    }

    [Fact]
    public async Task SelectingCurrentDirectoryReturnsCanonicalPath()
    {
        using var viewModel = new TransferTargetDirectoryPickerViewModel(
            Guid.NewGuid(), "\\parent\\child\\", new PathDirectorySource(
                new Dictionary<string, IReadOnlyList<TransferTargetDirectoryEntry>>()));
        string? chosen = null;
        viewModel.Completed += (_, path) => chosen = path;
        await viewModel.InitializeAsync();

        viewModel.SelectCurrentDirectoryCommand.Execute(null);

        Assert.Equal("/parent/child", chosen);
    }

    [Fact]
    public async Task CreateSuccessEntersChildWithoutCompletingPicker()
    {
        var accountId = Guid.NewGuid();
        var source = new PathDirectorySource(new Dictionary<string, IReadOnlyList<TransferTargetDirectoryEntry>>
        {
            ["/父目录"] = [new("已有", "/父目录/已有")],
            ["/父目录/新目录"] = [new("孙目录", "/父目录/新目录/孙目录")],
        });
        var creator = new RecordingDirectoryCreator(
            TransferTargetDirectoryCreateResult.Success("/父目录/新目录"));
        using var viewModel = new TransferTargetDirectoryPickerViewModel(
            accountId, "/父目录", source, creator);
        string? completed = null;
        var completionCount = 0;
        viewModel.Completed += (_, path) => { completed = path; completionCount++; };
        await viewModel.InitializeAsync();

        await viewModel.CreateFolderAsync(" 新目录 ");

        Assert.Equal(accountId, creator.AccountId);
        Assert.Equal("/父目录", creator.ParentPath);
        Assert.Equal(" 新目录 ", creator.FolderName);
        Assert.Equal("/父目录/新目录", viewModel.CurrentPath);
        Assert.Equal("孙目录", Assert.Single(viewModel.Directories).DisplayName);
        Assert.Equal(0, completionCount);
        Assert.Null(completed);
    }

    [Fact]
    public async Task ExistingDirectoryEnsureSuccessAlsoEntersChild()
    {
        var source = new PathDirectorySource(new Dictionary<string, IReadOnlyList<TransferTargetDirectoryEntry>>
        {
            ["/"] = [],
            ["/已有"] = [],
        });
        using var viewModel = new TransferTargetDirectoryPickerViewModel(
            Guid.NewGuid(), "/", source,
            new RecordingDirectoryCreator(TransferTargetDirectoryCreateResult.Success("/已有")));
        await viewModel.InitializeAsync();

        await viewModel.CreateFolderAsync("已有");

        Assert.Equal("/已有", viewModel.CurrentPath);
        Assert.Equal(string.Empty, viewModel.ErrorMessage);
    }

    [Fact]
    public async Task CreateFailurePreservesPathDirectoriesAndPriorSelection()
    {
        var selected = new TransferTargetDirectoryEntry("已有", "/父目录/已有");
        var source = new PathDirectorySource(new Dictionary<string, IReadOnlyList<TransferTargetDirectoryEntry>>
        {
            ["/父目录"] = [selected],
        });
        using var viewModel = new TransferTargetDirectoryPickerViewModel(
            Guid.NewGuid(), "/父目录", source,
            new RecordingDirectoryCreator(
                TransferTargetDirectoryCreateResult.Failure("无法创建文件夹，请更换名称。")));
        await viewModel.InitializeAsync();
        viewModel.SelectedDirectory = Assert.Single(viewModel.Directories);

        await viewModel.CreateFolderAsync("冲突");

        Assert.Equal("/父目录", viewModel.CurrentPath);
        Assert.Equal("已有", Assert.Single(viewModel.Directories).DisplayName);
        Assert.Same(viewModel.Directories[0], viewModel.SelectedDirectory);
        Assert.Equal("无法创建文件夹，请更换名称。", viewModel.ErrorMessage);
    }

    [Fact]
    public async Task CreatedFolderThatCannotBeOpenedStaysAtParentWithExactMessage()
    {
        var source = new PathDirectorySource(new Dictionary<string, IReadOnlyList<TransferTargetDirectoryEntry>>
        {
            ["/父目录"] = [new("已有", "/父目录/已有")],
        }) { ErrorPath = "/父目录/新目录" };
        using var viewModel = new TransferTargetDirectoryPickerViewModel(
            Guid.NewGuid(), "/父目录", source,
            new RecordingDirectoryCreator(
                TransferTargetDirectoryCreateResult.Success("/父目录/新目录")));
        await viewModel.InitializeAsync();
        viewModel.SelectedDirectory = Assert.Single(viewModel.Directories);

        await viewModel.CreateFolderAsync("新目录");

        Assert.Equal("/父目录", viewModel.CurrentPath);
        Assert.Equal("已有", Assert.Single(viewModel.Directories).DisplayName);
        Assert.NotNull(viewModel.SelectedDirectory);
        Assert.Equal("文件夹已创建，但暂时无法打开，请刷新后重试。", viewModel.ErrorMessage);
    }

    [Fact]
    public async Task CreateBusyStateDisablesNavigationSelectionAndRepeatCreation()
    {
        var source = new PathDirectorySource(new Dictionary<string, IReadOnlyList<TransferTargetDirectoryEntry>>
        {
            ["/父目录"] = [new("已有", "/父目录/已有")],
            ["/父目录/新目录"] = [],
        });
        var creator = new ControlledDirectoryCreator();
        using var viewModel = new TransferTargetDirectoryPickerViewModel(
            Guid.NewGuid(), "/父目录", source, creator);
        await viewModel.InitializeAsync();
        viewModel.SelectedDirectory = Assert.Single(viewModel.Directories);

        var creating = viewModel.CreateFolderAsync("新目录");
        await creator.WaitForCallAsync();

        Assert.True(viewModel.IsCreating);
        Assert.False(viewModel.CanCreateFolder);
        Assert.False(viewModel.GoToParentCommand.CanExecute(null));
        Assert.False(viewModel.EnterSelectedDirectoryCommand.CanExecute(null));
        Assert.False(viewModel.SelectCurrentDirectoryCommand.CanExecute(null));
        await viewModel.CreateFolderAsync("重复");
        Assert.Equal(1, creator.Calls);

        creator.Complete(TransferTargetDirectoryCreateResult.Success("/父目录/新目录"));
        await creating;
        Assert.False(viewModel.IsCreating);
        Assert.True(viewModel.CanCreateFolder);
    }

    [Fact]
    public async Task ClosingPickerCancelsCreateAndIgnoresLateSuccess()
    {
        var source = new PathDirectorySource(new Dictionary<string, IReadOnlyList<TransferTargetDirectoryEntry>>
        {
            ["/父目录"] = [],
            ["/父目录/新目录"] = [],
        });
        var creator = new ControlledDirectoryCreator();
        using var viewModel = new TransferTargetDirectoryPickerViewModel(
            Guid.NewGuid(), "/父目录", source, creator);
        await viewModel.InitializeAsync();

        var creating = viewModel.CreateFolderAsync("新目录");
        await creator.WaitForCallAsync();
        viewModel.Close();
        creator.Complete(TransferTargetDirectoryCreateResult.Success("/父目录/新目录"));
        await creating;

        Assert.True(creator.Token.IsCancellationRequested);
        Assert.Equal("/父目录", viewModel.CurrentPath);
    }

    [Fact]
    public async Task CancellationAfterCommittedCreateLeavesPickerAtParent()
    {
        var source = new PathDirectorySource(new Dictionary<string, IReadOnlyList<TransferTargetDirectoryEntry>>
        {
            ["/父目录"] = [],
            ["/父目录/新目录"] = [],
        });
        using var cancellation = new CancellationTokenSource();
        var creator = new RecordingDirectoryCreator(
            TransferTargetDirectoryCreateResult.Success("/父目录/新目录"))
        {
            CancelBeforeReturning = cancellation,
        };
        using var viewModel = new TransferTargetDirectoryPickerViewModel(
            Guid.NewGuid(), "/父目录", source, creator);
        await viewModel.InitializeAsync();

        await viewModel.CreateFolderAsync("新目录", cancellation.Token);

        Assert.Equal(1, creator.Calls);
        Assert.Equal("/父目录", viewModel.CurrentPath);
    }

    [Fact]
    public async Task NavigationGenerationChangeSuppressesStaleCreateResponse()
    {
        var source = new PathDirectorySource(new Dictionary<string, IReadOnlyList<TransferTargetDirectoryEntry>>
        {
            ["/父目录"] = [],
            ["/其它目录"] = [new("其它子目录", "/其它目录/其它子目录")],
            ["/父目录/新目录"] = [],
        });
        var creator = new ControlledDirectoryCreator();
        using var viewModel = new TransferTargetDirectoryPickerViewModel(
            Guid.NewGuid(), "/父目录", source, creator);
        await viewModel.InitializeAsync();

        var creating = viewModel.CreateFolderAsync("新目录");
        await creator.WaitForCallAsync();
        await viewModel.NavigateToAsync("/其它目录");
        creator.Complete(TransferTargetDirectoryCreateResult.Success("/父目录/新目录"));
        await creating;

        Assert.Equal("/其它目录", viewModel.CurrentPath);
        Assert.Equal("其它子目录", Assert.Single(viewModel.Directories).DisplayName);
    }

    private static AccountSession Session(Guid accountId) => new(
        accountId,
        "baidu-user",
        [new SessionCookie("BDUSS", "value", ".baidu.com", "/", null, true, true)],
        DateTimeOffset.UtcNow);

    private sealed class RecordingSessionStore(AccountSession? session) : IBaiduSessionStore
    {
        public string? LoadedAccountId { get; private set; }

        public Task<AccountSession?> LoadAsync(string accountId, CancellationToken cancellationToken)
        {
            LoadedAccountId = accountId;
            return Task.FromResult(session);
        }

        public Task SaveAsync(AccountSession value, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RemoveAsync(string accountId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingAdapter(BaiduAdapterResult<ImmutableArray<RemoteDirectory>> result)
        : IBaiduNetdiskAdapter
    {
        public AccountSession? Session { get; private set; }
        public RemotePath? Path { get; private set; }
        public int ListCalls { get; private set; }

        public Task<BaiduAdapterResult<ImmutableArray<RemoteDirectory>>> ListDirectoriesAsync(
            AccountSession session, RemotePath path, CancellationToken cancellationToken)
        {
            Session = session;
            Path = path;
            ListCalls++;
            return Task.FromResult(result);
        }

        public Task<BaiduAdapterResult<BaiduAccountIdentity>> ValidateSessionAsync(AccountSession session, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<AccountCapacity>> GetCapacityAsync(AccountSession session, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<RemoteDirectory>> EnsureDirectoryAsync(AccountSession session, RemotePath path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<ShareMetadata>> InspectShareAsync(AccountSession session, ShareReference share, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<TransferReceipt>> SaveWholeShareAsync(AccountSession session, ShareReference share, RemoteDirectory destination, DuplicatePolicy duplicatePolicy, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<TransferConfirmation>> ConfirmTransferAsync(AccountSession session, TransferConfirmationQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class DirectoryCreationAdapter : IBaiduNetdiskAdapter
    {
        public BaiduAdapterResult<RemoteDirectory> EnsureResult { get; init; } =
            BaiduAdapterResult<RemoteDirectory>.Success(
                new RemoteDirectory("created", new RemotePath("/created")));
        public CancellationTokenSource? CancelAfterEnsure { get; init; }
        public AccountSession? EnsureSession { get; private set; }
        public RemotePath? EnsurePath { get; private set; }
        public int EnsureCalls { get; private set; }

        public Task<BaiduAdapterResult<RemoteDirectory>> EnsureDirectoryAsync(
            AccountSession session, RemotePath path, CancellationToken cancellationToken)
        {
            EnsureSession = session;
            EnsurePath = path;
            EnsureCalls++;
            CancelAfterEnsure?.Cancel();
            return Task.FromResult(EnsureResult);
        }

        public Task<BaiduAdapterResult<BaiduAccountIdentity>> ValidateSessionAsync(AccountSession session, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<AccountCapacity>> GetCapacityAsync(AccountSession session, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<ImmutableArray<RemoteDirectory>>> ListDirectoriesAsync(AccountSession session, RemotePath path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<ShareMetadata>> InspectShareAsync(AccountSession session, ShareReference share, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<TransferReceipt>> SaveWholeShareAsync(AccountSession session, ShareReference share, RemoteDirectory destination, DuplicatePolicy duplicatePolicy, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<TransferConfirmation>> ConfirmTransferAsync(AccountSession session, TransferConfirmationQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class PathDirectorySource(
        IReadOnlyDictionary<string, IReadOnlyList<TransferTargetDirectoryEntry>> paths)
        : ITransferTargetDirectorySource
    {
        public string? ErrorPath { get; init; }

        public Task<TransferTargetDirectoryLoadResult> LoadAsync(
            Guid accountId, string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Equals(path, ErrorPath, StringComparison.Ordinal)
                ? TransferTargetDirectoryLoadResult.Failure("目录读取失败，请稍后重试。")
                : TransferTargetDirectoryLoadResult.Success(
                    paths.TryGetValue(path, out var entries) ? entries : []));
    }

    private sealed class ControlledDirectorySource : ITransferTargetDirectorySource
    {
        private readonly List<TaskCompletionSource<TransferTargetDirectoryLoadResult>> completions = [];
        private readonly List<TaskCompletionSource> started = [];
        public List<CancellationToken> Tokens { get; } = [];

        public Task<TransferTargetDirectoryLoadResult> LoadAsync(
            Guid accountId, string path, CancellationToken cancellationToken = default)
        {
            var completion = new TaskCompletionSource<TransferTargetDirectoryLoadResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var callStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            completions.Add(completion);
            started.Add(callStarted);
            Tokens.Add(cancellationToken);
            callStarted.TrySetResult();
            return completion.Task;
        }

        public Task WaitForCallAsync(int index) => started.Count > index
            ? started[index].Task
            : WaitUntilCreatedAsync(index);

        public void Complete(int index, TransferTargetDirectoryLoadResult result) =>
            completions[index].TrySetResult(result);

        private async Task WaitUntilCreatedAsync(int index)
        {
            for (var attempt = 0; attempt < 100 && started.Count <= index; attempt++)
                await Task.Yield();
            Assert.True(started.Count > index);
            await started[index].Task;
        }
    }

    private sealed class ThrowingDirectorySource : ITransferTargetDirectorySource
    {
        public Task<TransferTargetDirectoryLoadResult> LoadAsync(
            Guid accountId, string path, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("private source diagnostic");
    }

    private sealed class RecordingDirectoryCreator(TransferTargetDirectoryCreateResult result)
        : ITransferTargetDirectoryCreator
    {
        public int Calls { get; private set; }
        public Guid? AccountId { get; private set; }
        public string? ParentPath { get; private set; }
        public string? FolderName { get; private set; }
        public CancellationTokenSource? CancelBeforeReturning { get; init; }

        public Task<TransferTargetDirectoryCreateResult> CreateAsync(
            Guid accountId,
            string parentPath,
            string folderName,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            AccountId = accountId;
            ParentPath = parentPath;
            FolderName = folderName;
            CancelBeforeReturning?.Cancel();
            return Task.FromResult(result);
        }
    }

    private sealed class ControlledDirectoryCreator : ITransferTargetDirectoryCreator
    {
        private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<TransferTargetDirectoryCreateResult> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Calls { get; private set; }
        public CancellationToken Token { get; private set; }

        public Task<TransferTargetDirectoryCreateResult> CreateAsync(
            Guid accountId,
            string parentPath,
            string folderName,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Token = cancellationToken;
            started.TrySetResult();
            return completion.Task;
        }

        public Task WaitForCallAsync() => started.Task;

        public void Complete(TransferTargetDirectoryCreateResult result) =>
            completion.TrySetResult(result);
    }

    private static string FindRepositoryRoot([System.Runtime.CompilerServices.CallerFilePath] string sourceFilePath = "")
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
}
