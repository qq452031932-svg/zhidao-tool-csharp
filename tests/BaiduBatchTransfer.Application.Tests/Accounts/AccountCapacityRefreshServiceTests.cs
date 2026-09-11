using System.Collections.Immutable;
using BaiduBatchTransfer.Application.Accounts;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Tests.Accounts;

public sealed class AccountCapacityRefreshServiceTests
{
    private static readonly Guid AccountId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 8, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task ValidBoundSessionReadsPersistsAndReturnsCapacity()
    {
        var sessions = new FakeSessionStore { Session = Session(AccountId, "baidu-user") };
        var adapter = new FakeAdapter
        {
            CapacityResult = BaiduAdapterResult<AccountCapacity>.Success(new(100, 25)),
            IdentityResult = BaiduAdapterResult<BaiduAccountIdentity>.Success(new(
                "baidu-user",
                "测试账号",
                new Uri("https://himg.bdimg.com/sys/portrait/item/test.jpg"))),
        };
        var capacities = new FakeCapacityStore();
        var service = Service(sessions, adapter, capacities);

        var result = await service.RefreshAsync(AccountId);

        Assert.Equal(AccountCapacityRefreshDisposition.Updated, result.Disposition);
        Assert.Equal(100, result.Capacity!.TotalBytes);
        Assert.Equal(25, result.Capacity.UsedBytes);
        Assert.Equal(new Uri("https://himg.bdimg.com/sys/portrait/item/test.jpg"), result.AvatarUri);
        Assert.Equal(AccountId, capacities.AccountId);
        Assert.Equal("baidu-user", capacities.BaiduUserId);
        Assert.Equal(Now, capacities.ValidatedAt);
        Assert.Equal(1, adapter.CapacityCalls);
        Assert.Equal(1, adapter.IdentityCalls);
    }

    [Fact]
    public async Task RefreshedIdentityMustStillMatchTheBoundAccount()
    {
        var adapter = new FakeAdapter
        {
            CapacityResult = BaiduAdapterResult<AccountCapacity>.Success(new(100, 25)),
            IdentityResult = BaiduAdapterResult<BaiduAccountIdentity>.Success(new(
                "different-user",
                "其他账号",
                new Uri("https://himg.bdimg.com/sys/portrait/item/other.jpg"))),
        };
        var capacities = new FakeCapacityStore();

        var result = await Service(
            new FakeSessionStore { Session = Session(AccountId, "baidu-user") },
            adapter,
            capacities).RefreshAsync(AccountId);

        Assert.Equal(AccountCapacityRefreshDisposition.AccountChanged, result.Disposition);
        Assert.Null(result.Capacity);
        Assert.Null(result.AvatarUri);
        Assert.Equal(0, capacities.UpdateCalls);
    }

    [Fact]
    public async Task MissingOrMismatchedSessionNeverReadsOrWritesCapacity()
    {
        foreach (var session in new AccountSession?[]
        {
            null,
            Session(Guid.NewGuid(), "baidu-user"),
        })
        {
            var sessions = new FakeSessionStore { Session = session };
            var adapter = new FakeAdapter();
            var capacities = new FakeCapacityStore();

            var result = await Service(sessions, adapter, capacities).RefreshAsync(AccountId);

            Assert.Equal(AccountCapacityRefreshDisposition.SessionUnavailable, result.Disposition);
            Assert.Null(result.Capacity);
            Assert.Equal(0, adapter.CapacityCalls);
            Assert.Equal(0, capacities.UpdateCalls);
        }
    }

