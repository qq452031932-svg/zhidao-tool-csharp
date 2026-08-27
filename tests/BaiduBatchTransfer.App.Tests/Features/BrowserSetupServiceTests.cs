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
        var dialog = new RecordingBrowserDirectoryPicker(SelectedDirectory);
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
        var dialog = new RecordingBrowserDirectoryPicker(null);
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
        var dialog = new RecordingBrowserDirectoryPicker(SelectedDirectory);
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
        var dialog = new RecordingBrowserDirectoryPicker(SelectedDirectory);
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
        var dialog = new RecordingBrowserDirectoryPicker(SelectedDirectory);
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
        var dialog = new RecordingBrowserDirectoryPicker(SelectedDirectory);
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
        var dialog = new RecordingBrowserDirectoryPicker(SelectedDirectory);
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
        var dialog = new RecordingBrowserDirectoryPicker(privateRelativePath);
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
        var dialog = new RecordingBrowserDirectoryPicker(SelectedDirectory);
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
        var dialog = new RecordingBrowserDirectoryPicker(SelectedDirectory);
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
        var dialog = new RecordingBrowserDirectoryPicker(null, cancellation.Cancel);
        var fileSystem = new RecordingBrowserDirectoryFileSystem();
        var state = new RecordingBrowserDirectoryState();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateService(dialog, fileSystem, state).EnsureSelectedAsync(cancellation.Token));

        Assert.Equal(1, dialog.BrowserDirectoryShowCount);
        Assert.Empty(fileSystem.AccessedPaths);
        Assert.Equal(0, state.SaveCount);
    }

    [Fact]
    public async Task SingleServiceSerializesTheCompleteSelectionFlowInCallOrder()
    {
        const string laterDirectory = @"C:\Later\Browser";
        var dialog = new ControlledBrowserDirectoryPicker(2);
        var fileSystem = new RecordingBrowserDirectoryFileSystem(
            Path.Combine(SelectedDirectory, "chrome.exe"),
            Path.Combine(laterDirectory, "msedge.exe"));
        var state = new RecordingBrowserDirectoryState();
        var service = CreateService(dialog, fileSystem, state);

        var first = service.EnsureSelectedAsync(CancellationToken.None);
        await dialog.WaitUntilStartedAsync(0);
        var laterReselect = service.ReselectAsync(CancellationToken.None);

        Assert.False(dialog.HasStarted(1));
        dialog.Complete(0, SelectedDirectory);
        Assert.Equal(BrowserFamily.Chrome, Assert.Single((await first).Candidates).BrowserFamily);
        await dialog.WaitUntilStartedAsync(1);
        dialog.Complete(1, laterDirectory);

        Assert.Equal(BrowserFamily.Edge, Assert.Single((await laterReselect).Candidates).BrowserFamily);
        Assert.Equal(laterDirectory, state.Directory);
        Assert.Equal(2, state.SaveCount);
    }

    [Fact]
    public async Task StaleEnsureCannotOverwriteReselectFromAnotherService()
    {
        const string reselectedDirectory = @"C:\Reselected\Browser";
        var staleDialog = new ControlledBrowserDirectoryPicker(1);
        var fileSystem = new RecordingBrowserDirectoryFileSystem(
            Path.Combine(SelectedDirectory, "chrome.exe"),
            Path.Combine(reselectedDirectory, "msedge.exe"));
        var state = new RecordingBrowserDirectoryState();
        var staleEnsure = CreateService(staleDialog, fileSystem, state);
        var reselect = CreateService(
            new RecordingBrowserDirectoryPicker(reselectedDirectory), fileSystem, state);

        var staleResultTask = staleEnsure.EnsureSelectedAsync(CancellationToken.None);
        await staleDialog.WaitUntilStartedAsync(0);
        var reselectResult = await reselect.ReselectAsync(CancellationToken.None);
        staleDialog.Complete(0, SelectedDirectory);
        var staleResult = await staleResultTask;

        Assert.Equal(BrowserFamily.Edge, Assert.Single(reselectResult.Candidates).BrowserFamily);
        Assert.Equal(BrowserFamily.Edge, Assert.Single(staleResult.Candidates).BrowserFamily);
        Assert.Equal(reselectedDirectory, state.Directory);
        Assert.Equal(1, state.SaveCount);
        Assert.Equal(2, state.SaveAttemptCount);
    }

    [Fact]
    public async Task ExplicitReselectConflictAdoptsCurrentStateInsteadOfBlindlyRetrying()
    {
        const string competingDirectory = @"C:\Competing\Browser";
        var dialog = new RecordingBrowserDirectoryPicker(SelectedDirectory);
        var fileSystem = new RecordingBrowserDirectoryFileSystem(
            Path.Combine(competingDirectory, "chrome.exe"),
            Path.Combine(SelectedDirectory, "msedge.exe"));
        var state = new RecordingBrowserDirectoryState
        {
            ConflictsRemaining = 1,
            ConflictDirectory = competingDirectory,
        };

        var result = await CreateService(dialog, fileSystem, state)
            .ReselectAsync(CancellationToken.None);

        Assert.Equal(BrowserSetupStatus.Selected, result.Status);
        Assert.Equal(BrowserFamily.Chrome, Assert.Single(result.Candidates).BrowserFamily);
        Assert.Equal(competingDirectory, state.Directory);
        Assert.Equal(1, state.SaveAttemptCount);
        Assert.Equal(0, state.SaveCount);
    }

    [Fact]
    public async Task OlderExplicitReselectCannotOverwriteNewerReselectAcrossServices()
    {
        const string newerDirectory = @"C:\Newer\Browser";
        var olderDialog = new ControlledBrowserDirectoryPicker(1);
        var fileSystem = new RecordingBrowserDirectoryFileSystem(
            Path.Combine(SelectedDirectory, "chrome.exe"),
            Path.Combine(newerDirectory, "msedge.exe"));
        var state = new RecordingBrowserDirectoryState(SavedDirectory);
        var olderService = CreateService(olderDialog, fileSystem, state);
        var newerService = CreateService(
            new RecordingBrowserDirectoryPicker(newerDirectory), fileSystem, state);

        var olderResultTask = olderService.ReselectAsync(CancellationToken.None);
        await olderDialog.WaitUntilStartedAsync(0);
        var newerResult = await newerService.ReselectAsync(CancellationToken.None);
        olderDialog.Complete(0, SelectedDirectory);
        var olderResult = await olderResultTask;

        Assert.Equal(BrowserFamily.Edge, Assert.Single(newerResult.Candidates).BrowserFamily);
        Assert.Equal(BrowserFamily.Edge, Assert.Single(olderResult.Candidates).BrowserFamily);
        Assert.Equal(newerDirectory, state.Directory);
        Assert.Equal(1, state.SaveCount);
    }

    [Fact]
    public async Task EnsureCannotPromptOrSaveWhileExplicitReselectIntentIsPending()
    {
        const string reselectedDirectory = @"C:\PendingReselect\Browser";
        var reselectDialog = new ControlledBrowserDirectoryPicker(1);
        var ensureDialog = new RecordingBrowserDirectoryPicker(SelectedDirectory);
        var fileSystem = new RecordingBrowserDirectoryFileSystem(
            Path.Combine(SelectedDirectory, "chrome.exe"),
            Path.Combine(reselectedDirectory, "msedge.exe"));
        var state = new RecordingBrowserDirectoryState(SavedDirectory);
        var reselectService = CreateService(reselectDialog, fileSystem, state);
        var ensureService = CreateService(ensureDialog, fileSystem, state);

        var reselectTask = reselectService.ReselectAsync(CancellationToken.None);
        await reselectDialog.WaitUntilStartedAsync(0);
        var ensureResult = await ensureService.EnsureSelectedAsync(CancellationToken.None);

        Assert.Equal(BrowserSetupStatus.StateChanged, ensureResult.Status);
        Assert.Equal(0, ensureDialog.BrowserDirectoryShowCount);
        Assert.Equal(0, state.SaveAttemptCount);

        reselectDialog.Complete(0, reselectedDirectory);
        var reselectResult = await reselectTask;
        Assert.Equal(BrowserFamily.Edge, Assert.Single(reselectResult.Candidates).BrowserFamily);
        Assert.Equal(reselectedDirectory, state.Directory);
    }

    [Fact]
    public async Task CancellationAfterDurableCommitReturnsCommittedResult()
    {
        using var cancellation = new CancellationTokenSource();
        var dialog = new RecordingBrowserDirectoryPicker(SelectedDirectory);
        var fileSystem = new RecordingBrowserDirectoryFileSystem(
            Path.Combine(SelectedDirectory, "chrome.exe"));
        var state = new RecordingBrowserDirectoryState(SavedDirectory)
        {
            AfterCommit = cancellation.Cancel,
        };

        var result = await CreateService(dialog, fileSystem, state)
            .ReselectAsync(cancellation.Token);

        Assert.Equal(BrowserSetupStatus.Selected, result.Status);
        Assert.Equal(SelectedDirectory, state.Directory);
        Assert.Equal(1, state.SaveCount);
        Assert.True(cancellation.IsCancellationRequested);
    }

    [Fact]
    public async Task CancellationBeforeCommitPropagatesWithoutChangingSavedDirectory()
    {
        using var cancellation = new CancellationTokenSource();
        var dialog = new RecordingBrowserDirectoryPicker(SelectedDirectory);
        var fileSystem = new RecordingBrowserDirectoryFileSystem(
            Path.Combine(SelectedDirectory, "chrome.exe"));
        var state = new RecordingBrowserDirectoryState(SavedDirectory)
        {
            BeforeSave = cancellation.Cancel,
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateService(dialog, fileSystem, state).ReselectAsync(cancellation.Token));

        Assert.Equal(SavedDirectory, state.Directory);
        Assert.Equal(0, state.SaveCount);
        Assert.Equal(1, state.SaveAttemptCount);
    }

    private static BrowserSetupService CreateService(
        IBrowserDirectoryPicker dialog,
        IBrowserDirectoryFileSystem fileSystem,
        IBrowserDirectoryState state) => new(dialog, fileSystem, state);

    private static string[] ExpectedProbes(string directory) =>
        [Path.Combine(directory, "chrome.exe"), Path.Combine(directory, "msedge.exe")];

    private sealed class RecordingBrowserDirectoryPicker(
        string? selectedDirectory,
        Action? onBrowserDirectoryPick = null) : IBrowserDirectoryPicker
    {
        public int BrowserDirectoryShowCount { get; private set; }

        public Task<string?> PickBrowserDirectoryAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BrowserDirectoryShowCount++;
            onBrowserDirectoryPick?.Invoke();
            return Task.FromResult(selectedDirectory);
        }
    }

    private sealed class ControlledBrowserDirectoryPicker(int count) : IBrowserDirectoryPicker
    {
        private readonly TaskCompletionSource<bool>[] started = Enumerable.Range(0, count)
            .Select(_ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
        private readonly TaskCompletionSource<string?>[] completed = Enumerable.Range(0, count)
            .Select(_ => new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
        private int invocationIndex = -1;

        public bool HasStarted(int index) => started[index].Task.IsCompleted;

        public Task WaitUntilStartedAsync(int index) => started[index].Task;

        public void Complete(int index, string? selectedDirectory) =>
            completed[index].SetResult(selectedDirectory);

        public async Task<string?> PickBrowserDirectoryAsync(CancellationToken cancellationToken = default)
        {
            var index = Interlocked.Increment(ref invocationIndex);
            started[index].SetResult(true);
            return await completed[index].Task.WaitAsync(cancellationToken);
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
        private readonly object sync = new();
        private long? pendingReselectRevision;

        public string? Directory { get; private set; } = directory;
        public int LoadCount { get; private set; }
        public int SaveCount { get; private set; }

        public long Revision { get; private set; }
        public int SaveAttemptCount { get; private set; }
        public int ConflictsRemaining { get; set; }
        public string? ConflictDirectory { get; set; }
        public Action? BeforeSave { get; set; }
        public Action? AfterCommit { get; set; }

        public Task<BrowserDirectoryStateSnapshot> LoadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                LoadCount++;
                var pending = pendingReselectRevision is long value
                    ? new BrowserDirectoryReselectIntent(value)
                    : null;
                return Task.FromResult(new BrowserDirectoryStateSnapshot(Directory, Revision, pending));
            }
        }

        public Task<BrowserDirectoryReselectIntent> BeginReselectAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                Revision++;
                pendingReselectRevision = Revision;
                return Task.FromResult(new BrowserDirectoryReselectIntent(Revision));
            }
        }

        public Task<BrowserDirectoryStateSaveResult> TrySaveAsync(
            string selectedDirectory,
            long expectedRevision,
            BrowserDirectoryReselectIntent? reselectIntent,
            CancellationToken cancellationToken)
        {
            lock (sync) SaveAttemptCount++;
            BeforeSave?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                if (ConflictsRemaining > 0)
                {
                    ConflictsRemaining--;
                    Directory = ConflictDirectory;
                    Revision++;
                    pendingReselectRevision = null;
                    return Task.FromResult(BrowserDirectoryStateSaveResult.Conflict());
                }
                if (expectedRevision != Revision)
                    return Task.FromResult(BrowserDirectoryStateSaveResult.Conflict());
                if (reselectIntent is null && pendingReselectRevision is not null)
                    return Task.FromResult(BrowserDirectoryStateSaveResult.Conflict());
                if (reselectIntent is not null && pendingReselectRevision != reselectIntent.Revision)
                    return Task.FromResult(BrowserDirectoryStateSaveResult.Conflict());
                SaveCount++;
                Directory = selectedDirectory;
                Revision++;
                pendingReselectRevision = null;
                AfterCommit?.Invoke();
                return Task.FromResult(BrowserDirectoryStateSaveResult.Committed(Revision));
            }
        }

        public Task<bool> TryAbandonReselectAsync(
            BrowserDirectoryReselectIntent reselectIntent,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                if (pendingReselectRevision != reselectIntent.Revision)
                    return Task.FromResult(false);
                pendingReselectRevision = null;
                Revision++;
                return Task.FromResult(true);
            }
        }
    }
}
