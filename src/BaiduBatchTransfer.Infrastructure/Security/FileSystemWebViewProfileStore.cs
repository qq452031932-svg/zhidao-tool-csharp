using BaiduBatchTransfer.Core.Abstractions;

namespace BaiduBatchTransfer.Infrastructure.Security;

public sealed class FileSystemWebViewProfileStore(
    FileSystemAccountProfilePathPolicy pathPolicy,
    IWebViewProfileCloser profileCloser) : IWebViewProfileStore
{
    private readonly FileSystemAccountProfilePathPolicy pathPolicy =
        pathPolicy ?? throw new ArgumentNullException(nameof(pathPolicy));
    private readonly IWebViewProfileCloser profileCloser =
        profileCloser ?? throw new ArgumentNullException(nameof(profileCloser));

    public Task CloseAsync(Guid accountId, CancellationToken cancellationToken) =>
        profileCloser.CloseAsync(accountId, cancellationToken);

    public Task DeleteAsync(Guid accountId, string exactProfilePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Revalidate at the destructive boundary. The earlier application-service check is not trusted.
        pathPolicy.EnsureSafeAccountPath(accountId, exactProfilePath);
        if (Directory.Exists(exactProfilePath))
            Directory.Delete(exactProfilePath, recursive: true);
        else if (File.Exists(exactProfilePath))
            throw new IOException("The account profile path is not a directory.");

        return Task.CompletedTask;
    }
}
