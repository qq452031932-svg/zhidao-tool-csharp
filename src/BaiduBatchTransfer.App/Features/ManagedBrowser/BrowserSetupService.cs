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
        return await Task.Run(
            () =>
            {
                var exists = exactFileProbe.Exists(exactPath);
                cancellationToken.ThrowIfCancellationRequested();
                return exists;
            },
            cancellationToken).ConfigureAwait(false);
    }
}

public interface IBrowserDirectoryState
{
    Task<BrowserDirectoryStateSnapshot> LoadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Atomically and durably advances the revision and records a pending explicit-reselect
    /// intent before returning. A later call supersedes every earlier pending intent.
    /// </summary>
    Task<BrowserDirectoryReselectIntent> BeginReselectAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Atomically commits the directory only when <paramref name="expectedRevision"/> is current.
    /// An explicit reselect additionally requires its exact still-pending intent. Committed means
    /// the durable commit point has passed, even if the caller token is cancelled immediately after.
    /// </summary>
    Task<BrowserDirectoryStateSaveResult> TrySaveAsync(
        string selectedDirectory,
        long expectedRevision,
        BrowserDirectoryReselectIntent? reselectIntent,
        CancellationToken cancellationToken);

    /// <summary>
    /// Clears only the exact pending intent and advances the revision; it must not affect a newer intent.
    /// </summary>
    Task<bool> TryAbandonReselectAsync(
        BrowserDirectoryReselectIntent reselectIntent,
        CancellationToken cancellationToken);
}

public sealed class BrowserDirectoryReselectIntent
{
    public BrowserDirectoryReselectIntent(long revision)
    {
        if (revision < 0) throw new ArgumentOutOfRangeException(nameof(revision));
        Revision = revision;
    }

    public long Revision { get; }

    public override string ToString() =>
        $"BrowserDirectoryReselectIntent {{ Revision = {Revision} }}";
}

public sealed class BrowserDirectoryStateSnapshot
{
    public BrowserDirectoryStateSnapshot(
        string? directory,
        long revision,
        BrowserDirectoryReselectIntent? pendingReselect = null)
    {
        if (revision < 0) throw new ArgumentOutOfRangeException(nameof(revision));
        Directory = directory;
        Revision = revision;
        PendingReselect = pendingReselect;
    }

    public string? Directory { get; }
    public long Revision { get; }
    public BrowserDirectoryReselectIntent? PendingReselect { get; }

    public override string ToString() =>
        $"BrowserDirectoryStateSnapshot {{ Directory = [REDACTED], Revision = {Revision}, ReselectPending = {PendingReselect is not null} }}";
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
    private readonly SemaphoreSlim flowGate = new(1, 1);

    private sealed record SelectionAttemptResult(
        BrowserSetupResult Result,
        bool ReselectIntentCommitted);

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
            BrowserDirectoryReselectIntent intent;
            try
            {
                intent = await state.BeginReselectAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return Result(BrowserSetupStatus.StateUnavailable);
            }

            var intentCommitted = false;
            try
            {
                var selection = await SelectAndPersistAsync(
                    intent.Revision, intent, cancellationToken).ConfigureAwait(false);
                intentCommitted = selection.ReselectIntentCommitted;
                return selection.Result;
            }
            finally
            {
                if (!intentCommitted)
                {
                    try
                    {
                        await state.TryAbandonReselectAsync(intent, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        // A newer intent safely supersedes this one; cleanup is best effort.
                    }
                }
            }
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

        if (snapshot.PendingReselect is not null)
            return Result(BrowserSetupStatus.StateChanged);

        if (!string.IsNullOrWhiteSpace(snapshot.Directory))
        {
            var saved = await InspectAsync(snapshot.Directory, cancellationToken).ConfigureAwait(false);
            if (saved.Status is BrowserSetupStatus.Selected or BrowserSetupStatus.BrowserFamilyChoiceRequired)
                return saved;
        }

        return (await SelectAndPersistAsync(
            snapshot.Revision, null, cancellationToken).ConfigureAwait(false)).Result;
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

    private async Task<SelectionAttemptResult> SelectAndPersistAsync(
        long expectedRevision,
        BrowserDirectoryReselectIntent? reselectIntent,
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
                selectedDirectory, expectedRevision, reselectIntent, cancellationToken).ConfigureAwait(false);
            if (save.Status == BrowserDirectoryStateSaveStatus.Committed)
                return new SelectionAttemptResult(inspected, reselectIntent is not null);
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

    private async Task<BrowserSetupResult> ResolveStateConflictAsync(CancellationToken cancellationToken)
    {
        var reloaded = await TryLoadStateAsync(cancellationToken).ConfigureAwait(false);
        if (reloaded.Snapshot is null) return reloaded.Error!;
        if (reloaded.Snapshot.PendingReselect is not null)
            return Result(BrowserSetupStatus.StateChanged);
        if (string.IsNullOrWhiteSpace(reloaded.Snapshot.Directory))
            return Result(BrowserSetupStatus.StateChanged);

        var current = await InspectAsync(reloaded.Snapshot.Directory, cancellationToken).ConfigureAwait(false);
        return current.Status is BrowserSetupStatus.Selected or BrowserSetupStatus.BrowserFamilyChoiceRequired
            ? current
            : Result(BrowserSetupStatus.StateChanged);
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
}
