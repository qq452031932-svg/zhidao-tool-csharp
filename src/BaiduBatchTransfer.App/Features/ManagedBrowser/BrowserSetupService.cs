using BaiduBatchTransfer.App.Services;
using BaiduBatchTransfer.Core.Domain;
using System.IO;

namespace BaiduBatchTransfer.App.Features.ManagedBrowser;

public enum BrowserSetupStatus
{
    Selected = 1,
    BrowserFamilyChoiceRequired = 2,
    Cancelled = 3,
    NoSupportedBrowser = 4,
    InvalidDirectory = 5,
    DirectoryUnavailable = 6,
    DialogUnavailable = 7,
    StateUnavailable = 8,
    StateChanged = 9,
    CleanupFailed = 10,
}

public sealed class BrowserDirectoryCandidate
{
    public BrowserDirectoryCandidate(
        BrowserFamily browserFamily,
        string directoryPath,
        string executablePath)
    {
        if (browserFamily is not (BrowserFamily.Chrome or BrowserFamily.Edge))
            throw new ArgumentOutOfRangeException(nameof(browserFamily));
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        BrowserFamily = browserFamily;
        DirectoryPath = directoryPath;
        ExecutablePath = executablePath;
    }

    public BrowserFamily BrowserFamily { get; }
    public string DirectoryPath { get; }
    public string ExecutablePath { get; }

    public override string ToString() =>
        $"BrowserDirectoryCandidate {{ BrowserFamily = {BrowserFamily}, DirectoryPath = [REDACTED], ExecutablePath = [REDACTED] }}";
}

public sealed class BrowserSetupResult
{
    internal BrowserSetupResult(
        BrowserSetupStatus status,
        IReadOnlyList<BrowserDirectoryCandidate>? candidates = null)
    {
        Status = status;
        Candidates = candidates ?? Array.Empty<BrowserDirectoryCandidate>();
    }

    public BrowserSetupStatus Status { get; }
    public IReadOnlyList<BrowserDirectoryCandidate> Candidates { get; }

    public override string ToString() =>
        $"BrowserSetupResult {{ Status = {Status}, CandidateCount = {Candidates.Count} }}";
}

public interface IBrowserDirectoryFileSystem
{
    Task<bool> FileExistsAsync(string exactPath, CancellationToken cancellationToken);
}

public interface IExactFileExistenceProbe
{
    bool Exists(string exactPath);
}

public sealed class ExactFileExistenceProbe : IExactFileExistenceProbe
{
    public bool Exists(string exactPath) => File.Exists(exactPath);
}

public sealed class BrowserDirectoryFileSystem(IExactFileExistenceProbe? probe = null)
    : IBrowserDirectoryFileSystem
{
    private readonly IExactFileExistenceProbe exactFileProbe = probe ?? new ExactFileExistenceProbe();

    public async Task<bool> FileExistsAsync(string exactPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exactPath);
        cancellationToken.ThrowIfCancellationRequested();
        var probeTask = Task.Run(() => exactFileProbe.Exists(exactPath), CancellationToken.None);
        try
        {
            return await probeTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = ObserveProbeCompletionAsync(probeTask);
            throw;
        }
    }

    private static async Task ObserveProbeCompletionAsync(Task probeTask)
    {
        try
        {
            await probeTask.ConfigureAwait(false);
        }
        catch
        {
            // The caller has already observed cancellation; the read-only probe is only drained.
        }
    }
}

public interface IBrowserDirectoryState
{
    Task<BrowserDirectoryStateSnapshot> LoadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Atomically claims the shared prompt slot only when the revision is current and no intent
    /// is pending. The stored directory is not changed.
    /// </summary>
    Task<BrowserDirectoryIntentBeginResult> TryBeginEnsurePromptAsync(
        long expectedRevision,
        CancellationToken cancellationToken);

    /// <summary>
    /// Atomically claims the shared prompt slot only when no intent is pending. The stored
    /// directory is not changed; a conflict must not open another picker.
    /// </summary>
    Task<BrowserDirectoryIntentBeginResult> TryBeginReselectAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Atomically commits the directory only when <paramref name="expectedRevision"/> is current.
    /// Every save requires its exact still-pending prompt intent. Committed means the durable commit
    /// point has passed, even if the caller token is cancelled immediately after.
    /// </summary>
    Task<BrowserDirectoryStateSaveResult> TrySaveAsync(
        string selectedDirectory,
        long expectedRevision,
        BrowserDirectorySelectionIntent selectionIntent,
        CancellationToken cancellationToken);

