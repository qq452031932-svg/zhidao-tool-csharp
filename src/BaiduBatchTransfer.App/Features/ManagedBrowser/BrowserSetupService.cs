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

public sealed class BrowserDirectoryFileSystem : IBrowserDirectoryFileSystem
{
    public Task<bool> FileExistsAsync(string exactPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exactPath);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(File.Exists(exactPath));
    }
}

public interface IBrowserDirectoryState
{
    Task<string?> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(string selectedDirectory, CancellationToken cancellationToken);
}

public sealed class BrowserSetupService(
    IDialogService dialogService,
    IBrowserDirectoryFileSystem fileSystem,
    IBrowserDirectoryState state)
{
    private const string ChromeExecutableName = "chrome.exe";
    private const string EdgeExecutableName = "msedge.exe";

    public async Task<BrowserSetupResult> EnsureSelectedAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string? savedDirectory;
        try
        {
            savedDirectory = await state.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Result(BrowserSetupStatus.StateUnavailable);
        }

        if (!string.IsNullOrWhiteSpace(savedDirectory))
        {
            var saved = await InspectAsync(savedDirectory, cancellationToken).ConfigureAwait(false);
            if (saved.Status is BrowserSetupStatus.Selected or BrowserSetupStatus.BrowserFamilyChoiceRequired)
                return saved;
        }

        return await SelectAndPersistAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<BrowserSetupResult> ReselectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return SelectAndPersistAsync(cancellationToken);
    }

    private async Task<BrowserSetupResult> SelectAndPersistAsync(CancellationToken cancellationToken)
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
            return Result(BrowserSetupStatus.DialogUnavailable);
        }

        if (selectedDirectory is null) return Result(BrowserSetupStatus.Cancelled);

        var inspected = await InspectAsync(selectedDirectory, cancellationToken).ConfigureAwait(false);
        if (inspected.Status is not (BrowserSetupStatus.Selected or BrowserSetupStatus.BrowserFamilyChoiceRequired))
            return inspected;

        try
        {
            await state.SaveAsync(selectedDirectory, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Result(BrowserSetupStatus.StateUnavailable);
        }

        return inspected;
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
}
