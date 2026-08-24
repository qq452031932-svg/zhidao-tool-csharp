using System.IO;

namespace BaiduBatchTransfer.App.Features.Acceptance;

public interface IBaiduAcceptanceProbeProfilePathPolicy
{
    BaiduAcceptanceProbeProfileLease Create(Guid accountId);
}

public sealed class BaiduAcceptanceProbeProfileLease(string profilePath, Action release) : IAsyncDisposable
{
    private Action? release = release ?? throw new ArgumentNullException(nameof(release));
    public string ProfilePath { get; } = Path.GetFullPath(profilePath);

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref release, null)?.Invoke();
        return ValueTask.CompletedTask;
    }
}

public sealed class BaiduAcceptanceProbeProfilePathPolicy(string dataRoot) : IBaiduAcceptanceProbeProfilePathPolicy
{
    private readonly string rootDirectory = Path.Combine(Path.GetFullPath(dataRoot), "acceptance-probe");

    public BaiduAcceptanceProbeProfileLease Create(Guid accountId)
    {
        if (accountId == Guid.Empty) throw new ArgumentException("An account ID is required.", nameof(accountId));
        Directory.CreateDirectory(rootDirectory);
        var profilePath = Path.Combine(rootDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profilePath);
        return new(profilePath, () => DeleteIsolatedProfile(profilePath));
    }

    private void DeleteIsolatedProfile(string profilePath)
    {
        var relativePath = Path.GetRelativePath(rootDirectory, profilePath);
        if (relativePath.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relativePath))
            throw new InvalidOperationException("The probe profile is outside its isolated root.");
        if (Directory.Exists(profilePath)) Directory.Delete(profilePath, recursive: true);
    }
}