    /// <summary>
    /// Clears only the exact pending intent and advances the revision; it must not affect a newer intent.
    /// </summary>
    Task<bool> TryAbandonSelectionAsync(
        BrowserDirectorySelectionIntent selectionIntent,
        CancellationToken cancellationToken);
}

public enum BrowserDirectorySelectionIntentKind
{
    EnsurePrompt = 1,
    ExplicitReselect = 2,
}

public sealed class BrowserDirectorySelectionIntent
{
    public BrowserDirectorySelectionIntent(
        long revision,
        BrowserDirectorySelectionIntentKind kind)
    {
        if (revision < 0) throw new ArgumentOutOfRangeException(nameof(revision));
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        Revision = revision;
        Kind = kind;
    }

    public long Revision { get; }
    public BrowserDirectorySelectionIntentKind Kind { get; }

    public override string ToString() =>
        $"BrowserDirectorySelectionIntent {{ Revision = {Revision}, Kind = {Kind} }}";
}

public enum BrowserDirectoryIntentBeginStatus
{
    Acquired = 1,
    Conflict = 2,
}

public sealed class BrowserDirectoryIntentBeginResult
{
    private BrowserDirectoryIntentBeginResult(
        BrowserDirectoryIntentBeginStatus status,
        BrowserDirectorySelectionIntent? intent)
    {
        Status = status;
        Intent = intent;
    }

    public BrowserDirectoryIntentBeginStatus Status { get; }
    public BrowserDirectorySelectionIntent? Intent { get; }

    public static BrowserDirectoryIntentBeginResult Acquired(BrowserDirectorySelectionIntent intent) =>
        new(BrowserDirectoryIntentBeginStatus.Acquired, intent ?? throw new ArgumentNullException(nameof(intent)));

    public static BrowserDirectoryIntentBeginResult Conflict() =>
        new(BrowserDirectoryIntentBeginStatus.Conflict, null);
}

public sealed class BrowserDirectoryStateSnapshot
{
    public BrowserDirectoryStateSnapshot(
        string? directory,
        long revision,
        BrowserDirectorySelectionIntent? pendingIntent = null)
    {
        if (revision < 0) throw new ArgumentOutOfRangeException(nameof(revision));
        Directory = directory;
        Revision = revision;
        PendingIntent = pendingIntent;
    }

    public string? Directory { get; }
    public long Revision { get; }
    public BrowserDirectorySelectionIntent? PendingIntent { get; }

    public override string ToString() =>
        $"BrowserDirectoryStateSnapshot {{ Directory = [REDACTED], Revision = {Revision}, SelectionPending = {PendingIntent is not null} }}";
}

public enum BrowserDirectoryStateSaveStatus
{
    Committed = 1,
    Conflict = 2,
}

public sealed class BrowserDirectoryStateSaveResult
{
    private BrowserDirectoryStateSaveResult(BrowserDirectoryStateSaveStatus status, long revision)
    {
        Status = status;
        Revision = revision;
    }

    public BrowserDirectoryStateSaveStatus Status { get; }
    public long Revision { get; }

    public static BrowserDirectoryStateSaveResult Committed(long revision)
    {
        if (revision < 0) throw new ArgumentOutOfRangeException(nameof(revision));
        return new BrowserDirectoryStateSaveResult(BrowserDirectoryStateSaveStatus.Committed, revision);
    }

    public static BrowserDirectoryStateSaveResult Conflict() =>
        new(BrowserDirectoryStateSaveStatus.Conflict, 0);
}

