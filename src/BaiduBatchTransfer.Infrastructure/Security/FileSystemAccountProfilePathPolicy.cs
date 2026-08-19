using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.Infrastructure.Security;

public sealed class FileSystemAccountProfilePathPolicy : IAccountProfilePathPolicy
{
    private readonly string canonicalRoot;
    private readonly IProfilePathFileSystem fileSystem;

    public FileSystemAccountProfilePathPolicy(string configuredRoot) :
        this(configuredRoot, PhysicalProfilePathFileSystem.Instance)
    {
    }

    internal FileSystemAccountProfilePathPolicy(
        string configuredRoot,
        IProfilePathFileSystem fileSystem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredRoot);
        this.fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuredRoot));
        if (string.Equals(Path.GetPathRoot(canonicalRoot), canonicalRoot, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The profile root cannot be a filesystem root.", nameof(configuredRoot));
        EnsureExistingComponentsAreDirectoriesWithoutReparsePoints(canonicalRoot);
    }

    public string GetAccountProfilePath(Guid accountId)
    {
        if (accountId == Guid.Empty) throw new ArgumentException("A local account ID is required.", nameof(accountId));
        var path = Path.GetFullPath(Path.Combine(canonicalRoot, accountId.ToString("D")));
        EnsureSafeAccountPath(accountId, path);
        return path;
    }

    internal string CanonicalRoot => canonicalRoot;

    internal void EnsureSafeRoot() =>
        EnsureExistingComponentsAreDirectoriesWithoutReparsePoints(canonicalRoot);

    public void EnsureSafeAccountPath(Guid accountId, string candidatePath)
    {
        if (accountId == Guid.Empty) throw new ArgumentException("A local account ID is required.", nameof(accountId));
        ArgumentException.ThrowIfNullOrWhiteSpace(candidatePath);

        if (!Path.IsPathFullyQualified(candidatePath))
            throw new IOException("The account profile path must be fully qualified.");
        var normalizedInput = Path.TrimEndingDirectorySeparator(candidatePath);
        var canonicalCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidatePath));
        if (!string.Equals(normalizedInput, canonicalCandidate, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The account profile path must be canonical.");
        var parent = Path.GetDirectoryName(canonicalCandidate);
        if (!string.Equals(parent, canonicalRoot, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(canonicalCandidate), accountId.ToString("D"), StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("The account profile path escaped its configured root.");
        }

        EnsureExistingComponentsAreDirectoriesWithoutReparsePoints(canonicalCandidate);
    }

    private void EnsureExistingComponentsAreDirectoriesWithoutReparsePoints(string fullPath)
    {
        string? current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fullPath));
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                var attributes = fileSystem.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Profile paths cannot contain reparse points or junctions.");
                if ((attributes & FileAttributes.Directory) == 0)
                    throw new IOException("Every existing profile path component must be a directory.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                break;
            current = Path.TrimEndingDirectorySeparator(parent);
        }
    }
}

internal interface IProfilePathFileSystem
{
    FileAttributes GetAttributes(string path);
}

internal sealed class PhysicalProfilePathFileSystem : IProfilePathFileSystem
{
    public static PhysicalProfilePathFileSystem Instance { get; } = new();

    private PhysicalProfilePathFileSystem() { }

    public FileAttributes GetAttributes(string path) => File.GetAttributes(path);
}
