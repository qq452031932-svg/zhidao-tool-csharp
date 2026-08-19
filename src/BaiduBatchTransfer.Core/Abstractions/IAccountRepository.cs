namespace BaiduBatchTransfer.Core.Abstractions;

public interface IAccountRepository
{
    Task UpsertAsync(AccountRecord account, CancellationToken cancellationToken = default);
    Task<AccountRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<AccountRecord?> GetByBaiduUserIdAsync(string baiduUserId, CancellationToken cancellationToken = default);
    Task SetEncryptedSessionAsync(Guid id, byte[]? encryptedSession, CancellationToken cancellationToken = default);
    Task<Page<AccountRecord>> GetPageAsync(PageQuery query, CancellationToken cancellationToken = default);
    Task UpsertRuleAsync(AccountRuleRecord rule, CancellationToken cancellationToken = default);
    Task<Page<AccountRuleRecord>> GetRulesPageAsync(Guid accountId, PageQuery query, CancellationToken cancellationToken = default);
}