public sealed class BrowserSetupService(
    IBrowserDirectoryPicker dialogService,
    IBrowserDirectoryFileSystem fileSystem,
    IBrowserDirectoryState state)
{
    private const string ChromeExecutableName = "chrome.exe";
    private const string EdgeExecutableName = "msedge.exe";
    private const int MaximumStableObservationAttempts = 3;
    private const int MaximumIntentCleanupAttempts = 3;
    private readonly SemaphoreSlim flowGate = new(1, 1);

    private sealed record SelectionAttemptResult(
        BrowserSetupResult Result,
        bool IntentCommitted);

    public async Task<BrowserSetupResult> EnsureSelectedAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await flowGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await EnsureSelectedCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            flowGate.Release();
        }
    }

    public async Task<BrowserSetupResult> ReselectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await flowGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            BrowserDirectoryIntentBeginResult begin;
            try
            {
                begin = await state.TryBeginReselectAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return Result(BrowserSetupStatus.StateUnavailable);
            }
            if (begin.Status == BrowserDirectoryIntentBeginStatus.Conflict || begin.Intent is null)
                return Result(BrowserSetupStatus.StateChanged);
            return await RunClaimedSelectionAsync(begin.Intent, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            flowGate.Release();
        }
    }

    private async Task<BrowserSetupResult> EnsureSelectedCoreAsync(CancellationToken cancellationToken)
    {
        var loaded = await TryLoadStateAsync(cancellationToken).ConfigureAwait(false);
        if (loaded.Snapshot is null) return loaded.Error!;
        var snapshot = loaded.Snapshot;

        if (snapshot.PendingIntent is not null)
            return Result(BrowserSetupStatus.StateChanged);

        if (!string.IsNullOrWhiteSpace(snapshot.Directory))
        {
            var saved = await InspectAsync(snapshot.Directory, cancellationToken).ConfigureAwait(false);
            var validated = await TryLoadStateAsync(cancellationToken).ConfigureAwait(false);
            if (validated.Snapshot is null) return validated.Error!;
            if (SameSnapshot(snapshot, validated.Snapshot))
            {
                if (saved.Status is BrowserSetupStatus.Selected or BrowserSetupStatus.BrowserFamilyChoiceRequired)
                    return saved;
            }
            else
            {
                return await ObserveStableCurrentSelectionAsync(
                    validated.Snapshot, cancellationToken).ConfigureAwait(false);
            }
        }

        BrowserDirectoryIntentBeginResult begin;
        try
        {
            begin = await state.TryBeginEnsurePromptAsync(snapshot.Revision, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Result(BrowserSetupStatus.StateUnavailable);
        }

        if (begin.Status == BrowserDirectoryIntentBeginStatus.Conflict || begin.Intent is null)
            return Result(BrowserSetupStatus.StateChanged);
        return await RunClaimedSelectionAsync(begin.Intent, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(BrowserDirectoryStateSnapshot? Snapshot, BrowserSetupResult? Error)> TryLoadStateAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            return (await state.LoadAsync(cancellationToken).ConfigureAwait(false), null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return (null, Result(BrowserSetupStatus.StateUnavailable));
        }
    }

    private async Task<BrowserSetupResult> RunClaimedSelectionAsync(
        BrowserDirectorySelectionIntent intent,
        CancellationToken cancellationToken)
    {
        try
        {
            var selection = await SelectAndPersistAsync(intent, cancellationToken).ConfigureAwait(false);
            if (selection.IntentCommitted) return selection.Result;
            return await CleanupIntentAsync(intent).ConfigureAwait(false)
                ? selection.Result
                : Result(BrowserSetupStatus.CleanupFailed);
        }
        catch (OperationCanceledException)
        {
            if (!await CleanupIntentAsync(intent).ConfigureAwait(false))
                return Result(BrowserSetupStatus.CleanupFailed);
            throw;
        }
    }

    private async Task<SelectionAttemptResult> SelectAndPersistAsync(
        BrowserDirectorySelectionIntent intent,
        CancellationToken cancellationToken)
    {
        string? selectedDirectory;
        try
        {
            selectedDirectory = await dialogService.PickBrowserDirectoryAsync(cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Attempt(BrowserSetupStatus.DialogUnavailable);
        }

        if (selectedDirectory is null) return Attempt(BrowserSetupStatus.Cancelled);

        var inspected = await InspectAsync(selectedDirectory, cancellationToken).ConfigureAwait(false);
        if (inspected.Status is not (BrowserSetupStatus.Selected or BrowserSetupStatus.BrowserFamilyChoiceRequired))
            return new SelectionAttemptResult(inspected, false);

        try
        {
            var save = await state.TrySaveAsync(
                selectedDirectory, intent.Revision, intent, cancellationToken).ConfigureAwait(false);
            if (save.Status == BrowserDirectoryStateSaveStatus.Committed)
                return new SelectionAttemptResult(inspected, true);
            return new SelectionAttemptResult(
                await ResolveStateConflictAsync(cancellationToken).ConfigureAwait(false), false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Attempt(BrowserSetupStatus.StateUnavailable);
        }
    }

    private async Task<bool> CleanupIntentAsync(BrowserDirectorySelectionIntent intent)
    {
        for (var attempt = 0; attempt < MaximumIntentCleanupAttempts; attempt++)
        {
            try
            {
                if (await state.TryAbandonSelectionAsync(intent, CancellationToken.None)
                        .ConfigureAwait(false))
                {
                    return true;
                }
                break;
            }
            catch
            {
                // Confirm the durable state below after bounded retries.
            }
        }

        for (var attempt = 0; attempt < MaximumIntentCleanupAttempts; attempt++)
        {
            try
            {
                var snapshot = await state.LoadAsync(CancellationToken.None).ConfigureAwait(false);
                if (snapshot.PendingIntent is null || !SameIntent(snapshot.PendingIntent, intent))
                    return true;
            }
            catch
            {
                // A later read may still confirm whether the exact intent remains.
            }
        }

        return false;
    }

    private async Task<BrowserSetupResult> ResolveStateConflictAsync(CancellationToken cancellationToken)
    {
        var reloaded = await TryLoadStateAsync(cancellationToken).ConfigureAwait(false);
        if (reloaded.Snapshot is null) return reloaded.Error!;
        return await ObserveStableCurrentSelectionAsync(
            reloaded.Snapshot, cancellationToken).ConfigureAwait(false);
    }

    private async Task<BrowserSetupResult> ObserveStableCurrentSelectionAsync(
        BrowserDirectoryStateSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaximumStableObservationAttempts; attempt++)
        {
            if (snapshot.PendingIntent is not null || string.IsNullOrWhiteSpace(snapshot.Directory))
                return Result(BrowserSetupStatus.StateChanged);

            var observed = await InspectAsync(snapshot.Directory, cancellationToken).ConfigureAwait(false);
            var validated = await TryLoadStateAsync(cancellationToken).ConfigureAwait(false);
            if (validated.Snapshot is null) return validated.Error!;

            if (SameSnapshot(snapshot, validated.Snapshot))
            {
                return observed.Status is BrowserSetupStatus.Selected or BrowserSetupStatus.BrowserFamilyChoiceRequired
                    ? observed
                    : Result(BrowserSetupStatus.StateChanged);
            }

            snapshot = validated.Snapshot;
        }

        return Result(BrowserSetupStatus.StateChanged);
    }

    private async Task<BrowserSetupResult> InspectAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        if (!TryGetCanonicalDirectory(directory, out var canonicalDirectory))
            return Result(BrowserSetupStatus.InvalidDirectory);

        var chromePath = Path.Combine(canonicalDirectory, ChromeExecutableName);
        var edgePath = Path.Combine(canonicalDirectory, EdgeExecutableName);

        bool chromeExists;
        bool edgeExists;
        try
        {
            chromeExists = await fileSystem.FileExistsAsync(chromePath, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            edgeExists = await fileSystem.FileExistsAsync(edgePath, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Result(BrowserSetupStatus.DirectoryUnavailable);
        }

        var candidates = new List<BrowserDirectoryCandidate>(2);
        if (chromeExists)
            candidates.Add(new BrowserDirectoryCandidate(
                BrowserFamily.Chrome, canonicalDirectory, chromePath));
        if (edgeExists)
            candidates.Add(new BrowserDirectoryCandidate(
                BrowserFamily.Edge, canonicalDirectory, edgePath));

        return candidates.Count switch
        {
            0 => Result(BrowserSetupStatus.NoSupportedBrowser),
            1 => Result(BrowserSetupStatus.Selected, candidates),
            _ => Result(BrowserSetupStatus.BrowserFamilyChoiceRequired, candidates),
        };
    }

    private static bool TryGetCanonicalDirectory(string value, out string canonicalDirectory)
    {
        canonicalDirectory = string.Empty;
        if (string.IsNullOrWhiteSpace(value) ||
            value != value.Trim() ||
            value.Any(char.IsControl) ||
            Path.EndsInDirectorySeparator(value) ||
            !Path.IsPathFullyQualified(value))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(value);
            if (!string.Equals(value, fullPath, StringComparison.OrdinalIgnoreCase)) return false;
            canonicalDirectory = fullPath;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static BrowserSetupResult Result(
        BrowserSetupStatus status,
        IReadOnlyList<BrowserDirectoryCandidate>? candidates = null) => new(status, candidates);

    private static SelectionAttemptResult Attempt(BrowserSetupStatus status) =>
        new(Result(status), false);

    private static bool SameSnapshot(
        BrowserDirectoryStateSnapshot expected,
        BrowserDirectoryStateSnapshot actual) =>
        expected.Revision == actual.Revision &&
        string.Equals(expected.Directory, actual.Directory, StringComparison.Ordinal) &&
        SameIntent(expected.PendingIntent, actual.PendingIntent);

    private static bool SameIntent(
        BrowserDirectorySelectionIntent? expected,
        BrowserDirectorySelectionIntent? actual) =>
        expected is null
            ? actual is null
            : actual is not null && expected.Revision == actual.Revision && expected.Kind == actual.Kind;
}
