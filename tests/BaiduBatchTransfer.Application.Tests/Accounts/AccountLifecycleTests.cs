using BaiduBatchTransfer.Application.Accounts;
using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Core.Domain;
using BaiduBatchTransfer.Infrastructure.Security;

namespace BaiduBatchTransfer.Application.Tests.Accounts;

public sealed class AccountLifecycleTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"bbt-account-tests-{Guid.NewGuid():N}");
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid ProfileToken = Guid.Parse("f545802a-d747-431c-b953-b76e5fe559ca");

    [Fact]
    public async Task AddRejectsDuplicateIdentityIncludingTombstoneHistory()
    {
        var repository = new FakeAccountRepository();
        repository.Seed(Tombstone(AccountRecord.Create(Guid.NewGuid(), "stable-user", "Former Name")));
        var events = new List<string>();
        var profiles = new FakeProfileStore(events);
        var service = CreateAddService(repository, new FakeProtector(), profiles);

        var result = await service.AddAsync(CreateAddRequest(), CancellationToken.None);

        Assert.Equal(AddAccountStatus.DuplicateIdentity, result.Status);
        Assert.Equal(ProfileCleanupStatus.Completed, result.ProfileCleanupStatus);
        Assert.Equal(new[] { "close", "delete" }, events);
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
        var capturedCookie = Assert.Single(protector.LastSession.SessionCookies);
        Assert.Equal(".baidu.com", capturedCookie.Domain);
        Assert.Equal("/", capturedCookie.Path);
        Assert.Equal(Now.AddHours(2), capturedCookie.ExpiresUtc);
        Assert.True(capturedCookie.IsSecure);
        Assert.True(capturedCookie.IsHttpOnly);
    }

    [Fact]
    public async Task AddCommitFailureRollsBackAccount()
    {
        var repository = new FakeAccountRepository();
        var factory = new FakeUnitOfWorkFactory(repository) { FailNextCommit = true };
        var profiles = new FakeProfileStore([]);
        var service = new AddAccountService(
            new FakeAdapter("stable-user"), factory, new FakeProtector(), profiles, new FileSystemAccountProfilePathPolicy(root));

        var error = await Assert.ThrowsAsync<AddAccountOperationException>(
            () => service.AddAsync(CreateAddRequest(), CancellationToken.None));

        Assert.IsType<InvalidOperationException>(error.InnerException);
        Assert.Equal(ProfileCleanupStatus.Completed, error.ProfileCleanupStatus);
        Assert.Equal(1, profiles.DeleteCount);
        Assert.Empty(repository.Records);
        Assert.Equal(3, factory.RollbackCount);
    }

    [Fact]
    public async Task AddRejectsLocalAccountIdCollisionWithoutOverwritingHistory()
    {
        var repository = new FakeAccountRepository();
        var existing = AccountRecord.Create(CreateAddRequest().AccountId, "another-stable-user", "Existing");
        repository.Seed(existing);
        var profiles = new FakeProfileStore([]);
        var service = CreateAddService(repository, new FakeProtector(), profiles);

        var result = await service.AddAsync(CreateAddRequest(), CancellationToken.None);

        Assert.Equal(AddAccountStatus.AccountIdCollision, result.Status);
        Assert.Equal(ProfileCleanupStatus.SkippedExistingAccountProtection, result.ProfileCleanupStatus);
        Assert.Equal(0, profiles.DeleteCount);
        Assert.Equal(existing, Assert.Single(repository.Records));
    }

    [Fact]
    public async Task FailedAddDoesNotDeleteProfileWhoseOwnershipChangedAfterDatabaseCheck()
    {
        var repository = new FakeAccountRepository();
        repository.Seed(Tombstone(AccountRecord.Create(Guid.NewGuid(), "stable-user", "Former")));
        var request = CreateAddRequest();
        var replacement = AccountRecord.Create(request.AccountId, "replacement-user", "Replacement");
        FakeProfileStore? profiles = null;
        profiles = new FakeProfileStore([])
        {
            BeforeOwnedDelete = () =>
            {
                repository.Seed(replacement);
            }
        };
        var service = CreateAddService(repository, new FakeProtector(), profiles);

        var result = await service.AddAsync(request, CancellationToken.None);

        Assert.Equal(AddAccountStatus.DuplicateIdentity, result.Status);
        Assert.Equal(ProfileCleanupStatus.SkippedExistingAccountProtection, result.ProfileCleanupStatus);
        Assert.Equal(0, profiles.DeleteCount);
        Assert.Equal(replacement, await repository.GetByIdAsync(request.AccountId));
    }

    [Fact]
    public async Task AddRejectsOversizedAdapterIdentityMetadata()
    {
        var repository = new FakeAccountRepository();
        var profiles = new FakeProfileStore([]);
        var service = new AddAccountService(
            new FakeAdapter("stable-user", new string('x', 257)),
            new FakeUnitOfWorkFactory(repository),
            new FakeProtector(),
            profiles,
            new FileSystemAccountProfilePathPolicy(root));

        var result = await service.AddAsync(CreateAddRequest(), CancellationToken.None);

        Assert.Equal(AddAccountStatus.InvalidIdentity, result.Status);
        Assert.Equal(ProfileCleanupStatus.Completed, result.ProfileCleanupStatus);
        Assert.Equal(1, profiles.DeleteCount);
        Assert.Empty(repository.Records);
    }

    [Fact]
    public async Task AddCancellationPreservesCancellationAndReportsOwnedProfileCleanup()
    {
        var repository = new FakeAccountRepository();
        var profiles = new FakeProfileStore([]);
        var service = new AddAccountService(
            new FakeAdapter("stable-user") { ValidationFailure = new OperationCanceledException("cancelled") },
            new FakeUnitOfWorkFactory(repository),
            new FakeProtector(),
            profiles,
            new FileSystemAccountProfilePathPolicy(root));

        var error = await Assert.ThrowsAsync<AddAccountCanceledException>(
            () => service.AddAsync(CreateAddRequest(), CancellationToken.None));

        Assert.Equal(ProfileCleanupStatus.Completed, error.ProfileCleanupStatus);
        Assert.Equal(1, profiles.DeleteCount);
        Assert.Empty(repository.Records);
    }

    [Fact]
    public async Task AddValidationFailureCleansOwnedProfile()
    {
        var repository = new FakeAccountRepository();
        var profiles = new FakeProfileStore([]);
        var service = new AddAccountService(
            new FailingValidationAdapter(),
            new FakeUnitOfWorkFactory(repository),
            new FakeProtector(),
            profiles,
            new FileSystemAccountProfilePathPolicy(root));

        var result = await service.AddAsync(CreateAddRequest(), CancellationToken.None);

        Assert.Equal(AddAccountStatus.SessionRejected, result.Status);
        Assert.Equal(ProfileCleanupStatus.Completed, result.ProfileCleanupStatus);
        Assert.Equal(1, profiles.DeleteCount);
    }

    [Fact]
    public async Task CleanupFailureDoesNotReplaceDuplicateBusinessResult()
    {
        var repository = new FakeAccountRepository();
        repository.Seed(Tombstone(AccountRecord.Create(Guid.NewGuid(), "stable-user", "Former")));
        var profiles = new FakeProfileStore([]) { FailDelete = true };
        var service = CreateAddService(repository, new FakeProtector(), profiles);

        var result = await service.AddAsync(CreateAddRequest(), CancellationToken.None);

        Assert.Equal(AddAccountStatus.DuplicateIdentity, result.Status);
        Assert.Equal(ProfileCleanupStatus.Pending, result.ProfileCleanupStatus);
    }

    [Fact]
    public async Task CloseFailureStillAttemptsSafeDeleteAndReportsCleanupPending()
    {
        var repository = new FakeAccountRepository();
        repository.Seed(Tombstone(AccountRecord.Create(Guid.NewGuid(), "stable-user", "Former")));
        var events = new List<string>();
        var profiles = new FakeProfileStore(events) { CloseFailure = new IOException("close failed") };
        var service = CreateAddService(repository, new FakeProtector(), profiles);

        var result = await service.AddAsync(CreateAddRequest(), CancellationToken.None);

        Assert.Equal(AddAccountStatus.DuplicateIdentity, result.Status);
        Assert.Equal(ProfileCleanupStatus.Pending, result.ProfileCleanupStatus);
        Assert.Equal(new[] { "close", "delete" }, events);
    }

    [Fact]
    public async Task SessionProtectionFailureCleansOwnedProfile()
    {
        var repository = new FakeAccountRepository();
        var profiles = new FakeProfileStore([]);
        var service = CreateAddService(repository, new FakeProtector { FailProtection = true }, profiles);

        var result = await service.AddAsync(CreateAddRequest(), CancellationToken.None);

        Assert.Equal(AddAccountStatus.SessionRejected, result.Status);
        Assert.Equal(ProfileCleanupStatus.Completed, result.ProfileCleanupStatus);
        Assert.Equal(1, profiles.DeleteCount);
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
    public async Task LegacyCloseIoFailureUsesCredentialFreeCleanupPendingMessage()
    {
        var repository = new FakeAccountRepository();
        var account = ActiveAccount();
        repository.Seed(account);
        var profiles = new FakeProfileStore([]) { CloseFailure = new IOException("profile-close-failed") };
        var service = CreateRemoveService(repository, profiles);

        var result = await service.RemoveAsync(account.Id, Now, CancellationToken.None);

        Assert.Equal(RemoveAccountStatus.CleanupPending, result.Status);
        Assert.Equal(
            "The account is disabled and credential-free; profile cleanup is pending.",
            result.SafeMessage);
        Assert.DoesNotContain("profile-close-failed", result.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyDeleteAccessFailureUsesCredentialFreeCleanupPendingMessage()
    {
        var repository = new FakeAccountRepository();
        var account = ActiveAccount();
        repository.Seed(account);
        var profiles = new FakeProfileStore([])
        {
            DeleteFailure = new UnauthorizedAccessException("profile-delete-denied")
        };
        var service = CreateRemoveService(repository, profiles);

        var result = await service.RemoveAsync(account.Id, Now, CancellationToken.None);

        Assert.Equal(RemoveAccountStatus.CleanupPending, result.Status);
        Assert.Equal(
            "The account is disabled and credential-free; profile cleanup is pending.",
            result.SafeMessage);
        Assert.DoesNotContain("profile-delete-denied", result.SafeMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(LegacyCancellationBoundary.Close)]
    [InlineData(LegacyCancellationBoundary.Delete)]
    [InlineData(LegacyCancellationBoundary.Tombstone)]
    public async Task LegacyRemoveCancellationPropagatesAfterCredentialsAreCleared(
        LegacyCancellationBoundary boundary)
    {
        var repository = new FakeAccountRepository();
        var account = ActiveAccount();
        repository.Seed(account);
        var cancellation = new OperationCanceledException($"{boundary}-cancelled");
        var events = new List<string>();
        var factory = new FakeUnitOfWorkFactory(repository)
        {
            CommitFailureAttempt = boundary == LegacyCancellationBoundary.Tombstone ? 2 : null,
            CommitFailure = boundary == LegacyCancellationBoundary.Tombstone ? cancellation : null
        };
        var profiles = new FakeProfileStore(events)
        {
            CloseFailure = boundary == LegacyCancellationBoundary.Close ? cancellation : null,
            DeleteFailure = boundary == LegacyCancellationBoundary.Delete ? cancellation : null
        };
        var service = new RemoveAccountService(
            factory,
            profiles,
            new FileSystemAccountProfilePathPolicy(root));

        var actual = await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.RemoveAsync(account.Id, Now, CancellationToken.None));

        Assert.Same(cancellation, actual);
        Assert.Equal(ExpectedLegacyCancellationEvents(boundary), events);
        var disabled = Assert.Single(repository.Records);
        Assert.False(disabled.IsEnabled);
        Assert.False(disabled.IsDeleted);
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
        var service = new RemoveAccountService(factory, profiles, new FileSystemAccountProfilePathPolicy(root));

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
        var events = new List<string>();
        var profiles = new FakeProfileStore(events);
        var service = CreateRemoveService(repository, profiles);

        var result = await service.RemoveAsync(account.Id, Now, CancellationToken.None);

        Assert.Equal(RemoveAccountStatus.CleanupPending, result.Status);
        Assert.Empty(events);
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
        var policy = new FileSystemAccountProfilePathPolicy(linkRoot);
        var service = new RemoveAccountService(new FakeUnitOfWorkFactory(repository), profiles, policy);
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

    [Fact]
    public async Task AddRejectsAncestorJunctionIntroducedAfterPathPolicyConstruction()
    {
        var anchor = Path.Combine(root, "add-anchor");
        var profileRoot = Path.Combine(anchor, "profiles");
        var outside = Path.Combine(root, "add-outside");
        Directory.CreateDirectory(root);
        var policy = new FileSystemAccountProfilePathPolicy(profileRoot);
        Directory.CreateDirectory(Path.Combine(outside, "profiles"));
        CreateDirectoryJunction(anchor, outside);
        var repository = new FakeAccountRepository();
        var events = new List<string>();
        var profiles = new FakeProfileStore(events);
        var service = new AddAccountService(
            new FakeAdapter("stable-user"),
            new FakeUnitOfWorkFactory(repository),
            new FakeProtector(),
            profiles,
            policy);

        var result = await service.AddAsync(CreateAddRequest(), CancellationToken.None);

        Assert.Equal(AddAccountStatus.InvalidProfilePath, result.Status);
        Assert.Equal(ProfileCleanupStatus.Pending, result.ProfileCleanupStatus);
        Assert.Equal(new[] { "close" }, events);
        Assert.Empty(repository.Records);
        Directory.Delete(anchor, recursive: false);
    }

    [Fact]
    public async Task AncestorReplacementDuringCloseLeavesCleanupPendingAndDoesNotTombstone()
    {
        var anchor = Path.Combine(root, "delete-anchor");
        var profileRoot = Path.Combine(anchor, "profiles");
        var account = ActiveAccount() with
        {
            WebView2ProfileDirectory = Path.Combine(profileRoot, CreateAddRequest().AccountId.ToString("D"))
        };
        var outside = Path.Combine(root, "delete-outside");
        var outsideProfile = Path.Combine(outside, "profiles", account.Id.ToString("D"));
        Directory.CreateDirectory(account.WebView2ProfileDirectory!);
        Directory.CreateDirectory(outsideProfile);
        var policy = new FileSystemAccountProfilePathPolicy(profileRoot);
        var closer = new CallbackProfileCloser(() =>
        {
            Directory.Delete(anchor, recursive: true);
            CreateDirectoryJunction(anchor, outside);
        });
        var profiles = new FileSystemWebViewProfileStore(policy, closer);
        var repository = new FakeAccountRepository();
        repository.Seed(account);
        var service = new RemoveAccountService(new FakeUnitOfWorkFactory(repository), profiles, policy);

        var result = await service.RemoveAsync(account.Id, Now, CancellationToken.None);

        Assert.Equal(RemoveAccountStatus.CleanupPending, result.Status);
        var disabled = Assert.Single(repository.Records);
        Assert.False(disabled.IsDeleted);
        Assert.False(disabled.IsEnabled);
        Assert.Null(disabled.EncryptedSession);
        Assert.True(Directory.Exists(outsideProfile));
        Directory.Delete(anchor, recursive: false);
    }

    private static void CreateDirectoryJunction(string link, string target)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            ArgumentList = { "/c", "mklink", "/J", link, target },
            CreateNoWindow = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Could not start junction creation.");
        process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException("Could not create the test junction.");
    }

    private AddAccountService CreateAddService(
        FakeAccountRepository repository,
        FakeProtector protector,
        FakeProfileStore? profiles = null) =>
        new(
            new FakeAdapter("stable-user"),
            new FakeUnitOfWorkFactory(repository),
            protector,
            profiles ?? new FakeProfileStore([]),
            new FileSystemAccountProfilePathPolicy(root));

    private RemoveAccountService CreateRemoveService(FakeAccountRepository repository, FakeProfileStore profiles) =>
        new(new FakeUnitOfWorkFactory(repository), profiles, new FileSystemAccountProfilePathPolicy(root));

    private static string[] ExpectedLegacyCancellationEvents(LegacyCancellationBoundary boundary) => boundary switch
    {
        LegacyCancellationBoundary.Close => ["close"],
        LegacyCancellationBoundary.Delete or LegacyCancellationBoundary.Tombstone => ["close", "delete"],
        _ => throw new ArgumentOutOfRangeException(nameof(boundary))
    };

    private static AddAccountRequest CreateAddRequest()
    {
        var accountId = Guid.Parse("51f1a6f9-c88b-4dd8-8376-36163ca42bc5");
        var session = new AccountSession(
            "login-candidate",
            [new SessionCookie("BDUSS", "cookie-value", ".baidu.com", "/", Now.AddHours(2), true, true)],
            Now);
        return new(accountId, session, Now, AccountProfileOwnership.NewLoginProfile(ProfileToken));
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
        public Exception? ValidationFailure { get; set; }
        public Task<BaiduAdapterResult<BaiduAccountIdentity>> ValidateSessionAsync(AccountSession session, CancellationToken cancellationToken) =>
            ValidationFailure is null
                ? Task.FromResult(BaiduAdapterResult<BaiduAccountIdentity>.Success(new BaiduAccountIdentity(stableUserId, displayName)))
                : Task.FromException<BaiduAdapterResult<BaiduAccountIdentity>>(ValidationFailure);
        public Task<BaiduAdapterResult<AccountCapacity>> GetCapacityAsync(AccountSession session, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<System.Collections.Immutable.ImmutableArray<RemoteDirectory>>> ListDirectoriesAsync(AccountSession session, RemotePath path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<RemoteDirectory>> EnsureDirectoryAsync(AccountSession session, RemotePath path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<ShareMetadata>> InspectShareAsync(AccountSession session, ShareReference share, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<TransferReceipt>> SaveWholeShareAsync(AccountSession session, ShareReference share, RemoteDirectory destination, DuplicatePolicy duplicatePolicy, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<TransferConfirmation>> ConfirmTransferAsync(AccountSession session, TransferConfirmationQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeProtector : ISessionProtector
    {
        public bool FailProtection { get; set; }
        public AccountSession? LastSession { get; private set; }
        public SessionProtectionResult Protect(AccountSession session)
        {
            LastSession = session;
            return FailProtection
                ? SessionProtectionResult.Failed(SessionProtectionFailure.InvalidSession, "Session unavailable.")
                : SessionProtectionResult.Protected([9, 8, 7]);
        }
        public SessionProtectionResult Unprotect(ReadOnlySpan<byte> protectedBytes, Guid expectedLocalAccountId, string expectedBaiduUserId) => throw new NotSupportedException();
    }

    private sealed class FailingValidationAdapter : IBaiduNetdiskAdapter
    {
        public Task<BaiduAdapterResult<BaiduAccountIdentity>> ValidateSessionAsync(AccountSession session, CancellationToken cancellationToken) =>
            Task.FromResult(BaiduAdapterResult<BaiduAccountIdentity>.Failure(BaiduErrorKind.ExpiredLogin, null));
        public Task<BaiduAdapterResult<AccountCapacity>> GetCapacityAsync(AccountSession session, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<System.Collections.Immutable.ImmutableArray<RemoteDirectory>>> ListDirectoriesAsync(AccountSession session, RemotePath path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<RemoteDirectory>> EnsureDirectoryAsync(AccountSession session, RemotePath path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<ShareMetadata>> InspectShareAsync(AccountSession session, ShareReference share, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<TransferReceipt>> SaveWholeShareAsync(AccountSession session, ShareReference share, RemoteDirectory destination, DuplicatePolicy duplicatePolicy, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BaiduAdapterResult<TransferConfirmation>> ConfirmTransferAsync(AccountSession session, TransferConfirmationQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeProfileStore(List<string> events) : IWebViewProfileStore
    {
        public bool FailDelete { get; set; }
        public Exception? CloseFailure { get; set; }
        public Exception? DeleteFailure { get; set; }
        public Guid? CurrentOwnerToken { get; set; } = ProfileToken;
        public Action? BeforeOwnedDelete { get; set; }
        public int DeleteCount { get; private set; }
        public Task CloseAsync(Guid accountId, CancellationToken cancellationToken)
        {
            events.Add("close");
            return CloseFailure is null ? Task.CompletedTask : Task.FromException(CloseFailure);
        }
        public Task DeleteAsync(Guid accountId, string exactProfilePath, CancellationToken cancellationToken)
        {
            events.Add("delete"); DeleteCount++;
            if (DeleteFailure is not null) return Task.FromException(DeleteFailure);
            return FailDelete ? Task.FromException(new IOException("locked")) : Task.CompletedTask;
        }

        public Task RegisterOwnershipAsync(Guid accountId, string exactProfilePath, Guid ownershipToken, CancellationToken cancellationToken)
        {
            CurrentOwnerToken = ownershipToken;
            return Task.CompletedTask;
        }

        public async Task<bool> CommitOwnedAsync(
            Guid accountId,
            string exactProfilePath,
            Guid ownershipToken,
            Func<CancellationToken, Task> commitAction,
            CancellationToken cancellationToken)
        {
            if (CurrentOwnerToken != ownershipToken) return false;
            await commitAction(cancellationToken);
            return true;
        }

        public async Task<OwnedProfileDeletionStatus> DeleteOwnedAsync(
            Guid accountId,
            string exactProfilePath,
            Guid ownershipToken,
            Func<CancellationToken, Task<bool>> confirmNoActiveAccount,
            CancellationToken cancellationToken)
        {
            BeforeOwnedDelete?.Invoke();
            if (CurrentOwnerToken != ownershipToken)
                return OwnedProfileDeletionStatus.OwnershipMismatch;
            if (!await confirmNoActiveAccount(cancellationToken))
                return OwnedProfileDeletionStatus.OwnershipMismatch;
            events.Add("delete");
            DeleteCount++;
            if (FailDelete) throw new IOException("locked");
            return OwnedProfileDeletionStatus.Completed;
        }
    }

    private sealed class CallbackProfileCloser(Action callback) : IWebViewProfileCloser
    {
        public Task CloseAsync(Guid accountId, CancellationToken cancellationToken)
        {
            callback();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeUnitOfWorkFactory(FakeAccountRepository repository) : IUnitOfWorkFactory
    {
        public bool FailNextCommit { get; set; }
        public int? FailCommitAttempt { get; set; }
        public int? CommitFailureAttempt { get; set; }
        public Exception? CommitFailure { get; set; }
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
                if (owner.CommitFailureAttempt == owner.CommitAttempts && owner.CommitFailure is not null)
                    throw owner.CommitFailure;
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

    public enum LegacyCancellationBoundary
    {
        Close,
        Delete,
        Tombstone
    }
}
