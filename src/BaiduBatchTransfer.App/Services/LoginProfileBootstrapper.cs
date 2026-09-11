using BaiduBatchTransfer.Core.Abstractions;
using System.IO;

namespace BaiduBatchTransfer.App.Services;

public sealed record LoginProfileLease(Guid ProfileId, string Path, Guid OwnershipToken);

public sealed class LoginProfileBootstrapper(
    IAccountProfilePathPolicy pathPolicy,
    IWebViewProfileStore profileStore)
{
    private static readonly TimeSpan[] BrowserExitRetryDelays =
    [
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
    ];

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

    public async Task<LoginProfileLease> PrepareAttemptAsync(
        Guid ownershipToken,
        CancellationToken cancellationToken)
    {
        if (ownershipToken == Guid.Empty)
            throw new ArgumentException("An ownership token is required.", nameof(ownershipToken));
        var path = await PrepareAsync(
            ownershipToken,
            ownershipToken,
            cancellationToken).ConfigureAwait(false);
        return new(ownershipToken, path, ownershipToken);
    }

    public async Task<OwnedProfileDeletionStatus> CleanupAttemptAsync(
        LoginProfileLease lease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        pathPolicy.EnsureSafeAccountPath(lease.ProfileId, lease.Path);
        await profileStore.CloseAsync(lease.ProfileId, cancellationToken).ConfigureAwait(false);
        return await profileStore.DeleteOwnedAsync(
            lease.ProfileId,
            lease.Path,
            lease.OwnershipToken,
            _ => Task.FromResult(true),
            cancellationToken).ConfigureAwait(false);
    }

    public Task<OwnedProfileDeletionStatus> CleanupAttemptAfterBrowserExitAsync(
        LoginProfileLease lease,
        CancellationToken cancellationToken) =>
        CleanupAttemptAfterBrowserExitAsync(
            lease,
            static (delay, token) => Task.Delay(delay, token),
            cancellationToken);

    internal async Task<OwnedProfileDeletionStatus> CleanupAttemptAfterBrowserExitAsync(
        LoginProfileLease lease,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(delayAsync);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await CleanupAttemptAsync(lease, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException) when (attempt < BrowserExitRetryDelays.Length)
            {
                await delayAsync(BrowserExitRetryDelays[attempt], cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }
}
