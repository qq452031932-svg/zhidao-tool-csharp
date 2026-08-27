using BaiduBatchTransfer.App.Features.ManagedBrowser;
using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.App.Tests.Features;

public sealed class BrowserSetupServiceTests
{
    private const string SelectedDirectory = @"C:\Browser\Application";
    private const string SavedDirectory = @"C:\Saved\Browser";
    private readonly BrowserSelectionProcessContext processContext = new(Guid.NewGuid());
    private readonly ProcessBrowserSelectionActivityRegistry activityRegistry = new();

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
    public async Task EnsurePromptClaimPreventsConcurrentReselectFromOverwritingIt()
    {
        const string reselectedDirectory = @"C:\Reselected\Browser";
        var staleDialog = new ControlledBrowserDirectoryPicker(1);
        var fileSystem = new RecordingBrowserDirectoryFileSystem(
            Path.Combine(SelectedDirectory, "chrome.exe"),
            Path.Combine(reselectedDirectory, "msedge.exe"));
        var state = new RecordingBrowserDirectoryState();
        var staleEnsure = CreateService(staleDialog, fileSystem, state);
        var reselectDialog = new RecordingBrowserDirectoryPicker(reselectedDirectory);
        var reselect = CreateService(reselectDialog, fileSystem, state);

        var staleResultTask = staleEnsure.EnsureSelectedAsync(CancellationToken.None);
        await staleDialog.WaitUntilStartedAsync(0);
        var reselectResult = await reselect.ReselectAsync(CancellationToken.None);
        staleDialog.Complete(0, SelectedDirectory);
        var staleResult = await staleResultTask;

        Assert.Equal(BrowserSetupStatus.StateChanged, reselectResult.Status);
        Assert.Equal(0, reselectDialog.BrowserDirectoryShowCount);
        Assert.Equal(BrowserFamily.Chrome, Assert.Single(staleResult.Candidates).BrowserFamily);
        Assert.Equal(SelectedDirectory, state.Directory);
        Assert.Equal(1, state.SaveCount);
        Assert.Equal(1, state.SaveAttemptCount);
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
    public async Task PendingExplicitReselectPreventsSecondReselectPicker()
    {
        const string newerDirectory = @"C:\Newer\Browser";
        var olderDialog = new ControlledBrowserDirectoryPicker(1);
        var fileSystem = new RecordingBrowserDirectoryFileSystem(
            Path.Combine(SelectedDirectory, "chrome.exe"),
            Path.Combine(newerDirectory, "msedge.exe"));
        var state = new RecordingBrowserDirectoryState(SavedDirectory);
        var olderService = CreateService(olderDialog, fileSystem, state);
        var newerDialog = new RecordingBrowserDirectoryPicker(newerDirectory);
        var newerService = CreateService(newerDialog, fileSystem, state);

        var olderResultTask = olderService.ReselectAsync(CancellationToken.None);
        await olderDialog.WaitUntilStartedAsync(0);
        var newerResult = await newerService.ReselectAsync(CancellationToken.None);
        olderDialog.Complete(0, SelectedDirectory);
        var olderResult = await olderResultTask;

        Assert.Equal(BrowserSetupStatus.StateChanged, newerResult.Status);
        Assert.Equal(0, newerDialog.BrowserDirectoryShowCount);
        Assert.Equal(BrowserFamily.Chrome, Assert.Single(olderResult.Candidates).BrowserFamily);
        Assert.Equal(SelectedDirectory, state.Directory);
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
    public async Task SavedDirectoryProbeCannotReturnASelectionReplacedWhileProbeWasRunning()
    {
        const string replacementDirectory = @"C:\Replacement\Browser";
        var fileSystem = new BlockingBrowserDirectoryFileSystem(
            Path.Combine(SavedDirectory, "chrome.exe"),
            Path.Combine(SavedDirectory, "chrome.exe"),
            Path.Combine(replacementDirectory, "msedge.exe"));
        var state = new RecordingBrowserDirectoryState(SavedDirectory);
        var staleEnsure = CreateService(
            new RecordingBrowserDirectoryPicker(null), fileSystem, state);
        var replacement = CreateService(
            new RecordingBrowserDirectoryPicker(replacementDirectory), fileSystem, state);

        var staleResultTask = staleEnsure.EnsureSelectedAsync(CancellationToken.None);
        await fileSystem.WaitUntilBlockedProbeStartedAsync();
        var replacementResult = await replacement.ReselectAsync(CancellationToken.None);
        fileSystem.ReleaseBlockedProbe();
        var staleResult = await staleResultTask;

        Assert.Equal(BrowserFamily.Edge, Assert.Single(replacementResult.Candidates).BrowserFamily);
        Assert.Equal(BrowserFamily.Edge, Assert.Single(staleResult.Candidates).BrowserFamily);
        Assert.Equal(replacementDirectory, state.Directory);
    }

    [Fact]
    public async Task ConflictAdoptionProbeCannotReturnASelectionReplacedWhileProbeWasRunning()
    {
        const string replacementDirectory = @"C:\ConflictReplacement\Browser";
        var fileSystem = new BlockingBrowserDirectoryFileSystem(
            Path.Combine(SavedDirectory, "chrome.exe"),
            Path.Combine(SelectedDirectory, "chrome.exe"),
            Path.Combine(SavedDirectory, "chrome.exe"),
            Path.Combine(replacementDirectory, "msedge.exe"));
        var state = new RecordingBrowserDirectoryState
        {
            ConflictsRemaining = 1,
            ConflictDirectory = SavedDirectory,
        };
        var staleEnsure = CreateService(
            new RecordingBrowserDirectoryPicker(SelectedDirectory), fileSystem, state);
        var replacement = CreateService(
            new RecordingBrowserDirectoryPicker(replacementDirectory), fileSystem, state);

        var staleResultTask = staleEnsure.EnsureSelectedAsync(CancellationToken.None);
        await fileSystem.WaitUntilBlockedProbeStartedAsync();
        var replacementResult = await replacement.ReselectAsync(CancellationToken.None);
        fileSystem.ReleaseBlockedProbe();
        var staleResult = await staleResultTask;

        Assert.Equal(BrowserFamily.Edge, Assert.Single(replacementResult.Candidates).BrowserFamily);
        Assert.Equal(BrowserFamily.Edge, Assert.Single(staleResult.Candidates).BrowserFamily);
        Assert.Equal(replacementDirectory, state.Directory);
    }

    [Fact]
    public async Task ReselectClaimedAfterEnsureFinalLoadPreventsEnsureFromOpeningPicker()
    {
        const string replacementDirectory = @"C:\ClaimedByReselect\Browser";
        var ensurePicker = new RecordingBrowserDirectoryPicker(SelectedDirectory);
        var reselectPicker = new ControlledBrowserDirectoryPicker(1);
        var fileSystem = new RecordingBrowserDirectoryFileSystem(
            Path.Combine(replacementDirectory, "msedge.exe"));
        var state = new RecordingBrowserDirectoryState(SavedDirectory)
        {
            PauseLoadNumber = 2,
        };
        var ensureService = CreateService(ensurePicker, fileSystem, state);
        var reselectService = CreateService(reselectPicker, fileSystem, state);

        var ensureTask = ensureService.EnsureSelectedAsync(CancellationToken.None);
        await state.WaitUntilPausedLoadAsync();
        var reselectTask = reselectService.ReselectAsync(CancellationToken.None);
        await reselectPicker.WaitUntilStartedAsync(0);
        state.ReleasePausedLoad();
        var ensureResult = await ensureTask;

        Assert.Equal(BrowserSetupStatus.StateChanged, ensureResult.Status);
        Assert.Equal(0, ensurePicker.BrowserDirectoryShowCount);

        reselectPicker.Complete(0, replacementDirectory);
        Assert.Equal(BrowserSetupStatus.Selected, (await reselectTask).Status);
        Assert.Equal(replacementDirectory, state.Directory);
    }

    [Fact]
    public async Task EnsurePromptClaimPreventsReselectFromOpeningSecondPicker()
    {
        var ensurePicker = new ControlledBrowserDirectoryPicker(1);
        var reselectPicker = new RecordingBrowserDirectoryPicker(SelectedDirectory);
        var fileSystem = new RecordingBrowserDirectoryFileSystem(
            Path.Combine(SelectedDirectory, "chrome.exe"));
        var state = new RecordingBrowserDirectoryState(SavedDirectory);
        var ensureService = CreateService(ensurePicker, fileSystem, state);
        var reselectService = CreateService(reselectPicker, fileSystem, state);

        var ensureTask = ensureService.EnsureSelectedAsync(CancellationToken.None);
        await ensurePicker.WaitUntilStartedAsync(0);
        var reselectResult = await reselectService.ReselectAsync(CancellationToken.None);

        Assert.Equal(BrowserSetupStatus.StateChanged, reselectResult.Status);
        Assert.Equal(0, reselectPicker.BrowserDirectoryShowCount);

        ensurePicker.Complete(0, SelectedDirectory);
        Assert.Equal(BrowserSetupStatus.Selected, (await ensureTask).Status);
        Assert.Equal(SelectedDirectory, state.Directory);
    }

    [Fact]
    public async Task PersistentAbandonFailureReturnsCleanupFailedInsteadOfCancellation()
    {
        var state = new RecordingBrowserDirectoryState(SavedDirectory)
        {
            AbandonFailuresBeforeMutation = int.MaxValue,
        };

        var result = await CreateService(
                new RecordingBrowserDirectoryPicker(null),
                new RecordingBrowserDirectoryFileSystem(),
                state)
            .ReselectAsync(CancellationToken.None);

        Assert.Equal(BrowserSetupStatus.CleanupFailed, result.Status);
        Assert.True((await state.LoadAsync(CancellationToken.None)).PendingIntent is not null);
    }

    [Fact]
    public async Task CleanupFailureCanBeRecoveredByNextCallInSameProcess()
    {
        var state = new RecordingBrowserDirectoryState(SavedDirectory)
        {
            AbandonFailuresBeforeMutation = int.MaxValue,
        };

        var failed = await CreateService(
                new RecordingBrowserDirectoryPicker(null),
                new RecordingBrowserDirectoryFileSystem(),
                state)
            .ReselectAsync(CancellationToken.None);
        Assert.Equal(BrowserSetupStatus.CleanupFailed, failed.Status);

        state.AbandonFailuresBeforeMutation = 0;
        var recovered = await CreateService(
                new RecordingBrowserDirectoryPicker(SelectedDirectory),
                new RecordingBrowserDirectoryFileSystem(Path.Combine(SelectedDirectory, "chrome.exe")),
                state)
            .ReselectAsync(CancellationToken.None);

        Assert.Equal(BrowserSetupStatus.Selected, recovered.Status);
        Assert.Equal(SelectedDirectory, state.Directory);
        Assert.Null((await state.LoadAsync(CancellationToken.None)).PendingIntent);
    }

    [Fact]
    public async Task DifferentOwnerEpochRecoversCrashedIntentBeforeSelecting()
    {
        var state = new RecordingBrowserDirectoryState(SavedDirectory);
        state.SeedPendingIntent(new BrowserDirectorySelectionIntent(
            revision: 1,
            BrowserDirectorySelectionIntentKind.ExplicitReselect,
            ownerEpoch: Guid.NewGuid(),
            intentId: Guid.NewGuid()));

        var result = await CreateService(
                new RecordingBrowserDirectoryPicker(SelectedDirectory),
                new RecordingBrowserDirectoryFileSystem(Path.Combine(SelectedDirectory, "chrome.exe")),
                state)
            .ReselectAsync(CancellationToken.None);

        Assert.Equal(BrowserSetupStatus.Selected, result.Status);
        Assert.Equal(SelectedDirectory, state.Directory);
        Assert.True(state.AbandonAttemptCount >= 1);
    }

    [Fact]
    public async Task LiveIntentInCurrentEpochIsNotRecovered()
    {
        var intent = new BrowserDirectorySelectionIntent(
            revision: 1,
            BrowserDirectorySelectionIntentKind.ExplicitReselect,
            processContext.OwnerEpoch,
            Guid.NewGuid());
        var state = new RecordingBrowserDirectoryState(SavedDirectory);
        state.SeedPendingIntent(intent);
        using var activity = activityRegistry.Register(intent.OwnerEpoch, intent.IntentId);

        var result = await CreateService(
                new RecordingBrowserDirectoryPicker(SelectedDirectory),
                new RecordingBrowserDirectoryFileSystem(),
                state)
            .EnsureSelectedAsync(CancellationToken.None);

        Assert.Equal(BrowserSetupStatus.StateChanged, result.Status);
        Assert.Equal(0, state.AbandonAttemptCount);
        Assert.Equal(intent.IntentId,
            (await state.LoadAsync(CancellationToken.None)).PendingIntent!.IntentId);
    }

    [Fact]
    public async Task RecoveryOfOldIntentCannotClearReplacementIntent()
    {
        var oldIntent = new BrowserDirectorySelectionIntent(
            revision: 1,
            BrowserDirectorySelectionIntentKind.ExplicitReselect,
            Guid.NewGuid(),
            Guid.NewGuid());
        var replacementIntent = new BrowserDirectorySelectionIntent(
            revision: 2,
            BrowserDirectorySelectionIntentKind.ExplicitReselect,
            processContext.OwnerEpoch,
            Guid.NewGuid());
        var state = new RecordingBrowserDirectoryState(SavedDirectory)
        {
            ReplacementPendingIntent = replacementIntent,
        };
        state.SeedPendingIntent(oldIntent);
        using var activity = activityRegistry.Register(
            replacementIntent.OwnerEpoch, replacementIntent.IntentId);

        var result = await CreateService(
                new RecordingBrowserDirectoryPicker(SelectedDirectory),
                new RecordingBrowserDirectoryFileSystem(),
                state)
            .RecoverPendingIntentAsync(CancellationToken.None);

        Assert.Equal(BrowserDirectoryRecoveryStatus.ActiveIntent, result.Status);
        Assert.Equal(replacementIntent.IntentId,
            (await state.LoadAsync(CancellationToken.None)).PendingIntent!.IntentId);
    }

    [Fact]
    public async Task BeginMutationFollowedByExceptionIsConfirmedAndCompleted()
    {
        var state = new RecordingBrowserDirectoryState(SavedDirectory)
        {
            ThrowAfterBeginMutation = true,
        };

        var result = await CreateService(
                new RecordingBrowserDirectoryPicker(SelectedDirectory),
                new RecordingBrowserDirectoryFileSystem(Path.Combine(SelectedDirectory, "chrome.exe")),
                state)
            .ReselectAsync(CancellationToken.None);

        Assert.Equal(BrowserSetupStatus.Selected, result.Status);
        Assert.Equal(SelectedDirectory, state.Directory);
        Assert.Null((await state.LoadAsync(CancellationToken.None)).PendingIntent);
    }

    [Fact]
    public async Task BeginFailureBeforeMutationDoesNotLeaveUnknownClaim()
    {
        var state = new RecordingBrowserDirectoryState(SavedDirectory)
        {
            ThrowBeforeBeginMutation = true,
        };

        var result = await CreateService(
                new RecordingBrowserDirectoryPicker(SelectedDirectory),
                new RecordingBrowserDirectoryFileSystem(),
                state)
            .ReselectAsync(CancellationToken.None);

        Assert.Equal(BrowserSetupStatus.StateUnavailable, result.Status);
        Assert.Null((await state.LoadAsync(CancellationToken.None)).PendingIntent);
    }

    [Fact]
    public void IntentDiagnosticsRedactOwnerEpochAndIntentId()
    {
        var ownerEpoch = Guid.NewGuid();
        var intentId = Guid.NewGuid();
        var claim = new BrowserDirectoryIntentClaim(
            BrowserDirectorySelectionIntentKind.EnsurePrompt, ownerEpoch, intentId);
        var intent = new BrowserDirectorySelectionIntent(
            1, claim.Kind, ownerEpoch, intentId);

        Assert.DoesNotContain(ownerEpoch.ToString(), claim.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(intentId.ToString(), claim.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ownerEpoch.ToString(), intent.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(intentId.ToString(), intent.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TransientAbandonFailureRetriesAndReturnsOriginalCancellation()
    {
        var state = new RecordingBrowserDirectoryState(SavedDirectory)
        {
            AbandonFailuresBeforeMutation = 1,
        };

        var result = await CreateService(
                new RecordingBrowserDirectoryPicker(null),
                new RecordingBrowserDirectoryFileSystem(),
                state)
            .ReselectAsync(CancellationToken.None);

        Assert.Equal(BrowserSetupStatus.Cancelled, result.Status);
        Assert.Null((await state.LoadAsync(CancellationToken.None)).PendingIntent);
        Assert.Equal(2, state.AbandonAttemptCount);
    }

    [Fact]
    public async Task AbandonMutationFollowedByExceptionIsConfirmedAsSafe()
    {
        var state = new RecordingBrowserDirectoryState(SavedDirectory)
        {
            ThrowAfterAbandonMutation = true,
        };

        var result = await CreateService(
                new RecordingBrowserDirectoryPicker(null),
                new RecordingBrowserDirectoryFileSystem(),
                state)
            .ReselectAsync(CancellationToken.None);

        Assert.Equal(BrowserSetupStatus.Cancelled, result.Status);
        Assert.Null((await state.LoadAsync(CancellationToken.None)).PendingIntent);
    }

    [Fact]
    public async Task AbandonOfOldIntentDoesNotClearNewIntent()
    {
        var state = new RecordingBrowserDirectoryState(SavedDirectory)
        {
            ReplacePendingBeforeAbandon = true,
        };

        var result = await CreateService(
                new RecordingBrowserDirectoryPicker(null),
                new RecordingBrowserDirectoryFileSystem(),
                state)
            .ReselectAsync(CancellationToken.None);
        var snapshot = await state.LoadAsync(CancellationToken.None);

        Assert.Equal(BrowserSetupStatus.Cancelled, result.Status);
        Assert.NotNull(snapshot.PendingIntent);
        Assert.Equal(state.Revision, snapshot.PendingIntent!.Revision);
    }

    [Fact]
    public async Task ContinuallyChangingRevisionReturnsStateChangedAfterBoundedObservations()
    {
        var fileSystem = new RecordingBrowserDirectoryFileSystem(
            Path.Combine(SavedDirectory, "chrome.exe"));
        var state = new ChurningBrowserDirectoryState(SavedDirectory);

        var result = await CreateService(
                new RecordingBrowserDirectoryPicker(SelectedDirectory), fileSystem, state)
            .EnsureSelectedAsync(CancellationToken.None);

        Assert.Equal(BrowserSetupStatus.StateChanged, result.Status);
        Assert.Equal(5, state.LoadCount);
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

    private BrowserSetupService CreateService(
        IBrowserDirectoryPicker dialog,
        IBrowserDirectoryFileSystem fileSystem,
        IBrowserDirectoryState state) =>
        new(dialog, fileSystem, state, processContext, activityRegistry);

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

    private sealed class BlockingBrowserDirectoryFileSystem(
        string blockedPath,
        params string[] existingPaths) : IBrowserDirectoryFileSystem
    {
        private readonly HashSet<string> existing = new(existingPaths, StringComparer.OrdinalIgnoreCase);
        private readonly TaskCompletionSource<bool> blockedProbeStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> blockedProbeReleased =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitUntilBlockedProbeStartedAsync() => blockedProbeStarted.Task;
        public void ReleaseBlockedProbe() => blockedProbeReleased.TrySetResult(true);

        public async Task<bool> FileExistsAsync(string exactPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(exactPath, blockedPath, StringComparison.OrdinalIgnoreCase))
            {
                blockedProbeStarted.TrySetResult(true);
                await blockedProbeReleased.Task.WaitAsync(cancellationToken);
            }
            return existing.Contains(exactPath);
        }
    }

    private sealed class RecordingBrowserDirectoryState(string? directory = null) : IBrowserDirectoryState
    {
        private readonly object sync = new();
        private BrowserDirectorySelectionIntent? pendingIntent;

        public string? Directory { get; private set; } = directory;
        public int LoadCount { get; private set; }
        public int SaveCount { get; private set; }

        public long Revision { get; private set; }
        public int SaveAttemptCount { get; private set; }
        public int ConflictsRemaining { get; set; }
        public string? ConflictDirectory { get; set; }
        public Action? BeforeSave { get; set; }
        public Action? AfterCommit { get; set; }
        public int? PauseLoadNumber { get; set; }
        public int AbandonFailuresBeforeMutation { get; set; }
        public bool ThrowAfterAbandonMutation { get; set; }
        public bool ReplacePendingBeforeAbandon { get; set; }
        public BrowserDirectorySelectionIntent? ReplacementPendingIntent { get; set; }
        public bool ThrowBeforeBeginMutation { get; set; }
        public bool ThrowAfterBeginMutation { get; set; }
        public int AbandonAttemptCount { get; private set; }
        private readonly TaskCompletionSource<bool> pausedLoadStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> pausedLoadReleased =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitUntilPausedLoadAsync() => pausedLoadStarted.Task;
        public void ReleasePausedLoad() => pausedLoadReleased.TrySetResult(true);

        public void SeedPendingIntent(BrowserDirectorySelectionIntent intent)
        {
            lock (sync)
            {
                pendingIntent = intent;
                Revision = intent.Revision;
            }
        }

        public async Task<BrowserDirectoryStateSnapshot> LoadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BrowserDirectoryStateSnapshot snapshot;
            int loadNumber;
            lock (sync)
            {
                LoadCount++;
                loadNumber = LoadCount;
                snapshot = new BrowserDirectoryStateSnapshot(Directory, Revision, pendingIntent);
            }
            if (PauseLoadNumber == loadNumber)
            {
                pausedLoadStarted.TrySetResult(true);
                await pausedLoadReleased.Task.WaitAsync(cancellationToken);
            }
            return snapshot;
        }

        public Task<BrowserDirectoryIntentBeginResult> TryBeginEnsurePromptAsync(
            long expectedRevision,
            BrowserDirectoryIntentClaim claim,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                if (ThrowBeforeBeginMutation)
                    throw new IOException("simulated pre-mutation begin failure");
                if (expectedRevision != Revision || pendingIntent is not null)
                    return Task.FromResult(BrowserDirectoryIntentBeginResult.Conflict());
                Revision++;
                pendingIntent = new BrowserDirectorySelectionIntent(
                    Revision, claim.Kind, claim.OwnerEpoch, claim.IntentId);
                if (ThrowAfterBeginMutation)
                    throw new IOException("simulated post-mutation begin failure");
                return Task.FromResult(BrowserDirectoryIntentBeginResult.Acquired(pendingIntent));
            }
        }

        public Task<BrowserDirectoryIntentBeginResult> TryBeginReselectAsync(
            BrowserDirectoryIntentClaim claim,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                if (ThrowBeforeBeginMutation)
                    throw new IOException("simulated pre-mutation begin failure");
                if (pendingIntent is not null)
                    return Task.FromResult(BrowserDirectoryIntentBeginResult.Conflict());
                Revision++;
                pendingIntent = new BrowserDirectorySelectionIntent(
                    Revision, claim.Kind, claim.OwnerEpoch, claim.IntentId);
                if (ThrowAfterBeginMutation)
                    throw new IOException("simulated post-mutation begin failure");
                return Task.FromResult(BrowserDirectoryIntentBeginResult.Acquired(pendingIntent));
            }
        }

        public Task<BrowserDirectoryStateSaveResult> TrySaveAsync(
            string selectedDirectory,
            long expectedRevision,
            BrowserDirectorySelectionIntent selectionIntent,
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
                    pendingIntent = null;
                    return Task.FromResult(BrowserDirectoryStateSaveResult.Conflict());
                }
                if (expectedRevision != Revision)
                    return Task.FromResult(BrowserDirectoryStateSaveResult.Conflict());
                if (pendingIntent is null ||
                    pendingIntent.Revision != selectionIntent.Revision ||
                    pendingIntent.Kind != selectionIntent.Kind ||
                    pendingIntent.OwnerEpoch != selectionIntent.OwnerEpoch ||
                    pendingIntent.IntentId != selectionIntent.IntentId)
                    return Task.FromResult(BrowserDirectoryStateSaveResult.Conflict());
                SaveCount++;
                Directory = selectedDirectory;
                Revision++;
                pendingIntent = null;
                AfterCommit?.Invoke();
                return Task.FromResult(BrowserDirectoryStateSaveResult.Committed(Revision));
            }
        }

        public Task<bool> TryAbandonSelectionAsync(
            BrowserDirectorySelectionIntent selectionIntent,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                AbandonAttemptCount++;
                if (AbandonFailuresBeforeMutation > 0)
                {
                    AbandonFailuresBeforeMutation--;
                    throw new IOException("simulated abandon failure");
                }
                if (ReplacePendingBeforeAbandon)
                {
                    ReplacePendingBeforeAbandon = false;
                    Revision++;
                    pendingIntent = new BrowserDirectorySelectionIntent(
                        Revision, BrowserDirectorySelectionIntentKind.ExplicitReselect,
                        Guid.NewGuid(), Guid.NewGuid());
                }
                else if (ReplacementPendingIntent is not null)
                {
                    pendingIntent = ReplacementPendingIntent;
                    Revision = pendingIntent.Revision;
                    ReplacementPendingIntent = null;
                }
                if (pendingIntent is null ||
                    pendingIntent.Revision != selectionIntent.Revision ||
                    pendingIntent.Kind != selectionIntent.Kind ||
                    pendingIntent.OwnerEpoch != selectionIntent.OwnerEpoch ||
                    pendingIntent.IntentId != selectionIntent.IntentId)
                    return Task.FromResult(false);
                pendingIntent = null;
                Revision++;
                if (ThrowAfterAbandonMutation)
                {
                    ThrowAfterAbandonMutation = false;
                    throw new IOException("simulated post-mutation abandon failure");
                }
                return Task.FromResult(true);
            }
        }
    }

    private sealed class ChurningBrowserDirectoryState(string directory) : IBrowserDirectoryState
    {
        private long revision;
        public int LoadCount { get; private set; }

        public Task<BrowserDirectoryStateSnapshot> LoadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadCount++;
            revision++;
            return Task.FromResult(new BrowserDirectoryStateSnapshot(directory, revision));
        }

        public Task<BrowserDirectoryIntentBeginResult> TryBeginEnsurePromptAsync(
            long expectedRevision,
            BrowserDirectoryIntentClaim claim,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<BrowserDirectoryIntentBeginResult> TryBeginReselectAsync(
            BrowserDirectoryIntentClaim claim,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<BrowserDirectoryStateSaveResult> TrySaveAsync(
            string selectedDirectory,
            long expectedRevision,
            BrowserDirectorySelectionIntent selectionIntent,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> TryAbandonSelectionAsync(
            BrowserDirectorySelectionIntent selectionIntent,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
