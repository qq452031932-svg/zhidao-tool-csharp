using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.Infrastructure.Security;

public sealed class FileSystemAccountProfilePathPolicy : IAccountProfilePathPolicy
{
    private readonly string canonicalRoot;

    public FileSystemAccountProfilePathPolicy(string configuredRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredRoot);
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

    private static void EnsureExistingComponentsAreDirectoriesWithoutReparsePoints(string fullPath)
    {
        string? current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fullPath));
        while (!string.IsNullOrEmpty(current))
        {
            var isDirectory = Directory.Exists(current);
            var isFile = File.Exists(current);
            if (isDirectory || isFile)
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Profile paths cannot contain reparse points or junctions.");
                if (isFile)
                    throw new IOException("Every existing profile path component must be a directory.");
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                break;
            current = Path.TrimEndingDirectorySeparator(parent);
        }
    }
}