    [Theory]
    [InlineData(BaiduErrorKind.ExpiredLogin, AccountCapacityRefreshDisposition.LoginRequired)]
    [InlineData(BaiduErrorKind.VerificationRequired, AccountCapacityRefreshDisposition.VerificationRequired)]
    [InlineData(BaiduErrorKind.TransientService, AccountCapacityRefreshDisposition.TemporarilyUnavailable)]
    [InlineData(BaiduErrorKind.UnknownResponse, AccountCapacityRefreshDisposition.Unavailable)]
    [InlineData(BaiduErrorKind.NotValidated, AccountCapacityRefreshDisposition.Unavailable)]
    public async Task AdapterFailuresAreSafeAndNeverPersist(
        BaiduErrorKind error,
        AccountCapacityRefreshDisposition expected)
    {
        var capacities = new FakeCapacityStore();
        var adapter = new FakeAdapter
        {
            CapacityResult = BaiduAdapterResult<AccountCapacity>.Failure(
                error,
                "secret-cookie C:\\private\\profile"),
        };

        var result = await Service(
            new FakeSessionStore { Session = Session(AccountId, "baidu-user") },
            adapter,
            capacities).RefreshAsync(AccountId);

        Assert.Equal(expected, result.Disposition);
        Assert.Null(result.Capacity);
        Assert.Equal(0, capacities.UpdateCalls);
        Assert.DoesNotContain("secret", result.SafeMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private", result.SafeMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GuardedStoreRejectionDoesNotPublishCapacity()
    {
        var capacities = new FakeCapacityStore { UpdateResult = false };
        var adapter = new FakeAdapter
        {
            CapacityResult = BaiduAdapterResult<AccountCapacity>.Success(new(100, 25)),
        };

        var result = await Service(
            new FakeSessionStore { Session = Session(AccountId, "baidu-user") },
            adapter,
            capacities).RefreshAsync(AccountId);

        Assert.Equal(AccountCapacityRefreshDisposition.AccountChanged, result.Disposition);
        Assert.Null(result.Capacity);
        Assert.Equal(1, capacities.UpdateCalls);
    }

    [Fact]
    public async Task UnexpectedFailureIsSanitizedButCallerCancellationPropagates()
    {
        var throwing = new FakeAdapter { Throw = true };
        var sessions = new FakeSessionStore { Session = Session(AccountId, "baidu-user") };
        var unexpected = await Service(sessions, throwing, new FakeCapacityStore()).RefreshAsync(AccountId);
        Assert.Equal(AccountCapacityRefreshDisposition.TemporarilyUnavailable, unexpected.Disposition);
        Assert.DoesNotContain("secret", unexpected.SafeMessage, StringComparison.OrdinalIgnoreCase);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service(sessions, new FakeAdapter(), new FakeCapacityStore())
                .RefreshAsync(AccountId, cancellation.Token));
    }

    private static AccountCapacityRefreshService Service(
        IBaiduSessionStore sessions,
        IBaiduNetdiskAdapter adapter,
        IAccountCapacityStore capacities) =>
        new(sessions, adapter, capacities, new FixedTimeProvider(Now));

    private static AccountSession Session(Guid accountId, string baiduUserId) => new(
        accountId,
        baiduUserId,
        [new SessionCookie("BDUSS", "secret", ".baidu.com", "/", null, true, true)],
        Now);

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class FakeSessionStore : IBaiduSessionStore
    {
        public AccountSession? Session { get; init; }
        public Task SaveAsync(AccountSession session, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<AccountSession?> LoadAsync(string accountId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Session);
        }
        public Task RemoveAsync(string accountId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeCapacityStore : IAccountCapacityStore
    {
        public bool UpdateResult { get; init; } = true;
        public int UpdateCalls { get; private set; }
        public Guid AccountId { get; private set; }
        public string? BaiduUserId { get; private set; }
        public DateTimeOffset ValidatedAt { get; private set; }

        public Task<bool> TryUpdateAsync(Guid accountId, string expectedBaiduUserId,
            AccountCapacity capacity, DateTimeOffset validatedAt,
            CancellationToken cancellationToken = default)
        {
            UpdateCalls++;
            AccountId = accountId;
            BaiduUserId = expectedBaiduUserId;
            ValidatedAt = validatedAt;
            return Task.FromResult(UpdateResult);
        }
    }

    private sealed class FakeAdapter : IBaiduNetdiskAdapter
    {
        public BaiduAdapterResult<AccountCapacity> CapacityResult { get; init; } =
            BaiduAdapterResult<AccountCapacity>.Success(new(100, 25));
        public bool Throw { get; init; }
        public int CapacityCalls { get; private set; }
        public int IdentityCalls { get; private set; }
        public BaiduAdapterResult<BaiduAccountIdentity> IdentityResult { get; init; } =
            BaiduAdapterResult<BaiduAccountIdentity>.Success(new("baidu-user", "测试账号"));

        public Task<BaiduAdapterResult<AccountCapacity>> GetCapacityAsync(
            AccountSession session,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CapacityCalls++;
            if (Throw) throw new InvalidOperationException("secret-cookie C:\\private\\profile");
            return Task.FromResult(CapacityResult);
        }

        public Task<BaiduAdapterResult<BaiduAccountIdentity>> ValidateSessionAsync(AccountSession session, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IdentityCalls++;
            return Task.FromResult(IdentityResult);
        }
        public Task<BaiduAdapterResult<ImmutableArray<RemoteDirectory>>> ListDirectoriesAsync(AccountSession session, RemotePath path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<RemoteDirectory>> EnsureDirectoryAsync(AccountSession session, RemotePath path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<ShareMetadata>> InspectShareAsync(AccountSession session, ShareReference share, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<TransferReceipt>> SaveWholeShareAsync(AccountSession session, ShareReference share, RemoteDirectory destination, DuplicatePolicy duplicatePolicy, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<TransferConfirmation>> ConfirmTransferAsync(AccountSession session, TransferConfirmationQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
