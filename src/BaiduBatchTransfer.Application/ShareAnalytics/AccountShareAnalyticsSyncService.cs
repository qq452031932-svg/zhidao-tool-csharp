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

/// <param name="Detail">失败时给运行日志的原因（第几页、已读多少条、哪种异常），不含 Cookie 和链接；界面不显示。</param>
public sealed record AccountShareSyncResult(
    AccountShareSyncDisposition Disposition,
    string SafeMessage,
    string? Detail = null)
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
    /// <summary>
    /// 跨页重复的上限。百度按创建时间翻页，批量分享时很多条同一秒创建，排在页边界上的那几条
    /// 前后两页可能各出现一次（实测 6,798 条里 907 条同秒、3 处跨页边界）。少量重复按同一条去重；
    /// 多到一整页都是重复，说明翻页本身不对，仍然作废。
    /// </summary>
    private const int MaximumDuplicateItems = 100;
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
            var duplicates = 0;
            string? cursor = null;
            for (var pageNumber = 1; pageNumber <= MaximumPages; pageNumber++)
            {
                var response = await client.ReadPageAsync(session, cursor, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccess)
                    return Result(Map(response.ErrorKind),
                        $"第 {pageNumber} 页读取失败（{response.ErrorKind}），之前已读 {items.Count} 条");
                var page = response.Value!;
                foreach (var item in page.Items)
                {
                    if (!identities.Add(item.ShareId))
                    {
                        if (++duplicates > MaximumDuplicateItems)
                            return Result(AccountShareSyncDisposition.Unavailable,
                                $"翻页重复超过 {MaximumDuplicateItems} 条（到第 {pageNumber} 页），之前已读 {items.Count} 条");
                        continue;
                    }
                    items.Add(item);
                }
                if (page.NextCursor is null)
                {
                    var commit = new AccountShareSyncCommit(
                        accountId, generation, timeProvider.GetUtcNow(), items);
                    // 有重复时，同一秒里可能有相同条数这次没读到（会暂时不显示），下次刷新通常能补上。
                    var note = duplicates == 0 ? null : $"翻页时 {duplicates} 条分享前后两页重复出现，已按同一条计；可能有同样条数这次没读到";
                    return await repository.CommitCompleteSyncAsync(commit, cancellationToken).ConfigureAwait(false)
                        ? Result(AccountShareSyncDisposition.Updated, note)
                        : Result(AccountShareSyncDisposition.StaleResultIgnored, note);
                }
                if (!continuations.Add(page.NextCursor))
                    return Result(AccountShareSyncDisposition.Unavailable, $"第 {pageNumber} 页的下一页游标重复");
                cursor = page.NextCursor;
            }
            return Result(AccountShareSyncDisposition.Unavailable, $"超过 {MaximumPages} 页上限");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            return Result(AccountShareSyncDisposition.TemporarilyUnavailable, $"读取或保存时出错：{error.GetType().Name}");
        }
    }

    private static AccountShareSyncDisposition Map(BaiduErrorKind? errorKind) => errorKind switch
    {
        BaiduErrorKind.ExpiredLogin => AccountShareSyncDisposition.LoginRequired,
        BaiduErrorKind.VerificationRequired => AccountShareSyncDisposition.VerificationRequired,
        BaiduErrorKind.TransientService => AccountShareSyncDisposition.TemporarilyUnavailable,
        _ => AccountShareSyncDisposition.Unavailable,
    };

    private static AccountShareSyncResult Result(AccountShareSyncDisposition disposition, string? detail = null) =>
        new(disposition, disposition switch
        {
            AccountShareSyncDisposition.Updated => "分享数据已更新。",
            AccountShareSyncDisposition.StaleResultIgnored => "已保留较新的分享数据。",
            AccountShareSyncDisposition.SessionUnavailable or AccountShareSyncDisposition.LoginRequired => "账号登录状态不可用，请重新登录。",
            AccountShareSyncDisposition.VerificationRequired => "请先完成百度安全验证。",
            AccountShareSyncDisposition.TemporarilyUnavailable => "分享数据获取失败，请稍后重试。",
            _ => "暂时无法读取分享数据。",
        }, detail);

    private static void ValidateAccountId(Guid accountId)
    {
        if (accountId == Guid.Empty) throw new ArgumentException("An account id is required.", nameof(accountId));
    }
}
