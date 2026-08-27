using BaiduBatchTransfer.App.Features.ManagedBrowser;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class BrowserSetupServiceTests
{
    private const string SelectedDirectory = @"C:\Browser\Application";
    private const string SavedDirectory = @"C:\Saved\Browser";

    [Fact]
    public async Task FirstUsePromptsInsteadOfScanning()
    {
        var dialog = new RecordingDialogService(SelectedDirectory);
        var fileSystem = new RecordingBrowserDirectoryFileSystem(
            Path.Combine(SelectedDirectory, "chrome.exe"));
        var state = new RecordingBrowserDirectoryState();

        var result = await CreateService(dialog, fileSystem, state)
            .EnsureSelectedAsync(CancellationToken.None);

        Assert.Equal(BrowserSetupStatus.Selected, result.Status);
        Assert.Equal(BrowserFamily.Chrome, Assert.Single(result.Candidates).BrowserFamily);
        Assert.Equal(1, dialog.BrowserDirectoryShowCount);
        Assert.Equal(SelectedDirectory, state.Directory);
        Assert.Equal(1, state.SaveCount);
        Assert.Equal(ExpectedProbes(SelectedDirectory), fileSystem.AccessedPaths);
    }

    [Fact]
    public async Task UserCancellationDoesNotChangeSavedDirectory()
    {
        var dialog = new RecordingDialogService(null);
        var fileSystem = new RecordingBrowserDirectoryFileSystem();
        var state = new RecordingBrowserDirectoryState(SavedDirectory);

        var result = await CreateService(dialog, fileSystem, state)
            .EnsureSelectedAsync(CancellationToken.None);

        Assert.Equal(BrowserSetupStatus.Cancelled, result.Status);
        Assert.Empty(result.Candidates);
        Assert.Equal(SavedDirectory, state.Directory);
        Assert.Equal(0, state.SaveCount);
        Assert.Equal(ExpectedProbes(SavedDirectory), fileSystem.AccessedPaths);
        Assert.DoesNotContain(SavedDirectory, result.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SavedDirectoryIsReusedWithoutPromptOrRewrite()
    {
        var dialog = new RecordingDialogService(SelectedDirectory);
        var fileSystem = new RecordingBrowserDirectoryFileSystem(
            Path.Combine(SavedDirectory, "msedge.exe"));
        var state = new RecordingBrowserDirectoryState(SavedDirectory);

        var result = await CreateService(dialog, fileSystem, state)
            .EnsureSelectedAsync(CancellationToken.None);

        Assert.Equal(BrowserSetupStatus.Selected, result.Status);
        Assert.Equal(BrowserFamily.Edge, Assert.Single(result.Candidates).BrowserFamily);
        Assert.Equal(0, dialog.BrowserDirectoryShowCount);
        Assert.Equal(0, state.SaveCount);
        Assert.Equal(ExpectedProbes(SavedDirectory), fileSystem.AccessedPaths);
    }

    [Fact]
    public async Task InvalidSavedDirectoryPromptsAndPersistsValidReplacement()
    {
        var dialog = new RecordingDialogService(SelectedDirectory);
        var fileSystem = new RecordingBrowserDirectoryFileSystem(
            Path.Combine(SelectedDirectory, "msedge.exe"));
        var state = new RecordingBrowserDirectoryState(SavedDirectory);

        var result = await CreateService(dialog, fileSystem, state)
            .EnsureSelectedAsync(CancellationToken.None);

        Assert.Equal(BrowserSetupStatus.Selected, result.Status);
        Assert.Equal(BrowserFamily.Edge, Assert.Single(result.Candidates).BrowserFamily);
        Assert.Equal(1, dialog.BrowserDirectoryShowCount);
        Assert.Equal(SelectedDirectory, state.Directory);
        Assert.Equal(1, state.SaveCount);
        Assert.Equal(
            ExpectedProbes(SavedDirectory).Concat(ExpectedProbes(SelectedDirectory)),
            fileSystem.AccessedPaths);
    }

    [Fact]
    public async Task ExplicitReselectPromptsWithoutProbingSavedDirectory()
    {
        var dialog = new RecordingDialogService(SelectedDirectory);
        var fileSystem = new RecordingBrowserDirectoryFileSystem(
            Path.Combine(SavedDirectory, "chrome.exe"),
            Path.Combine(SelectedDirectory, "msedge.exe"));
        var state = new RecordingBrowserDirectoryState(SavedDirectory);

        var result = await CreateService(dialog, fileSystem, state)
            .ReselectAsync(CancellationToken.None);

        Assert.Equal(BrowserSetupStatus.Selected, result.Status);
        Assert.Equal(BrowserFamily.Edge, Assert.Single(result.Candidates).BrowserFamily);
        Assert.Equal(1, dialog.BrowserDirectoryShowCount);
        Assert.Equal(SelectedDirectory, state.Directory);
        Assert.Equal(ExpectedProbes(SelectedDirectory), fileSystem.AccessedPaths);
    }

    [Fact]
    public async Task DirectoryWithoutAllowedExecutablesDoesNotOverwriteSavedValue()
    {
        var dialog = new RecordingDialogService(SelectedDirectory);
        var fileSystem = new RecordingBrowserDirectoryFileSystem();
        var state = new RecordingBrowserDirectoryState(SavedDirectory);

        var result = await CreateService(dialog, fileSystem, state)
            .ReselectAsync(CancellationToken.None);

        Assert.Equal(BrowserSetupStatus.NoSupportedBrowser, result.Status);
        Assert.Empty(result.Candidates);
        Assert.Equal(SavedDirectory, state.Directory);
        Assert.Equal(0, state.SaveCount);
        Assert.Equal(ExpectedProbes(SelectedDirectory), fileSystem.AccessedPaths);
        Assert.DoesNotContain(SelectedDirectory, result.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DirectoryWithBothExecutablesRequiresCallerToChooseFamily()
    {
        var dialog = new RecordingDialogService(SelectedDirectory);
        var fileSystem = new RecordingBrowserDirectoryFileSystem(
            Path.Combine(SelectedDirectory, "chrome.exe"),
            Path.Combine(SelectedDirectory, "msedge.exe"));
        var state = new RecordingBrowserDirectoryState();

        var result = await CreateService(dialog, fileSystem, state)
            .EnsureSelectedAsync(CancellationToken.None);

        Assert.Equal(BrowserSetupStatus.BrowserFamilyChoiceRequired, result.Status);
        Assert.Equal([BrowserFamily.Chrome, BrowserFamily.Edge],
            result.Candidates.Select(candidate => candidate.BrowserFamily));
        Assert.Equal(SelectedDirectory, state.Directory);
        Assert.Equal(1, state.SaveCount);
        Assert.Equal(ExpectedProbes(SelectedDirectory), fileSystem.AccessedPaths);
        Assert.DoesNotContain(SelectedDirectory, result.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.All(result.Candidates, candidate =>
            Assert.DoesNotContain(SelectedDirectory, candidate.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task InvalidSelectedPathReturnsSafeTypedResultWithoutFilesystemAccess()
    {
        const string privateRelativePath = @"private\browser";
        var dialog = new RecordingDialogService(privateRelativePath);
        var fileSystem = new RecordingBrowserDirectoryFileSystem();
        var state = new RecordingBrowserDirectoryState(SavedDirectory);

        var result = await CreateService(dialog, fileSystem, state)
            .ReselectAsync(CancellationToken.None);

        Assert.Equal(BrowserSetupStatus.InvalidDirectory, result.Status);
        Assert.Empty(fileSystem.AccessedPaths);
        Assert.Equal(SavedDirectory, state.Directory);
        Assert.DoesNotContain(privateRelativePath, result.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FilesystemFailureReturnsSafeTypedResultWithoutChangingSavedValue()
    {
        var dialog = new RecordingDialogService(SelectedDirectory);
        var fileSystem = new RecordingBrowserDirectoryFileSystem
        {
            Failure = new UnauthorizedAccessException(SelectedDirectory),
        };
        var state = new RecordingBrowserDirectoryState(SavedDirectory);

        var result = await CreateService(dialog, fileSystem, state)
            .ReselectAsync(CancellationToken.None);

        Assert.Equal(BrowserSetupStatus.DirectoryUnavailable, result.Status);
        Assert.Equal(SavedDirectory, state.Directory);
        Assert.Equal(0, state.SaveCount);
        Assert.DoesNotContain(SelectedDirectory, result.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancellationPropagatesBeforeAnySideEffect()
    {
        var dialog = new RecordingDialogService(SelectedDirectory);
        var fileSystem = new RecordingBrowserDirectoryFileSystem(
            Path.Combine(SelectedDirectory, "chrome.exe"));
        var state = new RecordingBrowserDirectoryState();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateService(dialog, fileSystem, state).EnsureSelectedAsync(cancellation.Token));

        Assert.Equal(0, dialog.BrowserDirectoryShowCount);
        Assert.Empty(fileSystem.AccessedPaths);
        Assert.Equal(0, state.LoadCount);
        Assert.Equal(0, state.SaveCount);
    }

    [Fact]
    public async Task CancellationDuringDialogIsNotReportedAsUserCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var dialog = new RecordingDialogService(null, cancellation.Cancel);
        var fileSystem = new RecordingBrowserDirectoryFileSystem();
        var state = new RecordingBrowserDirectoryState();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateService(dialog, fileSystem, state).EnsureSelectedAsync(cancellation.Token));

        Assert.Equal(1, dialog.BrowserDirectoryShowCount);
        Assert.Empty(fileSystem.AccessedPaths);
        Assert.Equal(0, state.SaveCount);
    }

    private static BrowserSetupService CreateService(
        IDialogService dialog,
        IBrowserDirectoryFileSystem fileSystem,
        IBrowserDirectoryState state) => new(dialog, fileSystem, state);

    private static string[] ExpectedProbes(string directory) =>
        [Path.Combine(directory, "chrome.exe"), Path.Combine(directory, "msedge.exe")];

    private sealed class RecordingDialogService(
        string? selectedDirectory,
        Action? onBrowserDirectoryPick = null) : IDialogService
    {
        public int BrowserDirectoryShowCount { get; private set; }

        public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ShowMessageAsync(string title, string message, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string?> PickExcelFileAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string?> PickBrowserDirectoryAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BrowserDirectoryShowCount++;
            onBrowserDirectoryPick?.Invoke();
            return Task.FromResult(selectedDirectory);
        }
    }

    private sealed class RecordingBrowserDirectoryFileSystem(params string[] existingPaths)
        : IBrowserDirectoryFileSystem
    {
        private readonly HashSet<string> existing = new(existingPaths, StringComparer.OrdinalIgnoreCase);

        public List<string> AccessedPaths { get; } = [];
        public Exception? Failure { get; init; }

        public Task<bool> FileExistsAsync(string exactPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AccessedPaths.Add(exactPath);
            if (Failure is not null) throw Failure;
            return Task.FromResult(existing.Contains(exactPath));
        }
    }

    private sealed class RecordingBrowserDirectoryState(string? directory = null) : IBrowserDirectoryState
    {
        public string? Directory { get; private set; } = directory;
        public int LoadCount { get; private set; }
        public int SaveCount { get; private set; }

        public Task<string?> LoadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadCount++;
            return Task.FromResult(Directory);
        }

        public Task SaveAsync(string selectedDirectory, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SaveCount++;
            Directory = selectedDirectory;
            return Task.CompletedTask;
        }
    }
}
