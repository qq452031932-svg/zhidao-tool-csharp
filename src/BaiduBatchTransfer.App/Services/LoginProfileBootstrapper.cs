using BaiduBatchTransfer.Core.Abstractions;
using System.IO;

namespace BaiduBatchTransfer.App.Services;

public sealed class LoginProfileBootstrapper(
    IAccountProfilePathPolicy pathPolicy,
    IWebViewProfileStore profileStore)
{
    private readonly IAccountProfilePathPolicy pathPolicy =
        pathPolicy ?? throw new ArgumentNullException(nameof(pathPolicy));
    private readonly IWebViewProfileStore profileStore =
        profileStore ?? throw new ArgumentNullException(nameof(profileStore));

    public async Task<string> PrepareAsync(
        Guid accountId,
        Guid ownershipToken,
        CancellationToken cancellationToken)
    {
        if (accountId == Guid.Empty) throw new ArgumentException("An account ID is required.", nameof(accountId));
        if (ownershipToken == Guid.Empty) throw new ArgumentException("An ownership token is required.", nameof(ownershipToken));
        var path = pathPolicy.GetAccountProfilePath(accountId);
        pathPolicy.EnsureSafeAccountPath(accountId, path);
        var existed = Directory.Exists(path);
        Directory.CreateDirectory(path);
        try
        {
            pathPolicy.EnsureSafeAccountPath(accountId, path);
            await profileStore.RegisterOwnershipAsync(accountId, path, ownershipToken, cancellationToken);
            return path;
        }
        catch
        {
            if (!existed)
            {
                try
                {
                    pathPolicy.EnsureSafeAccountPath(accountId, path);
                    Directory.Delete(path, recursive: false);
                }
                catch (Exception) { /* Preserve the ownership registration failure. */ }
            }
            throw;
        }
    }
}
