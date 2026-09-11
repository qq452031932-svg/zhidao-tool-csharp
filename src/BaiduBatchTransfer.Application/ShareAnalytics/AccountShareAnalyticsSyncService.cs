using System.Collections.Concurrent;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.ShareAnalytics;

public enum AccountShareSyncDisposition
{
    Updated,
    StaleResultIgnored,
    SessionUnavailable,
    LoginRequired,
    VerificationRequired,
    TemporarilyUnavailable,
    Unavailable,
}

public sealed record AccountShareSyncResult(
    AccountShareSyncDisposition Disposition,
    string SafeMessage)
{
    public override string ToString() => $"AccountShareSyncResult {{ Disposition = {Disposition} }}";
}

public interface IShareAnalyticsWorkspaceService
{
    Task<AccountShareCachedSnapshot> LoadCachedAsync(Guid accountId, CancellationToken cancellationToken = default);
    Task<AccountShareSyncResult> SyncAsync(Guid accountId, CancellationToken cancellationToken = default);
}

public sealed class AccountShareAnalyticsSyncService(
    IBaiduSessionStore sessions,
    IAccountShareAnalyticsClient client,
    IAccountShareAnalyticsRepository repository,
    TimeProvider timeProvider) : IShareAnalyticsWorkspaceService
{
    private const int MaximumPages = 10_000;
    private readonly IBaiduSessionStore sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    private readonly IAccountShareAnalyticsClient client = client ?? throw new ArgumentNullException(nameof(client));
    private readonly IAccountShareAnalyticsRepository repository = repository ?? throw new ArgumentNullException(nameof(repository));
    private readonly TimeProvider timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> accountGates = new();

    public Task<AccountShareCachedSnapshot> LoadCachedAsync(
        Guid accountId,
        CancellationToken cancellationToken = default)
    {
        ValidateAccountId(accountId);
        return repository.LoadCachedAsync(accountId, cancellationToken);
    }

    public async Task<AccountShareSyncResult> SyncAsync(
        Guid accountId,
        CancellationToken cancellationToken = default)
    {
        ValidateAccountId(accountId);
        var gate = accountGates.GetOrAdd(accountId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await SyncCoreAsync(accountId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<AccountShareSyncResult> SyncCoreAsync(Guid accountId, CancellationToken cancellationToken)
    {
        try
        {
            var generation = await repository.ReserveGenerationAsync(accountId, cancellationToken).ConfigureAwait(false);
            var session = await sessions.LoadAsync(accountId.ToString("D"), cancellationToken).ConfigureAwait(false);
            if (session?.LocalAccountId != accountId || string.IsNullOrWhiteSpace(session.BaiduUserId))
                return Result(AccountShareSyncDisposition.SessionUnavailable);

            var items = new List<AccountShareObservation>();
            var identities = new HashSet<string>(StringComparer.Ordinal);
            var continuations = new HashSet<string>(StringComparer.Ordinal);
            string? cursor = null;
            for (var pageNumber = 1; pageNumber <= MaximumPages; pageNumber++)
            {
                var response = await client.ReadPageAsync(session, cursor, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccess) return Result(Map(response.ErrorKind));
                var page = response.Value!;
                foreach (var item in page.Items)
                {
                    if (!identities.Add(item.ShareId)) return Result(AccountShareSyncDisposition.Unavailable);
                    items.Add(item);
                }
                if (page.NextCursor is null)
                {
                    var commit = new AccountShareSyncCommit(
                        accountId, generation, timeProvider.GetUtcNow(), items);
                    return await repository.CommitCompleteSyncAsync(commit, cancellationToken).ConfigureAwait(false)
                        ? Result(AccountShareSyncDisposition.Updated)
                        : Result(AccountShareSyncDisposition.StaleResultIgnored);
                }
                if (!continuations.Add(page.NextCursor))
                    return Result(AccountShareSyncDisposition.Unavailable);
                cursor = page.NextCursor;
            }
            return Result(AccountShareSyncDisposition.Unavailable);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Result(AccountShareSyncDisposition.TemporarilyUnavailable);
        }
    }

    private static AccountShareSyncDisposition Map(BaiduErrorKind? errorKind) => errorKind switch
    {
        BaiduErrorKind.ExpiredLogin => AccountShareSyncDisposition.LoginRequired,
        BaiduErrorKind.VerificationRequired => AccountShareSyncDisposition.VerificationRequired,
        BaiduErrorKind.TransientService => AccountShareSyncDisposition.TemporarilyUnavailable,
        _ => AccountShareSyncDisposition.Unavailable,
    };

    private static AccountShareSyncResult Result(AccountShareSyncDisposition disposition) =>
        new(disposition, disposition switch
        {
            AccountShareSyncDisposition.Updated => "分享数据已更新。",
            AccountShareSyncDisposition.StaleResultIgnored => "已保留较新的分享数据。",
            AccountShareSyncDisposition.SessionUnavailable or AccountShareSyncDisposition.LoginRequired => "账号登录状态不可用，请重新登录。",
            AccountShareSyncDisposition.VerificationRequired => "请先完成百度安全验证。",
            AccountShareSyncDisposition.TemporarilyUnavailable => "分享数据获取失败，请稍后重试。",
            _ => "暂时无法读取分享数据。",
        });

    private static void ValidateAccountId(Guid accountId)
    {
        if (accountId == Guid.Empty) throw new ArgumentException("An account id is required.", nameof(accountId));
    }
}
