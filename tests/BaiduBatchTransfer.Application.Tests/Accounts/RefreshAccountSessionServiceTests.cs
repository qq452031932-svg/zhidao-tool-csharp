using System.Collections.Immutable;
using BaiduBatchTransfer.Application.Accounts;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Tests.Accounts;

public sealed class RefreshAccountSessionServiceTests
{
    [Fact]
    public async Task MatchingIdentityReplacesTheEncryptedBoundSession()
    {
        var accountId = Guid.NewGuid();
        var store = new SessionStore(Bound(accountId, "123", Minute(0)));
        var service = new RefreshAccountSessionService(new Adapter("123"), store);

        var result = await service.RefreshAsync(accountId, Unbound(Minute(1)), default);

        Assert.Equal(RefreshAccountSessionStatus.Refreshed, result.Status);
        var saved = Assert.IsType<AccountSession>(store.Value);
        Assert.True(saved.IsBoundTo(accountId, "123"));
        Assert.Equal(Minute(1), saved.CapturedAtUtc);
        Assert.Equal(1, store.SaveCount);
    }

    [Fact]
    public async Task DifferentBaiduIdentityDoesNotReplaceTheExistingSession()
    {
        var accountId = Guid.NewGuid();
        var original = Bound(accountId, "123", Minute(0));
        var store = new SessionStore(original);
        var service = new RefreshAccountSessionService(new Adapter("456"), store);

        var result = await service.RefreshAsync(accountId, Unbound(Minute(1)), default);

        Assert.Equal(RefreshAccountSessionStatus.IdentityMismatch, result.Status);
        Assert.Same(original, store.Value);
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public async Task MissingExistingBoundSessionFailsClosedBeforeValidation()
    {
        var adapter = new Adapter("123");
        var service = new RefreshAccountSessionService(adapter, new SessionStore());

        var result = await service.RefreshAsync(Guid.NewGuid(), Unbound(Minute(1)), default);

        Assert.Equal(RefreshAccountSessionStatus.AccountUnavailable, result.Status);
        Assert.Equal(0, adapter.ValidateCount);
    }

    private static DateTimeOffset Minute(int value) =>
        DateTimeOffset.Parse("2026-09-01T01:00:00Z").AddMinutes(value);

    private static AccountSession Bound(Guid accountId, string userId, DateTimeOffset capturedAt) =>
        new(accountId, userId, Cookies(), capturedAt);

    private static AccountSession Unbound(DateTimeOffset capturedAt) =>
        new("capture", Cookies(), capturedAt);

    private static SessionCookie[] Cookies() =>
        [new("BDUSS", "synthetic", ".baidu.com", "/", null, true, true)];

    private sealed class SessionStore(AccountSession? initial = null) : IBaiduSessionStore
    {
        public AccountSession? Value { get; private set; } = initial;
        public int SaveCount { get; private set; }

        public Task SaveAsync(AccountSession session, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Value = session;
            SaveCount++;
            return Task.CompletedTask;
        }

        public Task<AccountSession?> LoadAsync(string accountId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Value?.AccountId == accountId ? Value : null);
        }

        public Task RemoveAsync(string accountId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class Adapter(string userId) : IBaiduNetdiskAdapter
    {
        public int ValidateCount { get; private set; }

        public Task<BaiduAdapterResult<BaiduAccountIdentity>> ValidateSessionAsync(
            AccountSession session,
            CancellationToken cancellationToken)
        {
            ValidateCount++;
            return Task.FromResult(BaiduAdapterResult<BaiduAccountIdentity>.Success(
                new BaiduAccountIdentity(userId, "Synthetic")));
        }

        public Task<BaiduAdapterResult<AccountCapacity>> GetCapacityAsync(AccountSession session, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<ImmutableArray<RemoteDirectory>>> ListDirectoriesAsync(AccountSession session, RemotePath path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<RemoteDirectory>> EnsureDirectoryAsync(AccountSession session, RemotePath path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<ShareMetadata>> InspectShareAsync(AccountSession session, ShareReference share, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<TransferReceipt>> SaveWholeShareAsync(AccountSession session, ShareReference share, RemoteDirectory destination, DuplicatePolicy duplicatePolicy, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<TransferConfirmation>> ConfirmTransferAsync(AccountSession session, TransferConfirmationQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
