using System.IO;

namespace BaiduBatchTransfer.App.Features.Acceptance;

public interface IBaiduAcceptanceProbeProfilePathPolicy
{
    BaiduAcceptanceProbeProfileLease Create(Guid accountId);
}

public enum BaiduAcceptanceProbeCleanupOutcome { Cleaned, Deferred }

public sealed class BaiduAcceptanceProbeProfileLease(string profilePath, Func<Task<BaiduAcceptanceProbeCleanupOutcome>> release) : IAsyncDisposable
{
    private Func<Task<BaiduAcceptanceProbeCleanupOutcome>>? release = release ?? throw new ArgumentNullException(nameof(release));
    public string ProfilePath { get; } = Path.GetFullPath(profilePath);
    public BaiduAcceptanceProbeCleanupOutcome CleanupOutcome { get; private set; } = BaiduAcceptanceProbeCleanupOutcome.Cleaned;

    public BaiduAcceptanceProbeProfileLease(string profilePath, Action release) : this(
        profilePath,
        () => { release(); return Task.FromResult(BaiduAcceptanceProbeCleanupOutcome.Cleaned); }) { }

    public async ValueTask DisposeAsync()
    {
        var cleanup = Interlocked.Exchange(ref release, null);
        if (cleanup is not null) CleanupOutcome = await cleanup().ConfigureAwait(false);
    }
}

public sealed class BaiduAcceptanceProbeProfilePathPolicy(string dataRoot) : IBaiduAcceptanceProbeProfilePathPolicy
{
    private readonly string rootDirectory = Path.Combine(Path.GetFullPath(dataRoot), "acceptance-probe");

    public BaiduAcceptanceProbeProfileLease Create(Guid accountId)
    {
        if (accountId == Guid.Empty) throw new ArgumentException("An account ID is required.", nameof(accountId));
        Directory.CreateDirectory(rootDirectory);
        ScavengeQuarantine();
        var profilePath = Path.Combine(rootDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profilePath);
        return new(profilePath, () => DeleteIsolatedProfileAsync(profilePath));
    }

    private async Task<BaiduAcceptanceProbeCleanupOutcome> DeleteIsolatedProfileAsync(string profilePath)
    {
        var relativePath = Path.GetRelativePath(rootDirectory, profilePath);
        if (relativePath.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relativePath))
            return BaiduAcceptanceProbeCleanupOutcome.Deferred;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(profilePath)) Directory.Delete(profilePath, recursive: true);
                return BaiduAcceptanceProbeCleanupOutcome.Cleaned;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50 * (attempt + 1))).ConfigureAwait(false);
            }
        }
        try
        {
            var quarantine = Path.Combine(rootDirectory, "quarantine", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.GetDirectoryName(quarantine)!);
            if (Directory.Exists(profilePath)) Directory.Move(profilePath, quarantine);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        return BaiduAcceptanceProbeCleanupOutcome.Deferred;
    }

    private void ScavengeQuarantine()
    {
        var quarantineRoot = Path.Combine(rootDirectory, "quarantine");
        if (!Directory.Exists(quarantineRoot)) return;
        foreach (var candidate in Directory.EnumerateDirectories(quarantineRoot).Take(8))
        {
            var relative = Path.GetRelativePath(quarantineRoot, candidate);
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)) continue;
            try { Directory.Delete(candidate, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }
}
