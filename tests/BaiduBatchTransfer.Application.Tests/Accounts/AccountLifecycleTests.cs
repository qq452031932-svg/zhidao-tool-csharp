using BaiduBatchTransfer.Application.Accounts;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.Application.Tests.Accounts;

public sealed class AccountLifecycleTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"bbt-account-tests-{Guid.NewGuid():N}");
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AddRejectsDuplicateIdentityIncludingTombstoneHistory()
    {
        var repository = new FakeAccountRepository();
        repository.Seed(Tombstone(AccountRecord.Create(Guid.NewGuid(), "stable-user", "Former Name")));
        var service = CreateAddService(repository, new FakeProtector());

        var result = await service.AddAsync(CreateAddRequest(), CancellationToken.None);

        Assert.Equal(AddAccountStatus.DuplicateIdentity, result.Status);
        Assert.Single(repository.Records);
    }

    [Fact]
    public async Task AddPersistsOnlyEncryptedSessionAndCanonicalGuidProfileTransactionally()
    {
        var repository = new FakeAccountRepository();
        var protector = new FakeProtector();
        var request = CreateAddRequest();
        var service = CreateAddService(repository, protector);

        var result = await service.AddAsync(request, CancellationToken.None);

        Assert.Equal(AddAccountStatus.Added, result.Status);
        var account = Assert.Single(repository.Records);
        Assert.Equal(request.AccountId, account.Id);
        Assert.Equal("stable-user", account.BaiduUserId);
        Assert.Equal(new byte[] { 9, 8, 7 }, account.EncryptedSession);
        Assert.Equal(Path.Combine(Path.GetFullPath(root), request.AccountId.ToString("D")), account.WebView2ProfileDirectory);
        Assert.DoesNotContain("cookie-value", account.ToString(), StringComparison.Ordinal);
        Assert.True(protector.LastSession!.IsBoundTo(request.AccountId, "stable-user"));
    }

    [Fact]
    public async Task AddCommitFailureRollsBackAccount()
    {
        var repository = new FakeAccountRepository();
        var factory = new FakeUnitOfWorkFactory(repository) { FailNextCommit = true };
        var service = new AddAccountService(
            new FakeAdapter("stable-user"), factory, new FakeProtector(), root);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.AddAsync(CreateAddRequest(), CancellationToken.None));

        Assert.Empty(repository.Records);
        Assert.Equal(1, factory.RollbackCount);
    }

    [Fact]
    public async Task AddRejectsLocalAccountIdCollisionWithoutOverwritingHistory()
    {
        var repository = new FakeAccountRepository();
        var existing = AccountRecord.Create(CreateAddRequest().AccountId, "another-stable-user", "Existing");
        repository.Seed(existing);
        var service = CreateAddService(repository, new FakeProtector());

        var result = await service.AddAsync(CreateAddRequest(), CancellationToken.None);

        Assert.Equal(AddAccountStatus.AccountIdCollision, result.Status);
        Assert.Equal(existing, Assert.Single(repository.Records));
    }

    [Fact]
    public async Task AddRejectsOversizedAdapterIdentityMetadata()
    {
        var repository = new FakeAccountRepository();
        var service = new AddAccountService(
            new FakeAdapter("stable-user", new string('x', 257)),
            new FakeUnitOfWorkFactory(repository),
            new FakeProtector(),
            root);

        var result = await service.AddAsync(CreateAddRequest(), CancellationToken.None);

        Assert.Equal(AddAccountStatus.InvalidIdentity, result.Status);
        Assert.Empty(repository.Records);
    }

    [Fact]
    public async Task RemoveClearsCredentialsBeforeProfileCleanupThenTombstonesRedactedHistory()
    {
        var repository = new FakeAccountRepository();
        var account = ActiveAccount();
        repository.Seed(account);
        var events = new List<string>();
        repository.OnUpsert = record => events.Add(record.IsDeleted ? "tombstone" : "disable-clear");
        var profiles = new FakeProfileStore(events);
        var service = CreateRemoveService(repository, profiles);

        var result = await service.RemoveAsync(account.Id, Now, CancellationToken.None);

        Assert.Equal(RemoveAccountStatus.Removed, result.Status);
        Assert.Equal(new[] { "disable-clear", "close", "delete", "tombstone" }, events);
        var removed = Assert.Single(repository.Records);
        Assert.Equal(account.Id, removed.Id);
        Assert.Equal("stable-user", removed.BaiduUserId);
        Assert.Equal("Removed account", removed.DisplayName);
        Assert.Null(removed.EncryptedSession);
        Assert.Null(removed.WebView2ProfileDirectory);
        Assert.True(removed.IsDeleted);
        Assert.False(removed.IsEnabled);
    }

    [Fact]
    public async Task DeleteFailureLeavesDisabledCredentialFreeAndRetryResumesCleanup()
    {
        var repository = new FakeAccountRepository();
        var account = ActiveAccount();
        repository.Seed(account);
        var profiles = new FakeProfileStore([]) { FailDelete = true };
        var service = CreateRemoveService(repository, profiles);

        var first = await service.RemoveAsync(account.Id, Now, CancellationToken.None);

        Assert.Equal(RemoveAccountStatus.CleanupPending, first.Status);
        var disabled = Assert.Single(repository.Records);
        Assert.Equal("Disabled", disabled.Status);
        Assert.False(disabled.IsEnabled);
        Assert.Null(disabled.EncryptedSession);
        Assert.False(disabled.IsDeleted);
        profiles.FailDelete = false;

        var retry = await service.RemoveAsync(account.Id, Now.AddMinutes(1), CancellationToken.None);
        var idempotent = await service.RemoveAsync(account.Id, Now.AddMinutes(2), CancellationToken.None);

        Assert.Equal(RemoveAccountStatus.Removed, retry.Status);
        Assert.Equal(RemoveAccountStatus.AlreadyRemoved, idempotent.Status);
        Assert.Equal(2, profiles.DeleteCount);
    }

    [Fact]
    public async Task UnexpectedProfileCloseFailureStillReportsCleanupPendingWithCredentialsCleared()
    {
        var repository = new FakeAccountRepository();
        var account = ActiveAccount();
        repository.Seed(account);
        var profiles = new FakeProfileStore([]) { CloseFailure = new Exception("profile host stopped") };
        var service = CreateRemoveService(repository, profiles);

        var result = await service.RemoveAsync(account.Id, Now, CancellationToken.None);

        Assert.Equal(RemoveAccountStatus.CleanupPending, result.Status);
        var disabled = Assert.Single(repository.Records);
        Assert.False(disabled.IsEnabled);
        Assert.Null(disabled.EncryptedSession);
    }

    [Fact]
    public async Task TombstoneCommitFailureLeavesRetryableDisabledState()
    {
        var repository = new FakeAccountRepository();
        var account = ActiveAccount();
        repository.Seed(account);
        var factory = new FakeUnitOfWorkFactory(repository) { FailCommitAttempt = 2 };
        var profiles = new FakeProfileStore([]);
        var service = new RemoveAccountService(factory, profiles, root);

        var first = await service.RemoveAsync(account.Id, Now, CancellationToken.None);
        var retry = await service.RemoveAsync(account.Id, Now.AddMinutes(1), CancellationToken.None);

        Assert.Equal(RemoveAccountStatus.CleanupPending, first.Status);
        Assert.Equal(RemoveAccountStatus.Removed, retry.Status);
        Assert.True(Assert.Single(repository.Records).IsDeleted);
        Assert.Equal(2, profiles.DeleteCount);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("")]
    public async Task RemoveNeverDeletesRootParentOrAnotherAccountProfile(string maliciousLeaf)
    {
        var repository = new FakeAccountRepository();
        var account = ActiveAccount();
        var badPath = maliciousLeaf.Length == 0
            ? root
            : Path.Combine(root, maliciousLeaf, Guid.NewGuid().ToString("D"));
        repository.Seed(account with { WebView2ProfileDirectory = badPath });
        var profiles = new FakeProfileStore([]);
        var service = CreateRemoveService(repository, profiles);

        var result = await service.RemoveAsync(account.Id, Now, CancellationToken.None);

        Assert.Equal(RemoveAccountStatus.CleanupPending, result.Status);
        Assert.Equal(0, profiles.DeleteCount);
        Assert.Null(Assert.Single(repository.Records).EncryptedSession);
    }

    [Fact]
    public async Task RemoveRejectsAFileMasqueradingAsTheProfileFolder()
    {
        var repository = new FakeAccountRepository();
        var account = ActiveAccount();
        Directory.CreateDirectory(root);
        File.WriteAllText(account.WebView2ProfileDirectory!, "not a profile directory");
        repository.Seed(account);
        var profiles = new FakeProfileStore([]);
        var service = CreateRemoveService(repository, profiles);

        var result = await service.RemoveAsync(account.Id, Now, CancellationToken.None);

        Assert.Equal(RemoveAccountStatus.CleanupPending, result.Status);
        Assert.Equal(0, profiles.DeleteCount);
    }

    [Fact]
    public async Task RemoveRejectsProfileRootReplacedByDirectoryLinkAfterServiceConstruction()
    {
        var linkRoot = Path.Combine(root, "link-root");
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        var account = ActiveAccount() with
        {
            WebView2ProfileDirectory = Path.Combine(linkRoot, Guid.Parse("51f1a6f9-c88b-4dd8-8376-36163ca42bc5").ToString("D"))
        };
        var repository = new FakeAccountRepository();
        repository.Seed(account);
        var profiles = new FakeProfileStore([]);
        var service = new RemoveAccountService(new FakeUnitOfWorkFactory(repository), profiles, linkRoot);
        try
        {
            Directory.CreateDirectory(Path.Combine(outside, account.Id.ToString("D")));
            try { Directory.CreateSymbolicLink(linkRoot, outside); }
            catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
            {
                if (!OperatingSystem.IsWindows()) return;
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c mklink /J \"{linkRoot}\" \"{outside}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                if (process is null) return;
                await process.WaitForExitAsync();
                if (process.ExitCode != 0) return;
            }

            var result = await service.RemoveAsync(account.Id, Now, CancellationToken.None);

            Assert.Equal(RemoveAccountStatus.CleanupPending, result.Status);
            Assert.Equal(0, profiles.DeleteCount);
        }
        finally
        {
            if (Directory.Exists(linkRoot)) Directory.Delete(linkRoot, recursive: false);
        }
    }

    private AddAccountService CreateAddService(FakeAccountRepository repository, FakeProtector protector) =>
        new(new FakeAdapter("stable-user"), new FakeUnitOfWorkFactory(repository), protector, root);

    private RemoveAccountService CreateRemoveService(FakeAccountRepository repository, FakeProfileStore profiles) =>
        new(new FakeUnitOfWorkFactory(repository), profiles, root);

    private static AddAccountRequest CreateAddRequest()
    {
        var accountId = Guid.Parse("51f1a6f9-c88b-4dd8-8376-36163ca42bc5");
        var session = new AccountSession(
            "login-candidate",
            new Dictionary<string, string> { ["BDUSS"] = "cookie-value" },
            Now);
        return new(accountId, session, Now);
    }

    private AccountRecord ActiveAccount()
    {
        var id = Guid.Parse("51f1a6f9-c88b-4dd8-8376-36163ca42bc5");
        return AccountRecord.Create(id, "stable-user", "Visible Name") with
        {
            EncryptedSession = [1, 2, 3],
            WebView2ProfileDirectory = Path.Combine(Path.GetFullPath(root), id.ToString("D"))
        };
    }

    private static AccountRecord Tombstone(AccountRecord account) => account with
    {
        DisplayName = "Removed account", EncryptedSession = null, WebView2ProfileDirectory = null,
        Status = "Deleted", IsEnabled = false, IsDeleted = true, DeletedAt = Now
    };

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private sealed class FakeAdapter(string stableUserId, string displayName = "Visible Name") : IBaiduNetdiskAdapter
    {
        public Task<BaiduAdapterResult<BaiduAccountIdentity>> ValidateSessionAsync(AccountSession session, CancellationToken cancellationToken) =>
            Task.FromResult(BaiduAdapterResult<BaiduAccountIdentity>.Success(new BaiduAccountIdentity(stableUserId, displayName)));
        public Task<BaiduAdapterResult<AccountCapacity>> GetCapacityAsync(AccountSession session, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<System.Collections.Immutable.ImmutableArray<RemoteDirectory>>> ListDirectoriesAsync(AccountSession session, RemotePath path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<RemoteDirectory>> EnsureDirectoryAsync(AccountSession session, RemotePath path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<ShareMetadata>> InspectShareAsync(AccountSession session, ShareReference share, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<TransferReceipt>> SaveWholeShareAsync(AccountSession session, ShareReference share, RemoteDirectory destination, DuplicatePolicy duplicatePolicy, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<TransferConfirmation>> ConfirmTransferAsync(AccountSession session, TransferConfirmationQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeProtector : ISessionProtector
    {
        public AccountSession? LastSession { get; private set; }
        public SessionProtectionResult Protect(AccountSession session)
        {
            LastSession = session;
            return SessionProtectionResult.Protected([9, 8, 7]);
        }
        public SessionProtectionResult Unprotect(ReadOnlySpan<byte> protectedBytes, Guid expectedLocalAccountId, string expectedBaiduUserId) => throw new NotSupportedException();
    }

    private sealed class FakeProfileStore(List<string> events) : IWebViewProfileStore
    {
        public bool FailDelete { get; set; }
        public Exception? CloseFailure { get; set; }
        public int DeleteCount { get; private set; }
        public Task CloseAsync(Guid accountId, CancellationToken cancellationToken)
        {
            events.Add("close");
            return CloseFailure is null ? Task.CompletedTask : Task.FromException(CloseFailure);
        }
        public Task DeleteAsync(string exactProfilePath, CancellationToken cancellationToken)
        {
            events.Add("delete"); DeleteCount++;
            return FailDelete ? Task.FromException(new IOException("locked")) : Task.CompletedTask;
        }
    }

    private sealed class FakeUnitOfWorkFactory(FakeAccountRepository repository) : IUnitOfWorkFactory
    {
        public bool FailNextCommit { get; set; }
        public int? FailCommitAttempt { get; set; }
        public int CommitAttempts { get; private set; }
        public int RollbackCount { get; private set; }
        public Task<IUnitOfWork> BeginAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IUnitOfWork>(new FakeUnitOfWork(repository, this));

        private sealed class FakeUnitOfWork : IUnitOfWork
        {
            private readonly FakeAccountRepository live;
            private readonly FakeAccountRepository working;
            private readonly FakeUnitOfWorkFactory owner;
            public FakeUnitOfWork(FakeAccountRepository live, FakeUnitOfWorkFactory owner)
            {
                this.live = live; this.owner = owner; working = live.Clone(); Accounts = working;
            }
            public IImportRepository Imports => throw new NotSupportedException();
            public IJobRepository Jobs => throw new NotSupportedException();
            public IAccountRepository Accounts { get; }
            public Task CommitAsync(CancellationToken cancellationToken = default)
            {
                owner.CommitAttempts++;
                if (owner.FailNextCommit || owner.FailCommitAttempt == owner.CommitAttempts)
                {
                    owner.FailNextCommit = false;
                    throw new InvalidOperationException("commit failed");
                }
                live.ReplaceFrom(working); return Task.CompletedTask;
            }
            public Task RollbackAsync(CancellationToken cancellationToken = default) { owner.RollbackCount++; return Task.CompletedTask; }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FakeAccountRepository : IAccountRepository
    {
        private readonly Dictionary<Guid, AccountRecord> records = [];
        public Action<AccountRecord>? OnUpsert { get; set; }
        public IReadOnlyCollection<AccountRecord> Records => records.Values;
        public void Seed(AccountRecord record) => records[record.Id] = record;
        public FakeAccountRepository Clone() { var clone = new FakeAccountRepository { OnUpsert = OnUpsert }; foreach (var item in records.Values) clone.Seed(item); return clone; }
        public void ReplaceFrom(FakeAccountRepository other) { records.Clear(); foreach (var item in other.records.Values) records[item.Id] = item; OnUpsert?.Invoke(records.Values.Single()); }
        public Task UpsertAsync(AccountRecord account, CancellationToken cancellationToken = default) { records[account.Id] = account; return Task.CompletedTask; }
        public Task<AccountRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(records.GetValueOrDefault(id));
        public Task<AccountRecord?> GetByBaiduUserIdAsync(string baiduUserId, CancellationToken cancellationToken = default) => Task.FromResult(records.Values.SingleOrDefault(x => x.BaiduUserId == baiduUserId));
        public Task SetEncryptedSessionAsync(Guid id, byte[]? encryptedSession, CancellationToken cancellationToken = default)
        {
            if (records.TryGetValue(id, out var account)) records[id] = account with { EncryptedSession = encryptedSession is null ? null : (byte[])encryptedSession.Clone() };
            return Task.CompletedTask;
        }
        public Task<Page<AccountRecord>> GetPageAsync(PageQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpsertRuleAsync(AccountRuleRecord rule, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Page<AccountRuleRecord>> GetRulesPageAsync(Guid accountId, PageQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
